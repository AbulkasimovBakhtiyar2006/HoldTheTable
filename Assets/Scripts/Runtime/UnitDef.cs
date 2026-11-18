using UnityEngine;

namespace HoldTheTable
{
    public enum UnitType { Wall, ArcherTower, Barricade, Farm, Mine }

    /// <summary>Static per-type stats. Owner: Agent B.</summary>
    public sealed class UnitDef
    {
        public UnitType type;
        public int cost;
        public float hp;
        /// <summary>Footprint radius in meters (used for placement/overlap).</summary>
        public float footprint;
        /// <summary>Tower: attack range. Barricade: slow-zone radius. Wall: 0.</summary>
        public float range;
        /// <summary>Tower: damage per shot.</summary>
        public float damage;
        /// <summary>Barricade: speed multiplier inside the zone (0.4 = 40% speed). 1 = no slow.</summary>
        public float slow;
        /// <summary>Tower: seconds between shots.</summary>
        public float fireInterval;
        /// <summary>Gold cost in addition to the timber cost.</summary>
        public int goldCost;
        /// <summary>Economy: timber / gold produced per second while alive.</summary>
        public float timberPerSec;
        public float goldPerSec;
        /// <summary>Economy building: does not block, shoot or slow; produces resources.</summary>
        public bool isEconomy;

        static readonly UnitDef[] defs =
        {
            new UnitDef { type = UnitType.Wall,        cost = 10, hp = 30f, footprint = 0.06f, range = 0f,    damage = 0f, slow = 1f,   fireInterval = 0f },
            new UnitDef { type = UnitType.ArcherTower, cost = 15, hp = 20f, footprint = 0.05f, range = 0.25f, damage = 2f, slow = 1f,   fireInterval = 1f, goldCost = 4 },
            new UnitDef { type = UnitType.Barricade,   cost = 8,  hp = 15f, footprint = 0.05f, range = 0.08f, damage = 0f, slow = 0.4f, fireInterval = 0f },
            new UnitDef { type = UnitType.Farm,        cost = 12, hp = 15f, footprint = 0.05f, range = 0f,    damage = 0f, slow = 1f,   fireInterval = 0f, timberPerSec = 1.5f, isEconomy = true },
            new UnitDef { type = UnitType.Mine,        cost = 18, hp = 20f, footprint = 0.05f, range = 0f,    damage = 0f, slow = 1f,   fireInterval = 0f, goldPerSec = 1f,     isEconomy = true },
        };

        public static UnitDef Get(UnitType t) => defs[(int)t];

        public static float HpMul(UnitType t) => UpgradeState.Instance != null ? UpgradeState.Instance.HpMul(t) : 1f;
        public static float DamageMul(UnitType t) => UpgradeState.Instance != null ? UpgradeState.Instance.DamageMul(t) : 1f;
        public static float CostMul(UnitType t) => UpgradeState.Instance != null ? UpgradeState.Instance.CostMul(t) : 1f;

        public int EffectiveCost => Mathf.Max(1, Mathf.RoundToInt(cost * CostMul(type)));
        public int EffectiveGoldCost => goldCost <= 0 ? 0 : Mathf.Max(1, Mathf.RoundToInt(goldCost * CostMul(type)));
        public static int TypeCount => defs.Length;
        public float EffectiveHp => hp * HpMul(type);
        public float EffectiveDamage => damage * DamageMul(type);

        // Wall segment dimensions (meters) - also used by monsters for contact tests.
        public const float WallLength = 0.10f;
        public const float WallThickness = 0.025f;
        public const float WallHeight = 0.05f;
    }
}
