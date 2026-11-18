using System.Collections.Generic;
using UnityEngine;
#if HTT_MRUK
using Meta.XR.MRUtilityKit;
#endif

namespace HoldTheTable
{
    /// <summary>
    /// Enemy spawn portals on FREE floor of the scanned MRUK room. Candidates on a 0.15 m grid inside the floor polygon;
    /// rejected inside obstacle footprints (+0.30 m) or under raised surfaces (+0.30 m), closer than 0.6 m to a wall, or
    /// closer than 2.0 m to the Town Hall (relaxed to the farthest point ≥ 1.2 m in small rooms). Largest clearance wins.
    /// 1 portal (2 if floor area &gt; 12 m², ≥ 1.5 m apart). Recomputed when the hall moves / is founded.
    /// HasRoom = false on the fallback table / without a scan (WaveSpawner then uses settlement-edge spawns).
    /// Self-creating singleton. Name kept for API compatibility ("Corners" = spawn points). Owner: Agent B.
    /// </summary>
    public class RoomCornerSpawns : MonoBehaviour
    {
        public const float GridStep = 0.15f;
        public const float WallClearance = 0.6f;
        public const float ObstaclePad = 0.30f;
        public const float HallMinDist = 2.0f;
        public const float HallMinDistSmallRoom = 1.2f;
        public const float TwoPortalArea = 12f;
        public const float PortalSeparation = 1.5f;

        static RoomCornerSpawns instance;
        static bool quitting;
        /// <summary>Existing instance without auto-creation (null if none).</summary>
        public static RoomCornerSpawns Existing => instance;
        public static RoomCornerSpawns Instance
        {
            get
            {
                if (instance == null && !quitting && Application.isPlaying)
                    instance = new GameObject("RoomCornerSpawns").AddComponent<RoomCornerSpawns>();
                return instance;
            }
        }

        struct Rect2 { public float minX, maxX, minZ, maxZ; }

        readonly List<Vector3> corners = new List<Vector3>();
        readonly List<Vector3> raw = new List<Vector3>();
        readonly List<Vector2> poly = new List<Vector2>();
        readonly List<Rect2> blockers = new List<Rect2>();
        readonly List<Vector3> picked = new List<Vector3>(2);
        readonly List<GameObject> portals = new List<GameObject>();

        // animation state (no per-frame allocations)
        readonly List<Transform> spinT = new List<Transform>();
        readonly List<float> spinSpeed = new List<float>();
        readonly List<Transform> pulseT = new List<Transform>();
        readonly List<Vector3> pulseBase = new List<Vector3>();
        readonly List<Transform> sparks = new List<Transform>();
        readonly List<Vector3> sparkVel = new List<Vector3>();
        readonly List<float> sparkAge = new List<float>();

        Transform portalRoot;
        float pollTimer;
        Vector3 lastHallPos;
        bool lastHadHall;

