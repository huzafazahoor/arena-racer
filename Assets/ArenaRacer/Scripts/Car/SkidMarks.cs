using UnityEngine;

namespace ArenaRacer
{
    /// <summary>All tyre marks in one ring-buffer mesh: one draw call, oldest marks overwritten first.</summary>
    public sealed class SkidMarks : MonoBehaviour
    {
        const int MaxQuads = 1600;
        const float MinSegment = 0.45f;

        public struct Trail
        {
            public bool Active;
            public Vector3 Left, Right, Centre;
        }

        static SkidMarks _instance;

        readonly Vector3[] _vertices = new Vector3[MaxQuads * 4];
        readonly Color32[] _colors = new Color32[MaxQuads * 4];
        Mesh _mesh;
        int _next;
        bool _dirty;

        void Awake()
        {
            _instance = this;
            var triangles = new int[MaxQuads * 6];
            for (int q = 0; q < MaxQuads; q++)
            {
                int v = q * 4, t = q * 6;
                triangles[t] = v;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }
            _mesh = new Mesh { name = "Skid Marks" };
            _mesh.MarkDynamic();
            _mesh.vertices = _vertices;
            _mesh.colors32 = _colors;
            _mesh.triangles = triangles;
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(1000f, 10f, 1000f));

            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = GameAssets.Instance.SkidMark;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        public static void Extend(ref Trail trail, Vector3 position, Vector3 right, float width, byte alpha)
        {
            position.y = 0.035f;
            if (trail.Active && (position - trail.Centre).sqrMagnitude < MinSegment * MinSegment) return;

            Vector3 left = position - right * (width * 0.5f), rightEdge = position + right * (width * 0.5f);
            if (trail.Active && _instance != null) _instance.AddQuad(trail.Left, left, rightEdge, trail.Right, alpha);
            trail.Active = true;
            trail.Left = left;
            trail.Right = rightEdge;
            trail.Centre = position;
        }

        public static void Break(ref Trail trail) => trail.Active = false;

        void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, byte alpha)
        {
            int v = _next * 4;
            _vertices[v] = a;
            _vertices[v + 1] = b;
            _vertices[v + 2] = c;
            _vertices[v + 3] = d;
            var colour = new Color32(18, 18, 20, alpha);
            for (int k = 0; k < 4; k++) _colors[v + k] = colour;
            _next = (_next + 1) % MaxQuads;
            _dirty = true;
        }

        void LateUpdate()
        {
            if (!_dirty) return;
            _mesh.vertices = _vertices;
            _mesh.colors32 = _colors;
            _dirty = false;
        }
    }
}
