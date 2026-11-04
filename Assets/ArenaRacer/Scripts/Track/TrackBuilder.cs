using UnityEngine;
using UnityEngine.Rendering;

namespace ArenaRacer
{
    /// <summary>
    /// Builds the whole arena from <see cref="ArenaCircuit"/> at startup: asphalt with a rubbered-in racing line,
    /// kerbs on the corners, barriers, the start gantry and grid, grass, grandstands, light towers and trees.
    /// No art assets: everything is vertex-coloured geometry, which keeps the WebGL download small.
    /// </summary>
    public sealed class TrackBuilder : MonoBehaviour
    {
        [SerializeField] Material litMaterial;

        static readonly Color Asphalt = Palette.Hex(0x34363D);
        static readonly Color AsphaltRubber = Palette.Hex(0x2A2B30);
        static readonly Color Line = Palette.Hex(0xE6E7EA);
        static readonly Color KerbRed = Palette.Hex(0xD8433F);
        static readonly Color KerbWhite = Palette.Hex(0xF0F0F0);
        static readonly Color Apron = Palette.Hex(0x5B6B5E);
        static readonly Color GrassA = Palette.Hex(0x3F6E4B);
        static readonly Color GrassB = Palette.Hex(0x467853);
        static readonly Color WallDark = Palette.Hex(0x24262C);
        static readonly Color WallTop = Palette.Hex(0xD9DADF);
        static readonly Color Concrete = Palette.Hex(0x8E929A);

        const int RoadStrips = 7;

        TrackPath _track;

        void Awake()
        {
            _track = ArenaCircuit.Path;
            Spawn("Road", BuildRoad(), castShadows: false);
            Spawn("Barriers", BuildBarriers(), castShadows: true);
            Spawn("Ground", BuildGround(), castShadows: false);
            Spawn("Stadium", BuildStadium(), castShadows: true);
        }

        void Spawn(string objectName, Mesh mesh, bool castShadows)
        {
            var go = new GameObject(objectName);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = litMaterial;
            renderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            renderer.receiveShadows = true;
        }

        Vector3 At(int i, float lateral, float y = 0f)
        {
            Vector2 p = _track.Points[_track.Wrap(i)] + _track.Normals[_track.Wrap(i)] * lateral;
            return new Vector3(p.x, y, p.y);
        }

        Mesh BuildRoad()
        {
            var mb = new MeshBuilder();
            RacingLine line = ArenaCircuit.RacingLine;
            int n = _track.Count;
            float hw = _track.HalfWidth;
            float stripWidth = 2f * hw / RoadStrips;

            for (int i = 0; i < n; i++)
            {
                // Asphalt in lateral strips so the racing line can be darker ("rubbered in") and the surface patchy.
                for (int s = 0; s < RoadStrips; s++)
                {
                    float l0 = -hw + s * stripWidth, l1 = l0 + stripWidth;
                    float centre = (l0 + l1) * 0.5f;
                    float rubber = Mathf.Clamp01(1f - Mathf.Abs(centre - line.Offset[i]) / 2.6f);
                    float patch = Mathf.PerlinNoise(i * 0.05f, s * 0.7f) * 0.08f - 0.04f;
                    Color c = Color.Lerp(Asphalt, AsphaltRubber, rubber * 0.85f);
                    c = new Color(c.r + patch, c.g + patch, c.b + patch);
                    mb.Quad(At(i, l0), At(i + 1, l0), At(i + 1, l1), At(i, l1), Vector3.up, c);
                }

                // White edge lines.
                for (int side = -1; side <= 1; side += 2)
                {
                    float a = side * (hw - 0.45f), b = side * (hw - 0.2f);
                    mb.Quad(At(i, a, 0.01f), At(i + 1, a, 0.01f), At(i + 1, b, 0.01f), At(i, b, 0.01f), Vector3.up, Line);
                }

                // Run-off apron out to the barriers, with red/white kerbs over it in the corners.
                bool corner = IsCorner(i);
                for (int side = -1; side <= 1; side += 2)
                {
                    float edge = side * hw, wall = side * ArenaCircuit.WallInner;
                    mb.Quad(At(i, edge, 0.005f), At(i + 1, edge, 0.005f), At(i + 1, wall, 0.005f), At(i, wall, 0.005f), Vector3.up, Apron);
                    if (!corner) continue;
                    float k0 = side * (hw - 0.25f), k1 = side * (hw + 1.1f);
                    Color kerb = (i / 2) % 2 == 0 ? KerbRed : KerbWhite;
                    mb.Quad(At(i, k0, 0.03f), At(i + 1, k0, 0.03f), At(i + 1, k1, 0.03f), At(i, k1, 0.03f), Vector3.up, kerb);
                }
            }

            AddStartLineAndGrid(mb);
            return mb.ToMesh("Road");
        }

