using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// In-place specialization branches for Archer Towers.
    /// </summary>
    public enum TowerUpgradeType
    {
        None = 0,
        Ballista = 1,
        FireCatapult = 2,
        // Aliases for integration flexibility
        BallistaTower = 1,
        Catapult = 2,
        FireTower = 2,
        CatapultTower = 2
    }

    /// <summary>
    /// Attached to an upgraded or repaired PlacedUnit to track its in-place upgrade tier,
    /// procedural turret/catapult mesh parts, invested resources (for 50% salvage refund),
    /// and automatic shot enhancement.
    /// </summary>
    public class BuildingUpgradeAttachment : MonoBehaviour
    {
        public PlacedUnit unit;
        public TowerUpgradeType upgradeType = TowerUpgradeType.None;
        public int extraTimberSpent;
        public int extraGoldSpent;

        public Transform attachmentRoot;
        public Transform turretPivot;
        public Transform throwingArm;
        public Transform loadedProjectileVisual;

        public float baseAddedHp;
        public float armAnimT = 1f;
        public int lastObservedShots;
        public float lastExplicitFireTime = -10f;

        void Awake()
        {
            if (unit == null) unit = GetComponent<PlacedUnit>();
            if (unit != null) lastObservedShots = unit.shots;
        }

        void Update()
        {
            if (unit == null || unit.health == null || unit.health.IsDead) return;
            float dt = Time.deltaTime;

            // Animate catapult throwing arm or ballista reload recoil
            if (armAnimT < 1f)
            {
                armAnimT = Mathf.Min(1f, armAnimT + dt / 0.38f);
                if (upgradeType == TowerUpgradeType.FireCatapult && throwingArm != null)
                {
                    // Swing forward from -55 deg to +25 deg on fire, then winch back to -55 deg
                    float swing = armAnimT < 0.25f
                        ? Mathf.Lerp(-55f, 28f, armAnimT / 0.25f)
                        : Mathf.Lerp(28f, -55f, (armAnimT - 0.25f) / 0.75f);
                    throwingArm.localRotation = Quaternion.Euler(swing, 0f, 0f);
                }
                if (loadedProjectileVisual != null)
                {
                    bool showPayload = armAnimT > 0.65f;
                    if (loadedProjectileVisual.gameObject.activeSelf != showPayload)
                        loadedProjectileVisual.gameObject.SetActive(showPayload);
                }
            }

            // Fallback auto-trigger if PlacedUnit fired a normal arrow without calling TryFireUpgradedTower directly
            if (upgradeType != TowerUpgradeType.None && unit.shots != lastObservedShots)
            {
                lastObservedShots = unit.shots;
                if (Time.time - lastExplicitFireTime > 0.06f && BuildingUpgradeController.Instance != null)
                {
                    BuildingUpgradeController.Instance.OnTowerAutoFiredFallback(this);
                }
            }
        }

        public void AimAt(Vector3 targetWorldPos)
        {
            if (turretPivot == null || unit == null) return;
            Vector3 up = unit.transform.up;
            Vector3 dir = Vector3.ProjectOnPlane(targetWorldPos - turretPivot.position, up);
            if (dir.sqrMagnitude > 1e-6f)
            {
                turretPivot.rotation = Quaternion.LookRotation(dir.normalized, up);
            }
        }
    }

    /// <summary>
    /// Feature 3 Controller:
    /// 1. In-place ArcherTower upgrades on finger poke:
    ///    - Ballista Tower (8 Gold): high single-target armor-piercing damage vs Rams/Bosses/Siege, procedural swivel ballista attachment.
    ///    - Fire/Catapult Tower (10 Gold): AoE splash flaming boulder projectiles, procedural catapult & fire brazier attachment.
    /// 2. In-place Structure Repair on finger poke:
    ///    - Poking any damaged Wall, Barricade, or structure spends Wood (Timber) to restore HP with a 3D floating feedback effect.
    /// 3. Demolish / Salvage on hold-pinch & drop toward Tray/Scroll:
    ///    - Holding pinch on an existing placed structure and dropping it back toward the tray or scroll salvages it and refunds 50% of its build resources.
    /// </summary>
    public class BuildingUpgradeController : MonoBehaviour
    {
        public const int BallistaGoldCost = 8;
        public const int FireCatapultGoldCost = 10;

        public const float BallistaRange = 0.34f;
        public const float BallistaBaseDamage = 4.5f;
        public const float BallistaArmorPiercingMul = 2.2f;
        public const float BallistaFireInterval = 1.10f;
        public const float BallistaBonusHp = 12f;

        public const float FireCatapultRange = 0.32f;
        public const float FireCatapultDirectDamage = 3.5f;
        public const float FireCatapultSplashDamage = 2.6f;
        public const float FireCatapultSplashRadius = 0.09f;
        public const float FireCatapultFireInterval = 1.45f;
        public const float FireCatapultBonusHp = 18f;

        public const float UnitPokeRadius = 0.066f;
        public const float PokeCooldown = 0.55f;
        public const float SalvageGrabRadius = 0.075f;
        public const float SalvageHoldSeconds = 0.28f;
        public const float SalvagePullThreshold = 0.055f;

        static BuildingUpgradeController instance;
        static bool quitting;

        public static BuildingUpgradeController Instance
        {
            get
            {
                if (instance == null && !quitting && Application.isPlaying)
                {
                    instance = new GameObject("BuildingUpgradeController").AddComponent<BuildingUpgradeController>();
                }
                return instance;
            }
        }

        public static BuildingUpgradeController EnsureInstance() => Instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            instance = null;
            quitting = false;
            ResourceRefundRequested = null;
            TowerUpgraded = null;
            StructureRepaired = null;
            StructureSalvaged = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoBootstrap()
        {
            if (Application.isPlaying && instance == null)
                EnsureInstance();
        }

        /// <summary>
        /// Raised when a structure is salvaged so GameDirector can credit refunded (timber, gold).
        /// Arguments: (int timberRefund, int goldRefund).
        /// </summary>
        public static event Action<int, int> ResourceRefundRequested;

        /// <summary>Raised when an ArcherTower is upgraded in-place to Ballista or FireCatapult.</summary>
        public static event Action<PlacedUnit, TowerUpgradeType> TowerUpgraded;

        /// <summary>Raised when a damaged structure is repaired with wood. Arguments: (unit, hpRestored, woodSpent).</summary>
        public static event Action<PlacedUnit, float, int> StructureRepaired;

        /// <summary>Raised when a placed structure is salvaged. Arguments: (unit, timberRefund, goldRefund).</summary>
        public static event Action<PlacedUnit, int, int> StructureSalvaged;

        // Per-hand poke tracking
        readonly bool[] wasInUnitPokeZone = new bool[2];
        readonly float[] lastPokeTime = { -10f, -10f };

        // Per-hand pinch-hold salvage tracking
        readonly bool[] wasPinching = new bool[2];
        readonly PlacedUnit[] pinchUnit = new PlacedUnit[2];
        readonly float[] pinchStartTime = new float[2];
        readonly Vector3[] pinchStartPos = new Vector3[2];
        readonly bool[] isSalvageDragging = new bool[2];
        readonly TextMeshPro[] dragLabels = new TextMeshPro[2];
        readonly LineRenderer[] dragTethers = new LineRenderer[2];

        // Pooled 3D floating feedback texts
        class FloatingPopup
        {
            public Transform root;
            public TextMeshPro label;
            public Transform icon;
            public Vector3 startPos;
            public Color baseColor;
            public float elapsed;
            public float duration;
            public bool active;
        }

        // In-flight Fire/Catapult arcing splash projectiles
        class FireballShot
        {
            public Transform visual;
            public Vector3 from;
            public Vector3 targetPos;
            public MonsterUnit target;
            public int targetSerial;
            public float directDamage;
            public float splashDamage;
            public float splashRadius;
            public float t;
            public float duration;
            public float emberTimer;
            public bool active;
        }

        readonly List<FloatingPopup> popups = new List<FloatingPopup>(12);
        readonly List<FireballShot> fireballs = new List<FireballShot>(12);
        bool subscribed;

        void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
        }

        void Start()
        {
            TrySubscribe();
        }

        void OnApplicationQuit() => quitting = true;

        void OnDestroy()
        {
            if (subscribed && GameDirector.Instance != null)
                GameDirector.Instance.GameReset -= OnGameReset;
            if (instance == this) instance = null;
        }

        void TrySubscribe()
        {
            if (subscribed || GameDirector.Instance == null) return;
            GameDirector.Instance.GameReset += OnGameReset;
            subscribed = true;
        }

        void OnGameReset()
        {
            CancelAllSalvageDrags();
            ClearFireballs();
            HideAllPopups();
        }

        // ====================================================================
        // Static Query & Combat Integration API (for PlacedUnit & GameDirector)
        // ====================================================================

        public static TowerUpgradeType GetTowerUpgrade(PlacedUnit unit)
        {
            if (unit == null) return TowerUpgradeType.None;
            var att = unit.GetComponent<BuildingUpgradeAttachment>();
            return att != null ? att.upgradeType : TowerUpgradeType.None;
        }

        public static bool IsUpgradedTower(PlacedUnit unit) => GetTowerUpgrade(unit) != TowerUpgradeType.None;

        public static float GetEffectiveRange(PlacedUnit unit)
        {
            if (unit == null) return 0f;
            var upg = GetTowerUpgrade(unit);
            if (upg == TowerUpgradeType.Ballista) return BallistaRange;
            if (upg == TowerUpgradeType.FireCatapult) return FireCatapultRange;
            return unit.def != null ? unit.def.range : 0.25f;
        }

        public static float GetEffectiveFireInterval(PlacedUnit unit)
        {
            if (unit == null) return 1f;
            var upg = GetTowerUpgrade(unit);
            if (upg == TowerUpgradeType.Ballista) return BallistaFireInterval;
            if (upg == TowerUpgradeType.FireCatapult) return FireCatapultFireInterval;
            return unit.def != null ? unit.def.fireInterval : 1f;
        }

        /// <summary>
        /// Returns true if the target monster is a Ram, Boss, or heavy siege enemy that takes bonus armor-piercing damage from Ballista Towers.
        /// </summary>
        public static bool IsArmoredOrBossTarget(MonsterUnit target)
        {
            if (target == null) return false;
            if (target.IsRam) return true;
            if (target.health != null && target.health.Max >= 20f) return true;
            string n = target.gameObject.name;
            return n.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("King", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Catapult", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Siege", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static float GetEffectiveDamage(PlacedUnit unit, MonsterUnit target = null)
        {
            if (unit == null) return 2f;
            float mul = UnitDef.DamageMul(unit.type);
            var upg = GetTowerUpgrade(unit);
            if (upg == TowerUpgradeType.Ballista)
            {
                float dmg = BallistaBaseDamage * mul;
                if (IsArmoredOrBossTarget(target)) dmg *= BallistaArmorPiercingMul;
                return dmg;
            }
            if (upg == TowerUpgradeType.FireCatapult)
            {
                return FireCatapultDirectDamage * mul;
            }
            return unit.def != null ? unit.def.EffectiveDamage : 2f * mul;
        }

        /// <summary>
        /// Picks the optimal target in range for an upgraded tower:
        /// - Ballista Tower prioritizes Rams, Bosses, and high-HP armored targets.
        /// - Fire/Catapult Tower prioritizes targets with nearby neighbors for AoE splash value.
        /// Falls back to defaultTarget if un-upgraded or no better target is in range.
        /// </summary>
        public static MonsterUnit SelectPriorityTarget(PlacedUnit tower, float range, MonsterUnit defaultTarget = null)
        {
            if (tower == null) return defaultTarget;
            var upg = GetTowerUpgrade(tower);
            if (upg == TowerUpgradeType.None) return defaultTarget;

            var list = MonsterUnit.Active;
            if (list == null || list.Count == 0) return defaultTarget;

            Vector3 me = tower.transform.position;
            float r2 = range * range;
            MonsterUnit bestTarget = defaultTarget;

            if (upg == TowerUpgradeType.Ballista)
            {
                float bestScore = -1f;
                for (int i = 0; i < list.Count; i++)
                {
                    var m = list[i];
                    if (m == null || !m.IsAlive) continue;
                    float d2 = (m.transform.position - me).sqrMagnitude;
                    if (d2 > r2) continue;
                    // Heavy priority boost for Rams / Bosses, tie-broken by closeness
                    float score = (IsArmoredOrBossTarget(m) ? 1000f : 10f) + (m.health != null ? m.health.Max : 0f) - d2 * 20f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTarget = m;
                    }
                }
            }
            else if (upg == TowerUpgradeType.FireCatapult)
            {
                float bestScore = -1f;
                float splash2 = FireCatapultSplashRadius * FireCatapultSplashRadius;
                for (int i = 0; i < list.Count; i++)
                {
                    var m = list[i];
                    if (m == null || !m.IsAlive) continue;
                    float d2 = (m.transform.position - me).sqrMagnitude;
                    if (d2 > r2) continue;
                    int neighbors = 0;
                    Vector3 mp = m.transform.position;
                    for (int j = 0; j < list.Count; j++)
                    {
                        var o = list[j];
                        if (o != null && o.IsAlive && (o.transform.position - mp).sqrMagnitude <= splash2)
                            neighbors++;
                    }
                    float score = neighbors * 15f - d2 * 10f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestTarget = m;
                    }
                }
            }
            return bestTarget;
        }

        /// <summary>
        /// Fires an upgraded tower shot (Ballista armor-piercing bolt or Fire/Catapult AoE splash boulder).
        /// Call from PlacedUnit.Update before Projectile.Fire: if this returns true, the shot was handled!
        /// </summary>
        public static bool TryFireUpgradedTower(PlacedUnit tower, Vector3 origin, MonsterUnit target, float baseDamage = -1f)
        {
            if (tower == null || target == null) return false;
            var att = tower.GetComponent<BuildingUpgradeAttachment>();
            if (att == null || att.upgradeType == TowerUpgradeType.None) return false;

            EnsureInstance();
            att.lastExplicitFireTime = Time.time;
            att.lastObservedShots = tower.shots + 1;
            att.armAnimT = 0f;

            if (att.upgradeType == TowerUpgradeType.Ballista)
            {
                MonsterUnit chosen = IsArmoredOrBossTarget(target) ? target : SelectPriorityTarget(tower, GetEffectiveRange(tower), target);
                if (chosen == null || !chosen.IsAlive) chosen = target;
                att.AimAt(chosen.AimPoint);
                float dmg = GetEffectiveDamage(tower, chosen);
                Projectile.Fire(origin, chosen, dmg);
                if (VfxPool.Instance != null)
                {
                    VfxPool.Instance.Burst(origin, new Color(1f, 0.9f, 0.45f));
                    if (IsArmoredOrBossTarget(chosen))
                        VfxPool.Instance.Burst(chosen.AimPoint, new Color(1f, 0.65f, 0.15f));
                }
                ProceduralAudio.Fire(Sfx.Thud, origin);
                return true;
            }

            if (att.upgradeType == TowerUpgradeType.FireCatapult)
            {
                MonsterUnit chosen = SelectPriorityTarget(tower, GetEffectiveRange(tower), target);
                if (chosen == null || !chosen.IsAlive) chosen = target;
                att.AimAt(chosen.AimPoint);
                float mul = UnitDef.DamageMul(tower.type);
                Instance.LaunchFireball(origin, chosen, FireCatapultDirectDamage * mul, FireCatapultSplashDamage * mul, FireCatapultSplashRadius);
                return true;
            }

            return false;
        }

        /// <summary>Alias for TryFireUpgradedTower.</summary>
        public static bool FireUpgradedShot(PlacedUnit tower, Vector3 origin, MonsterUnit target, float baseDamage = -1f)
            => TryFireUpgradedTower(tower, origin, target, baseDamage);

        internal void OnTowerAutoFiredFallback(BuildingUpgradeAttachment att)
        {
            if (att == null || att.unit == null) return;
            Vector3 origin = att.unit.muzzle != null ? att.unit.muzzle.position : att.unit.transform.position + att.unit.transform.up * 0.09f;
            MonsterUnit target = SelectPriorityTarget(att.unit, GetEffectiveRange(att.unit), null);
            if (target == null) return;

            att.armAnimT = 0f;
            att.AimAt(target.AimPoint);
            float mul = UnitDef.DamageMul(att.unit.type);

            if (att.upgradeType == TowerUpgradeType.Ballista)
            {
                // Base PlacedUnit already fired base damage; apply the bonus armor-piercing delta directly
                float baseDmg = att.unit.def != null ? att.unit.def.EffectiveDamage : 2f * mul;
                float fullBallistaDmg = GetEffectiveDamage(att.unit, target);
                float extraDmg = Mathf.Max(0f, fullBallistaDmg - baseDmg);
                if (extraDmg > 0f && target.IsAlive && target.health != null)
                {
                    target.health.Damage(extraDmg);
                    if (IsArmoredOrBossTarget(target) && VfxPool.Instance != null)
                        VfxPool.Instance.Burst(target.AimPoint, new Color(1f, 0.72f, 0.2f));
                }
            }
            else if (att.upgradeType == TowerUpgradeType.FireCatapult)
            {
                LaunchFireball(origin, target, FireCatapultDirectDamage * mul * 0.5f, FireCatapultSplashDamage * mul, FireCatapultSplashRadius);
            }
        }

        // ====================================================================
        // 1. In-Place ArcherTower Upgrade API (Ballista & Fire/Catapult)
        // ====================================================================

        /// <summary>
        /// Upgrades an ArcherTower in-place.
        /// If un-upgraded (None), upgrades to Ballista Tower (8 Gold).
        /// If already a Ballista Tower, upgrades/converts to Fire/Catapult Tower (10 Gold).
        /// </summary>
        public bool TryUpgradeTower(PlacedUnit unit)
        {
            if (unit == null || unit.type != UnitType.ArcherTower) return false;
            var current = GetTowerUpgrade(unit);
            if (current == TowerUpgradeType.None)
                return TryUpgradeTower(unit, TowerUpgradeType.Ballista);
            if (current == TowerUpgradeType.Ballista)
                return TryUpgradeTower(unit, TowerUpgradeType.FireCatapult);
            return false;
        }

        /// <summary>
        /// Upgrades an existing ArcherTower in-place into the specified TowerUpgradeType (Ballista or FireCatapult),
        /// spending Gold, boosting max HP, and attaching procedural turret/catapult geometry.
        /// </summary>
        public bool TryUpgradeTower(PlacedUnit unit, TowerUpgradeType targetUpgrade)
        {
            if (unit == null || unit.type != UnitType.ArcherTower || unit.health == null || unit.health.IsDead)
                return false;
            if (targetUpgrade == TowerUpgradeType.None) return false;

            var gd = GameDirector.Instance;
            if (gd != null && !gd.IsCombatPhase) return false;

            var att = unit.GetComponent<BuildingUpgradeAttachment>();
            if (att == null) att = unit.gameObject.AddComponent<BuildingUpgradeAttachment>();
            att.unit = unit;

            if (att.upgradeType == targetUpgrade)
            {
                SpawnFloatingText(unit.transform.position + unit.transform.up * 0.11f,
                    targetUpgrade == TowerUpgradeType.Ballista ? "Ballista Ready" : "Fire Catapult Ready",
                    new Color(1f, 0.88f, 0.4f));
                return false;
            }

            int goldCost = targetUpgrade == TowerUpgradeType.Ballista ? BallistaGoldCost : FireCatapultGoldCost;
            if (gd != null && !gd.TrySpend(0, goldCost))
            {
                ProceduralAudio.Fire(Sfx.Deny, unit.transform.position);
                string title = targetUpgrade == TowerUpgradeType.Ballista ? "Ballista Tower" : "Fire Catapult";
                SpawnFloatingText(unit.transform.position + unit.transform.up * 0.11f,
                    $"{title}\nNeed {goldCost} Gold", new Color(1f, 0.5f, 0.42f));
                return false;
            }

            att.upgradeType = targetUpgrade;
            att.extraGoldSpent += goldCost;

            // Boost Max HP and heal the structure on upgrade
            float targetBonusHp = targetUpgrade == TowerUpgradeType.Ballista ? BallistaBonusHp : FireCatapultBonusHp;
            float deltaHp = Mathf.Max(0f, targetBonusHp - att.baseAddedHp);
            att.baseAddedHp = targetBonusHp;
            float newMax = unit.health.Max + deltaHp * UnitDef.HpMul(unit.type);
            unit.health.SetMaxKeepFraction(newMax);
            unit.health.Heal(deltaHp + newMax * 0.35f);

            // Rebuild procedural attachment on the tower
            BuildTowerUpgradeVisual(att);

            Vector3 pos = unit.transform.position;
            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Ring(pos, unit.Footprint * 2.0f);
                VfxPool.Instance.Burst(pos + unit.transform.up * 0.09f,
                    targetUpgrade == TowerUpgradeType.Ballista ? new Color(0.95f, 0.82f, 0.3f) : new Color(1f, 0.45f, 0.12f));
            }
            ProceduralAudio.Fire(Sfx.Hammer, pos);
            ProceduralAudio.Fire(Sfx.Coin, pos);

            string popupText = targetUpgrade == TowerUpgradeType.Ballista
                ? $"BALLISTA TOWER!\nArmor-Piercing (-{goldCost}G)"
                : $"FIRE CATAPULT!\nAoE Splash (-{goldCost}G)";
            Color popupCol = targetUpgrade == TowerUpgradeType.Ballista
                ? new Color(1f, 0.88f, 0.32f)
                : new Color(1f, 0.58f, 0.22f);
            SpawnFloatingText(pos + unit.transform.up * 0.12f, popupText, popupCol, true);

            TowerUpgraded?.Invoke(unit, targetUpgrade);
            Debug.Log($"[BuildingUpgradeController] Upgraded ArcherTower to {targetUpgrade} (-{goldCost} Gold)");
            return true;
        }

        // ====================================================================
        // 2. In-Place Structure Repair API (Walls, Barricades, any structure)
        // ====================================================================

        public bool CanRepairStructure(PlacedUnit unit, out int woodCost, out float missingHp)
        {
            woodCost = 0;
            missingHp = 0f;
            if (unit == null || unit.health == null || unit.health.IsDead) return false;
            missingHp = unit.health.Max - unit.health.Current;
            if (missingHp < 0.1f) return false;

            float baseBuildCost = unit.def != null ? unit.def.EffectiveCost : 10f;
            float fullRepairWood = Mathf.Max(2f, baseBuildCost * 0.45f);
            float missingFrac = Mathf.Clamp01(missingHp / Mathf.Max(1f, unit.health.Max));
            woodCost = Mathf.Clamp(Mathf.CeilToInt(fullRepairWood * missingFrac), 1, Mathf.CeilToInt(fullRepairWood));
            return true;
        }

        /// <summary>
        /// Spends Wood (Timber) to repair a damaged Wall, Barricade, or any PlacedUnit structure in-place,
        /// spawning a floating 3D feedback effect.
        /// </summary>
        public bool TryRepairStructure(PlacedUnit unit)
        {
            if (!CanRepairStructure(unit, out int fullWoodCost, out float missingHp))
                return false;

            var gd = GameDirector.Instance;
            if (gd != null && !gd.IsCombatPhase) return false;

            int spendWood = fullWoodCost;
            float healAmount = missingHp;

            if (gd != null)
            {
                if (!gd.CanAfford(spendWood, 0))
                {
                    int avail = Mathf.FloorToInt(gd.Timber);
                    if (avail >= 1)
                    {
                        spendWood = avail;
                        healAmount = missingHp * (spendWood / (float)fullWoodCost);
                    }
                    else
                    {
                        ProceduralAudio.Fire(Sfx.Deny, unit.transform.position);
                        SpawnFloatingText(unit.transform.position + unit.transform.up * 0.08f,
                            $"Repair: Need {fullWoodCost} Wood", new Color(1f, 0.48f, 0.42f));
                        return false;
                    }
                }
                if (!gd.TrySpend(spendWood, 0)) return false;
            }

            unit.health.Heal(healAmount);

            Vector3 pos = unit.transform.position;
            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Burst(pos + unit.transform.up * 0.04f, new Color(0.38f, 0.95f, 0.45f));
                VfxPool.Instance.Ring(pos, Mathf.Max(0.05f, unit.Footprint * 1.4f));
            }
            ProceduralAudio.Fire(Sfx.Hammer, pos);

            SpawnFloatingText(pos + unit.transform.up * 0.08f,
                $"+{Mathf.CeilToInt(healAmount)} HP\n(-{spendWood} Wood)",
                new Color(0.42f, 1f, 0.52f), true);

            StructureRepaired?.Invoke(unit, healAmount, spendWood);
            Debug.Log($"[BuildingUpgradeController] Repaired {unit.type} +{healAmount:0.#} HP (-{spendWood} Wood)");
            return true;
        }

        // ====================================================================
        // 3. Demolish / Salvage API (50% Build Resource Refund)
        // ====================================================================

        /// <summary>Computes the 50% Wood and Gold refund for salvaging a placed structure (including any upgrade gold invested).</summary>
        public static void GetSalvageRefund(PlacedUnit unit, out int timberRefund, out int goldRefund)
        {
            timberRefund = 0;
            goldRefund = 0;
            if (unit == null) return;

            int totalTimber = unit.def != null ? unit.def.EffectiveCost : 10;
            int totalGold = unit.def != null ? unit.def.EffectiveGoldCost : 0;

            var att = unit.GetComponent<BuildingUpgradeAttachment>();
            if (att != null)
            {
                totalTimber += att.extraTimberSpent;
                totalGold += att.extraGoldSpent;
            }

            timberRefund = totalTimber > 0 ? Mathf.Max(1, Mathf.RoundToInt(totalTimber * 0.5f)) : 0;
            goldRefund = totalGold > 0 ? Mathf.Max(1, Mathf.RoundToInt(totalGold * 0.5f)) : 0;
        }

        public bool SalvageUnit(PlacedUnit unit) => SalvageUnit(unit, out _, out _);

        /// <summary>
        /// Salvages an existing placed structure, refunding 50% of its build resources, unregistering its footprint,
        /// playing salvage VFX/SFX, and destroying the structure.
        /// </summary>
        public bool SalvageUnit(PlacedUnit unit, out int timberRefund, out int goldRefund)
        {
            timberRefund = 0;
            goldRefund = 0;
            if (unit == null || (unit.health != null && unit.health.IsDead)) return false;

            var gd = GameDirector.Instance;
            if (gd != null && !gd.IsCombatPhase) return false;

            GetSalvageRefund(unit, out timberRefund, out goldRefund);
            Vector3 pos = unit.transform.position;
            Vector3 up = unit.transform.up;
            float footprint = unit.Footprint;

            // Notify GameDirector / listeners to credit the refunded resources
            ResourceRefundRequested?.Invoke(timberRefund, goldRefund);
            StructureSalvaged?.Invoke(unit, timberRefund, goldRefund);

            if (SettlementBuildArea.Instance != null)
                SettlementBuildArea.Instance.Unregister(pos);

            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Burst(pos + up * 0.04f, new Color(1f, 0.84f, 0.26f));
                VfxPool.Instance.Ring(pos, Mathf.Max(0.06f, footprint * 1.5f));
            }
            ProceduralAudio.Fire(Sfx.Coin, pos);
            ProceduralAudio.Fire(Sfx.Hammer, pos);

            string refundMsg = goldRefund > 0
                ? $"SALVAGED\n+{timberRefund} Wood  +{goldRefund} Gold"
                : $"SALVAGED\n+{timberRefund} Wood";
            SpawnFloatingText(pos + up * 0.08f, refundMsg, new Color(1f, 0.88f, 0.35f), true);

            if (BuildingFactory.Instance != null)
                BuildingFactory.Instance.OnUnitDestroyed(unit);
            else
                Destroy(unit.gameObject);

            Debug.Log($"[BuildingUpgradeController] Salvaged structure for +{timberRefund} Wood, +{goldRefund} Gold");
            return true;
        }

        // ====================================================================
        // Update Loop: Hand Poke (Upgrade / Repair) + Pinch-Drag Salvage + VFX
        // ====================================================================

        void Update()
        {
            if (!subscribed) TrySubscribe();
            float dt = Time.deltaTime;

            UpdateFireballs(dt);
            UpdatePopups(dt);

            var gd = GameDirector.Instance;
            if (gd != null && !gd.IsCombatPhase)
            {
                CancelAllSalvageDrags();
                return;
            }

            var rig = HandRig.Instance;
            if (rig == null) return;

            for (int h = 0; h < 2; h++)
            {
                var side = (HandSide)h;
                if (!rig.IsTracked(side) || !rig.TryGetIndexTip(side, out Vector3 tip))
                {
                    wasInUnitPokeZone[h] = false;
                    if (wasPinching[h]) EndSalvagePinch(h, Vector3.zero, true);
                    wasPinching[h] = false;
                    continue;
                }

                bool pinching = rig.IsPinching(side);
                if (pinching)
                {
                    wasInUnitPokeZone[h] = false;
                    if (!wasPinching[h]) BeginSalvagePinch(h, tip);
                    else UpdateSalvagePinch(h, tip);
                }
                else
                {
                    if (wasPinching[h]) EndSalvagePinch(h, tip, false);
                    PollUnitPoke(h, tip);
                }
                wasPinching[h] = pinching;
            }
        }

        void PollUnitPoke(int h, Vector3 tip)
        {
            // Ignore pokes on the Town Hall (TownHall handles its own Age Up poke) or on tray tokens
            if (TownHall.Current != null)
            {
                Vector3 toHall = tip - TownHall.Current.transform.position;
                toHall -= Vector3.Dot(toHall, TownHall.Current.Up) * TownHall.Current.Up;
                if (toHall.sqrMagnitude <= (TownHall.PokeRadius + 0.015f) * (TownHall.PokeRadius + 0.015f))
                {
                    wasInUnitPokeZone[h] = false;
                    return;
                }
            }
            if (IsNearTraySlotTokens(tip, 0.055f))
            {
                wasInUnitPokeZone[h] = false;
                return;
            }

            PlacedUnit closest = FindClosestUnitForPoke(tip, UnitPokeRadius);
            if (closest != null)
            {
                if (!wasInUnitPokeZone[h] && Time.time - lastPokeTime[h] >= PokeCooldown)
                {
                    lastPokeTime[h] = Time.time;
                    HandleUnitPoked(closest);
                }
                wasInUnitPokeZone[h] = true;
            }
            else if (FindClosestUnitForPoke(tip, UnitPokeRadius * 1.15f) == null)
            {
                wasInUnitPokeZone[h] = false;
            }
        }

        /// <summary>Handles a finger poke on a PlacedUnit: upgrades ArcherTowers or repairs damaged structures.</summary>
        public bool HandleUnitPoked(PlacedUnit unit)
        {
            if (unit == null || unit.health == null || unit.health.IsDead) return false;
            var gd = GameDirector.Instance;
            bool isDamaged = unit.health.Current < unit.health.Max - 0.1f;

            if (unit.type == UnitType.ArcherTower)
            {
                var upg = GetTowerUpgrade(unit);
                if (upg == TowerUpgradeType.None)
                {
                    // If heavily damaged and player cannot afford Ballista Gold, repair with Wood instead
                    if (isDamaged && gd != null && !gd.CanAfford(0, BallistaGoldCost) && gd.CanAfford(1, 0))
                        return TryRepairStructure(unit);
                    return TryUpgradeTower(unit, TowerUpgradeType.Ballista);
                }
                if (upg == TowerUpgradeType.Ballista)
                {
                    // If damaged and either low HP or cannot afford FireCatapult conversion, repair first
                    if (isDamaged && (unit.health.Fraction < 0.7f || (gd != null && !gd.CanAfford(0, FireCatapultGoldCost) && gd.CanAfford(1, 0))))
                        return TryRepairStructure(unit);
                    return TryUpgradeTower(unit, TowerUpgradeType.FireCatapult);
                }
                // Fire/Catapult Tower (max tier): repair if damaged
                if (isDamaged) return TryRepairStructure(unit);
                SpawnFloatingText(unit.transform.position + unit.transform.up * 0.11f,
                    "Fire Catapult (Max Tier)", new Color(1f, 0.85f, 0.38f));
                return false;
            }

            // Wall, Barricade, Farm, Mine, Barracks, etc.: repair with Wood if damaged
            if (isDamaged)
            {
                return TryRepairStructure(unit);
            }

            ProceduralAudio.Fire(Sfx.Click, unit.transform.position);
            SpawnFloatingText(unit.transform.position + unit.transform.up * 0.07f,
                $"{unit.type} ({Mathf.CeilToInt(unit.health.Current)}/{Mathf.RoundToInt(unit.health.Max)} HP)",
                new Color(0.85f, 0.92f, 1f));
            return false;
        }

        static PlacedUnit FindClosestUnitForPoke(Vector3 tip, float radius)
        {
            var units = PlacedUnit.All;
            PlacedUnit bestUnit = null;
            float bestSq = radius * radius;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.health == null || u.health.IsDead) continue;
                Vector3 delta = tip - u.transform.position;
                float h = Vector3.Dot(delta, u.transform.up);
                float maxH = u.type == UnitType.ArcherTower ? 0.15f : 0.095f;
                if (h < -0.025f || h > maxH) continue;
                Vector3 planar = delta - u.transform.up * h;
                float r = Mathf.Max(radius, u.Footprint + 0.015f);
                float sq = planar.sqrMagnitude;
                if (sq <= r * r && sq < bestSq)
                {
                    bestSq = sq;
                    bestUnit = u;
                }
            }
            return bestUnit;
        }

        static bool IsNearTraySlotTokens(Vector3 tip, float radius)
        {
            var efc = EmpireFoundationController.Instance;
            if (efc == null || efc.trayRoot == null || !efc.trayRoot.gameObject.activeInHierarchy) return false;
            Vector3 local = efc.trayRoot.InverseTransformPoint(tip);
            // Tray base is 0.36 x 0.08 in XZ
            return Mathf.Abs(local.x) <= 0.20f + radius && Mathf.Abs(local.y) <= 0.09f + radius && Mathf.Abs(local.z) <= 0.06f + radius;
        }

        // ====================================================================
        // Pinch-Hold & Drop to Tray/Scroll Salvage Logic
        // ====================================================================

        void BeginSalvagePinch(int h, Vector3 tip)
        {
            pinchUnit[h] = null;
            isSalvageDragging[h] = false;
            if (IsNearTraySlotTokens(tip, 0.055f)) return;

            PlacedUnit best = null;
            float bestSq = SalvageGrabRadius * SalvageGrabRadius;
            var units = PlacedUnit.All;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.health == null || u.health.IsDead) continue;
                Vector3 center = u.transform.position + u.transform.up * 0.035f;
                float sq = (center - tip).sqrMagnitude;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = u;
                }
            }
            if (best == null) return;

            pinchUnit[h] = best;
            pinchStartTime[h] = Time.time;
            pinchStartPos[h] = tip;
        }

        void UpdateSalvagePinch(int h, Vector3 tip)
        {
            var u = pinchUnit[h];
            if (u == null || u.health == null || u.health.IsDead)
            {
                CancelSalvageDrag(h);
                return;
            }

            float held = Time.time - pinchStartTime[h];
            float distFromUnit = Vector3.Distance(tip, u.transform.position);

            if (!isSalvageDragging[h])
            {
                if (held >= SalvageHoldSeconds && distFromUnit >= SalvagePullThreshold)
                {
                    isSalvageDragging[h] = true;
                    ProceduralAudio.Fire(Sfx.Click, u.transform.position);
                }
                else
                {
                    return;
                }
            }

            // Visual lift feedback on the structure while dragging for salvage
            if (u.visual != null)
            {
                u.visual.localPosition = new Vector3(0f, 0.018f + Mathf.Sin(Time.time * 14f) * 0.003f, 0f);
            }

            bool readyToSalvage = IsInSalvageDropZone(tip, u.transform.position);
            GetSalvageRefund(u, out int wRefund, out int gRefund);
            string refundStr = gRefund > 0 ? $"+{wRefund}W +{gRefund}G" : $"+{wRefund}W";

            EnsureDragFeedback(h);
            var lbl = dragLabels[h];
            if (lbl != null)
            {
                if (!lbl.gameObject.activeSelf) lbl.gameObject.SetActive(true);
                lbl.transform.position = tip + Vector3.up * 0.055f;
                var cam = Camera.main;
                if (cam != null)
                {
                    Vector3 look = lbl.transform.position - cam.transform.position;
                    look.y = 0f;
                    if (look.sqrMagnitude > 1e-6f) lbl.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
                }
                lbl.text = readyToSalvage
                    ? $"RELEASE TO SALVAGE ({refundStr})"
                    : $"Drag to Tray/Scroll to Salvage ({refundStr})";
                lbl.color = readyToSalvage ? new Color(0.45f, 1f, 0.52f) : new Color(1f, 0.85f, 0.35f);
            }

            var lr = dragTethers[h];
            if (lr != null)
            {
                lr.enabled = true;
                lr.SetPosition(0, u.transform.position + u.transform.up * 0.04f);
                lr.SetPosition(1, tip);
                lr.sharedMaterial = Mats.Unlit(readyToSalvage ? new Color(0.45f, 1f, 0.52f) : new Color(1f, 0.78f, 0.25f));
            }
        }

        void EndSalvagePinch(int h, Vector3 releaseTip, bool cancelled)
        {
            var u = pinchUnit[h];
            bool dragging = isSalvageDragging[h];
            CancelSalvageDrag(h);

            if (cancelled || !dragging || u == null || u.health == null || u.health.IsDead)
                return;

            if (IsInSalvageDropZone(releaseTip, u.transform.position))
            {
                SalvageUnit(u);
            }
        }

        void CancelSalvageDrag(int h)
        {
            var u = pinchUnit[h];
            if (u != null && u.visual != null)
                u.visual.localPosition = Vector3.zero;
            pinchUnit[h] = null;
            isSalvageDragging[h] = false;
            if (dragLabels[h] != null && dragLabels[h].gameObject.activeSelf)
                dragLabels[h].gameObject.SetActive(false);
            if (dragTethers[h] != null)
                dragTethers[h].enabled = false;
        }

        void CancelAllSalvageDrags()
        {
            CancelSalvageDrag(0);
            CancelSalvageDrag(1);
        }

        /// <summary>
        /// Checks whether the pinched hand has pulled the structure back toward the building tray, the Imperial Scroll,
        /// or off the settlement toward the player.
        /// </summary>
        public static bool IsInSalvageDropZone(Vector3 handPos, Vector3 unitPos)
        {
            float moved = Vector3.Distance(handPos, unitPos);
            if (moved < SalvagePullThreshold) return false;

            // 1. Check proximity or movement toward the Building Tray
            var efc = EmpireFoundationController.Instance;
            if (efc != null && efc.trayRoot != null)
            {
                Vector3 trayPos = efc.trayRoot.position;
                float distToTray = Vector3.Distance(handPos, trayPos);
                if (distToTray <= 0.26f) return true;
                float startToTray = Vector3.Distance(unitPos, trayPos);
                if (moved >= 0.085f && distToTray <= startToTray - 0.055f) return true;
            }

            // 2. Check proximity or movement toward the Imperial Scroll
            var scroll = ImperialScroll.Instance;
            if (scroll != null)
            {
                Vector3 scrollPos = scroll.transform.position;
                float distToScroll = Vector3.Distance(handPos, scrollPos);
                if (distToScroll <= 0.38f) return true;
                float startToScroll = Vector3.Distance(unitPos, scrollPos);
                if (moved >= 0.09f && distToScroll <= startToScroll - 0.065f) return true;
            }

            // 3. Fallback: pulled back toward the player camera off the settlement
            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 camPos = cam.transform.position;
                float distToCam = Vector3.Distance(handPos, camPos);
                float startToCam = Vector3.Distance(unitPos, camPos);
                if (moved >= 0.14f && distToCam <= startToCam - 0.08f) return true;
            }

            return false;
        }

        void EnsureDragFeedback(int h)
        {
            if (dragLabels[h] == null)
            {
                var go = new GameObject("SalvageDragLabel_" + h);
                go.transform.SetParent(transform, false);
                var t = go.AddComponent<TextMeshPro>();
                t.fontSize = 0.16f;
                t.alignment = TextAlignmentOptions.Center;
                t.rectTransform.sizeDelta = new Vector2(0.38f, 0.08f);
                t.textWrappingMode = TextWrappingModes.NoWrap;
                go.SetActive(false);
                dragLabels[h] = t;
            }
            if (dragTethers[h] == null)
            {
                var go = new GameObject("SalvageDragTether_" + h);
                go.transform.SetParent(transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.startWidth = 0.004f;
                lr.endWidth = 0.002f;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.sharedMaterial = Mats.Unlit(new Color(1f, 0.78f, 0.25f));
                lr.enabled = false;
                dragTethers[h] = lr;
            }
        }

        // ====================================================================
        // Procedural Visual Attachments for Ballista & Fire/Catapult Towers
        // ====================================================================

        static void BuildTowerUpgradeVisual(BuildingUpgradeAttachment att)
        {
            if (att == null || att.unit == null || att.unit.visual == null) return;
            if (att.attachmentRoot != null) Destroy(att.attachmentRoot.gameObject);

            BuildingVisuals.Resolve( null, out var style, out var p);
            float deckY = att.unit.muzzle != null ? att.unit.muzzle.localPosition.y - 0.012f : 0.088f;

            att.attachmentRoot = BuildingVisuals.NewRoot(att.unit.visual, att.upgradeType + "_Attachment");
            var baseKit = new BuildingVisuals.Kit(att.attachmentRoot);

            if (att.upgradeType == TowerUpgradeType.Ballista)
            {
                BuildBallistaVisual(att, baseKit, p, deckY);
            }
            else if (att.upgradeType == TowerUpgradeType.FireCatapult)
            {
                BuildFireCatapultVisual(att, baseKit, p, deckY);
            }
        }

        static void BuildBallistaVisual(BuildingUpgradeAttachment att, BuildingVisuals.Kit baseKit, BuildingVisuals.Palette p, float deckY)
        {
            Color iron = new Color(0.26f, 0.27f, 0.31f);
            Color steel = new Color(0.68f, 0.72f, 0.78f);
            Color darkWood = new Color(0.32f, 0.19f, 0.09f);
            Color gold = new Color(0.95f, 0.78f, 0.24f);
            Color cord = new Color(0.86f, 0.82f, 0.72f);

            // Reinforced iron & gold turret ring on tower platform
            baseKit.Cyl(new Vector3(0f, deckY - 0.004f, 0f), 0.027f, 0.007f, iron);
            baseKit.Cyl(new Vector3(0f, deckY + 0.001f, 0f), 0.025f, 0.004f, gold);

            // Swivel pivot for the ballista weapon
            var pivot = BuildingVisuals.NewRoot(att.attachmentRoot, "BallistaTurretPivot");
            pivot.localPosition = new Vector3(0f, deckY + 0.008f, 0f);
            att.turretPivot = pivot;
            var k = new BuildingVisuals.Kit(pivot);

            // Central stock & iron slide trough
            k.Cyl(Vector3.zero, 0.008f, 0.010f, iron);
            k.Box(new Vector3(0f, 0.012f, 0.004f), new Vector3(0.010f, 0.007f, 0.056f), darkWood);
            k.Box(new Vector3(0f, 0.016f, 0.006f), new Vector3(0.005f, 0.003f, 0.054f), iron);

            // Torsion coil drums at the front
            k.Cyl(new Vector3(-0.011f, 0.006f, 0.018f), 0.005f, 0.014f, gold);
            k.Cyl(new Vector3(0.011f, 0.006f, 0.018f), 0.005f, 0.014f, gold);

            // Swept heavy steel crossbow prod arms
            k.Box(new Vector3(-0.023f, 0.013f, 0.013f), new Vector3(0.028f, 0.0045f, 0.005f), steel, new Vector3(0f, -24f, 0f));
            k.Box(new Vector3(0.023f, 0.013f, 0.013f), new Vector3(0.028f, 0.0045f, 0.005f), steel, new Vector3(0f, 24f, 0f));

            // Bowstrings pulled back to the trigger catch
            k.CylRot(new Vector3(-0.018f, 0.014f, -0.002f), 0.001f, 0.036f, cord, new Vector3(90f, 38f, 0f));
            k.CylRot(new Vector3(0.018f, 0.014f, -0.002f), 0.001f, 0.036f, cord, new Vector3(90f, -38f, 0f));

            // Heavy armor-piercing ballista bolt loaded in the trough
            var boltRoot = BuildingVisuals.NewRoot(pivot, "LoadedBolt");
            att.loadedProjectileVisual = boltRoot;
            var bk = new BuildingVisuals.Kit(boltRoot);
            bk.CylRot(new Vector3(0f, 0.019f, 0.010f), 0.0022f, 0.052f, darkWood, new Vector3(90f, 0f, 0f));
            bk.Cone(new Vector3(0f, 0.019f, 0.036f), 0.0048f, 0.014f, steel).transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            bk.Box(new Vector3(0f, 0.021f, -0.013f), new Vector3(0.008f, 0.0015f, 0.009f), gold);
        }

        static void BuildFireCatapultVisual(BuildingUpgradeAttachment att, BuildingVisuals.Kit baseKit, BuildingVisuals.Palette p, float deckY)
        {
            Color iron = new Color(0.23f, 0.23f, 0.26f);
            Color darkWood = new Color(0.35f, 0.20f, 0.09f);
            Color ember = new Color(1f, 0.38f, 0.08f);
            Color flame = new Color(1f, 0.78f, 0.20f);

            // Fortified stone & iron siege collar around tower top
            baseKit.Cyl(new Vector3(0f, deckY - 0.005f, 0f), 0.029f, 0.009f, iron);
            for (int i = 0; i < 4; i++)
            {
                float a = i * 90f + 45f;
                Quaternion q = Quaternion.Euler(0f, a, 0f);
                baseKit.Box(q * new Vector3(0f, deckY + 0.002f, 0.026f), new Vector3(0.009f, 0.014f, 0.008f), p.trim, new Vector3(0f, a, 0f));
            }

            // Flaming iron brazier mounted on the side of the tower platform
            Vector3 brazierPos = new Vector3(0.022f, deckY + 0.004f, -0.012f);
            baseKit.Cyl(brazierPos, 0.007f, 0.010f, iron);
            var coalGo = baseKit.Ball(brazierPos + Vector3.up * 0.011f, 0.011f, ember);
            coalGo.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(ember);
            var flameGo = baseKit.Cone(brazierPos + Vector3.up * 0.014f, 0.005f, 0.012f, flame);
            flameGo.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(flame);

            // Swivel catapult carriage
            var pivot = BuildingVisuals.NewRoot(att.attachmentRoot, "CatapultPivot");
            pivot.localPosition = new Vector3(0f, deckY + 0.006f, 0f);
            att.turretPivot = pivot;
            var k = new BuildingVisuals.Kit(pivot);

            // Twin timber side rails & crossbar stanchions
            k.Box(new Vector3(-0.013f, 0.005f, 0f), new Vector3(0.005f, 0.006f, 0.046f), darkWood);
            k.Box(new Vector3(0.013f, 0.005f, 0f), new Vector3(0.005f, 0.006f, 0.046f), darkWood);
            k.Box(new Vector3(-0.013f, 0.016f, 0.008f), new Vector3(0.004f, 0.022f, 0.006f), darkWood, new Vector3(-12f, 0f, 0f));
            k.Box(new Vector3(0.013f, 0.016f, 0.008f), new Vector3(0.004f, 0.022f, 0.006f), darkWood, new Vector3(-12f, 0f, 0f));
            k.CylRot(new Vector3(0f, 0.024f, 0.010f), 0.003f, 0.032f, iron, new Vector3(0f, 0f, 90f));
            k.CylRot(new Vector3(0f, 0.007f, -0.008f), 0.0035f, 0.032f, iron, new Vector3(0f, 0f, 90f));

            // Pivoting throwing arm with glowing fire boulder payload
            var arm = BuildingVisuals.NewRoot(pivot, "ThrowingArm");
            arm.localPosition = new Vector3(0f, 0.007f, -0.008f);
            arm.localRotation = Quaternion.Euler(-55f, 0f, 0f);
            att.throwingArm = arm;
            var ak = new BuildingVisuals.Kit(arm);
            ak.Box(new Vector3(0f, 0.022f, 0f), new Vector3(0.0045f, 0.044f, 0.0045f), darkWood);
            ak.Cyl(new Vector3(0f, 0.041f, -0.002f), 0.0085f, 0.004f, iron);

            var payload = BuildingVisuals.NewRoot(arm, "FireBoulderPayload");
            payload.localPosition = new Vector3(0f, 0.045f, -0.002f);
            att.loadedProjectileVisual = payload;
            var pk = new BuildingVisuals.Kit(payload);
            var orb = pk.Ball(Vector3.zero, 0.013f, ember);
            orb.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(ember);
            var core = pk.Ball(new Vector3(0f, 0.003f, 0f), 0.008f, flame);
            core.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(flame);
        }

        // ====================================================================
        // Arcing Fire/Catapult AoE Splash Projectiles
        // ====================================================================

        void LaunchFireball(Vector3 origin, MonsterUnit target, float directDmg, float splashDmg, float splashRadius)
        {
            if (target == null) return;
            ProceduralAudio.Fire(Sfx.Arrow, origin);
            ProceduralAudio.Fire(Sfx.Thud, origin);

            FireballShot fb = null;
            for (int i = 0; i < fireballs.Count; i++)
            {
                if (!fireballs[i].active) { fb = fireballs[i]; break; }
            }
            if (fb == null)
            {
                var root = new GameObject("FireCatapultBoulder").transform;
                root.SetParent(transform, false);
                var k = new BuildingVisuals.Kit(root);
                var outer = k.Ball(Vector3.zero, 0.016f, new Color(1f, 0.38f, 0.08f));
                outer.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(new Color(1f, 0.38f, 0.08f));
                var inner = k.Ball(new Vector3(0f, 0.003f, -0.003f), 0.011f, new Color(1f, 0.82f, 0.25f));
                inner.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(new Color(1f, 0.82f, 0.25f));
                fb = new FireballShot { visual = root };
                fireballs.Add(fb);
            }

            fb.from = origin;
            fb.target = target;
            fb.targetSerial = target.Serial;
            fb.targetPos = target.AimPoint;
            fb.directDamage = directDmg;
            fb.splashDamage = splashDmg;
            fb.splashRadius = splashRadius;
            fb.t = 0f;
            fb.emberTimer = 0f;
            fb.duration = Mathf.Max(0.14f, Vector3.Distance(origin, fb.targetPos) / 0.72f);
            fb.active = true;
            fb.visual.position = origin;
            fb.visual.gameObject.SetActive(true);
        }

        void UpdateFireballs(float dt)
        {
            for (int i = 0; i < fireballs.Count; i++)
            {
                var fb = fireballs[i];
                if (!fb.active) continue;

                bool targetValid = fb.target != null && fb.target.Serial == fb.targetSerial && fb.target.IsAlive;
                if (targetValid) fb.targetPos = fb.target.AimPoint;

                fb.t += dt / fb.duration;
                float u = Mathf.Clamp01(fb.t);
                Vector3 pos = Vector3.Lerp(fb.from, fb.targetPos, u) + Vector3.up * (Mathf.Sin(u * Mathf.PI) * 0.075f);
                fb.visual.position = pos;
                fb.visual.Rotate(380f * dt, 240f * dt, 0f, Space.Self);

                if (u < 1f) continue;

                // Impact: direct hit + AoE splash across all monsters within splashRadius
                if (targetValid && fb.target.health != null)
                {
                    fb.target.health.Damage(fb.directDamage);
                }

                float r2 = fb.splashRadius * fb.splashRadius;
                var list = MonsterUnit.Active;
                for (int mIdx = list.Count - 1; mIdx >= 0; mIdx--)
                {
                    if (mIdx >= list.Count) continue;
                    var m = list[mIdx];
                    if (m == null || !m.IsAlive || m.health == null) continue;
                    float d2 = (m.transform.position - fb.targetPos).sqrMagnitude;
                    if (d2 <= r2)
                    {
                        float falloff = Mathf.Lerp(1f, 0.55f, Mathf.Sqrt(d2) / Mathf.Max(0.01f, fb.splashRadius));
                        m.health.Damage(fb.splashDamage * falloff);
                    }
                }

                if (VfxPool.Instance != null)
                {
                    VfxPool.Instance.Ring(fb.targetPos, fb.splashRadius);
                    VfxPool.Instance.Burst(fb.targetPos, new Color(1f, 0.45f, 0.1f));
                }
                ProceduralAudio.Fire(Sfx.Thud, fb.targetPos);

                fb.active = false;
                fb.visual.gameObject.SetActive(false);
            }
        }

        void ClearFireballs()
        {
            for (int i = 0; i < fireballs.Count; i++)
            {
                fireballs[i].active = false;
                if (fireballs[i].visual != null) fireballs[i].visual.gameObject.SetActive(false);
            }
        }

        // ====================================================================
        // Pooled 3D Floating Feedback Effect
        // ====================================================================

        public void SpawnFloatingText(Vector3 worldPos, string text, Color color, bool showCrossIcon = false)
        {
            FloatingPopup p = null;
            for (int i = 0; i < popups.Count; i++)
            {
                if (!popups[i].active) { p = popups[i]; break; }
            }
            if (p == null)
            {
                var root = new GameObject("UpgradeFloatingPopup").transform;
                root.SetParent(transform, false);

                var labelGo = new GameObject("Label");
                labelGo.transform.SetParent(root, false);
                var tmp = labelGo.AddComponent<TextMeshPro>();
                tmp.fontSize = 0.16f;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.rectTransform.sizeDelta = new Vector2(0.36f, 0.09f);
                tmp.textWrappingMode = TextWrappingModes.NoWrap;

                // Small 3D plus/star emblem beside the floating feedback
                var iconRoot = BuildingVisuals.NewRoot(root, "PlusIcon");
                iconRoot.localPosition = new Vector3(0f, 0.028f, 0f);
                var ik = new BuildingVisuals.Kit(iconRoot);
                ik.Box(Vector3.zero, new Vector3(0.014f, 0.004f, 0.003f), new Color(0.45f, 1f, 0.55f));
                ik.Box(Vector3.zero, new Vector3(0.004f, 0.014f, 0.003f), new Color(0.45f, 1f, 0.55f));

                p = new FloatingPopup { root = root, label = tmp, icon = iconRoot };
                popups.Add(p);
            }

            p.startPos = worldPos;
            p.baseColor = color;
            p.elapsed = 0f;
            p.duration = 1.35f;
            p.active = true;
            p.label.text = text;
            p.label.color = color;
            if (p.icon != null) p.icon.gameObject.SetActive(showCrossIcon);
            p.root.position = worldPos;
            p.root.gameObject.SetActive(true);
        }

        void UpdatePopups(float dt)
        {
            var cam = Camera.main;
            for (int i = 0; i < popups.Count; i++)
            {
                var p = popups[i];
                if (!p.active) continue;
                p.elapsed += dt;
                if (p.elapsed >= p.duration)
                {
                    p.active = false;
                    p.root.gameObject.SetActive(false);
                    continue;
                }

                float u = p.elapsed / p.duration;
                p.root.position = p.startPos + Vector3.up * (u * 0.065f);
                if (cam != null)
                {
                    Vector3 look = p.root.position - cam.transform.position;
                    look.y = 0f;
                    if (look.sqrMagnitude > 1e-6f)
                        p.root.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
                }

                float alpha = u < 0.65f ? 1f : Mathf.Clamp01(1f - (u - 0.65f) / 0.35f);
                Color c = p.baseColor;
                c.a = alpha;
                p.label.color = c;
            }
        }

        void HideAllPopups()
        {
            for (int i = 0; i < popups.Count; i++)
            {
                popups[i].active = false;
                if (popups[i].root != null) popups[i].root.gameObject.SetActive(false);
            }
        }
    }
}
