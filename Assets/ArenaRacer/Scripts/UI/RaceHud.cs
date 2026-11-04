using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.UI;

namespace ArenaRacer
{
    /// <summary>Title screen, race HUD, pause menu, results and toasts. Reads game state; owns none of it.</summary>
    public sealed class RaceHud : MonoBehaviour
    {
        static readonly Color Dim = new(0f, 0f, 0f, 0.55f);

        GameObject _title, _hud, _carHud, _pause, _results;
        Text _titleStatus;
        Text _position, _lap, _clock, _best, _speed, _boostLabel, _center, _badge;
        Image _boostFill;
        RectTransform _boostTrack;
        readonly Text[] _rowNames = new Text[RaceDirector.CarCount];
        readonly Text[] _rowInfo = new Text[RaceDirector.CarCount];
        readonly Image[] _rowChips = new Image[RaceDirector.CarCount];
        Text _pauseTitle, _pauseBody;
        Button _restartButton, _devViewButton, _soundButton;
        Text _resultsHeadline, _resultsSub, _resultsStatus;
        const int ResultColumns = 5;
        static readonly float[] ColumnX = { 60f, 110f, 330f, 460f, 560f };
        static readonly float[] ColumnWidth = { 40f, 200f, 120f, 120f, 80f };
        readonly Text[,] _resultCells = new Text[RaceDirector.CarCount, ResultColumns];
        readonly Image[] _resultChips = new Image[RaceDirector.CarCount];
        RectTransform _toastRoot, _tagRoot;
        readonly List<(Text text, float until)> _toasts = new();
        readonly Dictionary<NetCar, Text> _nameTags = new();
        readonly List<NetCar> _standings = new();

        RacePhase _lastPhase = RacePhase.Waiting;
        int _lastRaceNumber;
        int _lastCountdown = -1;
        int _lastLaps;
        NetCar _lastCar;
        PlayerRef _lastMaster;
        string _message = "";
        float _messageUntil;
        float _wrongWayTime;
        Camera _camera;

        void Start()
        {
            _camera = Camera.main;
            Canvas canvas = Ui.Canvas("HUD", 10);
            canvas.transform.SetParent(transform, false);
            Transform root = canvas.transform;

            _tagRoot = Ui.Rect("Name Tags", root).Stretch();
            BuildHud(root);
            BuildPause(root);
            BuildResults(root);
            BuildTitle(root);
            _toastRoot = Ui.Rect("Toasts", root).Place(new Vector2(0.5f, 1f), new Vector2(0f, -52f), new Vector2(600f, 200f));

            GameSession.Instance.Notice += Toast;
        }

        void OnDestroy()
        {
            if (GameSession.Instance != null) GameSession.Instance.Notice -= Toast;
        }

        // ---------- Frame update ----------

        void Update()
        {
            GameInput.PollFrame();
            GameSession session = GameSession.Instance;
            RaceDirector race = RaceDirector.Instance;
            NetCar car = session.LocalCar;

            if (GameInput.MutePressed) Sfx.ToggleMute();
            if (GameInput.DevViewPressed) DevView.Toggle();

            bool onTitle = !session.WantsToRace;
            _title.SetActive(onTitle);
            _hud.SetActive(!onTitle);
            if (onTitle)
            {
                _titleStatus.text = ConnectionLine(session, verbose: true);
                if (GameInput.ConfirmPressed) StartRacing();
                UpdateNameTags(null);
                return;
            }

            _badge.text = ConnectionLine(session, verbose: false);
            _carHud.SetActive(race != null && car != null);
            UpdateToasts();
            WatchRoomEvents(session, race, car);

            if (race == null || car == null)
            {
                _center.text = race == null ? "Connecting…" : NetCar.All.Count > 0 && race.HumanCount >= RaceDirector.CarCount ? "Room full · spectating" : "Joining the race…";
                _results.SetActive(false);
                _pause.SetActive(false);
                UpdateNameTags(null);
                return;
            }

            if (GameInput.PausePressed && race.Phase != RacePhase.Results) SetPaused(!session.Paused);
            _pause.SetActive(session.Paused);
            if (session.Paused) UpdatePause(race);

            race.GetStandings(_standings);
            UpdateRacePanel(race, car);
            UpdateStandings(car);
            UpdateCenterText(race, car);
            UpdateResults(race, car, session);
            UpdateNameTags(car);
        }

