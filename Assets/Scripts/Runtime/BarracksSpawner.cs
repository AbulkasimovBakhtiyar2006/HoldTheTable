using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// 6th building type (Barracks) procedural mesh builder + garrison spawner component for both
    /// placed Barracks buildings (3 defenders: 2 Spearmen, 1 Swordsman) and the TownHall garrison (2 defenders).
    /// Periodically spawns and replenishes tiny friendly defenders wearing the player's civilization and banner colors.
    /// Owner: @living-civ (Feature 4).
    /// </summary>
    public class BarracksSpawner : MonoBehaviour
    {
        // Recommended UnitDef stats for UnitType.Barracks
        public const int DefaultCost = 16;
        public const int DefaultGoldCost = 5;
        public const float DefaultHp = 26f;
        public const float DefaultFootprint = 0.055f;
        public const float DefaultRange = 0.28f;
        public const float DefaultDamage = 2.5f;
        public const float BarracksRespawnSeconds = 5.0f;
        public const float TownHallRespawnSeconds = 6.5f;
        public const int BarracksSquadSize = 3;
        public const int TownHallSquadSize = 2;

        public static readonly List<BarracksSpawner> All = new List<BarracksSpawner>();

        public bool isTownHallGarrison;
        public PlacedUnit placedUnit;
        public TownHall townHall;
        public CivilizationDefinition civOverride;
        public int maxDefenders = BarracksSquadSize;
        public float respawnInterval = BarracksRespawnSeconds;

        readonly List<DefenderUnit> squad = new List<DefenderUnit>();
        float spawnTimer;
        int nextSpawnIndex;

        public IReadOnlyList<DefenderUnit> Squad => squad;
        public int AliveDefenderCount
        {
            get
            {
                CleanupDeadReferences();
                return squad.Count;
            }
        }

        public bool IsSpawnerAlive
        {
            get
            {
                if (isTownHallGarrison)
                    return townHall != null && townHall.health != null && !townHall.health.IsDead;
                return placedUnit != null && placedUnit.health != null && !placedUnit.health.IsDead;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            All.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoBootstrap()
        {
            LivingSettlementCoordinator.EnsureInstance();
        }

        /// <summary>Returns true if `t` is the 6th building type `Barracks` (safe before and after enum registration).</summary>
        public static bool IsBarracksType(UnitType t)
        {
            return (int)t == 5 || string.Equals(t.ToString(), "Barracks", StringComparison.Ordinal);
        }

        /// <summary>Creates a ready-to-use UnitDef entry for `UnitType.Barracks`.</summary>
        public static UnitDef CreateUnitDef(UnitType barracksType)
        {
            return new UnitDef
            {
                type = barracksType,
                cost = DefaultCost,
                goldCost = DefaultGoldCost,
                hp = DefaultHp,
                footprint = DefaultFootprint,
                range = DefaultRange,
                damage = DefaultDamage,
                slow = 1f,
                fireInterval = BarracksRespawnSeconds,
                isEconomy = false
            };
        }

        /// <summary>Attaches and initializes a BarracksSpawner on a placed Barracks unit.</summary>
        public static BarracksSpawner AttachToBarracks(PlacedUnit unit, CivilizationDefinition civ = null)
        {
            if (unit == null) return null;
            LivingSettlementCoordinator.EnsureInstance();
            var spawner = unit.GetComponent<BarracksSpawner>();
            if (spawner == null) spawner = unit.gameObject.AddComponent<BarracksSpawner>();
            spawner.InitForBarracks(unit, civ);
            return spawner;
        }

        /// <summary>Attaches and initializes a TownHall garrison spawner that spawns 2 friendly defenders.</summary>
        public static BarracksSpawner AttachToTownHall(TownHall hall)
        {
            if (hall == null) return null;
            LivingSettlementCoordinator.EnsureInstance();
            var spawner = hall.GetComponent<BarracksSpawner>();
            if (spawner == null) spawner = hall.gameObject.AddComponent<BarracksSpawner>();
            spawner.InitForTownHall(hall);
            return spawner;
        }

        /// <summary>Clears all active defenders and resets all spawner timers (called on Restart / NewEmpire).</summary>
        public static void ResetAll()
        {
            DefenderUnit.DespawnAll();
            for (int i = 0; i < All.Count; i++)
            {
                var s = All[i];
                if (s == null) continue;
                s.squad.Clear();
                s.spawnTimer = 0.6f;
                s.nextSpawnIndex = 0;
            }
        }

        public void InitForBarracks(PlacedUnit unit, CivilizationDefinition civ = null)
        {
            isTownHallGarrison = false;
            placedUnit = unit;
            townHall = null;
            civOverride = civ;
            maxDefenders = BarracksSquadSize;
            respawnInterval = BarracksRespawnSeconds;
            spawnTimer = 0.4f;
            nextSpawnIndex = 0;
        }

        public void InitForTownHall(TownHall hall)
        {
            isTownHallGarrison = true;
            townHall = hall;
            placedUnit = null;
            civOverride = hall != null ? hall.civ : null;
            maxDefenders = TownHallSquadSize;
            respawnInterval = TownHallRespawnSeconds;
            spawnTimer = 0.8f;
            nextSpawnIndex = 0;
        }

        void OnEnable()
        {
            if (!All.Contains(this)) All.Add(this);
        }

        void OnDisable()
        {
            All.Remove(this);
        }

        void OnDestroy()
        {
            All.Remove(this);
            for (int i = squad.Count - 1; i >= 0; i--)
            {
                var d = squad[i];
                if (d != null && d.IsAlive && d.health != null)
                    d.health.Damage(9999f);
            }
            squad.Clear();
        }

        public void NotifyDefenderDied(DefenderUnit d)
        {
            squad.Remove(d);
            if (spawnTimer <= 0f)
                spawnTimer = respawnInterval;
        }

        void CleanupDeadReferences()
        {
            for (int i = squad.Count - 1; i >= 0; i--)
            {
                if (squad[i] == null || !squad[i].IsAlive)
                    squad.RemoveAt(i);
            }
        }

        void Update()
        {
            if (!IsSpawnerAlive) return;
            var gd = GameDirector.Instance;
            if (gd != null && !gd.IsCombatPhase) return;

            CleanupDeadReferences();
            if (squad.Count >= maxDefenders) return;

            spawnTimer -= Time.deltaTime;
            if (spawnTimer > 0f) return;

            SpawnOneDefender();
            // Initial garrison deploys quickly, subsequent replacements use full respawnInterval
            spawnTimer = squad.Count < maxDefenders ? 1.1f : respawnInterval;
        }

        void SpawnOneDefender()
        {
            int slot = nextSpawnIndex++;
            // Mix Spearmen and Swordsmen (e.g. Spearman, Swordsman, Spearman)
            DefenderKind kind = (slot % 2 == 1) ? DefenderKind.Swordsman : DefenderKind.Spearman;

            Vector3 center = transform.position;
            Vector3 up = transform.up;
            float outRadius = isTownHallGarrison ? BuildingVisuals.HallFootprintRadius + 0.025f : 0.048f;
            float angle = (slot * 115f + 25f) * Mathf.Deg2Rad;
            Vector3 right = transform.right;
            Vector3 fwd = transform.forward;
            Vector3 spawnPos = center + (fwd * Mathf.Cos(angle) + right * Mathf.Sin(angle)) * outRadius;

            if (GameDirector.Instance != null && GameDirector.Instance.Surface != null)
            {
                var s = GameDirector.Instance.Surface;
                Vector3 proj = s.ProjectOnto(spawnPos);
                if (s.IsPointSupported(proj)) spawnPos = proj;
                else spawnPos = s.ProjectOnto(center);
            }

            CivilizationDefinition civ = civOverride;
            if (civ == null && isTownHallGarrison && townHall != null) civ = townHall.civ;

            var defender = DefenderUnit.Spawn(this, spawnPos, kind, slot, civ);
            if (defender != null)
            {
                squad.Add(defender);
                if (VfxPool.Instance != null)
                    VfxPool.Instance.Ring(spawnPos + up * 0.003f, 0.028f);
            }
        }

        // ====================================================================
        // Procedural Mesh Builder for the 6th Building Type: Barracks
        // ====================================================================

        /// <summary>
        /// Builds the procedural visual for a Barracks building in the player's civilization style and banner colors.
        /// Call from PlacedUnit.Init / BuildingVisuals.BuildUnit when UnitType == UnitType.Barracks.
        /// </summary>
        public static Transform BuildBarracksVisual(Transform parent, CivilizationDefinition civOverride)
        {
            return BuildBarracksVisual(parent, civOverride, out _);
        }

        /// <summary>
        /// Builds the procedural visual for a Barracks building with a rally/muzzle transform output.
        /// </summary>
        public static Transform BuildBarracksVisual(Transform parent, CivilizationDefinition civOverride, out Transform muzzle)
        {
            BuildingVisuals.Resolve(civOverride, out var style, out var p);
            var root = BuildingVisuals.NewRoot(parent, "BarracksVisual");
            var k = new BuildingVisuals.Kit(root);

            switch (style)
            {
                case ArchitectureStyle.TieredRoofs:
                    BuildBarracksTiered(k, p);
                    break;
                case ArchitectureStyle.Timber:
                    BuildBarracksTimber(k, p);
                    break;
                default:
                    BuildBarracksStone(k, p);
                    break;
            }

            var m = new GameObject("Muzzle").transform;
            m.SetParent(root, false);
            m.localPosition = new Vector3(0f, 0.045f, 0.04f);
            muzzle = m;
            return root;
        }

        static void BuildTrainingDummyAndRack(BuildingVisuals.Kit k, BuildingVisuals.Palette p, Vector3 dummyPos, Vector3 rackPos)
        {
            Color wood = new Color(0.46f, 0.30f, 0.15f);
            Color straw = new Color(0.86f, 0.74f, 0.38f);
            Color iron = new Color(0.72f, 0.74f, 0.78f);

            // Training dummy: upright post, cross-arms, straw torso with banner target band, straw head, mini shield
            k.Cyl(dummyPos, 0.0022f, 0.034f, wood);
            k.Cyl(dummyPos + new Vector3(0f, 0.008f, 0f), 0.0055f, 0.013f, straw);
            k.Cyl(dummyPos + new Vector3(0f, 0.012f, 0f), 0.0058f, 0.004f, p.banner);
            k.CylRot(dummyPos + new Vector3(0f, 0.017f, 0f), 0.0016f, 0.018f, wood, new Vector3(0f, 0f, 90f));
            k.Ball(dummyPos + new Vector3(0f, 0.0305f, 0f), UnitScale.HeadDiameter, straw);
            k.CylRot(dummyPos + new Vector3(-0.008f, 0.016f, 0.002f), 0.0045f, 0.0018f, p.trim, new Vector3(0f, 15f, 90f));

            // Weapon rack: two A-frame posts + crossbar + 3 upright spears
            k.Box(rackPos + new Vector3(-0.010f, 0.010f, 0f), new Vector3(0.0025f, 0.020f, 0.004f), wood);
            k.Box(rackPos + new Vector3(0.010f, 0.010f, 0f), new Vector3(0.0025f, 0.020f, 0.004f), wood);
            k.Box(rackPos + new Vector3(0f, 0.015f, 0f), new Vector3(0.024f, 0.0022f, 0.0025f), p.dark);
            for (int i = -1; i <= 1; i++)
            {
                Vector3 sp = rackPos + new Vector3(i * 0.006f, 0.002f, 0.0015f);
                k.Cyl(sp, 0.0011f, 0.028f, wood);
                k.Cone(sp + Vector3.up * 0.028f, 0.0022f, 0.006f, iron);
            }
        }

        static void BuildBarracksTiered(BuildingVisuals.Kit k, BuildingVisuals.Palette p)
        {
            // Celestial Dynasty Martial Dojo & Courtyard
            k.Box(new Vector3(0f, 0.003f, 0f), new Vector3(0.098f, 0.006f, 0.098f), p.stone);
            // Courtyard low wall enclosing front training yard
            k.Box(new Vector3(-0.045f, 0.011f, 0.015f), new Vector3(0.005f, 0.012f, 0.062f), p.main);
            k.Box(new Vector3(0.045f, 0.011f, 0.015f), new Vector3(0.005f, 0.012f, 0.062f), p.main);
            // Twin entrance gate pillars at +Z
            k.Cyl(new Vector3(-0.016f, 0.006f, 0.045f), 0.0035f, 0.032f, p.dark);
            k.Cyl(new Vector3(0.016f, 0.006f, 0.045f), 0.0035f, 0.032f, p.dark);
            k.PagodaRoof(new Vector3(0f, 0.036f, 0.045f), 0.024f, 0.011f, p.roof);

            // Main martial hall at back (-Z)
            k.Box(new Vector3(0f, 0.024f, -0.020f), new Vector3(0.072f, 0.036f, 0.042f), p.main);
            for (int sx = -1; sx <= 1; sx += 2)
                k.Cyl(new Vector3(sx * 0.033f, 0.006f, 0.001f), 0.0035f, 0.036f, p.dark);
            // Double dojo doors + golden emblem plaque
            k.Box(new Vector3(0f, 0.018f, 0.0015f), new Vector3(0.018f, 0.024f, 0.002f), p.dark);
            k.Box(new Vector3(0f, 0.034f, 0.002f), new Vector3(0.016f, 0.006f, 0.002f), p.trim);
            // Upturned Pagoda Roof with ridge ornaments
            k.PagodaRoof(new Vector3(0f, 0.042f, -0.020f), 0.048f, 0.024f, p.roof);
            k.Ball(new Vector3(0f, 0.068f, -0.020f), 0.007f, p.trim);

            // Twin vertical war banners
            k.Cyl(new Vector3(-0.036f, 0.006f, 0.038f), 0.0014f, 0.044f, p.dark);
            k.Box(new Vector3(-0.029f, 0.036f, 0.038f), new Vector3(0.012f, 0.018f, 0.0018f), p.banner);
            k.Cyl(new Vector3(0.036f, 0.006f, 0.038f), 0.0014f, 0.044f, p.dark);
            k.Box(new Vector3(0.029f, 0.036f, 0.038f), new Vector3(0.012f, 0.018f, 0.0018f), p.banner);

            BuildTrainingDummyAndRack(k, p, new Vector3(0.022f, 0.006f, 0.020f), new Vector3(-0.022f, 0.006f, 0.018f));
        }

        static void BuildBarracksTimber(BuildingVisuals.Kit k, BuildingVisuals.Palette p)
        {
            // Verdant Clans Timber War-Lodge & Training Yard
            k.Box(new Vector3(0f, 0.0025f, 0f), new Vector3(0.098f, 0.005f, 0.098f), p.ground);

            // Log palisade posts along sides
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int i = 0; i < 4; i++)
                {
                    float z = -0.01f + i * 0.016f;
                    k.Cyl(new Vector3(sx * 0.044f, 0.004f, z), 0.0042f, 0.024f, i % 2 == 0 ? p.main : p.dark);
                    k.Cone(new Vector3(sx * 0.044f, 0.028f, z), 0.0042f, 0.007f, p.main);
                }
            }

            // Timber war-lodge at back
            k.Box(new Vector3(0f, 0.022f, -0.020f), new Vector3(0.070f, 0.034f, 0.044f), p.main);
            k.Box(new Vector3(0f, 0.016f, 0.0025f), new Vector3(0.016f, 0.024f, 0.003f), p.dark);
            k.GableX(new Vector3(0f, 0.039f, -0.020f), 0.078f, 0.054f, 0.028f, p.roof);

            // Mounted round war-shields on the gable front in player's banner & trim colors
            k.CylRot(new Vector3(-0.016f, 0.026f, 0.003f), 0.0065f, 0.002f, p.banner, new Vector3(90f, 0f, 0f));
            k.CylRot(new Vector3(0.016f, 0.026f, 0.003f), 0.0065f, 0.002f, p.banner, new Vector3(90f, 0f, 0f));
            k.Ball(new Vector3(0f, 0.048f, 0.003f), 0.007f, p.trim);

            // Tall clan war-banner at front-left corner
            k.Cyl(new Vector3(-0.036f, 0.005f, 0.038f), 0.0018f, 0.052f, p.dark);
            k.Box(new Vector3(-0.025f, 0.046f, 0.038f), new Vector3(0.020f, 0.014f, 0.0018f), p.banner);

            BuildTrainingDummyAndRack(k, p, new Vector3(0.022f, 0.005f, 0.022f), new Vector3(-0.020f, 0.005f, 0.020f));
        }

        static void BuildBarracksStone(BuildingVisuals.Kit k, BuildingVisuals.Palette p)
        {
            // Iron Dominion Crenellated Garrison & Armory
            k.Box(new Vector3(0f, 0.0035f, 0f), new Vector3(0.098f, 0.007f, 0.098f), p.stone);

            // Main stone blockhouse at back (-Z)
            k.Box(new Vector3(0f, 0.027f, -0.018f), new Vector3(0.068f, 0.042f, 0.044f), p.main);
            k.Box(new Vector3(0f, 0.043f, -0.018f), new Vector3(0.071f, 0.004f, 0.047f), p.trim);
            // Crenellations along roof parapet
            for (int i = -2; i <= 2; i++)
            {
                k.Box(new Vector3(i * 0.014f, 0.051f, 0.002f), new Vector3(0.008f, 0.008f, 0.006f), p.main);
                k.Box(new Vector3(i * 0.014f, 0.051f, -0.038f), new Vector3(0.008f, 0.008f, 0.006f), p.main);
            }
            // Corner watch-turret with slate cone roof
            Vector3 turretBase = new Vector3(-0.030f, 0.007f, -0.028f);
            k.Cyl(turretBase, 0.014f, 0.056f, p.main);
            k.Cone(turretBase + Vector3.up * 0.056f, 0.017f, 0.024f, p.roof);

            // Arched garrison gate + banner crest above gate
            k.Box(new Vector3(0.004f, 0.018f, 0.0045f), new Vector3(0.018f, 0.024f, 0.003f), p.dark);
            k.Box(new Vector3(0.004f, 0.035f, 0.005f), new Vector3(0.014f, 0.010f, 0.002f), p.banner);

            // Flying garrison standard
            k.Cyl(new Vector3(0.024f, 0.048f, -0.018f), 0.0015f, 0.032f, p.dark);
            k.Box(new Vector3(0.034f, 0.072f, -0.018f), new Vector3(0.018f, 0.011f, 0.0018f), p.banner);

            BuildTrainingDummyAndRack(k, p, new Vector3(0.022f, 0.007f, 0.024f), new Vector3(-0.020f, 0.007f, 0.022f));
        }
    }

    /// <summary>
    /// Lightweight runtime coordinator that automatically wires TownHall garrisons, placed Barracks units,
    /// and ambient PeasantAgents during gameplay, and cleans up on session reset.
    /// </summary>
    internal sealed class LivingSettlementCoordinator : MonoBehaviour
    {
        static LivingSettlementCoordinator instance;
        static bool quitting;
        float scanTimer;
        bool subscribed;

        public static LivingSettlementCoordinator EnsureInstance()
        {
            if (instance == null && !quitting && Application.isPlaying)
            {
                var go = new GameObject("LivingSettlementCoordinator");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<LivingSettlementCoordinator>();
            }
            return instance;
        }

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
        }

        void OnApplicationQuit() => quitting = true;

        void OnDestroy()
        {
            Unsubscribe();
            if (instance == this) instance = null;
        }

        void TrySubscribe()
        {
            if (subscribed || GameDirector.Instance == null) return;
            GameDirector.Instance.GameReset += OnGameReset;
            GameDirector.Instance.PhaseChanged += OnPhaseChanged;
            GameDirector.Instance.WaveStarted += OnWaveStarted;
            subscribed = true;
        }

        void Unsubscribe()
        {
            if (!subscribed || GameDirector.Instance == null) return;
            GameDirector.Instance.GameReset -= OnGameReset;
            GameDirector.Instance.PhaseChanged -= OnPhaseChanged;
            GameDirector.Instance.WaveStarted -= OnWaveStarted;
            subscribed = false;
        }

        void OnGameReset()
        {
            BarracksSpawner.ResetAll();
            PeasantAgent.DespawnAll();
        }

        void OnPhaseChanged(GamePhase phase)
        {
            if (phase == GamePhase.CivilizationSetup || phase == GamePhase.HeadquartersPlacement)
            {
                BarracksSpawner.ResetAll();
                PeasantAgent.DespawnAll();
            }
            else if (phase == GamePhase.Defeat)
            {
                DefenderUnit.DespawnAll();
                PeasantAgent.DespawnAll();
            }
            else
            {
                PeasantAgent.NotifyPhaseChanged(phase);
            }
        }

        void OnWaveStarted(int waveIndex)
        {
            PeasantAgent.TriggerWavePanic();
        }

        void Update()
        {
            TrySubscribe();
            PeasantAgent.TickManager(Time.deltaTime);

            scanTimer -= Time.deltaTime;
            if (scanTimer > 0f) return;
            scanTimer = 0.35f;

            // Ensure TownHall has its garrison spawner attached
            var hall = TownHall.Current;
            if (hall != null && hall.health != null && !hall.health.IsDead)
            {
                if (hall.GetComponent<BarracksSpawner>() == null)
                    BarracksSpawner.AttachToTownHall(hall);
            }

            // Ensure any placed Barracks has its spawner and Barracks visual
            var units = PlacedUnit.All;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || !BarracksSpawner.IsBarracksType(u.type)) continue;
                if (u.GetComponent<BarracksSpawner>() == null)
                    BarracksSpawner.AttachToBarracks(u);

                // If BuildingVisuals.BuildUnit fell through to the default visual, swap in the Barracks visual
                if (u.visual != null && u.visual.name != "BarracksVisual")
                {
                    Vector3 scale = u.visual.localScale;
                    Destroy(u.visual.gameObject);
                    u.visual = BarracksSpawner.BuildBarracksVisual(u.transform, null, out u.muzzle);
                    u.visual.localScale = scale;
                }
            }
        }
    }
}
