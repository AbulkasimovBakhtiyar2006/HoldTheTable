using System;
using UnityEngine;

namespace HoldTheTable
{
    public enum GamePhase { CivilizationSetup, HeadquartersPlacement, BuildPhase, Wave, Upgrade, Victory, Defeat }

    /// <summary>
    /// Session state machine: CivilizationSetup → HeadquartersPlacement → BuildPhase → Wave → Upgrade/BuildPhase → Victory/Defeat.
    /// No waves, timber income or countdown before the Town Hall is placed. All timing runs in Update (no coroutines). Owner: Agent C.
    /// </summary>
    public class GameDirector : MonoBehaviour
    {
        public const float StartTimber = 40f;
        public const float TimberPerSecond = 6f;
        public const float StartGold = 10f;
        /// <summary>Economy income sums are recomputed at most this often (seconds).</summary>
        const float RateRefreshInterval = 0.25f;
        public const float BuildDuration = 12f;
        public const float BannerSideOffset = 0.205f; // was 0.13 (+7.5 cm)
        public const int WaveCount = 3;
        /// <summary>Safety net if the Quartermaster never reports back.</summary>
        const float UpgradeSafetyTimeout = QuartermasterAgent.AutoSkipSeconds + 4f;

        public static GameDirector Instance { get; private set; }

        public GamePhase Phase { get; private set; } = GamePhase.CivilizationSetup;
        public float Timber { get; private set; }
        public float Gold { get; private set; }
        /// <summary>Current timber income per second: base 6 + Σ farms × Harvest multiplier.</summary>
        public void AddTimber(float amount) { if (amount > 0f) Timber += amount; }

        public float TimberRate { get; private set; } = TimberPerSecond;
        /// <summary>Current gold income per second: Σ mines × Harvest multiplier.</summary>
        public float GoldRate { get; private set; }
        public int WaveIndex { get; private set; }
        public float Countdown { get; private set; }
        public TownHall Hall { get; private set; }
        public ITableSurface Surface { get; private set; }
        public BannerRenderer Banner { get; private set; }
        public bool IsEndlessMode { get; private set; }
        public int MaxBuildings => TownHall.CurrentBuildingCap;
        public int Charges => ShockwaveAbility.Instance != null ? ShockwaveAbility.Instance.Charges : 0;
        public bool IsCombatPhase => Phase == GamePhase.BuildPhase || Phase == GamePhase.Wave || Phase == GamePhase.Upgrade;
        public int LastScore { get; private set; }

        public event Action<GamePhase> PhaseChanged;
        public event Action GameReset;
        public event Action<int> WaveStarted;
        /// <summary>Raised when gold is spent/reset and whenever its whole-number value changes through income.</summary>
        public event Action<float> GoldChanged;

        Health hallHealth;   // the Health we are subscribed to (exactly one subscription)
        float upgradeTimer;
        float rateTimer;
        int lastGoldWhole = int.MinValue;
        bool profileSubscribed;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            if (CivilizationProfile.Current == null) ProfileStore.Load();
        }

        void OnEnable()
        {
            if (!profileSubscribed) { CivilizationProfile.Changed += OnProfileChanged; profileSubscribed = true; }
            EndScreen.EndlessSiegeRequested += OnEndlessSiegeRequested;
            BuildingUpgradeController.ResourceRefundRequested += OnResourceRefundRequested;
        }

        void OnDisable()
        {
            if (profileSubscribed) { CivilizationProfile.Changed -= OnProfileChanged; profileSubscribed = false; }
            EndScreen.EndlessSiegeRequested -= OnEndlessSiegeRequested;
            BuildingUpgradeController.ResourceRefundRequested -= OnResourceRefundRequested;
        }

        void OnDestroy()
        {
            UnsubscribeHall();
            if (Instance == this) Instance = null;
        }

        void Start()
        {
            SetPhase(GamePhase.CivilizationSetup);
            HandOfGodController.Ensure();
            BuildingUpgradeController.EnsureInstance();
            LivingSettlementCoordinator.EnsureInstance();
            if (ImperialScroll.Instance != null) ImperialScroll.Instance.Open();
            else Debug.LogWarning("[GameDirector] ImperialScroll missing in scene");
        }

        // ---------------- public API ----------------

        /// <summary>Timber-only spend (legacy).</summary>
        public bool TrySpend(int cost) => TrySpend(cost, 0);

        public bool CanAfford(int timber, int gold)
        {
            if (!IsCombatPhase) return false;
            if (timber < 0 || gold < 0) return false;
            return Timber >= timber && Gold >= gold;
        }

