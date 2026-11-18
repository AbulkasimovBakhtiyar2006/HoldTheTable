namespace HoldTheTable
{
    /// <summary>All player-visible English text. Sole owner: Agent D. User data (empire name, emblem) is not stored here.</summary>
    public static class Strings
    {
        public const string Title = "Hold the Table";
        public const string Subtitle = "Found your empire. Defend your hall.";
        public const string ChooseCivilization = "Choose your civilization";
        public const string CivCelestialName = "Celestial Dynasty";
        public const string CivCelestialDesc = "Tiered roofs and jade lanterns. Order under heaven.";
        public const string CivVerdantName = "Verdant Clans";
        public const string CivVerdantDesc = "Timber halls and living wood. Strength of the forest.";
        public const string CivIronName = "Iron Dominion";
        public const string CivIronDesc = "Stone towers and iron gates. Walls that never fall.";
        public const string Next = "Next";
        public const string Back = "Back";
        public const string NameYourEmpire = "Name your empire";
        public const string Space = "Space";
        public const string Backspace = "Del";
        public const string Confirm = "Confirm";
        public const string TypeName = "Tap to type the name";
        public const string BannerTitle = "Design your banner";
        public const string BannerColor = "Banner color";
        public const string EmblemColor = "Emblem color";
        public const string DrawEmblem = "Draw your emblem with a fingertip";
        public const string Undo = "Undo";
        public const string Clear = "Clear";
        public const string BrushThin = "Thin";
        public const string BrushThick = "Thick";
        public const string FoundEmpire = "Found Empire";
        public const string NewEmpire = "New Empire";
        public const string PlaceTownHallHint = "Pinch the Town Hall from the scroll and place it on a flat surface.";
        public const string PlacementInvalidSupport = "Needs solid ground beneath";
        public const string PlacementInvalidOverlap = "Too close to another building";
        public const string PlacementInvalidOutside = "Build next to your settlement";
        public const string PlacementInvalidLimit = "Defense limit reached";
        public const string PlacementInvalidObstacle = "Something is in the way";
        public const string NotEnoughTimber = "Not enough timber";
        public const string Timber = "Timber {0}";
        public const string Wave = "Wave {0}/{1}";
        public const string BuildPhase = "Build your defenses";
        public const string Countdown = "Next wave in {0}s";
        public const string KeepHp = "Hall {0}/{1}";
        public const string Charges = "Shockwave {0}";
        public const string WallName = "Wall";
        public const string TowerName = "Archer Tower";
        public const string BarricadeName = "Barricade";
        public const string FarmName = "Farm";
        public const string MineName = "Mine";
        public const string GoldCostSuffix = " + {0} gold";
        public const string Victory = "Victory";
        public const string Defeat = "Defeat";
        public const string Score = "Score {0}";
        public const string Restart = "RESTART";
        public const string QuartermasterGreeting = "Pick a supply, commander.";
        public const string QuartermasterSkip = "No choice? I'll pack up in {0}s.";
        public const string UpgradeDamage = "Sharper {0}";
        public const string UpgradeHp = "Sturdier {0}";
        public const string UpgradeCost = "Cheaper {0}";
        public const string UpgradeCharge = "Extra Shockwave";
        public const string UpgradeDamageDesc = "+35% damage for every {0}.";
        public const string UpgradeHpDesc = "+40% health for every {0}.";
        public const string UpgradeCostDesc = "Each {0} costs 25% less timber.";
        public const string UpgradeChargeDesc = "+1 shockwave charge.";
        public const string WaveIncoming = "Wave incoming!";
        public const string RamIncoming = "A ram approaches!";
        public const string Founded = "{0} is founded!";
        public const string LookingForSurfaces = "Looking for surfaces...";
        public const string FallbackTableNotice = "No surface found. Using a virtual table.";

        // Additional UI text used by Agent D components only.
        public const string Skip = "Skip";
        public const string Shift = "Shift";
        public const string SummaryCivilization = "Civilization: {0}";
        public const string ShockwaveHint = "Sweep both palms apart for a shockwave";
        public const string PlaceDefensesHint = "Pinch a token from the tray to build";
        public const string WaveFight = "Defend the hall!";

        // Enemy climbing (Agent B: ClimbRoutes announcements).
        public const string EngineerLadder = "Enemy engineers are raising a ladder!";
        public const string LadderBuilt = "An enemy ladder reaches your hall!";
        public const string RopeSet = "The enemy has set a rope!";
        public const string ClimbAccessDestroyed = "Enemy climbing gear destroyed!";

        // Economy upgrade (Agent C: Quartermaster Harvest card).
        public const string UpgradeHarvest = "Bountiful Harvest";
        public const string UpgradeHarvestDesc = "Farms and mines produce 30% more.";

        // Economy readouts (Agent D HUD).
        public const string Gold = "Gold {0}";
        public const string IncomeRate = "+{0}/s";

        public static string Format(string key, params object[] args) => string.Format(key, args);
    }
}
