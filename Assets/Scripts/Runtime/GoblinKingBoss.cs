using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Feature 5B: Giant Goblin King Boss for Wave 3 (and recurring every 3rd wave in Endless Siege Mode: Waves 3, 6, 9, 12...).
    /// Attaches to a pooled MonsterUnit so it participates natively in MonsterUnit.Active, ArcherTower / Ballista targeting,
    /// HandOfGod Focus Fire, DefenderUnit interception, and WaveSpawner.WaveCleared checks, while cleanly resetting on pool release.
    /// Includes:
    /// - Procedural golden crown with jewels, heavy iron chestplate/pauldrons/cape, and animated spiked war-club
    /// - Floating 3D billboarded boss HP bar above the crown
    /// - Periodic War-Cry Aura that temporarily buffs nearby goblins' movement speed (+45%)
    /// - Periodic AoE Ground-Slam attack with club smash animation against nearby walls, buildings, and the Town Hall
    /// </summary>
    public class GoblinKingBoss : MonoBehaviour
    {
        public const float BaseBossHp = 75f;
        public const float HpPerTier = 40f;
        public const float BossScale = 2.15f;

        public const float BaseGroundSlamDamage = 6f;
        public const float GroundSlamCooldown = 3.2f;
        public const float GroundSlamRadius = 0.095f;
        public const float GroundSlamTriggerRange = 0.075f;
        public const float WallSiegeMultiplier = 1.5f;

        public const float WarCryInterval = 6.0f;
        public const float WarCryDuration = 3.5f;
        public const float WarCryRadius = 0.25f;
        public const float WarCrySpeedMultiplier = 1.45f;

        const int AuraSegments = 36;
        const float HpBarWidth = 0.044f;

        public static readonly List<GoblinKingBoss> Active = new List<GoblinKingBoss>();
        public static GoblinKingBoss Current => Active.Count > 0 ? Active[0] : null;

        public static event Action<GoblinKingBoss> BossSpawned;
        public static event Action<GoblinKingBoss> BossDefeated;
        public static event Action<GoblinKingBoss, Vector3> GroundSlammed;
        public static event Action<GoblinKingBoss> WarCryTriggered;

        static bool externalSpeedHookUsed;
        static int lastAutoWaveFrame = -1;
        static int lastAutoWaveIndex = -1;

        public MonsterUnit Unit { get; private set; }
        public Health Health => Unit != null ? Unit.health : null;
        public bool IsActiveBoss { get; private set; }
        public int WaveIndex { get; private set; }
        public int BossTier { get; private set; } = 1;

        public bool IsAlive => IsActiveBoss && Unit != null && Unit.IsAlive && Unit.Serial == activeSerial;
        public bool IsWarCryActive => IsAlive && warCryActiveTimer > 0f;
        public float GroundSlamDamage => BaseGroundSlamDamage * (1f + (BossTier - 1) * 0.35f);

        int activeSerial = -1;
        Transform bobRoot;
        Transform attachmentsRoot;
        Transform clubPivot;
        Transform crownRoot;

        Transform hpBarRoot;
        Transform hpFill;
        MeshRenderer hpFillRenderer;
        TMP_Text hpLabel;

        LineRenderer auraRing;
        readonly Vector3[] auraPts = new Vector3[AuraSegments];

        float warCryCooldownTimer;
        float warCryActiveTimer;
        float warCrySparkTimer;
        float slamCooldownTimer;
        float slamAnimTimer;
        bool slamImpactPending;
        Vector3 pendingSlamTarget;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active.Clear();
            BossSpawned = null;
            BossDefeated = null;
            GroundSlammed = null;
            WarCryTriggered = null;
            externalSpeedHookUsed = false;
            lastAutoWaveFrame = -1;
            lastAutoWaveIndex = -1;
        }

        // ------------------------------------------------------------ Static Public API

        /// <summary>True on Wave 3 (0-based index 2) and every 3rd wave in Endless Mode (Wave 6 = index 5, Wave 9 = index 8, etc.).</summary>
        public static bool IsBossWave(int waveIndex) => waveIndex >= 2 && ((waveIndex + 1) % 3 == 0);

        /// <summary>Alias for IsBossWave(waveIndex).</summary>
        public static bool ShouldSpawnOnWave(int waveIndex) => IsBossWave(waveIndex);

        /// <summary>1-based wave number check (Waves 3, 6, 9, 12...).</summary>
        public static bool ShouldSpawnOnWaveNumber(int oneBasedWaveNumber) => oneBasedWaveNumber >= 3 && (oneBasedWaveNumber % 3 == 0);

        /// <summary>True if the given MonsterUnit is currently an active Goblin King Boss.</summary>
        public static bool IsBoss(MonsterUnit m)
        {
            if (m == null) return false;
            var b = m.GetComponent<GoblinKingBoss>();
            return b != null && b.IsAlive;
        }

        /// <summary>Returns the active GoblinKingBoss on a MonsterUnit, or null if not a boss.</summary>
        public static GoblinKingBoss GetBoss(MonsterUnit m)
        {
            if (m == null) return null;
            var b = m.GetComponent<GoblinKingBoss>();
            return b != null && b.IsAlive ? b : null;
        }

        /// <summary>
        /// Returns the War-Cry speed multiplier (1.45f when within an active Goblin King's War-Cry aura, 1f otherwise).
        /// Can be multiplied into speedMul in MonsterUnit.Update().
        /// </summary>
        public static float GetSpeedMultiplier(MonsterUnit m)
        {
            externalSpeedHookUsed = true;
            if (m == null || !m.IsAlive || Active.Count == 0) return 1f;
            return GetSpeedMultiplierInternal(m.transform.position, m);
        }

        /// <summary>Returns the War-Cry speed multiplier at a world position.</summary>
        public static float GetSpeedMultiplier(Vector3 worldPos)
        {
            externalSpeedHookUsed = true;
            if (Active.Count == 0) return 1f;
            return GetSpeedMultiplierInternal(worldPos, null);
        }

        /// <summary>True if a world position is currently inside any active Goblin King's War-Cry aura.</summary>
        public static bool IsInWarCryAura(Vector3 worldPos) => GetSpeedMultiplierInternal(worldPos, null) > 1.01f;

        static float GetSpeedMultiplierInternal(Vector3 worldPos, MonsterUnit queryUnit)
        {
            float r2 = WarCryRadius * WarCryRadius;
            for (int i = 0; i < Active.Count; i++)
            {
                var boss = Active[i];
                if (boss == null || !boss.IsWarCryActive) continue;
                if (queryUnit != null && boss.Unit == queryUnit) continue; // boss doesn't speed-buff himself as a minion
                Vector3 d = worldPos - boss.transform.position;
                d.y = 0f;
                if (d.sqrMagnitude <= r2) return WarCrySpeedMultiplier;
            }
            return 1f;
        }

        /// <summary>Scaling HP multiplier for regular monsters in Endless Mode (Wave 4+).</summary>
        public static float GetEndlessHpMultiplier(int waveIndex)
        {
            if (waveIndex < 3) return 1f;
            return 1f + (waveIndex - 2) * 0.25f;
        }

        /// <summary>Number of extra bonus monsters to spawn in Endless Mode (Wave 4+).</summary>
        public static int GetEndlessBonusMonsterCount(int waveIndex)
        {
            if (waveIndex < 3) return 0;
            return (waveIndex - 2) * 3;
        }

        /// <summary>
        /// Spawns a Goblin King Boss along a pre-built SplinePath.
        /// </summary>
        public static GoblinKingBoss Spawn(SplinePath path, int waveIndex = 2)
        {
            if (path == null) return null;
            var m = MonsterUnit.Spawn(path, false);
            if (m == null) return null;
            return Promote(m, waveIndex);
        }

        /// <summary>
        /// Builds a floor/climb/surface SplinePath from spawnPos to hallPos and spawns the Goblin King Boss.
        /// </summary>
        public static GoblinKingBoss SpawnAt(Vector3 spawnPos, Vector3 hallPos, ITableSurface surface, int waveIndex = 2)
        {
            SplinePath path = BuildBossPath(spawnPos, hallPos, surface);
            return Spawn(path, waveIndex);
        }

        /// <summary>
        /// Call from GameDirector.StartWave() (or WaveStarted event).
        /// Automatically spawns the Goblin King Boss on Wave 3 (index 2) and every 3rd wave in Endless Mode (Waves 6, 9, 12...).
        /// </summary>
        public static GoblinKingBoss SpawnForWave(int waveIndex)
        {
            var gd = GameDirector.Instance;
            if (gd == null || gd.Hall == null) return null;
            return SpawnForWave(waveIndex, gd.Hall.transform.position, gd.Surface);
        }

        /// <summary>
        /// Spawns the Goblin King Boss if waveIndex is a boss wave (Wave 3, 6, 9, 12...).
        /// Returns null on non-boss waves.
        /// </summary>
        public static GoblinKingBoss SpawnForWave(int waveIndex, Vector3 hallPos, ITableSurface surface)
        {
            if (!IsBossWave(waveIndex)) return null;
            if (lastAutoWaveFrame == Time.frameCount && lastAutoWaveIndex == waveIndex && Active.Count > 0)
                return Active[Active.Count - 1];

            lastAutoWaveFrame = Time.frameCount;
            lastAutoWaveIndex = waveIndex;

            Vector3 spawnPos = PickSpawnPoint(hallPos, surface);
            if (VfxPool.Instance != null) VfxPool.Instance.Ring(spawnPos + Vector3.up * 0.01f, 0.26f);
            ProceduralAudio.Fire(Sfx.Horn, spawnPos);
            return SpawnAt(spawnPos, hallPos, surface, waveIndex);
        }

        /// <summary>
        /// Convenience method for Endless Siege Mode (Wave 4+): spawns the Goblin King Boss on every 3rd wave (3, 6, 9...)
        /// AND spawns scaling endless reinforcements / buffs active wave monsters for waves >= 3.
        /// </summary>
        public static GoblinKingBoss SpawnWaveBossAndEndlessScaling(int waveIndex, Vector3 hallPos, ITableSurface surface)
        {
            GoblinKingBoss boss = null;
            if (IsBossWave(waveIndex))
                boss = SpawnForWave(waveIndex, hallPos, surface);

            if (waveIndex >= 3)
                EndlessWaveScaler.BeginEndlessWave(waveIndex, hallPos, surface);

            return boss;
        }

        /// <summary>
        /// Promotes an active MonsterUnit into the Giant Goblin King Boss with procedural crown, armor, war-club, and floating HP bar.
        /// </summary>
        public static GoblinKingBoss Promote(MonsterUnit m, int waveIndex = 2)
        {
            if (m == null) return null;
            var boss = m.GetComponent<GoblinKingBoss>();
            if (boss == null) boss = m.gameObject.AddComponent<GoblinKingBoss>();
            boss.ActivateBoss(m, waveIndex);
            return boss;
        }

        /// <summary>Deactivates and cleans up all active Goblin King Bosses.</summary>
        public static void DespawnAll()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var b = Active[i];
                if (b != null) b.DeactivateBoss();
            }
            Active.Clear();
        }

        static Vector3 PickSpawnPoint(Vector3 hallPos, ITableSurface surface)
        {
            var ws = WaveSpawner.Instance;
            if (ws != null && ws.SpawnPoints != null && ws.SpawnPoints.Count > 0)
            {
                int idx = UnityEngine.Random.Range(0, ws.SpawnPoints.Count);
                return ws.SpawnPoints[idx];
            }
            var rcs = RoomCornerSpawns.Existing;
            if (rcs != null && rcs.Corners != null && rcs.Corners.Count > 0)
            {
                int idx = UnityEngine.Random.Range(0, rcs.Corners.Count);
                return rcs.Corners[idx];
            }
            if (surface != null)
            {
                Vector3 up = surface.Pose.up;
                Vector3 edge = surface.ClosestEdgePoint(hallPos + Vector3.forward * 0.4f);
                Vector3 inward = Vector3.ProjectOnPlane(hallPos - edge, up);
                if (inward.sqrMagnitude > 1e-6f)
                {
                    Vector3 candidate = surface.ProjectOnto(edge + inward.normalized * 0.04f);
                    if (surface.IsPointSupported(candidate)) return candidate;
                }
                return surface.ProjectOnto(hallPos + Vector3.forward * 0.28f);
            }
            return hallPos + Vector3.forward * 0.3f;
        }

        static SplinePath BuildBossPath(Vector3 spawnPos, Vector3 hallPos, ITableSurface surface)
        {
            var ws = WaveSpawner.Instance;
            var rcs = RoomCornerSpawns.Existing;
            bool roomMode = (ws != null && ws.RoomMode) || (rcs != null && rcs.Corners != null && rcs.Corners.Count > 0);
            if (roomMode)
            {
                float floorY = rcs != null ? rcs.FloorY : spawnPos.y;
                return SplinePath.BuildRoute(spawnPos, hallPos, surface, floorY);
            }
            return SplinePath.Build(spawnPos, hallPos, surface);
        }

        // ------------------------------------------------------------ Boss Lifecycle

        void ActivateBoss(MonsterUnit m, int waveIndex)
        {
            Unit = m;
            WaveIndex = Mathf.Max(2, waveIndex);
            BossTier = Mathf.Max(1, (WaveIndex + 1) / 3);
            activeSerial = m.Serial;
            IsActiveBoss = true;
            enabled = true;

            float bossMaxHp = BaseBossHp + (BossTier - 1) * HpPerTier;
            if (m.health != null)
            {
                m.health.Init(bossMaxHp);
                m.health.Damaged -= OnBossDamaged;
                m.health.Died -= OnBossDied;
                m.health.Damaged += OnBossDamaged;
                m.health.Died += OnBossDied;
            }

            transform.localScale = Vector3.one * BossScale;

            EnsureVisualsBuilt();
            if (attachmentsRoot != null) attachmentsRoot.gameObject.SetActive(true);
            if (hpBarRoot != null) hpBarRoot.gameObject.SetActive(true);
            if (hpLabel != null)
                hpLabel.text = BossTier > 1 ? $"GOBLIN KING  Mk.{BossTier}" : "GOBLIN KING";

            warCryCooldownTimer = 1.8f; // first war-cry shortly after arrival
            warCryActiveTimer = 0f;
            warCrySparkTimer = 0f;
            slamCooldownTimer = 1.2f;
            slamAnimTimer = 0f;
            slamImpactPending = false;

            if (auraRing != null) auraRing.enabled = false;
            UpdateHpBar();

            if (!Active.Contains(this)) Active.Add(this);
            BossSpawned?.Invoke(this);
            Debug.Log($"[GoblinKingBoss] Spawned Wave {WaveIndex + 1} Goblin King (Tier {BossTier}, HP {bossMaxHp})");
        }

        void DeactivateBoss()
        {
            if (Unit != null && Unit.health != null)
            {
                Unit.health.Damaged -= OnBossDamaged;
                Unit.health.Died -= OnBossDied;
            }
            IsActiveBoss = false;
            activeSerial = -1;
            warCryActiveTimer = 0f;
            slamImpactPending = false;
            transform.localScale = Vector3.one;

            if (attachmentsRoot != null) attachmentsRoot.gameObject.SetActive(false);
            if (hpBarRoot != null) hpBarRoot.gameObject.SetActive(false);
            if (auraRing != null) auraRing.enabled = false;
            if (clubPivot != null) clubPivot.localRotation = Quaternion.identity;

            Active.Remove(this);
            enabled = false;
        }

        void OnDisable()
        {
            if (IsActiveBoss) DeactivateBoss();
        }

        void OnDestroy()
        {
            if (IsActiveBoss) DeactivateBoss();
        }

        void OnBossDamaged(Health h)
        {
            if (!IsAlive) return;
            UpdateHpBar();
        }

        void OnBossDied(Health h)
        {
            if (!IsActiveBoss) return;
            Vector3 deathPos = transform.position + Vector3.up * 0.04f;
            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Ring(transform.position, 0.25f);
                VfxPool.Instance.Burst(deathPos, new Color(1f, 0.82f, 0.2f));
                VfxPool.Instance.Burst(deathPos, new Color(0.75f, 0.18f, 0.12f));
            }
            ProceduralAudio.Fire(Sfx.Horn, deathPos);
            BossDefeated?.Invoke(this);
            DeactivateBoss();
        }

        // ------------------------------------------------------------ Update Loop

        void Update()
        {
            if (!IsActiveBoss) return;
            if (Unit == null || !Unit.IsAlive || Unit.Serial != activeSerial)
            {
                DeactivateBoss();
                return;
            }

            float dt = Time.deltaTime;
            UpdateWarCry(dt);
            UpdateGroundSlam(dt);
            UpdateCrownPulse(dt);
            UpdateHpBar();
        }

        void LateUpdate()
        {
            if (!IsAlive) return;

            // Keep the giant boss anchored on the settlement surface so a single shockwave at high HP doesn't cheese-drop him off the edge
            if (Unit.OnFinalSurface && Unit.Path != null && Unit.Path.Surface != null && Unit.health != null && Unit.health.Fraction > 0.2f)
            {
                var surf = Unit.Path.Surface;
                Vector3 pos = transform.position;
                Vector3 proj = surf.ProjectOnto(pos);
                if (!surf.IsPointSupported(proj))
                {
                    Vector3 edge = surf.ClosestEdgePoint(proj);
                    Vector3 hallPos = TownHall.Current != null ? TownHall.Current.transform.position : surf.Pose.position;
                    Vector3 inward = Vector3.ProjectOnPlane(hallPos - edge, surf.Pose.up);
                    if (inward.sqrMagnitude > 1e-6f)
                        transform.position = surf.ProjectOnto(edge + inward.normalized * 0.025f);
                }
            }

            // Billboard floating HP bar toward main camera
            if (hpBarRoot != null && hpBarRoot.gameObject.activeSelf)
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    Vector3 toCam = hpBarRoot.position - cam.transform.position;
                    if (toCam.sqrMagnitude > 1e-6f)
                        hpBarRoot.rotation = Quaternion.LookRotation(toCam.normalized, Vector3.up);
                }
            }
        }

        // ------------------------------------------------------------ War-Cry Aura

        void UpdateWarCry(float dt)
        {
            if (warCryActiveTimer > 0f)
            {
                warCryActiveTimer -= dt;
                UpdateAuraRingVisual();

                // Fallback direct speed boost if MonsterUnit hasn't queried GetSpeedMultiplier directly
                if (!externalSpeedHookUsed)
                    ApplyFallbackWarCrySpeedBoost(dt);

                warCrySparkTimer -= dt;
                if (warCrySparkTimer <= 0f)
                {
                    warCrySparkTimer = 0.55f;
                    PulseBuffedGoblinsVfx();
                }

                if (warCryActiveTimer <= 0f && auraRing != null)
                    auraRing.enabled = false;
            }
            else
            {
                warCryCooldownTimer -= dt;
                if (warCryCooldownTimer <= 0f)
                    TriggerWarCry();
            }
        }

        public void TriggerWarCry()
        {
            if (!IsAlive) return;
            warCryCooldownTimer = WarCryInterval;
            warCryActiveTimer = WarCryDuration;
            warCrySparkTimer = 0.1f;

            if (auraRing != null)
            {
                auraRing.enabled = true;
                UpdateAuraRingVisual();
            }
            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Ring(transform.position, WarCryRadius);
                VfxPool.Instance.Burst(transform.position + transform.up * 0.07f, new Color(1f, 0.78f, 0.18f));
            }
            ProceduralAudio.Fire(Sfx.Horn, transform.position);
            WarCryTriggered?.Invoke(this);
        }

        void UpdateAuraRingVisual()
        {
            if (auraRing == null || !auraRing.enabled) return;
            Vector3 center = transform.position + Vector3.up * 0.005f;
            float pulse = 1f + 0.06f * Mathf.Sin(Time.time * 10f);
            float rad = WarCryRadius * pulse;
            for (int i = 0; i < AuraSegments; i++)
            {
                float a = (i / (float)AuraSegments) * Mathf.PI * 2f;
                auraPts[i] = center + new Vector3(Mathf.Cos(a) * rad, 0f, Mathf.Sin(a) * rad);
            }
            auraRing.SetPositions(auraPts);
            float w = 0.0045f + 0.0015f * Mathf.Sin(Time.time * 12f);
            auraRing.startWidth = w;
            auraRing.endWidth = w;
        }

        void ApplyFallbackWarCrySpeedBoost(float dt)
        {
            float r2 = WarCryRadius * WarCryRadius;
            Vector3 myPos = transform.position;
            var list = MonsterUnit.Active;
            float extraSpeed = MonsterUnit.NormalSpeed * (WarCrySpeedMultiplier - 1f);

            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                if (m == null || m == Unit || !m.IsAlive) continue;
                var seg = m.Segment;
                if (seg != PathSegment.Surface && seg != PathSegment.Floor && seg != PathSegment.Top) continue;

                Vector3 d = m.transform.position - myPos;
                d.y = 0f;
                if (d.sqrMagnitude > r2) continue;

                Vector3 step = m.transform.forward * (extraSpeed * dt);
                step.y = 0f;
                Vector3 next = m.transform.position + step;
                if (seg == PathSegment.Surface && m.Path != null && m.Path.Surface != null)
                {
                    Vector3 proj = m.Path.Surface.ProjectOnto(next);
                    if (m.Path.Surface.IsPointSupported(proj))
                        m.transform.position = proj;
                }
                else
                {
                    m.transform.position = next;
                }
            }
        }

        void PulseBuffedGoblinsVfx()
        {
            if (VfxPool.Instance == null) return;
            float r2 = WarCryRadius * WarCryRadius;
            Vector3 myPos = transform.position;
            var list = MonsterUnit.Active;
            int count = 0;
            for (int i = 0; i < list.Count && count < 3; i++)
            {
                var m = list[i];
                if (m == null || m == Unit || !m.IsAlive) continue;
                Vector3 d = m.transform.position - myPos;
                d.y = 0f;
                if (d.sqrMagnitude <= r2)
                {
                    VfxPool.Instance.Burst(m.AimPoint, new Color(1f, 0.65f, 0.15f));
                    count++;
                }
            }
        }

        // ------------------------------------------------------------ Ground-Slam Attack

        void UpdateGroundSlam(float dt)
        {
            if (slamAnimTimer > 0f)
            {
                slamAnimTimer -= dt;
                float u = 1f - Mathf.Clamp01(slamAnimTimer / 0.45f);
                if (clubPivot != null)
                {
                    // Wind-up overhead (u: 0 -> 0.55), then smash down into the ground (u: 0.55 -> 1.0)
                    float angle = u < 0.55f
                        ? Mathf.Lerp(0f, -80f, u / 0.55f)
                        : Mathf.Lerp(-80f, 60f, (u - 0.55f) / 0.45f);
                    clubPivot.localRotation = Quaternion.Euler(angle, 0f, 0f);
                }
                if (slamImpactPending && u >= 0.65f)
                {
                    slamImpactPending = false;
                    ExecuteGroundSlamImpact(pendingSlamTarget);
                }
                if (slamAnimTimer <= 0f && clubPivot != null)
                {
                    clubPivot.localRotation = Quaternion.identity;
                }
                return;
            }

            slamCooldownTimer -= dt;
            if (slamCooldownTimer > 0f) return;

            if (TryFindGroundSlamTarget(out Vector3 targetPos))
            {
                BeginGroundSlam(targetPos);
            }
            else
            {
                slamCooldownTimer = 0.25f;
            }
        }

        bool TryFindGroundSlamTarget(out Vector3 targetPos)
        {
            Vector3 myPos = transform.position;
            float bestSq = GroundSlamTriggerRange * GroundSlamTriggerRange;
            bool found = false;
            targetPos = myPos + transform.forward * 0.035f;

            var units = PlacedUnit.All;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.health == null || u.health.IsDead) continue;
                Vector3 d = u.transform.position - myPos;
                d.y = 0f;
                float sq = d.sqrMagnitude;
                if (sq <= bestSq || u.Contacts(myPos, 0.035f))
                {
                    bestSq = sq;
                    targetPos = u.transform.position;
                    found = true;
                }
            }

            var hall = TownHall.Current;
            if (!found && hall != null && hall.health != null && !hall.health.IsDead)
            {
                Vector3 d = hall.transform.position - myPos;
                d.y = 0f;
                float reach = hall.AttackReach + 0.04f;
                if (d.sqrMagnitude <= reach * reach)
                {
                    targetPos = myPos + d.normalized * 0.04f;
                    found = true;
                }
            }

            return found;
        }

        void BeginGroundSlam(Vector3 targetPos)
        {
            slamCooldownTimer = GroundSlamCooldown;
            slamAnimTimer = 0.45f;
            slamImpactPending = true;
            pendingSlamTarget = targetPos;
        }

        void ExecuteGroundSlamImpact(Vector3 targetPos)
        {
            Vector3 impactPos = Vector3.Lerp(transform.position + transform.forward * 0.03f, targetPos, 0.5f);
            float baseDmg = GroundSlamDamage;
            float r2 = GroundSlamRadius * GroundSlamRadius;

            var units = PlacedUnit.All;
            for (int i = units.Count - 1; i >= 0; i--)
            {
                if (i >= units.Count) continue;
                var u = units[i];
                if (u == null || u.health == null || u.health.IsDead) continue;
                Vector3 d = u.transform.position - impactPos;
                d.y = 0f;
                if (d.sqrMagnitude <= r2 || u.Contacts(impactPos, GroundSlamRadius * 0.7f))
                {
                    bool isWallOrBarricade = u.type == UnitType.Wall || u.type == UnitType.Barricade;
                    float dmg = isWallOrBarricade ? baseDmg * WallSiegeMultiplier : baseDmg;
                    u.health.Damage(dmg);
                }
            }

            var hall = TownHall.Current;
            if (hall != null && hall.health != null && !hall.health.IsDead)
            {
                Vector3 d = hall.transform.position - impactPos;
                d.y = 0f;
                float hallReach = GroundSlamRadius + hall.FootprintRadius;
                if (d.sqrMagnitude <= hallReach * hallReach)
                {
                    hall.health.Damage(baseDmg * 0.85f);
                }
            }

            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Ring(impactPos, GroundSlamRadius * 1.4f);
                VfxPool.Instance.Burst(impactPos + Vector3.up * 0.015f, new Color(0.95f, 0.42f, 0.15f));
            }
            ProceduralAudio.Fire(Sfx.Ram, impactPos);
            ProceduralAudio.Fire(Sfx.Thud, impactPos);
            GroundSlammed?.Invoke(this, impactPos);
        }

        void UpdateCrownPulse(float dt)
        {
            if (crownRoot == null) return;
            float s = IsWarCryActive ? 1f + 0.08f * Mathf.Sin(Time.time * 14f) : 1f;
            crownRoot.localScale = Vector3.one * s;
        }

        // ------------------------------------------------------------ Procedural Visuals & Floating HP Bar

        void EnsureVisualsBuilt()
        {
            if (attachmentsRoot != null) return;

            bobRoot = transform.Find("Bob");
            if (bobRoot == null) bobRoot = transform;

            attachmentsRoot = BuildingVisuals.NewRoot(bobRoot, "GoblinKingAttachments");
            var k = new BuildingVisuals.Kit(attachmentsRoot);

            Color gold = new Color(1.00f, 0.82f, 0.18f);
            Color darkGold = new Color(0.78f, 0.58f, 0.10f);
            Color crimson = new Color(0.75f, 0.12f, 0.12f);
            Color emerald = new Color(0.18f, 0.85f, 0.35f);
            Color iron = new Color(0.24f, 0.25f, 0.28f);
            Color darkIron = new Color(0.15f, 0.16f, 0.18f);
            Color bone = new Color(0.92f, 0.88f, 0.78f);
            Color darkWood = new Color(0.32f, 0.19f, 0.09f);

            // 1. Procedural Golden Crown with Velvet Cap, Spikes & Jewels
            crownRoot = BuildingVisuals.NewRoot(attachmentsRoot, "Crown");
            crownRoot.localPosition = new Vector3(0f, 0.030f, 0f);
            var ck = new BuildingVisuals.Kit(crownRoot);
            ck.Cyl(Vector3.zero, 0.0108f, 0.0055f, gold);
            ck.Cyl(new Vector3(0f, 0.001f, 0f), 0.0112f, 0.0015f, darkGold);
            ck.Ellipsoid(new Vector3(0f, 0.003f, 0f), new Vector3(0.018f, 0.007f, 0.018f), crimson);
            const int CrownSpikes = 6;
            for (int i = 0; i < CrownSpikes; i++)
            {
                float a = (i / (float)CrownSpikes) * Mathf.PI * 2f;
                Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                ck.Cone(dir * 0.0092f + Vector3.up * 0.005f, 0.0024f, 0.0075f, gold);
                ck.Ball(dir * 0.011f + Vector3.up * 0.0028f, 0.0026f, i % 2 == 0 ? crimson : emerald);
            }
            ck.Ball(new Vector3(0f, 0.008f, 0f), 0.0032f, gold);

            // 2. Heavy Armor: Iron Chestplate, Belt, Spiked Pauldrons & Royal War Cape
            k.Ellipsoid(new Vector3(0f, 0.014f, 0.003f), new Vector3(0.031f, 0.020f, 0.029f), iron);
            k.Box(new Vector3(0f, 0.016f, 0.0145f), new Vector3(0.018f, 0.013f, 0.003f), darkIron);
            k.Box(new Vector3(0f, 0.016f, 0.0155f), new Vector3(0.008f, 0.008f, 0.002f), gold, new Vector3(0f, 0f, 45f));

            // War Belt + Golden Buckle
            k.Cyl(new Vector3(0f, 0.0065f, 0f), 0.0156f, 0.0035f, darkIron);
            k.Box(new Vector3(0f, 0.0082f, 0.0155f), new Vector3(0.007f, 0.005f, 0.002f), gold);

            // Spiked Shoulder Pauldrons
            for (int sx = -1; sx <= 1; sx += 2)
            {
                Vector3 sh = new Vector3(sx * 0.0165f, 0.021f, 0.001f);
                k.Ellipsoid(sh, new Vector3(0.013f, 0.010f, 0.014f), iron);
                k.Ball(sh + new Vector3(sx * 0.002f, -0.002f, 0.004f), 0.0035f, gold);
                var sp1 = k.Cone(sh + new Vector3(sx * 0.003f, 0.004f, 0f), 0.0022f, 0.0085f, bone);
                sp1.transform.localRotation = Quaternion.Euler(0f, 0f, -sx * 35f);
                var sp2 = k.Cone(sh + new Vector3(sx * 0.001f, 0.003f, -0.003f), 0.0018f, 0.007f, bone);
                sp2.transform.localRotation = Quaternion.Euler(-20f, 0f, -sx * 25f);
            }

            // Royal Crimson War Cape
            k.Box(new Vector3(0f, 0.015f, -0.0155f), new Vector3(0.024f, 0.023f, 0.0025f), crimson, new Vector3(14f, 0f, 0f));
            k.Ball(new Vector3(-0.010f, 0.023f, -0.011f), 0.004f, gold);
            k.Ball(new Vector3(0.010f, 0.023f, -0.011f), 0.004f, gold);

            // 3. Heavy Spiked War-Club (on animated pivot for Ground-Slam)
            clubPivot = BuildingVisuals.NewRoot(attachmentsRoot, "ClubPivot");
            clubPivot.localPosition = new Vector3(0.0185f, 0.016f, 0.006f);
            var clubKit = new BuildingVisuals.Kit(clubPivot);
            // Grip & Shaft angled slightly forward
            clubKit.CylRot(new Vector3(0f, 0.011f, 0.008f), 0.0022f, 0.028f, darkWood, new Vector3(25f, 0f, 0f));
            // Heavy Iron-Banded Club Head
            Vector3 headCenter = new Vector3(0f, 0.023f, 0.014f);
            clubKit.Ellipsoid(headCenter, new Vector3(0.012f, 0.018f, 0.012f), darkWood);
            clubKit.CylRot(headCenter, 0.0065f, 0.004f, iron, new Vector3(25f, 0f, 0f));
            clubKit.Ball(headCenter + new Vector3(0f, 0.008f, 0.003f), 0.0075f, iron);
            // Spikes on Club Head
            for (int i = 0; i < 4; i++)
            {
                float a = i * 90f;
                Quaternion rot = Quaternion.Euler(25f, 0f, 0f) * Quaternion.Euler(0f, a, 90f);
                Vector3 offset = rot * Vector3.up * 0.005f;
                var sp = clubKit.Cone(headCenter + offset, 0.002f, 0.007f, bone);
                sp.transform.localRotation = rot;
            }

            // 4. Floating 3D Boss HP Bar (child of transform so it doesn't tilt excessively with Bob)
            BuildFloatingHpBar();

            // 5. War-Cry Aura Ring
            BuildAuraRing();
        }

        void BuildFloatingHpBar()
        {
            hpBarRoot = BuildingVisuals.NewRoot(transform, "BossHpBar");
            hpBarRoot.localPosition = new Vector3(0f, 0.052f, 0f);
            var hk = new BuildingVisuals.Kit(hpBarRoot);

            Color goldFrame = new Color(0.95f, 0.76f, 0.20f);
            Color darkBg = new Color(0.10f, 0.07f, 0.07f);
            Color hpRed = new Color(0.92f, 0.18f, 0.14f);

            var frameGo = hk.Box(new Vector3(0f, 0f, 0.0006f), new Vector3(HpBarWidth + 0.004f, 0.0075f, 0.001f), goldFrame);
            frameGo.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(goldFrame);

            var bgGo = hk.Box(new Vector3(0f, 0f, 0.0003f), new Vector3(HpBarWidth, 0.0052f, 0.001f), darkBg);
            bgGo.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(darkBg);

            var fillGo = hk.Box(Vector3.zero, new Vector3(HpBarWidth, 0.0046f, 0.0012f), hpRed);
            hpFill = fillGo.transform;
            hpFillRenderer = fillGo.GetComponent<MeshRenderer>();
            hpFillRenderer.sharedMaterial = Mats.Unlit(hpRed);

            // Mini crown emblem on the left of the boss HP bar
            var pip = hk.Cone(new Vector3(-HpBarWidth * 0.5f - 0.005f, -0.002f, 0f), 0.003f, 0.0055f, goldFrame);
            pip.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(goldFrame);

            // World-space TMP title above bar if default font is loaded
            if (TMP_Settings.defaultFontAsset != null)
            {
                var labelGo = new GameObject("BossName");
                labelGo.transform.SetParent(hpBarRoot, false);
                labelGo.transform.localPosition = new Vector3(0f, 0.0075f, 0f);
                labelGo.transform.localScale = Vector3.one * 0.0012f;
                var tmp = labelGo.AddComponent<TextMeshPro>();
                tmp.font = TMP_Settings.defaultFontAsset;
                tmp.text = "GOBLIN KING";
                tmp.fontSize = 36f;
                tmp.fontStyle = FontStyles.Bold;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color = goldFrame;
                tmp.textWrappingMode = TextWrappingModes.NoWrap;
                hpLabel = tmp;
            }
        }

        void UpdateHpBar()
        {
            if (hpFill == null || Unit == null || Unit.health == null) return;
            float frac = Mathf.Clamp01(Unit.health.Fraction);
            float w = Mathf.Max(0.0005f, HpBarWidth * frac);
            hpFill.localScale = new Vector3(w, 0.0046f, 0.0014f);
            hpFill.localPosition = new Vector3((w - HpBarWidth) * 0.5f, 0f, 0f);

            if (hpFillRenderer != null)
            {
                Color c = IsWarCryActive
                    ? new Color(1.0f, 0.62f, 0.12f)
                    : Color.Lerp(new Color(0.95f, 0.15f, 0.12f), new Color(0.88f, 0.22f, 0.16f), frac);
                hpFillRenderer.sharedMaterial = Mats.Unlit(c);
            }
        }

        void BuildAuraRing()
        {
            var ringGo = new GameObject("WarCryAuraRing");
            ringGo.transform.SetParent(transform, false);
            ringGo.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
            auraRing = ringGo.AddComponent<LineRenderer>();
            auraRing.useWorldSpace = true;
            auraRing.loop = true;
            auraRing.positionCount = AuraSegments;
            auraRing.sharedMaterial = Mats.Unlit(new Color(1f, 0.68f, 0.14f, 1f));
            auraRing.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            auraRing.receiveShadows = false;
            auraRing.numCornerVertices = 0;
            auraRing.startWidth = 0.005f;
            auraRing.endWidth = 0.005f;
            auraRing.enabled = false;
        }
    }

    /// <summary>
    /// Helper component that spawns extra scaling waves of monsters/rams in Endless Siege Mode (Waves 4, 5, 6...)
    /// and scales their HP so endless waves grow progressively more intense without requiring edits to WaveSpawner.cs.
    /// </summary>
    public class EndlessWaveScaler : MonoBehaviour
    {
        static EndlessWaveScaler instance;

        int waveIndex;
        int extraRemaining;
        float timer;
        float interval = 1.35f;
        Vector3 hallPos;
        ITableSurface surface;
        readonly HashSet<int> buffedSerials = new HashSet<int>();

        public static void BeginEndlessWave(int waveIdx, Vector3 hall, ITableSurface surf)
        {
            if (waveIdx < 3) return;
            if (instance == null)
            {
                var go = new GameObject("EndlessWaveScaler");
                instance = go.AddComponent<EndlessWaveScaler>();
            }
            instance.StartScaling(waveIdx, hall, surf);
        }

        void StartScaling(int waveIdx, Vector3 hall, ITableSurface surf)
        {
            waveIndex = waveIdx;
            hallPos = hall;
            surface = surf;
            extraRemaining = GoblinKingBoss.GetEndlessBonusMonsterCount(waveIdx);
            interval = Mathf.Max(0.55f, 1.35f - (waveIdx - 3) * 0.08f);
            timer = 1.0f;
            buffedSerials.Clear();
            enabled = true;
        }

        void Update()
        {
            var gd = GameDirector.Instance;
            if (gd == null || gd.Phase != GamePhase.Wave)
            {
                extraRemaining = 0;
                enabled = false;
                return;
            }

            // Apply endless HP scaling to newly spawned regular monsters
            float hpMul = GoblinKingBoss.GetEndlessHpMultiplier(waveIndex);
            if (hpMul > 1.01f)
            {
                var list = MonsterUnit.Active;
                for (int i = 0; i < list.Count; i++)
                {
                    var m = list[i];
                    if (m == null || !m.IsAlive || GoblinKingBoss.IsBoss(m)) continue;
                    if (buffedSerials.Add(m.Serial) && m.health != null)
                    {
                        m.health.Init(m.health.Max * hpMul);
                    }
                }
            }

            if (extraRemaining <= 0) return;
            timer -= Time.deltaTime;
            if (timer > 0f) return;
            timer = interval;
            extraRemaining--;

            SpawnExtraMonster();
        }

        void SpawnExtraMonster()
        {
            Vector3 spawnPos = hallPos + Vector3.forward * 0.3f;
            var ws = WaveSpawner.Instance;
            if (ws != null && ws.SpawnPoints != null && ws.SpawnPoints.Count > 0)
            {
                spawnPos = ws.SpawnPoints[UnityEngine.Random.Range(0, ws.SpawnPoints.Count)];
            }

            SplinePath path;
            if (ws != null && ws.RoomMode)
            {
                float floorY = RoomCornerSpawns.Existing != null ? RoomCornerSpawns.Existing.FloorY : spawnPos.y;
                path = SplinePath.BuildRoute(spawnPos, hallPos, surface, floorY);
            }
            else
            {
                path = SplinePath.Build(spawnPos, hallPos, surface);
            }

            bool isRam = (extraRemaining % 5 == 0);
            var m = MonsterUnit.Spawn(path, isRam);
            if (m != null && m.health != null)
            {
                buffedSerials.Add(m.Serial);
                m.health.Init(m.health.Max * GoblinKingBoss.GetEndlessHpMultiplier(waveIndex));
            }
        }
    }
}
