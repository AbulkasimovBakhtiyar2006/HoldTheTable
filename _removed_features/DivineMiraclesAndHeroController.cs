using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Divine miracles + champion. Self-bootstrapping (RuntimeInitializeOnLoadMethod) or via
    /// <see cref="EnsureInstance"/> / AddComponent. No Find/LINQ in Update.
    ///  1) Hero champion figurine: pinch-grab (index tip within 6 cm, pinch), drop on the table -> Heroic Slam AoE
    ///     (damage + push, ring/burst VFX, thud). 15 s cooldown, returns to its plinth beside the hall.
    ///  2) Meteor Rain: both palms horizontal (facing up or down axis vertical), above the table, held 0.6 s -> 8 meteors
    ///     fall over the palm centre. 2 charges, 4 s cooldown, charges reset on game reset.
    ///  3) Protective Dome: wrists crossed (left wrist right of right wrist in head space, &lt; 0.2 m apart) above the
    ///     hall, held 0.4 s -> translucent dome 8 s; all damage to the hall's Health is healed back while active.
    /// </summary>
    public sealed class DivineMiraclesAndHeroController : MonoBehaviour
    {
        public static DivineMiraclesAndHeroController Instance { get; private set; }

        // ---- tunables ----
        public float heroCooldown = 15f, slamRadius = 0.5f, slamDamage = 8f, slamPush = 0.2f, grabRadius = 0.06f, maxDropHeight = 0.1f;
        public int meteorBaseCharges = 2;
        public float meteorCooldown = 4f, meteorHold = 0.6f, meteorRadius = 0.35f, meteorImpactRadius = 0.12f, meteorDamage = 3f, meteorPush = 0.08f, meteorFallTime = 0.8f, meteorDropHeight = 0.6f;
        public float domeCooldown = 25f, domeDuration = 8f, domeHold = 0.4f, domeRadius = 0.22f, crossMaxDistance = 0.2f;
        public float palmFlatDot = 0.8f, minPalmHeight = 0.12f;

        // ---- public state ----
        /// <summary>True while the dome absorbs damage to the town hall.</summary>
        public static bool DomeActive { get; private set; }
        /// <summary>Damage multiplier applied to the hall while the dome is up (0 = fully absorbed).</summary>
        public static float DomeDamageMultiplier => DomeActive ? 0f : 1f;
        public bool HeroGrabbed => grabbed;
        public float HeroCooldownRemaining => Mathf.Max(0f, heroReadyAt - Time.time);
        public int MeteorCharges => Mathf.Max(0, meteorBaseCharges + (UpgradeState.Instance != null ? UpgradeState.Instance.ExtraCharges : 0) - meteorsUsed);
        public float DomeRemaining => Mathf.Max(0f, domeUntil - Time.time);

        GameDirector gd;
        bool subscribed;

        // hero
        Transform hero, heroBody;
        Renderer[] heroRenderers;
        HRole[] heroRoles;
        int heroPlinthCount;
        Material heroDimMat;
        Material[] roleMats;
        Transform capeT, weaponT, torsoT, headT;
        string builtCiv;
        float slamAt = -10f;
        Color glowBase = new Color(1f, 0.85f, 0.4f);
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly List<Renderer> rBuild = new List<Renderer>(64);
        static readonly List<HRole> roleBuild = new List<HRole>(64);
        enum HRole { Banner, Accent, Steel, Dark, Prim, Sec, Glow, Skin, Wood, Jade, Bone, Count }
        bool grabbed; HandSide grabSide;
        float heroReadyAt;
        bool heroWasReady = true;
        Vector3 heroHome;

        // meteors
        struct Meteor { public Transform tr; public Vector3 start, target; public float t, delay; public bool active; }
        Meteor[] meteors;
        const int MeteorPool = 8;
        float meteorHoldT, meteorReadyAt;
        int meteorsUsed;

        // dome
        Transform dome;
        float domeHoldT, domeReadyAt, domeUntil;
        Health boundHealth; float lastHp;
        Material domeMat;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap() { EnsureInstance(); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; DomeActive = false; }

        /// <summary>Creates (once) and returns the controller. Leader wiring: DivineMiraclesAndHeroController.EnsureInstance();</summary>
        public static DivineMiraclesAndHeroController EnsureInstance()
        {
            if (Instance != null) return Instance;
            var ex = FindFirstObjectByType<DivineMiraclesAndHeroController>();
            if (ex != null) { Instance = ex; return ex; }
            var go = new GameObject("DivineMiraclesAndHeroController");
            return go.AddComponent<DivineMiraclesAndHeroController>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            Build();
        }

        void OnDestroy()
        {
            if (gd != null && subscribed) gd.GameReset -= ResetState;
            Unbind();
            if (Instance == this) { Instance = null; DomeActive = false; }
        }

        // ------------------------------------------------------------ build
        static GameObject Prim(PrimitiveType t, Transform parent, Vector3 lp, Vector3 ls, Material m)
        {
            var g = GameObject.CreatePrimitive(t);
            var c = g.GetComponent<Collider>(); if (c != null) Destroy(c);
            g.transform.SetParent(parent, false);
            g.transform.localPosition = lp; g.transform.localScale = ls;
            g.GetComponent<Renderer>().sharedMaterial = m;
            return g;
        }

        void Build()
        {
            var go = new GameObject("HeroChampion");
            go.transform.SetParent(transform, false);
            hero = go.transform;
            heroDimMat = Mats.Lit(new Color(0.35f, 0.35f, 0.38f));
            roleMats = new Material[(int)HRole.Count];
            roleMats[(int)HRole.Banner] = new Material(Mats.Lit(Color.white));
            roleMats[(int)HRole.Accent] = new Material(Mats.Lit(Color.white));
            roleMats[(int)HRole.Prim] = new Material(Mats.Lit(Color.white));
            roleMats[(int)HRole.Sec] = new Material(Mats.LitMetallic(Color.white, 0.5f));
            roleMats[(int)HRole.Glow] = new Material(Mats.Unlit(Color.white));
            roleMats[(int)HRole.Steel] = Mats.LitMetallic(new Color(0.78f, 0.8f, 0.85f), 0.55f);
            roleMats[(int)HRole.Dark] = Mats.LitMetallic(new Color(0.22f, 0.24f, 0.28f), 0.45f);
            roleMats[(int)HRole.Skin] = Mats.Lit(new Color(0.93f, 0.78f, 0.62f));
            roleMats[(int)HRole.Wood] = Mats.Lit(new Color(0.34f, 0.22f, 0.12f));
            roleMats[(int)HRole.Jade] = Mats.LitMetallic(new Color(0.12f, 0.55f, 0.42f), 0.6f);
            roleMats[(int)HRole.Bone] = Mats.Lit(new Color(0.92f, 0.88f, 0.76f));
            SetRoleColors(CivilizationCatalog.Get(CivilizationCatalog.DefaultId), new Color(0.72f, 0.08f, 0.12f), new Color(0.96f, 0.76f, 0.22f));
            BuildHeroVisuals(CivilizationCatalog.DefaultId);
            hero.gameObject.SetActive(false);

            meteors = new Meteor[MeteorPool];
            var mm = Mats.Unlit(new Color(1f, 0.45f, 0.1f));
            for (int i = 0; i < MeteorPool; i++)
            {
                var s = Prim(PrimitiveType.Sphere, transform, Vector3.zero, Vector3.one * 0.035f, mm);
                s.SetActive(false);
                meteors[i].tr = s.transform;
            }

            domeMat = new Material(Mats.Transparent(new Color(0.5f, 0.8f, 1f, 0.22f)));
            var d = Prim(PrimitiveType.Sphere, transform, Vector3.zero, Vector3.one * domeRadius * 2f, domeMat);
            d.name = "ProtectiveDome";
            dome = d.transform;
            d.SetActive(false);
        }

        // ------------------------------------------------------------ hero visuals
        Transform P(HRole role, PrimitiveType t, Transform parent, float x, float y, float z, float sx, float sy, float sz, float rx = 0f, float ry = 0f, float rz = 0f)
        {
            var g = Prim(t, parent, new Vector3(x, y, z), new Vector3(sx, sy, sz), roleMats[(int)role]);
            if (rx != 0f || ry != 0f || rz != 0f) g.transform.localRotation = Quaternion.Euler(rx, ry, rz);
            rBuild.Add(g.GetComponent<Renderer>()); roleBuild.Add(role);
            return g.transform;
        }

        static Transform Pivot(string n, Transform parent, float x, float y, float z)
        {
            var t = new GameObject(n).transform; t.SetParent(parent, false); t.localPosition = new Vector3(x, y, z); return t;
        }

        const PrimitiveType CUBE = PrimitiveType.Cube, SPH = PrimitiveType.Sphere, CYL = PrimitiveType.Cylinder;
        const float B = 0.008f; // plinth top / feet height

        /// <summary>Rebuilds the champion for a civilization. Roles map to shared/per-civ materials; no per-frame allocation afterwards.</summary>
        void BuildHeroVisuals(string civ)
        {
            builtCiv = civ;
            for (int i = hero.childCount - 1; i >= 0; i--) { var c = hero.GetChild(i); c.SetParent(null); Destroy(c.gameObject); }
            rBuild.Clear(); roleBuild.Clear();
            var plinth = Pivot("Plinth", hero, 0, 0, 0);
            heroBody = Pivot("Body", hero, 0, 0, 0);
            switch (civ)
            {
                case CivilizationCatalog.VerdantId: PlinthVerdant(plinth); BodyVerdant(heroBody); break;
                case CivilizationCatalog.IronId: PlinthIron(plinth); BodyIron(heroBody); break;
                default: PlinthCelestial(plinth); BodyCelestial(heroBody); break;
            }
            // plinth parts were added first: count them via the plinth transform
            heroPlinthCount = plinth.childCount;
            heroRenderers = rBuild.ToArray(); heroRoles = roleBuild.ToArray();
            rBuild.Clear(); roleBuild.Clear();
            if (hero.gameObject.activeSelf) SetHeroLook(Time.time >= heroReadyAt);
        }

        // Shared humanoid: ~1.5x a foot soldier (0.036 m) incl. helm. Returns nothing; sets torsoT/headT/capeT/weaponT.
        void Humanoid(Transform body, float w, HRole torso, HRole limbs, HRole boots, float capeLen, float capeW, float eyeZ)
        {
            // legs (heroic wide stance) + knees
            P(boots, CUBE, body, -0.0066f * w, B + 0.009f, 0f, 0.0078f * w, 0.018f, 0.0088f, 0, 0, 3);
            P(boots, CUBE, body, 0.0066f * w, B + 0.009f, 0f, 0.0078f * w, 0.018f, 0.0088f, 0, 0, -3);
            P(HRole.Steel, SPH, body, -0.0068f * w, B + 0.0115f, 0.0038f, 0.0052f, 0.0052f, 0.0052f);
            P(HRole.Steel, SPH, body, 0.0068f * w, B + 0.0115f, 0.0038f, 0.0052f, 0.0052f, 0.0052f);
            P(HRole.Dark, CUBE, body, -0.0066f * w, B + 0.0012f, 0.0012f, 0.0082f * w, 0.0024f, 0.0115f);
            P(HRole.Dark, CUBE, body, 0.0066f * w, B + 0.0012f, 0.0012f, 0.0082f * w, 0.0024f, 0.0115f);
            // belt + buckle
            P(HRole.Sec, CUBE, body, 0f, B + 0.0205f, 0f, 0.0228f * w, 0.003f, 0.0148f);
            P(HRole.Accent, CUBE, body, 0f, B + 0.0205f, 0.0076f, 0.0036f, 0.0036f, 0.0012f, 0, 0, 45);
            // torso: breathing pivot
            torsoT = Pivot("Torso", body, 0f, B + 0.0215f, 0f);
            P(torso, CUBE, torsoT, 0f, 0.0085f, 0f, 0.0215f * w, 0.017f, 0.0135f);
            P(HRole.Steel, CUBE, torsoT, 0f, 0.0097f, 0.0072f, 0.0165f * w, 0.012f, 0.0015f);
            P(HRole.Glow, CUBE, torsoT, 0f, 0.0097f, 0.0083f, 0.0056f, 0.0056f, 0.0014f, 0, 0, 45);   // chest emblem
            P(HRole.Banner, CUBE, body, 0f, B + 0.0125f, 0.0079f, 0.0095f * w, 0.0165f, 0.0012f);       // tabard
            P(HRole.Sec, CUBE, body, 0f, B + 0.0042f, 0.0079f, 0.0095f * w, 0.0014f, 0.0014f);
            // gorget, arms, gauntlets, pauldrons
            P(HRole.Steel, CYL, body, 0f, B + 0.0395f, 0f, 0.0125f, 0.0014f, 0.0105f);
            for (int s = -1; s <= 1; s += 2)
            {
                float ax = s * 0.0148f * w;
                P(limbs, CUBE, body, ax, B + 0.0285f, 0f, 0.0058f, 0.0145f, 0.0058f);
                P(HRole.Steel, CUBE, body, ax, B + 0.0192f, 0.0012f, 0.0070f, 0.0064f, 0.0070f);
                P(HRole.Steel, SPH, torsoT, s * 0.0135f * w, 0.0172f, 0f, 0.0105f, 0.0078f, 0.0105f);
            }
            // head
            headT = Pivot("Head", body, 0f, B + 0.0425f, 0f);
            P(HRole.Skin, SPH, headT, 0f, 0.0045f, 0f, 0.0115f, 0.0125f, 0.0115f);
            P(HRole.Glow, CUBE, headT, -0.0026f, 0.0052f, eyeZ, 0.0021f, 0.0012f, 0.0008f);
            P(HRole.Glow, CUBE, headT, 0.0026f, 0.0052f, eyeZ, 0.0021f, 0.0012f, 0.0008f);
            // cape (pivot at shoulders, sways)
            capeT = Pivot("Cape", body, 0f, B + 0.0385f, -0.0074f);
            P(HRole.Banner, CUBE, capeT, 0f, -capeLen * 0.5f, -0.0008f, capeW, capeLen, 0.0012f, 4f);
            P(HRole.Sec, CUBE, capeT, 0f, -capeLen, -0.0008f - 0.0004f, capeW, 0.0016f, 0.0014f, 4f);
            // weapon pivot at right fist
            weaponT = Pivot("Weapon", body, 0.0148f * w, B + 0.0192f, 0.0042f);
        }

        // ---------------- Celestial: armored general, jade-gold, winged tiered helm, guandao, long cape
        void BodyCelestial(Transform body)
        {
            Humanoid(body, 1.0f, HRole.Jade, HRole.Jade, HRole.Dark, 0.042f, 0.021f, 0.0054f);
            // tiered pauldrons (gold discs)
            for (int s = -1; s <= 1; s += 2)
            {
                P(HRole.Sec, CYL, torsoT, s * 0.0135f, 0.0197f, 0f, 0.0125f, 0.0007f, 0.0105f);
                P(HRole.Sec, CYL, torsoT, s * 0.0135f, 0.0212f, 0f, 0.0085f, 0.0007f, 0.0075f);
            }
            // tiered helm
            P(HRole.Prim, SPH, headT, 0f, 0.0070f, 0f, 0.0125f, 0.0085f, 0.0125f);
            P(HRole.Sec, CYL, headT, 0f, 0.0086f, 0f, 0.0158f, 0.0008f, 0.0158f);
            P(HRole.Prim, CYL, headT, 0f, 0.0100f, 0f, 0.0098f, 0.0016f, 0.0098f);
            P(HRole.Sec, CYL, headT, 0f, 0.0118f, 0f, 0.0062f, 0.0007f, 0.0062f);
            P(HRole.Sec, SPH, headT, 0f, 0.0135f, 0f, 0.0036f, 0.0046f, 0.0036f);
            P(HRole.Banner, CUBE, headT, 0f, 0.0120f, -0.0035f, 0.0022f, 0.0075f, 0.0022f, -25f);   // plume
            for (int s = -1; s <= 1; s += 2)
            {
                P(HRole.Sec, CUBE, headT, s * 0.0090f, 0.0085f, 0f, 0.0088f, 0.0012f, 0.0034f, 0, 0, s * 24f);   // wings
                P(HRole.Accent, CUBE, headT, s * 0.0092f, 0.0068f, 0f, 0.0074f, 0.0010f, 0.0030f, 0, 0, s * 12f);
                P(HRole.Sec, CUBE, headT, s * 0.0071f, 0.0050f, 0.0003f, 0.0016f, 0.0070f, 0.0036f);           // cheek guards
            }
            // guandao: long shaft, curved blade, tassel
            P(HRole.Wood, CYL, weaponT, 0f, 0.0100f, 0f, 0.0018f, 0.0320f, 0.0018f);
            P(HRole.Sec, SPH, weaponT, 0f, -0.0225f, 0f, 0.0034f, 0.0034f, 0.0034f);
            P(HRole.Sec, CYL, weaponT, 0f, 0.0312f, 0f, 0.0034f, 0.0010f, 0.0034f);
            P(HRole.Steel, CUBE, weaponT, 0.0036f, 0.0405f, 0f, 0.0066f, 0.0170f, 0.0011f, 0, 0, -8f);
            P(HRole.Glow, CUBE, weaponT, 0.0070f, 0.0405f, 0f, 0.0009f, 0.0150f, 0.0009f, 0, 0, -8f);
            P(HRole.Steel, CUBE, weaponT, 0.0010f, 0.0500f, 0f, 0.0030f, 0.0045f, 0.0011f, 0, 0, 30f);
            P(HRole.Banner, SPH, weaponT, 0f, 0.0300f, 0f, 0.0042f, 0.0070f, 0.0042f);
        }

        void PlinthCelestial(Transform pl)
        {
            P(HRole.Prim, CYL, pl, 0f, 0.0020f, 0f, 0.052f, 0.0020f, 0.052f);
            P(HRole.Sec, CYL, pl, 0f, 0.0042f, 0f, 0.055f, 0.0006f, 0.055f);
            P(HRole.Prim, CYL, pl, 0f, 0.0060f, 0f, 0.040f, 0.0020f, 0.040f);
            P(HRole.Sec, CYL, pl, 0f, 0.0079f, 0f, 0.042f, 0.0004f, 0.042f);
            for (int i = 0; i < 4; i++)
            {
                float a = (i * 90f + 45f) * Mathf.Deg2Rad;
                P(HRole.Glow, SPH, pl, Mathf.Cos(a) * 0.0235f, 0.0070f, Mathf.Sin(a) * 0.0235f, 0.0046f, 0.0046f, 0.0046f);
            }
        }

        // ---------------- Verdant: druid-knight, antlered helm, leaf mantle, living staff
        void BodyVerdant(Transform body)
        {
            Humanoid(body, 1.0f, HRole.Sec, HRole.Wood, HRole.Dark, 0.030f, 0.018f, 0.0054f);
            // leaf mantle ring around shoulders
            for (int i = 0; i < 7; i++)
            {
                float a = i / 7f * Mathf.PI * 2f;
                P(HRole.Prim, SPH, torsoT, Mathf.Cos(a) * 0.0128f, 0.0178f - Mathf.Abs(Mathf.Sin(a)) * 0.0006f, Mathf.Sin(a) * 0.0085f, 0.0085f, 0.0016f, 0.0046f, 0, -a * Mathf.Rad2Deg, 18f);
            }
            // leaf tips on cape bottom
            for (int s = -1; s <= 1; s++)
                P(HRole.Prim, CUBE, capeT, s * 0.0058f, -0.0300f, -0.0016f, 0.0050f, 0.0050f, 0.0012f, 0, 0, 45f);
            // antlered helm
            P(HRole.Wood, SPH, headT, 0f, 0.0060f, 0f, 0.0130f, 0.0100f, 0.0130f);
            P(HRole.Prim, CUBE, headT, 0f, 0.0040f, 0.0058f, 0.0112f, 0.0018f, 0.0010f);
            for (int s = -1; s <= 1; s += 2)
            {
                P(HRole.Bone, CUBE, headT, s * 0.0050f, 0.0128f, 0f, 0.0014f, 0.0110f, 0.0014f, 0, 0, -s * 16f);
                P(HRole.Bone, CUBE, headT, s * 0.0082f, 0.0138f, 0f, 0.0011f, 0.0062f, 0.0011f, 0, 0, -s * 52f);
                P(HRole.Bone, CUBE, headT, s * 0.0068f, 0.0172f, 0f, 0.0011f, 0.0058f, 0.0011f, 0, 0, s * 18f);
                P(HRole.Bone, CUBE, headT, s * 0.0098f, 0.0185f, 0f, 0.0010f, 0.0050f, 0.0010f, 0, 0, -s * 40f);
            }
            // living staff: twisted trunk, vines, glowing seed orb held by leaf prongs
            P(HRole.Wood, CYL, weaponT, 0f, 0.0080f, 0f, 0.0028f, 0.0225f, 0.0028f);
            P(HRole.Wood, CYL, weaponT, 0.0013f, 0.0365f, 0f, 0.0024f, 0.0085f, 0.0024f, 0, 0, -13f);
            for (int i = 0; i < 3; i++)
                P(HRole.Prim, SPH, weaponT, (i % 2 == 0 ? 0.0016f : -0.0016f), -0.0040f + i * 0.0085f, 0f, 0.0042f, 0.0026f, 0.0042f, 0, 0, 30f);
            P(HRole.Glow, SPH, weaponT, 0.0030f, 0.0470f, 0f, 0.0085f, 0.0085f, 0.0085f);
            for (int i = 0; i < 4; i++)
                P(HRole.Prim, CUBE, weaponT, 0.0030f + Mathf.Cos(i * 1.5708f) * 0.0050f, 0.0455f, Mathf.Sin(i * 1.5708f) * 0.0050f, 0.0016f, 0.0075f, 0.0016f, Mathf.Sin(i * 1.5708f) * 25f, 0f, -Mathf.Cos(i * 1.5708f) * 25f);
        }

        void PlinthVerdant(Transform pl)
        {
            P(HRole.Wood, CYL, pl, 0f, 0.0040f, 0f, 0.046f, 0.0040f, 0.046f);
            P(HRole.Prim, SPH, pl, 0f, 0.0074f, 0f, 0.042f, 0.0050f, 0.042f);
            P(HRole.Wood, CUBE, pl, 0.0235f, 0.0020f, 0f, 0.0120f, 0.0030f, 0.0050f, 0, 0, 15f);
            P(HRole.Wood, CUBE, pl, -0.0235f, 0.0020f, 0.004f, 0.0120f, 0.0030f, 0.0050f, 0, 25f, -15f);
            for (int s = -1; s <= 1; s += 2)
            {
                P(HRole.Bone, CYL, pl, s * 0.0165f, 0.0090f, 0.0100f, 0.0022f, 0.0014f, 0.0022f);
                P(HRole.Glow, SPH, pl, s * 0.0165f, 0.0108f, 0.0100f, 0.0062f, 0.0038f, 0.0062f);
            }
        }

        // ---------------- Iron: heavy plate juggernaut, horned helm, tower shield, rune war hammer
        void BodyIron(Transform body)
        {
            Humanoid(body, 1.28f, HRole.Steel, HRole.Dark, HRole.Dark, 0.026f, 0.024f, 0.0066f);
            // spiked pauldrons
            for (int s = -1; s <= 1; s += 2)
            {
                P(HRole.Dark, SPH, torsoT, s * 0.0172f, 0.0178f, 0f, 0.0120f, 0.0100f, 0.0120f);
                P(HRole.Steel, CUBE, torsoT, s * 0.0172f, 0.0245f, 0f, 0.0022f, 0.0050f, 0.0022f, 0, 0, 45f);
            }
            P(HRole.Sec, CUBE, torsoT, 0f, 0.0002f, 0.0072f, 0.0230f, 0.0016f, 0.0010f);   // blue belt rune
            // great helm + visor + horns + crest
            P(HRole.Steel, SPH, headT, 0f, 0.0052f, 0f, 0.0142f, 0.0132f, 0.0142f);
            P(HRole.Dark, CUBE, headT, 0f, 0.0050f, 0.0060f, 0.0112f, 0.0042f, 0.0014f);
            P(HRole.Dark, CUBE, headT, 0f, 0.0010f, 0.0050f, 0.0080f, 0.0040f, 0.0014f);
            P(HRole.Sec, CUBE, headT, 0f, 0.0116f, 0f, 0.0016f, 0.0042f, 0.0124f);
            for (int s = -1; s <= 1; s += 2)
            {
                P(HRole.Bone, CUBE, headT, s * 0.0078f, 0.0090f, 0f, 0.0024f, 0.0076f, 0.0024f, 0, 0, -s * 42f);
                P(HRole.Bone, CUBE, headT, s * 0.0112f, 0.0132f, 0f, 0.0017f, 0.0062f, 0.0017f, 0, 0, -s * 12f);
            }
            // tower shield (left)
            var sh = Pivot("Shield", body, -0.0235f * 1.28f, B + 0.0270f, 0.0062f);
            P(HRole.Dark, CUBE, sh, 0f, 0f, -0.0006f, 0.0186f, 0.0330f, 0.0022f);
            P(HRole.Prim, CUBE, sh, 0f, 0f, 0.0008f, 0.0160f, 0.0300f, 0.0014f);
            P(HRole.Sec, CUBE, sh, 0f, 0f, 0.0017f, 0.0042f, 0.0300f, 0.0008f);
            P(HRole.Sec, CUBE, sh, 0f, 0.0070f, 0.0018f, 0.0160f, 0.0042f, 0.0008f);
            P(HRole.Glow, SPH, sh, 0f, 0.0040f, 0.0022f, 0.0064f, 0.0064f, 0.0030f);
            // war hammer
            P(HRole.Wood, CYL, weaponT, 0f, 0.0085f, 0f, 0.0020f, 0.0270f, 0.0020f);
            P(HRole.Dark, CYL, weaponT, 0f, -0.0170f, 0f, 0.0036f, 0.0012f, 0.0036f);
            P(HRole.Steel, CUBE, weaponT, 0f, 0.0395f, 0f, 0.0215f, 0.0105f, 0.0105f);
            P(HRole.Dark, CUBE, weaponT, 0.0104f, 0.0395f, 0f, 0.0014f, 0.0122f, 0.0122f);
            P(HRole.Dark, CUBE, weaponT, -0.0104f, 0.0395f, 0f, 0.0014f, 0.0122f, 0.0122f);
            P(HRole.Glow, CUBE, weaponT, 0f, 0.0395f, 0.0054f, 0.0130f, 0.0016f, 0.0006f);
            P(HRole.Steel, CUBE, weaponT, 0f, 0.0476f, 0f, 0.0030f, 0.0062f, 0.0030f, 0, 0, 45f);
        }

        void PlinthIron(Transform pl)
        {
            P(HRole.Dark, CUBE, pl, 0f, 0.0020f, 0f, 0.0500f, 0.0040f, 0.0400f);
            P(HRole.Steel, CUBE, pl, 0f, 0.0055f, 0f, 0.0360f, 0.0030f, 0.0270f);
            P(HRole.Prim, CUBE, pl, 0f, 0.0076f, 0f, 0.0480f, 0.0010f, 0.0340f);
            P(HRole.Glow, CUBE, pl, 0f, 0.0040f, 0.0201f, 0.0400f, 0.0006f, 0.0006f);
            P(HRole.Glow, CUBE, pl, 0.0251f, 0.0040f, 0f, 0.0006f, 0.0006f, 0.0300f);
            P(HRole.Glow, CUBE, pl, -0.0251f, 0.0040f, 0f, 0.0006f, 0.0006f, 0.0300f);
            P(HRole.Sec, CUBE, pl, 0.0255f, 0.0078f, 0.0150f, 0.0030f, 0.0030f, 0.0030f, 0, 0, 45f);
            P(HRole.Sec, CUBE, pl, -0.0255f, 0.0078f, 0.0150f, 0.0030f, 0.0030f, 0.0030f, 0, 0, 45f);
        }

        // ------------------------------------------------------------ hero animation (allocation-free)
        void AnimateHero(bool ready)
        {
            float t = Time.time;
            float br = Mathf.Sin(t * 2.1f);
            float sq = ready ? 1f : 0.93f;
            float lean = 0f, capeBase = 5f, capeFreq = 1.7f, glow = 0.82f + 0.18f * Mathf.Sin(t * 3f);
            float weaponX = 0f;
            if (grabbed) { sq = 1.12f; lean = -12f + Mathf.Sin(t * 9f) * 3f; capeBase = 28f; capeFreq = 7f; glow = 1.35f + 0.2f * Mathf.Sin(t * 12f); weaponX = -20f; }
            float s = (t - slamAt) / 0.55f;
            float slamK = (s >= 0f && s <= 1f) ? Mathf.Sin(s * Mathf.PI) : 0f;
            if (slamK > 0f) { sq *= 1f - 0.14f * slamK; weaponX = 85f * slamK; lean = 14f * slamK; glow = 1.2f + slamK; capeBase = 5f - 15f * slamK; }
            if (!ready) glow = 1f;
            heroBody.localPosition = new Vector3(0f, grabbed ? 0f : 0.0004f * Mathf.Sin(t * 2.1f + 0.5f), 0f);
            heroBody.localRotation = Quaternion.Euler(lean, grabbed ? 0f : 0f, 0f);
            heroBody.localScale = new Vector3(1f, sq, 1f);
            if (torsoT != null) torsoT.localScale = new Vector3(1f + 0.015f * br, 1f + 0.03f * br, 1f + 0.015f * br);
            if (capeT != null) capeT.localRotation = Quaternion.Euler(capeBase + 6f * Mathf.Sin(t * capeFreq), 0f, 3f * Mathf.Sin(t * capeFreq * 0.7f + 1f));
            if (headT != null) headT.localRotation = Quaternion.Euler(0f, 8f * Mathf.Sin(t * 0.6f), 0f);
            if (weaponT != null) weaponT.localRotation = Quaternion.Euler(weaponX, 0f, 2f * Mathf.Sin(t * 1.3f));
            if (ready)
            {
                var gm = roleMats[(int)HRole.Glow];
                var c = glowBase * glow; c.a = 1f;
                gm.SetColor(BaseColorId, c);
            }
        }

        void ApplyCivColors()
        {
            var p = CivilizationProfile.Current;
            if (p == null) return;
            var def = CivilizationCatalog.Get(p.civId);
            if (def.id != builtCiv) BuildHeroVisuals(def.id);
            SetRoleColors(def, p.bannerColor, p.emblemColor);
            if (domeMat != null) { domeMat.SetColor("_BaseColor", Color.Lerp(new Color(0.6f, 0.85f, 1f), p.emblemColor, 0.25f) * new Color(1, 1, 1, 0.22f)); }
        }

        void SetRoleColors(CivilizationDefinition def, Color banner, Color emblem)
        {
            SetBase(roleMats[(int)HRole.Banner], banner);
            SetBase(roleMats[(int)HRole.Accent], emblem);
            SetBase(roleMats[(int)HRole.Prim], def.primary);
            SetBase(roleMats[(int)HRole.Sec], def.accent);
            glowBase = Color.Lerp(emblem, Color.white, 0.25f);
            SetBase(roleMats[(int)HRole.Glow], glowBase);
        }

        static void SetBase(Material m, Color c) { m.SetColor("_BaseColor"  , c); m.color = c; }

        // ------------------------------------------------------------ state
        void TrySubscribe()
        {
            gd = GameDirector.Instance;
            if (gd == null || subscribed) return;
            gd.GameReset += ResetState;
            subscribed = true;
        }

        void ResetState()
        {
            meteorsUsed = 0; meteorReadyAt = 0f; domeReadyAt = 0f; heroReadyAt = 0f;
            domeUntil = 0f; DomeActive = false; if (dome != null) dome.gameObject.SetActive(false);
            grabbed = false; heroWasReady = true;
            for (int i = 0; i < meteors.Length; i++) { meteors[i].active = false; meteors[i].tr.gameObject.SetActive(false); }
            meteorHoldT = 0f; domeHoldT = 0f;
            ApplyCivColors();
        }

        void Bind(Health h)
        {
            if (h == boundHealth) return;
            Unbind();
            boundHealth = h;
            if (h != null) { lastHp = h.Current; h.Damaged += OnHallDamaged; }
        }

        void Unbind()
        {
            if (boundHealth != null) boundHealth.Damaged -= OnHallDamaged;
            boundHealth = null;
        }

        void OnHallDamaged(Health h)
        {
            float diff = lastHp - h.Current;
            if (DomeActive && diff > 0f && !h.IsDead) h.Heal(diff);
            lastHp = h.Current;
        }

        static bool Combat(GameDirector g) => g.Phase == GamePhase.BuildPhase || g.Phase == GamePhase.Wave;

        // ------------------------------------------------------------ update
        void Update()
        {
            if (!subscribed) TrySubscribe();
            if (gd == null) return;
            var hall = gd.Hall;
            Bind(hall != null ? hall.health : null);
            if (boundHealth != null && !DomeActive) lastHp = boundHealth.Current;

            UpdateMeteors();
            UpdateDomeLife();

            var rig = HandRig.Instance;
            bool active = rig != null && hall != null && gd.Surface != null && Combat(gd);
            if (!active)
            {
                if (hero.gameObject.activeSelf) hero.gameObject.SetActive(false);
                grabbed = false; meteorHoldT = 0f; domeHoldT = 0f;
                return;
            }
            UpdateHero(rig, hall);
            UpdateMeteorGesture(rig, hall);
            UpdateDomeGesture(rig, hall);
        }

        // ------------------------------------------------------------ hero
        Vector3 HomePos(TownHall hall)
        {
            var cam = Camera.main;
            Vector3 right = cam != null ? cam.transform.right : Vector3.right;
            right.y = 0f; right = right.sqrMagnitude > 1e-4f ? right.normalized : Vector3.right;
            Vector3 p = hall.transform.position + right * 0.22f;
            return gd.Surface.IsPointSupported(p) ? gd.Surface.ProjectOnto(p) : gd.Surface.ProjectOnto(hall.transform.position - right * 0.22f);
        }

        void UpdateHero(HandRig rig, TownHall hall)
        {
            if (!hero.gameObject.activeSelf)
            {
                hero.gameObject.SetActive(true);
                ApplyCivColors();
                heroHome = HomePos(hall);
                hero.position = heroHome;
            }
            bool ready = Time.time >= heroReadyAt;
            if (ready != heroWasReady)
            {
                heroWasReady = ready;
                SetHeroLook(ready);
                if (ready) hall.ShowToast(Strings.HeroReady, new Color(1f, 0.9f, 0.5f), 2.5f);
            }

            AnimateHero(ready);
            if (!grabbed)
            {
                if (!ready) { hero.position = heroHome; return; }
                heroHome = HomePos(hall);
                hero.position = heroHome;
                hero.rotation = Quaternion.identity;
                for (int i = 0; i < 2; i++)
                {
                    var s = (HandSide)i;
                    if (!rig.IsPinching(s) || !rig.TryGetIndexTip(s, out var tip)) continue;
                    if ((tip - (hero.position + Vector3.up * 0.04f)).sqrMagnitude > grabRadius * grabRadius) continue;
                    grabbed = true; grabSide = s;
                    ProceduralAudio.Fire(Sfx.Click, hero.position);
                    break;
                }
                return;
            }

            if (rig.IsPinching(grabSide) && rig.TryGetIndexTip(grabSide, out var t))
            {
                hero.position = t - Vector3.up * 0.04f;
                return;
            }
            // released (or tracking lost)
            grabbed = false;
            var surf = gd.Surface;
            Vector3 foot = hero.position;
            Vector3 proj = surf.ProjectOnto(foot);
            float h = foot.y - proj.y;
            if (h >= -0.03f && h <= maxDropHeight && surf.IsPointSupported(proj))
                Slam(proj);
            hero.position = heroHome;
        }

        void SetHeroLook(bool ready)
        {
            if (heroRenderers == null) return;
            if (ready) { RestoreMaterials(); return; }
            // cooldown: grey out the champion and the plinth glow
            for (int i = 0; i < heroRenderers.Length; i++)
            {
                var r = heroRenderers[i];
                if (r == null) continue;
                if (i >= heroPlinthCount || heroRoles[i] == HRole.Glow) r.sharedMaterial = heroDimMat;
            }
        }

        void RestoreMaterials()
        {
            for (int i = 0; i < heroRenderers.Length; i++)
                if (heroRenderers[i] != null) heroRenderers[i].sharedMaterial = roleMats[(int)heroRoles[i]];
        }

        void Slam(Vector3 center)
        {
            heroReadyAt = Time.time + heroCooldown;
            slamAt = Time.time;
            hero.position = center;
            DealArea(center, slamRadius, slamDamage, slamPush);
            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Ring(center, slamRadius);
                VfxPool.Instance.Burst(center + Vector3.up * 0.01f, new Color(1f, 0.95f, 0.5f));
            }
            ProceduralAudio.Fire(Sfx.Shockwave, center);
            ProceduralAudio.Fire(Sfx.Thud, center);
            if (gd.Hall != null) gd.Hall.ShowToast(Strings.HeroSlam, new Color(1f, 0.9f, 0.4f), 1.8f);
        }

        static void DealArea(Vector3 center, float radius, float damage, float push)
        {
            float r2 = radius * radius;
            var list = MonsterUnit.Active;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (i >= list.Count) continue;
                var m = list[i];
                if (m == null) continue;
                Vector3 d = m.transform.position - center; d.y = 0f;
                if (d.sqrMagnitude > r2) continue;
                Vector3 dir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
                if (push > 0f) m.Push(dir, push);
                if (m.health != null && !m.health.IsDead) m.health.Damage(damage);
            }
        }

        // ------------------------------------------------------------ meteors
        void UpdateMeteorGesture(HandRig rig, TownHall hall)
        {
            if (!rig.TryGetPalm(HandSide.Left, out var pl) || !rig.TryGetPalm(HandSide.Right, out var pr)) { meteorHoldT = 0f; return; }
            float sep = Mathf.Sqrt((pl.position.x - pr.position.x) * (pl.position.x - pr.position.x) + (pl.position.z - pr.position.z) * (pl.position.z - pr.position.z));
            float nl = Mathf.Abs(Vector3.Dot(pl.rotation * Vector3.up, Vector3.up));
            float nr = Mathf.Abs(Vector3.Dot(pr.rotation * Vector3.up, Vector3.up));
            float tableY = gd.Surface.Height;
            bool pose = nl > palmFlatDot && nr > palmFlatDot && sep > 0.12f && sep < 0.7f
                        && pl.position.y > tableY + minPalmHeight && pr.position.y > tableY + minPalmHeight;
            if (!pose || grabbed) { meteorHoldT = 0f; return; }
            meteorHoldT += Time.deltaTime;
            if (meteorHoldT < meteorHold) return;
            if (Time.time < meteorReadyAt) return;
            if (MeteorCharges <= 0)
            {
                meteorHoldT = -2f; hall.ShowToast(Strings.MeteorEmpty, new Color(1f, 0.5f, 0.4f), 1.5f);
                return;
            }
            meteorHoldT = 0f;
            meteorsUsed++;
            meteorReadyAt = Time.time + meteorCooldown;
            Vector3 mid = gd.Surface.ProjectOnto((pl.position + pr.position) * 0.5f);
            Vector3 hp = hall.transform.position; hp.y = mid.y;
            if ((mid - hp).sqrMagnitude > 0.5f * 0.5f) mid = hp;
            for (int i = 0; i < MeteorPool; i++)
            {
                var a = Random.value * Mathf.PI * 2f; float r = Mathf.Sqrt(Random.value) * meteorRadius;
                Vector3 tgt = mid + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                meteors[i].target = tgt;
                meteors[i].start = tgt + new Vector3(0.1f, meteorDropHeight, 0.05f);
                meteors[i].delay = i * 0.12f;
                meteors[i].t = 0f;
                meteors[i].active = true;
                meteors[i].tr.gameObject.SetActive(false);
            }
            hall.ShowToast(Strings.MeteorRain, new Color(1f, 0.6f, 0.2f), 1.8f);
            ProceduralAudio.Fire(Sfx.Horn, mid);
        }

        void UpdateMeteors()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < meteors.Length; i++)
            {
                if (!meteors[i].active) continue;
                meteors[i].t += dt;
                float lt = meteors[i].t - meteors[i].delay;
                if (lt < 0f) continue;
                var tr = meteors[i].tr;
                if (!tr.gameObject.activeSelf) tr.gameObject.SetActive(true);
                float k = lt / meteorFallTime;
                if (k < 1f) { tr.position = Vector3.Lerp(meteors[i].start, meteors[i].target, k * k); continue; }
                meteors[i].active = false;
                tr.gameObject.SetActive(false);
                Vector3 p = meteors[i].target;
                DealArea(p, meteorImpactRadius, meteorDamage, meteorPush);
                if (VfxPool.Instance != null)
                {
                    VfxPool.Instance.Burst(p + Vector3.up * 0.01f, new Color(1f, 0.5f, 0.15f));
                    VfxPool.Instance.Ring(p, meteorImpactRadius);
                }
                ProceduralAudio.Fire(Sfx.Thud, p);
            }
        }

        // ------------------------------------------------------------ dome
        void UpdateDomeGesture(HandRig rig, TownHall hall)
        {
            if (DomeActive) { domeHoldT = 0f; return; }
            var cam = Camera.main;
            if (cam == null || !rig.TryGetWrist(HandSide.Left, out var wl) || !rig.TryGetWrist(HandSide.Right, out var wr)) { domeHoldT = 0f; return; }
            Vector3 hc = hall.transform.position;
            Vector3 mid = (wl.position + wr.position) * 0.5f;
            Vector3 lh = cam.transform.InverseTransformPoint(wl.position), rh = cam.transform.InverseTransformPoint(wr.position);
            bool crossed = lh.x > rh.x + 0.01f && (wl.position - wr.position).sqrMagnitude < crossMaxDistance * crossMaxDistance;
            float dx = mid.x - hc.x, dz = mid.z - hc.z;
            bool aboveHall = dx * dx + dz * dz < 0.35f * 0.35f && mid.y > hc.y + 0.08f;
            if (!(crossed && aboveHall)) { domeHoldT = 0f; return; }
            domeHoldT += Time.deltaTime;
            if (domeHoldT < domeHold) return;
            if (Time.time < domeReadyAt) { domeHoldT = -2f; hall.ShowToast(Strings.MiracleRecharging, new Color(0.7f, 0.85f, 1f), 1.5f); return; }
            domeHoldT = 0f;
            domeUntil = Time.time + domeDuration;
            domeReadyAt = domeUntil + domeCooldown;
            DomeActive = true;
            if (boundHealth != null) lastHp = boundHealth.Current;
            dome.position = hc;
            dome.gameObject.SetActive(true);
            ApplyCivColors();
            if (VfxPool.Instance != null) VfxPool.Instance.Ring(hc, domeRadius);
            ProceduralAudio.Fire(Sfx.Horn, hc);
            hall.ShowToast(Strings.DomeRaised, new Color(0.6f, 0.85f, 1f), 1.8f);
        }

        void UpdateDomeLife()
        {
            if (!DomeActive) return;
            if (gd != null && gd.Hall != null) dome.position = gd.Hall.transform.position;
            float left = domeUntil - Time.time;
            if (left > 1.5f) dome.localScale = Vector3.one * domeRadius * 2f;
            else dome.localScale = Vector3.one * domeRadius * 2f * (1f + 0.04f * Mathf.Sin(Time.time * 30f)); // flicker before expiry
            if (left > 0f) return;
            DomeActive = false;
            dome.gameObject.SetActive(false);
            if (gd.Hall != null) gd.Hall.ShowToast(Strings.DomeFaded, new Color(0.7f, 0.8f, 0.9f), 1.5f);
        }
    }
}