        void StartRacing()
        {
            Sfx.Play(GameAssets.Instance.Click, 0.6f);
            GameSession.Instance.JoinRace();
        }

        void SetPaused(bool paused)
        {
            GameSession.Instance.SetPaused(paused);
            Sfx.Play(GameAssets.Instance.Click, 0.5f);
        }

        void UpdateRacePanel(RaceDirector race, NetCar car)
        {
            int place = _standings.IndexOf(car) + 1;
            _position.text = $"{Ui.Ordinal(place)}<size=26><color=#9AA0AB> / {_standings.Count}</color></size>";
            _lap.text = $"LAP {car.CurrentLap}/{RaceDirector.Laps}";
            float raceTime = car.Finished ? car.FinishTime : race.RaceTime;
            _clock.text = Ui.RaceClock(race.Phase == RacePhase.Waiting || race.Phase == RacePhase.Countdown ? 0f : raceTime);
            _best.text = car.BestLap > 0f ? $"BEST {Ui.RaceClock(car.BestLap)}" : "BEST --:--.--";

            _speed.text = Mathf.RoundToInt(car.Velocity.magnitude * 3.6f).ToString();

            int level = CarPhysics.DriftLevel(car.DriftCharge);
            float fill;
            Color colour;
            if (car.BoostTime > 0f)
            {
                fill = Mathf.Clamp01(car.BoostTime / 1.15f);
                colour = Palette.Accent;
                _boostLabel.text = "BOOST";
            }
            else if (car.DriftDir != 0)
            {
                fill = Mathf.Clamp01(car.DriftCharge / 1.8f);
                colour = level == 2 ? Palette.Hex(0xFF9A3C) : level == 1 ? Palette.Hex(0x5CC8FF) : Palette.Muted;
                _boostLabel.text = level == 2 ? "DRIFT · BIG BOOST" : level == 1 ? "DRIFT · BOOST" : "DRIFT";
            }
            else
            {
                fill = 0f;
                colour = Palette.Muted;
                _boostLabel.text = "HOLD SPACE TO DRIFT";
            }
            _boostFill.color = colour;
            _boostFill.rectTransform.sizeDelta = new Vector2(_boostTrack.rect.width * fill, 0f);
        }

        void UpdateStandings(NetCar mine)
        {
            for (int i = 0; i < RaceDirector.CarCount; i++)
            {
                bool used = i < _standings.Count;
                _rowNames[i].gameObject.SetActive(used);
                _rowInfo[i].gameObject.SetActive(used);
                _rowChips[i].gameObject.SetActive(used);
                if (!used) continue;
                NetCar car = _standings[i];
                bool isMine = car == mine;
                _rowChips[i].color = car.Color;
                _rowNames[i].text = $"{i + 1}  {car.DisplayName}";
                _rowNames[i].color = isMine ? Palette.Accent : Palette.Text;
                _rowNames[i].fontStyle = isMine ? FontStyle.Bold : FontStyle.Normal;
                _rowInfo[i].text = car.Finished ? Ui.RaceClock(car.FinishTime) : car.IsBot ? "bot" : "";
            }
        }

