using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Fusion;
using Fusion.Photon.Realtime;
using Fusion.Sockets;
using UnityEngine;

namespace ArenaRacer
{
    /// <summary>
    /// Owns the NetworkRunner. Tries Photon Fusion Shared mode first (join any open room, or the one named in
    /// <c>?room=</c>), and falls back to Fusion's Single mode if there is no App ID or no connection, so the
    /// exact same game code runs against bots offline.
    /// </summary>
    public sealed class GameSession : MonoBehaviour, INetworkRunnerCallbacks
    {
        public enum ConnectionState { Connecting, Online, Offline }

        public static GameSession Instance { get; private set; }

        [SerializeField] NetworkObject directorPrefab;
        [SerializeField] float connectTimeout = 12f;

        public NetworkRunner Runner { get; private set; }
        public ConnectionState State { get; private set; } = ConnectionState.Connecting;
        public string OfflineReason { get; private set; } = "";
        public bool WantsToRace { get; private set; }
        public bool Paused { get; private set; }
        public bool AutopilotClient { get; private set; }   // ?autopilot=1: this client's car drives itself (demo page)

        public NetCar LocalCar => Runner != null && Runner.IsRunning ? NetCar.FindDrivenBy(Runner.LocalPlayer) : null;
        public bool IsOnline => State == ConnectionState.Online;

        /// <summary>Raised for room events worth a toast: joins, leaves, take-overs, master changes.</summary>
        public event Action<string> Notice;

        float _connectStarted;
        float _joinedAt;
        float _nextCarRequest;
        float _nextAutoVote;
        bool _shuttingDownOnPurpose;
        string _roomName;
        bool _connectingOnline;
        bool _reconnecting;
        float _lastReconnect;
        bool _wasOnline;
        int _retries;
        const string LostConnection = "Lost the connection to Photon";

        static bool MidRace => RaceDirector.Instance != null && RaceDirector.Instance.Phase == RacePhase.Racing;
        readonly HashSet<PlayerRef> _knownPlayers = new();

        void Awake()
        {
            Instance = this;
            Application.targetFrameRate = -1;
            AutopilotClient = UrlParameter("autopilot") == "1";
        }

        async void Start() => await Connect(UrlParameter("room"));

        /// <summary>Join (or create) a Shared-mode room; null joins any open one. Falls back to offline on failure.</summary>
        async Task Connect(string room)
        {
            string appId = PhotonAppSettings.Global.AppSettings.AppIdFusion;
            if (string.IsNullOrWhiteSpace(appId))
            {
                await StartOffline("No Photon App ID in this build");
                return;
            }

            State = ConnectionState.Connecting;
            _connectStarted = Time.unscaledTime;
            _shuttingDownOnPurpose = false;
            _connectingOnline = true;
            StartGameResult result = await StartRunner(GameMode.Shared, string.IsNullOrEmpty(room) ? null : room);
            _connectingOnline = false;
            if (result.Ok && !Runner.IsShutdown)
            {
                State = ConnectionState.Online;
                _wasOnline = true;
                _retries = 0;
                _roomName = Runner.SessionInfo != null && Runner.SessionInfo.IsValid ? Runner.SessionInfo.Name : room;
                return;
            }
            // The connection can also die between StartGame() succeeding and this line running (a throttled
            // background tab does that), so "Ok" with a runner that has already shut down counts as a failure.
            string reason = result.Ok ? LostConnection
                : result.ShutdownReason == ShutdownReason.Ok ? "Connection timed out"
                : Describe(result.ShutdownReason);
            await StartOffline(reason);
        }

        /// <summary>The room dropped us (often a background tab the browser stopped updating). Rejoin it.</summary>
        async Task Reconnect()
        {
            if (_reconnecting) return;
            _reconnecting = true;
            _lastReconnect = Time.unscaledTime;
            Paused = false;
            await Task.Yield();  // let Fusion finish tearing the old runner down first
            await Connect(_roomName);
            _reconnecting = false;
        }

        /// <summary>
        /// Watchdog: however the connection was lost (timeout callback, a race with StartGame, a sleeping tab),
        /// a session that claims to be online without a running runner gets rejoined. And if a lost connection
        /// pushed us offline, we try online again once the tab has focus.
        /// </summary>
        void KeepConnected()
        {
            if (_reconnecting) return;
            bool runnerDead = Runner == null || Runner.IsShutdown;  // (IsRunning is still false for a moment after a good start)
            if (State == ConnectionState.Online && runnerDead)
            {
                Notice?.Invoke("Connection lost · rejoining the room");
                _ = Reconnect();
            }
            else if (State == ConnectionState.Offline && (_wasOnline || OfflineReason == LostConnection) && _retries < 5 && Application.isFocused
                     && Time.unscaledTime - _lastReconnect > 15f && !MidRace)
            {
                _retries++;
                Notice?.Invoke("Trying to get back online");
                _ = Reconnect();
            }
        }

