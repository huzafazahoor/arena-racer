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
        readonly HashSet<PlayerRef> _knownPlayers = new();

        void Awake()
        {
            Instance = this;
            Application.targetFrameRate = -1;
            AutopilotClient = UrlParameter("autopilot") == "1";
        }

        async void Start()
        {
            string appId = PhotonAppSettings.Global.AppSettings.AppIdFusion;
            if (string.IsNullOrWhiteSpace(appId))
            {
                await StartOffline("No Photon App ID in this build");
                return;
            }

            State = ConnectionState.Connecting;
            _connectStarted = Time.unscaledTime;
            string room = UrlParameter("room");
            StartGameResult result = await StartRunner(GameMode.Shared, string.IsNullOrEmpty(room) ? null : room);
            if (result.Ok)
            {
                State = ConnectionState.Online;
                return;
            }
            await StartOffline(result.ShutdownReason == ShutdownReason.Ok ? "Connection timed out" : Describe(result.ShutdownReason));
        }

        void Update()
        {
            if (State == ConnectionState.Connecting && Runner != null && Time.unscaledTime - _connectStarted > connectTimeout)
            {
                _shuttingDownOnPurpose = true;
                Runner.Shutdown();  // StartGame() then returns a failed result and we go offline
            }

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
            if (runner != Runner || _shuttingDownOnPurpose || State != ConnectionState.Online) return;
            // Lost the room mid-session (tab slept too long, network dropped): keep playing offline.
            Notice?.Invoke("Disconnected · racing bots offline");
            Paused = false;
            _ = StartOffline("Disconnected: " + Describe(shutdownReason));
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
