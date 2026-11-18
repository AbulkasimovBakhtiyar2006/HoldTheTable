using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Connected build area grown from the Town Hall: center node r=0.45 plus a r=0.20 node per building.
    /// Validates support (incl. gaps), connectivity, overlap, obstacles and the 12-defense limit. Owner: Agent C.
    /// </summary>
    public class SettlementBuildArea : MonoBehaviour
    {
        public const int MaxDefenses = 12;
        /// <summary>Separate limit for economy buildings (Farm, Mine); they do not count toward MaxDefenses.</summary>
        public const int MaxEconomy = 8;
        public const float CenterRadius = 0.45f;
        public const float NodeRadius = 0.20f;
        public const float HallRadius = 0.12f;
        public const float MaxDropHeight = 0.12f;
        const float DefaultFootprint = 0.06f;

        static SettlementBuildArea instance;
        static bool quitting;
        public static SettlementBuildArea Instance
        {
            get
            {
                if (instance == null && !quitting && Application.isPlaying)
                    instance = new GameObject("SettlementBuildArea").AddComponent<SettlementBuildArea>();
                return instance;
            }
        }

        struct Node { public Vector3 pos; public float radius; }
        struct Footprint { public Vector3 pos; public float radius; public bool counted; public bool economy; }

        readonly List<Node> nodes = new List<Node>();
        readonly List<Footprint> footprints = new List<Footprint>();
        readonly List<MeshRenderer> discs = new List<MeshRenderer>();

        [Tooltip("Faint fill color of the build-area discs.")]
        public Color areaColor = new Color(0.45f, 0.9f, 1f, 0.10f);

        ITableSurface surface;
        Vector3 hallCenter;
        bool founded;
        bool visualsVisible;
        bool subscribed;
        static Mesh discMesh;

        public bool IsFounded => founded;
        public Vector3 Center => hallCenter;
        public ITableSurface Surface => surface;
        public int NodeCount => nodes.Count;

        /// <summary>Live number of defensive structures (Town Hall, banner and economy buildings excluded).</summary>
        public int DefenseCount
        {
            get
            {
                Sync();
                return CountFootprints(false);
            }
        }

        /// <summary>Live number of economy buildings (Farm, Mine).</summary>
        public int EconomyCount
        {
            get
            {
                Sync();
                return CountFootprints(true);
            }
        }

        int CountFootprints(bool economy)
        {
            int n = 0;
            for (int i = 0; i < footprints.Count; i++) if (footprints[i].economy == economy) n++;
            return n;
        }

        public static bool IsEconomyType(UnitType t)
        {
            var d = UnitDef.Get(t);
            return d != null && d.isEconomy;
        }

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(this); return; }
            instance = this;
        }

        void Start() => TrySubscribe();

        float syncTimer;
        void Update()
        {
            if (!founded) return;
            syncTimer -= Time.deltaTime;
            if (syncTimer > 0f) return;
            syncTimer = 0.2f;
            Sync();
        }

        void OnApplicationQuit() => quitting = true;

        void OnDestroy()
        {
            if (subscribed && GameDirector.Instance != null) GameDirector.Instance.PhaseChanged -= OnPhaseChanged;
            subscribed = false;
            if (instance == this) instance = null;
        }

        void TrySubscribe()
        {
            if (subscribed || GameDirector.Instance == null) return;
            GameDirector.Instance.PhaseChanged += OnPhaseChanged;
            subscribed = true;
            OnPhaseChanged(GameDirector.Instance.Phase);
        }

        void OnPhaseChanged(GamePhase p)
        {
            SetVisualsVisible(false); // user request: no build-area field around the capital (logic still enforced)
        }

        public void Found(Vector3 center, ITableSurface s)
        {
            TrySubscribe();
            surface = s;
            hallCenter = s != null ? s.ProjectOnto(center) : center;
            founded = true;
            nodes.Clear();
            footprints.Clear();
            nodes.Add(new Node { pos = hallCenter, radius = CenterRadius });
            RebuildVisuals();
        }

        public void AddNode(Vector3 p)
        {
            if (!founded) return;
            Vector3 pp = surface != null ? surface.ProjectOnto(p) : p;
            for (int i = 0; i < nodes.Count; i++)
                if (PlanarDist(nodes[i].pos, pp) < 0.01f) return;
            nodes.Add(new Node { pos = pp, radius = NodeRadius });
            RebuildVisuals();
        }

        /// <summary>Called after a defense is spawned: records its footprint and grows the settlement.</summary>
        public void RegisterPlacement(Vector3 p, float footprint)
        {
            if (!founded) return;
            Vector3 pp = surface != null ? surface.ProjectOnto(p) : p;
            for (int i = 0; i < footprints.Count; i++)
                if (PlanarDist(footprints[i].pos, pp) < 0.005f) { AddNode(pp); return; }
            footprints.Add(new Footprint { pos = pp, radius = Mathf.Max(0.01f, footprint) });
            AddNode(pp);
        }

        /// <summary>Removes the footprint of a destroyed defense (its area node stays so the settlement remains connected).</summary>
        public void Unregister(Vector3 p)
        {
            int best = -1; float bestD = 0.03f;
            for (int i = 0; i < footprints.Count; i++)
            {
                float d = PlanarDist(footprints[i].pos, p);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best >= 0) footprints.RemoveAt(best);
        }

        /// <summary>Legacy overload: validates as a defensive structure.</summary>
        public bool CanPlace(Vector3 p, float footprint, out string reasonKey) => CanPlace(p, footprint, false, out reasonKey);

        /// <summary>Type-aware validation: economy types use MaxEconomy, defenses use MaxDefenses.</summary>
        public bool CanPlace(Vector3 p, float footprint, UnitType type, out string reasonKey) => CanPlace(p, footprint, IsEconomyType(type), out reasonKey);

        bool CanPlace(Vector3 p, float footprint, bool economy, out string reasonKey)
        {
            reasonKey = null;
            if (!founded || surface == null) { reasonKey = Strings.PlacementInvalidOutside; return false; }
            Sync();
            if (economy ? CountFootprints(true) >= MaxEconomy : CountFootprints(false) >= MaxDefenses)
            { reasonKey = Strings.PlacementInvalidLimit; return false; }

            Vector3 pp = surface.ProjectOnto(p);
            Vector3 up = surface.Pose.up;
            if (Mathf.Abs(Vector3.Dot(p - pp, up)) > MaxDropHeight) { reasonKey = Strings.PlacementInvalidSupport; return false; }

            bool inside = false;
            for (int i = 0; i < nodes.Count; i++)
                if (PlanarDist(nodes[i].pos, pp) <= nodes[i].radius) { inside = true; break; }
            if (!inside) { reasonKey = Strings.PlacementInvalidOutside; return false; }

            if (!surface.IsPointSupported(pp) || !surface.IsFootprintSupported(pp, footprint))
            { reasonKey = Strings.PlacementInvalidSupport; return false; }

            if (PlanarDist(hallCenter, pp) < HallRadius + footprint) { reasonKey = Strings.PlacementInvalidOverlap; return false; }
            for (int i = 0; i < footprints.Count; i++)
                if (PlanarDist(footprints[i].pos, pp) < footprints[i].radius + footprint)
                { reasonKey = Strings.PlacementInvalidOverlap; return false; }

            var reg = SurfaceRegistry.Instance;
            if (reg != null && reg.OverlapsObstacle(pp, footprint, 0.08f)) { reasonKey = Strings.PlacementInvalidObstacle; return false; }
            return true;
        }

        /// <summary>Restart: keep the Town Hall position and its base area, forget all defenses.</summary>
        public void ResetKeepHall()
        {
            footprints.Clear();
            nodes.Clear();
            if (founded) nodes.Add(new Node { pos = hallCenter, radius = CenterRadius });
            RebuildVisuals();
        }

        /// <summary>New Empire: forget everything.</summary>
        public void Clear()
        {
            founded = false;
            surface = null;
            nodes.Clear();
            footprints.Clear();
            RebuildVisuals();
            SetVisualsVisible(false);
        }

        /// <summary>Keeps footprints consistent with live units: drops destroyed ones, adopts unregistered ones.</summary>
        void Sync()
        {
            if (!founded) return;
            var units = PlacedUnit.All;
            for (int i = footprints.Count - 1; i >= 0; i--)
            {
                bool alive = false;
                for (int u = 0; u < units.Count; u++)
                {
                    var unit = units[u];
                    if (unit != null && PlanarDist(unit.transform.position, footprints[i].pos) < 0.03f) { alive = true; break; }
                }
                if (!alive) footprints.RemoveAt(i);
            }
            for (int u = 0; u < units.Count; u++)
            {
                var unit = units[u];
                if (unit == null) continue;
                Vector3 up = unit.transform.position;
                bool known = false;
                for (int i = 0; i < footprints.Count; i++)
                    if (PlanarDist(up, footprints[i].pos) < 0.03f) { known = true; break; }
                if (!known) RegisterPlacement(up, unit.Footprint > 0f ? unit.Footprint : DefaultFootprint);
            }
            // Classify each new footprint once (economy vs defense); usage statistics for the Quartermaster count defenses only.
            for (int i = 0; i < footprints.Count; i++)
            {
                if (footprints[i].counted) continue;
                for (int u = 0; u < units.Count; u++)
                {
                    var unit = units[u];
                    if (unit == null || PlanarDist(unit.transform.position, footprints[i].pos) >= 0.03f) continue;
                    bool eco = IsEconomyType(unit.type);
                    var f = footprints[i]; f.counted = true; f.economy = eco; footprints[i] = f;
                    if (!eco && UpgradeState.Instance != null) UpgradeState.Instance.RecordPlacement(unit.type);
                    break;
                }
            }
        }

        float PlanarDist(Vector3 a, Vector3 b)
        {
            Vector3 d = a - b;
            if (surface != null) d = Vector3.ProjectOnPlane(d, surface.Pose.up);
            else d.y = 0f;
            return d.magnitude;
        }

        // ---------- visuals: faint flat discs on the surface ----------

        void SetVisualsVisible(bool v)
        {
            visualsVisible = v;
            for (int i = 0; i < discs.Count; i++)
                if (discs[i] != null) discs[i].enabled = v && i < nodes.Count;
        }

        void RebuildVisuals()
        {
            if (!Application.isPlaying) return;
            Quaternion rot = surface != null ? Quaternion.FromToRotation(Vector3.up, surface.Pose.up) : Quaternion.identity;
            Vector3 up = surface != null ? surface.Pose.up : Vector3.up;
            while (discs.Count < nodes.Count) discs.Add(CreateDisc(discs.Count));
            for (int i = 0; i < discs.Count; i++)
            {
                var r = discs[i];
                if (r == null) continue;
                if (i < nodes.Count)
                {
                    var t = r.transform;
                    t.position = nodes[i].pos + up * (0.002f + 0.0004f * i);
                    t.rotation = rot;
                    float d = nodes[i].radius * 2f;
                    t.localScale = new Vector3(d, 1f, d);
                    r.enabled = visualsVisible;
                }
                else r.enabled = false;
            }
        }

        MeshRenderer CreateDisc(int index)
        {
            var go = new GameObject("BuildAreaDisc_" + index);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = DiscMesh();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = Mats.Transparent(areaColor);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            mr.enabled = false;
            return mr;
        }

        /// <summary>Unit-diameter disc in XZ with a slightly brighter rim (vertex alpha via two rings).</summary>
        static Mesh DiscMesh()
        {
            if (discMesh != null) return discMesh;
            const int seg = 48;
            var verts = new Vector3[seg * 2 + 1];
            var tris = new int[seg * 9];
            verts[0] = Vector3.zero;
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                verts[1 + i] = dir * 0.47f;
                verts[1 + seg + i] = dir * 0.5f;
            }
            int k = 0;
            for (int i = 0; i < seg; i++)
            {
                int n = (i + 1) % seg;
                tris[k++] = 0; tris[k++] = 1 + n; tris[k++] = 1 + i;
                // rim quad (drawn on top of fill area outline)
                tris[k++] = 1 + i; tris[k++] = 1 + n; tris[k++] = 1 + seg + i;
                tris[k++] = 1 + n; tris[k++] = 1 + seg + n; tris[k++] = 1 + seg + i;
            }
            discMesh = new Mesh { name = "BuildAreaDisc" };
            discMesh.vertices = verts;
            discMesh.triangles = tris;
            discMesh.RecalculateNormals();
            discMesh.RecalculateBounds();
            return discMesh;
        }
    }
}
