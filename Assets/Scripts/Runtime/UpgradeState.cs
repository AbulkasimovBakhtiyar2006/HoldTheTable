using System;
using UnityEngine;

namespace HoldTheTable
{
    public enum UpgradeKind { Damage, Hp, Cost, ShockwaveCharge, Harvest }

    [Serializable]
    public class UpgradeOption
    {
        public UpgradeKind kind;
        public UnitType unit;
        public string title;
        public string description;

        public static string UnitName(UnitType t)
        {
            switch (t)
            {
                case UnitType.Wall: return Strings.WallName;
                case UnitType.ArcherTower: return Strings.TowerName;
                default: return Strings.BarricadeName;
            }
        }

        public static UpgradeOption Create(UpgradeKind kind, UnitType unit)
        {
            string n = UnitName(unit);
            var o = new UpgradeOption { kind = kind, unit = unit };
            switch (kind)
            {
                case UpgradeKind.Damage: o.title = string.Format(Strings.UpgradeDamage, n); o.description = string.Format(Strings.UpgradeDamageDesc, n); break;
                case UpgradeKind.Hp: o.title = string.Format(Strings.UpgradeHp, n); o.description = string.Format(Strings.UpgradeHpDesc, n); break;
                case UpgradeKind.Cost: o.title = string.Format(Strings.UpgradeCost, n); o.description = string.Format(Strings.UpgradeCostDesc, n); break;
                case UpgradeKind.Harvest: o.title = Strings.UpgradeHarvest; o.description = Strings.UpgradeHarvestDesc; break;
                default: o.title = Strings.UpgradeCharge; o.description = Strings.UpgradeChargeDesc; break;
            }
            return o;
        }
    }

    /// <summary>Real gameplay modifiers chosen from the Quartermaster plus per-type usage statistics. Owner: Agent C.</summary>
    public class UpgradeState : MonoBehaviour
    {
        public const float DamageStep = 1.35f;
        public const float HpStep = 1.40f;
        public const float CostStep = 0.75f;
        public const float MinCostMul = 0.4f;
        /// <summary>Harvest upgrade: +30% farm/mine output per pick (multiplicative).</summary>
        public const float HarvestStep = 1.30f;
        static readonly int TypeCount = Enum.GetValues(typeof(UnitType)).Length;

        static UpgradeState instance;
        static bool quitting;
        public static UpgradeState Instance
        {
            get
            {
                if (instance == null && !quitting && Application.isPlaying)
                    instance = new GameObject("UpgradeState").AddComponent<UpgradeState>();
                return instance;
            }
        }

        readonly float[] damageMul = Ones();
        readonly float[] hpMul = Ones();
        readonly float[] costMul = Ones();
        readonly int[] usage = new int[TypeCount];

        static float[] Ones()
        {
            var a = new float[TypeCount];
            for (int i = 0; i < a.Length; i++) a[i] = 1f;
            return a;
        }

        /// <summary>Multiplier for Farm timber and Mine gold output (Harvest upgrade).</summary>
        public float EconomyMul { get; private set; } = 1f;

        public int ExtraCharges { get; private set; }
        public int AppliedCount { get; private set; }
        public event Action Changed;

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(this); return; }
            instance = this;
        }

        void OnApplicationQuit() => quitting = true;
        void OnDestroy() { if (instance == this) instance = null; }

        static int Idx(UnitType t) => Mathf.Clamp((int)t, 0, TypeCount - 1);

        public float DamageMul(UnitType t) => damageMul[Idx(t)];
        public float HpMul(UnitType t) => hpMul[Idx(t)];
        public float CostMul(UnitType t) => costMul[Idx(t)];
        public int UsageCount(UnitType t) => usage[Idx(t)];

        public void Apply(UpgradeOption o)
        {
            if (o == null) return;
            int i = Idx(o.unit);
            switch (o.kind)
            {
                case UpgradeKind.Damage: damageMul[i] *= DamageStep; break;
                case UpgradeKind.Hp: hpMul[i] *= HpStep; break;
                case UpgradeKind.Cost: costMul[i] = Mathf.Max(MinCostMul, costMul[i] * CostStep); break;
                case UpgradeKind.ShockwaveCharge: ExtraCharges++; break;
                case UpgradeKind.Harvest: EconomyMul *= HarvestStep; break;
            }
            AppliedCount++;
            Debug.Log($"[UpgradeState] Applied {o.kind} {o.unit} dmg={damageMul[i]:0.00} hp={hpMul[i]:0.00} cost={costMul[i]:0.00} extraCharges={ExtraCharges}");
            Changed?.Invoke();
        }

        /// <summary>Usage statistic driving Quartermaster offers. Called automatically by SettlementBuildArea when a new defense appears — do not call it again from TraySlot.</summary>
        public void RecordPlacement(UnitType t)
        {
            if (IsEconomy(t)) return;
            usage[Idx(t)]++;
        }

        public void ResetAll()
        {
            for (int i = 0; i < TypeCount; i++) { damageMul[i] = 1f; hpMul[i] = 1f; costMul[i] = 1f; usage[i] = 0; }
            ExtraCharges = 0;
            EconomyMul = 1f;
            AppliedCount = 0;
            Changed?.Invoke();
        }

        /// <summary>Two most-used unit types (descending). Counts may be 0.</summary>
        public void MostUsed(out UnitType first, out int firstCount, out UnitType second, out int secondCount)
        {
            int a = -1, b = -1;
            for (int i = 0; i < TypeCount; i++)
            {
                if (IsEconomy((UnitType)i)) continue;
                if (a < 0 || usage[i] > usage[a]) { b = a; a = i; }
                else if (b < 0 || usage[i] > usage[b]) b = i;
            }
            first = (UnitType)a; firstCount = usage[a];
            second = (UnitType)b; secondCount = usage[b];
        }

        /// <summary>Farm / Mine (UnitDef.isEconomy): never offered unit upgrades, never counted in usage.</summary>
        public static bool IsEconomy(UnitType t)
        {
            var d = UnitDef.Get(t);
            return d != null && d.isEconomy;
        }

        /// <summary>Is this upgrade meaningful for this unit type?</summary>
        public static bool IsValid(UpgradeKind k, UnitType t)
        {
            if (k == UpgradeKind.Harvest || k == UpgradeKind.ShockwaveCharge) return true;
            if (IsEconomy(t)) return false;
            if (k == UpgradeKind.Damage) return t == UnitType.ArcherTower;
            return true;
        }
    }
}
