using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// A placed defensive structure (Wall / ArcherTower / Barricade). Walls block monsters (MonsterUnit tests contact),
    /// towers shoot pooled arrows, barricades slow monsters inside their radius. Owner: Agent B.
    /// </summary>
    public class PlacedUnit : MonoBehaviour
    {
        /// <summary>All live placed units (same content as BuildingFactory.Units). Cached for monster queries.</summary>
        public static readonly List<PlacedUnit> All = new List<PlacedUnit>();

        public UnitType type;
        public UnitDef def;
        public Health health;
        public Transform visual;
        public Transform muzzle;

        /// <summary>Total shots fired / damage dealt (used by Quartermaster stats if desired).</summary>
        public int shots;

        float cooldown;
        float produceT;
        float popT;
        float shake;
        bool upgradeHooked;

        public float Footprint => def != null ? def.footprint : 0.05f;
        public bool IsEconomy => def != null && def.isEconomy;

        // Agent D is adding Sfx.Harvest / Sfx.Coin; resolved by name so this compiles either way.
        static readonly Sfx HarvestSfx = ParseSfx("Harvest");
        static readonly Sfx CoinSfx = ParseSfx("Coin");
        static Sfx ParseSfx(string name) => System.Enum.TryParse(name, out Sfx s) ? s : Sfx.Place;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => All.Clear();

        public void Init(UnitType t, CivilizationDefinition civOverride = null)
        {
            type = t;
            def = UnitDef.Get(t);
            if (health == null) health = gameObject.AddComponent<Health>();
            health.Init(def.EffectiveHp);
            health.Damaged -= OnDamaged;
            health.Died -= OnDied;
            health.Damaged += OnDamaged;
            health.Died += OnDied;
            if (visual != null) Destroy(visual.gameObject);
            visual = BuildingVisuals.BuildUnit(transform, t, civOverride, out muzzle);
            visual.localScale = Vector3.one * 0.2f;
            popT = 0f;
            cooldown = 0.3f;
            HookUpgrades();
            CivilizationPassives.ApplyToUnit(this, civOverride);
            ResourceOutpostNode.NotifyUnitPlaced(this);
            if (BarracksSpawner.IsBarracksType(t)) BarracksSpawner.AttachToBarracks(this, civOverride);
        }

        void OnEnable()
        {
            if (!All.Contains(this)) All.Add(this);
            HookUpgrades();
        }

        void OnDisable()
        {
            All.Remove(this);
            if (upgradeHooked && UpgradeState.Instance != null) UpgradeState.Instance.Changed -= OnUpgradesChanged;
            upgradeHooked = false;
        }

        void OnDestroy()
        {
            All.Remove(this);
            if (health != null) { health.Damaged -= OnDamaged; health.Died -= OnDied; }
        }

        void HookUpgrades()
        {
            if (upgradeHooked || def == null || UpgradeState.Instance == null) return;
            UpgradeState.Instance.Changed += OnUpgradesChanged;
            upgradeHooked = true;
        }

        void OnUpgradesChanged()
        {
            if (health != null && def != null && !health.IsDead)
            {
                health.SetMaxKeepFraction(def.EffectiveHp);
                CivilizationPassives.RefreshUnitMaxHp(this);
            }
        }

        void OnDamaged(Health h) { shake = 0.2f; }

        void OnDied(Health h)
        {
            if (VfxPool.Instance != null) VfxPool.Instance.Burst(transform.position + transform.up * 0.03f, new Color(0.55f, 0.42f, 0.3f));
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Death);
            if (BuildingFactory.Instance != null) BuildingFactory.Instance.OnUnitDestroyed(this);
            else Destroy(gameObject);
        }

        /// <summary>Planar contact test used by monsters: is world point p within `pad` of this unit's solid body?</summary>
        public bool Contacts(Vector3 p, float pad)
        {
            Vector3 local = transform.InverseTransformPoint(p);
            if (Mathf.Abs(local.y) > 0.15f) return false;
            if (type == UnitType.Wall)
                return Mathf.Abs(local.x) <= UnitDef.WallLength * 0.5f + pad && Mathf.Abs(local.z) <= UnitDef.WallThickness * 0.5f + pad;
            if (type == UnitType.ArcherTower || type == UnitType.AntiAir || type == UnitType.Farm || type == UnitType.Mine || BarracksSpawner.IsBarracksType(type))
            {
                float r = ((type == UnitType.ArcherTower || type == UnitType.AntiAir) ? 0.026f : BarracksSpawner.IsBarracksType(type) ? 0.038f : 0.035f) + pad;
                return local.x * local.x + local.z * local.z <= r * r;
            }
            return false; // barricades slow, they do not block
        }

        /// <summary>Is world point p inside this barricade's slow zone?</summary>
        public bool InSlowZone(Vector3 p)
        {
            if (type != UnitType.Barricade || def == null) return false;
            Vector3 local = transform.InverseTransformPoint(p);
            if (Mathf.Abs(local.y) > 0.1f) return false;
            return local.x * local.x + local.z * local.z <= def.range * def.range;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (visual != null)
            {
                if (popT < 1f)
                {
                    popT = Mathf.Min(1f, popT + dt / 0.25f);
                    float s = 1f + Mathf.Sin(popT * Mathf.PI) * 0.15f;
                    visual.localScale = Vector3.one * Mathf.Lerp(0.2f, 1f, popT) * s;
                }
                if (shake > 0f)
                {
                    shake -= dt;
                    visual.localPosition = shake > 0f ? new Vector3(Mathf.Sin(Time.time * 95f) * 0.002f, 0f, 0f) : Vector3.zero;
                }
            }

            CivilizationPassives.TickUnitPassive(this, dt);

            if (def != null && def.isEconomy && health != null && !health.IsDead)
            {
                // production feedback (income itself is computed by GameDirector from UnitDef rates)
                produceT += dt;
                if (produceT >= 3f)
                {
                    produceT = 0f;
                    Vector3 p = transform.position + transform.up * 0.06f;
                    bool farm = type == UnitType.Farm;
                    if (VfxPool.Instance != null) VfxPool.Instance.Burst(p, farm ? new Color(0.45f, 0.9f, 0.3f) : new Color(1f, 0.82f, 0.2f));
                    if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(farm ? HarvestSfx : CoinSfx, p);
                }
                return;
            }
            if (type != UnitType.ArcherTower || def == null || health == null || health.IsDead) return;

            if (HandOfGodController.TryConsumeVolley(this, out _)) cooldown = 0f;
            cooldown -= dt;
            if (cooldown > 0f) return;

            Vector3 origin = muzzle != null ? muzzle.position : transform.position;
            float range = BuildingUpgradeController.GetEffectiveRange(this);
            float best = range * range;
            Vector3 me = transform.position;
            MonsterUnit target = HandOfGodController.GetFocusedTargetInRange(me, range);
            if (target == null)
            {
                var list = MonsterUnit.Active;
                for (int i = 0; i < list.Count; i++)
                {
                    var m = list[i];
                    if (m == null || !m.IsAlive) continue;
                    float d = (m.transform.position - me).sqrMagnitude;
                    if (d <= best) { best = d; target = m; }
                }
            }
            target = BuildingUpgradeController.SelectPriorityTarget(this, range, target);
            if (target == null) { cooldown = 0.1f; return; }

            if (!BuildingUpgradeController.TryFireUpgradedTower(this, origin, target, def.EffectiveDamage))
            {
                CivilizationPassives.FireTowerProjectile(origin, target, def.EffectiveDamage);
            }
            shots++;
            cooldown = BuildingUpgradeController.GetEffectiveFireInterval(this);
        }
    }
}
