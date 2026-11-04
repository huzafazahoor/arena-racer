using UnityEngine;

namespace ArenaRacer
{
    /// <summary>
    /// The one circuit in the game: a 763 m loop with a long main straight, a hairpin, a set of esses and a
    /// fast sweeper. Every client builds the identical path from these numbers, so nothing about the track
    /// ever needs to go over the network.
    /// </summary>
    public static class ArenaCircuit
    {
        public const float HalfWidth = 7f;        // asphalt is 14 m wide
        public const float WallOffset = 8.6f;     // centre of the barrier, metres from the centre line
        public const float WallThickness = 0.6f;
        public const float WallInner = WallOffset - WallThickness * 0.5f;
        public const float StartLine = 70f;       // metres along the centre line from the first control point

        const float GridFirstRow = 7f;            // metres behind the line
        const float GridSpacing = 5.5f;           // metres between consecutive grid boxes (staggered)
        const float GridLateral = 3f;

        static readonly Vector2[] ControlPoints =
        {
            new(-120, -70), new(-40, -70), new(40, -70), new(88, -66), new(108, -46), new(114, -12),
            new(112, 18), new(102, 40), new(86, 48), new(71, 38), new(66, 14), new(54, -8),
            new(32, -18), new(8, -6), new(-14, 6), new(-30, 36), new(-66, 62), new(-110, 58),
            new(-138, 24), new(-142, -30), new(-134, -60),
        };

        static TrackPath _path;
        static RacingLine _racingLine;

        public static TrackPath Path => _path ??= new TrackPath(ControlPoints, HalfWidth);

        public static RacingLine RacingLine => _racingLine ??= new RacingLine(Path, edgeMargin: 1.8f);

        /// <summary>Converts an absolute distance along the centre line into race distance (0 = the start line).</summary>
        public static float ToRaceDistance(float trackDistance) => Path.WrapDistance(trackDistance - StartLine);

        public static float ToTrackDistance(float raceDistance) => Path.WrapDistance(raceDistance + StartLine);

        public struct GridBox
        {
            public Vector2 Position;
            public float Heading;
            public float Progress;   // negative: behind the start line
        }

        /// <summary>Two staggered columns behind the start line. Index 0 is pole position.</summary>
        public static GridBox Grid(int index)
        {
            float progress = -(GridFirstRow + index * GridSpacing);
            float along = ToTrackDistance(progress);
            float side = index % 2 == 0 ? GridLateral : -GridLateral;
            return new GridBox
            {
                Position = Path.PointAt(along) + Path.NormalAt(along) * side,
                Heading = Path.HeadingAt(along),
                Progress = progress,
            };
        }
    }
}