        public IReadOnlyList<Vector3> Corners => corners;
        public IReadOnlyList<Vector3> SpawnPoints => corners;
        public bool HasRoom { get; private set; }
        public float FloorY { get; private set; }
        public float FloorArea { get; private set; }

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(this); return; }
            instance = this;
        }

        void OnApplicationQuit() => quitting = true;
        void OnDestroy() { if (instance == this) instance = null; }

        void Update()
        {
            pollTimer -= Time.deltaTime;
            // recompute immediately when the hall is founded / replaced (distance-to-base rule uses the real hall)
            var hall = TownHall.Current;
            bool hasHall = hall != null;
            if (hasHall != lastHadHall || (hasHall && (hall.transform.position - lastHallPos).sqrMagnitude > 0.01f)) pollTimer = 0f;
            if (pollTimer <= 0f)
            {
                pollTimer = HasRoom ? 5f : 1f;
                Refresh();
            }
            Animate(Time.deltaTime);
        }

        static Vector3 HallPosition(out bool has)
        {
            var hall = TownHall.Current;
            if (hall == null && GameDirector.Instance != null) hall = GameDirector.Instance.Hall;
            has = hall != null;
            return has ? hall.transform.position : Vector3.zero;
        }

        /// <summary>Recomputes spawn points from the current MRUK room. Returns HasRoom.</summary>
        public bool Refresh()
        {
            bool fallback = TableAnchorBootstrap.Instance != null && TableAnchorBootstrap.Instance.UsingFallback;
            raw.Clear();
            float floorY = 0f;
            bool found = !fallback && TryReadFloor(raw, out floorY);
            if (!found || raw.Count < 3)
            {
                if (HasRoom) ClearPortals();
                HasRoom = false;
                corners.Clear();
                return false;
            }

            Vector3 hallPos = HallPosition(out bool hasHall);
            lastHadHall = hasHall;
            lastHallPos = hallPos;
            ComputeSpawns(floorY, hallPos, hasHall);

            bool changed = !HasRoom || picked.Count != corners.Count || Mathf.Abs(floorY - FloorY) > 0.02f;
            if (!changed)
                for (int i = 0; i < picked.Count; i++)
                    if ((picked[i] - corners[i]).sqrMagnitude > 0.01f) { changed = true; break; }
            FloorY = floorY;
            HasRoom = true;
            if (changed || portals.Count != corners.Count)
            {
                corners.Clear();
                corners.AddRange(picked);
                BuildPortals(hallPos, hasHall);
                Debug.Log($"[RoomCornerSpawns] portals={corners.Count} floorY={FloorY:F2} area={FloorArea:F1}m² hall={(hasHall ? "yes" : "no")}");
            }
            return true;
        }

        // ------------------------------------------------------------ spawn selection
        void ComputeSpawns(float floorY, Vector3 hallPos, bool hasHall)
        {
            poly.Clear();
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < raw.Count; i++)
            {
                var p = new Vector2(raw[i].x, raw[i].z);
                poly.Add(p);
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.y); maxZ = Mathf.Max(maxZ, p.y);
            }
            float area = 0f;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) area += (poly[j].x + poly[i].x) * (poly[j].y - poly[i].y);
            FloorArea = Mathf.Abs(area) * 0.5f;

            CollectBlockers(floorY);
            int want = FloorArea > TwoPortalArea ? 2 : 1;
            picked.Clear();
            for (int n = 0; n < want; n++)
            {
                if (!PickBest(minX, maxX, minZ, maxZ, floorY, hallPos, hasHall, out Vector3 best)) break;
                picked.Add(best);
            }
            if (picked.Count == 0)
            {
                // nothing valid at all: use the floor point farthest from the hall that is still inside the polygon
                Vector3 c = Vector3.zero;
                for (int i = 0; i < raw.Count; i++) c += raw[i];
                c /= raw.Count;
                c.y = floorY;
                picked.Add(c);
            }
        }

        void CollectBlockers(float floorY)
        {
            blockers.Clear();
            var reg = SurfaceRegistry.Instance;
            if (reg == null) return;
            for (int i = 0; i < reg.ObstacleCount; i++)
            {
                if (!reg.TryGetObstacle(i, out _, out var b)) continue;
                if (b.max.y < floorY + 0.03f) continue; // rugs / floor clutter
                blockers.Add(new Rect2 { minX = b.min.x, maxX = b.max.x, minZ = b.min.z, maxZ = b.max.z });
            }
            var surfaces = reg.Surfaces;
            for (int i = 0; i < surfaces.Count; i++)
            {
                var s = surfaces[i];
                if (s == null || s.IsFallback || s.Pose.position.y <= floorY + 0.15f) continue;
                var pose = s.Pose;
                Vector3 r = pose.rotation * Vector3.right * (s.Size.x * 0.5f);
                Vector3 f = pose.rotation * Vector3.forward * (s.Size.y * 0.5f);
                float ex = Mathf.Abs(r.x) + Mathf.Abs(f.x), ez = Mathf.Abs(r.z) + Mathf.Abs(f.z);
                Vector3 c = pose.position;
                blockers.Add(new Rect2 { minX = c.x - ex, maxX = c.x + ex, minZ = c.z - ez, maxZ = c.z + ez });
            }
        }

        bool PickBest(float minX, float maxX, float minZ, float maxZ, float floorY, Vector3 hallPos, bool hasHall, out Vector3 best)
        {
            best = default;
            float bestScore = float.MinValue;          // clearance among points ≥ 2.0 m from the hall
            float bestFar = float.MinValue;            // hall distance among points ≥ 1.2 m (small rooms)
            Vector3 farPoint = default;
            bool haveStrict = false, haveFar = false;
            for (float x = minX + GridStep * 0.5f; x < maxX; x += GridStep)
            {
                for (float z = minZ + GridStep * 0.5f; z < maxZ; z += GridStep)
                {
                    var p2 = new Vector2(x, z);
                    if (!SurfaceMath.PointInPolygon(p2, poly)) continue;
                    float wall = WallDistance(p2);
                    if (wall < WallClearance) continue;
                    float obsClear = float.MaxValue;
                    bool blocked = false;
                    for (int i = 0; i < blockers.Count; i++)
                    {
                        float d = RectDistance(blockers[i], p2);
                        if (d < ObstaclePad) { blocked = true; break; }
                        if (d < obsClear) obsClear = d;
                    }
                    if (blocked) continue;
                    var p = new Vector3(x, floorY, z);
                    bool tooClose = false;
                    for (int i = 0; i < picked.Count; i++)
                        if (FlatDist(picked[i], p) < PortalSeparation) { tooClose = true; break; }
                    if (tooClose) continue;
                    float clearance = Mathf.Min(wall, obsClear);
                    float hd = hasHall ? FlatDist(p, hallPos) : float.MaxValue;
                    if (!hasHall || hd >= HallMinDist)
                    {
                        // prefer clearance; tiny bias toward distance from the hall breaks ties
                        float score = clearance + (hasHall ? Mathf.Min(hd, 6f) * 0.02f : 0f);
                        if (score > bestScore) { bestScore = score; best = p; haveStrict = true; }
                    }
                    else if (hd >= HallMinDistSmallRoom)
                    {
                        if (hd > bestFar) { bestFar = hd; farPoint = p; haveFar = true; }
                    }
                }
            }
            if (haveStrict) return true;
            if (haveFar) { best = farPoint; return true; }
            return false;
        }

        float WallDistance(Vector2 p)
        {
            float best = float.MaxValue;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector2 a = poly[j], ab = poly[i] - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-9f, ab.sqrMagnitude));
                float d = (a + ab * t - p).sqrMagnitude;
                if (d < best) best = d;
            }
            return Mathf.Sqrt(best);
        }

        static float RectDistance(in Rect2 r, Vector2 p)
        {
            float dx = Mathf.Max(Mathf.Max(r.minX - p.x, p.x - r.maxX), 0f);
            float dz = Mathf.Max(Mathf.Max(r.minZ - p.y, p.y - r.maxZ), 0f);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static float FlatDist(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

        bool TryReadFloor(List<Vector3> outPts, out float floorY)
        {
            floorY = 0f;
#if HTT_MRUK
            var mruk = MRUK.Instance;
            if (mruk == null || !mruk.IsInitialized) return false;
            MRUKRoom room = mruk.GetCurrentRoom();
            if (room == null && mruk.Rooms != null && mruk.Rooms.Count > 0) room = mruk.Rooms[0];
            if (room == null) return false;
            MRUKAnchor floor = room.FloorAnchor;
            if (floor == null) return false;
            var tr = floor.transform;
            floorY = tr.position.y;
            var b = floor.PlaneBoundary2D;
            if (b != null && b.Count >= 3)
            {
                for (int i = 0; i < b.Count; i++) outPts.Add(tr.TransformPoint(new Vector3(b[i].x, b[i].y, 0f)));
            }
            else if (floor.PlaneRect.HasValue)
            {
                var r = floor.PlaneRect.Value;
                outPts.Add(tr.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
                outPts.Add(tr.TransformPoint(new Vector3(r.xMax, r.yMin, 0f)));
                outPts.Add(tr.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
                outPts.Add(tr.TransformPoint(new Vector3(r.xMin, r.yMax, 0f)));
            }
            return outPts.Count >= 3;
#else
            return false;
#endif
        }

        // ------------------------------------------------------------ portal visuals
        static Mesh diskMesh, ringMesh, rimMesh, spiralMesh;

        static Mesh DiskMesh => diskMesh != null ? diskMesh : (diskMesh = Annulus(0f, 1f, 40, "htt_vortex_disk"));
        static Mesh RingMesh => ringMesh != null ? ringMesh : (ringMesh = Annulus(0.55f, 0.72f, 40, "htt_vortex_ring"));
        static Mesh RimMesh => rimMesh != null ? rimMesh : (rimMesh = Annulus(0.93f, 1.04f, 48, "htt_vortex_rim"));
        static Mesh SpiralMesh => spiralMesh != null ? spiralMesh : (spiralMesh = Spiral(2.2f, 0.09f, 90, "htt_vortex_spiral"));

        /// <summary>Double-sided annulus in the local XY plane (rIn = 0 → disk).</summary>
        static Mesh Annulus(float rIn, float rOut, int seg, string name)
        {
            var v = new List<Vector3>(seg * 2 + 2);
            var t = new List<int>(seg * 12);
            for (int i = 0; i <= seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                v.Add(new Vector3(c * rIn, s * rIn, 0f));
                v.Add(new Vector3(c * rOut, s * rOut, 0f));
            }
            for (int i = 0; i < seg; i++)
            {
                int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
                t.Add(a); t.Add(b); t.Add(d); t.Add(a); t.Add(d); t.Add(c);   // front
                t.Add(a); t.Add(d); t.Add(b); t.Add(a); t.Add(c); t.Add(d);   // back
            }
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Double-sided Archimedean spiral ribbon in the local XY plane, from r≈0.08 to r≈0.95.</summary>
        static Mesh Spiral(float turns, float width, int seg, string name)
        {
            var v = new List<Vector3>((seg + 1) * 2);
            var t = new List<int>(seg * 12);
            for (int i = 0; i <= seg; i++)
            {
                float u = i / (float)seg;
                float a = u * turns * Mathf.PI * 2f;
                float r = Mathf.Lerp(0.08f, 0.95f, u);
                float w = width * Mathf.Lerp(0.4f, 1f, u);
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                v.Add(new Vector3(c * (r - w * 0.5f), s * (r - w * 0.5f), 0f));
                v.Add(new Vector3(c * (r + w * 0.5f), s * (r + w * 0.5f), 0f));
            }
            for (int i = 0; i < seg; i++)
            {
                int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
                t.Add(a); t.Add(b); t.Add(d); t.Add(a); t.Add(d); t.Add(c);
                t.Add(a); t.Add(d); t.Add(b); t.Add(a); t.Add(c); t.Add(d);
            }
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        void ClearPortals()
        {
            for (int i = 0; i < hums.Count; i++) if (ProceduralAudio.Instance != null && hums[i] != null) ProceduralAudio.Instance.StopLoop(hums[i]);
            hums.Clear();
            for (int i = 0; i < portals.Count; i++) if (portals[i] != null) Destroy(portals[i]);
            portals.Clear();
            spinT.Clear(); spinSpeed.Clear();
            pulseT.Clear(); pulseBase.Clear();
            sparks.Clear(); sparkVel.Clear(); sparkAge.Clear();
        }

        readonly List<AudioSource> hums = new List<AudioSource>();

        Camera portalCam;

        void LateUpdate()
        {
            // portals always face the player (yaw only, so they stay upright on the floor)
            if (portalCam == null) portalCam = Camera.main;
            if (portalCam != null)
            {
                Vector3 cp = portalCam.transform.position;
                for (int i = 0; i < portals.Count; i++)
                {
                    var pt = portals[i] != null ? portals[i].transform : null;
                    if (pt == null) continue;
                    Vector3 d = cp - pt.position; d.y = 0f;
                    if (d.sqrMagnitude > 1e-4f) pt.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
                }
            }
            // portal hum loops follow the portals (one per portal)
            if (hums.Count == portals.Count) return;
            for (int i = 0; i < hums.Count; i++) if (ProceduralAudio.Instance != null && hums[i] != null) ProceduralAudio.Instance.StopLoop(hums[i]);
            hums.Clear();
            if (ProceduralAudio.Instance == null) return;
            for (int i = 0; i < portals.Count; i++)
                if (portals[i] != null) hums.Add(ProceduralAudio.Instance.PlayLoop(Sfx.PortalHum, portals[i].transform.position, 0.6f));
        }

        void BuildPortals(Vector3 hallPos, bool hasHall)
        {
            ClearPortals();
            if (portalRoot == null)
            {
                portalRoot = new GameObject("SpawnPortals").transform;
                portalRoot.SetParent(transform, false);
            }
            Vector3 center = Vector3.zero;
            for (int i = 0; i < raw.Count; i++) center += raw[i];
            if (raw.Count > 0) center /= raw.Count;
            for (int i = 0; i < corners.Count; i++)
            {
                Vector3 look = (hasHall ? hallPos : center) - corners[i];
                look.y = 0f;
                if (look.sqrMagnitude < 1e-4f) look = Vector3.forward;
                portals.Add(BuildPortal(corners[i], Quaternion.LookRotation(look.normalized, Vector3.up), i));
            }
        }

        static GameObject Layer(Transform parent, Mesh mesh, Material mat, float z, float scale)
        {
            var go = new GameObject("layer");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, z);
            go.transform.localScale = Vector3.one * scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return go;
        }

        GameObject BuildPortal(Vector3 at, Quaternion facing, int index)
        {
            var root = new GameObject("SpawnPortal_" + index);
            root.transform.SetParent(portalRoot, false);
            root.transform.SetPositionAndRotation(at, facing);

            Color core = new Color(0.02f, 0.12f, 0.05f, 0.92f);
            Color emerald = new Color(0.05f, 0.75f, 0.35f, 0.75f);
            Color lime = new Color(0.6f, 1f, 0.2f, 0.7f);
            Color mint = new Color(0.35f, 1f, 0.6f, 0.45f);
            Color rim = new Color(0.75f, 1f, 0.35f);
            Color glow = new Color(0.3f, 1f, 0.35f, 0.22f);

            // soft ground glow
            var k = new BuildingVisuals.Kit(root.transform);
            k.CylRot(new Vector3(0f, 0.003f, 0f), 0.36f, 0.003f, glow, Vector3.zero).GetComponent<MeshRenderer>().sharedMaterial = Mats.Transparent(glow);
            k.CylRot(new Vector3(0f, 0.005f, 0f), 0.2f, 0.003f, glow, Vector3.zero).GetComponent<MeshRenderer>().sharedMaterial = Mats.Transparent(glow);

            // vertical oval vortex (~0.6 m tall) standing on the floor; local +Z faces the hall
            var vortex = new GameObject("Vortex").transform;
            vortex.SetParent(root.transform, false);
            Vector3 ovalScale = new Vector3(0.21f, 0.3f, 1f);
            vortex.localPosition = new Vector3(0f, 0.31f, 0f);
            vortex.localScale = ovalScale;
            pulseT.Add(vortex); pulseBase.Add(ovalScale);

            Layer(vortex, DiskMesh, Mats.Transparent(core), 0f, 1f);
            var s1 = Layer(vortex, SpiralMesh, Mats.Transparent(emerald), 0.004f, 1f).transform;
            var s2 = Layer(vortex, SpiralMesh, Mats.Transparent(lime), 0.008f, 0.92f).transform;
            s2.localRotation = Quaternion.Euler(0f, 0f, 120f);
            var s3 = Layer(vortex, SpiralMesh, Mats.Transparent(mint), 0.012f, 0.8f).transform;
            s3.localRotation = Quaternion.Euler(0f, 0f, 240f);
            var ring = Layer(vortex, RingMesh, Mats.Transparent(mint), 0.006f, 1f).transform;
            var ring2 = Layer(vortex, RingMesh, Mats.Transparent(emerald), 0.010f, 0.55f).transform;
            Layer(vortex, RimMesh, Mats.Unlit(rim), 0.014f, 1f);
            spinT.Add(s1); spinSpeed.Add(-140f);
            spinT.Add(s2); spinSpeed.Add(-95f);
            spinT.Add(s3); spinSpeed.Add(-200f);
            pulseT.Add(ring); pulseBase.Add(Vector3.one);
            pulseT.Add(ring2); pulseBase.Add(Vector3.one * 0.55f);

            // drifting sparks (local to root, recycled)
            var ks = new BuildingVisuals.Kit(root.transform);
            Material sparkMat = Mats.Unlit(new Color(0.7f, 1f, 0.3f));
            for (int i = 0; i < 8; i++)
            {
                var sp = ks.Ball(Vector3.zero, 0.012f, lime);
                sp.GetComponent<MeshRenderer>().sharedMaterial = sparkMat;
                sparks.Add(sp.transform);
                sparkVel.Add(Vector3.zero);
                sparkAge.Add(-i * 0.2f); // staggered start
            }
            return root;
        }

        void Animate(float dt)
        {
            float t = Time.time;
            for (int i = 0; i < spinT.Count; i++)
                if (spinT[i] != null) spinT[i].Rotate(0f, 0f, spinSpeed[i] * dt, Space.Self);
            for (int i = 0; i < pulseT.Count; i++)
            {
                if (pulseT[i] == null) continue;
                float k = 1f + Mathf.Sin(t * (2.2f + i * 0.7f) + i) * 0.045f;
                pulseT[i].localScale = new Vector3(pulseBase[i].x * k, pulseBase[i].y * k, pulseBase[i].z);
            }
            for (int i = 0; i < sparks.Count; i++)
            {
                var sp = sparks[i];
                if (sp == null) continue;
                float age = sparkAge[i] + dt;
                if (age >= 1.6f || (age >= 0f && sparkAge[i] < 0f))
                {
                    // respawn at the vortex face, drift outward and forward (deterministic pseudo-random per index)
                    float a = (i * 2.399f + t * 1.7f) % (Mathf.PI * 2f);
                    Vector3 dir = new Vector3(Mathf.Cos(a) * 0.7f, Mathf.Sin(a), 0f);
                    sp.localPosition = new Vector3(dir.x * 0.12f, 0.31f + dir.y * 0.18f, 0.02f);
                    sparkVel[i] = new Vector3(dir.x * 0.12f, dir.y * 0.1f + 0.05f, 0.18f + (i % 3) * 0.04f);
                    age = 0f;
                }
                if (age >= 0f)
                {
                    sp.localPosition += sparkVel[i] * dt;
                    float s = 0.012f * (1f - age / 1.6f) + 0.002f;
                    sp.localScale = new Vector3(s, s, s);
                }
                else sp.localScale = Vector3.zero;
                sparkAge[i] = age;
            }
        }
    }
}
