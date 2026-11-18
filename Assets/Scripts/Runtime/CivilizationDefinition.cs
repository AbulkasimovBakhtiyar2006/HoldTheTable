using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    public enum ArchitectureStyle { TieredRoofs, Timber, StoneTowers }

    /// <summary>One playable civilization archetype with architectural palette and unique gameplay bonuses. Owner: Agent C / civ-bonuses.</summary>
    public sealed class CivilizationDefinition
    {
        public string id;
        public string nameKey;
        public string descKey;
        public ArchitectureStyle style;
        public Color primary;
        public Color accent;
        public string suggestedName;

        // ---- Gameplay bonuses (Feature 1: Making Civilization Choice Impact Gameplay) ----
        /// <summary>Multiplier applied to max HP of all player structures (PlacedUnits &amp; TownHall). Iron Dominion = 1.35f (+35%).</summary>
        public float structureHpMultiplier = 1f;
        /// <summary>Multiplier applied to Farm timber/wood production rate. Verdant Clans = 1.40f (+40%).</summary>
        public float farmTimberMultiplier = 1f;
        /// <summary>Multiplier applied to Mine gold production rate. Iron Dominion = 1.35f (+35%).</summary>
        public float mineGoldMultiplier = 1f;

        /// <summary>Celestial Dynasty: ArcherTowers shoot fire/firework arrows with micro-splash AoE + spark VFX.</summary>
        public bool hasFireworkArrows;
        /// <summary>Splash radius (meters) of Celestial firework arrows on impact.</summary>
        public float fireArrowSplashRadius = 0.075f;
        /// <summary>Splash damage ratio (fraction of direct arrow damage) dealt to nearby monsters inside splash radius.</summary>
        public float fireArrowSplashDamageRatio = 0.55f;
        /// <summary>Celestial Dynasty: Shockwave cooldown (and wave charge) resets automatically at the end of every wave.</summary>
        public bool resetShockwaveOnWaveClear;

        /// <summary>Verdant Clans: Walls and Barricades passively regenerate HP between waves with a living-wood pulse.</summary>
        public bool hasLivingWoodRegen;
        /// <summary>HP regenerated per second between waves for Walls and Barricades.</summary>
        public float wallBarricadeRegenPerSec = 3.5f;

        /// <summary>Iron Dominion: Mines periodically zap nearby monsters in close range with a procedural lightning arc.</summary>
        public bool hasMineLightningZap;
        /// <summary>Close-range lightning zap radius (meters) around an Iron Dominion Mine.</summary>
        public float mineZapRange = 0.14f;
        /// <summary>Lightning zap damage dealt to the closest monster in range.</summary>
        public float mineZapDamage = 2.2f;
        /// <summary>Seconds between Mine lightning zaps.</summary>
        public float mineZapInterval = 1.6f;

        /// <summary>Concise player-facing summary of the civilization's gameplay bonuses.</summary>
        public string passiveSummary = "";

        public string DisplayName => nameKey;
        public string Description => descKey;
        public string PassiveSummary => passiveSummary;

        public bool IsCelestial => id == CivilizationCatalog.CelestialId;
        public bool IsVerdant => id == CivilizationCatalog.VerdantId;
        public bool IsIron => id == CivilizationCatalog.IronId;

        /// <summary>Returns structure max HP with this civilization's HP bonus applied (+35% for Iron Dominion).</summary>
        public float ApplyStructureHp(float baseHp) => baseHp * (structureHpMultiplier > 0f ? structureHpMultiplier : 1f);

        /// <summary>Returns Farm timber/sec with this civilization's wood bonus applied (+40% for Verdant Clans).</summary>
        public float ApplyFarmTimber(float baseTimberPerSec) => baseTimberPerSec * (farmTimberMultiplier > 0f ? farmTimberMultiplier : 1f);

        /// <summary>Returns Mine gold/sec with this civilization's gold bonus applied (+35% for Iron Dominion).</summary>
        public float ApplyMineGold(float baseGoldPerSec) => baseGoldPerSec * (mineGoldMultiplier > 0f ? mineGoldMultiplier : 1f);

        /// <summary>Returns the resource rate multiplier for a given UnitType (Farm -> farmTimberMultiplier, Mine -> mineGoldMultiplier, others -> 1f).</summary>
        public float GetEconomyMultiplier(UnitType type)
        {
            if (type == UnitType.Farm) return farmTimberMultiplier > 0f ? farmTimberMultiplier : 1f;
            if (type == UnitType.Mine) return mineGoldMultiplier > 0f ? mineGoldMultiplier : 1f;
            return 1f;
        }
    }

    public static class CivilizationCatalog
    {
        public const string DefaultId = "celestial";
        public const string CelestialId = "celestial";
        public const string VerdantId = "verdant";
        public const string IronId = "iron";

        static readonly CivilizationDefinition[] all =
        {
            new CivilizationDefinition
            {
                id = CelestialId, nameKey = Strings.CivCelestialName, descKey = Strings.CivCelestialDesc,
                style = ArchitectureStyle.TieredRoofs,
                primary = new Color(0.72f, 0.08f, 0.12f), accent = new Color(0.96f, 0.76f, 0.22f),
                suggestedName = "Heavenly Kingdom",
                structureHpMultiplier = 1f,
                farmTimberMultiplier = 1f,
                mineGoldMultiplier = 1f,
                hasFireworkArrows = true,
                fireArrowSplashRadius = 0.075f,
                fireArrowSplashDamageRatio = 0.55f,
                resetShockwaveOnWaveClear = true,
                passiveSummary = "Towers fire explosive firework arrows (micro-splash AoE). Shockwave resets after every wave."
            },
            new CivilizationDefinition
            {
                id = VerdantId, nameKey = Strings.CivVerdantName, descKey = Strings.CivVerdantDesc,
                style = ArchitectureStyle.Timber,
                primary = new Color(0.18f, 0.50f, 0.22f), accent = new Color(0.47f, 0.31f, 0.15f),
                suggestedName = "Greenhold",
                structureHpMultiplier = 1f,
                farmTimberMultiplier = 1.40f,
                mineGoldMultiplier = 1f,
                hasLivingWoodRegen = true,
                wallBarricadeRegenPerSec = 3.5f,
                passiveSummary = "Farms yield +40% timber. Walls & barricades regenerate HP between waves."
            },
            new CivilizationDefinition
            {
                id = IronId, nameKey = Strings.CivIronName, descKey = Strings.CivIronDesc,
                style = ArchitectureStyle.StoneTowers,
                primary = new Color(0.52f, 0.56f, 0.62f), accent = new Color(0.16f, 0.32f, 0.68f),
                suggestedName = "Iron Crown",
                structureHpMultiplier = 1.35f,
                farmTimberMultiplier = 1f,
                mineGoldMultiplier = 1.35f,
                hasMineLightningZap = true,
                mineZapRange = 0.14f,
                mineZapDamage = 2.2f,
                mineZapInterval = 1.6f,
                passiveSummary = "All structures gain +35% max HP. Mines yield +35% gold and zap nearby monsters with lightning."
            },
        };

        public static IReadOnlyList<CivilizationDefinition> All => all;

        /// <summary>Returns the definition with this id, or the default (celestial) when unknown.</summary>
        public static CivilizationDefinition Get(string id)
        {
            if (!string.IsNullOrEmpty(id))
                for (int i = 0; i < all.Length; i++)
                    if (all[i].id == id) return all[i];
            return all[0];
        }

        public static bool Exists(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            for (int i = 0; i < all.Length; i++) if (all[i].id == id) return true;
            return false;
        }

        /// <summary>Returns the currently selected player civilization definition (null-safe, defaults to Celestial).</summary>
        public static CivilizationDefinition Active
        {
            get
            {
                var prof = CivilizationProfile.Current;
                return prof != null ? Get(prof.civId) : all[0];
            }
        }

        public static string ActiveId => Active.id;
        public static bool IsCelestialActive => Active.IsCelestial;
        public static bool IsVerdantActive => Active.IsVerdant;
        public static bool IsIronActive => Active.IsIron;

        public static float ActiveStructureHpMultiplier => Active.structureHpMultiplier;
        public static float ActiveFarmTimberMultiplier => Active.farmTimberMultiplier;
        public static float ActiveMineGoldMultiplier => Active.mineGoldMultiplier;
    }
}
