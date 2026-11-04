using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace ArenaRacer
{
    /// <summary>
    /// One of the six cars. Whoever holds State Authority simulates it each tick: the player driving it, or the
    /// shared-mode master client while a bot drives it. Everybody else only renders it, interpolating between
    /// the two snapshots either side of Fusion's remote render time.
    ///
    /// Taking over a car is just a State Authority transfer: the master writes <see cref="Driver"/>, the new
    /// driver calls RequestStateAuthority(), and the car carries on from exactly where it was.
    /// </summary>
    public sealed class NetCar : NetworkBehaviour
    {
        public static readonly List<NetCar> All = new();

        [Networked] public PlayerRef Driver { get; set; }       // PlayerRef.None = a bot is driving
        [Networked] public int Slot { get; set; }               // 0..5, fixed for the car's lifetime (colour, bot name)
        [Networked] public Vector2 Position { get; set; }
        [Networked] public Vector2 Velocity { get; set; }
        [Networked] public float Heading { get; set; }
        [Networked] public float Steer { get; set; }
        [Networked] public int DriftDir { get; set; }
        [Networked] public float DriftCharge { get; set; }
        [Networked] public float BoostTime { get; set; }
        [Networked] public float Progress { get; set; }         // metres since the start line, all laps included
        [Networked] public int LapsDone { get; set; }
        [Networked] public float LapStartTime { get; set; }     // race seconds
        [Networked] public float BestLap { get; set; }
        [Networked] public float FinishTime { get; set; }       // race seconds, 0 = still racing
        [Networked] public int RaceNumber { get; set; }         // the race this car last lined up for
        [Networked] public float GhostTime { get; set; }        // no car-to-car contact for a moment after a reset
        [Networked] public NetworkBool Autopilot { get; set; }  // driver paused with other humans in the room
        [Networked] public byte TeleportCount { get; set; }     // bumped on every reset so renderers don't interpolate across it
        [Networked] public byte BumpCount { get; set; }         // bumped on every hard hit, for sound and sparks everywhere

        // Interpolated values for this frame, written in Render(). Views, camera and Dev View read these.
        public Vector2 RenderPosition { get; private set; }
        public float RenderHeading { get; private set; }
        public int InterpolationFromTick { get; private set; }
        public int InterpolationToTick { get; private set; }
        public float InterpolationAlpha { get; private set; }

        public bool IsBot => Driver == PlayerRef.None;
        public bool IsLocalPlayer => !IsBot && Runner != null && Driver == Runner.LocalPlayer;
        public bool Finished => FinishTime > 0f;
        public Color Color => Palette.Car(Slot);
        public int CurrentLap => Mathf.Clamp(LapsDone + 1, 1, RaceDirector.Laps);

        /// <summary>How old the newest snapshot of this car is, in seconds. Zero for cars this client simulates.</summary>
        public float SnapshotAge =>
            HasStateAuthority ? 0f : Mathf.Clamp((Runner.Tick - Object.LastReceiveTick) * Runner.DeltaTime, 0f, 0.5f);

        public string DisplayName
        {
            get
            {
                if (IsBot) return BotDriver.Roster[Slot].Name;
                return IsLocalPlayer ? "You" : $"Player {Driver.PlayerId}";
            }
        }

        BotDriver _bot;
        int _trackHint = -1;
        bool _hadAuthority;
        float _nextAuthorityRequest;
        PropertyReader<Vector2> _positionReader;
        PropertyReader<float> _headingReader;
        PropertyReader<byte> _teleportReader;

        public override void Spawned()
        {
            All.Add(this);
            All.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            _bot = new BotDriver(BotDriver.Roster[Slot]);
            _positionReader = GetPropertyReader<Vector2>(nameof(Position));
            _headingReader = GetPropertyReader<float>(nameof(Heading));
            _teleportReader = GetPropertyReader<byte>(nameof(TeleportCount));
            RenderPosition = Position;
            RenderHeading = Heading;
            GetComponent<CarView>().Bind(this);
        }

        public override void Despawned(NetworkRunner runner, bool hasState) => All.Remove(this);

        public override void FixedUpdateNetwork()
        {
            RaceDirector race = RaceDirector.Instance;
            if (!HasStateAuthority || race == null) return;

            if (!_hadAuthority)
            {
                _hadAuthority = true;
                _trackHint = -1;  // we just received this car: do a full search once
            }

            if (RaceNumber != race.RaceNumber)
            {
                LineUp(race);
                return;
            }
            if (race.Frozen) return;

            float dt = Runner.DeltaTime;
            GhostTime = Mathf.Max(0f, GhostTime - dt);
            if (IsLocalPlayer) Autopilot = GameSession.Instance.Paused || GameSession.Instance.AutopilotClient;

            CarState state = ReadState();
            if (race.Phase == RacePhase.Waiting || race.Phase == RacePhase.Countdown)
            {
                state.Velocity = Vector2.zero;  // parked on the grid
                WriteState(state);
                return;
            }

            TrackPath track = ArenaCircuit.Path;
            CarInput input = ChooseInput(race, state, dt, out bool wantsReset);
            if (wantsReset)
            {
                ResetOntoTrack();
                return;
            }

            CarPhysics.Step(ref state, input, dt);
            float impact = CarPhysics.CollideWithWalls(ref state, track, _trackHint);
            impact = Mathf.Max(impact, CollideWithCars(ref state));
            if (impact > 4f) BumpCount++;

            TrackPath.Location at = track.Locate(state.Position, _trackHint);
            _trackHint = at.Index;
            WriteState(state);
            UpdateProgress(race, at, dt);
        }

        public override void Render()
        {
            // Cars driven by someone else must ask for authority once the master has handed them over.
            if (IsLocalPlayer && !HasStateAuthority && Time.unscaledTime >= _nextAuthorityRequest)
            {
                _nextAuthorityRequest = Time.unscaledTime + 1f;
                Object.RequestStateAuthority();
            }
            if (!HasStateAuthority) _hadAuthority = false;

            if (TryGetSnapshotsBuffers(out NetworkBehaviourBuffer from, out NetworkBehaviourBuffer to, out float alpha))
            {
                (Vector2 p0, Vector2 p1) = _positionReader.Read(from, to);
                (float h0, float h1) = _headingReader.Read(from, to);
                (byte t0, byte t1) = _teleportReader.Read(from, to);
                if (t0 != t1)
                {
                    p0 = p1;  // reset between these snapshots: snap instead of sliding across the map
                    h0 = h1;
                }
                RenderPosition = Vector2.Lerp(p0, p1, alpha);
                RenderHeading = Mathf.LerpAngle(h0 * Mathf.Rad2Deg, h1 * Mathf.Rad2Deg, alpha) * Mathf.Deg2Rad;
                InterpolationFromTick = from.Tick;
                InterpolationToTick = to.Tick;
                InterpolationAlpha = alpha;
            }
            else
            {
                RenderPosition = Position;
                RenderHeading = Heading;
            }

            transform.SetPositionAndRotation(
                new Vector3(RenderPosition.x, 0f, RenderPosition.y),
                Quaternion.Euler(0f, RenderHeading * Mathf.Rad2Deg, 0f));
        }

        CarInput ChooseInput(RaceDirector race, in CarState state, float dt, out bool wantsReset)
        {
            bool raceOver = Finished || race.Phase == RacePhase.Results;
            if (IsLocalPlayer && !Autopilot && !raceOver)
            {
                wantsReset = GameInput.ConsumeReset();
                return GameInput.ReadCar();
            }

            // Bots, paused players and cool-down laps after the flag all use the same AI.
            float pace = raceOver ? 0.55f : race.BotPace(this);
            return _bot.Think(state, _trackHint < 0 ? ArenaCircuit.Path.Locate(state.Position).Index : _trackHint,
                Progress, pace, All, this, dt, out wantsReset);
        }

        float CollideWithCars(ref CarState state)
        {
            if (GhostTime > 0f) return 0f;
            float strongest = 0f;
            foreach (NetCar other in All)
            {
                if (other == this || other.GhostTime > 0f) continue;
                // A remote car's newest snapshot is already a few ticks old. Collide with where it is *now*
                // (extrapolated), not where it was, or you get hit by cars that have already moved on.
                Vector2 otherPosition = other.Position + other.Velocity * other.SnapshotAge;
                strongest = Mathf.Max(strongest,
                    CarPhysics.CollideWithCar(ref state, otherPosition, other.Heading, other.Velocity));
            }
            return strongest;
        }

        void UpdateProgress(RaceDirector race, in TrackPath.Location at, float dt)
        {
            float length = ArenaCircuit.Path.Length;
            float along = ArenaCircuit.ToRaceDistance(at.Distance);
            float delta = along - Mathf.Repeat(Progress, length);
            if (delta > length * 0.5f) delta -= length;
            else if (delta < -length * 0.5f) delta += length;

            float before = Progress;
            Progress += delta;
            if (race.Phase != RacePhase.Racing || Finished) return;

            int lap = Mathf.FloorToInt(Progress / length);
            if (lap <= LapsDone || lap < 1) return;

            // Crossed the line. Time it to a fraction of a tick, so the result doesn't depend on tick boundaries.
            float fraction = Mathf.Clamp01((lap * length - before) / Mathf.Max(1e-4f, Progress - before));
            float crossTime = race.RaceTime - dt * (1f - fraction);
            float lapTime = crossTime - LapStartTime;
            if (BestLap <= 0f || lapTime < BestLap) BestLap = lapTime;
            LapStartTime = crossTime;
            LapsDone = lap;
            if (LapsDone >= RaceDirector.Laps) FinishTime = Mathf.Max(0.001f, crossTime);
        }

        void LineUp(RaceDirector race)
        {
            ArenaCircuit.GridBox box = ArenaCircuit.Grid(race.GridPositionOf(Slot));
            WriteState(new CarState { Position = box.Position, Heading = box.Heading });
            Progress = box.Progress;
            LapsDone = 0;
            LapStartTime = 0f;
            BestLap = 0f;
            FinishTime = 0f;
            GhostTime = 0f;
            Autopilot = false;
            RaceNumber = race.RaceNumber;
            TeleportCount++;
            _trackHint = -1;
        }

        void ResetOntoTrack()
        {
            TrackPath track = ArenaCircuit.Path;
            float along = ArenaCircuit.ToTrackDistance(Progress - 4f);
            WriteState(new CarState { Position = track.PointAt(along), Heading = track.HeadingAt(along) });
            Progress -= 4f;
            GhostTime = 2f;
            TeleportCount++;
            _trackHint = -1;
        }

        CarState ReadState() => new()
        {
            Position = Position,
            Velocity = Velocity,
            Heading = Heading,
            Steer = Steer,
            DriftDir = DriftDir,
            DriftCharge = DriftCharge,
            BoostTime = BoostTime,
        };

        void WriteState(in CarState s)
        {
            Position = s.Position;
            Velocity = s.Velocity;
            Heading = s.Heading;
            Steer = s.Steer;
            DriftDir = s.DriftDir;
            DriftCharge = s.DriftCharge;
            BoostTime = s.BoostTime;
        }

        public static NetCar FindDrivenBy(PlayerRef player)
        {
            if (player == PlayerRef.None) return null;
            foreach (NetCar car in All)
                if (car.Driver == player) return car;
            return null;
        }
    }
}
