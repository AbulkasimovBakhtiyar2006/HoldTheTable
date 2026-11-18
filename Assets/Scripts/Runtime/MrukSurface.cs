#if HTT_MRUK
using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// ITableSurface over an MRUK anchor (Agent A). Plane anchors: local XY plane, normal = anchor +Z (forward).
    /// The real boundary polygon (PlaneBoundary2D) is used for support tests, so holes/notches of the scanned
    /// outline are excluded. Volume-only furniture uses the top face of its VolumeBounds.
    /// </summary>
    public sealed class MrukSurface : ITableSurface
    {
        public readonly MRUKAnchor anchor;
        readonly List<Vector2> poly = new List<Vector2>();
        readonly float planeZ;
        readonly Rect rect;
        const float PlaneTolerance = 0.06f;

        public string Id { get; }
        public bool IsFallback => false;
        public MRUKAnchor.SceneLabels Label => anchor != null ? anchor.Label : 0;

        MrukSurface(MRUKAnchor a, string id, Rect r, float z, List<Vector2> polygon)
        {
            anchor = a;
            Id = id;
            rect = r;
            planeZ = z;
            poly.AddRange(polygon);
        }

        public static string IdFor(MRUKAnchor a)
        {
            var uuid = a.Anchor.Uuid;
            return uuid != System.Guid.Empty ? "mruk_" + uuid.ToString("N") : "mruk_h" + a.GetHashCode();
        }

        /// <summary>Builds a surface if the anchor is a horizontal, up-facing, large-enough walkable plane/top.</summary>
        public static MrukSurface TryCreate(MRUKAnchor a, float minSize)
        {
            if (a == null) return null;
            const MRUKAnchor.SceneLabels excluded = MRUKAnchor.SceneLabels.CEILING | MRUKAnchor.SceneLabels.WALL_FACE |
                                                   MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE | MRUKAnchor.SceneLabels.INNER_WALL_FACE |
                                                   MRUKAnchor.SceneLabels.DOOR_FRAME | MRUKAnchor.SceneLabels.WINDOW_FRAME |
                                                   MRUKAnchor.SceneLabels.WALL_ART | MRUKAnchor.SceneLabels.GLOBAL_MESH |
                                                   MRUKAnchor.SceneLabels.SCREEN | MRUKAnchor.SceneLabels.LAMP | MRUKAnchor.SceneLabels.PLANT;
            if ((a.Label & excluded) != 0) return null;
            if (Vector3.Dot(a.transform.forward, Vector3.up) < 0.9f) return null; // must face up

            var polygon = new List<Vector2>();
            if (a.PlaneRect.HasValue)
            {
                var r = a.PlaneRect.Value;
                if (Mathf.Min(r.width, r.height) < minSize) return null;
                if (a.PlaneBoundary2D != null && a.PlaneBoundary2D.Count >= 3) polygon.AddRange(a.PlaneBoundary2D);
                else
                {
                    polygon.Add(new Vector2(r.xMin, r.yMin));
                    polygon.Add(new Vector2(r.xMax, r.yMin));
                    polygon.Add(new Vector2(r.xMax, r.yMax));
                    polygon.Add(new Vector2(r.xMin, r.yMax));
                }
                return new MrukSurface(a, IdFor(a), r, 0f, polygon);
            }
            if (a.VolumeBounds.HasValue)
            {
                var b = a.VolumeBounds.Value;
                if (Mathf.Min(b.size.x, b.size.y) < minSize) return null;
                var r = new Rect(b.min.x, b.min.y, b.size.x, b.size.y);
                polygon.Add(new Vector2(r.xMin, r.yMin));
                polygon.Add(new Vector2(r.xMax, r.yMin));
                polygon.Add(new Vector2(r.xMax, r.yMax));
                polygon.Add(new Vector2(r.xMin, r.yMax));
                return new MrukSurface(a, IdFor(a), r, b.max.z, polygon);
            }
            return null;
        }

        /// <summary>World AABB of the anchor's volume (crude obstacle), if it has one.</summary>
        public static bool TryGetWorldVolume(MRUKAnchor a, out Bounds world)
        {
            world = default;
            if (a == null || !a.VolumeBounds.HasValue) return false;
            if (a.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.CEILING | MRUKAnchor.SceneLabels.GLOBAL_MESH |
                              MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE | MRUKAnchor.SceneLabels.INNER_WALL_FACE))
                return false;
            var b = a.VolumeBounds.Value;
            var t = a.transform;
            bool first = true;
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                var w = t.TransformPoint(c);
                if (first) { world = new Bounds(w, Vector3.zero); first = false; }
                else world.Encapsulate(w);
            }
            return true;
        }

        Transform T => anchor.transform;

        public Pose Pose
        {
            get
            {
                var t = T;
                Vector3 n = t.forward;
                Vector3 c = t.TransformPoint(new Vector3(rect.center.x, rect.center.y, planeZ));
                Vector3 f = Vector3.ProjectOnPlane(t.up, n);
                if (f.sqrMagnitude < 1e-6f) f = Vector3.ProjectOnPlane(Vector3.forward, n);
                return new Pose(c, Quaternion.LookRotation(f.normalized, n));
            }
        }

        public float Height => T.TransformPoint(new Vector3(rect.center.x, rect.center.y, planeZ)).y;

        /// <summary>X = width along anchor local X, Y = depth along anchor local Y (maps to Pose right/forward).</summary>
        public Vector2 Size => rect.size;

        public bool IsPointSupported(Vector3 w)
        {
            if (anchor == null) return false;
            var l = T.InverseTransformPoint(w);
            if (Mathf.Abs(l.z - planeZ) > PlaneTolerance) return false;
            return SurfaceMath.PointInPolygon(new Vector2(l.x, l.y), poly);
        }

        public bool IsFootprintSupported(Vector3 center, float radius) => SurfaceMath.FootprintSupported(this, center, radius);

        public Vector3 ClosestEdgePoint(Vector3 w)
        {
            var l = T.InverseTransformPoint(w);
            var q = SurfaceMath.ClosestOnPolygon(new Vector2(l.x, l.y), poly);
            return T.TransformPoint(new Vector3(q.x, q.y, planeZ));
        }

        public Vector3 ProjectOnto(Vector3 w)
        {
            var l = T.InverseTransformPoint(w);
            l.z = planeZ;
            return T.TransformPoint(l);
        }
    }
}
#endif
