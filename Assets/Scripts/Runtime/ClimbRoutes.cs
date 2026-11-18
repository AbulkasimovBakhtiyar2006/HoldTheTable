using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>A rope or engineer ladder leading from the floor onto a raised surface edge.</summary>
    public sealed class ClimbAccess
    {
        public bool isLadder;
        public string surfaceId;
        public Vector3 bottom;   // on the floor, at the foot of the rope/ladder
        public Vector3 approach; // free floor point (outside obstacle padding) from which monsters step to `bottom`
        public Vector3 top;      // on the surface edge, at surface height
        public Vector3 outward;  // horizontal, away from the surface
        public bool complete;
        public float progress;
        public GameObject visual;
        /// <summary>Harpooner firing position on the floor (rope sites only).</summary>
        public Vector3 shootPos;
        /// <summary>True while a harpoon rope is reserved but not yet deployed.</summary>
        public bool pending;
        internal float deadline;
        internal Transform grow;
        internal readonly List<GameObject> rungs = new List<GameObject>();
        internal readonly List<float> rungT = new List<float>();
        internal float swayPhase;
        public float ClimbLength => Vector3.Distance(bottom, top);
    }

    /// <summary>
    /// Terrain-aware enemy routes in the scanned room: floor legs go around furniture (or climb over it when a detour
    /// is &gt; 1.6x longer), then reach the hall's raised surface by bare climb, rope (set by the first climber) or an
    /// engineer ladder. Owns ropes/ladders (visuals, shockwave destruction, reset). Self-creating singleton. Owner: Agent B.
    /// </summary>
    public class ClimbRoutes : MonoBehaviour
    {
        public const float ObstaclePad = 0.04f;
        public const float DetourRatio = 1.6f;
        public const int MaxRopesPerWave = 8;
        public const int MaxRopesTotal = 12;
        public const int MaxLadders = 2;
        public const float LadderBuildSeconds = 6f;
        const float FloorSpeed = 0.10f, ClimbSpeed = 0.05f, RopeSpeed = 0.12f, LadderSpeed = 0.09f;

        static ClimbRoutes instance;
        static bool quitting;
        /// <summary>Existing instance without auto-creation (null if none).</summary>
        public static ClimbRoutes Existing => instance;
        public static ClimbRoutes Instance
        {
            get
            {
                if (instance == null && !quitting && Application.isPlaying)
                    instance = new GameObject("ClimbRoutes").AddComponent<ClimbRoutes>();
                return instance;
            }
        }

        /// <summary>(Strings key text, world position) — RopeSet, EngineerLadder, LadderBuilt, ClimbAccessDestroyed.</summary>
        public static event Action<string, Vector3> Announcement;

        struct Obs
        {
            public string id;
            public float minX, maxX, minZ, maxZ, topY;
            public bool noClimb;
        }

        struct ToppleAnim
        {
            public GameObject visual;
            public bool isLadder;
            public Vector3 fallVel;
            public Vector3 rotAxis;
            public Quaternion startRot;
            public float elapsed;
            public float duration;
        }

        readonly List<Obs> obs = new List<Obs>(32);
        readonly List<int> visited = new List<int>(16);
        readonly List<ClimbAccess> accesses = new List<ClimbAccess>();
        readonly List<ToppleAnim> toppling = new List<ToppleAnim>(8);
        readonly List<ClimbAccess> pendingRopes = new List<ClimbAccess>(4);
        int ropesThisWave;
        Transform visualRoot;

        // last route context (used to reroute monsters when an access is destroyed)
        Vector3 lastHall;
        ITableSurface lastSurface;
        float lastFloorY;
        bool hasContext;

        public IReadOnlyList<ClimbAccess> Accesses => accesses;
        /// <summary>Reserved harpoon ropes not yet deployed.</summary>
        public int PendingRopeCount => pendingRopes.Count;
        public int RopeCount { get { int n = 0; for (int i = 0; i < accesses.Count; i++) if (!accesses[i].isLadder) n++; return n; } }
        public int LadderCount { get { int n = 0; for (int i = 0; i < accesses.Count; i++) if (accesses[i].isLadder) n++; return n; } }

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(this); return; }
            instance = this;
        }

        void OnApplicationQuit() => quitting = true;
        void OnDestroy() { if (instance == this) instance = null; }

        void Update()
        {
            float t = Time.time;
            // fallback: a reserved harpoon rope whose harpooner died / never arrived is deployed anyway (enemies are never stuck)
            for (int i = pendingRopes.Count - 1; i >= 0; i--)
                if (i < pendingRopes.Count && t >= pendingRopes[i].deadline) DeployRope(pendingRopes[i]);
            for (int i = 0; i < accesses.Count; i++)
            {
                var a = accesses[i];
                if (a.isLadder || a.visual == null) continue;
                a.visual.transform.localRotation = Quaternion.Euler(Mathf.Sin(t * 1.3f + a.swayPhase) * 3f, 0f, Mathf.Cos(t * 0.9f + a.swayPhase) * 3f);
            }

            float dt = Time.deltaTime;
            for (int i = toppling.Count - 1; i >= 0; i--)
            {
                var tp = toppling[i];
                if (tp.visual == null) { toppling.RemoveAt(i); continue; }
                tp.elapsed += dt;
                float u = Mathf.Clamp01(tp.elapsed / Mathf.Max(0.01f, tp.duration));
                if (tp.isLadder)
                {
                    float angle = u * u * 88f;
                    tp.visual.transform.rotation = Quaternion.AngleAxis(angle, tp.rotAxis) * tp.startRot;
                }
                else
                {
                    tp.fallVel += Physics.gravity * dt;
                    tp.visual.transform.position += tp.fallVel * dt;
                    tp.visual.transform.Rotate(tp.rotAxis, 260f * dt, Space.World);
                }
                if (u >= 1f)
                {
                    if (VfxPool.Instance != null) VfxPool.Instance.Burst(tp.visual.transform.position, new Color(0.55f, 0.4f, 0.2f));
                    ProceduralAudio.Fire(Sfx.Thud, tp.visual.transform.position);
                    Destroy(tp.visual);
                    toppling.RemoveAt(i);
                }
                else
                {
                    toppling[i] = tp;
                }
            }
        }

        public void OnWaveStart() => ropesThisWave = 0;

        static bool IsRaised(ITableSurface s, float floorY) => s != null && s.Pose.position.y > floorY + 0.15f;

        // ------------------------------------------------------------ obstacles
        void CollectObstacles(ITableSurface hallSurface, float floorY)
        {
            obs.Clear();
            var reg = SurfaceRegistry.Instance;
            if (reg == null) return;
            string hallId = hallSurface != null ? hallSurface.Id : null;
            for (int i = 0; i < reg.ObstacleCount; i++)
            {
                if (!reg.TryGetObstacle(i, out var id, out var b)) continue;
                if (b.max.y < floorY + 0.03f) continue;           // rugs / floor clutter
                if (b.min.y > floorY + 0.25f) continue;           // wall shelves, things monsters pass under
                AddObs(id, b.min.x, b.max.x, b.min.z, b.max.z, b.max.y, id == hallId || b.max.y - floorY > 1.1f);
            }
            var surfaces = reg.Surfaces;
            for (int i = 0; i < surfaces.Count; i++)
            {
                var s = surfaces[i];
                if (s == null || s.IsFallback || !IsRaised(s, floorY)) continue;
                bool known = false;
                for (int k = 0; k < obs.Count; k++) if (obs[k].id == s.Id) { known = true; break; }
                if (known) continue;
                var pose = s.Pose;
                Vector3 r = pose.rotation * Vector3.right * (s.Size.x * 0.5f);
                Vector3 f = pose.rotation * Vector3.forward * (s.Size.y * 0.5f);
                float ex = Mathf.Abs(r.x) + Mathf.Abs(f.x), ez = Mathf.Abs(r.z) + Mathf.Abs(f.z);
                Vector3 c = pose.position;
                AddObs(s.Id, c.x - ex, c.x + ex, c.z - ez, c.z + ez, c.y, s.Id == hallId || c.y - floorY > 1.1f);
            }
        }

        void AddObs(string id, float minX, float maxX, float minZ, float maxZ, float topY, bool noClimb)
        {
            obs.Add(new Obs
            {
                id = id, minX = minX - ObstaclePad, maxX = maxX + ObstaclePad, minZ = minZ - ObstaclePad, maxZ = maxZ + ObstaclePad,
                topY = topY, noClimb = noClimb,
            });
        }

        static bool Inside(in Obs o, Vector3 p) => p.x > o.minX && p.x < o.maxX && p.z > o.minZ && p.z < o.maxZ;

        /// <summary>2D slab test of segment p→q against the padded rect. Entry/exit params in [0,1].</summary>
        static bool SegHit(in Obs o, Vector3 p, Vector3 q, out float tIn, out float tOut)
        {
            tIn = 0f; tOut = 1f;
            float dx = q.x - p.x, dz = q.z - p.z;
            if (!Slab(p.x, dx, o.minX, o.maxX, ref tIn, ref tOut)) return false;
            if (!Slab(p.z, dz, o.minZ, o.maxZ, ref tIn, ref tOut)) return false;
            return tIn <= tOut;
        }

        static bool Slab(float p, float d, float min, float max, ref float tIn, ref float tOut)
        {
            if (Mathf.Abs(d) < 1e-7f) return p > min && p < max;
            float t1 = (min - p) / d, t2 = (max - p) / d;
            if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
            if (t1 > tIn) tIn = t1;
            if (t2 < tOut) tOut = t2;
            return tIn <= tOut;
        }

        // perimeter parameter of a boundary point (CCW: minZ edge → maxX edge → maxZ edge → minX edge)
        static float PerimParam(in Obs o, Vector3 p)
        {
            float w = o.maxX - o.minX, h = o.maxZ - o.minZ;
            float d0 = Mathf.Abs(p.z - o.minZ), d1 = Mathf.Abs(p.x - o.maxX), d2 = Mathf.Abs(p.z - o.maxZ), d3 = Mathf.Abs(p.x - o.minX);
            float m = Mathf.Min(Mathf.Min(d0, d1), Mathf.Min(d2, d3));
            if (m == d0) return Mathf.Clamp(p.x - o.minX, 0f, w);
            if (m == d1) return w + Mathf.Clamp(p.z - o.minZ, 0f, h);
            if (m == d2) return w + h + Mathf.Clamp(o.maxX - p.x, 0f, w);
            return 2f * w + h + Mathf.Clamp(o.maxZ - p.z, 0f, h);
        }

        static Vector3 Corner(in Obs o, int k, float y, float margin)
        {
            switch (k & 3)
            {
                case 0: return new Vector3(o.minX - margin, y, o.minZ - margin);
                case 1: return new Vector3(o.maxX + margin, y, o.minZ - margin);
                case 2: return new Vector3(o.maxX + margin, y, o.maxZ + margin);
                default: return new Vector3(o.minX - margin, y, o.maxZ + margin);
            }
        }

        static Vector3 FaceNormal(in Obs o, Vector3 p)
        {
            float d0 = Mathf.Abs(p.z - o.minZ), d1 = Mathf.Abs(p.x - o.maxX), d2 = Mathf.Abs(p.z - o.maxZ), d3 = Mathf.Abs(p.x - o.minX);
            float m = Mathf.Min(Mathf.Min(d0, d1), Mathf.Min(d2, d3));
            if (m == d0) return Vector3.back;
            if (m == d1) return Vector3.right;
            if (m == d2) return Vector3.forward;
            return Vector3.left;
        }

        /// <summary>Floor leg from the path's current end to `to`, going around furniture or climbing over it.</summary>
        void FloorLeg(SplinePath path, Vector3 to, float floorY)
        {
            to.y = floorY;
            Vector3 cur = path.End;
            cur.y = floorY;
            visited.Clear();
            for (int guard = 0; guard < 10; guard++)
            {
                int hit = -1;
                float bestIn = 2f, bestOut = 0f;
                for (int i = 0; i < obs.Count; i++)
                {
                    if (visited.Contains(i)) continue;
                    var o = obs[i];
                    if (Inside(o, cur) || Inside(o, to)) continue;
                    if (!SegHit(o, cur, to, out float tIn, out float tOut)) continue;
                    if (tOut - tIn < 1e-3f || tIn <= 1e-4f) continue;
                    if (tIn < bestIn) { bestIn = tIn; bestOut = tOut; hit = i; }
                }
                if (hit < 0) break;
                visited.Add(hit);
                var ob = obs[hit];
                Vector3 entry = Vector3.Lerp(cur, to, bestIn);
                Vector3 exit = Vector3.Lerp(cur, to, bestOut);
                float direct = Vector3.Distance(cur, to);

                float w = ob.maxX - ob.minX, h = ob.maxZ - ob.minZ, per = 2f * (w + h);
                float sIn = PerimParam(ob, entry), sOut = PerimParam(ob, exit);
                float fwdLen = Mathf.Repeat(sOut - sIn, per);
                bool ccw = fwdLen <= per - fwdLen;
                float arc = ccw ? fwdLen : per - fwdLen;
                float detour = Vector3.Distance(cur, entry) + arc + Vector3.Distance(exit, to);

                if (ob.noClimb || detour < DetourRatio * direct)
                {
                    // walk around: visit the rect corners between entry and exit along the shorter perimeter arc
                    cp[0] = 0f; cp[1] = w; cp[2] = w + h; cp[3] = 2f * w + h;
                    // collect corners sorted by their distance along the arc
                    for (int n = 0; n < 4; n++)
                    {
                        float bestRel = float.MaxValue; int bestK = -1;
                        float prevRel = n == 0 ? 0f : lastRel;
                        for (int k = 0; k < 4; k++)
                        {
                            float rel = ccw ? Mathf.Repeat(cp[k] - sIn, per) : Mathf.Repeat(sIn - cp[k], per);
                            if (rel <= prevRel + 1e-4f || rel >= arc - 1e-4f) continue;
                            if (rel < bestRel) { bestRel = rel; bestK = k; }
                        }
                        if (bestK < 0) break;
                        lastRel = bestRel;
                        Vector3 c = Corner(ob, bestK, floorY, 0.02f);
                        path.Add(c, PathSegment.Floor, Vector3.up);
                        cur = c;
                    }
                }
                else
                {
                    // climb over: up the near face, across the top, down the far face
                    Vector3 nIn = FaceNormal(ob, entry), nOut = FaceNormal(ob, exit);
                    float topY = ob.topY + 0.005f;
                    Vector3 wallIn = entry - nIn * (ObstaclePad - 0.01f);   // 1 cm off the real face
                    Vector3 wallOut = exit - nOut * (ObstaclePad - 0.01f);
                    path.Add(entry, PathSegment.Floor, Vector3.up);
                    path.Add(wallIn, PathSegment.Floor, Vector3.up);
                    path.Add(new Vector3(wallIn.x, topY, wallIn.z), PathSegment.Climb, nIn);
                    path.Add(new Vector3(wallOut.x, topY, wallOut.z), PathSegment.Top, Vector3.up);
                    path.Add(wallOut, PathSegment.Descend, nOut);
                    path.Add(exit, PathSegment.Floor, Vector3.up);
                    cur = exit;
                }
            }
            path.Add(to, PathSegment.Floor, Vector3.up);
        }

        float lastRel;
        readonly float[] cp = new float[4];
        static readonly float[] LadderAngles = { 0f, 25f, -25f, 50f, -50f, 80f, -80f };
        static readonly float[] LadderDists = { 0.42f, 0.35f, 0.5f };

        // ------------------------------------------------------------ routes
        void Remember(Vector3 hall, ITableSurface s, float floorY)
        {
            lastHall = hall; lastSurface = s; lastFloorY = floorY; hasContext = true;
        }

        /// <summary>Obstacle-aware route from a floor point to the hall (bare climb / best rope / best ladder).</summary>
        public SplinePath BuildRoute(Vector3 from, Vector3 hall, ITableSurface hallSurface, float floorY)
        {
            Remember(hall, hallSurface, floorY);
            CollectObstacles(hallSurface, floorY);
            var path = new SplinePath(hallSurface);
            path.Add(new Vector3(from.x, floorY, from.z), PathSegment.Floor, Vector3.up);

            if (!IsRaised(hallSurface, floorY))
            {
                FloorLeg(path, hall, floorY);
                path.RetypeFloorTailAsSurface();
                path.SurfaceIsFloor = true;
                return path;
            }

            // bare climb candidate
            Vector3 edge = EdgeToward(hallSurface, from, hall, out Vector3 outward);
            Vector3 baseP = new Vector3(edge.x + outward.x * (ObstaclePad + 0.01f), floorY, edge.z + outward.z * (ObstaclePad + 0.01f));
            float surfY = hallSurface.Pose.position.y;
            float bestCost = FlatDist(from, baseP) / FloorSpeed + (surfY - floorY) / ClimbSpeed;
            ClimbAccess best = null;
            for (int i = 0; i < accesses.Count; i++)
            {
                var a = accesses[i];
                if (!a.complete || a.surfaceId != hallSurface.Id) continue;
                float cost = FlatDist(from, a.approach) / FloorSpeed + a.ClimbLength / (a.isLadder ? LadderSpeed : RopeSpeed);
                if (cost < bestCost) { bestCost = cost; best = a; }
            }

            if (best != null)
            {
                FloorLeg(path, best.approach, floorY);
                path.Add(best.bottom, PathSegment.Floor, Vector3.up);
                path.MarkClimbStart();
                Vector3 up = best.isLadder ? LadderUp(best) : best.outward;
                path.Add(best.top, best.isLadder ? PathSegment.Ladder : PathSegment.Rope, up);
                path.Access = best;
                path.ClimbOutward = best.outward;
                path.Add(hallSurface.ProjectOnto(best.top - best.outward * 0.03f), PathSegment.Surface, Vector3.up);
                path.AddSurfaceLeg(hall);
                return path;
            }

            FloorLeg(path, baseP, floorY);
            Vector3 wallBase = new Vector3(edge.x + outward.x * 0.012f, floorY, edge.z + outward.z * 0.012f);
            path.Add(wallBase, PathSegment.Floor, Vector3.up);
            path.MarkClimbStart();
            Vector3 climbTop = new Vector3(wallBase.x, surfY + 0.004f, wallBase.z);
            path.Add(climbTop, PathSegment.Climb, outward);
            path.BareClimb = true;
            path.BareClimbBottom = wallBase;
            path.BareClimbApproach = baseP;
            path.BareClimbTop = climbTop;
            path.ClimbOutward = outward;
            path.Add(InsidePoint(hallSurface, edge, outward, hall), PathSegment.Surface, Vector3.up);
            path.AddSurfaceLeg(hall);
            return path;
        }

        /// <summary>Route that starts at an access bottom (e.g. engineer at its finished ladder).</summary>
        public SplinePath RouteViaAccess(ClimbAccess a, Vector3 hall, ITableSurface hallSurface, float floorY)
        {
            var path = new SplinePath(hallSurface);
            path.Add(a.approach, PathSegment.Floor, Vector3.up);
            path.Add(a.bottom, PathSegment.Floor, Vector3.up);
            path.MarkClimbStart();
            path.Add(a.top, a.isLadder ? PathSegment.Ladder : PathSegment.Rope, a.isLadder ? LadderUp(a) : a.outward);
            path.Access = a;
            path.ClimbOutward = a.outward;
            path.Add(hallSurface.ProjectOnto(a.top - a.outward * 0.03f), PathSegment.Surface, Vector3.up);
            path.AddSurfaceLeg(hall);
            return path;
        }

        /// <summary>Floor-only route to `to` (engineer walking to its ladder foot).</summary>
        public SplinePath FloorRoute(Vector3 from, Vector3 to, ITableSurface hallSurface, float floorY)
        {
            CollectObstacles(hallSurface, floorY);
            var path = new SplinePath(hallSurface);
            path.Add(new Vector3(from.x, floorY, from.z), PathSegment.Floor, Vector3.up);
            FloorLeg(path, to, floorY);
            return path;
        }

        /// <summary>Re-plans a route from a monster's current position (after its rope/ladder was destroyed).</summary>
        public SplinePath Reroute(Vector3 from)
        {
            if (!hasContext || lastSurface == null) return null;
            return BuildRoute(from, lastHall, lastSurface, lastFloorY);
        }

        static float FlatDist(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }

        static Vector3 LadderUp(ClimbAccess a)
        {
            Vector3 axis = (a.top - a.bottom).normalized;
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, axis);
            return up.sqrMagnitude > 1e-6f ? up.normalized : a.outward;
        }

        static Vector3 EdgeToward(ITableSurface s, Vector3 from, Vector3 hall, out Vector3 outward)
        {
            Vector3 edge = s.ClosestEdgePoint(s.ProjectOnto(from));
            outward = from - edge;
            outward.y = 0f;
            if (outward.sqrMagnitude < 1e-6f) { outward = edge - hall; outward.y = 0f; }
            if (outward.sqrMagnitude < 1e-6f) outward = Vector3.forward;
            outward.Normalize();
            return edge;
        }

        static Vector3 InsidePoint(ITableSurface s, Vector3 edge, Vector3 outward, Vector3 hall)
        {
            Vector3 top = s.ProjectOnto(edge - outward * 0.03f);
            for (int g = 1; g <= 5 && !s.IsPointSupported(top); g++)
                top = s.ProjectOnto(Vector3.Lerp(edge - outward * 0.03f, hall, g / 5f));
            return top;
        }

        // ------------------------------------------------------------ harpoon ropes (Harpooner)
        static readonly float[] RopeAngles = { 0f, 35f, -35f, 70f, -70f, 110f, -110f, 150f, 180f };

        /// <summary>Reserves a rope site at a table/surface edge for a Harpooner (not climbable until DeployRope).
        /// `index` spreads several harpooners over different edges. Never returns null for a raised surface.</summary>
        public ClimbAccess ReserveRopeSite(Vector3 from, Vector3 hall, ITableSurface s, float floorY, int index)
        {
            if (s == null || !IsRaised(s, floorY)) return null;
            Remember(hall, s, floorY);
            CollectObstacles(s, floorY);
            Vector3 dir0 = from - hall; dir0.y = 0f;
            if (dir0.sqrMagnitude < 1e-6f) dir0 = Vector3.forward;
            dir0.Normalize();
            float surfY = s.Pose.position.y;
            float shift = (index % 2 == 0 ? 1f : -1f) * ((index + 1) / 2) * 38f;
            ClimbAccess first = null;
            for (int ai = 0; ai <= RopeAngles.Length; ai++)
            {
                float ang = shift + (ai < RopeAngles.Length ? RopeAngles[ai] : 0f);
                Vector3 dir = Quaternion.Euler(0f, ang, 0f) * dir0;
                Vector3 edge = s.ClosestEdgePoint(s.ProjectOnto(hall + dir * 5f));
                Vector3 top = edge - dir * 0.01f; top.y = surfY + 0.004f;
                var a = new ClimbAccess
                {
                    isLadder = false, surfaceId = s.Id, top = top, outward = dir,
                    bottom = new Vector3(edge.x + dir.x * 0.012f, floorY, edge.z + dir.z * 0.012f),
                    approach = new Vector3(edge.x + dir.x * (ObstaclePad + 0.01f), floorY, edge.z + dir.z * (ObstaclePad + 0.01f)),
                    shootPos = new Vector3(edge.x + dir.x * 0.40f, floorY, edge.z + dir.z * 0.40f),
                    complete = false, pending = true, swayPhase = UnityEngine.Random.value * 6f,
                    deadline = Time.time + 14f,
                };
                if (first == null) first = a;
                if (RopeSiteFree(a, s.Id) || ai == RopeAngles.Length) { if (ai == RopeAngles.Length) a = first; pendingRopes.Add(a); Announcement?.Invoke(Strings.HarpoonerSpotted, a.shootPos); return a; }
            }
            return null;
        }

        bool RopeSiteFree(ClimbAccess a, string hallId)
        {
            for (int i = 0; i < accesses.Count; i++) if (FlatDist(accesses[i].top, a.top) < 0.18f) return false;
            for (int i = 0; i < pendingRopes.Count; i++) if (FlatDist(pendingRopes[i].top, a.top) < 0.18f) return false;
            for (int i = 0; i < obs.Count; i++)
            {
                if (obs[i].id == hallId) continue;
                if (Inside(obs[i], a.approach) || Inside(obs[i], a.shootPos)) return false;
            }
            return true;
        }

        /// <summary>Makes the reserved rope real RIGHT NOW (harpoon hit, or fallback). Returns the live rope (an existing one if adjacent).</summary>
        public ClimbAccess DeployRope(ClimbAccess site)
        {
            if (site == null) return null;
            pendingRopes.Remove(site);
            site.pending = false;
            for (int i = 0; i < accesses.Count; i++)
                if (accesses[i] == site) return site;
            for (int i = 0; i < accesses.Count; i++)
                if (!accesses[i].isLadder && accesses[i].surfaceId == site.surfaceId && FlatDist(accesses[i].top, site.top) < 0.15f) return accesses[i];
            site.complete = true; site.progress = 1f;
            site.visual = BuildRopeVisual(site);
            accesses.Add(site);
            ropesThisWave++;
            if (VfxPool.Instance != null) { VfxPool.Instance.Burst(site.top, new Color(0.6f, 0.45f, 0.25f)); VfxPool.Instance.Ring(site.top, 0.06f); }
            ProceduralAudio.Fire(Sfx.Rope, site.top);
            Announcement?.Invoke(Strings.RopeSet, site.top);
            // everything already walking on the floor toward a bare climb now takes the rope
            var list = MonsterUnit.Active;
            for (int i = 0; i < list.Count; i++) if (list[i] != null) list[i].RefreshRoute(site);
            return site;
        }

        /// <summary>The harpooner died or aborted: deploy its rope after `delay` seconds anyway.</summary>
        public void ScheduleFallbackRope(ClimbAccess site, float delay)
        {
            if (site == null || !site.pending) return;
            site.deadline = Mathf.Min(site.deadline, Time.time + delay);
        }

        // ------------------------------------------------------------ ropes
        /// <summary>Creates a rope at a climbed edge (first climber). Ignored if one is close by or limits are reached.</summary>
        public ClimbAccess AddRope(string surfaceId, Vector3 top, Vector3 bottom)
        {
            Vector3 outward = Vector3.forward;
            var reg = SurfaceRegistry.Instance;
            var s = reg != null ? reg.Get(surfaceId) : null;
            if (s != null) { outward = top - s.Pose.position; outward.y = 0f; }
            if (outward.sqrMagnitude < 1e-6f) outward = Vector3.forward;
            outward.Normalize();
            return AddRope(surfaceId, top, bottom, outward, bottom + outward * (ObstaclePad + 0.01f));
        }

        /// <summary>Rope with explicit outward direction and free-floor approach point.</summary>
        public ClimbAccess AddRope(string surfaceId, Vector3 top, Vector3 bottom, Vector3 outward, Vector3 approach)
        {
            if (ropesThisWave >= MaxRopesPerWave || RopeCount >= MaxRopesTotal) return null;
            for (int i = 0; i < accesses.Count; i++)
                if (accesses[i].surfaceId == surfaceId && FlatDist(accesses[i].top, top) < 0.15f) return null;
            outward.y = 0f;
            if (outward.sqrMagnitude < 1e-6f) outward = Vector3.forward;
            var a = new ClimbAccess
            {
                isLadder = false, surfaceId = surfaceId, top = top, bottom = bottom, approach = approach, outward = outward.normalized,
                complete = true, progress = 1f, swayPhase = UnityEngine.Random.value * 6f,
            };
            a.visual = BuildRopeVisual(a);
            accesses.Add(a);
            ropesThisWave++;
            if (VfxPool.Instance != null) VfxPool.Instance.Burst(top, new Color(0.6f, 0.45f, 0.25f));
            ProceduralAudio.Fire(Sfx.Rope, top);
            Announcement?.Invoke(Strings.RopeSet, top);
            return a;
        }

        Transform Root()
        {
            if (visualRoot == null)
            {
                visualRoot = new GameObject("ClimbGear").transform;
                visualRoot.SetParent(transform, false);
            }
            return visualRoot;
        }

        GameObject BuildRopeVisual(ClimbAccess a)
        {
            var go = new GameObject("Rope");
            go.transform.SetParent(Root(), false);
            go.transform.position = a.top;
            var k = new BuildingVisuals.Kit(go.transform);
            float len = Mathf.Max(0.02f, a.top.y - a.bottom.y);
            Vector3 down = a.bottom - a.top;
            Vector3 mid = down * 0.5f;
            var rope = k.CylRot(mid, 0.003f, down.magnitude, new Color(0.55f, 0.4f, 0.22f), Vector3.zero);
            rope.transform.up = down.sqrMagnitude > 1e-6f ? -down.normalized : Vector3.up;
            Color iron = new Color(0.3f, 0.3f, 0.33f);
            k.Ball(new Vector3(0f, 0.004f, 0f), 0.012f, iron);
            for (int i = 0; i < 3; i++)
            {
                float ang = i * 120f;
                var hook = k.Cone(new Vector3(0f, 0.004f, 0f), 0.0025f, 0.014f, iron);
                hook.transform.localRotation = Quaternion.Euler(0f, ang, 0f) * Quaternion.Euler(70f, 0f, 0f);
            }
            for (int i = 1; i <= 4; i++) // knots
                k.Ball(new Vector3(0f, -len * i / 5f, 0f), 0.008f, new Color(0.45f, 0.32f, 0.18f));
            return go;
        }

        // ------------------------------------------------------------ ladders
        /// <summary>Reserves a ladder site for an engineer: floor foot 0.35–0.5 m out from the hall surface edge, on free floor,
        /// with an obstacle-free ladder line. Returns null when no site or MaxLadders reached.</summary>
        public ClimbAccess TryReserveLadder(Vector3 from, Vector3 hall, ITableSurface hallSurface, float floorY)
        {
            if (hallSurface == null || !IsRaised(hallSurface, floorY) || LadderCount >= MaxLadders) return null;
            Remember(hall, hallSurface, floorY);
            CollectObstacles(hallSurface, floorY);
            Vector3 dir0 = from - hall; dir0.y = 0f;
            if (dir0.sqrMagnitude < 1e-6f) dir0 = Vector3.forward;
            dir0.Normalize();
            float surfY = hallSurface.Pose.position.y;
            var angles = LadderAngles;
            var dists = LadderDists;
            for (int ai = 0; ai < angles.Length; ai++)
            {
                Vector3 dir = Quaternion.Euler(0f, angles[ai], 0f) * dir0;
                Vector3 edge = hallSurface.ClosestEdgePoint(hallSurface.ProjectOnto(hall + dir * 5f));
                Vector3 top = edge - dir * 0.01f;
                top.y = surfY + 0.004f;
                for (int di = 0; di < dists.Length; di++)
                {
                    Vector3 foot = new Vector3(edge.x + dir.x * dists[di], floorY, edge.z + dir.z * dists[di]);
                    if (!LadderSiteFree(foot, top, hallSurface.Id)) continue;
                    var a = new ClimbAccess
                    {
                        isLadder = true, surfaceId = hallSurface.Id, bottom = foot, approach = foot, top = top, outward = dir,
                        complete = false, progress = 0f,
                    };
                    a.visual = BuildLadderVisual(a);
                    SetLadderProgress(a, 0f);
                    accesses.Add(a);
                    return a;
                }
            }
            return null;
        }

        bool LadderSiteFree(Vector3 foot, Vector3 top, string hallId)
        {
            for (int i = 0; i < accesses.Count; i++)
                if (FlatDist(accesses[i].bottom, foot) < 0.3f || FlatDist(accesses[i].top, top) < 0.2f) return false;
            for (int i = 0; i < obs.Count; i++)
            {
                var o = obs[i];
                if (Inside(o, foot)) return false;
                if (o.id == hallId) continue; // the ladder leans on the hall surface itself
                if (SegHit(o, foot, top, out float tIn, out float tOut) && tOut - tIn > 1e-3f) return false;
            }
            return true;
        }

        GameObject BuildLadderVisual(ClimbAccess a)
        {
            var go = new GameObject("EnemyLadder");
            go.transform.SetParent(Root(), false);
            Vector3 axis = a.top - a.bottom;
            float len = axis.magnitude;
            go.transform.position = a.bottom;
            go.transform.rotation = Quaternion.LookRotation(axis / Mathf.Max(1e-4f, len), LadderUp(a));
            Color wood = new Color(0.5f, 0.33f, 0.15f), dark = new Color(0.33f, 0.21f, 0.1f);
            var grow = BuildingVisuals.NewRoot(go.transform, "Grow");
            var kg = new BuildingVisuals.Kit(grow);
            // rails along local Z from 0..1 (grow scales Z to the built length)
            kg.Box(new Vector3(-0.026f, 0f, 0.5f), new Vector3(0.005f, 0.005f, 1f), wood);
            kg.Box(new Vector3(0.026f, 0f, 0.5f), new Vector3(0.005f, 0.005f, 1f), wood);
            a.grow = grow;
            grow.localScale = new Vector3(1f, 1f, len);
            var k = new BuildingVisuals.Kit(go.transform);
            int n = Mathf.Max(2, Mathf.FloorToInt(len / 0.05f));
            for (int i = 1; i <= n; i++)
            {
                float z = len * i / (n + 1);
                var r = k.Box(new Vector3(0f, 0f, z), new Vector3(0.052f, 0.004f, 0.006f), dark);
                a.rungs.Add(r);
                a.rungT.Add(z / len);
            }
            return go;
        }

        public void SetLadderProgress(ClimbAccess a, float p)
        {
            if (a == null || a.visual == null) return;
            a.progress = Mathf.Clamp01(p);
            float len = a.ClimbLength;
            a.grow.localScale = new Vector3(1f, 1f, Mathf.Max(0.001f, len * a.progress));
            for (int i = 0; i < a.rungs.Count; i++)
            {
                bool on = a.rungT[i] <= a.progress;
                if (a.rungs[i].activeSelf != on) a.rungs[i].SetActive(on);
            }
        }

        public void AnnounceLadderStarted(ClimbAccess a)
        {
            ProceduralAudio.Fire(Sfx.LadderBuild, a.bottom);
            Announcement?.Invoke(Strings.EngineerLadder, a.bottom);
        }

        public void CompleteLadder(ClimbAccess a)
        {
            if (a == null) return;
            SetLadderProgress(a, 1f);
            a.complete = true;
            if (VfxPool.Instance != null) VfxPool.Instance.Ring(a.top, 0.08f);
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Place);
            Announcement?.Invoke(Strings.LadderBuilt, a.top);
        }

        /// <summary>Removes an unfinished ladder (its engineer died).</summary>
        public void Abandon(ClimbAccess a)
        {
            int i = accesses.IndexOf(a);
            if (i < 0 || a.complete) return;
            accesses.RemoveAt(i);
            if (VfxPool.Instance != null) VfxPool.Instance.Burst(a.bottom + Vector3.up * 0.05f, new Color(0.55f, 0.4f, 0.2f));
            if (a.visual != null) Destroy(a.visual);
            a.visual = null;
        }

        // ------------------------------------------------------------ hand poke / shockwave / reset
        /// <summary>
        /// Tactile AR hand poke/swipe at a siege ladder or rope near the table edge.
        /// Topples the ladder/rope off the edge and knocks any climbing goblins back to the floor with fall damage.
        /// </summary>
        public bool TryPokeAccess(Vector3 fingerPos, Vector3 fingerVel, float pokeRadius, float fallDamage, out ClimbAccess knockedAccess)
        {
            knockedAccess = null;
            float r2 = pokeRadius * pokeRadius;
            float topBonusR2 = (pokeRadius * 1.25f) * (pokeRadius * 1.25f);
            int bestIdx = -1;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < accesses.Count; i++)
            {
                var a = accesses[i];
                if (a == null) continue;
                if (a.isLadder && !a.complete && a.progress < 0.35f) continue;

                Vector3 builtTop = Vector3.Lerp(a.bottom, a.top, Mathf.Clamp01(a.progress));
                float topDistSq = (fingerPos - a.top).sqrMagnitude;
                float segDistSq = SegmentDistSq(fingerPos, a.bottom, builtTop);

                bool hitTop = topDistSq <= topBonusR2;
                bool hitSeg = segDistSq <= r2 && fingerPos.y >= a.bottom.y - 0.02f && fingerPos.y <= a.top.y + 0.08f;
                if (!hitTop && !hitSeg) continue;

                float dSq = Mathf.Min(topDistSq, segDistSq);
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0) return false;
            knockedAccess = accesses[bestIdx];
            KnockDownAccess(knockedAccess, fingerVel, fallDamage);
            return true;
        }

        public bool TryPokeAccess(Vector3 fingerPos, Vector3 fingerVel, float pokeRadius = 0.085f, float fallDamage = 3.5f)
            => TryPokeAccess(fingerPos, fingerVel, pokeRadius, fallDamage, out _);

        /// <summary>Knocks down all ladders/ropes within `radius` of `worldPos` with topple animation and fall damage.</summary>
        public int KnockDownInRadius(Vector3 worldPos, float radius, float fallDamage = 3.5f)
        {
            int count = 0;
            for (int i = accesses.Count - 1; i >= 0; i--)
            {
                var a = accesses[i];
                if (a == null) continue;
                if (FlatDist(a.top, worldPos) > radius && SegmentDistSq(worldPos, a.bottom, a.top) > radius * radius) continue;
                if (KnockDownAccess(a, a.outward, fallDamage)) count++;
            }
            return count;
        }

        /// <summary>Topples a specific ladder or rope off the table edge, dealing fall damage to climbing monsters and knocking them back to the floor.</summary>
        public bool KnockDownAccess(ClimbAccess a, Vector3 pushDir = default, float fallDamage = 3.5f)
        {
            if (a == null) return false;
            int index = accesses.IndexOf(a);
            if (index < 0) return false;
            accesses.RemoveAt(index);

            if (VfxPool.Instance != null) VfxPool.Instance.Burst(a.top, new Color(0.65f, 0.45f, 0.22f));
            ProceduralAudio.Fire(Sfx.Climb, a.top);
            ProceduralAudio.Fire(Sfx.Thud, a.top);
            Announcement?.Invoke(Strings.ClimbAccessDestroyed, a.top);

            if (a.visual != null)
            {
                Vector3 outDir = a.outward.sqrMagnitude > 1e-4f ? a.outward.normalized : Vector3.forward;
                Vector3 flatPush = new Vector3(pushDir.x, 0f, pushDir.z);
                if (flatPush.sqrMagnitude > 1e-4f)
                    outDir = (outDir + flatPush.normalized * 0.5f).normalized;
                Vector3 rotAxis = Vector3.Cross(Vector3.up, outDir);
                if (rotAxis.sqrMagnitude < 1e-4f) rotAxis = Vector3.right;
                else rotAxis.Normalize();

                toppling.Add(new ToppleAnim
                {
                    visual = a.visual,
                    isLadder = a.isLadder,
                    fallVel = outDir * 0.45f + Vector3.up * 0.15f,
                    rotAxis = rotAxis,
                    startRot = a.visual.transform.rotation,
                    elapsed = 0f,
                    duration = a.isLadder ? 0.55f : 0.45f
                });
                a.visual = null;
            }

            var list = MonsterUnit.Active;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (i >= list.Count) continue;
                var m = list[i];
                if (m == null || !m.IsAlive) continue;

                bool onThisClimb = m.Path != null && m.Path.Access == a &&
                    (m.Segment == PathSegment.Ladder || m.Segment == PathSegment.Rope || m.Segment == PathSegment.Climb);
                bool buildingThisLadder = m.EngineerLadder == a;

                if (onThisClimb)
                {
                    if (VfxPool.Instance != null) VfxPool.Instance.Burst(m.AimPoint, new Color(0.55f, 0.4f, 0.2f));
                    if (fallDamage > 0f && m.health != null && !m.health.IsDead)
                        m.health.Damage(fallDamage);
                    if (m.IsAlive)
                    {
                        m.OnAccessLost(a);
                        Vector3 knockDir = a.outward.sqrMagnitude > 1e-4f ? a.outward : Vector3.forward;
                        m.Push(knockDir, 0.08f);
                    }
                }
                else if (buildingThisLadder || (m.Path != null && m.Path.Access == a))
                {
                    if (buildingThisLadder && fallDamage > 0f && m.health != null && !m.health.IsDead)
                        m.health.Damage(fallDamage * 0.5f);
                    if (m.IsAlive) m.OnAccessLost(a);
                }
            }
            return true;
        }

        static float SegmentDistSq(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float l2 = ab.sqrMagnitude;
            if (l2 < 1e-8f) return (p - a).sqrMagnitude;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / l2);
            return (a + ab * t - p).sqrMagnitude;
        }

        /// <summary>Destroys ropes/ladders whose top lies within radius of center; monsters on them fall back and re-plan.</summary>
        public void OnShockwave(Vector3 center, float radius)
        {
            for (int i = accesses.Count - 1; i >= 0; i--)
            {
                var a = accesses[i];
                if (FlatDist(a.top, center) > radius + 0.05f) continue;
                DestroyAccess(i);
            }
        }

        void DestroyAccess(int index)
        {
            var a = accesses[index];
            accesses.RemoveAt(index);
            if (VfxPool.Instance != null) VfxPool.Instance.Burst(a.top, new Color(0.55f, 0.4f, 0.2f));
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Death);
            if (a.visual != null) Destroy(a.visual);
            a.visual = null;
            Announcement?.Invoke(Strings.ClimbAccessDestroyed, a.top);
            var list = MonsterUnit.Active;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) list[i].OnAccessLost(a);
        }

        /// <summary>Removes all ropes/ladders (restart / new empire). Called from MonsterUnit.DespawnAll.</summary>
        public void ClearAll()
        {
            for (int i = 0; i < accesses.Count; i++) if (accesses[i].visual != null) Destroy(accesses[i].visual);
            accesses.Clear();
            for (int i = 0; i < toppling.Count; i++) if (toppling[i].visual != null) Destroy(toppling[i].visual);
            toppling.Clear();
            pendingRopes.Clear();
            ropesThisWave = 0;
        }
    }
}
