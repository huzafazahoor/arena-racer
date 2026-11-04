using UnityEngine;

namespace ArenaRacer
{
    /// <summary>Low-poly car parts built in code. Local space: +Z forward, +X right, origin on the ground.</summary>
    public static class CarMeshFactory
    {
        public const float WheelRadius = 0.38f;
        public static readonly Vector3[] WheelPositions =
        {
            new(-0.88f, WheelRadius, 1.3f), new(0.88f, WheelRadius, 1.3f),   // front left, front right
            new(-0.88f, WheelRadius, -1.3f), new(0.88f, WheelRadius, -1.3f), // rear left, rear right
        };

        static readonly Color Glass = Palette.Hex(0x1C222C);
        static readonly Color Trim = Palette.Hex(0x1A1B1F);
        static readonly Color HeadLamp = new(1f, 0.95f, 0.8f, 0f);   // alpha 0 = self-lit
        static readonly Color TailLamp = new(0.75f, 0.08f, 0.08f, 0.5f);

        static Mesh _wheel, _ghost, _brakeLights, _flame;

        public static Mesh Body(Color paint)
        {
            var mb = new MeshBuilder();

            // Wedge-shaped chassis: low nose, higher tail.
            mb.Hull(new[]
            {
                new Vector3(-0.95f, 0.28f, 2.05f), new Vector3(0.95f, 0.28f, 2.05f),
                new Vector3(0.95f, 0.28f, -2.05f), new Vector3(-0.95f, 0.28f, -2.05f),
                new Vector3(-0.9f, 0.62f, 1.75f), new Vector3(0.9f, 0.62f, 1.75f),
                new Vector3(0.92f, 0.82f, -2.0f), new Vector3(-0.92f, 0.82f, -2.0f),
            }, paint, bottom: true);

            // Cabin with a raked windscreen, painted roof panel on top.
            mb.Hull(new[]
            {
                new Vector3(-0.75f, 0.66f, 0.7f), new Vector3(0.75f, 0.66f, 0.7f),
                new Vector3(0.78f, 0.79f, -1.35f), new Vector3(-0.78f, 0.79f, -1.35f),
                new Vector3(-0.6f, 1.2f, 0.0f), new Vector3(0.6f, 1.2f, 0.0f),
                new Vector3(0.62f, 1.22f, -1.05f), new Vector3(-0.62f, 1.22f, -1.05f),
            }, Glass);
            mb.Box(new Vector3(0f, 1.24f, -0.52f), new Vector3(1.14f, 0.05f, 0.95f), paint);

            // Rear wing.
            mb.Box(new Vector3(0f, 1.12f, -1.85f), new Vector3(1.9f, 0.07f, 0.45f), Trim);
            mb.Box(new Vector3(-0.6f, 0.97f, -1.85f), new Vector3(0.08f, 0.3f, 0.2f), Trim);
            mb.Box(new Vector3(0.6f, 0.97f, -1.85f), new Vector3(0.08f, 0.3f, 0.2f), Trim);

            // Bumpers, side skirts, lamps.
            mb.Box(new Vector3(0f, 0.32f, 2.06f), new Vector3(1.8f, 0.14f, 0.12f), Trim);
            mb.Box(new Vector3(0f, 0.36f, -2.06f), new Vector3(1.8f, 0.16f, 0.1f), Trim);
            mb.Box(new Vector3(-0.97f, 0.36f, 0f), new Vector3(0.06f, 0.16f, 3.2f), Trim);
            mb.Box(new Vector3(0.97f, 0.36f, 0f), new Vector3(0.06f, 0.16f, 3.2f), Trim);
            mb.Box(new Vector3(-0.6f, 0.5f, 1.96f), new Vector3(0.42f, 0.1f, 0.14f), HeadLamp);
            mb.Box(new Vector3(0.6f, 0.5f, 1.96f), new Vector3(0.42f, 0.1f, 0.14f), HeadLamp);
            mb.Box(new Vector3(-0.62f, 0.66f, -2.03f), new Vector3(0.45f, 0.12f, 0.06f), TailLamp);
            mb.Box(new Vector3(0.62f, 0.66f, -2.03f), new Vector3(0.45f, 0.12f, 0.06f), TailLamp);

            return mb.ToMesh("Car Body");
        }

        public static Mesh Wheel()
        {
            if (_wheel != null) return _wheel;
            var mb = new MeshBuilder();
            mb.CylinderX(Vector3.zero, WheelRadius, 0.3f, 12, Palette.Hex(0x17181B), Palette.Hex(0x9EA3AD));
            return _wheel = mb.ToMesh("Wheel");
        }

        public static Mesh BrakeLights()
        {
            if (_brakeLights != null) return _brakeLights;
            var mb = new MeshBuilder();
            var bright = new Color(1f, 0.12f, 0.1f, 0f);
            mb.Box(new Vector3(-0.62f, 0.66f, -2.055f), new Vector3(0.48f, 0.14f, 0.03f), bright);
            mb.Box(new Vector3(0.62f, 0.66f, -2.055f), new Vector3(0.48f, 0.14f, 0.03f), bright);
            return _brakeLights = mb.ToMesh("Brake Lights");
        }

        /// <summary>Exhaust flame pointing up the local Y axis; rotate the object to point it backwards.</summary>
        public static Mesh Flame()
        {
            if (_flame != null) return _flame;
            var mb = new MeshBuilder();
            mb.Cone(Vector3.zero, 0.16f, 1f, 8, Color.white);
            return _flame = mb.ToMesh("Flame");
        }

        /// <summary>Simplified silhouette for Dev View ghosts.</summary>
        public static Mesh Ghost()
        {
            if (_ghost != null) return _ghost;
            var mb = new MeshBuilder();
            mb.Hull(new[]
            {
                new Vector3(-0.95f, 0.25f, 2.05f), new Vector3(0.95f, 0.25f, 2.05f),
                new Vector3(0.95f, 0.25f, -2.05f), new Vector3(-0.95f, 0.25f, -2.05f),
                new Vector3(-0.75f, 1.15f, 0.6f), new Vector3(0.75f, 1.15f, 0.6f),
                new Vector3(0.8f, 1.15f, -1.7f), new Vector3(-0.8f, 1.15f, -1.7f),
            }, Color.white, bottom: true);
            return _ghost = mb.ToMesh("Ghost");
        }
    }
}
