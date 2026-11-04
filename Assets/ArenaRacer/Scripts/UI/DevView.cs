using System.Collections.Generic;
using Fusion;
using Fusion.Statistics;
using UnityEngine;
using UnityEngine.UI;

namespace ArenaRacer
{
    /// <summary>
    /// Tab toggles a live view of the netcode:
    ///  - ghosts: where the newest snapshot of a remote car says it is (cyan), where it probably is right now
    ///    (green, extrapolated by the snapshot's age), and where other players see the cars you simulate (magenta);
    ///  - who has State Authority over every car;
    ///  - ping, tick and send rates, interpolation delay, input latency to other screens, bandwidth;
    ///  - a timeline of snapshots arriving for one remote car, with the point in time it's being drawn at.
    /// </summary>
    public sealed class DevView : MonoBehaviour
    {
        public static bool Enabled { get; private set; }

        public static void Toggle()
        {
            Enabled = !Enabled;
            PlayerPrefs.SetInt("arena-racer.devview", Enabled ? 1 : 0);
        }

        const int HistoryLength = 160;     // render-time samples per car (~2.5 s at 60 fps)
        const int PingSamples = 120;       // 10 per second → 12 s of ping history
        const float TimelineSpan = 0.35f;  // seconds shown in the interpolation timeline

        sealed class CarTrace
        {
            public readonly float[] Times = new float[HistoryLength];
            public readonly Vector2[] Positions = new Vector2[HistoryLength];
            public readonly float[] Headings = new float[HistoryLength];
            public int Next, Count;
            public readonly List<int> ReceivedTicks = new();
            public int LastReceiveTick = -1;
            public Text Label;
        }

        readonly Dictionary<NetCar, CarTrace> _traces = new();
        readonly List<(Vector3 a, Vector3 b, Color color)> _lines = new();
        readonly float[] _ping = new float[PingSamples];
        int _pingNext;
        float _nextPingSample;
        float _bytesIn, _bytesOut, _bandwidthWindow;
        float _shownIn = -1f, _shownOut = -1f;

        GameObject _root;
        Text _keys, _values, _timelineCaption, _pingCaption;
        UiPlot _pingPlot, _timeline;
        RectTransform _labelRoot;
        MaterialPropertyBlock _block;
        Camera _camera;

        void Awake() => Enabled = PlayerPrefs.GetInt("arena-racer.devview", 0) == 1;

        void Start()
        {
            _camera = Camera.main;
            _block = new MaterialPropertyBlock();
            Build();
            Camera.onPostRender += DrawLines;
        }

        void OnDestroy() => Camera.onPostRender -= DrawLines;

        void LateUpdate()
        {
            GameSession session = GameSession.Instance;
            NetworkRunner runner = session != null ? session.Runner : null;
            bool active = Enabled && session != null && session.WantsToRace && runner != null && runner.IsRunning;
            _root.SetActive(active);
            _lines.Clear();

            SampleStatistics(runner);
            if (!active)
            {
                foreach (CarTrace trace in _traces.Values) trace.Label.enabled = false;
                return;
            }

            float othersSeeDelay = OthersSeeDelay(runner);
            NetCar timelineCar = null;
            foreach (NetCar car in NetCar.All)
            {
                CarTrace trace = TraceFor(car);
                Record(car, trace, runner);
                DrawGhosts(car, trace, othersSeeDelay);
                UpdateLabel(car, trace, runner);
                if (!car.HasStateAuthority && (timelineCar == null || Distance(car) < Distance(timelineCar))) timelineCar = car;
            }

            UpdatePanel(session, runner, othersSeeDelay);
            UpdatePingPlot();
            UpdateTimeline(runner, timelineCar);
        }

        // ---------- Ghosts ----------