        void Update()
        {
            if (_connectingOnline && Runner != null && Time.unscaledTime - _connectStarted > connectTimeout)
            {
                _shuttingDownOnPurpose = true;
                _connectingOnline = false;
                Runner.Shutdown();  // StartGame() then returns a failed result and we go offline
            }

            KeepConnected();

            if (AutopilotClient && !WantsToRace && State != ConnectionState.Connecting) WantsToRace = true;
            if (WantsToRace) ClaimCarWhenPossible();

            // A self-driving demo client is always ready for the next race.
            RaceDirector race = RaceDirector.Instance;
            if (AutopilotClient && race != null && race.Phase != RacePhase.Results)
            {
                _nextAutoVote = Time.unscaledTime + 6f;  // let the results be read first
            }
            else if (AutopilotClient && race != null && Time.unscaledTime >= _nextAutoVote)
            {
                _nextAutoVote = Time.unscaledTime + 2f;
                VoteRestart();
            }
        }

        /// <summary>Called by the title screen's Race button.</summary>
        public void JoinRace() => WantsToRace = true;

        public void SetPaused(bool paused)
        {
            Paused = paused;
            if (RaceDirector.Instance != null && Runner != null)
                RaceDirector.Instance.RPC_SetPaused(Runner.LocalPlayer, paused);
        }

        public void VoteRestart()
        {
            if (RaceDirector.Instance != null) RaceDirector.Instance.RPC_VoteRestart(Runner.LocalPlayer);
        }

        void ClaimCarWhenPossible()
        {
            if (Runner == null || !Runner.IsRunning || RaceDirector.Instance == null) return;
            if (LocalCar != null || Time.unscaledTime < _nextCarRequest) return;
            _nextCarRequest = Time.unscaledTime + 1.5f;  // retry: the request can race another joiner for the same bot
            RaceDirector.Instance.RPC_RequestCar(Runner.LocalPlayer);
        }

        async Task StartOffline(string reason)
        {
            State = ConnectionState.Connecting;  // until the offline runner is up, there is no session to show
            OfflineReason = reason;
            Debug.Log($"[ArenaRacer] Offline mode: {reason}");
            StartGameResult result = await StartRunner(GameMode.Single, "offline");
            State = ConnectionState.Offline;
            if (!result.Ok) Debug.LogError($"[ArenaRacer] Could not start offline runner: {result.ShutdownReason}");
        }

        async Task<StartGameResult> StartRunner(GameMode mode, string sessionName)
        {
            if (Runner != null) Destroy(Runner.gameObject);
            _knownPlayers.Clear();

            var runnerObject = new GameObject($"NetworkRunner ({mode})");  // root object: Fusion marks it DontDestroyOnLoad
            Runner = runnerObject.AddComponent<NetworkRunner>();
            Runner.ProvideInput = false;  // shared mode: each client simulates what it owns from local input
            Runner.AddCallbacks(this);

            return await Runner.StartGame(new StartGameArgs
            {
                GameMode = mode,
                SessionName = sessionName,
                PlayerCount = RaceDirector.CarCount,
                IsOpen = true,
                IsVisible = true,
            });
        }

        void SpawnDirectorIfNeeded(NetworkRunner runner)
        {
            bool roomAuthority = runner.IsSharedModeMasterClient || runner.GameMode == GameMode.Single;
            if (roomAuthority && RaceDirector.Instance == null && FindAnyObjectByType<RaceDirector>() == null)
                runner.Spawn(directorPrefab);
        }

        static string UrlParameter(string key)
        {
            string url = Application.absoluteURL;
            int query = url.IndexOf('?');
            if (query < 0) return null;
            foreach (string pair in url.Substring(query + 1).Split('&', '#'))
            {
                string[] kv = pair.Split('=');
                if (kv.Length == 2 && kv[0] == key) return Uri.UnescapeDataString(kv[1]);
            }
            return null;
        }

        static string Describe(ShutdownReason reason) => reason switch
        {
            ShutdownReason.InvalidAuthentication => "Photon rejected the App ID",
            ShutdownReason.CustomAuthenticationFailed => "Photon rejected the App ID",
            ShutdownReason.PhotonCloudTimeout => "Photon Cloud didn't answer",
            ShutdownReason.ConnectionTimeout => "Connection timed out",
            ShutdownReason.ConnectionRefused => "Connection refused",
            ShutdownReason.GameIsFull => "Room is full",
            ShutdownReason.MaxCcuReached => "Server is at capacity",
            _ => reason.ToString(),
        };

        // ---- INetworkRunnerCallbacks ----

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            if (player == runner.LocalPlayer)
            {
                SpawnDirectorIfNeeded(runner);
                _joinedAt = Time.unscaledTime;
            }
            else if (_knownPlayers.Add(player) && Time.unscaledTime - _joinedAt > 2f)
            {
                Notice?.Invoke($"Player {player.PlayerId} joined");  // players already in the room when we arrive don't get a toast
            }
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            _knownPlayers.Remove(player);
            Notice?.Invoke($"Player {player.PlayerId} left · a bot takes their car");
        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            // Recovery happens in KeepConnected(): it also covers shutdowns that race with StartGame().
            if (runner == Runner && !_shuttingDownOnPurpose) Debug.Log($"[ArenaRacer] Runner shut down: {shutdownReason}");
        }

        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
        public void OnConnectedToServer(NetworkRunner runner) { }
        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnInput(NetworkRunner runner, NetworkInput input) { }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        public void OnSceneLoadDone(NetworkRunner runner) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
    }
}
