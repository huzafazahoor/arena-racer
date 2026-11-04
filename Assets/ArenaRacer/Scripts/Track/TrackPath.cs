using System.Collections.Generic;
using UnityEngine;

namespace ArenaRacer
{
    /// <summary>
    /// The circuit's centre line: a closed loop of evenly spaced samples (about 1 m apart) on the XZ plane.
    /// Walls, lap progress, bots and respawning all ask this class "where on the track is this point?".
    /// Coordinates are (x, z) packed into a Vector2; "left" is counter-clockwise seen from above.
    /// </summary>
    public sealed class TrackPath
    {
        public readonly Vector2[] Points;
        public readonly Vector2[] Tangents;  // unit direction of travel
        public readonly Vector2[] Normals;   // unit vector to the left of the direction of travel
        public readonly float[] Curvature;   // signed 1/radius, positive = left turn
        public readonly float Length;
        public readonly float Spacing;
        public readonly float HalfWidth;

        /// <summary>How far (in samples) a hinted search looks either side of the hint. Cars move &lt; 1 m per tick.</summary>
        const int SearchWindow = 12;

        public int Count => Points.Length;

        public struct Location
        {
            public int Index;        // nearest sample
            public float Distance;   // metres along the centre line, [0, Length)
            public float Lateral;    // metres to the left (+) or right (-) of the centre line
            public Vector2 Tangent;
            public Vector2 Normal;
        }

        public TrackPath(IReadOnlyList<Vector2> controlPoints, float halfWidth, float spacing = 1f)
        {
            HalfWidth = halfWidth;
            Points = Resample(SampleSpline(controlPoints), spacing, out Length);

            int n = Points.Length;
            Spacing = Length / n;
            Tangents = new Vector2[n];
            Normals = new Vector2[n];
            Curvature = new float[n];

            for (int i = 0; i < n; i++)
            {
                Vector2 t = (Points[Wrap(i + 1)] - Points[Wrap(i - 1)]).normalized;
                Tangents[i] = t;
                Normals[i] = new Vector2(-t.y, t.x);
            }
            for (int i = 0; i < n; i++)
            {
                float turn = Vector2.SignedAngle(Tangents[Wrap(i - 2)], Tangents[Wrap(i + 2)]) * Mathf.Deg2Rad;
                Curvature[i] = turn / (4f * Spacing);
            }
        }

        public int Wrap(int index) => ((index % Count) + Count) % Count;

        public float WrapDistance(float distance) => Mathf.Repeat(distance, Length);

        public int IndexAt(float distance) => Wrap(Mathf.RoundToInt(WrapDistance(distance) / Spacing));

        public Vector2 PointAt(float distance)
        {
            float f = WrapDistance(distance) / Spacing;
            int a = Wrap(Mathf.FloorToInt(f));
            return Vector2.Lerp(Points[a], Points[Wrap(a + 1)], f - Mathf.Floor(f));
        }

        public Vector2 TangentAt(float distance) => Tangents[IndexAt(distance)];

        public Vector2 NormalAt(float distance) => Normals[IndexAt(distance)];

        /// <summary>Heading (radians, 0 = +Z, clockwise positive) of the direction of travel.</summary>
        public float HeadingAt(float distance)
        {
            Vector2 t = TangentAt(distance);
            return Mathf.Atan2(t.x, t.y);
        }

        /// <summary>
        /// Finds where <paramref name="position"/> is relative to the centre line. Pass the previous result's
        /// index as <paramref name="hint"/> to search only nearby samples: it's cheaper, and it can never snap
        /// onto a different stretch of track that happens to be close (the hairpin, for example).
        /// </summary>
        public Location Locate(Vector2 position, int hint = -1)
        {
            int best = 0;
            float bestSq = float.MaxValue;
            int from = hint < 0 ? 0 : hint - SearchWindow;
            int to = hint < 0 ? Count - 1 : hint + SearchWindow;
            for (int k = from; k <= to; k++)
            {
                int i = Wrap(k);
                float sq = (Points[i] - position).sqrMagnitude;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = i;
                }
            }

            // Refine on the segment either before or after the nearest sample.
            int a = Vector2.Dot(position - Points[best], Tangents[best]) >= 0f ? best : Wrap(best - 1);
            int b = Wrap(a + 1);
            Vector2 segment = Points[b] - Points[a];
            float f = Mathf.Clamp01(Vector2.Dot(position - Points[a], segment) / segment.sqrMagnitude);
            Vector2 tangent = Vector2.Lerp(Tangents[a], Tangents[b], f).normalized;
            Vector2 normal = new Vector2(-tangent.y, tangent.x);

            return new Location
            {
                Index = best,
                Distance = WrapDistance((a + f) * Spacing),
                Lateral = Vector2.Dot(position - (Points[a] + segment * f), normal),
                Tangent = tangent,
                Normal = normal,
            };
        }

        static List<Vector2> SampleSpline(IReadOnlyList<Vector2> p)
        {
            var result = new List<Vector2>();
            int n = p.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = p[(i - 1 + n) % n], p1 = p[i], p2 = p[(i + 1) % n], p3 = p[(i + 2) % n];
                int steps = Mathf.Max(4, Mathf.CeilToInt(Vector2.Distance(p1, p2) / 0.5f));
                for (int s = 0; s < steps; s++)
                    result.Add(CatmullRom(p0, p1, p2, p3, s / (float)steps));
            }
            return result;
        }

        /// <summary>Centripetal Catmull-Rom (alpha = 0.5): no cusps or loops even with uneven control-point spacing.</summary>
        static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float t0 = 0f;
            float t1 = t0 + Mathf.Sqrt(Vector2.Distance(p0, p1));
            float t2 = t1 + Mathf.Sqrt(Vector2.Distance(p1, p2));
            float t3 = t2 + Mathf.Sqrt(Vector2.Distance(p2, p3));
            float u = Mathf.Lerp(t1, t2, t);

            Vector2 a1 = Blend(p0, p1, t0, t1, u), a2 = Blend(p1, p2, t1, t2, u), a3 = Blend(p2, p3, t2, t3, u);
            Vector2 b1 = Blend(a1, a2, t0, t2, u), b2 = Blend(a2, a3, t1, t3, u);
            return Blend(b1, b2, t1, t2, u);
        }

        static Vector2 Blend(Vector2 a, Vector2 b, float ta, float tb, float u) =>
            tb - ta < 1e-6f ? a : (a * (tb - u) + b * (u - ta)) / (tb - ta);

        static Vector2[] Resample(List<Vector2> dense, float spacing, out float length)
        {
            int m = dense.Count;
            var cumulative = new float[m + 1];
            for (int i = 1; i <= m; i++)
                cumulative[i] = cumulative[i - 1] + Vector2.Distance(dense[i - 1], dense[i % m]);
            length = cumulative[m];

            int count = Mathf.Max(8, Mathf.RoundToInt(length / spacing));
            var result = new Vector2[count];
            int j = 0;
            for (int k = 0; k < count; k++)
            {
                float d = k * length / count;
                while (cumulative[j + 1] < d) j++;
                float f = (d - cumulative[j]) / Mathf.Max(1e-6f, cumulative[j + 1] - cumulative[j]);
                result[k] = Vector2.Lerp(dense[j], dense[(j + 1) % m], f);
            }
            return result;
        }
    }
}