        bool IsCorner(int i)
        {
            for (int k = -8; k <= 8; k++)
                if (Mathf.Abs(_track.Curvature[_track.Wrap(i + k)]) > 1f / 55f) return true;
            return false;
        }

        void AddStartLineAndGrid(MeshBuilder mb)
        {
            float startAlong = ArenaCircuit.StartLine;
            int start = _track.IndexAt(startAlong);
            float hw = _track.HalfWidth;

            // Chequered line: two rows of 1 m squares.
            for (int row = 0; row < 2; row++)
            for (int col = 0; col < 14; col++)
            {
                float l0 = -hw + col, l1 = l0 + 1f;
                Color c = (row + col) % 2 == 0 ? Color.white : Palette.Hex(0x15161A);
                Vector3 a = At(start + row, l0, 0.02f), b = At(start + row + 1, l0, 0.02f);
                Vector3 d = At(start + row, l1, 0.02f), e = At(start + row + 1, l1, 0.02f);
                mb.Quad(a, b, e, d, Vector3.up, c);
            }

            // Grid boxes: a white bracket per car.
            for (int box = 0; box < RaceDirector.CarCount; box++)
            {
                ArenaCircuit.GridBox g = ArenaCircuit.Grid(box);
                Vector2 f = CarPhysics.Forward(g.Heading), r = CarPhysics.Right(g.Heading);
                Vector3 P(float side, float along) =>
                    new(g.Position.x + r.x * side + f.x * along, 0.02f, g.Position.y + r.y * side + f.y * along);
                mb.Quad(P(-1.3f, 2.6f), P(1.3f, 2.6f), P(1.3f, 2.35f), P(-1.3f, 2.35f), Vector3.up, Line);
                mb.Quad(P(-1.3f, 2.6f), P(-1.05f, 2.6f), P(-1.05f, 0.8f), P(-1.3f, 0.8f), Vector3.up, Line);
                mb.Quad(P(1.05f, 2.6f), P(1.3f, 2.6f), P(1.3f, 0.8f), P(1.05f, 0.8f), Vector3.up, Line);
            }

            // Gantry over the line.
            Vector2 tangent = _track.Tangents[start];
            float yaw = Mathf.Atan2(tangent.x, tangent.y) * Mathf.Rad2Deg;
            Vector3 left = At(start, ArenaCircuit.WallOffset + 0.6f), right = At(start, -ArenaCircuit.WallOffset - 0.6f);
            mb.Box(left + Vector3.up * 3.5f, new Vector3(0.6f, 7f, 0.6f), yaw, Concrete);
            mb.Box(right + Vector3.up * 3.5f, new Vector3(0.6f, 7f, 0.6f), yaw, Concrete);
            Vector3 mid = (left + right) * 0.5f;
            float span = Vector3.Distance(left, right);
            mb.Box(mid + Vector3.up * 7.3f, new Vector3(span + 0.6f, 1.2f, 0.8f), yaw, Palette.Hex(0x1C1D22));
            mb.Box(mid + Vector3.up * 7.3f + Vector3.down * 0.01f, new Vector3(span * 0.5f, 0.5f, 0.85f), yaw, Palette.Accent.WithAlpha(0.35f));
        }