        /// <summary>Atomically spends timber and gold; nothing is spent when either is insufficient.</summary>
        public bool TrySpend(int timber, int gold)
        {
            if (!CanAfford(timber, gold)) return false;
            Timber -= timber;
            if (gold > 0)
            {
                Gold -= gold;
                RaiseGoldChanged(true);
            }
            return true;
        }

        /// <summary>Scroll "Found Empire": persist the profile and hand out the Town Hall token.</summary>
        public void OnProfileConfirmed()
        {
            var p = CivilizationProfile.Current;
            p.empireName = CivilizationProfile.SanitizeName(p.empireName, p.Definition);
            ProfileStore.Save();
            CivilizationProfile.NotifyChanged();
            if (Phase == GamePhase.CivilizationSetup) SetPhase(GamePhase.HeadquartersPlacement);
        }

        /// <summary>Called once by EmpireFoundationController after the player confirmed a valid hall position.</summary>
        public void OnHeadquartersPlaced(TownHall hall, ITableSurface surface)
        {
            if (hall == null) { Debug.LogError("[GameDirector] OnHeadquartersPlaced with null hall"); return; }
            if (Phase != GamePhase.HeadquartersPlacement && Phase != GamePhase.CivilizationSetup)
            {
                Debug.LogWarning("[GameDirector] Ignoring duplicate OnHeadquartersPlaced in phase " + Phase);
                return;
            }
            Hall = hall;
            Surface = surface;
            CivilizationPassives.ApplyToTownHall(hall);
            SubscribeHall(hall.health);

            Vector3 hallPos = hall.transform.position;
            if (SettlementBuildArea.Instance != null) SettlementBuildArea.Instance.Found(hallPos, surface);
            SpawnBannerOnce(hallPos);

            ResetSession();
            ResourceOutpostNode.SpawnForSession(hallPos, surface);
            BuildingUpgradeController.EnsureInstance();
            if (VfxPool.Instance != null) VfxPool.Instance.Ring(hallPos, 0.2f);
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Place, hallPos);
            Debug.Log($"[GameDirector] Empire '{CivilizationProfile.Current.DisplayName}' founded at {hallPos} on surface {(surface != null ? surface.Id : "null")}");
            EnterBuildPhase();
        }

        /// <summary>RESTART: new combat session; keeps profile, hall position and banner.</summary>
        public void Restart()
        {
            if (Hall == null)
            {
                Debug.LogWarning("[GameDirector] Restart without a Town Hall → New Empire flow");
                NewEmpire();
                return;
            }
            ResetSession();
            Hall.ResetForRestart();
            CivilizationPassives.ApplyToTownHall(Hall);
            if (SettlementBuildArea.Instance != null) SettlementBuildArea.Instance.ResetKeepHall();
            ResourceOutpostNode.SpawnForSession(Hall.transform.position, Surface);
            GameReset?.Invoke();
            EnterBuildPhase();
        }

        /// <summary>Scroll "New Empire": stop the session, remove its objects and go back to civilization setup.</summary>
        public void NewEmpire()
        {
            StopSessionObjects();
            UnsubscribeHall();
            CivilizationPassives.ResetStatics();
            IsEndlessMode = false;
            if (BuildingFactory.Instance != null) BuildingFactory.Instance.DestroyTownHall();
            Hall = null;
            Surface = null;
            if (Banner != null) Destroy(Banner.gameObject);
            Banner = null;
            if (SettlementBuildArea.Instance != null) SettlementBuildArea.Instance.Clear();
            if (UpgradeState.Instance != null) UpgradeState.Instance.ResetAll();
            if (ScoreKeeper.Instance != null) ScoreKeeper.Instance.ResetScore();
            if (ShockwaveAbility.Instance != null) ShockwaveAbility.Instance.ResetCharges();
            Timber = 0f;
            Gold = 0f;
            TimberRate = TimberPerSecond;
            GoldRate = 0f;
            RaiseGoldChanged(true);
            WaveIndex = 0;
            Countdown = 0f;
            GameReset?.Invoke();
            SetPhase(GamePhase.CivilizationSetup);
            if (ImperialScroll.Instance != null) ImperialScroll.Instance.Open();
        }

        public void SkipCountdown()
        {
            if (Phase == GamePhase.BuildPhase) Countdown = 0f;
        }

        // ---------------- state machine ----------------

        void Update()
        {
            float dt = Time.deltaTime;
            switch (Phase)
            {
                case GamePhase.BuildPhase:
                    CivilizationPassives.TickBetweenWaves(dt);
                    AccrueIncome(dt);
                    Countdown -= dt;
                    if (Countdown <= 0f) StartWave();
                    break;
                case GamePhase.Wave:
                    AccrueIncome(dt);
                    var ws = WaveSpawner.Instance;
                    if (ws != null && ws.WaveCleared) OnWaveCleared();
                    break;
                case GamePhase.Upgrade:
                    CivilizationPassives.TickBetweenWaves(dt);
                    upgradeTimer -= dt;
                    if (upgradeTimer <= 0f)
                    {
                        if (QuartermasterAgent.Instance != null) QuartermasterAgent.Instance.Cancel();
                        OnUpgradeFinished();
                    }
                    break;
            }
        }

        void AccrueIncome(float dt)
        {
            rateTimer -= dt;
            if (rateTimer <= 0f)
            {
                rateTimer = RateRefreshInterval;
                RecomputeRates();
            }
            Timber += TimberRate * dt;
            if (GoldRate > 0f)
            {
                Gold += GoldRate * dt;
                RaiseGoldChanged(false);
            }
        }

        /// <summary>Σ economy output from live placed units (no allocations; called ≤ 4×/s).</summary>
        public void RecomputeRates()
        {
            float farm = 0f, mine = 0f;
            var units = PlacedUnit.All;
            var civ = CivilizationProfile.Current != null ? CivilizationProfile.Current.Definition : null;
            float farmCivMul = CivilizationPassives.GetFarmTimberMultiplier(civ);
            float mineCivMul = CivilizationPassives.GetMineGoldMultiplier(civ);
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null) continue;
                if (u.health != null && u.health.IsDead) continue;
                var d = UnitDef.Get(u.type);
                if (d == null || !d.isEconomy) continue;
                if (u.type == UnitType.Farm) farm += d.timberPerSec * farmCivMul;
                else if (u.type == UnitType.Mine) mine += d.goldPerSec * mineCivMul * ResourceOutpostNode.GetGoldMultiplier(u);
            }
            mine += ResourceOutpostNode.GetUnboundClaimedGoldPerSec();
            float mul = UpgradeState.Instance != null ? UpgradeState.Instance.EconomyMul : 1f;
            TimberRate = TimberPerSecond + farm * mul;
            GoldRate = mine * mul;
        }

        void RaiseGoldChanged(bool force)
        {
            int whole = Mathf.FloorToInt(Gold);
            if (!force && whole == lastGoldWhole) return;
            lastGoldWhole = whole;
            GoldChanged?.Invoke(Gold);
        }

        void EnterBuildPhase()
        {
            Countdown = BuildDuration;
            SetPhase(GamePhase.BuildPhase);
        }

        void StartWave()
        {
            Countdown = 0f;
            SetPhase(GamePhase.Wave);
            if (Hall != null)
            {
                Vector3 hallPos = Hall.transform.position;
                if (WaveSpawner.Instance != null)
                    WaveSpawner.Instance.BeginWave(WaveIndex, hallPos, Surface);
                GoblinKingBoss.SpawnWaveBossAndEndlessScaling(WaveIndex, hallPos, Surface);
                FlyingThreatUnit.SpawnForWave(WaveIndex);
                SiegeCatapultUnit.SpawnForWave(WaveIndex);
            }
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.WaveStart);
            ProceduralAudio.Fire(Sfx.Horn);
            WaveStarted?.Invoke(WaveIndex);
        }

        void OnWaveCleared()
        {
            if (WaveSpawner.Instance != null) WaveSpawner.Instance.StopWave();
            CivilizationPassives.OnWaveCleared();
            if (ShockwaveAbility.Instance != null) ShockwaveAbility.Instance.OnWaveCleared();
            if (!IsEndlessMode && WaveIndex >= WaveCount - 1) { EndGame(true); return; }
            upgradeTimer = UpgradeSafetyTimeout;
            SetPhase(GamePhase.Upgrade);
            if (QuartermasterAgent.Instance != null) QuartermasterAgent.Instance.Begin(OnUpgradeFinished);
            else OnUpgradeFinished();
        }

        void OnUpgradeFinished()
        {
            if (Phase != GamePhase.Upgrade) return;
            WaveIndex = IsEndlessMode ? WaveIndex + 1 : Mathf.Min(WaveIndex + 1, WaveCount - 1);
            EnterBuildPhase();
        }

        public void OnEndlessSiegeRequested()
        {
            if (Hall == null || (Hall.health != null && Hall.health.IsDead)) return;
            IsEndlessMode = true;
            WaveIndex++;
            upgradeTimer = UpgradeSafetyTimeout;
            SetPhase(GamePhase.Upgrade);
            if (EndScreen.Instance != null) EndScreen.Instance.Hide();
            if (QuartermasterAgent.Instance != null) QuartermasterAgent.Instance.Begin(OnUpgradeFinished);
            else OnUpgradeFinished();
        }

        void OnResourceRefundRequested(int timberRefund, int goldRefund)
        {
            if (timberRefund > 0) Timber += timberRefund;
            if (goldRefund > 0) { Gold += goldRefund; RaiseGoldChanged(true); }
        }

        void OnHallDied(Health h)
        {
            if (Phase == GamePhase.BuildPhase || Phase == GamePhase.Wave || Phase == GamePhase.Upgrade) EndGame(false);
        }

        void EndGame(bool victory)
        {
            if (Phase == GamePhase.Victory || Phase == GamePhase.Defeat) return;
            if (WaveSpawner.Instance != null) WaveSpawner.Instance.StopWave();
            if (QuartermasterAgent.Instance != null) QuartermasterAgent.Instance.Cancel();
            LastScore = ScoreKeeper.Instance != null ? ScoreKeeper.Instance.Compute() : 0;
            SetPhase(victory ? GamePhase.Victory : GamePhase.Defeat);
            if (!victory) MonsterUnit.DespawnAll();
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(victory ? Sfx.Victory : Sfx.Defeat);
            if (EndScreen.Instance != null) EndScreen.Instance.Show(victory, LastScore, WaveIndex, IsEndlessMode);
            Debug.Log($"[GameDirector] {(victory ? "VICTORY" : "DEFEAT")} score={LastScore}");
        }

        /// <summary>Combat-session reset shared by founding and restart (no coroutines, no subscriptions added).</summary>
        void ResetSession()
        {
            StopSessionObjects();
            Timber = StartTimber;
            Gold = StartGold;
            TimberRate = TimberPerSecond;
            GoldRate = 0f;
            rateTimer = 0f;
            RaiseGoldChanged(true);
            WaveIndex = 0;
            Countdown = 0f;
            LastScore = 0;
            IsEndlessMode = false;
            if (UpgradeState.Instance != null) UpgradeState.Instance.ResetAll();
            if (ScoreKeeper.Instance != null) ScoreKeeper.Instance.ResetScore();
            if (ShockwaveAbility.Instance != null) ShockwaveAbility.Instance.ResetCharges();
        }

        void StopSessionObjects()
        {
            if (WaveSpawner.Instance != null) WaveSpawner.Instance.StopWave();
            if (QuartermasterAgent.Instance != null) QuartermasterAgent.Instance.Cancel();
            MonsterUnit.DespawnAll();
            ResourceOutpostNode.ClearAll();
            if (HandOfGodController.Existing != null) HandOfGodController.Existing.ResetState();
            GoblinKingBoss.DespawnAll();
            FlyingThreatUnit.DespawnAll();
            SiegeCatapultUnit.DespawnAll();
            if (BuildingFactory.Instance != null) BuildingFactory.Instance.ClearUnits();
            if (EndScreen.Instance != null) EndScreen.Instance.Hide();
            if (UpgradeChoice.Instance != null) UpgradeChoice.Instance.Hide();
        }

        void SetPhase(GamePhase p)
        {
            Phase = p;
            Debug.Log("[GameDirector] Phase → " + p);
            PhaseChanged?.Invoke(p);
        }

        // ---------------- hall + banner ----------------

        void SubscribeHall(Health h)
        {
            if (hallHealth == h) return;
            UnsubscribeHall();
            hallHealth = h;
            if (hallHealth != null) hallHealth.Died += OnHallDied;
        }

        void UnsubscribeHall()
        {
            if (hallHealth != null) hallHealth.Died -= OnHallDied;
            hallHealth = null;
        }

        void SpawnBannerOnce(Vector3 hallPos)
        {
            Vector3 up = Surface != null ? Surface.Pose.up : Vector3.up;
            var cam = Camera.main;
            Vector3 toCam = cam != null ? Vector3.ProjectOnPlane(cam.transform.position - hallPos, up) : Vector3.back;
            if (toCam.sqrMagnitude < 1e-4f) toCam = Vector3.back;
            toCam.Normalize();
            Vector3 side = Vector3.Cross(up, toCam).normalized;
            Vector3 basePos = hallPos + side * BannerSideOffset;
            if (Surface != null)
            {
                basePos = Surface.ProjectOnto(basePos);
                if (!Surface.IsPointSupported(basePos))
                {
                    Vector3 other = Surface.ProjectOnto(hallPos - side * BannerSideOffset);
                    basePos = Surface.IsPointSupported(other) ? other : Surface.ProjectOnto(hallPos + toCam * BannerSideOffset);
                }
            }

            if (Banner == null) Banner = BannerRenderer.Spawn(basePos, up, toCam);
            else
            {
                Banner.transform.position = basePos;
                Banner.transform.rotation = Quaternion.LookRotation(toCam, up);
            }
            if (Banner != null) Banner.Apply(CivilizationProfile.Current);
        }

        void OnProfileChanged()
        {
            if (Banner != null) Banner.Apply(CivilizationProfile.Current);
        }
    }
}
