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
        public const string RollUp = "Roll up";
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
        public const string PlaceDefensesHint = "Touch a building on your wrist menu, then pinch to place it";
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

        // ---- WonderBlock (WorldEventsController wonders; append-only) ----
        public const string WonderJadeBellName = "Jade Bell";
        public const string WonderYggdrasilName = "Yggdrasil";
        public const string WonderTeslaName = "Tesla Tower";
        public const string WonderJadeBellDesc = "Tolls every 10 s: goblins nearby are stunned.";
        public const string WonderYggdrasilDesc = "Heals buildings and defenders. Grows over time.";
        public const string WonderTeslaDesc = "Chain lightning strikes goblins.";
        public const string WonderOffer = "{0}\nPinch and place\n{1} timber  {2} gold";
        public const string WonderBuilding = "Wonder rising...";
        public const string WonderComplete = "{0} completed!";
        public const string WonderCannotAfford = "Not enough resources for the wonder";
        // ---- /WonderBlock ----

        // ---- BEGIN Anti-Air (AntiAirTower)
        public const string AntiAirName = "Anti-Air Battery";
        // ---- END Anti-Air

        // ---- BEGIN Divine Miracles & Hero (DivineMiraclesAndHeroController) ----
        public const string HeroSlam = "Heroic Slam!";
        public const string HeroReady = "Your champion is ready. Pinch and drop him on the table.";
        public const string HeroResting = "The champion rests...";
        public const string MeteorRain = "Meteor Rain!";
        public const string MeteorHint = "Hold both palms flat, facing down, for Meteor Rain";
        public const string MeteorEmpty = "No meteor charges left.";
        public const string DomeRaised = "Protective Dome!";
        public const string DomeFaded = "The dome fades.";
        public const string DomeHint = "Cross your wrists above the hall for a Protective Dome";
        public const string MiracleRecharging = "The heavens are recharging...";
        // ---- END Divine Miracles & Hero ----

        // ---- BEGIN Trophy stand (ImperialTrophiesAndAudioJuiceController)
        public const string TrophyLocked = "Locked";
        public const string TrophyFirstBlood = "First Blood";
        public const string TrophyFirstBloodDesc = "Defeat your first goblin.";
        public const string TrophySlayer = "Goblin Slayer";
        public const string TrophySlayerDesc = "Defeat {0} goblins in one siege.";
        public const string TrophyBuilder = "Master Builder";
        public const string TrophyBuilderDesc = "Raise {0} structures at once.";
        public const string TrophyVictory = "Table Holder";
        public const string TrophyVictoryDesc = "Survive all three waves.";
        public const string TrophyUnbroken = "Unbroken Hall";
        public const string TrophyUnbrokenDesc = "Win without your hall taking damage.";
        public const string TrophyGiantKiller = "Giant Killer";
        public const string TrophyGiantKillerDesc = "Defeat the Goblin King.";
        // ---- END Trophy stand

        // ---- War Chronicle (WarChronicleTracker end-screen panel)
        public const string ChronicleTitle = "War Chronicle";
        public const string ChronicleGoldLegend = "Gold income (peak {0}/s)";
        public const string ChronicleTimberLegend = "Timber income (peak {0}/s)";
        public const string ChronicleKills = "Goblins slain: {0}";
        public const string ChronicleKnockedOff = "Hurled off the table: {0}";
        public const string ChronicleLadders = "Ladders toppled: {0}";
        public const string ChronicleBuilt = "Buildings raised: {0}";
        public const string ChronicleDamage = "Keep damage taken: {0}";
        public const string ChronicleEarned = "Earned: {0} gold, {1} timber";
        public const string ChronicleDuration = "Siege lasted {0}:{1}";
        // ---- end War Chronicle

        // ---- Warlords (WarlordAiPersonalityDirector) ----
        public const string WarlordSaboteurName = "Gryzzle the Saboteur";
        public const string WarlordBreakerName = "Krunk the Breaker";
        public const string WarlordShamanName = "Mogwa the Shaman";
        public const string WarlordArrives = "{0} leads the raid!";
        public const string WarlordSlain = "{0} has fallen!";
        public const string WarlordTotem = "Healing Totem";

        // ---- Harpooner (ClimbRoutes / HarpoonerUnit) ----
        public const string HarpoonerSpotted = "A goblin harpooner is taking aim at your table!";
        public const string HarpoonerName = "Goblin Harpooner";
        // ---- end Harpooner

        // ---- Start menu / Settings / Scroll end page ----
        public const string StartGame = "Start Game";
        public const string Settings = "Settings";
        public const string StartIntro = "Raise a hall on any surface of your room and hold it against the goblin horde.";
        public const string MenuHand = "Menu hand";
        public const string RightHanded = "Right-handed";
        public const string LeftHanded = "Left-handed";
        public const string MenuHandHint = "The building menu sits on the wrist of this hand. Your other hand places buildings and pokes buttons.";
        public const string EndlessSiege = "ENDLESS SIEGE";
        public const string SiegeFallen = "SIEGE FALLEN";
        public const string ScoreBest = "{0}  \u00b7  Best {1}";
        public const string WaveStats = "Wave Reached: {0}/{1}  \u00b7  Waves Survived: {2}";
        public const string EndlessWaveStats = "Endless Wave Reached: {0}  \u00b7  Waves Survived: {1}";
        // ---- end Start menu / Settings / Scroll end page ----

        public static string Format(string key, params object[] args) => string.Format(key, args);
    }
}
