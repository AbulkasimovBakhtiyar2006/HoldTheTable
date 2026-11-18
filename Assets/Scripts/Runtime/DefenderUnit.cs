using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    public enum DefenderKind { Spearman, Swordsman }

    /// <summary>
    /// Friendly miniature garrison soldier (Spearman or Swordsman) spawned by a placed Barracks or the TownHall garrison.
    /// Wears the player's civilization architecture style and banner colors, patrols near defensive walls/towers,
    /// and marches out to intercept nearby enemies in melee combat, holding them back at the defensive line.
    /// Owner: @living-civ (Feature 4).
    /// </summary>
    public class DefenderUnit : MonoBehaviour
    {
        public const float SpearmanHp = 10f;
        public const float SwordsmanHp = 14f;
        public const float MoveSpeed = 0.085f;
        public const float PatrolSpeed = 0.048f;
        public const float SpearmanDps = 2.4f;
        public const float SwordsmanDps = 3.0f;
        public const float SpearmanReach = 0.038f;
        public const float SwordsmanReach = 0.032f;
        public const float InterceptRange = 0.34f;
        public const float CounterDamageFactor = 0.55f;
        const float SeparationRadius = UnitScale.SeparationRadius;
        const float AttackInterval = 0.75f;

        /// <summary>All currently active friendly defenders on the table.</summary>
        public static readonly List<DefenderUnit> Active = new List<DefenderUnit>();
        public static event Action<DefenderUnit> Killed;

        static SimplePool<DefenderUnit> pool;
        static Transform poolRoot;

        public Health health;
        public DefenderKind Kind { get; private set; }
        public BarracksSpawner SourceSpawner { get; private set; }
        public MonsterUnit Target { get; private set; }
        public Vector3 HomePosition { get; private set; }
        public Vector3 PatrolWaypoint { get; private set; }
        public bool IsAlive => launched && !dying && health != null && !health.IsDead;
        public float Radius => UnitScale.SoldierRadius * UnitScale.Soldier(Kind);
        public float MeleeReach => Kind == DefenderKind.Spearman ? SpearmanReach : SwordsmanReach;
        public float MeleeDps => (Kind == DefenderKind.Spearman ? SpearmanDps : SwordsmanDps) * GetDamageMultiplier();
        public Vector3 AimPoint => transform.position + transform.up * UnitScale.AimHeight;

        Transform visualRoot;
        Transform bobRoot;
        Transform weaponPivot;
        CivilizationDefinition lastCiv;
        Color lastBannerColor;
        ArchitectureStyle lastStyle;
        bool visualBuilt;

        bool launched;
        bool dying;
        float attackTimer;
        float counterTimer;
        float patrolWaitTimer;
        float bobPhase;
        int patrolSlot;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active.Clear();
            Killed = null;
            pool = null;
            poolRoot = null;
        }

        static void EnsurePool(int prewarm = 6)
        {
            if (pool == null || poolRoot == null)
            {
                if (poolRoot == null) poolRoot = new GameObject("DefenderPool").transform;
                pool = new SimplePool<DefenderUnit>(Create, prewarm);
            }
        }

        static DefenderUnit Create()
        {
            if (poolRoot == null) poolRoot = new GameObject("DefenderPool").transform;
            var go = new GameObject("DefenderUnit");
            go.transform.SetParent(poolRoot, false);
            var d = go.AddComponent<DefenderUnit>();
            d.health = go.AddComponent<Health>();
            d.health.Died += d.OnDied;
            d.bobRoot = BuildingVisuals.NewRoot(go.transform, "Bob");
            go.SetActive(false);
            return d;
        }

        /// <summary>Spawns a pooled friendly defender affiliated with a Barracks or TownHall garrison spawner.</summary>
        public static DefenderUnit Spawn(BarracksSpawner spawner, Vector3 spawnPos, DefenderKind kind, int slotIndex = 0, CivilizationDefinition civOverride = null)
        {
            EnsurePool(6);
            var d = pool.Get();
            d.Launch(spawner, spawnPos, kind, slotIndex, civOverride);
            return d;
        }

        /// <summary>Despawns all active defenders immediately (used on session reset, restart, or new empire).</summary>
        public static void DespawnAll()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var d = Active[i];
                if (d == null) continue;
                d.launched = false;
                d.dying = false;
                d.Target = null;
                d.SourceSpawner = null;
                if (pool != null) pool.Release(d);
                else d.gameObject.SetActive(false);
            }
            Active.Clear();
        }

        /// <summary>
        /// Optional query for MonsterUnit or @integrator: returns an alive DefenderUnit blocking world point `p` within `pad`.
        /// </summary>
        public static DefenderUnit FindBlockingDefender(Vector3 p, float pad = 0.022f)
        {
            float bestSq = float.MaxValue;
            DefenderUnit best = null;
            for (int i = 0; i < Active.Count; i++)
            {
                var d = Active[i];
                if (d == null || !d.IsAlive) continue;
                if (d.ContactsMonster(p, pad, out float sq) && sq < bestSq)
                {
                    bestSq = sq;
                    best = d;
                }
            }
            return best;
        }

        public bool ContactsMonster(Vector3 monsterPos, float pad) => ContactsMonster(monsterPos, pad, out _);

        public bool ContactsMonster(Vector3 monsterPos, float pad, out float planarSq)
        {
            Vector3 up = SurfaceUp();
            Vector3 diff = monsterPos - transform.position;
            float vDist = Mathf.Abs(Vector3.Dot(diff, up));
            if (vDist > 0.08f) { planarSq = float.MaxValue; return false; }
            diff -= Vector3.Dot(diff, up) * up;
            planarSq = diff.sqrMagnitude;
            float r = Radius + pad;
            return planarSq <= r * r;
        }

        public void Launch(BarracksSpawner spawner, Vector3 spawnPos, DefenderKind kind, int slotIndex, CivilizationDefinition civOverride)
        {
            SourceSpawner = spawner;
            Kind = kind;
            patrolSlot = slotIndex;
            HomePosition = spawnPos;
            Target = null;
            attackTimer = 0.25f;
            counterTimer = 0.6f;
            patrolWaitTimer = 0.2f;
            bobPhase = UnityEngine.Random.value * 10f;
            dying = false;
            launched = true;

            float baseHp = kind == DefenderKind.Swordsman ? SwordsmanHp : SpearmanHp;
            health.Init(baseHp * GetHpMultiplier());

            RebuildVisualIfNeeded(civOverride);

            ITableSurface s = CurrentSurface();
            if (s != null) spawnPos = s.ProjectOnto(spawnPos);
            transform.position = spawnPos;
            Vector3 up = SurfaceUp();
            Vector3 fwd = spawner != null ? spawner.transform.forward : Vector3.forward;
            fwd -= Vector3.Dot(fwd, up) * up;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            transform.rotation = Quaternion.LookRotation(fwd.normalized, up);

            PickPatrolWaypoint();

            if (!gameObject.activeSelf) gameObject.SetActive(true);
            if (!Active.Contains(this)) Active.Add(this);
        }

        static float GetHpMultiplier()
        {
            if (UpgradeState.Instance == null) return 1f;
            if (Enum.TryParse("Barracks", out UnitType bt)) return UpgradeState.Instance.HpMul(bt);
            return UpgradeState.Instance.HpMul(UnitType.Wall);
        }

        static float GetDamageMultiplier()
        {
            if (UpgradeState.Instance == null) return 1f;
            if (Enum.TryParse("Barracks", out UnitType bt)) return UpgradeState.Instance.DamageMul(bt);
            return UpgradeState.Instance.DamageMul(UnitType.ArcherTower);
        }

        void RebuildVisualIfNeeded(CivilizationDefinition civOverride)
        {
            BuildingVisuals.Resolve(civOverride, out var style, out var pal);
            if (visualBuilt && visualRoot != null && style == lastStyle && ColorsClose(pal.banner, lastBannerColor) && civOverride == lastCiv)
                return;

            if (visualRoot != null) Destroy(visualRoot.gameObject);
            lastCiv = civOverride;
            lastStyle = style;
            lastBannerColor = pal.banner;
            visualRoot = BuildSoldierVisual(bobRoot, Kind, style, pal, out weaponPivot);
            visualBuilt = true;
        }

        static bool ColorsClose(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.02f;
        }

        // ------------------------------------------------------------ soldier visuals
        static readonly Color Skin = new Color(0.93f, 0.77f, 0.62f);
        static readonly Color Steel = new Color(0.74f, 0.77f, 0.82f);
        static readonly Color DarkSteel = new Color(0.30f, 0.33f, 0.38f);
        static readonly Color Leather = new Color(0.36f, 0.24f, 0.13f);
        static readonly Color Wood = new Color(0.50f, 0.33f, 0.16f);
        static readonly Color Gold = new Color(0.95f, 0.78f, 0.25f);
        static readonly Color EyeCol = new Color(0.08f, 0.08f, 0.10f);

        /// <summary>Merges all direct mesh children of root into one mesh per material (build-time only, no per-frame cost).</summary>
        public static void CombineByMaterial(Transform root)
        {
            var groups = new Dictionary<Material, List<CombineInstance>>();
            var kill = new List<GameObject>();
            Matrix4x4 w2l = root.worldToLocalMatrix;
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                var mf = c.GetComponent<MeshFilter>();
                var mr = c.GetComponent<MeshRenderer>();
                if (mf == null || mr == null || mf.sharedMesh == null || mr.sharedMaterial == null) continue;
                if (!groups.TryGetValue(mr.sharedMaterial, out var list)) { list = new List<CombineInstance>(); groups[mr.sharedMaterial] = list; }
                list.Add(new CombineInstance { mesh = mf.sharedMesh, transform = w2l * c.localToWorldMatrix });
                kill.Add(c.gameObject);
            }
            foreach (var g in kill) UnityEngine.Object.DestroyImmediate(g);
            foreach (var kv in groups)
            {
                var mesh = new Mesh { name = "soldierPart" };
                mesh.CombineMeshes(kv.Value.ToArray(), true, true);
                var go = new GameObject("part");
                go.transform.SetParent(root, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = kv.Key;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            }
        }

        /// <summary>Legs, boots, torso, neck, arms, head+face shared by every civ; style tunes proportions/colors.</summary>
        static void BuildBody(BuildingVisuals.Kit k, ArchitectureStyle style, BuildingVisuals.Palette pal, bool bareLegs)
        {
            Color legC = style == ArchitectureStyle.StoneTowers ? DarkSteel : (style == ArchitectureStyle.Timber ? Leather : new Color(0.20f, 0.12f, 0.10f));
            Color bootC = style == ArchitectureStyle.StoneTowers ? Steel : Leather;
            // legs + boots + cuffs
            for (int s = -1; s <= 1; s += 2)
            {
                float x = s * 0.0042f;
                k.Cyl(new Vector3(x, 0.004f, 0f), 0.0027f, 0.008f, legC);
                k.Ellipsoid(new Vector3(x, 0.0025f, 0.0015f), new Vector3(0.0058f, 0.0055f, 0.0098f), bootC);
                k.Cyl(new Vector3(x, 0.0055f, 0f), 0.0032f, 0.0025f, style == ArchitectureStyle.TieredRoofs ? pal.trim : pal.dark);
            }
            // torso (tapered: hips + chest) + neck
            k.Cyl(new Vector3(0f, 0.0085f, 0f), 0.0072f, 0.005f, pal.main);
            k.Cyl(new Vector3(0f, 0.0125f, 0f), 0.0078f, 0.0085f, style == ArchitectureStyle.StoneTowers ? Steel : pal.main);
            k.Cyl(new Vector3(0f, 0.0205f, 0f), 0.0026f, 0.0028f, Skin);
            // belt + buckle
            k.Cyl(new Vector3(0f, 0.0105f, 0f), 0.0081f, 0.0022f, style == ArchitectureStyle.Timber ? Leather : pal.dark);
            k.Box(new Vector3(0f, 0.0116f, 0.0081f), new Vector3(0.0034f, 0.0030f, 0.0014f), pal.trim);
            // head, ears hint, eyes
            k.Ball(new Vector3(0f, 0.0252f, 0f), UnitScale.HeadDiameter, Skin);
            k.Ball(new Vector3(-0.0026f, 0.0256f, 0.0049f), 0.0021f, EyeCol);
            k.Ball(new Vector3(0.0026f, 0.0256f, 0.0049f), 0.0021f, EyeCol);
            k.Box(new Vector3(0f, 0.0236f, 0.0057f), new Vector3(0.0016f, 0.0016f, 0.0016f), new Color(0.85f, 0.65f, 0.52f)); // nose
        }

        /// <summary>Civ-specific torso dressing, tabard, pauldrons and helmet.</summary>
        static void BuildArmorAndHelm(BuildingVisuals.Kit k, ArchitectureStyle style, BuildingVisuals.Palette pal, bool archer)
        {
            switch (style)
            {
                case ArchitectureStyle.TieredRoofs:
                    // Celestial: lamellar robe, flared skirt, gold sash, wide pauldron plates, upturned conical hat with tassel
                    k.Cone(new Vector3(0f, 0.0035f, 0f), 0.0092f, 0.0085f, pal.main);                 // flared robe skirt
                    k.Cyl(new Vector3(0f, 0.0035f, 0f), 0.0094f, 0.0012f, pal.trim);                  // hem trim
                    k.Box(new Vector3(0f, 0.0142f, 0.0072f), new Vector3(0.0065f, 0.0105f, 0.0016f), pal.banner);     // chest tabard
                    k.Box(new Vector3(0f, 0.0142f, -0.0072f), new Vector3(0.0075f, 0.0105f, 0.0016f), pal.banner);
                    k.Box(new Vector3(0f, 0.0142f, 0.0079f), new Vector3(0.0014f, 0.0095f, 0.0008f), pal.trim);       // gold seam
                    k.Cyl(new Vector3(0f, 0.0104f, 0f), 0.0084f, 0.0026f, Gold);                                        // sash
                    k.Box(new Vector3(-0.0088f, 0.0178f, 0f), new Vector3(0.0062f, 0.0024f, 0.0085f), pal.trim, new Vector3(0f, 0f, 14f));
                    k.Box(new Vector3(0.0088f, 0.0178f, 0f), new Vector3(0.0062f, 0.0024f, 0.0085f), pal.trim, new Vector3(0f, 0f, -14f));
                    k.Cone(new Vector3(0f, 0.0292f, 0f), 0.0100f, 0.0085f, pal.roof);                  // conical hat
                    k.Cyl(new Vector3(0f, 0.0284f, 0f), 0.0104f, 0.0011f, pal.trim);                   // brim
                    k.Ball(new Vector3(0f, 0.0382f, 0f), 0.0030f, Gold);                               // finial
                    k.Box(new Vector3(0f, 0.0345f, -0.0062f), new Vector3(0.0018f, 0.0070f, 0.0018f), pal.banner, new Vector3(-20f, 0f, 0f)); // hanging tassel
                    break;
                case ArchitectureStyle.Timber:
                    // Verdant: leather jerkin, fur mantle, banner sash cloak, leafy hood and antler/leaf crest
                    k.Cyl(new Vector3(0f, 0.0055f, 0f), 0.0086f, 0.004f, Leather);                      // kilt
                    for (int i = 0; i < 6; i++)                                                         // leather tabs
                    {
                        float a = i * 60f * Mathf.Deg2Rad;
                        k.Box(new Vector3(Mathf.Sin(a) * 0.0085f, 0.0045f, Mathf.Cos(a) * 0.0085f), new Vector3(0.0035f, 0.0045f, 0.0012f), pal.dark, new Vector3(0f, i * 60f, 0f));
                    }
                    k.Box(new Vector3(0f, 0.0142f, 0.0076f), new Vector3(0.0050f, 0.0100f, 0.0014f), pal.banner);     // sash front
                    k.Box(new Vector3(0f, 0.0160f, -0.0070f), new Vector3(0.0100f, 0.0130f, 0.0018f), pal.banner);    // short cloak back
                    k.Ellipsoid(new Vector3(-0.0088f, 0.0176f, 0f), new Vector3(0.0072f, 0.0046f, 0.0072f), pal.trim);   // fur mantle
                    k.Ellipsoid(new Vector3(0.0088f, 0.0176f, 0f), new Vector3(0.0072f, 0.0046f, 0.0072f), pal.trim);
                    k.Cyl(new Vector3(0f, 0.0192f, 0f), 0.0062f, 0.0020f, pal.trim);                      // collar
                    k.Ellipsoid(new Vector3(0f, 0.0286f, -0.0008f), new Vector3(0.0128f, 0.0078f, 0.0128f), pal.main);   // hood/cap
                    k.Box(new Vector3(0f, 0.0338f, 0f), new Vector3(0.0022f, 0.0050f, 0.0100f), pal.banner);            // crest
                    k.Box(new Vector3(-0.0050f, 0.0345f, 0f), new Vector3(0.0014f, 0.0075f, 0.0014f), pal.dark, new Vector3(0f, 0f, 25f)); // antler tines
                    k.Box(new Vector3(0.0050f, 0.0345f, 0f), new Vector3(0.0014f, 0.0075f, 0.0014f), pal.dark, new Vector3(0f, 0f, -25f));
                    break;
                default:
                    // Iron: plate cuirass, mail skirt, banner surcoat with trim cross, steel pauldrons, great helm with visor and crest
                    k.Cyl(new Vector3(0f, 0.0040f, 0f), 0.0086f, 0.0050f, DarkSteel);                    // mail skirt
                    k.Box(new Vector3(0f, 0.0132f, 0.0081f), new Vector3(0.0100f, 0.0120f, 0.0016f), pal.banner);       // surcoat front
                    k.Box(new Vector3(0f, 0.0132f, -0.0081f), new Vector3(0.0100f, 0.0120f, 0.0016f), pal.banner);
                    k.Box(new Vector3(0f, 0.0140f, 0.0091f), new Vector3(0.0016f, 0.0090f, 0.0008f), pal.trim);         // cross
                    k.Box(new Vector3(0f, 0.0152f, 0.0091f), new Vector3(0.0060f, 0.0016f, 0.0008f), pal.trim);
                    k.Ellipsoid(new Vector3(-0.0090f, 0.0178f, 0f), new Vector3(0.0070f, 0.0058f, 0.0070f), Steel);     // pauldrons
                    k.Ellipsoid(new Vector3(0.0090f, 0.0178f, 0f), new Vector3(0.0070f, 0.0058f, 0.0070f), Steel);
                    k.Cyl(new Vector3(0f, 0.0192f, 0f), 0.0050f, 0.0020f, Steel);                         // gorget
                    k.Cyl(new Vector3(0f, 0.0228f, 0f), 0.0066f, 0.0084f, Steel);                         // great helm
                    k.Ellipsoid(new Vector3(0f, 0.0318f, 0f), new Vector3(0.0128f, 0.0060f, 0.0128f), Steel);
                    k.Box(new Vector3(0f, 0.0262f, 0.0064f), new Vector3(0.0075f, 0.0014f, 0.0012f), EyeCol);           // visor slit
                    k.Box(new Vector3(0f, 0.0240f, 0.0066f), new Vector3(0.0014f, 0.0040f, 0.0010f), DarkSteel);        // breath bar
                    k.Box(new Vector3(0f, 0.0355f, -0.0005f), new Vector3(0.0024f, 0.0050f, 0.0100f), pal.banner);      // crest
                    break;
            }
        }

        /// <summary>Builds a procedural miniature soldier wearing the civilization's armor style and banner colors.</summary>
        public static Transform BuildSoldierVisual(Transform parent, DefenderKind kind, ArchitectureStyle style, BuildingVisuals.Palette pal, out Transform weaponPivotOut)
        {
            var root = BuildingVisuals.NewRoot(parent, kind.ToString());
            root.localScale = Vector3.one * UnitScale.Soldier(kind);
            root.localPosition = new Vector3(0f, UnitScale.GroundOffset, 0f);
            var k = new BuildingVisuals.Kit(root);

            BuildBody(k, style, pal, false);
            BuildArmorAndHelm(k, style, pal, false);

            // Left arm (shield arm): upper arm + hand
            Color sleeve = style == ArchitectureStyle.StoneTowers ? Steel : (style == ArchitectureStyle.Timber ? Leather : pal.main);
            k.CylRot(new Vector3(-0.0092f, 0.0152f, 0.0020f), 0.0020f, 0.0070f, sleeve, new Vector3(-42f, 0f, 0f));
            k.Ball(new Vector3(-0.0096f, 0.0125f, 0.0045f), 0.0032f, style == ArchitectureStyle.StoneTowers ? DarkSteel : Skin);

            // Shield on left arm (-X), civ motif
            bool spear = kind == DefenderKind.Spearman;
            switch (style)
            {
                case ArchitectureStyle.TieredRoofs:
                    // round lacquered shield, gold rim, sun boss, banner ring
                    k.CylRot(new Vector3(-0.0112f, 0.0135f, 0.0050f), spear ? 0.0072f : 0.0066f, 0.0020f, pal.trim, new Vector3(0f, 0f, 90f));
                    k.CylRot(new Vector3(-0.0120f, 0.0135f, 0.0050f), spear ? 0.0058f : 0.0053f, 0.0016f, pal.banner, new Vector3(0f, 0f, 90f));
                    k.Ball(new Vector3(-0.0128f, 0.0135f, 0.0050f), 0.0034f, Gold);
                    break;
                case ArchitectureStyle.Timber:
                    // wooden round shield with plank ribs, banner center, leaf boss
                    k.CylRot(new Vector3(-0.0112f, 0.0135f, 0.0050f), spear ? 0.0072f : 0.0066f, 0.0020f, Wood, new Vector3(0f, 0f, 90f));
                    k.Box(new Vector3(-0.0123f, 0.0135f, 0.0050f), new Vector3(0.0010f, 0.0030f, 0.0110f), pal.banner);
                    k.Box(new Vector3(-0.0123f, 0.0135f, 0.0050f), new Vector3(0.0010f, 0.0110f, 0.0030f), pal.banner);
                    k.Ball(new Vector3(-0.0127f, 0.0135f, 0.0050f), 0.0030f, pal.trim);
                    break;
                default:
                    // heater shield: steel rim, banner field, trim cross
                    k.Box(new Vector3(-0.0112f, 0.0135f, 0.0050f), new Vector3(0.0022f, 0.0150f, 0.0105f), Steel, new Vector3(0f, -12f, 0f));
                    k.Box(new Vector3(-0.0122f, 0.0135f, 0.0052f), new Vector3(0.0014f, 0.0128f, 0.0082f), pal.banner, new Vector3(0f, -12f, 0f));
                    k.Box(new Vector3(-0.0129f, 0.0135f, 0.0053f), new Vector3(0.0010f, 0.0100f, 0.0016f), pal.trim, new Vector3(0f, -12f, 0f));
                    k.Box(new Vector3(-0.0129f, 0.0145f, 0.0053f), new Vector3(0.0010f, 0.0016f, 0.0066f), pal.trim, new Vector3(0f, -12f, 0f));
                    k.Ellipsoid(new Vector3(-0.0112f, 0.0070f, 0.0040f), new Vector3(0.0020f, 0.0030f, 0.0050f), Steel); // tapered tip
                    break;
            }
            CombineByMaterial(root);

            // Weapon pivot on right hand (+X) for animated melee strikes (includes sleeve + hand so the arm swings with it)
            var wp = BuildingVisuals.NewRoot(root, "WeaponPivot");
            wp.localPosition = new Vector3(0.0095f, 0.013f, 0.003f);
            var wk = new BuildingVisuals.Kit(wp);
            wk.CylRot(new Vector3(-0.0004f, 0.0022f, -0.0017f), 0.0020f, 0.0058f, sleeve, new Vector3(-37f, 0f, 0f));
            wk.Ball(Vector3.zero, 0.0032f, style == ArchitectureStyle.StoneTowers ? DarkSteel : Skin);

            if (spear)
            {
                // spear shaft along the lean axis (68 deg), civ-specific head, tassel/feather/pennant
                Vector3 e = new Vector3(68f, 0f, 0f);
                wk.CylRot(new Vector3(0f, 0.0020f, 0.0080f), 0.0011f, 0.0400f, Wood, e);
                switch (style)
                {
                    case ArchitectureStyle.TieredRoofs:
                        wk.Cone(new Vector3(0f, 0.0090f, 0.0225f), 0.0022f, 0.0085f, Steel).transform.localRotation = Quaternion.Euler(e);
                        wk.Ball(new Vector3(0f, 0.0083f, 0.0205f), 0.0030f, pal.banner);                       // red tassel
                        wk.Box(new Vector3(0f, 0.0060f, 0.0182f), new Vector3(0.0010f, 0.0045f, 0.0012f), pal.banner, e);
                        break;
                    case ArchitectureStyle.Timber:
                        wk.Ellipsoid(new Vector3(0f, 0.0092f, 0.0228f), new Vector3(0.0034f, 0.0030f, 0.0100f), pal.trim).transform.localRotation = Quaternion.Euler(e); // leaf blade
                        wk.Box(new Vector3(0.0012f, 0.0058f, 0.0170f), new Vector3(0.0008f, 0.0050f, 0.0030f), pal.banner, new Vector3(68f, 0f, 20f)); // feather
                        break;
                    default:
                        wk.Cone(new Vector3(0f, 0.0088f, 0.0222f), 0.0030f, 0.0100f, Steel).transform.localRotation = Quaternion.Euler(e);
                        wk.Box(new Vector3(0f, 0.0075f, 0.0200f), new Vector3(0.0070f, 0.0012f, 0.0012f), DarkSteel, e); // lugs
                        wk.Box(new Vector3(0f, 0.0058f, 0.0182f), new Vector3(0.0012f, 0.0050f, 0.0070f), pal.banner);   // pennant
                        break;
                }
            }
            else
            {
                // sword: pommel, grip, crossguard, blade (shape per civ)
                Vector3 e = new Vector3(-45f, 0f, 0f);
                wk.Ball(new Vector3(0f, -0.0012f, -0.0014f), 0.0026f, style == ArchitectureStyle.TieredRoofs ? pal.banner : pal.trim);
                wk.CylRot(new Vector3(0f, 0f, 0.0020f), 0.0011f, 0.0060f, pal.dark, new Vector3(45f, 0f, 0f));
                switch (style)
                {
                    case ArchitectureStyle.TieredRoofs: // slim straight jian with gold guard + tassel
                        wk.Box(new Vector3(0f, 0.0028f, 0.0048f), new Vector3(0.0050f, 0.0014f, 0.0018f), Gold, new Vector3(45f, 0f, 0f));
                        wk.Box(new Vector3(0f, 0.0105f, 0.0125f), new Vector3(0.0014f, 0.0030f, 0.0200f), Steel, e);
                        wk.Box(new Vector3(0f, -0.0006f, -0.0030f), new Vector3(0.0010f, 0.0050f, 0.0010f), pal.banner);
                        break;
                    case ArchitectureStyle.Timber: // short broad leaf blade, bronze guard
                        wk.Box(new Vector3(0f, 0.0028f, 0.0048f), new Vector3(0.0050f, 0.0014f, 0.0018f), pal.trim, new Vector3(45f, 0f, 0f));
                        wk.Ellipsoid(new Vector3(0f, 0.0095f, 0.0115f), new Vector3(0.0034f, 0.0030f, 0.0150f), new Color(0.80f, 0.62f, 0.30f)).transform.localRotation = Quaternion.Euler(e);
                        break;
                    default: // longsword with wide crossguard and fuller
                        wk.Box(new Vector3(0f, 0.0028f, 0.0048f), new Vector3(0.0090f, 0.0016f, 0.0020f), DarkSteel, new Vector3(45f, 0f, 0f));
                        wk.Box(new Vector3(0f, 0.0108f, 0.0128f), new Vector3(0.0022f, 0.0034f, 0.0210f), Steel, e);
                        wk.Box(new Vector3(0f, 0.0110f, 0.0128f), new Vector3(0.0008f, 0.0036f, 0.0180f), DarkSteel, e);
                        break;
                }
            }
            CombineByMaterial(wp);

            weaponPivotOut = wp;
            return root;
        }

        /// <summary>Tower archer (UnitScale.ArcherMul), standing at 'at' on the tower platform: civ garb, hood/helm, quiver, bow with string.</summary>
        public static void BuildArcherVisual(Transform parent, Vector3 at, ArchitectureStyle style, BuildingVisuals.Palette pal)
        {
            var root = BuildingVisuals.NewRoot(parent, "Archer");
            root.localPosition = at;
            root.localScale = Vector3.one * UnitScale.ArcherMul;
            var k = new BuildingVisuals.Kit(root);
            BuildBody(k, style, pal, false);
            BuildArmorAndHelm(k, style, pal, true);
            Color sleeve = style == ArchitectureStyle.StoneTowers ? Steel : (style == ArchitectureStyle.Timber ? Leather : pal.main);
            k.Cyl(new Vector3(0.0072f, 0.0100f, -0.0030f), 0.0030f, 0.0100f, Leather);                 // hip quiver
            k.Box(new Vector3(0.0072f, 0.0205f, -0.0030f), new Vector3(0.0012f, 0.0035f, 0.0012f), pal.banner); // arrow fletch
            k.CylRot(new Vector3(-0.0090f, 0.0150f, 0.0030f), 0.0020f, 0.0075f, sleeve, new Vector3(-55f, 0f, 0f));
            k.CylRot(new Vector3(0.0092f, 0.0160f, 0.0020f), 0.0020f, 0.0075f, sleeve, new Vector3(-30f, 0f, 0f));
            // bow (vertical limb arc via 3 segments) + string
            Color bowC = style == ArchitectureStyle.StoneTowers ? DarkSteel : (style == ArchitectureStyle.Timber ? pal.trim : pal.dark);
            k.CylRot(new Vector3(-0.0098f, 0.0140f, 0.0085f), 0.0010f, 0.0100f, bowC, new Vector3(0f, 0f, 0f));
            k.CylRot(new Vector3(-0.0098f, 0.0200f, 0.0070f), 0.0009f, 0.0070f, bowC, new Vector3(-30f, 0f, 0f));
            k.CylRot(new Vector3(-0.0098f, 0.0080f, 0.0070f), 0.0009f, 0.0070f, bowC, new Vector3(30f, 0f, 0f));
            k.Box(new Vector3(-0.0098f, 0.0140f, 0.0048f), new Vector3(0.0003f, 0.0125f, 0.0003f), new Color(0.9f, 0.88f, 0.8f)); // string
            k.CylRot(new Vector3(-0.0098f, 0.0140f, 0.0065f), 0.0004f, 0.0110f, Wood, new Vector3(90f, 0f, 0f));     // nocked arrow
            CombineByMaterial(root);
        }

        ITableSurface CurrentSurface()
        {
            if (GameDirector.Instance != null && GameDirector.Instance.Surface != null)
                return GameDirector.Instance.Surface;
            return SettlementBuildArea.Instance != null ? SettlementBuildArea.Instance.Surface : null;
        }

        Vector3 SurfaceUp()
        {
            var s = CurrentSurface();
            return s != null ? s.Pose.up : Vector3.up;
        }

        void OnDied(Health h)
        {
            if (dying || !launched) return;
            Die();
        }

        void Die()
        {
            dying = true;
            if (VfxPool.Instance != null)
                VfxPool.Instance.Burst(AimPoint, lastBannerColor.a > 0f ? lastBannerColor : new Color(0.8f, 0.7f, 0.3f));
            ProceduralAudio.Fire(Sfx.Death, transform.position);
            if (SourceSpawner != null) SourceSpawner.NotifyDefenderDied(this);
            Killed?.Invoke(this);
        }

        void LateUpdate()
        {
            if (dying) ReturnToPool();
        }

        void ReturnToPool()
        {
            launched = false;
            dying = false;
            Target = null;
            SourceSpawner = null;
            Active.Remove(this);
            if (pool != null) pool.Release(this);
            else gameObject.SetActive(false);
        }

        void Update()
        {
            if (!launched || dying) return;

            var gd = GameDirector.Instance;
            if (gd != null && (gd.Phase == GamePhase.CivilizationSetup || gd.Phase == GamePhase.HeadquartersPlacement || gd.Phase == GamePhase.Defeat))
            {
                DieSilent();
                return;
            }

            if (SourceSpawner != null && !SourceSpawner.IsSpawnerAlive)
            {
                DieSilent();
                return;
            }

            float dt = Time.deltaTime;
            Vector3 pos = transform.position;
            Vector3 up = SurfaceUp();
            ITableSurface surface = CurrentSurface();

            // 1. Validate or acquire an enemy target within intercept range
            if (Target != null && (!Target.IsAlive || !IsMonsterReachable(Target, pos, up)))
                Target = null;

            if (Target == null)
                Target = FindBestTarget(pos, up);

            // 2. If we have an enemy target, march to intercept and engage in melee
            if (Target != null)
            {
                Vector3 toEnemy = Target.transform.position - pos;
                toEnemy -= Vector3.Dot(toEnemy, up) * up;
                float dist = toEnemy.magnitude;
                float engageDist = MeleeReach + Target.Radius * 0.65f;

                if (dist <= engageDist)
                {
                    // Hold enemy back at the defensive line so it cannot slip past the defender
                    HoldMonsterAtBay(Target, pos, up, engageDist, surface, dt);

                    Face(toEnemy, up, dt);
                    attackTimer -= dt;
                    if (attackTimer <= 0f)
                    {
                        attackTimer = AttackInterval;
                        Target.health.Damage(MeleeDps * AttackInterval);
                        ProceduralAudio.Fire(Sfx.Hit, Target.AimPoint);
                        if (VfxPool.Instance != null)
                            VfxPool.Instance.Burst( Vector3.Lerp(AimPoint, Target.AimPoint, 0.6f), new Color(1f, 0.88f, 0.45f));
                    }

                    // Mutual melee exchange: engaged monster strikes back periodically
                    counterTimer -= dt;
                    if (counterTimer <= 0f && Target != null && Target.IsAlive)
                    {
                        counterTimer = 1.0f;
                        float enemyDps = (Target.IsRam ? MonsterUnit.RamDps : MonsterUnit.NormalDps) * CounterDamageFactor;
                        health.Damage(enemyDps);
                    }

                    AnimateCombat(dt, 1f - Mathf.Clamp01(attackTimer / AttackInterval));
                    return;
                }

                // March toward enemy to intercept
                MoveToward(Target.transform.position, MoveSpeed, surface, up, dt);
                ResetWeaponPose(dt);
                return;
            }

            // 3. No nearby enemy: patrol around walls, towers, or home spawner
            ResetWeaponPose(dt);
            Vector3 toPatrol = PatrolWaypoint - pos;
            toPatrol -= Vector3.Dot(toPatrol, up) * up;
            if (toPatrol.sqrMagnitude <= 0.012f * 0.012f)
            {
                patrolWaitTimer -= dt;
                AnimateIdle(dt);
                if (patrolWaitTimer <= 0f)
                {
                    patrolWaitTimer = UnityEngine.Random.Range(1.4f, 2.8f);
                    PickPatrolWaypoint();
                }
            }
            else
            {
                MoveToward(PatrolWaypoint, PatrolSpeed, surface, up, dt);
            }
        }

        void DieSilent()
        {
            dying = true;
            if (SourceSpawner != null) SourceSpawner.NotifyDefenderDied(this);
        }

        bool IsMonsterReachable(MonsterUnit m, Vector3 myPos, Vector3 up)
        {
            if (m == null || !m.IsAlive) return false;
            Vector3 anchor = SourceSpawner != null ? SourceSpawner.transform.position : HomePosition;
            Vector3 diff = m.transform.position - anchor;
            float vDist = Mathf.Abs(Vector3.Dot(diff, up));
            if (vDist > 0.09f && !m.OnFinalSurface) return false;
            diff -= Vector3.Dot(diff, up) * up;
            return diff.sqrMagnitude <= (InterceptRange * 1.25f) * (InterceptRange * 1.25f);
        }

        MonsterUnit FindBestTarget(Vector3 myPos, Vector3 up)
        {
            var monsters = MonsterUnit.Active;
            if (monsters.Count == 0) return null;

            Vector3 anchor = SourceSpawner != null ? SourceSpawner.transform.position : HomePosition;
            Vector3 hallPos = TownHall.Current != null ? TownHall.Current.transform.position : anchor;
            float maxRangeSq = InterceptRange * InterceptRange;

            MonsterUnit best = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < monsters.Count; i++)
            {
                var m = monsters[i];
                if (m == null || !m.IsAlive) continue;

                Vector3 fromAnchor = m.transform.position - anchor;
                float vDist = Mathf.Abs(Vector3.Dot(fromAnchor, up));
                if (vDist > 0.08f && !m.OnFinalSurface) continue;

                fromAnchor -= Vector3.Dot(fromAnchor, up) * up;
                float anchorDistSq = fromAnchor.sqrMagnitude;

                Vector3 fromHall = m.transform.position - hallPos;
                fromHall -= Vector3.Dot(fromHall, up) * up;
                float hallDistSq = fromHall.sqrMagnitude;

                if (anchorDistSq > maxRangeSq && hallDistSq > maxRangeSq) continue;

                Vector3 fromMe = m.transform.position - myPos;
                fromMe -= Vector3.Dot(fromMe, up) * up;
                float myDist = fromMe.magnitude;

                // Count how many other defenders are already targeting this monster so defenders spread across threats
                int engagedCount = 0;
                for (int j = 0; j < Active.Count; j++)
                {
                    var other = Active[j];
                    if (other != null && other != this && other.IsAlive && other.Target == m)
                        engagedCount++;
                }

                float score = myDist + engagedCount * 0.06f + Mathf.Sqrt(hallDistSq) * 0.35f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = m;
                }
            }
            return best;
        }

        void HoldMonsterAtBay(MonsterUnit m, Vector3 myPos, Vector3 up, float holdDist, ITableSurface surface, float dt)
        {
            if (m == null || !m.IsAlive || !m.OnFinalSurface) return;
            Vector3 hallPos = TownHall.Current != null ? TownHall.Current.transform.position : HomePosition;
            Vector3 mPos = m.transform.position;
            Vector3 toMonster = mPos - myPos;
            toMonster -= Vector3.Dot(toMonster, up) * up;
            float d = toMonster.magnitude;

            // If the monster tries to push closer than holdDist or move past the defender toward the TownHall, hold it back
            Vector3 outward = d > 1e-5f ? (toMonster / d) : (myPos - hallPos).normalized;
            outward -= Vector3.Dot(outward, up) * up;
            if (outward.sqrMagnitude < 1e-6f) outward = transform.forward;
            else outward.Normalize();

            float minKeep = Mathf.Max(0.022f, holdDist * 0.82f);
            if (d < minKeep)
            {
                Vector3 pushed = myPos + outward * minKeep;
                if (surface != null)
                {
                    Vector3 proj = surface.ProjectOnto(pushed);
                    if (surface.IsPointSupported(proj))
                        m.transform.position = Vector3.Lerp(mPos, proj, Mathf.Min(1f, dt * 18f));
                }
                else
                {
                    pushed.y = mPos.y;
                    m.transform.position = Vector3.Lerp(mPos, pushed, Mathf.Min(1f, dt * 18f));
                }
            }
        }

        void PickPatrolWaypoint()
        {
            Vector3 anchor = SourceSpawner != null ? SourceSpawner.transform.position : HomePosition;
            Vector3 hallPos = TownHall.Current != null ? TownHall.Current.transform.position : anchor;
            Vector3 up = SurfaceUp();
            ITableSurface surface = CurrentSurface();

            // Look for a nearby Wall, ArcherTower, or Barricade to patrol along
            PlacedUnit bestDefense = null;
            float bestDistSq = 0.28f * 0.28f;
            var units = PlacedUnit.All;
            int offsetSeed = (patrolSlot * 3 + UnityEngine.Random.Range(0, 5));

            for (int i = 0; i < units.Count; i++)
            {
                var u = units[(i + offsetSeed) % units.Count];
                if (u == null || u.health == null || u.health.IsDead || u.IsEconomy) continue;
                if (u.type == UnitType.Wall || u.type == UnitType.ArcherTower || u.type == UnitType.Barricade)
                {
                    Vector3 diff = u.transform.position - anchor;
                    diff -= Vector3.Dot(diff, up) * up;
                    float sq = diff.sqrMagnitude;
                    if (sq <= bestDistSq)
                    {
                        bestDistSq = sq;
                        bestDefense = u;
                        break;
                    }
                }
            }

            Vector3 candidate;
            if (bestDefense != null)
            {
                // Stand slightly on the outer (enemy-facing) side of the wall/tower
                Vector3 outward = bestDefense.transform.position - hallPos;
                outward -= Vector3.Dot(outward, up) * up;
                if (outward.sqrMagnitude < 1e-5f) outward = bestDefense.transform.forward;
                else outward.Normalize();
                Vector3 side = Vector3.Cross(up, outward).normalized;
                float sideOffset = ((patrolSlot % 3) - 1) * 0.025f + UnityEngine.Random.Range(-0.015f, 0.015f);
                candidate = bestDefense.transform.position + outward * 0.036f + side * sideOffset;
            }
            else
            {
                // Orbit around the Barracks or TownHall
                float angle = (patrolSlot * 120f + UnityEngine.Random.Range(-45f, 45f)) * Mathf.Deg2Rad;
                float r = SourceSpawner != null && SourceSpawner.isTownHallGarrison
                    ? BuildingVisuals.HallFootprintRadius + 0.035f
                    : 0.065f;
                Vector3 right = Vector3.Cross(up, Vector3.forward);
                if (right.sqrMagnitude < 1e-4f) right = Vector3.right;
                right.Normalize();
                Vector3 fwd = Vector3.Cross(right, up).normalized;
                candidate = anchor + (right * Mathf.Cos(angle) + fwd * Mathf.Sin(angle)) * r;
            }

            if (surface != null)
            {
                Vector3 proj = surface.ProjectOnto(candidate);
                if (surface.IsPointSupported(proj))
                {
                    PatrolWaypoint = proj;
                    return;
                }
            }
            PatrolWaypoint = surface != null ? surface.ProjectOnto(anchor) : anchor;
        }

        void MoveToward(Vector3 goal, float speed, ITableSurface surface, Vector3 up, float dt)
        {
            Vector3 pos = transform.position;
            Vector3 seek = goal - pos;
            seek -= Vector3.Dot(seek, up) * up;
            float dist = seek.magnitude;
            if (dist < 1e-5f) return;
            Vector3 dir = seek / dist;

            // Friendly separation steering
            Vector3 sep = Vector3.zero;
            for (int i = 0; i < Active.Count; i++)
            {
                var o = Active[i];
                if (o == null || o == this || !o.IsAlive) continue;
                Vector3 diff = pos - o.transform.position;
                diff -= Vector3.Dot(diff, up) * up;
                float sq = diff.sqrMagnitude;
                if (sq < SeparationRadius * SeparationRadius && sq > 1e-10f)
                {
                    float d = Mathf.Sqrt(sq);
                    sep += (diff / d) * (1f - d / SeparationRadius);
                }
            }

            Vector3 vel = dir * speed + sep * (speed * 1.15f);
            Vector3 next = pos + vel * dt;
            if (surface != null)
            {
                Vector3 proj = surface.ProjectOnto(next);
                if (!surface.IsPointSupported(proj))
                    proj = surface.ProjectOnto(pos + dir * speed * dt);
                if (surface.IsPointSupported(proj))
                    next = proj;
                else
                    next = surface.ProjectOnto(pos);
            }

            transform.position = next;
            Face(dir, up, dt);
            AnimateWalk(dt, speed / MoveSpeed);
        }

        void Face(Vector3 dir, Vector3 up, float dt)
        {
            dir -= Vector3.Dot(dir, up) * up;
            if (dir.sqrMagnitude < 1e-8f) return;
            Quaternion want = Quaternion.LookRotation(dir.normalized, up);
            transform.rotation = Quaternion.Slerp(transform.rotation, want, Mathf.Min(1f, dt * 12f));
        }

        void AnimateWalk(float dt, float rate)
        {
            if (bobRoot == null) return;
            bobPhase += dt * 15f * Mathf.Max(0.4f, rate);
            float s = Mathf.Sin(bobPhase);
            bobRoot.localPosition = new Vector3(0f, Mathf.Abs(s) * 0.0042f, 0f);
            bobRoot.localRotation = Quaternion.Euler(4f, s * 3f, s * 6f); // forward lean + sway
        }

        void AnimateIdle(float dt)
        {
            if (bobRoot == null) return;
            bobPhase += dt * 3.5f;
            bobRoot.localPosition = new Vector3(0f, Mathf.Abs(Mathf.Sin(bobPhase)) * 0.0012f, 0f);
            bobRoot.localRotation = Quaternion.Lerp(bobRoot.localRotation, Quaternion.identity, Mathf.Min(1f, dt * 8f));
            if (weaponPivot != null)
                weaponPivot.localRotation = Quaternion.Slerp(weaponPivot.localRotation, Quaternion.Euler(Mathf.Sin(bobPhase * 0.5f) * 3f, 0f, 0f), Mathf.Min(1f, dt * 6f));
        }

        void AnimateCombat(float dt, float swingPhase01)
        {
            // wind-up (0..0.3) then strike (0.3..1) of the compressed attack window
            float f = Mathf.Clamp01(swingPhase01 * 2.2f);
            float wind = f < 0.3f ? Mathf.Sin(f / 0.3f * Mathf.PI * 0.5f) : 0f;
            float strike = f < 0.3f ? 0f : Mathf.Sin((f - 0.3f) / 0.7f * Mathf.PI);
            if (bobRoot != null)
            {
                bobPhase += dt * 10f;
                bobRoot.localPosition = new Vector3(0f, Mathf.Abs(Mathf.Sin(bobPhase)) * 0.0016f, strike * 0.0025f);
                bobRoot.localRotation = Quaternion.Euler(strike * 9f - wind * 4f, 0f, 0f); // lunge forward, lean back on wind-up
            }
            if (weaponPivot != null)
            {
                if (Kind == DefenderKind.Spearman)
                {
                    weaponPivot.localPosition = new Vector3(0.0095f, 0.013f, 0.003f + strike * 0.011f - wind * 0.004f);
                    weaponPivot.localRotation = Quaternion.Euler(strike * 14f - wind * 8f, 0f, 0f);
                }
                else
                {
                    weaponPivot.localPosition = new Vector3(0.0095f, 0.013f + wind * 0.003f, 0.003f + strike * 0.005f);
                    weaponPivot.localRotation = Quaternion.Euler(-wind * 50f + strike * 85f, -strike * 25f, wind * 10f);
                }
            }
        }

        void ResetWeaponPose(float dt)
        {
            if (weaponPivot == null) return;
            weaponPivot.localPosition = Vector3.Lerp(weaponPivot.localPosition, new Vector3(0.0095f, 0.013f, 0.003f), Mathf.Min(1f, dt * 10f));
            weaponPivot.localRotation = Quaternion.Slerp(weaponPivot.localRotation, Quaternion.identity, Mathf.Min(1f, dt * 10f));
        }
    }
}
