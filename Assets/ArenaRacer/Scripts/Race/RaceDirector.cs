using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace ArenaRacer
{
    public enum RacePhase : byte { Waiting, Countdown, Racing, Results }

    /// <summary>
    /// Race rules and room bookkeeping. Its network object has the MasterClientObject flag, so State Authority
    /// always sits with the current shared-mode master client. If the master leaves, Fusion hands this object to
    /// the next player, and the code below makes that player adopt the bots too.
    ///
    /// Only the master ever changes who drives a car, so it is also where race points are reset.
    /// </summary>
    public sealed class RaceDirector : NetworkBehaviour
    {
        public const int Laps = 3;
        public const int CarCount = 6;
        const float CountdownSeconds = 3.5f;
        const float FinishGraceSeconds = 30f;   // after the winner crosses the line, the rest have this long
        const float ResultsSeconds = 20f;       // results auto-restart after this, if not everyone voted sooner
        static readonly short[] PointsForPlace = { 10, 7, 5, 3, 2, 1 };

        public static RaceDirector Instance { get; private set; }

        [SerializeField] NetworkObject carPrefab;

        [Networked] public RacePhase Phase { get; set; }
        [Networked] public int RaceNumber { get; set; }
        [Networked] public int StartTick { get; set; }       // the tick the lights go out
        [Networked] public int DeadlineTick { get; set; }    // racing: end of the grace period; results: auto restart
        [Networked] public NetworkBool Frozen { get; set; }  // solo pause: the whole race stops
        [Networked] public int FrozenAtTick { get; set; }
        [Networked] public NetworkBool CarsSpawned { get; set; }
        [Networked] public int RestartVotes { get; set; }    // one bit per car slot
        [Networked, Capacity(CarCount)] public NetworkArray<byte> GridOrder => default;   // grid box -> slot
        [Networked, Capacity(CarCount)] public NetworkArray<byte> FinalOrder => default;  // finishing place -> slot
        [Networked, Capacity(CarCount)] public NetworkArray<short> Points => default;     // per slot

        readonly List<NetCar> _scratch = new();
        float _nextReclaim;

        /// <summary>Seconds since the lights went out, excluding time spent paused. Shared by every client.</summary>
        public float RaceTime
        {
            get
            {
                if (Phase == RacePhase.Waiting) return 0f;
                int now = Frozen ? FrozenAtTick : Runner.Tick;
                return Mathf.Max(0f, (now - StartTick) * Runner.DeltaTime);
            }
        }

        public float SecondsUntilStart => Mathf.Max(0f, ((Frozen ? FrozenAtTick : Runner.Tick) - StartTick) * -Runner.DeltaTime);

        public float SecondsUntilDeadline => DeadlineTick == 0 ? 0f : Mathf.Max(0f, (DeadlineTick - Runner.Tick) * Runner.DeltaTime);

        public int HumanCount
        {
            get
            {
                int count = 0;
                foreach (NetCar car in NetCar.All)
                    if (!car.IsBot) count++;
                return count;
            }
        }

        public override void Spawned() => Instance = this;

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (Instance == this) Instance = null;
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority) return;

            if (!CarsSpawned)
            {
                SpawnCars();
                return;
            }

            AdoptAbandonedCars();

            if (Frozen)
            {
                if (HumanCount != 1) Unfreeze();  // someone joined: a shared race can't stay paused
                return;
            }

            switch (Phase)
            {
                case RacePhase.Waiting:
                    if (HumanCount > 0) StartCountdown();
                    break;
                case RacePhase.Countdown:
                    if (Runner.Tick >= StartTick) Phase = RacePhase.Racing;
                    break;
                case RacePhase.Racing:
                    UpdateRacing();
                    break;
                case RacePhase.Results:
                    if (Runner.Tick >= DeadlineTick || EveryHumanVoted()) StartNextRace();
                    break;
            }
        }

        // ---- Requests from clients (any player may call these; they run on the master) ----

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void RPC_RequestCar(PlayerRef player)
        {
            if (NetCar.FindDrivenBy(player) != null) return;

            // Take over a bot: the back of the grid before the start, the last-placed bot mid-race.
            NetCar pick = null;
            foreach (NetCar car in NetCar.All)
            {
                if (!car.IsBot || !car.HasStateAuthority) continue;
                if (pick == null || TakeoverScore(car) < TakeoverScore(pick)) pick = car;
            }
            if (pick == null) return;  // six humans already

            pick.Driver = player;  // the player now calls RequestStateAuthority() on it (see NetCar.Render)
            pick.Autopilot = false;
            Points.Set(pick.Slot, 0);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void RPC_SetPaused(PlayerRef player, NetworkBool paused)
        {
            if (NetCar.FindDrivenBy(player) == null || HumanCount != 1) return;
            if (paused && !Frozen)
            {
                Frozen = true;
                FrozenAtTick = Runner.Tick;
            }
            else if (!paused && Frozen)
            {
                Unfreeze();
            }
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void RPC_VoteRestart(PlayerRef player)
        {
            NetCar car = NetCar.FindDrivenBy(player);
            if (car == null) return;
            if (Phase == RacePhase.Results) RestartVotes |= 1 << car.Slot;
            else if (HumanCount == 1) StartNextRace();  // solo "Restart race" from the pause menu
        }

        // ---- Queries used by every client ----

        public int GridPositionOf(int slot)
        {
            for (int i = 0; i < CarCount; i++)
                if (GridOrder.Get(i) == slot) return i;
            return slot;
        }

        public bool HasVoted(int slot) => (RestartVotes & (1 << slot)) != 0;

        /// <summary>Current running order: finished cars by time, then everyone else by distance covered.</summary>
        public void GetStandings(List<NetCar> into)
        {
            into.Clear();
            if (Phase == RacePhase.Results)
            {
                for (int place = 0; place < CarCount; place++)
                {
                    NetCar car = CarInSlot(FinalOrder.Get(place));
                    if (car != null) into.Add(car);
                }
                if (into.Count == NetCar.All.Count) return;
                into.Clear();
            }
            into.AddRange(NetCar.All);
            into.Sort(CompareRunningOrder);
        }

        /// <summary>
        /// Bot speed multiplier. Rubber-banding against the best human keeps the race close: bots well
        /// ahead ease off by up to 9%, bots well behind push by up to 9%.
        /// </summary>
        public float BotPace(NetCar bot)
        {
            float pace = BotDriver.Roster[bot.Slot].Pace;
            float bestHuman = float.MinValue;
            foreach (NetCar car in NetCar.All)
                if (!car.IsBot && !car.Autopilot) bestHuman = Mathf.Max(bestHuman, car.Progress);
            if (bestHuman == float.MinValue) return pace;
            float gap = bot.Progress - bestHuman;
            return pace * (1f - Mathf.Clamp(gap / 100f, -1f, 1f) * 0.09f);
        }

        public static NetCar CarInSlot(int slot)
        {
            foreach (NetCar car in NetCar.All)
                if (car.Slot == slot) return car;
            return null;
        }

        // ---- Master-only internals ----

        void SpawnCars()
        {
            for (int slot = 0; slot < CarCount; slot++)
            {
                int carSlot = slot;
                GridOrder.Set(slot, (byte)slot);
                ArenaCircuit.GridBox box = ArenaCircuit.Grid(slot);
                Runner.Spawn(carPrefab,
                    new Vector3(box.Position.x, 0f, box.Position.y),
                    Quaternion.Euler(0f, box.Heading * Mathf.Rad2Deg, 0f),
                    null,
                    (runner, obj) => obj.GetComponent<NetCar>().Slot = carSlot);
            }
            RaceNumber = 1;
            Phase = RacePhase.Waiting;
            CarsSpawned = true;
        }

        /// <summary>
        /// Cars whose driver left come back as bots. When a client leaves, Fusion clears State Authority on the
        /// objects it owned (they are not destroyed, see the car prefab flags); the master claims them back.
        /// This also covers master migration: the new master claims the previous master's bots.
        /// </summary>
        void AdoptAbandonedCars()
        {
            if (Runner.GameMode == GameMode.Single || Time.unscaledTime < _nextReclaim) return;
            _nextReclaim = Time.unscaledTime + 0.25f;

            foreach (NetCar car in NetCar.All)
            {
                bool driverGone = !car.IsBot && !IsInRoom(car.Driver);
                if (car.HasStateAuthority)
                {
                    if (!driverGone) continue;
                    car.Driver = PlayerRef.None;
                    car.Autopilot = false;
                    Points.Set(car.Slot, 0);
                }
                else
                {
                    PlayerRef owner = car.Object.StateAuthority;
                    bool ownerGone = owner == PlayerRef.None || !IsInRoom(owner);
                    bool botOwnedByOldMaster = car.IsBot && owner != Object.StateAuthority;
                    if (ownerGone || driverGone || botOwnedByOldMaster) car.Object.RequestStateAuthority();
                }
            }
        }

        bool IsInRoom(PlayerRef player)
        {
            foreach (PlayerRef p in Runner.ActivePlayers)
                if (p == player) return true;
            return false;
        }

        void StartCountdown()
        {
            StartTick = Runner.Tick + Ticks(CountdownSeconds);
            DeadlineTick = 0;
            Phase = RacePhase.Countdown;
        }

        void UpdateRacing()
        {
            if (HumanCount == 0)
            {
                EndRace();
                return;
            }

            bool everyHumanFinished = true;
            bool anyoneFinished = false;
            foreach (NetCar car in NetCar.All)
            {
                if (car.Finished) anyoneFinished = true;
                else if (!car.IsBot) everyHumanFinished = false;
            }

            if (anyoneFinished && DeadlineTick == 0) DeadlineTick = Runner.Tick + Ticks(FinishGraceSeconds);
            if (everyHumanFinished || (DeadlineTick != 0 && Runner.Tick >= DeadlineTick)) EndRace();
        }

        void EndRace()
        {
            _scratch.Clear();
            _scratch.AddRange(NetCar.All);
            _scratch.Sort(CompareRunningOrder);
            for (int place = 0; place < CarCount; place++)
            {
                if (place >= _scratch.Count) break;
                NetCar car = _scratch[place];
                FinalOrder.Set(place, (byte)car.Slot);
                Points.Set(car.Slot, (short)(Points.Get(car.Slot) + PointsForPlace[place]));
            }
            RestartVotes = 0;
            DeadlineTick = Runner.Tick + Ticks(ResultsSeconds);
            Phase = RacePhase.Results;
        }

        void StartNextRace()
        {
            // Bots line up in front, humans at the back: you race through the field.
            int box = 0;
            for (int pass = 0; pass < 2; pass++)
            foreach (NetCar car in NetCar.All)
            {
                if (car.IsBot == (pass == 0)) GridOrder.Set(box++, (byte)car.Slot);
            }

            Frozen = false;
            RestartVotes = 0;
            RaceNumber++;  // every car's owner sees this and lines its own car up (NetCar.LineUp)
            StartCountdown();
        }

        void Unfreeze()
        {
            int pausedFor = Runner.Tick - FrozenAtTick;
            StartTick += pausedFor;
            if (DeadlineTick != 0) DeadlineTick += pausedFor;
            Frozen = false;
        }

        bool EveryHumanVoted()
        {
            foreach (NetCar car in NetCar.All)
                if (!car.IsBot && !HasVoted(car.Slot)) return false;
            return HumanCount > 0;
        }

        float TakeoverScore(NetCar bot) =>
            Phase == RacePhase.Racing ? bot.Progress : -GridPositionOf(bot.Slot);

        int Ticks(float seconds) => Mathf.CeilToInt(seconds / Runner.DeltaTime);

        static int CompareRunningOrder(NetCar a, NetCar b)
        {
            if (a.Finished != b.Finished) return a.Finished ? -1 : 1;
            if (a.Finished) return a.FinishTime.CompareTo(b.FinishTime);
            return b.Progress.CompareTo(a.Progress);
        }
    }
}
