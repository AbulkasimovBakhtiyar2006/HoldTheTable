using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// A horizontal, up-facing play surface (MRUK plane or procedural fallback table). Owner: Agent A.
    /// Pose.position = center on top of the surface, Pose.up = surface normal.
    /// Height = world-space Y of the top. Size = extents along the surface's local X (right) and Z (forward).
    /// </summary>
    public interface ITableSurface
    {
        string Id { get; }
        bool IsFallback { get; }
        Pose Pose { get; }
        float Height { get; }
        Vector2 Size { get; }
        /// <summary>True if the world point lies over this surface's real outline (holes/gaps excluded) and near its plane.</summary>
        bool IsPointSupported(Vector3 w);
        /// <summary>Center + 8 points on the circle of the given radius must all be supported.</summary>
        bool IsFootprintSupported(Vector3 center, float radius);
        /// <summary>Closest point on the outline (edge) of the surface, on its plane.</summary>
        Vector3 ClosestEdgePoint(Vector3 w);
        /// <summary>Orthogonal projection of a world point onto the surface plane.</summary>
        Vector3 ProjectOnto(Vector3 w);
    }

    /// <summary>Shared polygon helpers for surface implementations.</summary>
    public static class SurfaceMath
    {
        static readonly Vector2[] circle = BuildCircle();

        static Vector2[] BuildCircle()
        {
            var c = new Vector2[8];
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI * 0.25f;
                c[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            }
            return c;
        }

        /// <summary>Generic footprint test: center + 8 circle samples in the surface plane must be supported.</summary>
        public static bool FootprintSupported(ITableSurface s, Vector3 center, float radius)
        {
            if (!s.IsPointSupported(center)) return false;
            var pose = s.Pose;
            Vector3 right = pose.rotation * Vector3.right;
            Vector3 fwd = pose.rotation * Vector3.forward;
            for (int i = 0; i < 8; i++)
            {
                var p = center + (right * circle[i].x + fwd * circle[i].y) * radius;
                if (!s.IsPointSupported(p)) return false;
            }
            return true;
        }

        /// <summary>Even-odd point-in-polygon test (polygon may be concave).</summary>
        public static bool PointInPolygon(Vector2 p, System.Collections.Generic.List<Vector2> poly)
        {
            int n = poly.Count;
            if (n < 3) return false;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = poly[i], b = poly[j];
                if (((a.y > p.y) != (b.y > p.y)) && (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y + 1e-12f) + a.x))
                    inside = !inside;
            }
            return inside;
        }

        public static Vector2 ClosestOnPolygon(Vector2 p, System.Collections.Generic.List<Vector2> poly)
        {
            int n = poly.Count;
            if (n == 0) return p;
            if (n == 1) return poly[0];
            float best = float.MaxValue;
            Vector2 bestP = poly[0];
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = poly[j], b = poly[i];
                Vector2 ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-9f, ab.sqrMagnitude));
                Vector2 q = a + ab * t;
                float d = (q - p).sqrMagnitude;
                if (d < best) { best = d; bestP = q; }
            }
            return bestP;
        }
    }
}
