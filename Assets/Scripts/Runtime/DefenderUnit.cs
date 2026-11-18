using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    public enum DefenderKind { Spearman, Swordsman }

    /// <summary>
    /// Friendly miniature garrison soldier (Spearman or Swordsman) spawned by a placed Barracks or the TownHall garrison.
    /// Wears the player's civilization architecture style and banner colors, patrols near defensive walls/towers,
    /// and marches out to intercept nearby enemies in melee combat, holding them back at the defensive line.
    /// Owner: @living-civ (Feature 4).
    /// </summary>
    public class DefenderUnit : MonoBehaviour
    {
        public const float SpearmanHp = 10f;
        public const float SwordsmanHp = 14f;
        public const float MoveSpeed = 0.085f;
        public const float PatrolSpeed = 0.048f;
        public const float SpearmanDps = 2.4f;
        public const float SwordsmanDps = 3.0f;
        public const float SpearmanReach = 0.038f;
        public const float SwordsmanReach = 0.032f;
        public const float InterceptRange = 0.34f;
        public const float CounterDamageFactor = 0.55f;
        const float SeparationRadius = 0.026f;
        const float AttackInterval = 0.75f;

        /// <summary>All currently active friendly defenders on the table.</summary>
        public static readonly List<DefenderUnit> Active = new List<DefenderUnit>();
        public static event Action<DefenderUnit> Killed;

        static SimplePool<DefenderUnit> pool;
        static Transform poolRoot;

        public Health health;
        public DefenderKind Kind { get; private set; }
        public BarracksSpawner SourceSpawner { get; private set; }
        public MonsterUnit Target { get; private set; }
        public Vector3 HomePosition { get; private set; }
        public Vector3 PatrolWaypoint { get; private set; }
        public bool IsAlive => launched && !dying && health != null && !health.IsDead;
        public float Radius => 0.015f;
        public float MeleeReach => Kind == DefenderKind.Spearman ? SpearmanReach : SwordsmanReach;
        public float MeleeDps => (Kind == DefenderKind.Spearman ? SpearmanDps : SwordsmanDps) * GetDamageMultiplier();
        public Vector3 AimPoint => transform.position + transform.up * 0.018f;

        Transform visualRoot;
        Transform bobRoot;
        Transform weaponPivot;
        CivilizationDefinition lastCiv;
        Color lastBannerColor;
        ArchitectureStyle lastStyle;
        bool visualBuilt;

        bool launched;
        bool dying;
        float attackTimer;
        float counterTimer;
        float patrolWaitTimer;
        float bobPhase;
        int patrolSlot;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active.Clear();
            Killed = null;
            pool = null;
            poolRoot = null;
        }

        static void EnsurePool(int prewarm = 6)
        {
            if (pool == null || poolRoot == null)
            {
                if (poolRoot == null) poolRoot = new GameObject("DefenderPool").transform;
                pool = new SimplePool<DefenderUnit>(Create, prewarm);
            }
        }

        static DefenderUnit Create()
        {
            if (poolRoot == null) poolRoot = new GameObject("DefenderPool").transform;
            var go = new GameObject("DefenderUnit");
            go.transform.SetParent(poolRoot, false);
            var d = go.AddComponent<DefenderUnit>();
            d.health = go.AddComponent<Health>();
            d.health.Died += d.OnDied;
            d.bobRoot = BuildingVisuals.NewRoot(go.transform, "Bob");
            go.SetActive(false);
            return d;
        }

        /// <summary>Spawns a pooled friendly defender affiliated with a Barracks or TownHall garrison spawner.</summary>
        public static DefenderUnit Spawn(BarracksSpawner spawner, Vector3 spawnPos, DefenderKind kind, int slotIndex = 0, CivilizationDefinition civOverride = null)
        {
            EnsurePool(6);
            var d = pool.Get();
            d.Launch(spawner, spawnPos, kind, slotIndex, civOverride);
            return d;
        }

        /// <summary>Despawns all active defenders immediately (used on session reset, restart, or new empire).</summary>
        public static void DespawnAll()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var d = Active[i];
                if (d == null) continue;
                d.launched = false;
                d.dying = false;
                d.Target = null;
                d.SourceSpawner = null;
                if (pool != null) pool.Release(d);
                else d.gameObject.SetActive(false);
            }
            Active.Clear();
        }

        /// <summary>
        /// Optional query for MonsterUnit or @integrator: returns an alive DefenderUnit blocking world point `p` within `pad`.
        /// </summary>
        public static DefenderUnit FindBlockingDefender(Vector3 p, float pad = 0.022f)
        {
            float bestSq = float.MaxValue;
            DefenderUnit best = null;
            for (int i = 0; i < Active.Count; i++)
            {
                var d = Active[i];
                if (d == null || !d.IsAlive) continue;
                if (d.ContactsMonster(p, pad, out float sq) && sq < bestSq)
                {
                    bestSq = sq;
                    best = d;
                }
            }
            return best;
        }

        public bool ContactsMonster(Vector3 monsterPos, float pad) => ContactsMonster(monsterPos, pad, out _);

        public bool ContactsMonster(Vector3 monsterPos, float pad, out float planarSq)
        {
            Vector3 up = SurfaceUp();
            Vector3 diff = monsterPos - transform.position;
            float vDist = Mathf.Abs(Vector3.Dot(diff, up));
            if (vDist > 0.08f) { planarSq = float.MaxValue; return false; }
            diff -= Vector3.Dot(diff, up) * up;
            planarSq = diff.sqrMagnitude;
            float r = Radius + pad;
            return planarSq <= r * r;
        }

        public void Launch(BarracksSpawner spawner, Vector3 spawnPos, DefenderKind kind, int slotIndex, CivilizationDefinition civOverride)
        {
            SourceSpawner = spawner;
            Kind = kind;
            patrolSlot = slotIndex;
            HomePosition = spawnPos;
            Target = null;
            attackTimer = 0.25f;
            counterTimer = 0.6f;
            patrolWaitTimer = 0.2f;
            bobPhase = UnityEngine.Random.value * 10f;
            dying = false;
            launched = true;

            float baseHp = kind == DefenderKind.Swordsman ? SwordsmanHp : SpearmanHp;
            health.Init(baseHp * GetHpMultiplier());

            RebuildVisualIfNeeded(civOverride);

            ITableSurface s = CurrentSurface();
            if (s != null) spawnPos = s.ProjectOnto(spawnPos);
            transform.position = spawnPos;
            Vector3 up = SurfaceUp();
            Vector3 fwd = spawner != null ? spawner.transform.forward : Vector3.forward;
            fwd -= Vector3.Dot(fwd, up) * up;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            transform.rotation = Quaternion.LookRotation(fwd.normalized, up);

            PickPatrolWaypoint();

            if (!gameObject.activeSelf) gameObject.SetActive(true);
            if (!Active.Contains(this)) Active.Add(this);
        }

        static float GetHpMultiplier()
        {
            if (UpgradeState.Instance == null) return 1f;
            if (Enum.TryParse("Barracks", out UnitType bt)) return UpgradeState.Instance.HpMul(bt);
            return UpgradeState.Instance.HpMul(UnitType.Wall);
        }

        static float GetDamageMultiplier()
        {
            if (UpgradeState.Instance == null) return 1f;
            if (Enum.TryParse("Barracks", out UnitType bt)) return UpgradeState.Instance.DamageMul(bt);
            return UpgradeState.Instance.DamageMul(UnitType.ArcherTower);
        }

        void RebuildVisualIfNeeded(CivilizationDefinition civOverride)
        {
            BuildingVisuals.Resolve(civOverride, out var style, out var pal);
            if (visualBuilt && visualRoot != null && style == lastStyle && ColorsClose(pal.banner, lastBannerColor) && civOverride == lastCiv)
                return;

            if (visualRoot != null) Destroy(visualRoot.gameObject);
            lastCiv = civOverride;
            lastStyle = style;
            lastBannerColor = pal.banner;
            visualRoot = BuildSoldierVisual(bobRoot, Kind, style, pal, out weaponPivot);
            visualBuilt = true;
        }

        static bool ColorsClose(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.02f;
        }

        /// <summary>Builds a procedural miniature soldier wearing the civilization's armor style and banner colors.</summary>
        public static Transform BuildSoldierVisual(Transform parent, DefenderKind kind, ArchitectureStyle style, BuildingVisuals.Palette pal, out Transform weaponPivotOut)
        {
            var root = BuildingVisuals.NewRoot(parent, kind.ToString());
            var k = new BuildingVisuals.Kit(root);

            Color skin = new Color(0.93f, 0.77f, 0.62f);
            Color iron = new Color(0.72f, 0.74f, 0.78f);
            Color darkIron = new Color(0.32f, 0.34f, 0.38f);
            Color wood = new Color(0.46f, 0.30f, 0.15f);

            // Boots
            k.Ellipsoid(new Vector3(-0.0045f, 0.0025f, 0.001f), new Vector3(0.0055f, 0.005f, 0.009f), pal.dark);
            k.Ellipsoid(new Vector3(0.0045f, 0.0025f, 0.001f), new Vector3(0.0055f, 0.005f, 0.009f), pal.dark);

            // Tunic & torso armor in civilization + banner colors
            k.Cyl(new Vector3(0f, 0.004f, 0f), 0.0075f, 0.015f, pal.main);
            // Banner tabard / surcoat front & back
            k.Box(new Vector3(0f, 0.011f, 0.0068f), new Vector3(0.009f, 0.013f, 0.0025f), pal.banner);
            k.Box(new Vector3(0f, 0.011f, -0.0068f), new Vector3(0.010f, 0.014f, 0.0025f), pal.banner);
            // Belt with civilization accent buckle
            k.Cyl(new Vector3(0f, 0.0095f, 0f), 0.008f, 0.0025f, pal.dark);
            k.Box(new Vector3(0f, 0.0107f, 0.0078f), new Vector3(0.0035f, 0.003f, 0.0015f), pal.trim);

            // Pauldrons (shoulders)
            k.Ball(new Vector3(-0.0085f, 0.017f, 0f), 0.0055f, style == ArchitectureStyle.StoneTowers ? iron : pal.trim);
            k.Ball(new Vector3(0.0085f, 0.017f, 0f), 0.0055f, style == ArchitectureStyle.StoneTowers ? iron : pal.trim);

            // Head & eyes
            k.Ball(new Vector3(0f, 0.0235f, 0f), 0.0115f, skin);
            k.Ball(new Vector3(-0.0026f, 0.024f, 0.0052f), 0.0022f, new Color(0.1f, 0.1f, 0.12f));
            k.Ball(new Vector3(0.0026f, 0.024f, 0.0052f), 0.0022f, new Color(0.1f, 0.1f, 0.12f));

            // Civilization-specific helmet
            switch (style)
            {
                case ArchitectureStyle.TieredRoofs:
                    // Celestial Dynasty conical helm with banner plume
                    k.Cone(new Vector3(0f, 0.0265f, 0f), 0.0085f, 0.008f, pal.roof);
                    k.Cyl(new Vector3(0f, 0.026f, 0f), 0.0088f, 0.0015f, pal.trim);
                    k.Ball(new Vector3(0f, 0.035f, 0f), 0.0038f, pal.banner);
                    break;
                case ArchitectureStyle.Timber:
                    // Verdant Clans crested timber/bronze helm
                    k.Ellipsoid(new Vector3(0f, 0.027f, 0f), new Vector3(0.0125f, 0.008f, 0.0125f), pal.dark);
                    k.Box(new Vector3(0f, 0.031f, 0f), new Vector3(0.0025f, 0.005f, 0.011f), pal.banner);
                    break;
                default:
                    // Iron Dominion great-helm with banner crest
                    k.Cyl(new Vector3(0f, 0.024f, 0f), 0.0064f, 0.008f, iron);
                    k.Box(new Vector3(0f, 0.026f, 0.0062f), new Vector3(0.007f, 0.0016f, 0.0015f), darkIron);
                    k.Ball(new Vector3(0f, 0.033f, 0f), 0.0042f, pal.banner);
                    break;
            }

            // Shield on left arm (-X) painted in player's banner & trim colors
            if (kind == DefenderKind.Spearman)
            {
                // Round targe shield
                k.CylRot(new Vector3(-0.010f, 0.013f, 0.004f), 0.0065f, 0.0022f, pal.banner, new Vector3(0f, 20f, 90f));
                k.Ball(new Vector3(-0.011f, 0.013f, 0.0045f), 0.0035f, pal.trim);
            }
            else
            {
                // Heater / tower shield with emblem stripe
                k.Box(new Vector3(-0.0105f, 0.013f, 0.005f), new Vector3(0.0025f, 0.014f, 0.010f), pal.banner, new Vector3(0f, -15f, 0f));
                k.Box(new Vector3(-0.0115f, 0.013f, 0.0052f), new Vector3(0.0015f, 0.010f, 0.0035f), pal.trim, new Vector3(0f, -15f, 0f));
            }

            // Weapon pivot on right hand (+X) for animated melee strikes
            var wp = BuildingVisuals.NewRoot(root, "WeaponPivot");
            wp.localPosition = new Vector3(0.0095f, 0.013f, 0.003f);
            var wk = new BuildingVisuals.Kit(wp);

            if (kind == DefenderKind.Spearman)
            {
                // Long spear pointed forward-up + small banner pennant under spearhead
                wk.CylRot(new Vector3(0f, 0.002f, 0.008f), 0.0012f, 0.038f, wood, new Vector3(68f, 0f, 0f));
                wk.Cone(new Vector3(0f, 0.0088f, 0.024f), 0.0024f, 0.007f, iron).transform.localRotation = Quaternion.Euler(68f, 0f, 0f);
                wk.Box(new Vector3(0f, 0.0055f, 0.019f), new Vector3(0.0012f, 0.0045f, 0.006f), pal.banner);
            }
            else
            {
                // Broadsword with crossguard
                wk.CylRot(new Vector3(0f, 0f, 0.002f), 0.0012f, 0.006f, pal.dark, new Vector3(45f, 0f, 0f));
                wk.Box(new Vector3(0f, 0.0025f, 0.0045f), new Vector3(0.006f, 0.0016f, 0.002f), pal.trim, new Vector3(45f, 0f, 0f));
                wk.Box(new Vector3(0f, 0.0095f, 0.0115f), new Vector3(0.0018f, 0.0032f, 0.018f), iron, new Vector3(-45f, 0f, 0f));
            }

            weaponPivotOut = wp;
            return root;
        }

        ITableSurface CurrentSurface()
        {
            if (GameDirector.Instance != null && GameDirector.Instance.Surface != null)
                return GameDirector.Instance.Surface;
            return SettlementBuildArea.Instance != null ? SettlementBuildArea.Instance.Surface : null;
        }

        Vector3 SurfaceUp()
        {
            var s = CurrentSurface();
            return s != null ? s.Pose.up : Vector3.up;
        }

        void OnDied(Health h)
        {
            if (dying || !launched) return;
            Die();
        }

        void Die()
        {
            dying = true;
            if (VfxPool.Instance != null)
                VfxPool.Instance.Burst(AimPoint, lastBannerColor.a > 0f ? lastBannerColor : new Color(0.8f, 0.7f, 0.3f));
            ProceduralAudio.Fire(Sfx.Death, transform.position);
            if (SourceSpawner != null) SourceSpawner.NotifyDefenderDied(this);
            Killed?.Invoke(this);
        }

        void LateUpdate()
        {
            if (dying) ReturnToPool();
        }

        void ReturnToPool()
        {
            launched = false;
            dying = false;
            Target = null;
            SourceSpawner = null;
            Active.Remove(this);
            if (pool != null) pool.Release(this);
            else gameObject.SetActive(false);
        }

        void Update()
        {
            if (!launched || dying) return;

            var gd = GameDirector.Instance;
            if (gd != null && (gd.Phase == GamePhase.CivilizationSetup || gd.Phase == GamePhase.HeadquartersPlacement || gd.Phase == GamePhase.Defeat))
            {
                DieSilent();
                return;
            }

            if (SourceSpawner != null && !SourceSpawner.IsSpawnerAlive)
            {
                DieSilent();
                return;
            }

            float dt = Time.deltaTime;
            Vector3 pos = transform.position;
            Vector3 up = SurfaceUp();
            ITableSurface surface = CurrentSurface();

            // 1. Validate or acquire an enemy target within intercept range
            if (Target != null && (!Target.IsAlive || !IsMonsterReachable(Target, pos, up)))
                Target = null;

            if (Target == null)
                Target = FindBestTarget(pos, up);

            // 2. If we have an enemy target, march to intercept and engage in melee
            if (Target != null)
            {
                Vector3 toEnemy = Target.transform.position - pos;
                toEnemy -= Vector3.Dot(toEnemy, up) * up;
                float dist = toEnemy.magnitude;
                float engageDist = MeleeReach + Target.Radius * 0.65f;

                if (dist <= engageDist)
                {
                    // Hold enemy back at the defensive line so it cannot slip past the defender
                    HoldMonsterAtBay(Target, pos, up, engageDist, surface, dt);

                    Face(toEnemy, up, dt);
                    attackTimer -= dt;
                    if (attackTimer <= 0f)
                    {
                        attackTimer = AttackInterval;
                        Target.health.Damage(MeleeDps * AttackInterval);
                        ProceduralAudio.Fire(Sfx.Hit, Target.AimPoint);
                        if (VfxPool.Instance != null)
                            VfxPool.Instance.Burst( Vector3.Lerp(AimPoint, Target.AimPoint, 0.6f), new Color(1f, 0.88f, 0.45f));
                    }

                    // Mutual melee exchange: engaged monster strikes back periodically
                    counterTimer -= dt;
                    if (counterTimer <= 0f && Target != null && Target.IsAlive)
                    {
                        counterTimer = 1.0f;
                        float enemyDps = (Target.IsRam ? MonsterUnit.RamDps : MonsterUnit.NormalDps) * CounterDamageFactor;
                        health.Damage(enemyDps);
                    }

                    AnimateCombat(dt, 1f - Mathf.Clamp01(attackTimer / AttackInterval));
                    return;
                }

                // March toward enemy to intercept
                MoveToward(Target.transform.position, MoveSpeed, surface, up, dt);
                ResetWeaponPose(dt);
                return;
            }

            // 3. No nearby enemy: patrol around walls, towers, or home spawner
            ResetWeaponPose(dt);
            Vector3 toPatrol = PatrolWaypoint - pos;
            toPatrol -= Vector3.Dot(toPatrol, up) * up;
            if (toPatrol.sqrMagnitude <= 0.012f * 0.012f)
            {
                patrolWaitTimer -= dt;
                AnimateIdle(dt);
                if (patrolWaitTimer <= 0f)
                {
                    patrolWaitTimer = UnityEngine.Random.Range(1.4f, 2.8f);
                    PickPatrolWaypoint();
                }
            }
            else
            {
                MoveToward(PatrolWaypoint, PatrolSpeed, surface, up, dt);
            }
        }

        void DieSilent()
        {
            dying = true;
            if (SourceSpawner != null) SourceSpawner.NotifyDefenderDied(this);
        }

        bool IsMonsterReachable(MonsterUnit m, Vector3 myPos, Vector3 up)
        {
            if (m == null || !m.IsAlive) return false;
            Vector3 anchor = SourceSpawner != null ? SourceSpawner.transform.position : HomePosition;
            Vector3 diff = m.transform.position - anchor;
            float vDist = Mathf.Abs(Vector3.Dot(diff, up));
            if (vDist > 0.09f && !m.OnFinalSurface) return false;
            diff -= Vector3.Dot(diff, up) * up;
            return diff.sqrMagnitude <= (InterceptRange * 1.25f) * (InterceptRange * 1.25f);
        }

        MonsterUnit FindBestTarget(Vector3 myPos, Vector3 up)
        {
            var monsters = MonsterUnit.Active;
            if (monsters.Count == 0) return null;

            Vector3 anchor = SourceSpawner != null ? SourceSpawner.transform.position : HomePosition;
            Vector3 hallPos = TownHall.Current != null ? TownHall.Current.transform.position : anchor;
            float maxRangeSq = InterceptRange * InterceptRange;

            MonsterUnit best = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < monsters.Count; i++)
            {
                var m = monsters[i];
                if (m == null || !m.IsAlive) continue;

                Vector3 fromAnchor = m.transform.position - anchor;
                float vDist = Mathf.Abs(Vector3.Dot(fromAnchor, up));
                if (vDist > 0.08f && !m.OnFinalSurface) continue;

                fromAnchor -= Vector3.Dot(fromAnchor, up) * up;
                float anchorDistSq = fromAnchor.sqrMagnitude;

                Vector3 fromHall = m.transform.position - hallPos;
                fromHall -= Vector3.Dot(fromHall, up) * up;
                float hallDistSq = fromHall.sqrMagnitude;

                if (anchorDistSq > maxRangeSq && hallDistSq > maxRangeSq) continue;

                Vector3 fromMe = m.transform.position - myPos;
                fromMe -= Vector3.Dot(fromMe, up) * up;
                float myDist = fromMe.magnitude;

                // Count how many other defenders are already targeting this monster so defenders spread across threats
                int engagedCount = 0;
                for (int j = 0; j < Active.Count; j++)
                {
                    var other = Active[j];
                    if (other != null && other != this && other.IsAlive && other.Target == m)
                        engagedCount++;
                }

                float score = myDist + engagedCount * 0.06f + Mathf.Sqrt(hallDistSq) * 0.35f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = m;
                }
            }
            return best;
        }

        void HoldMonsterAtBay(MonsterUnit m, Vector3 myPos, Vector3 up, float holdDist, ITableSurface surface, float dt)
        {
            if (m == null || !m.IsAlive || !m.OnFinalSurface) return;
            Vector3 hallPos = TownHall.Current != null ? TownHall.Current.transform.position : HomePosition;
            Vector3 mPos = m.transform.position;
            Vector3 toMonster = mPos - myPos;
            toMonster -= Vector3.Dot(toMonster, up) * up;
            float d = toMonster.magnitude;

            // If the monster tries to push closer than holdDist or move past the defender toward the TownHall, hold it back
            Vector3 outward = d > 1e-5f ? (toMonster / d) : (myPos - hallPos).normalized;
            outward -= Vector3.Dot(outward, up) * up;
            if (outward.sqrMagnitude < 1e-6f) outward = transform.forward;
            else outward.Normalize();

            float minKeep = Mathf.Max(0.022f, holdDist * 0.82f);
            if (d < minKeep)
            {
                Vector3 pushed = myPos + outward * minKeep;
                if (surface != null)
                {
                    Vector3 proj = surface.ProjectOnto(pushed);
                    if (surface.IsPointSupported(proj))
                        m.transform.position = Vector3.Lerp(mPos, proj, Mathf.Min(1f, dt * 18f));
                }
                else
                {
                    pushed.y = mPos.y;
                    m.transform.position = Vector3.Lerp(mPos, pushed, Mathf.Min(1f, dt * 18f));
                }
            }
        }

        void PickPatrolWaypoint()
        {
            Vector3 anchor = SourceSpawner != null ? SourceSpawner.transform.position : HomePosition;
            Vector3 hallPos = TownHall.Current != null ? TownHall.Current.transform.position : anchor;
            Vector3 up = SurfaceUp();
            ITableSurface surface = CurrentSurface();

            // Look for a nearby Wall, ArcherTower, or Barricade to patrol along
            PlacedUnit bestDefense = null;
            float bestDistSq = 0.28f * 0.28f;
            var units = PlacedUnit.All;
            int offsetSeed = (patrolSlot * 3 + UnityEngine.Random.Range(0, 5));

            for (int i = 0; i < units.Count; i++)
            {
                var u = units[(i + offsetSeed) % units.Count];
                if (u == null || u.health == null || u.health.IsDead || u.IsEconomy) continue;
                if (u.type == UnitType.Wall || u.type == UnitType.ArcherTower || u.type == UnitType.Barricade)
                {
                    Vector3 diff = u.transform.position - anchor;
                    diff -= Vector3.Dot(diff, up) * up;
                    float sq = diff.sqrMagnitude;
                    if (sq <= bestDistSq)
                    {
                        bestDistSq = sq;
                        bestDefense = u;
                        break;
                    }
                }
            }

            Vector3 candidate;
            if (bestDefense != null)
            {
                // Stand slightly on the outer (enemy-facing) side of the wall/tower
                Vector3 outward = bestDefense.transform.position - hallPos;
                outward -= Vector3.Dot(outward, up) * up;
                if (outward.sqrMagnitude < 1e-5f) outward = bestDefense.transform.forward;
                else outward.Normalize();
                Vector3 side = Vector3.Cross(up, outward).normalized;
                float sideOffset = ((patrolSlot % 3) - 1) * 0.025f + UnityEngine.Random.Range(-0.015f, 0.015f);
                candidate = bestDefense.transform.position + outward * 0.036f + side * sideOffset;
            }
            else
            {
                // Orbit around the Barracks or TownHall
                float angle = (patrolSlot * 120f + UnityEngine.Random.Range(-45f, 45f)) * Mathf.Deg2Rad;
                float r = SourceSpawner != null && SourceSpawner.isTownHallGarrison
                    ? BuildingVisuals.HallFootprintRadius + 0.035f
                    : 0.065f;
                Vector3 right = Vector3.Cross(up, Vector3.forward);
                if (right.sqrMagnitude < 1e-4f) right = Vector3.right;
                right.Normalize();
                Vector3 fwd = Vector3.Cross(right, up).normalized;
                candidate = anchor + (right * Mathf.Cos(angle) + fwd * Mathf.Sin(angle)) * r;
            }

            if (surface != null)
            {
                Vector3 proj = surface.ProjectOnto(candidate);
                if (surface.IsPointSupported(proj))
                {
                    PatrolWaypoint = proj;
                    return;
                }
            }
            PatrolWaypoint = surface != null ? surface.ProjectOnto(anchor) : anchor;
        }

        void MoveToward(Vector3 goal, float speed, ITableSurface surface, Vector3 up, float dt)
        {
            Vector3 pos = transform.position;
            Vector3 seek = goal - pos;
            seek -= Vector3.Dot(seek, up) * up;
            float dist = seek.magnitude;
            if (dist < 1e-5f) return;
            Vector3 dir = seek / dist;

            // Friendly separation steering
            Vector3 sep = Vector3.zero;
            for (int i = 0; i < Active.Count; i++)
            {
                var o = Active[i];
                if (o == null || o == this || !o.IsAlive) continue;
                Vector3 diff = pos - o.transform.position;
                diff -= Vector3.Dot(diff, up) * up;
                float sq = diff.sqrMagnitude;
                if (sq < SeparationRadius * SeparationRadius && sq > 1e-10f)
                {
                    float d = Mathf.Sqrt(sq);
                    sep += (diff / d) * (1f - d / SeparationRadius);
                }
            }

            Vector3 vel = dir * speed + sep * (speed * 1.15f);
            Vector3 next = pos + vel * dt;
            if (surface != null)
            {
                Vector3 proj = surface.ProjectOnto(next);
                if (!surface.IsPointSupported(proj))
                    proj = surface.ProjectOnto(pos + dir * speed * dt);
                if (surface.IsPointSupported(proj))
                    next = proj;
                else
                    next = surface.ProjectOnto(pos);
            }

            transform.position = next;
            Face(dir, up, dt);
            AnimateWalk(dt, speed / MoveSpeed);
        }

        void Face(Vector3 dir, Vector3 up, float dt)
        {
            dir -= Vector3.Dot(dir, up) * up;
            if (dir.sqrMagnitude < 1e-8f) return;
            Quaternion want = Quaternion.LookRotation(dir.normalized, up);
            transform.rotation = Quaternion.Slerp(transform.rotation, want, Mathf.Min(1f, dt * 12f));
        }

        void AnimateWalk(float dt, float rate)
        {
            if (bobRoot == null) return;
            bobPhase += dt * 15f * Mathf.Max(0.4f, rate);
            bobRoot.localPosition = new Vector3(0f, Mathf.Abs(Mathf.Sin(bobPhase)) * 0.0038f, 0f);
            bobRoot.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(bobPhase) * 6f);
        }

        void AnimateIdle(float dt)
        {
            if (bobRoot == null) return;
            bobPhase += dt * 3.5f;
            bobRoot.localPosition = new Vector3(0f, Mathf.Abs(Mathf.Sin(bobPhase)) * 0.0012f, 0f);
            bobRoot.localRotation = Quaternion.Lerp(bobRoot.localRotation, Quaternion.identity, Mathf.Min(1f, dt * 8f));
        }

        void AnimateCombat(float dt, float swingPhase01)
        {
            if (bobRoot != null)
            {
                bobPhase += dt * 10f;
                bobRoot.localPosition = new Vector3(0f, Mathf.Abs(Mathf.Sin(bobPhase)) * 0.002f, 0f);
                bobRoot.localRotation = Quaternion.identity;
            }
            if (weaponPivot != null)
            {
                // Thrust/slash forward during the strike window
                float strike = Mathf.Sin(Mathf.Clamp01(swingPhase01 * 2.2f) * Mathf.PI);
                if (Kind == DefenderKind.Spearman)
                {
                    weaponPivot.localPosition = new Vector3(0.0095f, 0.013f, 0.003f + strike * 0.009f);
                    weaponPivot.localRotation = Quaternion.Euler(strike * 18f, 0f, 0f);
                }
                else
                {
                    weaponPivot.localPosition = new Vector3(0.0095f, 0.013f, 0.003f + strike * 0.005f);
                    weaponPivot.localRotation = Quaternion.Euler(strike * 55f, -strike * 25f, 0f);
                }
            }
        }

        void ResetWeaponPose(float dt)
        {
            if (weaponPivot == null) return;
            weaponPivot.localPosition = Vector3.Lerp(weaponPivot.localPosition, new Vector3(0.0095f, 0.013f, 0.003f), Mathf.Min(1f, dt * 10f));
            weaponPivot.localRotation = Quaternion.Slerp(weaponPivot.localRotation, Quaternion.identity, Mathf.Min(1f, dt * 10f));
        }
    }
}