        void DrawGhosts(NetCar car, CarTrace trace, float othersSeeDelay)
        {
            Mesh ghost = CarMeshFactory.Ghost();
            Vector3 drawn = car.transform.position + Vector3.up * 0.05f;

            if (!car.HasStateAuthority)
            {
                // Newest snapshot we have, and that snapshot pushed forward by its age.
                Vector3 snapshot = To3(car.Position);
                Vector3 predicted = To3(car.Position + car.Velocity * car.SnapshotAge);
                if (FarEnough(drawn, snapshot)) DrawGhost(ghost, snapshot, car.Heading, Palette.Snapshot);
                if (FarEnough(drawn, predicted)) DrawGhost(ghost, predicted, car.Heading, Palette.Predicted);
                _lines.Add((drawn, snapshot, Palette.Snapshot));
                _lines.Add((snapshot, predicted, Palette.Predicted));
            }
            else if (othersSeeDelay > 0.001f && TrySample(trace, Time.unscaledTime - othersSeeDelay, out Vector2 past, out float pastHeading))
            {
                Vector3 seen = To3(past);
                if (FarEnough(drawn, seen)) DrawGhost(ghost, seen, pastHeading, Palette.OthersSee);
                _lines.Add((drawn, seen, Palette.OthersSee));
            }
        }

        void DrawGhost(Mesh mesh, Vector3 position, float heading, Color color)
        {
            _block.SetColor("_Color", new Color(color.r, color.g, color.b, 0.55f));
            Matrix4x4 matrix = Matrix4x4.TRS(position, Quaternion.Euler(0f, heading * Mathf.Rad2Deg, 0f), new Vector3(1.04f, 1.04f, 1.04f));
            Graphics.DrawMesh(mesh, matrix, GameAssets.Instance.Ghost, 0, null, 0, _block, false, false);
        }

        void DrawLines(Camera cam)
        {
            if (cam != _camera || _lines.Count == 0 || GameAssets.Instance == null) return;
            GameAssets.Instance.Line.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            foreach ((Vector3 a, Vector3 b, Color color) in _lines)
            {
                GL.Color(color);
                GL.Vertex(a + Vector3.up * 0.6f);
                GL.Vertex(b + Vector3.up * 0.6f);
            }
            GL.End();
            GL.PopMatrix();
        }

        // ---------- History of what was drawn, for "where others see you" ----------

        CarTrace TraceFor(NetCar car)
        {
            if (_traces.TryGetValue(car, out CarTrace trace)) return trace;
            trace = new CarTrace
            {
                Label = Ui.Label(_labelRoot, "", 12, Palette.Text, TextAnchor.LowerCenter, FontStyle.Normal, shadow: true),
            };
            trace.Label.rectTransform.sizeDelta = new Vector2(180f, 34f);
            _traces[car] = trace;
            return trace;
        }

        static void Record(NetCar car, CarTrace trace, NetworkRunner runner)
        {
            trace.Times[trace.Next] = Time.unscaledTime;
            trace.Positions[trace.Next] = car.RenderPosition;
            trace.Headings[trace.Next] = car.RenderHeading;
            trace.Next = (trace.Next + 1) % HistoryLength;
            trace.Count = Mathf.Min(trace.Count + 1, HistoryLength);

            if (!car.HasStateAuthority && car.Object.LastReceiveTick != trace.LastReceiveTick)
            {
                trace.LastReceiveTick = car.Object.LastReceiveTick;
                trace.ReceivedTicks.Add(trace.LastReceiveTick);
                if (trace.ReceivedTicks.Count > 40) trace.ReceivedTicks.RemoveAt(0);
            }
        }

        static bool TrySample(CarTrace trace, float time, out Vector2 position, out float heading)
        {
            position = default;
            heading = 0f;
            for (int k = 1; k < trace.Count; k++)
            {
                int newer = Wrap(trace.Next - k), older = Wrap(trace.Next - k - 1);
                if (trace.Times[older] > time) continue;
                float span = trace.Times[newer] - trace.Times[older];
                float t = span > 1e-5f ? Mathf.Clamp01((time - trace.Times[older]) / span) : 1f;
                position = Vector2.Lerp(trace.Positions[older], trace.Positions[newer], t);
                heading = Mathf.LerpAngle(trace.Headings[older] * Mathf.Rad2Deg, trace.Headings[newer] * Mathf.Rad2Deg, t) * Mathf.Deg2Rad;
                return true;
            }
            return false;
        }

        static int Wrap(int i) => ((i % HistoryLength) + HistoryLength) % HistoryLength;

        // ---------- Labels, panel, plots ----------