        void UpdateCenterText(RaceDirector race, NetCar car)
        {
            // Countdown beeps, then GO.
            if (race.Phase == RacePhase.Countdown)
            {
                int n = Mathf.CeilToInt(race.SecondsUntilStart);
                if (n != _lastCountdown && n <= 3 && n >= 1) Sfx.Play(GameAssets.Instance.Beep, 0.7f);
                _lastCountdown = n;
                _center.text = n > 3 ? "<size=40>GET READY</size>" : n.ToString();
                _center.color = Palette.Text;
                _lastPhase = race.Phase;
                return;
            }
            if (_lastPhase == RacePhase.Countdown && race.Phase == RacePhase.Racing)
            {
                Sfx.Play(GameAssets.Instance.Go, 0.8f);
                Flash("GO!", 0.9f);
            }
            _lastPhase = race.Phase;
            _lastCountdown = -1;

            // Lap and finish messages for your car.
            if (car != _lastCar || race.RaceNumber != _lastRaceNumber)
            {
                _lastCar = car;
                _lastRaceNumber = race.RaceNumber;
                _lastLaps = car.LapsDone;
            }
            if (car.LapsDone > _lastLaps)
            {
                _lastLaps = car.LapsDone;
                if (car.Finished)
                {
                    int place = _standings.IndexOf(car) + 1;
                    Sfx.Play(GameAssets.Instance.Finish, 0.9f);
                    Flash(place == 1 ? "YOU WIN!" : $"FINISHED {Ui.Ordinal(place).ToUpperInvariant()}", 2.5f);
                }
                else
                {
                    Sfx.Play(GameAssets.Instance.Lap, 0.8f);
                    Flash(car.LapsDone == RaceDirector.Laps - 1 ? "FINAL LAP" : $"LAP {car.LapsDone + 1}", 1.6f);
                }
            }

            // Wrong way.
            TrackPath.Location at = ArenaCircuit.Path.Locate(car.Position);
            bool backwards = Vector2.Dot(CarPhysics.Forward(car.Heading), at.Tangent) < -0.25f && car.Velocity.magnitude > 3f;
            _wrongWayTime = backwards && race.Phase == RacePhase.Racing ? _wrongWayTime + Time.deltaTime : 0f;

            if (Time.unscaledTime < _messageUntil)
            {
                _center.text = _message;
                _center.color = Palette.Accent;
            }
            else if (_wrongWayTime > 0.8f)
            {
                _center.text = "<size=44>WRONG WAY</size>";
                _center.color = Palette.Hex(0xE5484D);
            }
            else
            {
                _center.text = "";
            }
        }

        void Flash(string message, float seconds)
        {
            _message = message;
            _messageUntil = Time.unscaledTime + seconds;
        }

        void UpdatePause(RaceDirector race)
        {
            bool solo = race.HumanCount <= 1;
            _pauseTitle.text = solo ? "PAUSED" : "AUTOPILOT ON";
            _pauseBody.text = solo
                ? "The race is frozen until you resume."
                : "Other players are racing, so the race can't stop. A bot drives your car until you resume.";
            _restartButton.gameObject.SetActive(solo);
            _devViewButton.SetText(DevView.Enabled ? "Dev View: ON" : "Dev View: OFF");
            _soundButton.SetText(Sfx.Muted ? "Sound: OFF" : "Sound: ON");
        }

        void UpdateResults(RaceDirector race, NetCar car, GameSession session)
        {
            bool show = race.Phase == RacePhase.Results;
            _results.SetActive(show);
            if (!show) return;
            if (session.Paused) session.SetPaused(false);

            int myPlace = _standings.IndexOf(car) + 1;
            _resultsHeadline.text = !car.Finished ? "Did not finish" : myPlace == 1 ? "YOU WIN!" : $"You finished {Ui.Ordinal(myPlace)}";
            _resultsHeadline.color = myPlace == 1 && car.Finished ? Palette.Accent : Palette.Text;
            _resultsSub.text = $"Race {race.RaceNumber} · {RaceDirector.Laps} laps · points add up while you stay in the room";

            for (int i = 0; i < RaceDirector.CarCount; i++)
            {
                bool used = i < _standings.Count;
                _resultChips[i].enabled = used;
                if (!used)
                {
                    for (int column = 0; column < ResultColumns; column++) _resultCells[i, column].text = "";
                    continue;
                }
                NetCar c = _standings[i];
                Color colour = c == car ? Palette.Accent : Palette.Text;
                _resultChips[i].color = c.Color;
                SetCell(i, 0, $"{i + 1}", colour);
                SetCell(i, 1, c.DisplayName, colour);
                SetCell(i, 2, c.Finished ? Ui.RaceClock(c.FinishTime) : "DNF", colour);
                SetCell(i, 3, c.BestLap > 0f ? Ui.RaceClock(c.BestLap) : "--", colour);
                SetCell(i, 4, race.Points.Get(c.Slot).ToString(), colour);
            }

            int humans = race.HumanCount;
            int voted = 0;
            foreach (NetCar c in NetCar.All)
                if (!c.IsBot && race.HasVoted(c.Slot)) voted++;
            int seconds = Mathf.CeilToInt(race.SecondsUntilDeadline);
            _resultsStatus.text = humans <= 1
                ? $"Next race in {seconds} s"
                : $"{voted}/{humans} players ready · next race in {seconds} s";

            if (GameInput.ConfirmPressed) session.VoteRestart();
        }

