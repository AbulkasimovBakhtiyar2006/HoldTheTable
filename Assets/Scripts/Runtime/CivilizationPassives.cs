#pragma warning disable CS0619
using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Feature 1: Making Civilization Choice Impact Gameplay.
    /// Implements runtime passives, visual effects, and helper methods for all 3 civilizations:
    /// - Celestial Dynasty: Firework arrows with micro-splash AoE + spark/fire VFX; Shockwave resets at the end of every wave.
    /// - Verdant Clans: Farms yield +40% wood; Walls and Barricades regenerate HP between waves with living-wood pulse + regen tick.
    /// - Iron Dominion: All player structures receive +35% max HP; Mines yield +35% gold and periodically zap nearby monsters with a procedural lightning arc.
    /// Owner: civ-bonuses.
    /// </summary>
    public class CivilizationPassives : MonoBehaviour
    {
        public static CivilizationPassives Instance { get; private set; }

        static Transform vfxRoot;
        static SimplePool<FireworkArrowProjectile> fireworkPool;
        static readonly List<FireworkArrowProjectile> liveFireworks = new List<FireworkArrowProjectile>();

        struct LightningArc
        {
            public LineRenderer lr;
            public Vector3 from;
            public Vector3 to;
            public float elapsed;
            public float duration;
            public uint seed;
            public bool active;
        }

        struct PulseRing
        {
            public LineRenderer lr;
            public Vector3 center;
            public float maxRadius;
            public float elapsed;
            public float duration;
            public bool active;
        }

        const int ArcSegments = 9;
        const int RingSegments = 28;
        const int MaxPooledArcs = 8;
        const int MaxPooledRings = 8;

        static readonly List<LightningArc> arcs = new List<LightningArc>(MaxPooledArcs);
        static readonly List<PulseRing> pulseRings = new List<PulseRing>(MaxPooledRings);
        static readonly Vector3[] arcBuffer = new Vector3[ArcSegments];
        static readonly Vector3[] ringBuffer = new Vector3[RingSegments];

        static int lastWaveClearFrame = -100;
        static uint rngState = 0x9E3779B9u;

        bool directorSubscribed;
        GamePhase lastObservedPhase = GamePhase.CivilizationSetup;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetStatics()
        {
            Instance = null;
            vfxRoot = null;
            fireworkPool = null;
            liveFireworks.Clear();
            arcs.Clear();
            pulseRings.Clear();
            lastWaveClearFrame = -100;
            rngState = 0x9E3779B9u;
        }

        public static CivilizationPassives EnsureInstance()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("CivilizationPassives");
            Instance = go.AddComponent<CivilizationPassives>();
            return Instance;
        }

        static Transform EnsureVfxRoot()
        {
            EnsureInstance();
            if (vfxRoot == null)
            {
                vfxRoot = new GameObject("CivPassivesVfxPool").transform;
                if (Instance != null) vfxRoot.SetParent(Instance.transform, false);
            }
            return vfxRoot;
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        void OnDestroy()
        {
            if (directorSubscribed && GameDirector.Instance != null)
            {
                GameDirector.Instance.PhaseChanged -= OnDirectorPhaseChanged;
                GameDirector.Instance.GameReset -= OnDirectorReset;
            }
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            TrySubscribeDirector();
            float dt = Time.deltaTime;
            UpdateLightningArcs(dt);
            UpdatePulseRings(dt);

            // Auto-tick any live placed units that were not already ticked this frame
            var units = PlacedUnit.All;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u != null) TickUnitPassive(u, dt);
            }

            // Ensure TownHall has Iron Dominion +35% max HP applied when founded
            if (TownHall.Current != null) ApplyToTownHall(TownHall.Current);
        }

        void TrySubscribeDirector()
        {
            var gd = GameDirector.Instance;
            if (gd == null || directorSubscribed) return;
            gd.PhaseChanged += OnDirectorPhaseChanged;
            gd.GameReset += OnDirectorReset;
            lastObservedPhase = gd.Phase;
            directorSubscribed = true;
        }

        void OnDirectorPhaseChanged(GamePhase newPhase)
        {
            // When transitioning out of Wave into Upgrade or Victory, trigger wave-cleared civilization bonuses
            if (lastObservedPhase == GamePhase.Wave && (newPhase == GamePhase.Upgrade || newPhase == GamePhase.Victory))
            {
                OnWaveCleared();
            }
            lastObservedPhase = newPhase;
        }

        void OnDirectorReset()
        {
            DespawnAllPassivesVfx();
        }

        // ====================================================================
        // 1. CIVILIZATION STAT & MULTIPLIER HELPERS
        // ====================================================================

        /// <summary>Resolves the given civilization override or falls back to the active player civilization.</summary>
        public static CivilizationDefinition Resolve(CivilizationDefinition civOverride = null)
        {
            return civOverride ?? CivilizationCatalog.Active;
        }

        /// <summary>Returns structure max HP multiplier: 1.35f (+35%) for Iron Dominion, 1.0f otherwise.</summary>
        public static float GetStructureHpMultiplier(CivilizationDefinition civOverride = null)
        {
            var civ = Resolve(civOverride);
            return civ != null && civ.structureHpMultiplier > 0f ? civ.structureHpMultiplier : 1f;
        }

        /// <summary>Returns structure max HP with the active civilization multiplier applied (+35% for Iron Dominion).</summary>
        public static float GetEffectiveStructureHp(float baseHp, CivilizationDefinition civOverride = null)
        {
            return baseHp * GetStructureHpMultiplier(civOverride);
        }

        /// <summary>Returns Farm timber production multiplier: 1.40f (+40%) for Verdant Clans, 1.0f otherwise.</summary>
        public static float GetFarmTimberMultiplier(CivilizationDefinition civOverride = null)
        {
            var civ = Resolve(civOverride);
            return civ != null && civ.farmTimberMultiplier > 0f ? civ.farmTimberMultiplier : 1f;
        }

        /// <summary>Returns Mine gold production multiplier: 1.35f (+35%) for Iron Dominion, 1.0f otherwise.</summary>
        public static float GetMineGoldMultiplier(CivilizationDefinition civOverride = null)
        {
            var civ = Resolve(civOverride);
            return civ != null && civ.mineGoldMultiplier > 0f ? civ.mineGoldMultiplier : 1f;
        }

        /// <summary>Returns a unit's effective timber/sec including Verdant Clans' +40% Farm bonus.</summary>
        public static float GetUnitTimberPerSec(UnitDef def, CivilizationDefinition civOverride = null)
        {
            if (def == null || def.timberPerSec <= 0f) return 0f;
            return def.timberPerSec * GetFarmTimberMultiplier(civOverride);
        }

        /// <summary>Returns a unit's effective gold/sec including Iron Dominion's +35% Mine bonus.</summary>
        public static float GetUnitGoldPerSec(UnitDef def, CivilizationDefinition civOverride = null)
        {
            if (def == null || def.goldPerSec <= 0f) return 0f;
            return def.goldPerSec * GetMineGoldMultiplier(civOverride);
        }

        public static bool HasFireworkArrows(CivilizationDefinition civOverride = null) => Resolve(civOverride).hasFireworkArrows;
        public static bool ShouldResetShockwaveOnWaveClear(CivilizationDefinition civOverride = null) => Resolve(civOverride).resetShockwaveOnWaveClear;
        public static bool HasLivingWoodRegen(CivilizationDefinition civOverride = null) => Resolve(civOverride).hasLivingWoodRegen;
        public static bool HasMineLightningZap(CivilizationDefinition civOverride = null) => Resolve(civOverride).hasMineLightningZap;

        /// <summary>True when the game is between combat waves (BuildPhase or Upgrade phase).</summary>
        public static bool IsBetweenWaves()
        {
            var gd = GameDirector.Instance;
            if (gd == null) return true;
            return gd.Phase == GamePhase.BuildPhase || gd.Phase == GamePhase.Upgrade;
        }

        // ====================================================================
        // 2. STRUCTURE HP & PASSIVE COMPONENT ATTACHMENT
        // ====================================================================

        /// <summary>
        /// Call in PlacedUnit.Init(UnitType t, CivilizationDefinition civOverride = null).
        /// Idempotently applies Iron Dominion's +35% max HP bonus and attaches per-unit civilization passive visuals/logic.
        /// </summary>
        public static void ApplyToUnit(PlacedUnit unit, CivilizationDefinition civOverride = null)
        {
            if (unit == null) return;
            EnsureInstance();
            var civ = Resolve(civOverride);

            // Idempotent max HP application (+35% for Iron Dominion)
            if (unit.health != null && unit.def != null)
            {
                float expectedHp = ComputeExpectedUnitMaxHp(unit.def, civ);
                if (Mathf.Abs(unit.health.Max - expectedHp) > 0.05f)
                {
                    unit.health.Init(expectedHp);
                }
            }

            var comp = unit.GetComponent<CivilizationUnitPassive>();
            if (comp == null) comp = unit.gameObject.AddComponent<CivilizationUnitPassive>();
            comp.Bind(unit, civ);
        }

        /// <summary>
        /// Call in PlacedUnit.OnUpgradesChanged().
        /// Preserves current health fraction while applying both Quartermaster HP upgrades and Iron Dominion's +35% structure HP bonus.
        /// </summary>
        public static void RefreshUnitMaxHp(PlacedUnit unit, CivilizationDefinition civOverride = null)
        {
            if (unit == null || unit.health == null || unit.def == null || unit.health.IsDead) return;
            var civ = Resolve(civOverride);
            float expectedHp = ComputeExpectedUnitMaxHp(unit.def, civ);
            unit.health.SetMaxKeepFraction(expectedHp);
        }

        static float ComputeExpectedUnitMaxHp(UnitDef def, CivilizationDefinition civ)
        {
            if (def == null) return 10f;
            float civMul = GetStructureHpMultiplier(civ);
            float rawUpgradeHp = def.hp * UnitDef.HpMul(def.type);
            // Guard against double-application if UnitDef.EffectiveHp or UnitDef.HpMul already includes civMul
            float eff = def.EffectiveHp;
            if (civMul > 1.001f && Mathf.Abs(eff - rawUpgradeHp * civMul) <= 0.05f)
                return eff;
            return eff * civMul;
        }

        /// <summary>
        /// Call in TownHall.Init() or GameDirector.OnHeadquartersPlaced().
        /// Idempotently applies Iron Dominion's +35% structure max HP bonus to the TownHall.
        /// </summary>
        public static void ApplyToTownHall(TownHall hall, CivilizationDefinition civOverride = null)
        {
            if (hall == null || hall.health == null || hall.health.IsDead) return;
            var civ = Resolve(civOverride ?? hall.civ);
            float civMul = GetStructureHpMultiplier(civ);
            if (civMul <= 1.001f) return;

            // Only apply if current max HP is still at an un-boosted baseline (e.g. 20, or Age-scaled 30/40)
            float curMax = hall.health.Max;
            float baseExpected = TownHall.MaxHp * civMul;
            if (Mathf.Abs(curMax - TownHall.MaxHp) < 0.05f)
            {
                hall.health.Init(baseExpected);
            }
        }

        // ====================================================================
        // 3. CELESTIAL DYNASTY: FIREWORK ARROWS & WAVE-END SHOCKWAVE RESET
        // ====================================================================

        /// <summary>
        /// Drop-in replacement for Projectile.Fire(origin, target, damage) in PlacedUnit.Update().
        /// Fires an explosive firework rocket arrow with micro-splash AoE + spark VFX for Celestial Dynasty,
        /// or a standard arrow for other civilizations.
        /// </summary>
        public static void FireTowerProjectile(Vector3 origin, MonsterUnit target, float damage, CivilizationDefinition civOverride = null)
        {
            if (target == null) return;
            var civ = Resolve(civOverride);
            if (civ != null && civ.hasFireworkArrows)
            {
                EnsureInstance();
                FireworkArrowProjectile.Spawn(origin, target, damage, civ.fireArrowSplashRadius, civ.fireArrowSplashDamageRatio);
            }
            else
            {
                Projectile.Fire(origin, target, damage);
            }
        }

        /// <summary>
        /// Helper for ShockwaveAbility: resets Shockwave cooldown (and restores wave charge) at the end of a wave for Celestial Dynasty.
        /// Returns true if the reset was triggered.
        /// </summary>
        public static bool TryResetShockwaveOnWaveClear(ref float lastFire, ref int used, bool restoreCharge = true, CivilizationDefinition civOverride = null)
        {
            var civ = Resolve(civOverride);
            if (civ == null || !civ.resetShockwaveOnWaveClear) return false;
            lastFire = -10f;
            if (restoreCharge && used > 0) used = 0;
            PlayCelestialShockwaveResetVfx();
            return true;
        }

        /// <summary>
        /// Call in GameDirector.OnWaveCleared().
        /// Executes end-of-wave civilization passives:
        /// - Celestial Dynasty: Automatically resets Shockwave cooldown &amp; charges with a golden-jade Mandate ring VFX.
        /// - Verdant Clans: Triggers a living-wood wave-clear heal pulse across all surviving Walls and Barricades.
        /// - Iron Dominion: Emits an overcharge spark pulse on surviving Mines.
        /// </summary>
        public static void OnWaveCleared(CivilizationDefinition civOverride = null)
        {
            if (Time.frameCount == lastWaveClearFrame) return;
            lastWaveClearFrame = Time.frameCount;

            EnsureInstance();
            var civ = Resolve(civOverride);
            if (civ == null) return;

            if (civ.resetShockwaveOnWaveClear)
            {
                if (ShockwaveAbility.Instance != null)
                {
                    ShockwaveAbility.Instance.ResetCharges();
                }
                PlayCelestialShockwaveResetVfx();
            }

            if (civ.hasLivingWoodRegen)
            {
                var units = PlacedUnit.All;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || u.health == null || u.health.IsDead) continue;
                    if (u.type != UnitType.Wall && u.type != UnitType.Barricade) continue;
                    if (u.health.Current < u.health.Max)
                    {
                        u.health.Heal(u.health.Max * 0.25f);
                        SpawnVerdantRegenVfx(u.transform.position, u.Footprint);
                    }
                }
            }

            if (civ.hasMineLightningZap)
            {
                var units = PlacedUnit.All;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || u.health == null || u.health.IsDead || u.type != UnitType.Mine) continue;
                    SpawnPulseRing(u.transform.position + u.transform.up * 0.01f, 0.09f, new Color(0.35f, 0.82f, 1f), 0.35f);
                }
            }
        }

        static void PlayCelestialShockwaveResetVfx()
        {
            Vector3 center = TownHall.Current != null ? TownHall.Current.transform.position : Vector3.zero;
            if (TownHall.Current != null)
            {
                if (VfxPool.Instance != null)
                {
                    VfxPool.Instance.Ring(center, 0.22f);
                    VfxPool.Instance.Burst(center + Vector3.up * 0.08f, new Color(0.98f, 0.80f, 0.22f));
                }
                SpawnPulseRing(center + Vector3.up * 0.008f, 0.20f, new Color(0.96f, 0.76f, 0.22f), 0.45f);
                ProceduralAudio.Fire(Sfx.Coin, center);
            }
        }

        // ====================================================================
        // 4. PER-UNIT & BETWEEN-WAVE PASSIVE TICKS (VERDANT REGEN & IRON ZAP)
        // ====================================================================

        /// <summary>
        /// Call in PlacedUnit.Update() (or let CivilizationUnitPassive / CivilizationPassives auto-tick).
        /// Frame-deduplicated per unit so calling from multiple places in the same frame is completely safe.
        /// </summary>
        public static void TickUnitPassive(PlacedUnit unit, float dt, CivilizationDefinition civOverride = null)
        {
            if (unit == null || unit.health == null || unit.health.IsDead) return;
            EnsureInstance();
            var comp = unit.GetComponent<CivilizationUnitPassive>();
            if (comp == null)
            {
                ApplyToUnit(unit, civOverride);
                comp = unit.GetComponent<CivilizationUnitPassive>();
            }
            if (comp != null) comp.Tick(dt, civOverride);
        }

        /// <summary>Optional global tick helper for GameDirector.Update().</summary>
        public static void TickBetweenWaves(float dt, CivilizationDefinition civOverride = null)
        {
            var units = PlacedUnit.All;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u != null) TickUnitPassive(u, dt, civOverride);
            }
        }

        // ====================================================================
        // 5. PROCEDURAL VFX HELPERS (LIGHTNING ARCS, PULSE RINGS, VERDANT BURSTS)
        // ====================================================================

        public static void SpawnVerdantRegenVfx(Vector3 worldPos, float footprint)
        {
            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Burst(worldPos + Vector3.up * 0.03f, new Color(0.30f, 0.92f, 0.38f));
            }
            SpawnPulseRing(worldPos + Vector3.up * 0.005f, Mathf.Max(0.055f, footprint * 1.15f), new Color(0.28f, 0.90f, 0.36f), 0.42f);
        }

        public static void SpawnLightningArc(Vector3 from, Vector3 to, float duration = 0.20f)
        {
            EnsureVfxRoot();
            int idx = -1;
            for (int i = 0; i < arcs.Count; i++)
            {
                if (!arcs[i].active) { idx = i; break; }
            }

            if (idx < 0)
            {
                if (arcs.Count < MaxPooledArcs)
                {
                    var go = new GameObject("LightningArc_" + arcs.Count);
                    go.transform.SetParent(vfxRoot, false);
                    var lr = go.AddComponent<LineRenderer>();
                    lr.useWorldSpace = true;
                    lr.loop = false;
                    lr.positionCount = ArcSegments;
                    lr.sharedMaterial = Mats.Unlit(new Color(0.42f, 0.88f, 1f));
                    lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lr.receiveShadows = false;
                    lr.numCornerVertices = 2;
                    lr.numCapVertices = 2;
                    lr.enabled = false;
                    idx = arcs.Count;
                    arcs.Add(new LightningArc { lr = lr });
                }
                else
                {
                    idx = 0;
                }
            }

            var arc = arcs[idx];
            arc.from = from;
            arc.to = to;
            arc.elapsed = 0f;
            arc.duration = Mathf.Max(0.05f, duration);
            arc.seed = NextRandUint();
            arc.active = true;
            if (arc.lr != null)
            {
                arc.lr.enabled = true;
                UpdateSingleArcGeometry(ref arc);
            }
            arcs[idx] = arc;
        }

        public static void SpawnPulseRing(Vector3 center, float maxRadius, Color color, float duration = 0.40f)
        {
            EnsureVfxRoot();
            int idx = -1;
            for (int i = 0; i < pulseRings.Count; i++)
            {
                if (!pulseRings[i].active) { idx = i; break; }
            }

            if (idx < 0)
            {
                if (pulseRings.Count < MaxPooledRings)
                {
                    var go = new GameObject("CivPulseRing_" + pulseRings.Count);
                    go.transform.SetParent(vfxRoot, false);
                    var lr = go.AddComponent<LineRenderer>();
                    lr.useWorldSpace = true;
                    lr.loop = true;
                    lr.positionCount = RingSegments;
                    lr.sharedMaterial = Mats.Unlit(color);
                    lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lr.receiveShadows = false;
                    lr.numCornerVertices = 0;
                    lr.enabled = false;
                    idx = pulseRings.Count;
                    pulseRings.Add(new PulseRing { lr = lr });
                }
                else
                {
                    idx = 0;
                }
            }

            var ring = pulseRings[idx];
            ring.center = center;
            ring.maxRadius = maxRadius;
            ring.elapsed = 0f;
            ring.duration = Mathf.Max(0.08f, duration);
            ring.active = true;
            if (ring.lr != null)
            {
                ring.lr.sharedMaterial = Mats.Unlit(color);
                ring.lr.enabled = true;
                UpdateSingleRingGeometry(ref ring);
            }
            pulseRings[idx] = ring;
        }

        static void UpdateLightningArcs(float dt)
        {
            for (int i = 0; i < arcs.Count; i++)
            {
                var arc = arcs[i];
                if (!arc.active) continue;
                arc.elapsed += dt;
                if (arc.elapsed >= arc.duration || arc.lr == null)
                {
                    arc.active = false;
                    if (arc.lr != null) arc.lr.enabled = false;
                    arcs[i] = arc;
                    continue;
                }
                // Re-seed slightly every frame so the lightning crackles dynamically
                arc.seed ^= (uint)(Time.frameCount * 2654435761u);
                UpdateSingleArcGeometry(ref arc);
                arcs[i] = arc;
            }
        }

        static void UpdateSingleArcGeometry(ref LightningArc arc)
        {
            Vector3 delta = arc.to - arc.from;
            float len = delta.magnitude;
            Vector3 dir = len > 1e-5f ? delta / len : Vector3.forward;
            Vector3 side1 = Vector3.Cross(dir, Vector3.up);
            if (side1.sqrMagnitude < 1e-4f) side1 = Vector3.Cross(dir, Vector3.right);
            side1.Normalize();
            Vector3 side2 = Vector3.Cross(dir, side1).normalized;

            float jitterAmp = Mathf.Clamp(len * 0.16f, 0.004f, 0.022f);
            uint s = arc.seed;

            arcBuffer[0] = arc.from;
            arcBuffer[ArcSegments - 1] = arc.to;

            for (int k = 1; k < ArcSegments - 1; k++)
            {
                float u = k / (float)(ArcSegments - 1);
                float envelope = Mathf.Sin(u * Mathf.PI); // 0 at endpoints, 1 in middle
                s = StepRng(s);
                float r1 = ((s & 0xFFFF) / 32767.5f) - 1f;
                s = StepRng(s);
                float r2 = ((s & 0xFFFF) / 32767.5f) - 1f;
                Vector3 basePt = Vector3.Lerp(arc.from, arc.to, u);
                arcBuffer[k] = basePt + (side1 * r1 + side2 * (r2 * 0.7f + 0.25f)) * (jitterAmp * envelope);
            }

            arc.lr.SetPositions(arcBuffer);
            float t = Mathf.Clamp01(arc.elapsed / arc.duration);
            float width = Mathf.Lerp(0.0055f, 0.0012f, t);
            arc.lr.startWidth = width;
            arc.lr.endWidth = width * 0.75f;
        }

        static void UpdatePulseRings(float dt)
        {
            for (int i = 0; i < pulseRings.Count; i++)
            {
                var ring = pulseRings[i];
                if (!ring.active) continue;
                ring.elapsed += dt;
                if (ring.elapsed >= ring.duration || ring.lr == null)
                {
                    ring.active = false;
                    if (ring.lr != null) ring.lr.enabled = false;
                    pulseRings[i] = ring;
                    continue;
                }
                UpdateSingleRingGeometry(ref ring);
                pulseRings[i] = ring;
            }
        }

        static void UpdateSingleRingGeometry(ref PulseRing ring)
        {
            float u = Mathf.Clamp01(ring.elapsed / ring.duration);
            float ease = 1f - (1f - u) * (1f - u);
            float rad = Mathf.Lerp(0.2f, 1f, ease) * ring.maxRadius;
            for (int k = 0; k < RingSegments; k++)
            {
                float a = k / (float)RingSegments * Mathf.PI * 2f;
                ringBuffer[k] = ring.center + new Vector3(Mathf.Cos(a) * rad, 0f, Mathf.Sin(a) * rad);
            }
            ring.lr.SetPositions(ringBuffer);
            float w = Mathf.Lerp(0.006f, 0.0008f, u);
            ring.lr.startWidth = w;
            ring.lr.endWidth = w;
        }

        public static void DespawnAllPassivesVfx()
        {
            for (int i = liveFireworks.Count - 1; i >= 0; i--)
            {
                var fw = liveFireworks[i];
                if (fw != null && fireworkPool != null) fireworkPool.Release(fw);
            }
            liveFireworks.Clear();

            for (int i = 0; i < arcs.Count; i++)
            {
                var a = arcs[i];
                a.active = false;
                if (a.lr != null) a.lr.enabled = false;
                arcs[i] = a;
            }

            for (int i = 0; i < pulseRings.Count; i++)
            {
                var r = pulseRings[i];
                r.active = false;
                if (r.lr != null) r.lr.enabled = false;
                pulseRings[i] = r;
            }
        }

        static uint StepRng(uint s)
        {
            s ^= s << 13;
            s ^= s >> 17;
            s ^= s << 5;
            return s == 0 ? 1234567u : s;
        }

        static uint NextRandUint()
        {
            rngState = StepRng(rngState);
            return rngState;
        }

        // ====================================================================
        // 6. POOLED CELESTIAL FIREWORK ARROW PROJECTILE
        // ====================================================================

        sealed class FireworkArrowProjectile : MonoBehaviour
        {
            const float Speed = 0.95f;
            const float ArcHeight = 0.045f;
            const int TrailPoints = 5;

            MonsterUnit target;
            int targetSerial;
            Vector3 from;
            Vector3 lastTargetPos;
            float damage;
            float splashRadius;
            float splashRatio;
            float t;
            float duration;
            float sparkTimer;
            LineRenderer trail;
            readonly Vector3[] trailHistory = new Vector3[TrailPoints];

            public static void Spawn(Vector3 origin, MonsterUnit target, float damage, float splashRadius, float splashRatio)
            {
                if (target == null) return;
                ProceduralAudio.Fire(Sfx.Arrow, origin);
                EnsureVfxRoot();
                if (fireworkPool == null) fireworkPool = new SimplePool<FireworkArrowProjectile>(Create, 6);
                var p = fireworkPool.Get();
                p.from = origin;
                p.target = target;
                p.targetSerial = target.Serial;
                p.lastTargetPos = target.AimPoint;
                p.damage = damage;
                p.splashRadius = Mathf.Max(0.03f, splashRadius);
                p.splashRatio = Mathf.Clamp(splashRatio, 0.1f, 1.5f);
                p.t = 0f;
                p.sparkTimer = 0f;
                p.duration = Mathf.Max(0.08f, Vector3.Distance(origin, p.lastTargetPos) / Speed);
                p.transform.position = origin;
                for (int i = 0; i < TrailPoints; i++) p.trailHistory[i] = origin;
                if (p.trail != null)
                {
                    p.trail.enabled = true;
                    p.trail.SetPositions(p.trailHistory);
                }
                liveFireworks.Add(p);
            }

            static FireworkArrowProjectile Create()
            {
                EnsureVfxRoot();
                var go = new GameObject("CelestialFireworkArrow");
                go.transform.SetParent(vfxRoot, false);
                var k = new BuildingVisuals.Kit(go.transform);
                // Bamboo arrow shaft
                k.CylRot(Vector3.zero, 0.0013f, 0.036f, new Color(0.48f, 0.30f, 0.14f), new Vector3(90f, 0f, 0f));
                // Crimson firework powder tube + imperial gold bands
                k.CylRot(new Vector3(0f, 0f, 0.005f), 0.0028f, 0.014f, new Color(0.86f, 0.12f, 0.12f), new Vector3(90f, 0f, 0f));
                k.CylRot(new Vector3(0f, 0f, 0.010f), 0.0031f, 0.0024f, new Color(0.98f, 0.80f, 0.22f), new Vector3(90f, 0f, 0f));
                k.CylRot(new Vector3(0f, 0f, 0.000f), 0.0031f, 0.0024f, new Color(0.98f, 0.80f, 0.22f), new Vector3(90f, 0f, 0f));
                // Blazing fire tip + glowing fuse spark
                k.Cone(new Vector3(0f, 0f, 0.018f), 0.0032f, 0.009f, new Color(1f, 0.48f, 0.10f)).transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                k.Ball(new Vector3(0f, 0f, -0.003f), 0.0048f, new Color(1f, 0.88f, 0.30f));
                // Golden fletching
                k.Box(new Vector3(0f, 0f, -0.016f), new Vector3(0.0065f, 0.0009f, 0.007f), new Color(0.96f, 0.76f, 0.22f));

                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.loop = false;
                lr.positionCount = TrailPoints;
                lr.sharedMaterial = Mats.Unlit(new Color(1f, 0.56f, 0.14f));
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.startWidth = 0.0048f;
                lr.endWidth = 0.0008f;

                var proj = go.AddComponent<FireworkArrowProjectile>();
                proj.trail = lr;
                return proj;
            }

            void Update()
            {
                float dt = Time.deltaTime;
                bool targetValid = target != null && target.Serial == targetSerial && target.IsAlive;
                if (targetValid) lastTargetPos = target.AimPoint;

                t += dt / duration;
                float u = Mathf.Clamp01(t);
                Vector3 pos = Vector3.Lerp(from, lastTargetPos, u) + Vector3.up * (Mathf.Sin(u * Mathf.PI) * ArcHeight);
                Vector3 dir = pos - transform.position;
                if (dir.sqrMagnitude > 1e-10f) transform.rotation = Quaternion.LookRotation(dir);
                transform.position = pos;

                // Shift flame trail history
                for (int i = TrailPoints - 1; i > 0; i--) trailHistory[i] = trailHistory[i - 1];
                trailHistory[0] = pos;
                if (trail != null) trail.SetPositions(trailHistory);

                // Mid-flight micro spark trail
                sparkTimer += dt;
                if (sparkTimer >= 0.09f && VfxPool.Instance != null)
                {
                    sparkTimer = 0f;
                    VfxPool.Instance.Burst(pos, new Color(1f, 0.78f, 0.22f));
                }

                if (u < 1f) return;

                // Impact: direct hit + micro-splash AoE firework detonation
                Detonate(targetValid);
                if (trail != null) trail.enabled = false;
                liveFireworks.Remove(this);
                if (fireworkPool != null) fireworkPool.Release(this);
                else gameObject.SetActive(false);
            }

            void Detonate(bool primaryValid)
            {
                Vector3 impactPos = lastTargetPos;
                if (primaryValid && target != null && target.health != null && !target.health.IsDead)
                {
                    // Direct firework arrow damage + 15% searing fire bonus
                    target.health.Damage(damage * 1.15f);
                }

                // Micro-splash AoE damage to all nearby monsters within splashRadius
                float r2 = splashRadius * splashRadius;
                float splashDmg = damage * splashRatio;
                var list = MonsterUnit.Active;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (i >= list.Count) continue;
                    var m = list[i];
                    if (m == null || !m.IsAlive || m.health == null) continue;
                    if (primaryValid && m == target) continue;
                    float d2 = (m.AimPoint - impactPos).sqrMagnitude;
                    if (d2 <= r2)
                    {
                        m.health.Damage(splashDmg);
                    }
                }

                // Dual crimson-orange fire & golden firework spark burst + shock ring
                if (VfxPool.Instance != null)
                {
                    VfxPool.Instance.Burst(impactPos, new Color(1f, 0.36f, 0.08f));
                    VfxPool.Instance.Burst(impactPos + Vector3.up * 0.012f, new Color(1f, 0.88f, 0.26f));
                }
                SpawnPulseRing(impactPos, splashRadius, new Color(1f, 0.62f, 0.15f), 0.28f);
                ProceduralAudio.Fire(Sfx.Hit, impactPos);
            }
        }
    }

    /// <summary>
    /// Per-building passive component attached automatically by CivilizationPassives.ApplyToUnit().
    /// Adds subtle civilization-specific procedural visual motifs and runs per-unit passive ticks:
    /// - Verdant Clans: Living-wood sprouts + between-wave HP regeneration &amp; breathing pulse on Walls and Barricades.
    /// - Iron Dominion: Procedural Tesla Lightning Spire + close-range periodic lightning zap on Mines.
    /// - Celestial Dynasty: Firework rocket Brazier/Lantern on ArcherTowers.
    /// </summary>
    public sealed class CivilizationUnitPassive : MonoBehaviour
    {
        PlacedUnit unit;
        CivilizationDefinition civ;
        Transform accentRoot;
        Transform teslaOrb;
        string builtForCivId;
        UnitType builtForType;

        float zapCooldown;
        float regenPulseTimer;
        float phaseOffset;
        int lastTickFrame = -1;

        public void Bind(PlacedUnit placedUnit, CivilizationDefinition civDef)
        {
            unit = placedUnit;
            civ = civDef ?? CivilizationCatalog.Active;
            phaseOffset = (GetHashCode() & 0xFF) * 0.07f;
            zapCooldown = 0.4f;
            regenPulseTimer = 0.5f;
            RebuildAccentIfNeeded();
        }

        void Update()
        {
            Tick(Time.deltaTime, null);
        }

        public void Tick(float dt, CivilizationDefinition civOverride)
        {
            if (lastTickFrame == Time.frameCount) return;
            lastTickFrame = Time.frameCount;

            if (unit == null) unit = GetComponent<PlacedUnit>();
            if (unit == null || unit.health == null || unit.health.IsDead) return;

            var activeCiv = civOverride ?? CivilizationCatalog.Active;
            if (activeCiv != null && (civ == null || activeCiv.id != civ.id))
            {
                civ = activeCiv;
                RebuildAccentIfNeeded();
            }
            if (civ == null) return;

            // 1. Verdant Clans: Walls & Barricades passively regenerate HP between waves with living-wood visual pulse
            if (civ.hasLivingWoodRegen && (unit.type == UnitType.Wall || unit.type == UnitType.Barricade))
            {
                TickVerdantLivingWood(dt);
            }

            // 2. Iron Dominion: Mines periodically zap close-range monsters with a procedural lightning arc
            if (civ.hasMineLightningZap && unit.type == UnitType.Mine)
            {
                TickIronMineLightning(dt);
            }
        }

        void TickVerdantLivingWood(float dt)
        {
            if (!CivilizationPassives.IsBetweenWaves()) return;

            bool damaged = unit.health.Current < unit.health.Max - 0.01f;
            if (damaged)
            {
                float healAmount = Mathf.Max(0.5f, civ.wallBarricadeRegenPerSec) * dt;
                unit.health.Heal(healAmount);
            }

            // Living-wood visual breathing pulse on the structure or sprout accent
            if (accentRoot != null)
            {
                float pulseSpeed = damaged ? 5.5f : 2.6f;
                float pulseAmp = damaged ? 0.16f : 0.06f;
                float s = 1f + Mathf.Sin(Time.time * pulseSpeed + phaseOffset) * pulseAmp;
                accentRoot.localScale = new Vector3(s, s, s);
            }

            if (damaged)
            {
                regenPulseTimer -= dt;
                if (regenPulseTimer <= 0f)
                {
                    regenPulseTimer = 1.25f;
                    CivilizationPassives.SpawnVerdantRegenVfx(unit.transform.position, unit.Footprint);
                }
            }
        }

        void TickIronMineLightning(float dt)
        {
            // Animate the glowing Tesla capacitor orb on top of the Iron Dominion Mine
            if (teslaOrb != null)
            {
                float pulse = 1f + Mathf.Sin(Time.time * 9f + phaseOffset) * 0.15f;
                teslaOrb.localScale = Vector3.one * (0.011f * pulse);
            }

            zapCooldown -= dt;
            if (zapCooldown > 0f) return;

            float range = Mathf.Max(0.06f, civ.mineZapRange);
            float bestSq = range * range;
            MonsterUnit primary = null;
            Vector3 myPos = unit.transform.position;
            var list = MonsterUnit.Active;

            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                if (m == null || !m.IsAlive || m.health == null) continue;
                float d2 = (m.transform.position - myPos).sqrMagnitude;
                if (d2 <= bestSq)
                {
                    bestSq = d2;
                    primary = m;
                }
            }

            if (primary == null)
            {
                zapCooldown = 0.15f;
                return;
            }

            zapCooldown = Mathf.Max(0.35f, civ.mineZapInterval);
            float zapDmg = Mathf.Max(0.5f, civ.mineZapDamage);

            Vector3 spireTip = teslaOrb != null
                ? teslaOrb.position
                : unit.transform.position + unit.transform.up * 0.056f;
            Vector3 primaryAim = primary.AimPoint;

            // Zap primary monster
            primary.health.Damage(zapDmg);
            CivilizationPassives.SpawnLightningArc(spireTip, primaryAim, 0.22f);
            if (VfxPool.Instance != null) VfxPool.Instance.Burst(primaryAim, new Color(0.35f, 0.85f, 1f));
            ProceduralAudio.Fire(Sfx.Hit, primaryAim);

            // Chain a secondary 50%-damage mini shock arc if another monster is tightly clustered near primary
            const float ChainRangeSq = 0.065f * 0.065f;
            MonsterUnit secondary = null;
            float bestChainSq = ChainRangeSq;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                if (m == null || m == primary || !m.IsAlive || m.health == null) continue;
                float d2 = (m.AimPoint - primaryAim).sqrMagnitude;
                if (d2 <= bestChainSq)
                {
                    bestChainSq = d2;
                    secondary = m;
                }
            }

            if (secondary != null)
            {
                secondary.health.Damage(zapDmg * 0.5f);
                CivilizationPassives.SpawnLightningArc(primaryAim, secondary.AimPoint, 0.18f);
                if (VfxPool.Instance != null) VfxPool.Instance.Burst(secondary.AimPoint, new Color(0.45f, 0.90f, 1f));
            }
        }

        void RebuildAccentIfNeeded()
        {
            if (unit == null || unit.visual == null || civ == null) return;
            if (accentRoot != null && builtForCivId == civ.id && builtForType == unit.type) return;

            if (accentRoot != null) Destroy(accentRoot.gameObject);
            accentRoot = null;
            teslaOrb = null;
            builtForCivId = civ.id;
            builtForType = unit.type;

            // Build procedural visual indicator for active civilization bonuses on this structure
            if (civ.hasMineLightningZap && unit.type == UnitType.Mine)
            {
                accentRoot = BuildingVisuals.NewRoot(unit.visual, "IronTeslaSpire");
                var k = new BuildingVisuals.Kit(accentRoot);
                Color iron = new Color(0.32f, 0.35f, 0.40f);
                Color coil = new Color(0.22f, 0.52f, 0.88f);
                Color spark = new Color(0.45f, 0.90f, 1f);
                k.Cyl(new Vector3(0f, 0.036f, -0.012f), 0.0022f, 0.024f, iron);
                k.Cyl(new Vector3(0f, 0.043f, -0.012f), 0.0055f, 0.0025f, coil);
                k.Cyl(new Vector3(0f, 0.050f, -0.012f), 0.0042f, 0.0025f, coil);
                var orbGo = k.Ball(new Vector3(0f, 0.061f, -0.012f), 0.011f, spark);
                teslaOrb = orbGo.transform;
            }
            else if (civ.hasLivingWoodRegen && (unit.type == UnitType.Wall || unit.type == UnitType.Barricade))
            {
                accentRoot = BuildingVisuals.NewRoot(unit.visual, "VerdantLivingWood");
                var k = new BuildingVisuals.Kit(accentRoot);
                Color vine = new Color(0.20f, 0.65f, 0.25f);
                Color leaf = new Color(0.38f, 0.88f, 0.36f);
                float h = unit.type == UnitType.Wall ? 0.046f : 0.022f;
                k.Ball(new Vector3(-0.024f, h, 0.008f), 0.008f, vine);
                k.Ball(new Vector3(-0.021f, h + 0.004f, 0.008f), 0.006f, leaf);
                k.Ball(new Vector3(0.024f, h, -0.006f), 0.008f, vine);
                k.Ball(new Vector3(0.021f, h + 0.004f, -0.006f), 0.006f, leaf);
            }
            else if (civ.hasFireworkArrows && unit.type == UnitType.ArcherTower)
            {
                accentRoot = BuildingVisuals.NewRoot(unit.visual, "CelestialFireworkRack");
                var k = new BuildingVisuals.Kit(accentRoot);
                Color crimson = new Color(0.84f, 0.12f, 0.12f);
                Color gold = new Color(0.98f, 0.78f, 0.22f);
                Color ember = new Color(1f, 0.52f, 0.12f);
                k.Cyl(new Vector3(0.018f, 0.068f, 0.012f), 0.0038f, 0.012f, crimson);
                k.Cyl(new Vector3(0.018f, 0.078f, 0.012f), 0.0042f, 0.002f, gold);
                k.Ball(new Vector3(0.018f, 0.082f, 0.012f), 0.0055f, ember);
            }
        }
    }
}
