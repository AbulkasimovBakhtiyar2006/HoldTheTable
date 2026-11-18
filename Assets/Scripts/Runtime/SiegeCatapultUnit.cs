using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    public enum SiegeThreatKind { GoblinCatapult, GoblinShaman }

    /// <summary>
    /// Feature 5A: Ranged Siege Threats (Goblin Catapults &amp; Goblin Shamans).
    /// Wraps a pooled <see cref="MonsterUnit"/> (with <c>Path == null</c> so <see cref="SiegeCatapultUnit"/> controls
    /// rim/furniture positioning while remaining in <see cref="MonsterUnit.Active"/> for ArcherTowers, Focus Fire,
    /// Defenders, Shockwave, ScoreKeeper, and WaveSpawner).
    /// Stops at the table edge or on nearby secondary furniture and bombards player walls and towers from afar
    /// with high-arcing stone and fire projectiles.
    /// </summary>
    public class SiegeCatapultUnit : MonoBehaviour
    {
        public const float CatapultHp = 16f;
        public const float ShamanHp = 10f;
        public const float MoveSpeed = 0.065f;
        public const float ClimbSpeed = 0.048f;
        public const float BombardRange = 1.35f;
        public const float MinStandOffDist = 0.24f;
        public const float PreferredRimInset = 0.042f;

        public const float CatapultDamage = 5.0f;
        public const float ShamanDamage = 3.5f;
        public const float CatapultFireInterval = 3.2f;
        public const float ShamanFireInterval = 2.5f;
        public const float ProjectileSpeed = 0.58f;
        public const float SplashRadius = 0.055f;
        public const float SplashDamage = 1.8f;

        public static readonly List<SiegeCatapultUnit> Active = new List<SiegeCatapultUnit>();
        public static event Action<SiegeCatapultUnit> Deployed;
        public static event Action<SiegeCatapultUnit> Killed;

        static int lastWaveSpawnFrame = -1;
        static int lastProjectileUpdateFrame = -1;
        static Transform projectilePoolRoot;

        sealed class SiegeBombProjectile
        {
            public Transform root;
            public Transform boulderVis;
            public Transform fireballVis;
            public Vector3 from;
            public Vector3 to;
            public PlacedUnit targetUnit;
            public TownHall targetHall;
            public float damage;
            public float arcHeight;
            public float duration;
            public float t;
            public SiegeThreatKind kind;
            public bool active;
        }

        static readonly List<SiegeBombProjectile> projectiles = new List<SiegeBombProjectile>();

        public MonsterUnit Monster { get; private set; }
        public Health health => Monster != null ? Monster.health : null;
        public SiegeThreatKind ThreatKind { get; private set; }
        public bool IsCatapult => ThreatKind == SiegeThreatKind.GoblinCatapult;
        public bool IsShaman => ThreatKind == SiegeThreatKind.GoblinShaman;
        public bool IsDeployed => isDeployed;
        public bool IsAlive => launched && !falling && Monster != null && Monster.IsAlive;
        public Vector3 AimPoint => transform.position + transform.up * (IsCatapult ? 0.028f : 0.022f);

        Transform siegeVisualRoot;
        Transform catapultVisual;
        Transform shamanVisual;
        Transform catapultArmPivot;
        Transform catapultPayloadBall;
        Transform shamanStaffPivot;
        Transform shamanOrbBall;

        SplinePath approachPath;
        float pathDist;
        ITableSurface activeSurface;
        Vector3 hallBasePos;
        Vector3 deployTargetPos;
        bool useApproachSpline;
        bool isDeployed;
        bool launched;

        float fireTimer;
        float fireAnimT;
        float bobPhase;
        float pushTime;
        Vector3 pushVel;
        bool falling;
        float fallT;
        Vector3 fallVel;
        bool shockwaveHooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active.Clear();
            Deployed = null;
            Killed = null;
            projectiles.Clear();
            projectilePoolRoot = null;
            lastWaveSpawnFrame = -1;
            lastProjectileUpdateFrame = -1;
        }

        // ------------------------------------------------------------ spawning API

        /// <summary>
        /// Spawns wave-appropriate Ranged Siege Threats (Goblin Catapults &amp; Shamans) that position on
        /// nearby furniture or stop at the outer table rim to bombard walls and towers. Idempotent per frame.
        /// </summary>
        public static void SpawnWaveThreats(int waveIndex, Vector3 hallPos, ITableSurface surface)
        {
            if (Time.frameCount == lastWaveSpawnFrame) return;
            lastWaveSpawnFrame = Time.frameCount;

            int count = waveIndex <= 0 ? 1 : waveIndex == 1 ? 2 : Mathf.Min(4, 2 + (waveIndex - 1) / 2);
            var rcs = RoomCornerSpawns.Existing;
            bool hasRoom = rcs != null && rcs.HasRoom && rcs.Corners != null && rcs.Corners.Count > 0;

            for (int i = 0; i < count; i++)
            {
                SiegeThreatKind kind = (waveIndex == 0 && i == 0)
                    ? SiegeThreatKind.GoblinShaman
                    : (i % 2 == 0 ? SiegeThreatKind.GoblinCatapult : SiegeThreatKind.GoblinShaman);

                // First check if there is a nearby secondary furniture surface in the room within siege range
                if (hasRoom && i % 2 == 1 && TryPickSecondaryFurnitureVantage(hallPos, surface, rcs.FloorY, i, out ITableSurface furnSurf, out Vector3 furnPos))
                {
                    SpawnOnSurfaceVantage(furnPos, hallPos, furnSurf, kind);
                    continue;
                }

                Vector3 spawnPos;
                if (hasRoom)
                {
                    Vector3 portal = rcs.Corners[i % rcs.Corners.Count];
                    Vector3 toHall = hallPos - portal;
                    toHall.y = 0f;
                    Vector3 side = Vector3.Cross(Vector3.up, toHall.sqrMagnitude > 1e-4f ? toHall.normalized : Vector3.forward);
                    spawnPos = portal + side * ((i - (count - 1) * 0.5f) * 0.16f);
                    spawnPos.y = rcs.FloorY;
                }
                else
                {
                    float angle = (i * (360f / Mathf.Max(1, count)) + waveIndex * 65f + 110f) * Mathf.Deg2Rad;
                    Vector3 dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    if (surface != null)
                    {
                        Vector3 edge = surface.ClosestEdgePoint(surface.ProjectOnto(hallPos + dir * 1.2f));
                        Vector3 inward = hallPos - edge;
                        inward.y = 0f;
                        if (inward.sqrMagnitude < 1e-6f) inward = -dir;
                        spawnPos = surface.ProjectOnto(edge + inward.normalized * 0.02f);
                    }
                    else
                    {
                        spawnPos = hallPos + dir * 0.44f;
                    }
                }

                Spawn(spawnPos, hallPos, surface, kind);
            }
        }

        /// <summary>Convenience hook matching Action&lt;int&gt; (e.g. GameDirector.WaveStarted).</summary>
        public static void SpawnForWave(int waveIndex)
        {
            var gd = GameDirector.Instance;
            var hall = TownHall.Current != null ? TownHall.Current : (gd != null ? gd.Hall : null);
            if (hall == null) return;
            ITableSurface surf = gd != null ? gd.Surface : null;
            SpawnWaveThreats(waveIndex, hall.transform.position, surf);
        }

        /// <summary>
        /// Spawns a single SiegeCatapultUnit (GoblinCatapult or GoblinShaman) backed by a pooled MonsterUnit.
        /// </summary>
        public static SiegeCatapultUnit Spawn(Vector3 spawnPos, Vector3 hallPos, ITableSurface surface, SiegeThreatKind kind = SiegeThreatKind.GoblinCatapult)
        {
            var m = MonsterUnit.Get();
            if (m == null) return null;

            m.Launch(null, kind == SiegeThreatKind.GoblinCatapult ? MonsterKind.Ram : MonsterKind.Normal);

            var sc = m.GetComponent<SiegeCatapultUnit>();
            if (sc == null) sc = m.gameObject.AddComponent<SiegeCatapultUnit>();
            sc.enabled = true;
            sc.Init(m, spawnPos, hallPos, surface, kind, false);
            return sc;
        }

        static SiegeCatapultUnit SpawnOnSurfaceVantage(Vector3 vantagePos, Vector3 hallPos, ITableSurface furnitureSurface, SiegeThreatKind kind)
        {
            var m = MonsterUnit.Get();
            if (m == null) return null;

            m.Launch(null, kind == SiegeThreatKind.GoblinCatapult ? MonsterKind.Ram : MonsterKind.Normal);

            var sc = m.GetComponent<SiegeCatapultUnit>();
            if (sc == null) sc = m.gameObject.AddComponent<SiegeCatapultUnit>();
            sc.enabled = true;
            sc.Init(m, vantagePos, hallPos, furnitureSurface, kind, true);
            return sc;
        }

        static bool TryPickSecondaryFurnitureVantage(Vector3 hallPos, ITableSurface hallSurface, float floorY, int seedIdx, out ITableSurface pickedSurface, out Vector3 vantagePos)
        {
            pickedSurface = null;
            vantagePos = default;
            var reg = SurfaceRegistry.Instance;
            if (reg == null || reg.Surfaces == null) return false;

            string hallId = hallSurface != null ? hallSurface.Id : null;
            int matchCount = 0;
            for (int i = 0; i < reg.Surfaces.Count; i++)
            {
                var s = reg.Surfaces[i];
                if (s == null || s.IsFallback || s.Id == hallId) continue;
                if (s.Pose.position.y <= floorY + 0.15f) continue;

                Vector3 edge = s.ClosestEdgePoint(s.ProjectOnto(hallPos));
                Vector3 inward = s.Pose.position - edge;
                inward.y = 0f;
                Vector3 candidate = s.ProjectOnto(edge + (inward.sqrMagnitude > 1e-5f ? inward.normalized * 0.045f : Vector3.zero));
                if (!s.IsPointSupported(candidate)) candidate = s.ProjectOnto(s.Pose.position);

                float dist = Vector3.Distance(candidate, hallPos);
                if (dist >= MinStandOffDist && dist <= BombardRange * 0.9f)
                {
                    pickedSurface = s;
                    vantagePos = candidate;
                    if (matchCount == (seedIdx % 3)) return true;
                    matchCount++;
                }
            }
            return pickedSurface != null;
        }

        public static void DespawnAll()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var sc = Active[i];
                if (sc == null) continue;
                sc.CleanupAndDisable();
            }
            Active.Clear();
            ClearAllProjectiles();
        }

        static void ClearAllProjectiles()
        {
            for (int i = 0; i < projectiles.Count; i++)
            {
                var p = projectiles[i];
                p.active = false;
                if (p.root != null) p.root.gameObject.SetActive(false);
            }
        }

        // ------------------------------------------------------------ setup & visuals

        void Init(MonsterUnit monster, Vector3 spawnPos, Vector3 hallPos, ITableSurface surface, SiegeThreatKind kind, bool directFurnitureVantage)
        {
            Monster = monster;
            ThreatKind = kind;
            activeSurface = surface;
            hallBasePos = hallPos;
            transform.localScale = Vector3.one;
            bobPhase = UnityEngine.Random.value * 8f;
            fireTimer = kind == SiegeThreatKind.GoblinCatapult ? 1.4f : 1.1f;
            fireAnimT = 0f;
            pushTime = 0f;
            falling = false;
            fallT = 0f;
            isDeployed = false;
            launched = true;

            var rcs = RoomCornerSpawns.Existing;
            bool roomMode = !directFurnitureVantage && rcs != null && rcs.HasRoom && Mathf.Abs(spawnPos.y - rcs.FloorY) < 0.08f;

            if (roomMode && surface != null)
            {
                // Build obstacle-aware route from floor portal up onto the settlement surface rim
                approachPath = SplinePath.BuildRoute(spawnPos, hallPos, surface, rcs.FloorY);
                useApproachSpline = approachPath != null;
                pathDist = 0f;
                transform.position = useApproachSpline ? approachPath.EvaluateDistance(0f) : spawnPos;
                deployTargetPos = ComputeRimStopPoint(spawnPos, hallPos, surface);
            }
            else if (directFurnitureVantage)
            {
                useApproachSpline = false;
                approachPath = null;
                transform.position = spawnPos;
                deployTargetPos = spawnPos;
                isDeployed = true;
                if (VfxPool.Instance != null) VfxPool.Instance.Ring(spawnPos + Vector3.up * 0.01f, 0.14f);
            }
            else
            {
                useApproachSpline = false;
                approachPath = null;
                deployTargetPos = ComputeRimStopPoint(spawnPos, hallPos, surface);
                transform.position = surface != null ? surface.ProjectOnto(spawnPos) : spawnPos;
            }

            Vector3 toHall = hallPos - transform.position;
            toHall.y = 0f;
            if (toHall.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(toHall.normalized, SurfaceUp());

            if (Monster.health != null)
            {
                Monster.health.Init(kind == SiegeThreatKind.GoblinCatapult ? CatapultHp : ShamanHp);
                Monster.health.Died -= OnMonsterDied;
                Monster.health.Died += OnMonsterDied;
            }

            SetGroundVisualsActive(false);
            EnsureVisuals();
            siegeVisualRoot.gameObject.SetActive(true);
            siegeVisualRoot.localPosition = Vector3.zero;
            siegeVisualRoot.localRotation = Quaternion.identity;
            catapultVisual.gameObject.SetActive(kind == SiegeThreatKind.GoblinCatapult);
            shamanVisual.gameObject.SetActive(kind == SiegeThreatKind.GoblinShaman);
            if (catapultPayloadBall != null) catapultPayloadBall.gameObject.SetActive(true);
            if (catapultArmPivot != null) catapultArmPivot.localRotation = Quaternion.Euler(-18f, 0f, 0f);

            if (!Active.Contains(this)) Active.Add(this);
            TryHookShockwave();
        }

        static Vector3 ComputeRimStopPoint(Vector3 fromPos, Vector3 hallPos, ITableSurface surface)
        {
            if (surface == null)
            {
                Vector3 dir = fromPos - hallPos;
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-5f) dir = Vector3.back;
                return hallPos + dir.normalized * 0.38f;
            }
            Vector3 up = surface.Pose.up;
            Vector3 projFrom = surface.ProjectOnto(fromPos);
            Vector3 edge = surface.ClosestEdgePoint(projFrom);
            Vector3 inward = Vector3.ProjectOnPlane(hallPos - edge, up);
            if (inward.sqrMagnitude < 1e-6f) inward = Vector3.forward;
            Vector3 stop = surface.ProjectOnto(edge + inward.normalized * PreferredRimInset);
            if (!surface.IsPointSupported(stop))
                stop = surface.ProjectOnto(Vector3.Lerp(edge, hallPos, 0.22f));
            return stop;
        }

        void SetGroundVisualsActive(bool active)
        {
            var bob = transform.Find("Bob");
            if (bob == null) return;
            for (int i = 0; i < bob.childCount; i++)
                bob.GetChild(i).gameObject.SetActive(active);
        }

        void EnsureVisuals()
        {
            if (siegeVisualRoot != null) return;
            siegeVisualRoot = BuildingVisuals.NewRoot(transform, "SiegeVisualRoot");
            catapultVisual = BuildCatapultVisual(siegeVisualRoot, out catapultArmPivot, out catapultPayloadBall);
            shamanVisual = BuildShamanVisual(siegeVisualRoot, out shamanStaffPivot, out shamanOrbBall);
        }

        static Transform BuildCatapultVisual(Transform parent, out Transform armPivot, out Transform payloadBall)
        {
            var root = BuildingVisuals.NewRoot(parent, "GoblinCatapult");
            var k = new BuildingVisuals.Kit(root);

            Color wood = new Color(0.46f, 0.30f, 0.15f);
            Color darkWood = new Color(0.27f, 0.17f, 0.08f);
            Color iron = new Color(0.28f, 0.29f, 0.32f);
            Color ember = new Color(1f, 0.48f, 0.12f);
            Color goblinSkin = new Color(0.36f, 0.62f, 0.24f);

            // Chassis side rails & crossbars
            k.Box(new Vector3(-0.014f, 0.010f, 0f), new Vector3(0.005f, 0.006f, 0.056f), wood);
            k.Box(new Vector3(0.014f, 0.010f, 0f), new Vector3(0.005f, 0.006f, 0.056f), wood);
            k.Box(new Vector3(0f, 0.010f, 0.022f), new Vector3(0.032f, 0.005f, 0.006f), darkWood);
            k.Box(new Vector3(0f, 0.010f, -0.022f), new Vector3(0.032f, 0.005f, 0.006f), darkWood);

            // 4 Siege Wheels
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    k.CylRot(new Vector3(sx * 0.019f, 0.009f, sz * 0.018f), 0.009f, 0.005f, darkWood, new Vector3(0f, 0f, 90f));
                    k.CylRot(new Vector3(sx * 0.021f, 0.009f, sz * 0.018f), 0.0035f, 0.006f, iron, new Vector3(0f, 0f, 90f));
                }
            }

            // A-frame upright posts & crossbar
            k.Box(new Vector3(-0.014f, 0.023f, 0.006f), new Vector3(0.0045f, 0.024f, 0.005f), darkWood, new Vector3(10f, 0f, 0f));
            k.Box(new Vector3(0.014f, 0.023f, 0.006f), new Vector3(0.0045f, 0.024f, 0.005f), darkWood, new Vector3(10f, 0f, 0f));
            k.CylRot(new Vector3(0f, 0.032f, 0.008f), 0.003f, 0.034f, iron, new Vector3(0f, 0f, 90f));
            k.CylRot(new Vector3(0f, 0.014f, 0.005f), 0.0035f, 0.030f, darkWood, new Vector3(0f, 0f, 90f));

            // Articulated throwing arm
            armPivot = BuildingVisuals.NewRoot(root, "ThrowingArm");
            armPivot.localPosition = new Vector3(0f, 0.014f, 0.005f);
            armPivot.localRotation = Quaternion.Euler(-18f, 0f, 0f);
            var ka = new BuildingVisuals.Kit(armPivot);
            ka.Box(new Vector3(0f, 0.002f, -0.016f), new Vector3(0.0045f, 0.0045f, 0.044f), wood);
            ka.Cyl(new Vector3(0f, 0.004f, -0.034f), 0.0085f, 0.004f, iron);

            // Loaded fiery boulder in spoon
            var ballGo = ka.Ball(new Vector3(0f, 0.011f, -0.034f), 0.013f, ember);
            ballGo.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(ember);
            payloadBall = ballGo.transform;

            // Goblin crewman next to catapult
            k.Ellipsoid(new Vector3(-0.024f, 0.014f, -0.012f), new Vector3(0.016f, 0.020f, 0.016f), goblinSkin);
            k.Ball(new Vector3(-0.024f, 0.026f, -0.012f), 0.012f, iron);

            return root;
        }

        static Transform BuildShamanVisual(Transform parent, out Transform staffPivot, out Transform orbBall)
        {
            var root = BuildingVisuals.NewRoot(parent, "GoblinShaman");
            var k = new BuildingVisuals.Kit(root);

            Color skin = new Color(0.34f, 0.58f, 0.22f);
            Color robe = new Color(0.28f, 0.14f, 0.32f);
            Color bone = new Color(0.90f, 0.87f, 0.78f);
            Color wood = new Color(0.42f, 0.26f, 0.12f);
            Color fireGlow = new Color(0.35f, 1f, 0.35f);

            // Robed body & skull mask
            k.Ellipsoid(new Vector3(0f, 0.016f, 0f), new Vector3(0.026f, 0.030f, 0.024f), robe);
            k.Ellipsoid(new Vector3(0f, 0.018f, 0.004f), new Vector3(0.020f, 0.022f, 0.020f), skin);
            k.Ellipsoid(new Vector3(0f, 0.031f, 0.003f), new Vector3(0.024f, 0.018f, 0.024f), bone);
            k.Ball(new Vector3(-0.0055f, 0.030f, 0.013f), 0.005f, fireGlow).GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(fireGlow);
            k.Ball(new Vector3(0.0055f, 0.030f, 0.013f), 0.005f, fireGlow).GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(fireGlow);

            // Skull horns
            k.Cone(new Vector3(-0.010f, 0.037f, 0.002f), 0.003f, 0.013f, bone).transform.localRotation = Quaternion.Euler(0f, 0f, 35f);
            k.Cone(new Vector3(0.010f, 0.037f, 0.002f), 0.003f, 0.013f, bone).transform.localRotation = Quaternion.Euler(0f, 0f, -35f);

            // Shaman totem staff
            staffPivot = BuildingVisuals.NewRoot(root, "ShamanStaff");
            staffPivot.localPosition = new Vector3(0.015f, 0.014f, 0.006f);
            var ks = new BuildingVisuals.Kit(staffPivot);
            ks.Cyl(new Vector3(0f, -0.012f, 0f), 0.002f, 0.044f, wood);
            var orbGo = ks.Ball(new Vector3(0f, 0.036f, 0f), 0.011f, new Color(1f, 0.55f, 0.15f));
            orbGo.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(new Color(1f, 0.6f, 0.15f));
            orbBall = orbGo.transform;

            return root;
        }

        // ------------------------------------------------------------ lifecycle

        void OnEnable()
        {
            TryHookShockwave();
        }

        void OnDisable()
        {
            CleanupAndDisable();
        }

        void OnDestroy()
        {
            UnhookShockwave();
            Active.Remove(this);
            if (Monster != null && Monster.health != null)
                Monster.health.Died -= OnMonsterDied;
        }

        void TryHookShockwave()
        {
            if (shockwaveHooked || ShockwaveAbility.Instance == null) return;
            ShockwaveAbility.Instance.Fired += OnGlobalShockwaveFired;
            shockwaveHooked = true;
        }

        void UnhookShockwave()
        {
            if (!shockwaveHooked) return;
            if (ShockwaveAbility.Instance != null)
                ShockwaveAbility.Instance.Fired -= OnGlobalShockwaveFired;
            shockwaveHooked = false;
        }

        void OnGlobalShockwaveFired()
        {
            if (!IsAlive) return;
            var sw = ShockwaveAbility.Instance;
            var gd = GameDirector.Instance;
            Vector3 center = gd != null && gd.Hall != null ? gd.Hall.transform.position : hallBasePos;
            float rad = sw != null ? sw.radius : 0.6f;
            float pushDist = sw != null ? sw.push : 0.12f;
            Vector3 d = transform.position - center;
            d.y = 0f;
            if (d.sqrMagnitude <= rad * rad)
            {
                Vector3 dir = d.sqrMagnitude > 1e-6f ? d.normalized : -transform.forward;
                Push(dir, pushDist);
            }
        }

        void OnMonsterDied(Health h)
        {
            Killed?.Invoke(this);
        }

        void CleanupAndDisable()
        {
            launched = false;
            isDeployed = false;
            falling = false;
            approachPath = null;
            UnhookShockwave();
            Active.Remove(this);
            if (Monster != null && Monster.health != null)
                Monster.health.Died -= OnMonsterDied;
            if (siegeVisualRoot != null)
                siegeVisualRoot.gameObject.SetActive(false);
            enabled = false;
        }

        // ------------------------------------------------------------ shockwave & queries

        public void Push(Vector3 worldDir, float meters)
        {
            if (!IsAlive) return;
            Vector3 up = SurfaceUp();
            worldDir -= Vector3.Dot(worldDir, up) * up;
            if (worldDir.sqrMagnitude < 1e-6f) return;
            pushTime = 0.22f;
            pushVel = worldDir.normalized * (meters / 0.22f);
        }

        public static void OnShockwave(Vector3 center, float radius, float push = 0.14f)
        {
            float r2 = radius * radius;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var sc = Active[i];
                if (sc == null || !sc.IsAlive) continue;
                Vector3 d = sc.transform.position - center;
                d.y = 0f;
                if (d.sqrMagnitude > r2) continue;
                Vector3 dir = d.sqrMagnitude > 1e-6f ? d.normalized : -sc.transform.forward;
                sc.Push(dir, push);
            }
        }

        public static SiegeCatapultUnit FindClosestInRange(Vector3 origin, float range)
        {
            float best = range * range;
            SiegeCatapultUnit found = null;
            for (int i = 0; i < Active.Count; i++)
            {
                var sc = Active[i];
                if (sc == null || !sc.IsAlive) continue;
                float d2 = (sc.transform.position - origin).sqrMagnitude;
                if (d2 <= best)
                {
                    best = d2;
                    found = sc;
                }
            }
            return found;
        }

        Vector3 SurfaceUp() => activeSurface != null ? activeSurface.Pose.rotation * Vector3.up : Vector3.up;

        // ------------------------------------------------------------ update & bombardment

        void Update()
        {
            UpdateAllProjectiles();

            if (!launched || Monster == null) return;
            if (!shockwaveHooked) TryHookShockwave();

            float dt = Time.deltaTime;
            Vector3 pos = transform.position;

            if (falling)
            {
                fallVel += Physics.gravity * dt;
                transform.position = pos + fallVel * dt;
                transform.Rotate(320f * dt, 0f, 180f * dt, Space.Self);
                fallT += dt;
                if (fallT > 0.6f && Monster.health != null && !Monster.health.IsDead)
                    Monster.health.Damage(9999f);
                return;
            }

            if (!Monster.IsAlive) return;

            var hall = TownHall.Current != null ? TownHall.Current : (GameDirector.Instance != null ? GameDirector.Instance.Hall : null);
            if (hall != null) hallBasePos = hall.transform.position;

            if (pushTime > 0f)
            {
                float step = Mathf.Min(dt, pushTime);
                pushTime -= step;
                pos += pushVel * step;
                if (activeSurface != null && (!useApproachSpline || isDeployed || approachPath.SegmentAt(pathDist) == PathSegment.Surface))
                {
                    Vector3 proj = activeSurface.ProjectOnto(pos);
                    if (!activeSurface.IsPointSupported(proj))
                    {
                        falling = true;
                        fallT = 0f;
                        fallVel = pushVel * 0.65f;
                        transform.position = pos;
                        return;
                    }
                    pos = proj;
                }
                transform.position = pos;
                return;
            }

            if (!isDeployed)
            {
                UpdateApproachToRim(dt);
                return;
            }

            // Deployed at table edge or nearby furniture: aim & bombard player walls/towers
            PlacedUnit targetUnit = PickBombardTarget(out Vector3 targetPos);
            if (targetUnit == null && hall != null)
                targetPos = hall.transform.position;

            Vector3 up = SurfaceUp();
            Face(targetPos - transform.position, up, dt);

            if (fireAnimT > 0f)
                fireAnimT = Mathf.Max(0f, fireAnimT - dt * 2.5f);

            fireTimer -= dt;
            if (fireTimer <= 0f)
            {
                float interval = IsCatapult ? CatapultFireInterval : ShamanFireInterval;
                fireTimer = interval;
                fireAnimT = 1f;
                FireSiegeProjectile(targetPos, targetUnit, hall);
            }

            AnimateVisuals(dt, 0.35f);
        }

        void UpdateApproachToRim(float dt)
        {
            if (useApproachSpline && approachPath != null)
            {
                PathSegment seg = approachPath.SegmentAt(pathDist);
                bool vertical = seg == PathSegment.Climb || seg == PathSegment.Descend || seg == PathSegment.Rope || seg == PathSegment.Ladder;
                float speed = vertical ? ClimbSpeed : MoveSpeed;

                pathDist = Mathf.Min(approachPath.Length, pathDist + speed * dt);
                Vector3 next = approachPath.EvaluateDistance(pathDist);
                Vector3 tan = approachPath.TangentAtDistance(pathDist);
                Vector3 up = seg == PathSegment.Surface ? SurfaceUp() : vertical ? approachPath.UpAt(pathDist) : Vector3.up;

                transform.position = next;
                Face(tan, up, dt, vertical);
                AnimateVisuals(dt, 1f);

                // As soon as the siege unit climbs onto the table rim (Surface segment) and steps inside the rim,
                // or reaches siege stand-off distance on a floor surface, deploy and stop!
                if (seg == PathSegment.Surface)
                {
                    float intoSurface = pathDist - approachPath.SurfaceStartDistance;
                    float distToHall = Vector3.Distance(new Vector3(next.x, hallBasePos.y, next.z), hallBasePos);
                    if ((!approachPath.SurfaceIsFloor && intoSurface >= 0.038f) ||
                        distToHall <= 0.44f ||
                        pathDist >= approachPath.Length - 0.02f)
                    {
                        DeployHere();
                    }
                }
            }
            else
            {
                Vector3 pos = transform.position;
                Vector3 toStop = deployTargetPos - pos;
                Vector3 up = SurfaceUp();
                toStop -= Vector3.Dot(toStop, up) * up;
                float dist = toStop.magnitude;
                if (dist <= 0.008f)
                {
                    DeployHere();
                    return;
                }
                Vector3 step = (toStop / dist) * Mathf.Min(dist, MoveSpeed * dt);
                Vector3 next = pos + step;
                if (activeSurface != null) next = activeSurface.ProjectOnto(next);
                transform.position = next;
                Face(hallBasePos - next, up, dt);
                AnimateVisuals(dt, 1f);
            }
        }

        void DeployHere()
        {
            if (isDeployed) return;
            isDeployed = true;
            if (VfxPool.Instance != null)
                VfxPool.Instance.Ring(transform.position + Vector3.up * 0.005f, 0.08f);
            ProceduralAudio.Fire(Sfx.Place, transform.position);
            Deployed?.Invoke(this);
        }

        PlacedUnit PickBombardTarget(out Vector3 targetWorldPos)
        {
            targetWorldPos = hallBasePos;
            var units = PlacedUnit.All;
            PlacedUnit best = null;
            float bestScore = float.MaxValue;
            Vector3 myPos = transform.position;
            float maxR2 = BombardRange * BombardRange;

            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.health == null || u.health.IsDead) continue;
                float d2 = (u.transform.position - myPos).sqrMagnitude;
                if (d2 > maxR2) continue;

                // Prioritize player Walls and ArcherTowers on the perimeter
                float typeWeight = 1f;
                if (u.type == UnitType.Wall) typeWeight = 0.55f;
                else if (u.type == UnitType.ArcherTower) typeWeight = 0.62f;
                else if (u.type == UnitType.Barricade) typeWeight = 0.85f;

                float score = Mathf.Sqrt(d2) * typeWeight;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = u;
                }
            }

            if (best != null)
                targetWorldPos = best.transform.position;
            return best;
        }

        void FireSiegeProjectile(Vector3 targetPos, PlacedUnit targetUnit, TownHall targetHall)
        {
            Vector3 origin = transform.position + Vector3.up * (IsCatapult ? 0.036f : 0.034f);
            float dist = Vector3.Distance(origin, targetPos);
            float arcH = Mathf.Clamp(dist * 0.45f, 0.12f, 0.28f);
            float dur = Mathf.Max(0.35f, dist / ProjectileSpeed);
            float dmg = IsCatapult ? CatapultDamage : ShamanDamage;

            var proj = GetOrCreateProjectile();
            proj.from = origin;
            proj.to = targetPos;
            proj.targetUnit = targetUnit;
            proj.targetHall = targetHall;
            proj.damage = dmg;
            proj.arcHeight = arcH;
            proj.duration = dur;
            proj.t = 0f;
            proj.kind = ThreatKind;
            proj.active = true;
            proj.root.position = origin;
            proj.boulderVis.gameObject.SetActive(IsCatapult);
            proj.fireballVis.gameObject.SetActive(IsShaman);
            proj.root.gameObject.SetActive(true);

            ProceduralAudio.Fire(IsCatapult ? Sfx.Ram : Sfx.Arrow, origin);
        }

        static SiegeBombProjectile GetOrCreateProjectile()
        {
            for (int i = 0; i < projectiles.Count; i++)
            {
                if (!projectiles[i].active && projectiles[i].root != null)
                    return projectiles[i];
            }

            if (projectilePoolRoot == null)
                projectilePoolRoot = new GameObject("SiegeProjectilePool").transform;

            var go = new GameObject("SiegeBomb");
            go.transform.SetParent(projectilePoolRoot, false);

            var boulderRoot = BuildingVisuals.NewRoot(go.transform, "Boulder");
            var kb = new BuildingVisuals.Kit(boulderRoot);
            kb.Ball(Vector3.zero, 0.015f, new Color(0.35f, 0.32f, 0.30f));
            var core = kb.Ball(new Vector3(0.002f, 0.002f, 0f), 0.011f, new Color(1f, 0.45f, 0.1f));
            core.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(new Color(1f, 0.5f, 0.12f));

            var fireRoot = BuildingVisuals.NewRoot(go.transform, "Fireball");
            var kf = new BuildingVisuals.Kit(fireRoot);
            var outer = kf.Ball(Vector3.zero, 0.014f, new Color(0.35f, 1f, 0.3f));
            outer.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(new Color(0.4f, 1f, 0.3f));
            var inner = kf.Ball(Vector3.zero, 0.009f, new Color(1f, 0.85f, 0.25f));
            inner.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(new Color(1f, 0.9f, 0.3f));

            go.SetActive(false);
            var p = new SiegeBombProjectile
            {
                root = go.transform,
                boulderVis = boulderRoot,
                fireballVis = fireRoot,
                active = false
            };
            projectiles.Add(p);
            return p;
        }

        static void UpdateAllProjectiles()
        {
            if (Time.frameCount == lastProjectileUpdateFrame) return;
            lastProjectileUpdateFrame = Time.frameCount;

            float dt = Time.deltaTime;
            for (int i = 0; i < projectiles.Count; i++)
            {
                var p = projectiles[i];
                if (!p.active || p.root == null) continue;

                if (p.targetUnit != null && p.targetUnit.health != null && !p.targetUnit.health.IsDead)
                    p.to = p.targetUnit.transform.position + Vector3.up * 0.018f;
                else if (p.targetUnit == null && p.targetHall != null && p.targetHall.health != null && !p.targetHall.health.IsDead)
                    p.to = p.targetHall.transform.position + Vector3.up * 0.04f;

                p.t += dt / Mathf.Max(0.1f, p.duration);
                float u = Mathf.Clamp01(p.t);
                Vector3 pos = Vector3.Lerp(p.from, p.to, u) + Vector3.up * (Mathf.Sin(u * Mathf.PI) * p.arcHeight);
                p.root.position = pos;
                p.root.Rotate(420f * dt, 260f * dt, 0f, Space.Self);

                if (u < 1f) continue;

                // Impact!
                Vector3 hitPos = p.to;
                if (p.targetUnit != null && p.targetUnit.health != null && !p.targetUnit.health.IsDead)
                {
                    p.targetUnit.health.Damage(p.damage);
                }
                else if (p.targetHall != null && p.targetHall.health != null && !p.targetHall.health.IsDead)
                {
                    p.targetHall.health.Damage(p.damage);
                }

                // Siege splash damage to adjacent walls/structures within SplashRadius
                float sr2 = SplashRadius * SplashRadius;
                var units = PlacedUnit.All;
                for (int k = units.Count - 1; k >= 0; k--)
                {
                    if (k >= units.Count) continue;
                    var uOther = units[k];
                    if (uOther == null || uOther == p.targetUnit || uOther.health == null || uOther.health.IsDead) continue;
                    if ((uOther.transform.position - hitPos).sqrMagnitude <= sr2)
                        uOther.health.Damage(SplashDamage);
                }

                if (VfxPool.Instance != null)
                {
                    Color burstCol = p.kind == SiegeThreatKind.GoblinCatapult
                        ? new Color(1f, 0.48f, 0.14f)
                        : new Color(0.45f, 1f, 0.3f);
                    VfxPool.Instance.Burst(hitPos, burstCol);
                    VfxPool.Instance.Ring(hitPos, SplashRadius * 1.25f);
                }
                ProceduralAudio.Fire(Sfx.Thud, hitPos);

                p.active = false;
                p.root.gameObject.SetActive(false);
            }
        }

        void Face(Vector3 dir, Vector3 up, float dt, bool raw = false)
        {
            if (!raw) dir -= Vector3.Dot(dir, up) * up;
            if (dir.sqrMagnitude < 1e-8f) return;
            Quaternion want = Quaternion.LookRotation(dir.normalized, up);
            transform.rotation = Quaternion.Slerp(transform.rotation, want, Mathf.Min(1f, dt * 8f));
        }

        void AnimateVisuals(float dt, float rate)
        {
            bobPhase += dt * 10f * rate;
            if (IsCatapult)
            {
                if (catapultArmPivot != null)
                {
                    // Throwing arm snaps up to +62 deg when firing, then winches back down to -18 deg
                    float armAngle = fireAnimT > 0f
                        ? Mathf.Lerp(-18f, 65f, Mathf.Sin(fireAnimT * Mathf.PI * 0.5f))
                        : Mathf.Lerp(65f, -18f, Mathf.Clamp01(1f - fireTimer / CatapultFireInterval));
                    catapultArmPivot.localRotation = Quaternion.Euler(armAngle, 0f, 0f);
                }
                if (catapultPayloadBall != null)
                {
                    bool loaded = fireAnimT <= 0.1f && fireTimer < CatapultFireInterval * 0.65f;
                    if (catapultPayloadBall.gameObject.activeSelf != loaded)
                        catapultPayloadBall.gameObject.SetActive(loaded);
                }
                if (siegeVisualRoot != null)
                {
                    float recoil = fireAnimT * 0.004f;
                    siegeVisualRoot.localPosition = new Vector3(0f, 0f, -recoil);
                }
            }
            else
            {
                if (shamanStaffPivot != null)
                {
                    float castTilt = fireAnimT > 0f ? Mathf.Sin(fireAnimT * Mathf.PI) * 35f : Mathf.Sin(bobPhase) * 6f;
                    shamanStaffPivot.localRotation = Quaternion.Euler(castTilt, 0f, 0f);
                }
                if (shamanOrbBall != null)
                {
                    float charge = 1f - Mathf.Clamp01(fireTimer / ShamanFireInterval);
                    float s = 0.009f + charge * 0.006f + Mathf.Sin(Time.time * 12f) * 0.0015f;
                    shamanOrbBall.localScale = Vector3.one * s;
                }
                if (siegeVisualRoot != null)
                {
                    siegeVisualRoot.localPosition = new Vector3(0f, Mathf.Abs(Mathf.Sin(bobPhase)) * 0.003f, 0f);
                }
            }
        }
    }
}
