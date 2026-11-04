using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ArenaRacer
{
    /// <summary>
    /// Tiny procedural mesh helper. Everything in the game is flat-shaded geometry with vertex colours, so the
    /// whole build needs no textures. Vertex alpha below 1 marks self-lit parts (see ArenaRacer/Lit shader).
    /// </summary>
    public sealed class MeshBuilder
    {
        readonly List<Vector3> _vertices = new();
        readonly List<Vector3> _normals = new();
        readonly List<Color> _colors = new();
        readonly List<int> _triangles = new();

        public int VertexCount => _vertices.Count;

        /// <summary>Adds a quad facing <paramref name="outward"/>, whatever order the corners were given in.</summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward, Color color)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(normal, outward) < 0f)
            {
                (b, d) = (d, b);
                normal = -normal;
            }
            normal.Normalize();

            int i = _vertices.Count;
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            _vertices.Add(d);
            for (int k = 0; k < 4; k++)
            {
                _normals.Add(normal);
                _colors.Add(color.linear.WithAlpha(color.a));
            }
            _triangles.Add(i);
            _triangles.Add(i + 1);
            _triangles.Add(i + 2);
            _triangles.Add(i);
            _triangles.Add(i + 2);
            _triangles.Add(i + 3);
        }

        public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, Color color)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(normal, outward) < 0f)
            {
                (b, c) = (c, b);
                normal = -normal;
            }
            normal.Normalize();

            int i = _vertices.Count;
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            for (int k = 0; k < 3; k++)
            {
                _normals.Add(normal);
                _colors.Add(color.linear.WithAlpha(color.a));
            }
            _triangles.Add(i);
            _triangles.Add(i + 1);
            _triangles.Add(i + 2);
        }

        /// <summary>
        /// A six-sided hull from eight corners: bottom four then top four, each in the order
        /// front-left, front-right, back-right, back-left. Lets you taper or wedge a box.
        /// </summary>
        public void Hull(Vector3[] c, Color color, bool bottom = false)
        {
            Vector3 centre = Vector3.zero;
            foreach (Vector3 p in c) centre += p;
            centre /= 8f;

            void Face(int a, int b, int d, int e)
            {
                Vector3 mid = (c[a] + c[b] + c[d] + c[e]) * 0.25f;
                Quad(c[a], c[b], c[d], c[e], mid - centre, color);
            }

            Face(4, 5, 6, 7);                // top
            Face(0, 1, 5, 4);                // front
            Face(2, 3, 7, 6);                // back
            Face(1, 2, 6, 5);                // right
            Face(3, 0, 4, 7);                // left
            if (bottom) Face(0, 1, 2, 3);
        }

        public void Box(Vector3 centre, Vector3 size, Color color, bool bottom = false)
        {
            Vector3 h = size * 0.5f;
            Hull(new[]
            {
                centre + new Vector3(-h.x, -h.y, h.z), centre + new Vector3(h.x, -h.y, h.z),
                centre + new Vector3(h.x, -h.y, -h.z), centre + new Vector3(-h.x, -h.y, -h.z),
                centre + new Vector3(-h.x, h.y, h.z), centre + new Vector3(h.x, h.y, h.z),
                centre + new Vector3(h.x, h.y, -h.z), centre + new Vector3(-h.x, h.y, -h.z),
            }, color, bottom);
        }

        /// <summary>Box rotated about Y, for scenery placed along the track.</summary>
        public void Box(Vector3 centre, Vector3 size, float yawDegrees, Color color)
        {
            Quaternion r = Quaternion.Euler(0f, yawDegrees, 0f);
            Vector3 h = size * 0.5f;
            Vector3 P(float x, float y, float z) => centre + r * new Vector3(x, y, z);
            Hull(new[]
            {
                P(-h.x, -h.y, h.z), P(h.x, -h.y, h.z), P(h.x, -h.y, -h.z), P(-h.x, -h.y, -h.z),
                P(-h.x, h.y, h.z), P(h.x, h.y, h.z), P(h.x, h.y, -h.z), P(-h.x, h.y, -h.z),
            }, color);
        }

        /// <summary>Cylinder along the local X axis (wheels).</summary>
        public void CylinderX(Vector3 centre, float radius, float width, int segments, Color side, Color cap)
        {
            float hw = width * 0.5f;
            for (int s = 0; s < segments; s++)
            {
                float a0 = s * Mathf.PI * 2f / segments, a1 = (s + 1) * Mathf.PI * 2f / segments;
                Vector3 r0 = new(0f, Mathf.Cos(a0) * radius, Mathf.Sin(a0) * radius);
                Vector3 r1 = new(0f, Mathf.Cos(a1) * radius, Mathf.Sin(a1) * radius);
                Vector3 l = centre + Vector3.left * hw, r = centre + Vector3.right * hw;
                Quad(l + r0, r + r0, r + r1, l + r1, (r0 + r1) * 0.5f, side);
                Triangle(r, r + r0, r + r1, Vector3.right, cap);
                Triangle(l, l + r0, l + r1, Vector3.left, cap);
            }
        }

        /// <summary>Upright cone (trees, flames when rotated).</summary>
        public void Cone(Vector3 baseCentre, float radius, float height, int segments, Color color)
        {
            Vector3 tip = baseCentre + Vector3.up * height;
            for (int s = 0; s < segments; s++)
            {
                float a0 = s * Mathf.PI * 2f / segments, a1 = (s + 1) * Mathf.PI * 2f / segments;
                Vector3 p0 = baseCentre + new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) * radius;
                Vector3 p1 = baseCentre + new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1)) * radius;
                Vector3 outward = ((p0 + p1) * 0.5f - baseCentre).normalized + Vector3.up * (radius / height);
                Triangle(p0, p1, tip, outward, color);
            }
        }

        public Mesh ToMesh(string name)
        {
            var mesh = new Mesh
            {
                name = name,
                indexFormat = _vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            mesh.SetVertices(_vertices);
            mesh.SetNormals(_normals);
            mesh.SetColors(_colors);
            mesh.SetTriangles(_triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    static class ColorExtensions
    {
        public static Color WithAlpha(this Color c, float a) => new(c.r, c.g, c.b, a);
    }
}