        void SetCell(int row, int column, string text, Color colour)
        {
            Text cell = _resultCells[row, column];
            cell.text = text;
            cell.color = colour;
            cell.fontStyle = colour == Palette.Accent ? FontStyle.Bold : FontStyle.Normal;
        }

        void WatchRoomEvents(GameSession session, RaceDirector race, NetCar car)
        {
            if (race == null || !session.IsOnline) return;
            PlayerRef master = race.Object.StateAuthority;
            if (master != _lastMaster)
            {
                if (_lastMaster != PlayerRef.None)
                    Toast(master == session.Runner.LocalPlayer
                        ? "You are now the master client · your browser runs the bots"
                        : $"Player {master.PlayerId} is now the master client");
                _lastMaster = master;
            }
            if (car != null && _lastCar == null && race.Phase == RacePhase.Racing)
                Toast($"You took over {BotDriver.Roster[car.Slot].Name}'s car mid-race");
        }

        void Toast(string message)
        {
            Text text = Ui.Label(_toastRoot, message, 18, Palette.Text, TextAnchor.MiddleCenter, FontStyle.Normal, shadow: true);
            text.rectTransform.Place(new Vector2(0.5f, 1f), Vector2.zero, new Vector2(600f, 26f));
            _toasts.Add((text, Time.unscaledTime + 4.5f));
        }

        void UpdateToasts()
        {
            for (int i = _toasts.Count - 1; i >= 0; i--)
            {
                (Text text, float until) = _toasts[i];
                float left = until - Time.unscaledTime;
                if (left <= 0f)
                {
                    Destroy(text.gameObject);
                    _toasts.RemoveAt(i);
                    continue;
                }
                Color c = text.color;
                c.a = Mathf.Clamp01(left / 0.6f);
                text.color = c;
            }
            for (int i = 0; i < _toasts.Count; i++)
                _toasts[_toasts.Count - 1 - i].text.rectTransform.anchoredPosition = new Vector2(0f, -i * 26f);
        }

        void UpdateNameTags(NetCar mine)
        {
            bool show = mine != null && !DevView.Enabled && _camera != null;
            foreach (NetCar car in NetCar.All)
            {
                if (!_nameTags.TryGetValue(car, out Text tag))
                {
                    tag = Ui.Label(_tagRoot, "", 15, Palette.Text, TextAnchor.MiddleCenter, FontStyle.Bold, shadow: true);
                    tag.rectTransform.sizeDelta = new Vector2(160f, 22f);
                    _nameTags[car] = tag;
                }
                Vector3 screen = _camera != null ? _camera.WorldToScreenPoint(car.transform.position + Vector3.up * 2.1f) : Vector3.zero;
                bool visible = show && car != mine && screen.z > 0f && screen.z < 120f;
                tag.enabled = visible;
                if (!visible) continue;
                tag.rectTransform.position = screen;
                tag.text = car.DisplayName;
                tag.color = car.IsBot ? new Color(1f, 1f, 1f, 0.75f) : Palette.Accent;
            }
        }

