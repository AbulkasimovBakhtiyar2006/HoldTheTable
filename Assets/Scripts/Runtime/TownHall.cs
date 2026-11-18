using System;
using TMPro;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Three-stage Imperial Age progression for the Town Hall.
    /// Age I: Wooden Fort (cap 12) → Age II: Stone Castle (cap 15) → Age III: Imperial Citadel (cap 18).
    /// </summary>
    public enum TownHallAge
    {
        AgeI = 1,
        AgeII = 2,
        AgeIII = 3,
        WoodenFort = 1,
        StoneCastle = 2,
        ImperialCitadel = 3
    }

    /// <summary>
    /// The Imperial House: the player's keep. Defeat when health dies (GameDirector subscribes to health.Died).
    /// Supports finger-poke Age Up progression across 3 Ages (Wooden Fort → Stone Castle → Imperial Citadel),
    /// expanding building cap (12 → 15 → 18), increasing Town Hall max HP, and adding procedural stone battlements,
    /// corner turrets, spire crown, and heraldic banners.
    /// </summary>
    public class TownHall : MonoBehaviour
    {
        public const float MaxHp = 20f;

        public const float Age1MaxHp = MaxHp;
        public const float Age2MaxHp = 35f;
        public const float Age3MaxHp = 55f;

        public const int Age1BuildingCap = 12;
        public const int Age2BuildingCap = 15;
        public const int Age3BuildingCap = 18;

        public const int Age2TimberCost = 35;
        public const int Age2GoldCost = 12;
        public const int Age3TimberCost = 60;
        public const int Age3GoldCost = 25;

        public const float PokeRadius = 0.115f;
        public const float HoverRadius = 0.19f;
        public const float PokeCooldown = 0.65f;

        public static TownHall Current { get; private set; }

        public Health health;
        public ArchitectureStyle style;
        public CivilizationDefinition civ;
        public Transform visual;
        public Transform ageOverlay;

        /// <summary>Current Age index (1 = Wooden Fort, 2 = Stone Castle, 3 = Imperial Citadel).</summary>
        public int Age { get; private set; } = 1;
        public TownHallAge CurrentAge => (TownHallAge)Age;
        public int BuildingCap => GetBuildingCap(Age);
        public int MaxBuildings => BuildingCap;
        public string AgeName => GetAgeName(Age);
        public bool IsMaxAge => Age >= 3;
        public bool CanAgeUp => Age < 3 && !ruined && (health == null || !health.IsDead);

        /// <summary>Static convenience query for current active building cap (defaults to 12 in Age I).</summary>
        public static int CurrentBuildingCap => Current != null ? Current.BuildingCap : Age1BuildingCap;
        public static int CurrentAgeLevel => Current != null ? Current.Age : 1;

        /// <summary>Raised when the Town Hall advances (or resets) its Age. Argument is the new Age (1..3).</summary>
        public event Action<int> AgeChanged;
        /// <summary>Raised when the Town Hall building cap changes (12, 15, or 18).</summary>
        public event Action<int> BuildingCapChanged;
        /// <summary>Static event raised whenever any TownHall changes Age.</summary>
        public static event Action<TownHall, int> AnyAgeChanged;

        public float FootprintRadius => BuildingVisuals.HallFootprintRadius;
        /// <summary>Monsters within this planar distance of the hall center may attack it.</summary>
        public float AttackReach => BuildingVisuals.HallFootprintRadius + 0.02f;
        public Vector3 Up => transform.up;

        float shake;
        bool ruined;
        float ruinT;
        float agePopT = 1f;
        float lastPokeTime = -10f;
        readonly bool[] wasInPokeZone = new bool[2];

        TextMeshPro floatingBanner;
        float bannerTimer;
        bool bannerIsToast;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Current = null;
            AnyAgeChanged = null;
        }

        public static int GetBuildingCap(int age)
        {
            if (age >= 3) return Age3BuildingCap;
            if (age == 2) return Age2BuildingCap;
            return Age1BuildingCap;
        }

        public static float GetMaxHpForAge(int age)
        {
            if (age >= 3) return Age3MaxHp;
            if (age == 2) return Age2MaxHp;
            return Age1MaxHp;
        }

        public static string GetAgeName(int age)
        {
            if (age >= 3) return "Age III: Imperial Citadel";
            if (age == 2) return "Age II: Stone Castle";
            return "Age I: Wooden Fort";
        }

        public static float GetScaleForAge(int age)
        {
            if (age >= 3) return 1.12f;
            if (age == 2) return 1.06f;
            return 1f;
        }

        /// <summary>Checks whether the defense count is within the current Age's building cap.</summary>
        public static bool IsWithinBuildingCap(int currentDefenseCount) => currentDefenseCount < CurrentBuildingCap;

        /// <summary>Outputs the Wood (Timber) and Gold cost to advance to the next Age. Returns false if already at Age III.</summary>
        public bool GetNextAgeCost(out int timberCost, out int goldCost)
        {
            if (Age == 1)
            {
                timberCost = Age2TimberCost;
                goldCost = Age2GoldCost;
                return true;
            }
            if (Age == 2)
            {
                timberCost = Age3TimberCost;
                goldCost = Age3GoldCost;
                return true;
            }
            timberCost = 0;
            goldCost = 0;
            return false;
        }

        public bool CanAffordNextAge()
        {
            if (!CanAgeUp) return false;
            if (!GetNextAgeCost(out int timber, out int gold)) return false;
            var gd = GameDirector.Instance;
            return gd != null && gd.CanAfford(timber, gold);
        }

        public void Init(CivilizationDefinition definition)
        {
            civ = definition;
            Age = 1;
            agePopT = 1f;
            BuildingVisuals.Resolve(definition, out style, out _);
            if (health == null) health = gameObject.AddComponent<Health>();
            health.Init(Age1MaxHp);
            health.Damaged -= OnDamaged;
            health.Died -= OnDied;
            health.Damaged += OnDamaged;
            health.Died += OnDied;
            if (visual != null) Destroy(visual.gameObject);
            visual = BuildingVisuals.BuildTownHall(transform, definition);
            RebuildAgeOverlay();
            EnsureFloatingBanner();
            ShowToast($"{AgeName} (Cap {BuildingCap})", new Color(1f, 0.92f, 0.72f), 2.5f);
            Current = this;
            BuildingUpgradeController.EnsureInstance();
        }

        void OnEnable() { if (health != null) Current = this; }

        void OnDestroy()
        {
            if (health != null) { health.Damaged -= OnDamaged; health.Died -= OnDied; }
            if (Current == this) Current = null;
        }

        void OnDamaged(Health h)
        {
            shake = 0.25f;
            if (VfxPool.Instance != null) VfxPool.Instance.Burst(transform.position + transform.up * 0.08f, new Color(0.9f, 0.4f, 0.2f));
            ProceduralAudio.Fire(Sfx.Thud, transform.position);
        }

        void OnDied(Health h)
        {
            ruined = true;
            ruinT = 0f;
            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Burst(transform.position + transform.up * 0.05f, new Color(0.35f, 0.33f, 0.3f));
                VfxPool.Instance.Burst(transform.position + transform.up * 0.12f, new Color(0.9f, 0.3f, 0.1f));
            }
        }

        /// <summary>Full HP, Age I reset, and intact visuals for RESTART (same place, same civilization).</summary>
        public void ResetForRestart()
        {
            ruined = false;
            ruinT = 0f;
            shake = 0f;
            agePopT = 1f;
            SetAge(1, true);
            if (health != null) health.ResetFull();
            if (visual != null)
            {
                visual.localPosition = Vector3.zero;
                visual.localRotation = Quaternion.identity;
                visual.localScale = Vector3.one;
            }
        }

        /// <summary>
        /// Spends Wood + Gold via GameDirector to advance the Town Hall to the next Age (I → II → III).
        /// Increases building cap (12 → 15 → 18), boosts max HP, and upgrades procedural visuals.
        /// </summary>
        public bool TryAgeUp()
        {
            if (ruined || (health != null && health.IsDead)) return false;
            var gd = GameDirector.Instance;
            if (gd != null && !gd.IsCombatPhase) return false;

            if (Age >= 3)
            {
                // At Age III, poking a damaged citadel can spend wood to repair it
                if (health != null && health.Current < health.Max - 0.1f && gd != null)
                {
                    int repairWood = Mathf.Clamp(Mathf.CeilToInt((health.Max - health.Current) * 0.5f), 2, 10);
                    if (gd.TrySpend(repairWood, 0))
                    {
                        float healed = Mathf.Min(health.Max - health.Current, repairWood * 2.5f);
                        health.Heal(healed);
                        if (VfxPool.Instance != null) VfxPool.Instance.Burst(transform.position + transform.up * 0.10f, new Color(0.35f, 0.95f, 0.45f));
                        ProceduralAudio.Fire(Sfx.Hammer, transform.position);
                        ShowToast($"Citadel Repaired +{Mathf.CeilToInt(healed)} HP (-{repairWood} Wood)", new Color(0.45f, 1f, 0.55f), 2.0f);
                        return true;
                    }
                }
                ProceduralAudio.Fire(Sfx.Click, transform.position);
                ShowToast("Max Age: Imperial Citadel (Cap 18)", new Color(1f, 0.88f, 0.4f), 1.8f);
                return false;
            }

            if (!GetNextAgeCost(out int timberCost, out int goldCost)) return false;
            if (gd != null && !gd.TrySpend(timberCost, goldCost))
            {
                ProceduralAudio.Fire(Sfx.Deny, transform.position);
                string nextTitle = GetAgeName(Age + 1);
                ShowToast($"{nextTitle}\nNeed {timberCost} Wood, {goldCost} Gold", new Color(1f, 0.52f, 0.45f), 2.2f);
                return false;
            }

            SetAge(Age + 1, true);
            agePopT = 0f;

            Vector3 pos = transform.position;
            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Ring(pos, FootprintRadius * 2.4f);
                VfxPool.Instance.Burst(pos + transform.up * 0.10f, new Color(1f, 0.84f, 0.28f));
                VfxPool.Instance.Burst(pos + transform.up * 0.16f, Age == 3 ? new Color(0.95f, 0.35f, 0.2f) : new Color(0.75f, 0.78f, 0.82f));
            }
            ProceduralAudio.Fire(Sfx.Horn, pos);
            ProceduralAudio.Fire(Sfx.Hammer, pos);

            ShowToast($"{AgeName}!\nBuilding Cap {BuildingCap} | Max HP {Mathf.RoundToInt(health != null ? health.Max : GetMaxHpForAge(Age))}",
                new Color(1f, 0.88f, 0.32f), 3.0f);
            Debug.Log($"[TownHall] Advanced to {AgeName} (Age {Age}, Cap={BuildingCap}, HP={(health != null ? health.Current : 0f):0.#}/{(health != null ? health.Max : 0f):0.#})");
            return true;
        }

        /// <summary>Directly sets the Town Hall Age (1..3), updating max HP, procedural visuals, and raising events.</summary>
        public void SetAge(int targetAge, bool healBonus = true)
        {
            int clamped = Mathf.Clamp(targetAge, 1, 3);
            int prevAge = Age;
            Age = clamped;

            if (health != null && !health.IsDead)
            {
                float prevBase = GetMaxHpForAge(prevAge);
                float nextBase = GetMaxHpForAge(Age);
                // Preserve any external civilization multiplier (e.g. Iron Dominion +35% structure HP)
                float extMul = prevBase > 0.01f ? Mathf.Max(1f, health.Max / prevBase) : 1f;
                float oldMax = health.Max;
                float newMax = nextBase * extMul;
                health.SetMaxKeepFraction(newMax);
                if (healBonus && newMax > oldMax)
                    health.Heal((newMax - oldMax) * 0.65f);
            }

            RebuildAgeOverlay();

            if (prevAge != Age)
            {
                AgeChanged?.Invoke(Age);
                BuildingCapChanged?.Invoke(BuildingCap);
                AnyAgeChanged?.Invoke(this, Age);
            }
        }

        /// <summary>Checks if a world-space finger position pokes the Town Hall, triggering TryAgeUp on contact.</summary>
        public bool TryPoke(Vector3 fingerWorldPos)
        {
            if (Time.time - lastPokeTime < PokeCooldown) return false;
            if (!IsPointInPokeVolume(fingerWorldPos, PokeRadius)) return false;
            lastPokeTime = Time.time;
            return TryAgeUp();
        }

        bool IsPointInPokeVolume(Vector3 worldPos, float radius)
        {
            Vector3 delta = worldPos - transform.position;
            float h = Vector3.Dot(delta, transform.up);
            if (h < -0.025f || h > 0.25f) return false;
            Vector3 planar = delta - transform.up * h;
            return planar.sqrMagnitude <= radius * radius;
        }

        void Update()
        {
            if (visual == null) return;
            float dt = Time.deltaTime;

            if (ruined)
            {
                // collapse: sink and tilt over 1.2 s, then stay as a ruin
                ruinT = Mathf.Min(1f, ruinT + dt / 1.2f);
                float e = ruinT * ruinT;
                visual.localPosition = new Vector3(0f, -0.05f * e, 0f);
                visual.localRotation = Quaternion.Euler(14f * e, 0f, 9f * e);
                visual.localScale = new Vector3(1f, 1f - 0.45f * e, 1f);
                if (floatingBanner != null && floatingBanner.gameObject.activeSelf)
                    floatingBanner.gameObject.SetActive(false);
                return;
            }

            float targetScale = GetScaleForAge(Age);
            if (agePopT < 1f)
            {
                agePopT = Mathf.Min(1f, agePopT + dt / 0.35f);
                float pulse = 1f + Mathf.Sin(agePopT * Mathf.PI) * 0.22f;
                visual.localScale = Vector3.one * (targetScale * pulse);
            }
            else
            {
                visual.localScale = Vector3.one * targetScale;
            }

            if (shake > 0f)
            {
                shake -= dt;
                float a = Mathf.Max(0f, shake) * 4f;
                visual.localPosition = new Vector3(Mathf.Sin(Time.time * 90f) * 0.003f * a, 0f, Mathf.Cos(Time.time * 77f) * 0.003f * a);
                if (shake <= 0f) visual.localPosition = Vector3.zero;
            }

            PollHandInteraction(dt);
        }

        void PollHandInteraction(float dt)
        {
            var gd = GameDirector.Instance;
            if (gd != null && !gd.IsCombatPhase)
            {
                if (floatingBanner != null && floatingBanner.gameObject.activeSelf)
                    floatingBanner.gameObject.SetActive(false);
                return;
            }

            bool fingerHovering = false;
            var rig = HandRig.Instance;
            if (rig != null)
            {
                for (int h = 0; h < 2; h++)
                {
                    var side = (HandSide)h;
                    if (!rig.IsTracked(side) || !rig.TryGetIndexTip(side, out Vector3 tip))
                    {
                        wasInPokeZone[h] = false;
                        continue;
                    }
                    // Pinching near the hall is used by BuildingRotator; poke uses unpinched index tip
                    if (rig.IsPinching(side))
                    {
                        wasInPokeZone[h] = false;
                        continue;
                    }

                    if (IsPointInPokeVolume(tip, HoverRadius))
                        fingerHovering = true;

                    bool inside = IsPointInPokeVolume(tip, PokeRadius);
                    if (inside && !wasInPokeZone[h] && Time.time - lastPokeTime >= PokeCooldown)
                    {
                        lastPokeTime = Time.time;
                        TryAgeUp();
                    }
                    else if (!inside && !IsPointInPokeVolume(tip, PokeRadius * 1.12f))
                    {
                        wasInPokeZone[h] = false;
                        continue;
                    }
                    if (inside) wasInPokeZone[h] = true;
                }
            }

            UpdateFloatingBanner(dt, fingerHovering);
        }

        void EnsureFloatingBanner()
        {
            if (floatingBanner != null) return;
            var go = new GameObject("TownHallAgeBanner");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 0.255f, 0f);
            floatingBanner = go.AddComponent<TextMeshPro>();
            floatingBanner.fontSize = 0.17f;
            floatingBanner.alignment = TextAlignmentOptions.Center;
            floatingBanner.rectTransform.sizeDelta = new Vector2(0.42f, 0.10f);
            floatingBanner.textWrappingMode = TextWrappingModes.NoWrap;
            floatingBanner.color = new Color(1f, 0.92f, 0.72f);
            go.SetActive(false);
        }

        public void ShowToast(string message, Color color, float duration = 2.2f)
        {
            EnsureFloatingBanner();
            if (floatingBanner == null) return;
            floatingBanner.text = message;
            floatingBanner.color = color;
            bannerTimer = duration;
            bannerIsToast = true;
            if (!floatingBanner.gameObject.activeSelf) floatingBanner.gameObject.SetActive(true);
        }

        void UpdateFloatingBanner(float dt, bool fingerHovering)
        {
            if (floatingBanner == null) return;

            if (bannerTimer > 0f)
            {
                bannerTimer -= dt;
                if (bannerTimer <= 0f) bannerIsToast = false;
            }

            if (!bannerIsToast)
            {
                if (fingerHovering)
                {
                    if (GetNextAgeCost(out int wCost, out int gCost))
                    {
                        bool afford = CanAffordNextAge();
                        floatingBanner.text = $"{AgeName} (Cap {BuildingCap})\nPoke → {GetAgeName(Age + 1)} ({wCost}W {gCost}G)";
                        floatingBanner.color = afford ? new Color(0.55f, 1f, 0.6f) : new Color(1f, 0.78f, 0.45f);
                    }
                    else
                    {
                        floatingBanner.text = $"{AgeName} (Cap {BuildingCap})";
                        floatingBanner.color = new Color(1f, 0.88f, 0.35f);
                    }
                    if (!floatingBanner.gameObject.activeSelf) floatingBanner.gameObject.SetActive(true);
                }
                else if (floatingBanner.gameObject.activeSelf)
                {
                    floatingBanner.gameObject.SetActive(false);
                }
            }

            if (floatingBanner.gameObject.activeSelf)
            {
                float topY = Age >= 3 ? 0.285f : (Age == 2 ? 0.26f : 0.24f);
                floatingBanner.transform.localPosition = new Vector3(0f, topY, 0f);
                var cam = Camera.main;
                if (cam != null)
                {
                    Vector3 look = floatingBanner.transform.position - cam.transform.position;
                    look.y = 0f;
                    if (look.sqrMagnitude > 1e-6f)
                        floatingBanner.transform.rotation = Quaternion.LookRotation(look.normalized, transform.up);
                }
            }
        }

        // ------------------------------------------------------------------ procedural Age visuals

        void RebuildAgeOverlay()
        {
            if (visual == null) return;
            if (ageOverlay != null) Destroy(ageOverlay.gameObject);
            ageOverlay = BuildingVisuals.NewRoot(visual, "AgeOverlay_Age" + Age);
            var k = new BuildingVisuals.Kit(ageOverlay);
            BuildingVisuals.Resolve(civ, out var resolvedStyle, out var p);

            if (Age == 1)
            {
                BuildAge1WoodenFortAccents(k, p);
            }
            else if (Age == 2)
            {
                BuildAge2StoneCastle(k, resolvedStyle, p);
            }
            else if (Age >= 3)
            {
                BuildAge2StoneCastle(k, resolvedStyle, p);
                BuildAge3ImperialCitadel(k, resolvedStyle, p);
            }
        }

        /// <summary>Age I: Wooden Fort perimeter corner stakes & timber crossbars.</summary>
        static void BuildAge1WoodenFortAccents(BuildingVisuals.Kit k, BuildingVisuals.Palette p)
        {
            Color timber = new Color(0.46f, 0.29f, 0.14f);
            Color darkTimber = new Color(0.29f, 0.18f, 0.08f);
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 corner = new Vector3(sx * 0.080f, 0.004f, sz * 0.080f);
                    k.Cyl(corner, 0.0065f, 0.048f, darkTimber);
                    k.Cone(corner + Vector3.up * 0.048f, 0.007f, 0.014f, timber);
                }
            }
        }

        /// <summary>Age II: Stone Castle outer curtain wall, crenellated battlements, stone corner bastions, and gatehouse arch.</summary>
        static void BuildAge2StoneCastle(BuildingVisuals.Kit k, ArchitectureStyle s, BuildingVisuals.Palette p)
        {
            Color masonry = Color.Lerp(p.stone, new Color(0.58f, 0.59f, 0.63f), 0.55f);
            Color darkStone = masonry * 0.72f; darkStone.a = 1f;
            const float half = 0.083f;
            const float wallH = 0.056f;
            const float wallT = 0.011f;

            // Stone foundation plinth collar
            k.Box(new Vector3(0f, 0.004f, 0f), new Vector3(0.184f, 0.008f, 0.184f), darkStone);

            // Back (-Z), Left (-X), Right (+X) stone curtain walls + merlons
            k.Box(new Vector3(0f, wallH * 0.5f, -half), new Vector3(half * 2f, wallH, wallT), masonry);
            k.Box(new Vector3(-half, wallH * 0.5f, 0f), new Vector3(wallT, wallH, half * 2f), masonry);
            k.Box(new Vector3(half, wallH * 0.5f, 0f), new Vector3(wallT, wallH, half * 2f), masonry);

            // Front (+Z) stone wall split around a fortified gatehouse portal
            k.Box(new Vector3(-0.052f, wallH * 0.5f, half), new Vector3(0.062f, wallH, wallT), masonry);
            k.Box(new Vector3(0.052f, wallH * 0.5f, half), new Vector3(0.062f, wallH, wallT), masonry);
            // Gatehouse lintel + twin flanking pillars
            k.Box(new Vector3(-0.022f, 0.036f, half + 0.004f), new Vector3(0.012f, 0.072f, 0.016f), darkStone);
            k.Box(new Vector3(0.022f, 0.036f, half + 0.004f), new Vector3(0.012f, 0.072f, 0.016f), darkStone);
            k.Box(new Vector3(0f, 0.066f, half + 0.004f), new Vector3(0.058f, 0.014f, 0.018f), masonry);
            k.Box(new Vector3(0f, 0.074f, half + 0.004f), new Vector3(0.060f, 0.004f, 0.020f), p.trim);

            // Battlement merlons along all 4 curtain walls
            for (int side = 0; side < 4; side++)
            {
                Quaternion q = Quaternion.Euler(0f, side * 90f, 0f);
                for (int i = -2; i <= 2; i++)
                {
                    if (side == 0 && i == 0) continue; // leave space above gate arch center
                    Vector3 mp = q * new Vector3(i * 0.028f, wallH + 0.006f, half);
                    k.Box(mp, new Vector3(0.015f, 0.012f, wallT + 0.003f), masonry, new Vector3(0f, side * 90f, 0f));
                }
            }

            // 4 Stone corner bastion towers with battlement rims
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 b = new Vector3(sx * half, 0.004f, sz * half);
                    k.Cyl(b, 0.019f, 0.095f, masonry);
                    k.Cyl(b + Vector3.up * 0.093f, 0.022f, 0.008f, darkStone);
                    k.Cyl(b + Vector3.up * 0.101f, 0.021f, 0.004f, p.trim);
                    if (s == ArchitectureStyle.TieredRoofs)
                        k.PagodaRoof(b + Vector3.up * 0.105f, 0.023f, 0.024f, p.roof);
                    else
                        k.Cone(b + Vector3.up * 0.105f, 0.022f, 0.028f, p.roof);
                }
            }
        }

        /// <summary>Age III: Imperial Citadel tall corner turrets with mounted siege barrels, grand central spire crown, and 4 royal banners.</summary>
        static void BuildAge3ImperialCitadel(BuildingVisuals.Kit k, ArchitectureStyle s, BuildingVisuals.Palette p)
        {
            Color gold = new Color(0.96f, 0.78f, 0.24f);
            Color citadelStone = Color.Lerp(p.main, Color.white, 0.25f);
            Color iron = new Color(0.24f, 0.25f, 0.29f);
            const float half = 0.083f;

            // Golden Imperial trim band around the curtain wall
            for (int side = 0; side < 4; side++)
            {
                Quaternion q = Quaternion.Euler(0f, side * 90f, 0f);
                k.Box(q * new Vector3(0f, 0.048f, half + 0.006f), new Vector3(half * 1.85f, 0.004f, 0.003f), gold, new Vector3(0f, side * 90f, 0f));
            }

            // Elevate all 4 corner bastions into tall Imperial Turrets with outward-pointing cannon/ballista barrels
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 b = new Vector3(sx * half, 0.105f, sz * half);
                    k.Cyl(b, 0.016f, 0.048f, citadelStone);
                    k.Cyl(b + Vector3.up * 0.046f, 0.019f, 0.006f, gold);
                    if (s == ArchitectureStyle.TieredRoofs)
                        k.PagodaRoof(b + Vector3.up * 0.052f, 0.024f, 0.028f, p.roof);
                    else
                        k.Cone(b + Vector3.up * 0.052f, 0.022f, 0.035f, p.roof);
                    k.Ball(b + Vector3.up * 0.088f, 0.008f, gold);

                    // Outward-facing citadel defense turret barrel on each corner tower
                    Vector3 outDir = new Vector3(sx, 0f, sz).normalized;
                    float yaw = Mathf.Atan2(outDir.x, outDir.z) * Mathf.Rad2Deg;
                    k.CylRot(b + Vector3.up * 0.032f + outDir * 0.018f, 0.0035f, 0.022f, iron, new Vector3(80f, yaw, 0f));
                }
            }

            // Grand central Imperial Citadel spire extension & glowing beacon orb
            Vector3 spireBase = new Vector3(0f, 0.185f, 0f);
            k.Cyl(spireBase, 0.015f, 0.038f, citadelStone);
            k.Cyl(spireBase + Vector3.up * 0.036f, 0.018f, 0.005f, gold);
            k.Cone(spireBase + Vector3.up * 0.041f, 0.017f, 0.030f, p.roof);
            k.Ball(spireBase + Vector3.up * 0.074f, 0.013f, gold);

            // 4 Royal Imperial Banners flanking the gatehouse and upper keep
            BuildImperialBanner(k, new Vector3(-0.032f, 0.075f, half + 0.008f), 0.065f, p.banner, gold);
            BuildImperialBanner(k, new Vector3(0.032f, 0.075f, half + 0.008f), 0.065f, p.banner, gold);
            BuildImperialBanner(k, new Vector3(-0.045f, 0.135f, 0f), 0.055f, p.banner, gold);
            BuildImperialBanner(k, new Vector3(0.045f, 0.135f, 0f), 0.055f, p.banner, gold);
        }

        static void BuildImperialBanner(BuildingVisuals.Kit k, Vector3 basePos, float poleH, Color bannerCol, Color gold)
        {
            k.Cyl(basePos, 0.0018f, poleH, gold);
            k.Ball(basePos + Vector3.up * (poleH + 0.003f), 0.0055f, gold);
            // Crossbar + vertical hanging royal banner cloth with gold trim
            Vector3 top = basePos + Vector3.up * (poleH - 0.006f);
            k.Box(top, new Vector3(0.020f, 0.0022f, 0.0022f), gold);
            k.Box(top + new Vector3(0f, -0.016f, 0.002f), new Vector3(0.016f, 0.028f, 0.0018f), bannerCol);
            k.Box(top + new Vector3(0f, -0.031f, 0.002f), new Vector3(0.016f, 0.003f, 0.002f), gold);
        }
    }
}