        void UpdateLabel(NetCar car, CarTrace trace, NetworkRunner runner)
        {
            Vector3 screen = _camera.WorldToScreenPoint(car.transform.position + Vector3.up * 2.4f);
            bool visible = screen.z > 0f && screen.z < 45f;
            trace.Label.enabled = visible;
            if (!visible) return;
            trace.Label.rectTransform.position = screen;

            // Two short lines: who drives it, and who simulates it (plus snapshot age for remote cars).
            string who = car.IsLocalPlayer ? "<b>You</b>" : car.IsBot ? car.DisplayName : $"<b>{car.DisplayName}</b>";
            if (car.Autopilot) who += " <color=#9AA0AB>(autopilot)</color>";
            string detail = car.HasStateAuthority
                ? $"<color={Palette.ToRichText(Palette.OthersSee)}>sim here</color>"
                : $"<color={Palette.ToRichText(Palette.Snapshot)}>sim {ShortName(car.Object.StateAuthority)} · {car.SnapshotAge * 1000f:0} ms</color>";
            trace.Label.text = $"{who}\n{detail}";
        }

        static string ShortName(PlayerRef player)
        {
            if (player == PlayerRef.None) return "nobody";
            RaceDirector race = RaceDirector.Instance;
            bool master = race != null && race.Object.StateAuthority == player;
            return master ? $"P{player.PlayerId}·master" : $"P{player.PlayerId}";
        }

        static string PlayerName(PlayerRef player, NetworkRunner runner)
        {
            if (player == PlayerRef.None) return "nobody (handing over)";
            if (player == runner.LocalPlayer) return "you";
            RaceDirector race = RaceDirector.Instance;
            bool master = race != null && race.Object.StateAuthority == player;
            return master ? $"Player {player.PlayerId} (master)" : $"Player {player.PlayerId}";
        }

        void UpdatePanel(GameSession session, NetworkRunner runner, float othersSeeDelay)
        {
            float rtt = (float)runner.GetPlayerRtt(runner.LocalPlayer);
            float interpolation = Mathf.Max(0f, runner.LocalRenderTime - runner.RemoteRenderTime);
            float tickRate = 1f / runner.DeltaTime;
            TickRate.Resolved rates = TickRate.Resolve(runner.Config.Simulation.TickRateSelection);
            int simulatedHere = 0, bots = 0;
            foreach (NetCar car in NetCar.All)
            {
                if (car.HasStateAuthority) simulatedHere++;
                if (car.IsBot) bots++;
            }

            bool online = session.IsOnline;
            string master = !online ? "you (offline)" : PlayerName(RaceDirector.Instance != null ? RaceDirector.Instance.Object.StateAuthority : PlayerRef.None, runner);
            SessionInfo info = runner.SessionInfo;

            _keys.text =
                "Mode\nRoom\nMaster client\nPing (RTT)\nTick\nSend rate\nRemote cars drawn\nYour input\nBandwidth\nCars simulated here";
            _values.text =
                $"{(online ? "Shared (Photon Cloud)" : "Single (offline, no network)")}\n" +
                $"{(online ? $"{info.Name.Substring(0, Mathf.Min(6, info.Name.Length)).ToUpperInvariant()} · {info.Region} · {info.PlayerCount}/{RaceDirector.CarCount} players" : "-")}\n" +
                $"{master}\n" +
                $"{(online ? $"{rtt * 1000f:0} ms" : "-")}\n" +
                $"#{(int)runner.Tick} · {tickRate:0} Hz ({runner.DeltaTime * 1000f:0.0} ms)\n" +
                $"{(online ? $"{rates.ClientSend} Hz up · {rates.ServerSend} Hz down" : "-")}\n" +
                $"{(online ? $"{interpolation * 1000f:0} ms in the past (interpolated)" : "-")}\n" +
                $"next tick here{(online ? $" · ~{othersSeeDelay * 1000f:0} ms on other screens" : "")}\n" +
                $"{(online && _shownIn >= 0f ? $"in {_shownIn / 1024f:0.0} kB/s · out {_shownOut / 1024f:0.0} kB/s" : "-")}\n" +
                $"{simulatedHere} of {NetCar.All.Count} ({bots} bots)";
        }

