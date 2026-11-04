using UnityEngine;

namespace ArenaRacer
{
    public static class Palette
    {
        public static readonly Color Accent = Hex(0xF5B841);
        public static readonly Color Ink = Hex(0x0F1013);
        public static readonly Color Panel = new(0.06f, 0.065f, 0.08f, 0.86f);
        public static readonly Color Text = Hex(0xECEEF2);
        public static readonly Color Muted = Hex(0x9AA0AB);

        // Dev View ghosts. Car colours below deliberately avoid these hues.
        public static readonly Color Snapshot = Hex(0x38E1FF);     // last state received from the network
        public static readonly Color Predicted = Hex(0x5CFF8A);    // extrapolated to "now"
        public static readonly Color OthersSee = Hex(0xFF4FD8);    // where other players see your car

        static readonly Color[] Cars =
        {
            Hex(0xF5B841), // amber
            Hex(0xE5484D), // red
            Hex(0x3D7BF2), // blue
            Hex(0x9B6CF0), // violet
            Hex(0xE8E8EC), // white
            Hex(0x50545E), // graphite
        };

        public static Color Car(int slot) => Cars[((slot % Cars.Length) + Cars.Length) % Cars.Length];

        public static Color Hex(uint rgb) =>
            new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

        public static string ToRichText(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);
    }
}
