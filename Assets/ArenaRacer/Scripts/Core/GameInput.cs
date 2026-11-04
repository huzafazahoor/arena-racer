using UnityEngine;

namespace ArenaRacer
{
    /// <summary>
    /// Keyboard and gamepad (browser "standard" mapping: A=0 B=1 X=2 Y=3 LB=4 RB=5 LT=6 RT=7 Back=8 Start=9).
    /// Uses the legacy Input Manager: it works in WebGL builds and costs nothing in download size.
    /// </summary>
    public static class GameInput
    {
        static bool _resetQueued;

        public static CarInput ReadCar()
        {
            float steer = Input.GetAxisRaw("Horizontal");  // A/D, arrows and the left stick
            if (Mathf.Abs(steer) < 0.15f) steer = 0f;

            bool gas = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)
                       || Input.GetKey(KeyCode.JoystickButton7) || Input.GetKey(KeyCode.JoystickButton0);
            bool brake = Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)
                         || Input.GetKey(KeyCode.JoystickButton6) || Input.GetKey(KeyCode.JoystickButton1);
            bool drift = Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
                         || Input.GetKey(KeyCode.JoystickButton5) || Input.GetKey(KeyCode.JoystickButton2);

            return new CarInput
            {
                Steer = steer,
                Throttle = gas ? 1f : 0f,
                Brake = brake ? 1f : 0f,
                Drift = drift,
            };
        }

        /// <summary>Call once per frame; key presses are latched here and consumed by the next network tick.</summary>
        public static void PollFrame()
        {
            if (Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.JoystickButton3)) _resetQueued = true;
        }

        public static bool ConsumeReset()
        {
            bool queued = _resetQueued;
            _resetQueued = false;
            return queued;
        }

        public static bool PausePressed =>
            Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.P) || Input.GetKeyDown(KeyCode.JoystickButton9);

        public static bool DevViewPressed =>
            Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.F3) || Input.GetKeyDown(KeyCode.JoystickButton8);

        public static bool ConfirmPressed =>
            Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.JoystickButton0);

        public static bool MutePressed => Input.GetKeyDown(KeyCode.M);
    }
}