        void SampleStatistics(NetworkRunner runner)
        {
            if (runner == null || !runner.IsRunning) return;

            if (Time.unscaledTime >= _nextPingSample)
            {
                _nextPingSample = Time.unscaledTime + 0.1f;
                _ping[_pingNext] = (float)runner.GetPlayerRtt(runner.LocalPlayer) * 1000f;
                _pingNext = (_pingNext + 1) % PingSamples;
            }

            // Fusion's statistics snapshot holds this frame's traffic; sum it over one second like Fusion's own graphs.
            if (runner.TryGetFusionStatistics(out FusionStatisticsManager statistics))
            {
                FusionStatisticsSnapshot snapshot = statistics.CompleteSnapshot;
                _bytesIn += snapshot.InBandwidth;
                _bytesOut += snapshot.OutBandwidth;
            }
            _bandwidthWindow += Time.unscaledDeltaTime;
            if (_bandwidthWindow >= 1f)
            {
                _shownIn = _bytesIn / _bandwidthWindow;
                _shownOut = _bytesOut / _bandwidthWindow;
                _bytesIn = _bytesOut = _bandwidthWindow = 0f;
            }
        }

        void UpdatePingPlot()
        {
            float max = 100f;
            foreach (float p in _ping) max = Mathf.Max(max, p * 1.2f);
            _pingPlot.Clear();
            _pingPlot.Box(Vector2.zero, Vector2.one, new Color(1f, 1f, 1f, 0.05f));
            for (float line = 50f; line < max; line += 50f)
                _pingPlot.Line(new Vector2(0f, line / max), new Vector2(1f, line / max), 1f, new Color(1f, 1f, 1f, 0.08f));
            for (int i = 1; i < PingSamples; i++)
            {
                float a = _ping[(_pingNext + i - 1) % PingSamples], b = _ping[(_pingNext + i) % PingSamples];
                if (a <= 0f || b <= 0f) continue;
                _pingPlot.Line(new Vector2((i - 1f) / (PingSamples - 1), a / max), new Vector2(i / (PingSamples - 1f), b / max), 2f, Palette.Accent);
            }
            _pingPlot.Apply();
            _pingCaption.text = $"Ping, last 12 s  <color=#9AA0AB>(scale {max:0} ms)</color>";
        }

        void UpdateTimeline(NetworkRunner runner, NetCar car)
        {
            _timeline.Clear();
            _timeline.Box(Vector2.zero, Vector2.one, new Color(1f, 1f, 1f, 0.05f));
            if (car == null || !_traces.TryGetValue(car, out CarTrace trace))
            {
                _timelineCaption.text = "Interpolation timeline: no remote cars yet.\n<color=#9AA0AB>Open the game in a second window to see snapshots arrive here.</color>";
                _timeline.Apply();
                return;
            }

            float dt = runner.DeltaTime;
            float now = runner.LocalRenderTime;
            float left = now - TimelineSpan;
            float X(float time) => Mathf.Clamp01((time - left) / TimelineSpan);

            // The two snapshots being blended right now.
            float from = car.InterpolationFromTick * dt, to = car.InterpolationToTick * dt;
            _timeline.Box(new Vector2(X(from), 0.1f), new Vector2(X(to), 0.9f), new Color(Palette.Snapshot.r, Palette.Snapshot.g, Palette.Snapshot.b, 0.18f));
            foreach (int tick in trace.ReceivedTicks)
            {
                float x = X(tick * dt);
                if (x <= 0f) continue;
                _timeline.Line(new Vector2(x, 0.15f), new Vector2(x, 0.85f), 2f, Palette.Snapshot);
            }
            float drawnAt = X(runner.RemoteRenderTime);
            _timeline.Line(new Vector2(drawnAt, 0f), new Vector2(drawnAt, 1f), 3f, Color.white);
            _timeline.Line(new Vector2(1f, 0f), new Vector2(1f, 1f), 3f, Palette.Predicted);
            _timeline.Apply();

            _timelineCaption.text =
                $"Snapshots of <b>{car.DisplayName}</b> arriving (<color={Palette.ToRichText(Palette.Snapshot)}>|</color>), " +
                $"drawn at the <b>white</b> line,\n{(now - runner.RemoteRenderTime) * 1000f:0} ms behind now (<color={Palette.ToRichText(Palette.Predicted)}>green</color>). Window: {TimelineSpan * 1000f:0} ms.";
        }

        // ---------- Helpers ----------

