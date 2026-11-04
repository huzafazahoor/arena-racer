using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ArenaRacer
{
    /// <summary>
    /// An immediate-mode drawing surface for uGUI: call Clear(), add lines and boxes in 0..1 coordinates, then
    /// Apply(). Used for the Dev View's ping graph and interpolation timeline.
    /// </summary>
    public sealed class UiPlot : MaskableGraphic
    {
        struct Shape
        {
            public Vector2 A, B;
            public float Thickness;
            public Color Color;
            public bool IsBox;
        }

        readonly List<Shape> _shapes = new();

        public void Clear() => _shapes.Clear();

        public void Line(Vector2 a, Vector2 b, float thickness, Color color) =>
            _shapes.Add(new Shape { A = a, B = b, Thickness = thickness, Color = color });

        public void Box(Vector2 min, Vector2 max, Color color) =>
            _shapes.Add(new Shape { A = min, B = max, Color = color, IsBox = true });

        public void Apply() => SetVerticesDirty();

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            Vector2 ToLocal(Vector2 p) => new(r.xMin + p.x * r.width, r.yMin + p.y * r.height);

            foreach (Shape s in _shapes)
            {
                if (s.IsBox)
                {
                    Vector2 a = ToLocal(s.A), b = ToLocal(s.B);
                    AddQuad(vh, new Vector2(a.x, a.y), new Vector2(a.x, b.y), new Vector2(b.x, b.y), new Vector2(b.x, a.y), s.Color);
                }
                else
                {
                    Vector2 a = ToLocal(s.A), b = ToLocal(s.B);
                    Vector2 dir = (b - a).normalized;
                    Vector2 n = new Vector2(-dir.y, dir.x) * (s.Thickness * 0.5f);
                    AddQuad(vh, a - n, a + n, b + n, b - n, s.Color);
                }
            }
        }

        static void AddQuad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
        {
            int i = vh.currentVertCount;
            vh.AddVert(a, color, Vector2.zero);
            vh.AddVert(b, color, Vector2.zero);
            vh.AddVert(c, color, Vector2.zero);
            vh.AddVert(d, color, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i, i + 2, i + 3);
        }
    }
}