        static string ConnectionLine(GameSession session, bool verbose)
        {
            switch (session.State)
            {
                case GameSession.ConnectionState.Connecting:
                    return verbose ? "Connecting to Photon Cloud…" : "CONNECTING…";
                case GameSession.ConnectionState.Offline:
                    return verbose ? $"Offline · {session.OfflineReason}. You'll race bots locally." : "OFFLINE · BOTS";
                default:
                    NetworkRunner runner = session.Runner;
                    SessionInfo info = runner.SessionInfo;
                    string room = info.Name.Length > 6 ? info.Name.Substring(0, 6).ToUpperInvariant() : info.Name.ToUpperInvariant();
                    int players = info.PlayerCount;
                    if (verbose)
                        return players > 1
                            ? $"Online · {info.Region} · room {room} · {players - 1} other player{(players > 2 ? "s" : "")} here"
                            : $"Online · {info.Region} · room {room} · you're the only player: bots fill the grid";
                    return $"ONLINE · {info.Region.ToUpperInvariant()} · ROOM {room} · {players}/{RaceDirector.CarCount}";
            }
        }

        // ---------- Construction ----------

        void BuildTitle(Transform root)
        {
            Image shade = Ui.Panel("Title", root, new Color(0.03f, 0.035f, 0.045f, 0.62f), rounded: false);
            shade.rectTransform.Stretch();
            shade.raycastTarget = true;
            _title = shade.gameObject;
            Transform t = shade.transform;

            Ui.Label(t, "ARENA RACER", 72, Palette.Text, TextAnchor.LowerLeft, FontStyle.Bold, true)
                .rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(90f, 170f), new Vector2(800f, 90f));
            Ui.Label(t, "Six cars, three laps. Online multiplayer on Photon Fusion 2 (Shared mode).\nBots fill every empty seat, and anyone who opens the page takes over a bot's car.", 20, Palette.Muted, TextAnchor.UpperLeft)
                .rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(92f, 110f), new Vector2(760f, 60f));