        float OthersSeeDelay(NetworkRunner runner)
        {
            if (GameSession.Instance == null || !GameSession.Instance.IsOnline) return 0f;
            // Remote cars are drawn this far behind your own timeline: transit through the Photon server plus the
            // interpolation buffer. Assuming the other player's connection is like yours, they see you the same
            // distance in the past.
            return Mathf.Max(0f, runner.LocalRenderTime - runner.RemoteRenderTime);
        }

        float Distance(NetCar car) => _camera != null ? Vector3.Distance(car.transform.position, _camera.transform.position) : 0f;

        static Vector3 To3(Vector2 p) => new(p.x, 0.05f, p.y);

        /// <summary>A ghost sitting right on top of its car only tints it; skip it until they separate.</summary>
        static bool FarEnough(Vector3 a, Vector3 b) => (a - b).sqrMagnitude > 0.8f * 0.8f;

        void Build()
        {
            Canvas canvas = Ui.Canvas("Dev View", 5);  // under the HUD, so pause and results menus cover it
            canvas.transform.SetParent(transform, false);
            _labelRoot = Ui.Rect("Car Labels", canvas.transform).Stretch();

            Image panel = Ui.Panel("Dev Panel", canvas.transform, new Color(0.03f, 0.04f, 0.06f, 0.88f));
            panel.rectTransform.Place(new Vector2(0f, 1f), new Vector2(16f, -178f), new Vector2(430f, 470f));
            _root = panel.gameObject;
            Transform p = panel.transform;

            Ui.Label(p, "<b>DEV VIEW</b>  <color=#9AA0AB>Photon Fusion 2 · Tab to hide</color>", 16, Palette.Accent, TextAnchor.UpperLeft)
                .rectTransform.Place(new Vector2(0f, 1f), new Vector2(14f, -10f), new Vector2(400f, 22f));

            _keys = Ui.Label(p, "", 13, Palette.Muted, TextAnchor.UpperLeft);
            _keys.lineSpacing = 1.15f;
            _keys.rectTransform.Place(new Vector2(0f, 1f), new Vector2(14f, -38f), new Vector2(130f, 180f));
            _values = Ui.Label(p, "", 13, Palette.Text, TextAnchor.UpperLeft);
            _values.lineSpacing = 1.15f;
            _values.rectTransform.Place(new Vector2(0f, 1f), new Vector2(146f, -38f), new Vector2(276f, 180f));

            // Legend.
            string[] names = { "drawn (interpolated)", "newest snapshot", "extrapolated to now", "what others see of your cars" };
            Color[] colours = { Color.white, Palette.Snapshot, Palette.Predicted, Palette.OthersSee };
            for (int i = 0; i < names.Length; i++)
            {
                float x = 14f + (i % 2) * 205f, y = -222f - (i / 2) * 20f;
                Ui.Panel("Swatch", p, colours[i], rounded: false).rectTransform.Place(new Vector2(0f, 1f), new Vector2(x, y - 5f), new Vector2(10f, 10f));
                Ui.Label(p, names[i], 12, Palette.Text, TextAnchor.MiddleLeft)
                    .rectTransform.Place(new Vector2(0f, 1f), new Vector2(x + 16f, y), new Vector2(190f, 20f));
            }

            _pingCaption = Ui.Label(p, "", 12, Palette.Text, TextAnchor.LowerLeft);
            _pingCaption.rectTransform.Place(new Vector2(0f, 1f), new Vector2(14f, -268f), new Vector2(400f, 18f));
            _pingPlot = Ui.Rect("Ping Plot", p).gameObject.AddComponent<UiPlot>();
            _pingPlot.raycastTarget = false;
            _pingPlot.rectTransform.Place(new Vector2(0f, 1f), new Vector2(14f, -290f), new Vector2(402f, 50f));

            _timeline = Ui.Rect("Timeline", p).gameObject.AddComponent<UiPlot>();
            _timeline.raycastTarget = false;
            _timeline.rectTransform.Place(new Vector2(0f, 1f), new Vector2(14f, -354f), new Vector2(402f, 44f));
            _timelineCaption = Ui.Label(p, "", 12, Palette.Text, TextAnchor.UpperLeft);
            _timelineCaption.rectTransform.Place(new Vector2(0f, 1f), new Vector2(14f, -402f), new Vector2(402f, 60f));
        }
    }
}