        Mesh BuildBarriers()
        {
            var mb = new MeshBuilder();
            int n = _track.Count;
            const float Height = 0.9f;
            float inner = ArenaCircuit.WallInner, outer = ArenaCircuit.WallOffset + ArenaCircuit.WallThickness * 0.5f;

            for (int i = 0; i < n; i++)
            {
                Color face = (i / 4) % 2 == 0 ? Palette.Accent : WallDark;
                for (int side = -1; side <= 1; side += 2)
                {
                    float a = side * inner, b = side * outer;
                    Vector3 inward = new Vector3(-_track.Normals[i].x, 0f, -_track.Normals[i].y) * side;
                    mb.Quad(At(i, a), At(i + 1, a), At(i + 1, a, Height), At(i, a, Height), inward, face);
                    mb.Quad(At(i, a, Height), At(i + 1, a, Height), At(i + 1, b, Height), At(i, b, Height), Vector3.up, WallTop);
                    mb.Quad(At(i, b), At(i + 1, b), At(i + 1, b, Height), At(i, b, Height), -inward, WallDark);
                }
            }
            return mb.ToMesh("Barriers");
        }

        Mesh BuildGround()
        {
            var mb = new MeshBuilder();
            // Mowed stripes, like a stadium pitch.
            const float MinX = -380f, MaxX = 350f, MinZ = -260f, MaxZ = 250f, Stripe = 14f;
            int index = 0;
            for (float x = MinX; x < MaxX; x += Stripe, index++)
            {
                Color c = index % 2 == 0 ? GrassA : GrassB;
                mb.Quad(new Vector3(x, -0.02f, MinZ), new Vector3(x, -0.02f, MaxZ),
                    new Vector3(x + Stripe, -0.02f, MaxZ), new Vector3(x + Stripe, -0.02f, MinZ), Vector3.up, c);
            }
            return mb.ToMesh("Ground");
        }

        Mesh BuildStadium()
        {
            var mb = new MeshBuilder();
            var random = new System.Random(7);

            // Main grandstand along the outside of the start/finish straight, and one by the sweeper.
            Grandstand(mb, random, new Vector3(-45f, 0f, -92f), 150f, 0f);
            Grandstand(mb, random, new Vector3(-78f, 0f, 90f), 90f, 180f);
            Grandstand(mb, random, new Vector3(140f, 0f, -20f), 80f, 270f);

            // Outer stadium wall with an advertising band.
            const float X0 = -200f, X1 = 170f, Z0 = -120f, Z1 = 120f;
            OuterWall(mb, new Vector3(X0, 0, Z0), new Vector3(X1, 0, Z0));
            OuterWall(mb, new Vector3(X1, 0, Z0), new Vector3(X1, 0, Z1));
            OuterWall(mb, new Vector3(X1, 0, Z1), new Vector3(X0, 0, Z1));
            OuterWall(mb, new Vector3(X0, 0, Z1), new Vector3(X0, 0, Z0));

            // Floodlight towers.
            foreach (Vector3 p in new[] { new Vector3(-185, 0, -105), new Vector3(155, 0, -105), new Vector3(155, 0, 105), new Vector3(-185, 0, 105), new Vector3(-15, 0, 110) })
            {
                mb.Box(p + Vector3.up * 14f, new Vector3(1.2f, 28f, 1.2f), 0f, Concrete);
                mb.Box(p + Vector3.up * 28.5f, new Vector3(7f, 3f, 1.2f), 0f, WallDark);
                mb.Box(p + Vector3.up * 28.5f + new Vector3(0f, 0f, 0f), new Vector3(6.4f, 2.4f, 1.3f), 0f, new Color(1f, 0.96f, 0.85f, 0f));
            }

            // Trees wherever there is room (at least 14 m from the centre line).
            int planted = 0;
            for (int attempt = 0; attempt < 900 && planted < 140; attempt++)
            {
                var p = new Vector2((float)(random.NextDouble() * 350 - 190), (float)(random.NextDouble() * 225 - 110));
                if (Mathf.Abs(_track.Locate(p).Lateral) < 15f) continue;
                if (InGrandstandArea(p)) continue;
                float scale = 0.8f + (float)random.NextDouble() * 0.7f;
                Color leaves = Color.Lerp(Palette.Hex(0x2E5A3A), Palette.Hex(0x4C7F45), (float)random.NextDouble());
                Vector3 b = new(p.x, 0f, p.y);
                mb.Box(b + Vector3.up * 0.9f * scale, new Vector3(0.5f, 1.8f, 0.5f) * scale, 0f, Palette.Hex(0x5A4636));
                mb.Cone(b + Vector3.up * 1.4f * scale, 2.2f * scale, 5.5f * scale, 7, leaves);
                planted++;
            }

            return mb.ToMesh("Stadium");
        }