            Button race = Ui.Button(t, "RACE", 34, Palette.Accent, Palette.Ink, StartRacing);
            ((RectTransform)race.transform).Place(new Vector2(0f, 0.5f), new Vector2(92f, 10f), new Vector2(280f, 76f));
            Ui.Label(t, "or press Enter", 16, Palette.Muted)
                .rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(392f, 10f), new Vector2(200f, 30f));

            Ui.Label(t,
                    "<b>W S A D</b> or <b>arrow keys</b>  drive (S brakes, then reverses)\n" +
                    "<b>Space</b>  drift (hold through a corner, release for a boost)     <b>R</b>  reset onto track\n" +
                    "<b>Esc</b>  pause     <b>Tab</b>  Dev View (netcode internals)     <b>M</b>  mute     Gamepad works too",
                    17, Palette.Text, TextAnchor.UpperLeft)
                .rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(92f, -60f), new Vector2(900f, 80f));

            _titleStatus = Ui.Label(t, "", 17, Palette.Accent, TextAnchor.UpperLeft);
            _titleStatus.rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(92f, -150f), new Vector2(900f, 30f));
            Ui.Label(t, "Tip: open this page in a second window side by side and you're racing yourself online.", 16, Palette.Muted, TextAnchor.UpperLeft)
                .rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(92f, -182f), new Vector2(900f, 30f));
        }

        void BuildHud(Transform root)
        {
            _hud = Ui.Rect("Race HUD", root).Stretch().gameObject;
            Transform h = _hud.transform;
            _carHud = Ui.Rect("Car HUD", h).Stretch().gameObject;  // only shown once you have a car
            Transform c = _carHud.transform;

            Image info = Ui.Panel("Race Panel", c, Palette.Panel);
            info.rectTransform.Place(new Vector2(0f, 1f), new Vector2(16f, -16f), new Vector2(210f, 150f));
            _position = Ui.Label(info.transform, "", 52, Palette.Text, TextAnchor.UpperLeft, FontStyle.Bold);
            _position.rectTransform.Place(new Vector2(0f, 1f), new Vector2(16f, -6f), new Vector2(190f, 60f));
            _lap = Ui.Label(info.transform, "", 20, Palette.Accent, TextAnchor.UpperLeft, FontStyle.Bold);
            _lap.rectTransform.Place(new Vector2(0f, 1f), new Vector2(18f, -70f), new Vector2(190f, 26f));
            _clock = Ui.Label(info.transform, "", 20, Palette.Text, TextAnchor.UpperLeft);
            _clock.rectTransform.Place(new Vector2(0f, 1f), new Vector2(18f, -96f), new Vector2(190f, 26f));
            _best = Ui.Label(info.transform, "", 15, Palette.Muted, TextAnchor.UpperLeft);
            _best.rectTransform.Place(new Vector2(0f, 1f), new Vector2(18f, -122f), new Vector2(190f, 22f));

            Image board = Ui.Panel("Standings", c, Palette.Panel);
            board.rectTransform.Place(new Vector2(1f, 1f), new Vector2(-16f, -16f), new Vector2(230f, 22f + RaceDirector.CarCount * 26f));
            for (int i = 0; i < RaceDirector.CarCount; i++)
            {
                float y = -12f - i * 26f;
                _rowChips[i] = Ui.Panel("Chip", board.transform, Color.white, rounded: false);
                _rowChips[i].rectTransform.Place(new Vector2(0f, 1f), new Vector2(12f, y - 6f), new Vector2(5f, 14f));
                _rowNames[i] = Ui.Label(board.transform, "", 17, Palette.Text, TextAnchor.MiddleLeft);
                _rowNames[i].rectTransform.Place(new Vector2(0f, 1f), new Vector2(24f, y), new Vector2(150f, 26f));
                _rowInfo[i] = Ui.Label(board.transform, "", 15, Palette.Muted, TextAnchor.MiddleRight);
                _rowInfo[i].rectTransform.Place(new Vector2(1f, 1f), new Vector2(-12f, y), new Vector2(90f, 26f));
            }

            _speed = Ui.Label(c, "0", 54, Palette.Text, TextAnchor.LowerCenter, FontStyle.Bold, true);
            _speed.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(-30f, 52f), new Vector2(200f, 64f));
            Ui.Label(c, "km/h", 18, Palette.Muted, TextAnchor.LowerLeft, FontStyle.Normal, true)
                .rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(82f, 60f), new Vector2(80f, 24f));
            Image track = Ui.Panel("Boost Track", c, new Color(1f, 1f, 1f, 0.12f), rounded: false);
            _boostTrack = track.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0f, 38f), new Vector2(260f, 8f));
            _boostFill = Ui.Panel("Boost Fill", track.transform, Palette.Accent, rounded: false);
            _boostFill.rectTransform.anchorMin = new Vector2(0f, 0f);
            _boostFill.rectTransform.anchorMax = new Vector2(0f, 1f);
            _boostFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            _boostFill.rectTransform.anchoredPosition = Vector2.zero;
            _boostFill.rectTransform.sizeDelta = Vector2.zero;
            _boostLabel = Ui.Label(c, "", 13, Palette.Muted, TextAnchor.MiddleCenter, FontStyle.Bold, true);
            _boostLabel.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(300f, 18f));

            _center = Ui.Label(h, "", 96, Palette.Text, TextAnchor.MiddleCenter, FontStyle.Bold, true);
            _center.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, 120f), new Vector2(900f, 140f));

            _badge = Ui.Label(h, "", 14, Palette.Muted, TextAnchor.MiddleCenter, FontStyle.Bold, true);
            _badge.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0f, -16f), new Vector2(600f, 22f));

            Ui.Label(h, "Space drift · R reset · Esc pause · Tab Dev View · M mute", 14, new Color(1f, 1f, 1f, 0.55f), TextAnchor.LowerLeft, FontStyle.Normal, true)
                .rectTransform.Place(new Vector2(0f, 0f), new Vector2(18f, 14f), new Vector2(520f, 20f));
        }

        void BuildPause(Transform root)
        {
            Image shade = Ui.Panel("Pause", root, Dim, rounded: false);
            shade.rectTransform.Stretch();
            shade.raycastTarget = true;
            _pause = shade.gameObject;

            Image box = Ui.Panel("Pause Box", shade.transform, Palette.Panel);
            box.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(440f, 400f));
            Transform b = box.transform;
            _pauseTitle = Ui.Label(b, "PAUSED", 40, Palette.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
            _pauseTitle.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0f, -24f), new Vector2(400f, 50f));
            _pauseBody = Ui.Label(b, "", 16, Palette.Muted, TextAnchor.UpperCenter);
            _pauseBody.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0f, -80f), new Vector2(380f, 48f));

            float y = -140f;
            Button Add(string text, UnityEngine.Events.UnityAction action, bool primary = false)
            {
                Button button = Ui.Button(b, text, 20, primary ? Palette.Accent : new Color(1f, 1f, 1f, 0.1f), primary ? Palette.Ink : Palette.Text, action);
                ((RectTransform)button.transform).Place(new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(300f, 50f));
                y -= 60f;
                return button;
            }
            Add("Resume", () => SetPaused(false), primary: true);
            _restartButton = Add("Restart race", () =>
            {
                GameSession.Instance.VoteRestart();
                SetPaused(false);
            });
            _devViewButton = Add("Dev View", DevView.Toggle);
            _soundButton = Add("Sound", Sfx.ToggleMute);
            _pause.SetActive(false);
        }

        void BuildResults(Transform root)
        {
            Image box = Ui.Panel("Results", root, Palette.Panel);
            box.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, -10f), new Vector2(700f, 470f));
            box.raycastTarget = true;
            _results = box.gameObject;
            Transform b = box.transform;

            _resultsHeadline = Ui.Label(b, "", 44, Palette.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
            _resultsHeadline.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0f, -20f), new Vector2(660f, 56f));
            _resultsSub = Ui.Label(b, "", 15, Palette.Muted, TextAnchor.MiddleCenter);
            _resultsSub.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0f, -76f), new Vector2(660f, 22f));

            string[] headers = { "", "DRIVER", "TIME", "BEST LAP", "POINTS" };
            for (int column = 0; column < ResultColumns; column++)
            {
                Ui.Label(b, headers[column], 13, Palette.Muted, column == 4 ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft, FontStyle.Bold)
                    .rectTransform.Place(new Vector2(0f, 1f), new Vector2(ColumnX[column], -112f), new Vector2(ColumnWidth[column], 20f));
            }
            for (int i = 0; i < RaceDirector.CarCount; i++)
            {
                float y = -140f - i * 34f;
                _resultChips[i] = Ui.Panel("Chip", b, Color.white, rounded: false);
                _resultChips[i].rectTransform.Place(new Vector2(0f, 1f), new Vector2(ColumnX[1] - 16f, y - 9f), new Vector2(6f, 16f));
                for (int column = 0; column < ResultColumns; column++)
                {
                    _resultCells[i, column] = Ui.Label(b, "", 19, Palette.Text, column == 4 ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft);
                    _resultCells[i, column].rectTransform.Place(new Vector2(0f, 1f), new Vector2(ColumnX[column], y), new Vector2(ColumnWidth[column], 32f));
                }
            }

            Button again = Ui.Button(b, "RACE AGAIN", 24, Palette.Accent, Palette.Ink, () => GameSession.Instance.VoteRestart());
            ((RectTransform)again.transform).Place(new Vector2(0.5f, 0f), new Vector2(0f, 46f), new Vector2(260f, 56f));
            _resultsStatus = Ui.Label(b, "", 14, Palette.Muted, TextAnchor.MiddleCenter);
            _resultsStatus.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(600f, 22f));
            _results.SetActive(false);
        }
    }
}
