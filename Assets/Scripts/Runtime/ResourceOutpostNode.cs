using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    public enum OutpostNodeKind
    {
        GoldenVein,
        AncientRelic
    }

    /// <summary>
    /// Feature 6: Multi-Table / Adjacent Furniture Outposts &amp; Golden Veins.
    /// Procedurally spawns glowing Golden Veins and Ancient Relics on secondary detected furniture surfaces
    /// or the outer corners/rims of the primary table.
    /// When the player places a Mine (or claims the node by poking or building an outpost near it),
    /// it grants 2x gold income and activates a glowing crystal/relic beacon.
    /// Goblins have a chance to divert a raiding party toward an active claimed Outpost/Mine on the rim.
    /// </summary>
    public class ResourceOutpostNode : MonoBehaviour
    {
        public const float ClaimRadius = 0.16f;
        public const float PokeClaimRadius = 0.085f;
        public const float ClaimedGoldMultiplier = 2.0f;
        public const float RaidDivertChance = 0.35f;
        public const float EdgeInset = 0.075f;
        public const float MinDistFromHall = 0.23f;
        public const float MinNodeSeparation = 0.26f;
        public const int MaxTotalNodes = 3;

        /// <summary>All active resource outpost nodes in the current session.</summary>
        public static readonly List<ResourceOutpostNode> All = new List<ResourceOutpostNode>();

        public static event Action<ResourceOutpostNode> NodeClaimed;
        public static event Action<ResourceOutpostNode> NodePillaged;
        public static event Action<MonsterUnit, ResourceOutpostNode> RaidingPartyDiverted;

        static Transform nodesRoot;
        static readonly HashSet<int> evaluatedMonsterSerials = new HashSet<int>();
        static readonly Dictionary<int, ResourceOutpostNode> secondarySurfaceRaiders = new Dictionary<int, ResourceOutpostNode>();
        static int nextRaidNodeCursor;

        public OutpostNodeKind Kind { get; private set; }
        public ITableSurface Surface { get; private set; }
        public bool IsSecondarySurface { get; private set; }
        public bool IsClaimed { get; private set; }
        public bool ClaimedByPoke { get; private set; }
        public PlacedUnit ClaimedUnit { get; private set; }
        public Vector3 Position => transform.position;
        public Vector3 SurfaceUp => Surface != null ? Surface.Pose.up : Vector3.up;

        /// <summary>True when a live Mine is currently bound to this node.</summary>
        public bool HasActiveMine => ClaimedUnit != null && ClaimedUnit.type == UnitType.Mine && ClaimedUnit.health != null && !ClaimedUnit.health.IsDead;

        Transform visualRoot;
        Transform crystalCore;
        Transform beaconRoot;
        Transform beaconCrystal;
        Transform beaconRingLower;
        Transform beaconRingUpper;
        readonly List<Transform> orbiters = new List<Transform>(4);

        float animPhase;
        float beaconPulseTimer;
        float pokeCooldown;
        float pillageDamageAccum;
        float buildAreaSyncTimer;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            All.Clear();
            evaluatedMonsterSerials.Clear();
            secondarySurfaceRaiders.Clear();
            NodeClaimed = null;
            NodePillaged = null;
            RaidingPartyDiverted = null;
            nodesRoot = null;
            nextRaidNodeCursor = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoBootstrap()
        {
            OutpostCoordinator.Ensure();
        }

        // ====================================================================
        // PUBLIC STATIC MANAGER / SPAWNER API (For GameDirector & PlacedUnit)
        // ====================================================================

        /// <summary>
        /// Spawns glowing Golden Veins / Ancient Relics on secondary detected furniture surfaces
        /// and on the outer corners/rims of the primary table surface.
        /// Safe to call from GameDirector.OnHeadquartersPlaced and GameDirector.ResetSession / Restart.
        /// </summary>
        public static void SpawnForSession(Vector3 hallPos, ITableSurface primarySurface)
        {
            OutpostCoordinator.Ensure();
            ClearAll();

            if (primarySurface == null && GameDirector.Instance != null)
                primarySurface = GameDirector.Instance.Surface;
            if (primarySurface == null) return;

            if (nodesRoot == null)
                nodesRoot = new GameObject("ResourceOutpostNodes").transform;

            float floorY = RoomCornerSpawns.Existing != null ? RoomCornerSpawns.Existing.FloorY : primarySurface.Height - 0.72f;
            var reg = SurfaceRegistry.Instance;

            // 1. Check secondary detected furniture surfaces first (up to 1-2 Ancient Relics on adjacent tables/furniture)
            if (reg != null)
            {
                var surfaces = reg.Surfaces;
                for (int i = 0; i < surfaces.Count && All.Count < MaxTotalNodes - 1; i++)
                {
                    var s = surfaces[i];
                    if (s == null || s == primarySurface || s.Id == primarySurface.Id || s.IsFallback) continue;
                    if (s.Height <= floorY + 0.14f) continue; // skip floor

                    float distToHall = FlatDist(s.Pose.position, hallPos);
                    if (distToHall < 0.25f || distToHall > 2.2f) continue;

                    if (TryFindSecondarySurfacePoint(s, hallPos, reg, out Vector3 secPos))
                    {
                        CreateNode(secPos, s, OutpostNodeKind.AncientRelic, isSecondary: true);
                    }
                }
            }

            // 2. Fill remaining slots (up to MaxTotalNodes = 3) on outer corners/rims of the primary table
            int wantPrimary = MaxTotalNodes - All.Count;
            var rimCandidates = FindPrimaryRimPositions(hallPos, primarySurface, reg, wantPrimary);
            for (int i = 0; i < rimCandidates.Count; i++)
            {
                var kind = (i % 2 == 0) ? OutpostNodeKind.GoldenVein : OutpostNodeKind.AncientRelic;
                var node = CreateNode(rimCandidates[i], primarySurface, kind, isSecondary: false);
                if (SettlementBuildArea.Instance != null && SettlementBuildArea.Instance.IsFounded)
                    SettlementBuildArea.Instance.AddNode(node.Position);
            }

            if (GameDirector.Instance != null)
                GameDirector.Instance.RecomputeRates();
        }

        /// <summary>Alias for SpawnForSession.</summary>
        public static void SpawnNodes(Vector3 hallPos, ITableSurface primarySurface) => SpawnForSession(hallPos, primarySurface);

        /// <summary>Alias for SpawnForSession using current GameDirector Hall &amp; Surface.</summary>
        public static void SpawnNodes()
        {
            var gd = GameDirector.Instance;
            if (gd != null && gd.Hall != null && gd.Surface != null)
                SpawnForSession(gd.Hall.transform.position, gd.Surface);
        }

        /// <summary>Removes all spawned outpost nodes (call from GameDirector.StopSessionObjects / NewEmpire).</summary>
        public static void ClearAll()
        {
            for (int i = All.Count - 1; i >= 0; i--)
            {
                if (All[i] != null) Destroy(All[i].gameObject);
            }
            All.Clear();
            evaluatedMonsterSerials.Clear();
            secondarySurfaceRaiders.Clear();
            nextRaidNodeCursor = 0;
        }

        /// <summary>
        /// Returns 2.0f (ClaimedGoldMultiplier) if the given PlacedUnit is a Mine placed on/near a Golden Vein / Ancient Relic,
        /// or 1.0f otherwise. Also ensures the node is marked claimed by that Mine.
        /// Call from GameDirector.RecomputeRates() when summing Mine goldPerSec.
        /// </summary>
        public static float GetGoldMultiplier(PlacedUnit unit)
        {
            if (unit == null || unit.type != UnitType.Mine) return 1f;
            if (unit.health != null && unit.health.IsDead) return 1f;

            var node = FindNodeForUnit(unit);
            if (node != null)
            {
                if (!node.IsClaimed || node.ClaimedUnit != unit)
                    node.BindUnit(unit);
                return ClaimedGoldMultiplier;
            }
            return 1f;
        }

        /// <summary>Overload checking world position for 2x gold multiplier.</summary>
        public static float GetGoldMultiplier(Vector3 worldPos)
        {
            var node = FindClosestNode(worldPos, ClaimRadius);
            return node != null ? ClaimedGoldMultiplier : 1f;
        }

        /// <summary>True if this PlacedUnit is currently boosting/claiming a Golden Vein or Ancient Relic.</summary>
        public static bool IsBoostedMine(PlacedUnit unit) => GetGoldMultiplier(unit) > 1.01f;

        /// <summary>
        /// Returns bonus gold/sec from nodes that were claimed by finger-poke or a non-Mine outpost building
        /// and do NOT have a Mine unit on them (so it never double-counts with GetGoldMultiplier(mineUnit)).
        /// Add this to `mine` inside GameDirector.RecomputeRates().
        /// </summary>
        public static float GetUnboundClaimedGoldPerSec()
        {
            float baseMineRate = UnitDef.Get(UnitType.Mine) != null ? UnitDef.Get(UnitType.Mine).goldPerSec : 1f;
            float bonus = 0f;
            for (int i = 0; i < All.Count; i++)
            {
                var n = All[i];
                if (n == null || !n.IsClaimed) continue;
                if (n.HasActiveMine) continue; // already accounted for via GetGoldMultiplier(mine)
                bonus += baseMineRate * ClaimedGoldMultiplier;
            }
            return bonus;
        }

        /// <summary>Alias for GetUnboundClaimedGoldPerSec().</summary>
        public static float GetBonusGoldPerSecond() => GetUnboundClaimedGoldPerSec();

        /// <summary>
        /// Notifies the outpost system that a unit was placed. If placed within ClaimRadius of a Golden Vein / Ancient Relic,
        /// claims the node and activates the crystal/relic beacon.
        /// Call from PlacedUnit.Init() or BuildingFactory.SpawnUnit().
        /// </summary>
        public static bool NotifyUnitPlaced(PlacedUnit unit)
        {
            if (unit == null || (unit.health != null && unit.health.IsDead)) return false;
            var node = FindClosestNode(unit.transform.position, ClaimRadius);
            if (node == null) return false;
            node.BindUnit(unit);
            return true;
        }

        /// <summary>Alias for NotifyUnitPlaced.</summary>
        public static bool OnUnitPlaced(PlacedUnit unit) => NotifyUnitPlaced(unit);

        /// <summary>Alias for NotifyUnitPlaced.</summary>
        public static bool TryClaim(PlacedUnit unit) => NotifyUnitPlaced(unit);

        /// <summary>Number of currently claimed active outpost nodes.</summary>
        public static int ActiveClaimedCount
        {
            get
            {
                int c = 0;
                for (int i = 0; i < All.Count; i++)
                    if (All[i] != null && All[i].IsClaimed) c++;
                return c;
            }
        }

        /// <summary>
        /// Gives a newly spawned goblin a chance (RaidDivertChance = 35%) to divert toward an active claimed
        /// Outpost/Mine on the rim or secondary surface, before continuing to the Town Hall.
        /// Safe to call from MonsterUnit.Launch() — idempotent per MonsterUnit.Serial.
        /// </summary>
        public static bool TryDivertMonster(MonsterUnit monster)
        {
            if (monster == null || !monster.IsAlive || monster.Path == null) return false;
            if (!evaluatedMonsterSerials.Add(monster.Serial)) return false; // already evaluated this launch
            if (monster.Kind != MonsterKind.Normal) return false;           // normal goblins form raiding parties
            if (GoblinKingBoss.IsBoss(monster)) return false;               // never divert the boss

            var targetNode = PickClaimedRaidTarget();
            if (targetNode == null) return false;
            if (UnityEngine.Random.value > RaidDivertChance) return false;

            var gd = GameDirector.Instance;
            Vector3 hallPos = TownHall.Current != null ? TownHall.Current.transform.position :
                              (gd != null && gd.Hall != null ? gd.Hall.transform.position : monster.Path.End);
            ITableSurface hallSurface = gd != null && gd.Surface != null ? gd.Surface : monster.Path.Surface;
            if (hallSurface == null) return false;

            float floorY = RoomCornerSpawns.Existing != null ? RoomCornerSpawns.Existing.FloorY : hallSurface.Height - 0.72f;
            Vector3 spawnPos = monster.transform.position;
            bool roomRoute = monster.Path.HasFloorPart;

            SplinePath raidPath = BuildRaidSplinePath(spawnPos, targetNode, hallPos, hallSurface, floorY, roomRoute);
            if (raidPath == null) return false;

            // Assign diverted path by re-launching cleanly without recursive evaluation
            monster.Launch(raidPath, monster.Kind);
            evaluatedMonsterSerials.Add(monster.Serial);
            if (targetNode.IsSecondarySurface)
                secondarySurfaceRaiders[monster.Serial] = targetNode;

            if (VfxPool.Instance != null)
                VfxPool.Instance.Ring(targetNode.Position + targetNode.SurfaceUp * 0.01f, 0.11f);
            RaidingPartyDiverted?.Invoke(monster, targetNode);
            return true;
        }

        /// <summary>
        /// Builds a raiding party SplinePath that routes from `spawnPos` to a claimed outpost node and then to `hallPos`.
        /// </summary>
        public static bool TryBuildRaidPath(Vector3 spawnPos, Vector3 hallPos, ITableSurface hallSurface, float floorY, bool roomMode, out SplinePath raidPath, out ResourceOutpostNode targetNode)
        {
            raidPath = null;
            targetNode = PickClaimedRaidTarget();
            if (targetNode == null || UnityEngine.Random.value > RaidDivertChance) return false;
            raidPath = BuildRaidSplinePath(spawnPos, targetNode, hallPos, hallSurface, floorY, roomMode);
            return raidPath != null;
        }

        // ====================================================================
        // INTERNAL SPAWN PLACEMENT & PATH HELPERS
        // ====================================================================

        static ResourceOutpostNode CreateNode(Vector3 pos, ITableSurface surface, OutpostNodeKind kind, bool isSecondary)
        {
            var go = new GameObject($"ResourceOutpost_{kind}_{All.Count}");
            if (nodesRoot != null) go.transform.SetParent(nodesRoot, false);
            Vector3 up = surface != null ? surface.Pose.up : Vector3.up;
            Vector3 fwd = Vector3.ProjectOnPlane(Vector3.forward, up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.right;
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd.normalized, up));

            var node = go.AddComponent<ResourceOutpostNode>();
            node.InitNode(kind, surface, isSecondary);
            All.Add(node);
            return node;
        }

        static bool TryFindSecondarySurfacePoint(ITableSurface s, Vector3 hallPos, SurfaceRegistry reg, out Vector3 result)
        {
            result = default;
            Vector3 center = s.ProjectOnto(s.Pose.position);
            Vector3 up = s.Pose.up;
            Vector3 towardHall = Vector3.ProjectOnPlane(hallPos - center, up);
            if (towardHall.sqrMagnitude < 1e-4f) towardHall = s.Pose.rotation * Vector3.forward;
            towardHall.Normalize();

            // Prefer a point slightly toward the player/hall edge of the secondary furniture so it's easy to reach
            Vector3 edge = s.ClosestEdgePoint(center + towardHall * 1.5f);
            Vector3 inward = Vector3.ProjectOnPlane(center - edge, up);
            if (inward.sqrMagnitude > 1e-4f)
            {
                Vector3 edgeCand = s.ProjectOnto(edge + inward.normalized * 0.08f);
                if (s.IsFootprintSupported(edgeCand, 0.045f) && (reg == null || !reg.OverlapsObstacle(edgeCand, 0.045f, 0.08f)))
                {
                    result = edgeCand;
                    return true;
                }
            }

            if (s.IsPointSupported(center) && (reg == null || !reg.OverlapsObstacle(center, 0.045f, 0.08f)))
            {
                result = center;
                return true;
            }
            return false;
        }

        static List<Vector3> FindPrimaryRimPositions(Vector3 hallPos, ITableSurface surface, SurfaceRegistry reg, int count)
        {
            var picked = new List<Vector3>(count);
            if (surface == null || count <= 0) return picked;

            Vector3 up = surface.Pose.up;
            Vector3 right = Vector3.ProjectOnPlane(surface.Pose.rotation * Vector3.right, up).normalized;
            if (right.sqrMagnitude < 0.5f) right = Vector3.Cross(up, Vector3.forward).normalized;
            Vector3 fwd = Vector3.Cross(right, up).normalized;

            // Sample 16 directions around the table, scoring outer corners & rim spots far from the hall
            const int Samples = 16;
            var candidates = new List<Vector3>(Samples);
            var scores = new List<float>(Samples);

            for (int i = 0; i < Samples; i++)
            {
                float angle = i * Mathf.PI * 2f / Samples;
                Vector3 dir = right * Mathf.Cos(angle) + fwd * Mathf.Sin(angle);
                Vector3 edge = surface.ClosestEdgePoint(hallPos + dir * 2.5f);
                Vector3 inward = Vector3.ProjectOnPlane(surface.Pose.position - edge, up);
                if (inward.sqrMagnitude < 1e-5f) inward = -dir;
                inward.Normalize();

                Vector3 cand = surface.ProjectOnto(edge + inward * EdgeInset);
                if (!surface.IsFootprintSupported(cand, 0.048f))
                {
                    cand = surface.ProjectOnto(edge + inward * (EdgeInset + 0.025f));
                    if (!surface.IsPointSupported(cand)) continue;
                }

                float hallDist = Vector3.ProjectOnPlane(cand - hallPos, up).magnitude;
                if (hallDist < MinDistFromHall) continue;
                if (reg != null && reg.OverlapsObstacle(cand, 0.045f, 0.08f)) continue;

                // Corner affinity: diagonal angles (45, 135, 225, 315 deg) get a score boost
                float diag = Mathf.Abs(Mathf.Sin(angle * 2f));
                float score = hallDist + diag * 0.12f;
                candidates.Add(cand);
                scores.Add(score);
            }

            for (int n = 0; n < count; n++)
            {
                int bestIdx = -1;
                float bestScore = float.MinValue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    Vector3 c = candidates[i];
                    bool tooClose = false;
                    for (int k = 0; k < picked.Count; k++)
                    {
                        if (Vector3.ProjectOnPlane(picked[k] - c, up).magnitude < MinNodeSeparation)
                        {
                            tooClose = true;
                            break;
                        }
                    }
                    for (int k = 0; k < All.Count; k++)
                    {
                        if (All[k] != null && FlatDist(All[k].Position, c) < MinNodeSeparation)
                        {
                            tooClose = true;
                            break;
                        }
                    }
                    if (tooClose) continue;
                    if (scores[i] > bestScore)
                    {
                        bestScore = scores[i];
                        bestIdx = i;
                    }
                }
                if (bestIdx < 0) break;
                picked.Add(candidates[bestIdx]);
            }

            // Fallback if table is very small: place at supported perimeter points >= 0.16m from hall
            if (picked.Count == 0)
            {
                for (int i = 0; i < 4 && picked.Count < count; i++)
                {
                    Vector3 dir = Quaternion.AngleAxis(45f + i * 90f, up) * fwd;
                    Vector3 edge = surface.ClosestEdgePoint(hallPos + dir * 1.5f);
                    Vector3 inward = Vector3.ProjectOnPlane(hallPos - edge, up).normalized;
                    Vector3 p = surface.ProjectOnto(edge + inward * 0.06f);
                    if (surface.IsPointSupported(p) && Vector3.ProjectOnPlane(p - hallPos, up).magnitude >= 0.15f)
                        picked.Add(p);
                }
            }

            return picked;
        }

        static ResourceOutpostNode PickClaimedRaidTarget()
        {
            int count = All.Count;
            if (count == 0) return null;
            for (int i = 0; i < count; i++)
            {
                int idx = (nextRaidNodeCursor + i) % count;
                var n = All[idx];
                if (n != null && n.IsClaimed)
                {
                    nextRaidNodeCursor = (idx + 1) % count;
                    return n;
                }
            }
            return null;
        }

        static SplinePath BuildRaidSplinePath(Vector3 spawnPos, ResourceOutpostNode targetNode, Vector3 hallPos, ITableSurface hallSurface, float floorY, bool roomMode)
        {
            if (targetNode == null || hallSurface == null) return null;
            ITableSurface nodeSurface = targetNode.Surface != null ? targetNode.Surface : hallSurface;

            if (targetNode.IsSecondarySurface && nodeSurface != hallSurface)
            {
                // Divert to the secondary furniture surface outpost first; OutpostCoordinator reroutes to hall after pillage
                if (roomMode && ClimbRoutes.Instance != null)
                    return ClimbRoutes.Instance.BuildRoute(spawnPos, targetNode.Position, nodeSurface, floorY);
                return SplinePath.Build(spawnPos, targetNode.Position, nodeSurface);
            }

            // Primary table rim outpost: climb/enter near the outpost rim, attack the outpost/Mine, then proceed to Town Hall
            SplinePath path;
            if (roomMode && ClimbRoutes.Instance != null)
            {
                path = ClimbRoutes.Instance.BuildRoute(spawnPos, targetNode.Position, hallSurface, floorY);
                path.AddSurfaceLeg(hallPos);
                return path;
            }

            path = new SplinePath(hallSurface);
            path.Add(hallSurface.ProjectOnto(spawnPos), PathSegment.Surface, Vector3.up);
            path.AddSurfaceLeg(targetNode.Position);
            path.AddSurfaceLeg(hallPos);
            return path;
        }

        static ResourceOutpostNode FindNodeForUnit(PlacedUnit u)
        {
            if (u == null) return null;
            for (int i = 0; i < All.Count; i++)
            {
                var n = All[i];
                if (n != null && n.ClaimedUnit == u) return n;
            }
            return FindClosestNode(u.transform.position, ClaimRadius);
        }

        public static ResourceOutpostNode FindClosestNode(Vector3 worldPos, float maxRadius)
        {
            ResourceOutpostNode best = null;
            float bestSq = maxRadius * maxRadius;
            for (int i = 0; i < All.Count; i++)
            {
                var n = All[i];
                if (n == null) continue;
                Vector3 d = n.Position - worldPos;
                d.y *= 0.5f; // slightly forgiving vertical tolerance for table surface projection
                float sq = d.sqrMagnitude;
                if (sq <= bestSq)
                {
                    bestSq = sq;
                    best = n;
                }
            }
            return best;
        }

        static float FlatDist(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // ====================================================================
        // INSTANCE LIFECYCLE, CLAIMING & PROCEDURAL VISUALS
        // ====================================================================

        void InitNode(OutpostNodeKind kind, ITableSurface surface, bool isSecondary)
        {
            Kind = kind;
            Surface = surface;
            IsSecondarySurface = isSecondary;
            IsClaimed = false;
            ClaimedByPoke = false;
            ClaimedUnit = null;
            animPhase = UnityEngine.Random.value * 10f;
            BuildNodeVisuals();
        }

        void OnDestroy()
        {
            All.Remove(this);
        }

        /// <summary>Binds a placed unit (Mine or outpost structure) to this node and activates the 2x gold beacon.</summary>
        public void BindUnit(PlacedUnit unit)
        {
            if (unit == null) return;
            // If already claimed by a Mine, keep the Mine unless replaced by another Mine
            if (HasActiveMine && unit.type != UnitType.Mine) return;

            bool wasClaimed = IsClaimed;
            ClaimedUnit = unit;
            IsClaimed = true;
            SetBeaconActive(true);

            if (!wasClaimed)
            {
                OnClaimActivated();
            }
            else if (GameDirector.Instance != null)
            {
                GameDirector.Instance.RecomputeRates();
            }
        }

        /// <summary>Claims this node via direct hand finger-poke (and auto-spawns a Mine if on a secondary surface and affordable).</summary>
        public void ClaimByPoke()
        {
            if (IsClaimed) return;
            var gd = GameDirector.Instance;
            if (gd != null && !gd.IsCombatPhase) return;

            // On secondary furniture surfaces (where standard tray placement cannot reach), try auto-building a Mine if affordable
            if (IsSecondarySurface && gd != null && BuildingFactory.Instance != null)
            {
                var mineDef = UnitDef.Get(UnitType.Mine);
                if (mineDef != null && gd.TrySpend(mineDef.EffectiveCost, mineDef.EffectiveGoldCost))
                {
                    var mine = BuildingFactory.Instance.SpawnUnit(UnitType.Mine, Position, transform.rotation);
                    if (mine != null)
                    {
                        BindUnit(mine);
                        ProceduralAudio.Fire(Sfx.Hammer, Position);
                        return;
                    }
                }
            }

            IsClaimed = true;
            ClaimedByPoke = true;
            SetBeaconActive(true);
            if (!IsSecondarySurface && SettlementBuildArea.Instance != null && SettlementBuildArea.Instance.IsFounded)
                SettlementBuildArea.Instance.AddNode(Position);
            OnClaimActivated();
        }

        void OnClaimActivated()
        {
            beaconPulseTimer = 0f;
            pillageDamageAccum = 0f;
            Color fxCol = Kind == OutpostNodeKind.GoldenVein
                ? new Color(1f, 0.85f, 0.2f)
                : new Color(0.35f, 0.95f, 1f);
            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Ring(Position, 0.14f);
                VfxPool.Instance.Burst(Position + SurfaceUp * 0.05f, fxCol);
            }
            ProceduralAudio.Fire(Sfx.Coin, Position);
            if (GameDirector.Instance != null)
                GameDirector.Instance.RecomputeRates();
            NodeClaimed?.Invoke(this);
        }

        /// <summary>Called when raiding goblins destroy the outpost Mine or pillage an undefended claimed node.</summary>
        public void Pillage()
        {
            if (!IsClaimed) return;
            IsClaimed = false;
            ClaimedByPoke = false;
            ClaimedUnit = null;
            pillageDamageAccum = 0f;
            SetBeaconActive(false);

            if (VfxPool.Instance != null)
                VfxPool.Instance.Burst(Position + SurfaceUp * 0.06f, new Color(0.85f, 0.45f, 0.15f));
            ProceduralAudio.Fire(Sfx.Death, Position);

            if (GameDirector.Instance != null)
                GameDirector.Instance.RecomputeRates();
            NodePillaged?.Invoke(this);
        }

        void SetBeaconActive(bool active)
        {
            if (beaconRoot != null && beaconRoot.gameObject.activeSelf != active)
                beaconRoot.gameObject.SetActive(active);
        }

        void Update()
        {
            float dt = Time.deltaTime;
            animPhase += dt;
            if (pokeCooldown > 0f) pokeCooldown -= dt;

            // Keep build-area node registered on primary surface (even after SettlementBuildArea.ResetKeepHall on Restart)
            if (!IsSecondarySurface)
            {
                buildAreaSyncTimer -= dt;
                if (buildAreaSyncTimer <= 0f)
                {
                    buildAreaSyncTimer = 0.4f;
                    var area = SettlementBuildArea.Instance;
                    if (area != null && area.IsFounded && area.Surface == Surface)
                        area.AddNode(Position);
                }
            }

            // 1. Validate claimed unit status or auto-detect nearby placed units (Mine / outpost defenses)
            if (ClaimedUnit != null && (ClaimedUnit.health == null || ClaimedUnit.health.IsDead))
            {
                ClaimedUnit = null;
                if (!ClaimedByPoke)
                    Pillage();
            }

            ScanForNearbyPlacedUnits();

            // 2. Check hand index-finger poke to claim unclaimed nodes
            if (!IsClaimed && pokeCooldown <= 0f)
            {
                CheckFingerPokeClaim();
            }

            // 3. When claimed: emit periodic crystal/gold resonance & check if undefended poke-claimed node is pillaged by goblins
            if (IsClaimed)
            {
                beaconPulseTimer += dt;
                if (beaconPulseTimer >= 3.0f)
                {
                    beaconPulseTimer = 0f;
                    Color pulseCol = Kind == OutpostNodeKind.GoldenVein
                        ? new Color(1f, 0.84f, 0.22f)
                        : new Color(0.4f, 0.95f, 1f);
                    if (VfxPool.Instance != null)
                        VfxPool.Instance.Burst(Position + SurfaceUp * 0.09f, pulseCol);
                }

                // If claimed only by poke (no live PlacedUnit defending it), goblins reaching it can pillage it
                if (ClaimedUnit == null)
                {
                    var monsters = MonsterUnit.Active;
                    for (int i = 0; i < monsters.Count; i++)
                    {
                        var m = monsters[i];
                        if (m == null || !m.IsAlive) continue;
                        if ((m.transform.position - Position).sqrMagnitude <= 0.06f * 0.06f)
                        {
                            pillageDamageAccum += dt;
                            if (pillageDamageAccum >= 1.5f)
                            {
                                Pillage();
                                break;
                            }
                        }
                    }
                }
            }

            AnimateVisuals(dt);
        }

        void ScanForNearbyPlacedUnits()
        {
            var units = PlacedUnit.All;
            float r2 = ClaimRadius * ClaimRadius;
            PlacedUnit bestMine = null;
            PlacedUnit bestOutpost = null;

            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.health == null || u.health.IsDead) continue;
                Vector3 d = u.transform.position - Position;
                d.y *= 0.5f;
                if (d.sqrMagnitude > r2) continue;
                if (u.type == UnitType.Mine)
                {
                    bestMine = u;
                    break;
                }
                if (bestOutpost == null) bestOutpost = u;
            }

            if (bestMine != null && ClaimedUnit != bestMine)
            {
                BindUnit(bestMine);
            }
            else if (bestMine == null && bestOutpost != null && !IsClaimed)
            {
                BindUnit(bestOutpost);
            }
        }

        void CheckFingerPokeClaim()
        {
            var rig = HandRig.Instance;
            var gd = GameDirector.Instance;
            if (rig == null || (gd != null && !gd.IsCombatPhase)) return;

            float r2 = PokeClaimRadius * PokeClaimRadius;
            for (int h = 0; h < 2; h++)
            {
                var side = (HandSide)h;
                if (!rig.IsTracked(side) || !rig.TryGetIndexTip(side, out Vector3 tip)) continue;
                if ((tip - (Position + SurfaceUp * 0.025f)).sqrMagnitude <= r2)
                {
                    pokeCooldown = 0.6f;
                    ClaimByPoke();
                    return;
                }
            }
        }

        void AnimateVisuals(float dt)
        {
            if (crystalCore != null)
            {
                crystalCore.localPosition = new Vector3(0f, 0.028f + Mathf.Sin(animPhase * 2.6f) * 0.004f, 0f);
                crystalCore.Rotate(0f, 55f * dt, 0f, Space.Self);
            }

            for (int i = 0; i < orbiters.Count; i++)
            {
                var o = orbiters[i];
                if (o == null) continue;
                float a = animPhase * 2.2f + i * (Mathf.PI * 2f / Mathf.Max(1, orbiters.Count));
                float rad = IsClaimed ? 0.038f : 0.028f;
                float y = 0.024f + Mathf.Sin(animPhase * 3.4f + i) * 0.006f;
                o.localPosition = new Vector3(Mathf.Cos(a) * rad, y, Mathf.Sin(a) * rad);
                o.Rotate(90f * dt, 120f * dt, 0f, Space.Self);
            }

            if (IsClaimed && beaconRoot != null)
            {
                if (beaconCrystal != null)
                {
                    beaconCrystal.localPosition = new Vector3(0f, 0.115f + Mathf.Sin(animPhase * 3.2f) * 0.008f, 0f);
                    beaconCrystal.Rotate(0f, 110f * dt, 0f, Space.Self);
                }
                if (beaconRingLower != null)
                {
                    float s1 = 1f + Mathf.Sin(animPhase * 3.5f) * 0.12f;
                    beaconRingLower.localScale = new Vector3(0.11f * s1, 0.0015f, 0.11f * s1);
                }
                if (beaconRingUpper != null)
                {
                    float s2 = 1f + Mathf.Cos(animPhase * 3.5f) * 0.15f;
                    beaconRingUpper.localScale = new Vector3(0.075f * s2, 0.0015f, 0.075f * s2);
                }
            }
        }

        // ====================================================================
        // PROCEDURAL GEOMETRY (Collider-free via BuildingVisuals.Kit & Mats)
        // ====================================================================

        void BuildNodeVisuals()
        {
            if (visualRoot != null) Destroy(visualRoot.gameObject);
            orbiters.Clear();

            visualRoot = BuildingVisuals.NewRoot(transform, "OutpostNodeVisual");
            var k = new BuildingVisuals.Kit(visualRoot);

            Color goldBright = new Color(1f, 0.86f, 0.22f);
            Color goldDeep = new Color(0.92f, 0.65f, 0.10f);
            Color relicCyan = new Color(0.30f, 0.95f, 1.0f);
            Color stoneDark = new Color(0.28f, 0.27f, 0.30f);
            Color stoneMid = new Color(0.45f, 0.43f, 0.40f);

            Color auraCol = Kind == OutpostNodeKind.GoldenVein
                ? new Color(1f, 0.82f, 0.15f, 0.24f)
                : new Color(0.25f, 0.90f, 1f, 0.24f);

            // Soft glowing ground halo disc
            var halo = k.CylRot(new Vector3(0f, 0.0015f, 0f), 0.055f, 0.002f, auraCol, Vector3.zero);
            halo.GetComponent<MeshRenderer>().sharedMaterial = Mats.Transparent(auraCol);

            if (Kind == OutpostNodeKind.GoldenVein)
            {
                // Rocky basalt bed with protruding glowing golden crystal veins
                k.Ellipsoid(new Vector3(0f, 0.007f, 0f), new Vector3(0.068f, 0.016f, 0.062f), stoneDark);
                k.Ellipsoid(new Vector3(-0.016f, 0.011f, 0.010f), new Vector3(0.032f, 0.018f, 0.028f), stoneMid);
                k.Ellipsoid(new Vector3(0.018f, 0.010f, -0.012f), new Vector3(0.028f, 0.016f, 0.030f), stoneDark);

                // Jagged golden ore spikes
                Vector3[] spikePos =
                {
                    new Vector3(-0.014f, 0.008f, -0.008f),
                    new Vector3(0.015f, 0.008f, 0.009f),
                    new Vector3(0.004f, 0.010f, -0.016f),
                    new Vector3(-0.008f, 0.009f, 0.015f),
                };
                Vector3[] spikeRot =
                {
                    new Vector3(-18f, 25f, 15f),
                    new Vector3(20f, -40f, -18f),
                    new Vector3(-22f, 110f, 10f),
                    new Vector3(16f, 200f, -14f),
                };
                for (int i = 0; i < spikePos.Length; i++)
                {
                    var spike = k.Pyramid(spikePos[i], 0.007f, 0.024f + (i % 2) * 0.005f, i % 2 == 0 ? goldBright : goldDeep);
                    spike.transform.localRotation = Quaternion.Euler(spikeRot[i]);
                    if (i % 2 == 0) spike.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(goldBright);
                }

                // Hovering central golden crystal cluster
                crystalCore = BuildingVisuals.NewRoot(visualRoot, "GoldenCore");
                var kc = new BuildingVisuals.Kit(crystalCore);
                var upPyr = kc.Pyramid(Vector3.zero, 0.009f, 0.018f, goldBright);
                upPyr.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(goldBright);
                var downPyr = kc.Pyramid(Vector3.zero, 0.009f, 0.012f, goldDeep);
                downPyr.transform.localRotation = Quaternion.Euler(180f, 0f, 0f);
                downPyr.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(goldDeep);
            }
            else
            {
                // Ancient Relic: stepped temple stone pedestal + bronze rune ring + floating cyan-gold octahedron
                k.Box(new Vector3(0f, 0.004f, 0f), new Vector3(0.062f, 0.008f, 0.062f), stoneMid);
                k.Box(new Vector3(0f, 0.010f, 0f), new Vector3(0.044f, 0.006f, 0.044f), stoneDark);
                k.Cyl(new Vector3(0f, 0.013f, 0f), 0.018f, 0.004f, goldDeep);

                for (int c = 0; c < 4; c++)
                {
                    float a = (45f + c * 90f) * Mathf.Deg2Rad;
                    Vector3 pillarPos = new Vector3(Mathf.Cos(a) * 0.024f, 0.008f, Mathf.Sin(a) * 0.024f);
                    k.Cyl(pillarPos, 0.0035f, 0.018f, stoneMid);
                    var tip = k.Ball(pillarPos + Vector3.up * 0.020f, 0.0065f, relicCyan);
                    tip.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(relicCyan);
                }

                crystalCore = BuildingVisuals.NewRoot(visualRoot, "RelicCore");
                var kc = new BuildingVisuals.Kit(crystalCore);
                var topHalf = kc.Pyramid(Vector3.zero, 0.010f, 0.018f, relicCyan);
                topHalf.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(relicCyan);
                var botHalf = kc.Pyramid(Vector3.zero, 0.010f, 0.014f, goldBright);
                botHalf.transform.localRotation = Quaternion.Euler(180f, 0f, 0f);
                botHalf.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(goldBright);
            }

            // Orbiting glowing motes
            Color moteCol = Kind == OutpostNodeKind.GoldenVein ? goldBright : relicCyan;
            for (int i = 0; i < 3; i++)
            {
                var mote = k.Ball(new Vector3(0.025f, 0.024f, 0f), 0.0065f, moteCol);
                mote.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(moteCol);
                orbiters.Add(mote.transform);
            }

            // Claimed Beacon hierarchy (activated when a Mine/outpost is placed or node is poked)
            beaconRoot = BuildingVisuals.NewRoot(visualRoot, "ClaimedBeacon");
            var kb = new BuildingVisuals.Kit(beaconRoot);

            Color beamCol = Kind == OutpostNodeKind.GoldenVein
                ? new Color(1f, 0.88f, 0.25f, 0.30f)
                : new Color(0.30f, 0.95f, 1f, 0.30f);
            Color ringCol = Kind == OutpostNodeKind.GoldenVein
                ? new Color(1f, 0.90f, 0.30f, 0.50f)
                : new Color(0.45f, 0.98f, 1f, 0.50f);
            Color coreCol = Kind == OutpostNodeKind.GoldenVein ? goldBright : relicCyan;

            // Vertical sky-beam column
            var beam = kb.Cyl(new Vector3(0f, 0.01f, 0f), 0.016f, 0.20f, beamCol);
            beam.GetComponent<MeshRenderer>().sharedMaterial = Mats.Transparent(beamCol);

            // Pulsing base and mid rings
            var rLow = kb.CylRot(new Vector3(0f, 0.004f, 0f), 0.055f, 0.003f, ringCol, Vector3.zero);
            rLow.GetComponent<MeshRenderer>().sharedMaterial = Mats.Transparent(ringCol);
            beaconRingLower = rLow.transform;

            var rHigh = kb.CylRot(new Vector3(0f, 0.085f, 0f), 0.038f, 0.003f, ringCol, Vector3.zero);
            rHigh.GetComponent<MeshRenderer>().sharedMaterial = Mats.Transparent(ringCol);
            beaconRingUpper = rHigh.transform;

            // Elevated radiant crystal beacon diamond above the Mine
            beaconCrystal = BuildingVisuals.NewRoot(beaconRoot, "BeaconCrystal");
            beaconCrystal.localPosition = new Vector3(0f, 0.115f, 0f);
            var kbc = new BuildingVisuals.Kit(beaconCrystal);
            var bTop = kbc.Pyramid(Vector3.zero, 0.014f, 0.028f, coreCol);
            bTop.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(coreCol);
            var bBot = kbc.Pyramid(Vector3.zero, 0.014f, 0.020f, goldBright);
            bBot.transform.localRotation = Quaternion.Euler(180f, 0f, 0f);
            bBot.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(goldBright);

            beaconRoot.gameObject.SetActive(false);
        }

        // ====================================================================
        // AUTO-COORDINATOR (Hooks GameDirector phases & MonsterUnit raid diversion)
        // ====================================================================

        sealed class OutpostCoordinator : MonoBehaviour
        {
            static OutpostCoordinator instance;
            static bool quitting;
            bool subscribed;
            Vector3 lastFoundedHallPos;

            public static void Ensure()
            {
                if (instance != null || quitting || !Application.isPlaying) return;
                var go = new GameObject("ResourceOutpostCoordinator");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<OutpostCoordinator>();
            }

            void OnApplicationQuit() => quitting = true;

            void OnDestroy()
            {
                Unsubscribe();
                if (instance == this) instance = null;
            }

            void Update()
            {
                TrySubscribe();

                var gd = GameDirector.Instance;
                if (gd == null) return;

                // Ensure nodes exist once the Town Hall is placed in a combat phase
                if (gd.IsCombatPhase && gd.Hall != null && gd.Surface != null && All.Count == 0)
                {
                    lastFoundedHallPos = gd.Hall.transform.position;
                    SpawnForSession(lastFoundedHallPos, gd.Surface);
                }

                // Evaluate any newly launched monsters for raiding party diversion (idempotent with TryDivertMonster)
                if (gd.Phase == GamePhase.Wave && ActiveClaimedCount > 0)
                {
                    var active = MonsterUnit.Active;
                    for (int i = 0; i < active.Count; i++)
                    {
                        var m = active[i];
                        if (m == null || !m.IsAlive) continue;
                        if (!evaluatedMonsterSerials.Contains(m.Serial))
                        {
                            TryDivertMonster(m);
                        }
                        else if (secondarySurfaceRaiders.TryGetValue(m.Serial, out var secNode))
                        {
                            // If a secondary-surface outpost was destroyed/pillaged, reroute the raider back to the Town Hall
                            if (secNode == null || !secNode.IsClaimed)
                            {
                                secondarySurfaceRaiders.Remove(m.Serial);
                                RerouteRaiderToHall(m, gd);
                            }
                        }
                    }
                }
            }

            static void RerouteRaiderToHall(MonsterUnit m, GameDirector gd)
            {
                if (m == null || !m.IsAlive || gd == null || gd.Hall == null || gd.Surface == null) return;
                float floorY = RoomCornerSpawns.Existing != null ? RoomCornerSpawns.Existing.FloorY : gd.Surface.Height - 0.72f;
                float curHp = m.health != null ? m.health.Current : MonsterUnit.NormalHp;
                SplinePath toHall = ClimbRoutes.Instance != null
                    ? ClimbRoutes.Instance.BuildRoute(m.transform.position, gd.Hall.transform.position, gd.Surface, floorY)
                    : SplinePath.Build(m.transform.position, gd.Hall.transform.position, gd.Surface);
                m.Launch(toHall, m.Kind);
                evaluatedMonsterSerials.Add(m.Serial);
                if (m.health != null && curHp < m.health.Max)
                    m.health.Damage(m.health.Max - curHp);
            }

            void TrySubscribe()
            {
                if (subscribed || GameDirector.Instance == null) return;
                GameDirector.Instance.PhaseChanged += OnPhaseChanged;
                GameDirector.Instance.GameReset += OnGameReset;
                subscribed = true;
            }

            void Unsubscribe()
            {
                if (!subscribed || GameDirector.Instance == null) return;
                GameDirector.Instance.PhaseChanged -= OnPhaseChanged;
                GameDirector.Instance.GameReset -= OnGameReset;
                subscribed = false;
            }

            void OnPhaseChanged(GamePhase phase)
            {
                if (phase == GamePhase.CivilizationSetup || phase == GamePhase.HeadquartersPlacement)
                {
                    ClearAll();
                }
            }

            void OnGameReset()
            {
                var gd = GameDirector.Instance;
                if (gd != null && gd.Hall != null && gd.Surface != null)
                {
                    SpawnForSession(gd.Hall.transform.position, gd.Surface);
                }
                else
                {
                    ClearAll();
                }
            }
        }
    }
}