        static bool InGrandstandArea(Vector2 p) =>
            (p.y < -82f && p.x > -125f && p.x < 35f) || (p.y > 80f && p.x > -125f && p.x < -30f) || (p.x > 128f);

        static void Grandstand(MeshBuilder mb, System.Random random, Vector3 front, float length, float yaw)
        {
            Quaternion r = Quaternion.Euler(0f, yaw, 0f);
            Vector3 along = r * Vector3.right, back = r * Vector3.back;
            const int Steps = 6;
            for (int s = 0; s < Steps; s++)
            {
                Vector3 centre = front + back * (s * 2.4f + 1.2f) + Vector3.up * (s * 1.1f + 0.55f);
                mb.Box(centre, new Vector3(length, 1.1f + s * 2.2f, 2.4f), yaw, Palette.Hex(0x3A3D46));
                // The crowd: blocks of colour on each step.
                for (float x = -length * 0.5f + 1f; x < length * 0.5f - 1f; x += 1.6f)
                {
                    if (random.NextDouble() < 0.18) continue;
                    Color shirt = Color.HSVToRGB((float)random.NextDouble(), 0.45f, 0.85f);
                    Vector3 seat = front + along * x + back * (s * 2.4f + 1.2f);
                    seat.y = s * 2.2f + 1.45f;  // sitting on top of step s
                    mb.Box(seat, new Vector3(0.9f, 0.7f, 0.6f), yaw, shirt);
                }
            }
            // Roof on posts.
            Vector3 roof = front + back * 7f + Vector3.up * 15f;
            mb.Box(roof, new Vector3(length + 4f, 1.4f, 16f), yaw, Palette.Hex(0xD9DADF));
            mb.Box(roof + back * -8.05f, new Vector3(length + 4.2f, 1.6f, 0.3f), yaw, Palette.Accent);  // fascia along the front edge
            for (float x = -length * 0.5f; x <= length * 0.5f; x += length / 4f)
                mb.Box(front + along * x + back * 14.5f + Vector3.up * 7.5f, new Vector3(0.6f, 15f, 0.6f), yaw, Concrete);
        }

        static void OuterWall(MeshBuilder mb, Vector3 a, Vector3 b)
        {
            Vector3 mid = (a + b) * 0.5f;
            Vector3 dir = b - a;
            float yaw = Mathf.Atan2(-dir.z, dir.x) * Mathf.Rad2Deg;  // box length runs along local X
            float length = dir.magnitude;
            mb.Box(mid + Vector3.up * 4f, new Vector3(length, 8f, 1.5f), yaw, Palette.Hex(0x2A2C33));
            mb.Box(mid + Vector3.up * 5.5f, new Vector3(length, 1.4f, 1.6f), yaw, Palette.Accent);
        }
    }
}
