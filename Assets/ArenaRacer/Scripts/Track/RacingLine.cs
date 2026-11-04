using UnityEngine;

namespace ArenaRacer
{
    /// <summary>
    /// A smooth, apex-cutting line round the circuit for the bots, and the fastest speed a car can carry at
    /// every point of it. Computed once at startup; bots then only do array lookups.
    /// </summary>
    public sealed class RacingLine
    {
        public readonly float[] Offset;     // lateral offset from the centre line, per track sample
        public readonly float[] Curvature;  // 1/radius of the racing line itself (unsigned)
        public readonly float[] MaxSpeed;   // m/s: corner limit, lowered so there is room to brake for what comes next

        const float BotBraking = 17f;       // m/s², gentler than the car can do, so bots never overshoot
        const float CornerMargin = 1.12f;   // bots aim for a radius 12% wider than the car's tightest at that speed

        public RacingLine(TrackPath track, float edgeMargin)
        {
            int n = track.Count;
            Offset = new float[n];
            Curvature = new float[n];
            MaxSpeed = new float[n];

            var line = (Vector2[])track.Points.Clone();
            Relax(track, line, track.HalfWidth - edgeMargin);

            for (int i = 0; i < n; i++)
                Offset[i] = Vector2.Dot(line[i] - track.Points[i], track.Normals[i]);

            for (int i = 0; i < n; i++)
            {
                Vector2 a = line[track.Wrap(i - 3)], b = line[i], c = line[track.Wrap(i + 3)];
                Curvature[i] = CircleCurvature(a, b, c);
                MaxSpeed[i] = CornerSpeed(Curvature[i]);
            }

            // Walk backwards (twice round, so the lap seam is covered) lowering each speed to what still lets the
            // car brake in time for the next sample: v² = v_next² + 2·a·d.
            for (int pass = 0; pass < 2 * n; pass++)
            {
                int i = track.Wrap(-pass);
                int next = track.Wrap(i + 1);
                float d = Vector2.Distance(line[i], line[next]);
                MaxSpeed[i] = Mathf.Min(MaxSpeed[i], Mathf.Sqrt(MaxSpeed[next] * MaxSpeed[next] + 2f * BotBraking * d));
            }
        }

        /// <summary>
        /// "Elastic band": pull every point towards the midpoint of its neighbours while keeping it inside the
        /// track. That straightens the path as much as the walls allow, which naturally gives the classic
        /// outside-apex-outside line through each corner.
        /// </summary>
        static void Relax(TrackPath track, Vector2[] line, float limit)
        {
            const int Reach = 6;
            const int Iterations = 400;
            int n = line.Length;
            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                for (int i = 0; i < n; i++)
                {
                    Vector2 mid = (line[track.Wrap(i - Reach)] + line[track.Wrap(i + Reach)]) * 0.5f;
                    Vector2 moved = Vector2.Lerp(line[i], mid, 0.5f);
                    float lateral = Mathf.Clamp(Vector2.Dot(moved - track.Points[i], track.Normals[i]), -limit, limit);
                    line[i] = track.Points[i] + track.Normals[i] * lateral;
                }
            }
        }

        static float CircleCurvature(Vector2 a, Vector2 b, Vector2 c)
        {
            float ab = Vector2.Distance(a, b), bc = Vector2.Distance(b, c), ca = Vector2.Distance(c, a);
            float cross = Mathf.Abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
            float denominator = ab * bc * ca;
            return denominator < 1e-5f ? 0f : 2f * cross / denominator;
        }

        /// <summary>Highest speed at which the car's own steering can follow a curve this tight.</summary>
        static float CornerSpeed(float curvature)
        {
            if (curvature < 1e-4f) return CarPhysics.TopSpeed;
            float radius = 1f / curvature;
            for (float v = CarPhysics.TopSpeed; v > 4f; v -= 0.25f)
            {
                if (CarPhysics.TightestRadius(v) * CornerMargin <= radius)
                    return v;
            }
            return 4f;
        }
    }
}
