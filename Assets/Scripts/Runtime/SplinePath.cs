using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>Type of the path edge a monster is on (picks speed, orientation and surface snapping).</summary>
    public enum PathSegment { Floor, Climb, Top, Descend, Rope, Ladder, Surface }

    /// <summary>
    /// Typed polyline/Catmull-Rom path with an arc-length table. Owner: Agent B.
    /// Build(): single wiggly Catmull-Rom across one ITableSurface (all Surface).
    /// BuildRoute(): obstacle-aware room route (floor → around/over furniture → climb/rope/ladder → hall surface),
    /// produced by ClimbRoutes. Only Surface edges are snapped to / support-checked against the final surface.
    /// Each point stores the type and "up" (belly-to-wall normal) of the edge that ENDS at that point.
    /// </summary>
    public sealed class SplinePath
    {
        const int SurfaceSamples = 64;

        readonly List<Vector3> pts = new List<Vector3>(160);
        readonly List<Vector3> ups = new List<Vector3>(160);
        readonly List<float> dist = new List<float>(160);
        readonly List<PathSegment> types = new List<PathSegment>(160);
        Vector3[] control = new Vector3[0];

        /// <summary>The final surface (the one the hall stands on).</summary>
        public ITableSurface Surface { get; private set; }
        public float Length { get; private set; }
        public Vector3 Start => pts[0];
        public Vector3 End => pts[pts.Count - 1];
        public Vector3[] ControlPoints => control;
        /// <summary>Arc-length where the final-surface leg begins (Length if the path has none, e.g. engineer to ladder foot).</summary>
        public float SurfaceStartDistance { get; private set; }
        /// <summary>Start of the climb/rope/ladder run that leads onto the final surface (0 if none).</summary>
        public float ClimbStartDistance { get; private set; }
        /// <summary>Horizontal direction away from the climbed edge.</summary>
        public Vector3 ClimbOutward { get; set; } = Vector3.forward;
        public bool HasFloorPart => types.Count > 1 && types[1] != PathSegment.Surface;
        /// <summary>True when the hall itself stands on the room floor.</summary>
        public bool SurfaceIsFloor { get; set; }
        /// <summary>Rope or ladder this path uses to reach the final surface (null = bare climb or none).</summary>
        public ClimbAccess Access { get; set; }
        /// <summary>True when the path ends with a bare climb onto the final surface (first climber sets a rope there).</summary>
        public bool BareClimb { get; set; }
        public Vector3 BareClimbBottom { get; set; }
        public Vector3 BareClimbApproach { get; set; }
        public Vector3 BareClimbTop { get; set; }
        public int PointCount => pts.Count;

        public SplinePath(ITableSurface finalSurface)
        {
            Surface = finalSurface;
        }

        // ------------------------------------------------------------ factories
        public static SplinePath Build(Vector3 from, Vector3 to, ITableSurface s)
        {
            var path = new SplinePath(s);
            path.Add(s != null ? s.ProjectOnto(from) : from, PathSegment.Surface, Vector3.up);
            path.AddSurfaceLeg(to);
            return path;
        }

        /// <summary>Room-corner route (obstacle-aware, uses ropes/ladders). Falls back to Build without ClimbRoutes.</summary>
        public static SplinePath BuildRoute(Vector3 corner, Vector3 hall, ITableSurface hallSurface, float floorY)
        {
            var cr = ClimbRoutes.Instance;
            if (cr != null) return cr.BuildRoute(corner, hall, hallSurface, floorY);
            return Build(corner, hall, hallSurface);
        }

        // ------------------------------------------------------------ builder
        /// <summary>Appends a point; the edge from the previous point gets this type and up vector.</summary>
        public void Add(Vector3 p, PathSegment type, Vector3 up)
        {
            float d = pts.Count == 0 ? 0f : dist[dist.Count - 1] + Vector3.Distance(pts[pts.Count - 1], p);
            pts.Add(p);
            ups.Add(up);
            types.Add(type);
            dist.Add(d);
            Length = Mathf.Max(1e-4f, d);
            if (type != PathSegment.Surface) SurfaceStartDistance = Length;
        }

        public void MarkClimbStart() => ClimbStartDistance = Length;

        /// <summary>Wiggly Catmull-Rom leg on the final surface from the current end to `to`.</summary>
        public void AddSurfaceLeg(Vector3 to)
        {
            var s = Surface;
            Vector3 from = pts.Count > 0 ? pts[pts.Count - 1] : to;
            SurfaceStartDistance = pts.Count > 0 ? dist[dist.Count - 1] : 0f;
            var cps = SurfaceControlPoints(from, to, s);
            control = cps;
            Vector3 up = s != null ? s.Pose.rotation * Vector3.up : Vector3.up;
            int segs = cps.Length - 1;
            for (int i = 1; i <= SurfaceSamples; i++)
            {
                float u = i / (float)SurfaceSamples * segs;
                int seg = Mathf.Min(segs - 1, Mathf.FloorToInt(u));
                float lt = u - seg;
                Vector3 p = CatmullRom(cps[Mathf.Max(0, seg - 1)], cps[seg], cps[seg + 1], cps[Mathf.Min(cps.Length - 1, seg + 2)], lt);
                if (s != null) p = s.ProjectOnto(p);
                float d = dist[dist.Count - 1] + Vector3.Distance(pts[pts.Count - 1], p);
                pts.Add(p); ups.Add(up); types.Add(PathSegment.Surface); dist.Add(d);
                Length = Mathf.Max(1e-4f, d);
            }
        }

        /// <summary>Retypes the trailing run of Floor edges to Surface (hall on the floor).</summary>
        public void RetypeFloorTailAsSurface()
        {
            int i = types.Count - 1;
            while (i >= 1 && types[i] == PathSegment.Floor) { types[i] = PathSegment.Surface; i--; }
            SurfaceStartDistance = dist[Mathf.Max(0, i)];
        }

        static Vector3[] SurfaceControlPoints(Vector3 from, Vector3 to, ITableSurface s)
        {
            Vector3 up = s != null ? s.Pose.rotation * Vector3.up : Vector3.up;
            if (s != null) { from = s.ProjectOnto(from); to = s.ProjectOnto(to); }
            Vector3 d = to - from;
            d -= Vector3.Dot(d, up) * up;
            float len = d.magnitude;
            Vector3 fwd = len > 1e-4f ? d / len : Vector3.forward;
            Vector3 lateral = Vector3.Cross(up, fwd).normalized;

            int mids = len > 1.5f ? 5 : len > 0.6f ? 4 : (len > 0.3f ? 3 : 2);
            var cps = new Vector3[mids + 2];
            cps[0] = from;
            cps[cps.Length - 1] = to;
            float amp = Mathf.Min(len > 1f ? 0.2f : 0.08f, len * 0.15f);
            float sign = Random.value < 0.5f ? -1f : 1f;
            for (int i = 1; i <= mids; i++)
            {
                float t = i / (float)(mids + 1);
                Vector3 onLine = Vector3.Lerp(from, to, t);
                float taper = Mathf.Sin(t * Mathf.PI);
                Vector3 p = onLine + lateral * (sign * amp * taper * Random.Range(0.5f, 1f));
                sign = -sign;
                if (s != null)
                {
                    p = s.ProjectOnto(p);
                    int guard = 0;
                    while (!s.IsPointSupported(p) && guard < 5)
                    {
                        guard++;
                        p = s.ProjectOnto(Vector3.Lerp(p, onLine, guard / 5f));
                    }
                }
                cps[i] = p;
            }
            return cps;
        }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        // ------------------------------------------------------------ queries
        int IndexAt(float d)
        {
            int last = pts.Count - 1;
            if (last <= 0) return 0;
            if (d <= 0f) return 1;
            if (d >= Length) return last;
            int lo = 0, hi = last;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (dist[mid] < d) lo = mid; else hi = mid;
            }
            return hi;
        }

        public PathSegment SegmentAt(float d) => types[IndexAt(d)];
        public Vector3 UpAt(float d) => ups[IndexAt(d)];

        /// <summary>Distance where the contiguous run of the segment type at d starts.</summary>
        public float RunStart(float d)
        {
            int i = IndexAt(d);
            var t = types[i];
            while (i > 1 && types[i - 1] == t) i--;
            return dist[i - 1];
        }

        /// <summary>Distance where the contiguous run of the segment type at d ends.</summary>
        public float RunEnd(float d)
        {
            int i = IndexAt(d);
            var t = types[i];
            while (i < types.Count - 1 && types[i + 1] == t) i++;
            return dist[i];
        }

        /// <summary>Point at normalized arc-length t (0 = spawn, 1 = end).</summary>
        public Vector3 Evaluate(float t) => EvaluateDistance(Mathf.Clamp01(t) * Length);

        public Vector3 EvaluateDistance(float d)
        {
            int last = pts.Count - 1;
            if (last <= 0) return pts[0];
            if (d <= 0f) return pts[0];
            if (d >= Length) return pts[last];
            int hi = IndexAt(d), lo = hi - 1;
            float span = dist[hi] - dist[lo];
            float f = span > 1e-6f ? (d - dist[lo]) / span : 0f;
            return Vector3.LerpUnclamped(pts[lo], pts[hi], f);
        }

        public Vector3 TangentAtDistance(float d)
        {
            Vector3 a = EvaluateDistance(d - 0.01f), b = EvaluateDistance(d + 0.01f);
            Vector3 v = b - a;
            return v.sqrMagnitude > 1e-10f ? v.normalized : Vector3.forward;
        }

        /// <summary>Normalized t of the closest path point.</summary>
        public float Project(Vector3 w) => ProjectDistance(w) / Length;

        /// <summary>Arc-length distance of the closest path point.</summary>
        public float ProjectDistance(Vector3 w) => ProjectDistance(w, 0f, float.MaxValue);

        /// <summary>Arc-length distance of the closest path point restricted to [minD, maxD].</summary>
        public float ProjectDistance(Vector3 w, float minD, float maxD)
        {
            float best = float.MaxValue, bestD = Mathf.Clamp(0f, minD, maxD);
            for (int i = 0; i < pts.Count - 1; i++)
            {
                if (dist[i + 1] < minD || dist[i] > maxD) continue;
                Vector3 a = pts[i], ab = pts[i + 1] - a;
                float l2 = ab.sqrMagnitude;
                float f = l2 > 1e-10f ? Mathf.Clamp01(Vector3.Dot(w - a, ab) / l2) : 0f;
                float sq = (a + ab * f - w).sqrMagnitude;
                if (sq < best) { best = sq; bestD = Mathf.Clamp(Mathf.Lerp(dist[i], dist[i + 1], f), minD, maxD); }
            }
            return bestD;
        }
    }
}
