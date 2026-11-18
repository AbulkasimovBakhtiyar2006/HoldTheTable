using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace HoldTheTable
{
    public enum WarlordKind { Saboteur, Breaker, Shaman }

    /// <summary>
    /// Named goblin warlords with distinct AI, composed on top of pooled MonsterUnits (no edits to MonsterUnit/WaveSpawner).
    /// Saboteur flanks to the least defended economy building / claimed outpost, Breaker shield-bashes towers and walls
    /// with bonus damage, Shaman drops healing totems that towers and defenders can destroy.
    /// Self-bootstraps after scene load; resets on GameDirector.GameReset. Reward on death: timber + VFX/audio.
    /// </summary>
    public class WarlordAiPersonalityDirector : MonoBehaviour
    {
        public static WarlordAiPersonalityDirector Instance { get; private set; }
        static bool quitting;

        /// <summary>Wave schedule by 0-based wave index (cycles in endless): 0 Saboteur, 1 Breaker, 2 Shaman.</summary>
        public static readonly WarlordKind[] Schedule = { WarlordKind.Saboteur, WarlordKind.Breaker, WarlordKind.Shaman };

        public static readonly List<WarlordBehavior> Active = new List<WarlordBehavior>();
        public static event System.Action<WarlordBehavior> WarlordSpawned;
        public static event System.Action<WarlordBehavior> WarlordSlain;

        readonly List<Totem> totems = new List<Totem>();
        readonly Stack<Totem> totemPool = new Stack<Totem>();
        GameDirector hooked;
        float hookTimer;
        float totemTick;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Active.Clear(); WarlordSpawned = null; WarlordSlain = null; Instance = null; quitting = false; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap() { Ensure(); }

        public static WarlordAiPersonalityDirector Ensure()
        {
            if (Instance == null && !quitting && Application.isPlaying)
            {
                var ex = FindFirstObjectByType<WarlordAiPersonalityDirector>();
                Instance = ex != null ? ex : new GameObject("WarlordAiPersonalityDirector").AddComponent<WarlordAiPersonalityDirector>();
            }
            return Instance;
        }

        public static string NameFor(WarlordKind k)
        {
            switch (k)
            {
                case WarlordKind.Saboteur: return Strings.WarlordSaboteurName;
                case WarlordKind.Breaker: return Strings.WarlordBreakerName;
                default: return Strings.WarlordShamanName;
            }
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        void OnEnable() { MonsterUnit.Killed += OnMonsterKilled; }

        void OnDisable()
        {
            MonsterUnit.Killed -= OnMonsterKilled;
            Unhook();
        }

        void OnApplicationQuit() { quitting = true; }
        void OnDestroy() { if (Instance == this) Instance = null; }

        void Unhook()
        {
            if (hooked != null)
            {
                hooked.WaveStarted -= OnWaveStarted;
                hooked.GameReset -= ResetAll;
            }
            hooked = null;
        }

        void Update()
        {
            if (hooked == null || hooked != GameDirector.Instance)
            {
                hookTimer -= Time.deltaTime;
                if (hookTimer <= 0f)
                {
                    hookTimer = 0.5f;
                    Unhook();
                    var gd = GameDirector.Instance;
                    if (gd != null)
                    {
                        gd.WaveStarted += OnWaveStarted;
                        gd.GameReset += ResetAll;
                        hooked = gd;
                    }
                }
            }
            TickTotems(Time.deltaTime);
        }

        // ------------------------------------------------------------ spawning
        void OnWaveStarted(int waveIndex)
        {
            var gd = GameDirector.Instance;
            if (gd == null || gd.Hall == null) return;
            var kind = Schedule[Mathf.Abs(waveIndex) % Schedule.Length];
            SpawnWarlord(kind, waveIndex);
        }

        /// <summary>Spawns a warlord of the given kind (public so debug/stress tools can call it).</summary>
        public WarlordBehavior SpawnWarlord(WarlordKind kind, int waveIndex)
        {
            var gd = GameDirector.Instance;
            var ws = WaveSpawner.Instance;
            if (gd == null || gd.Hall == null || ws == null) return null;
            Vector3 hallPos = gd.Hall.transform.position;
            ITableSurface surface = gd.Surface;
            var pts = ws.SpawnPoints;
            Vector3 spawn = pts.Count > 0 ? pts[Random.Range(0, pts.Count)] : hallPos + Vector3.forward * 0.3f;
            float floorY = RoomCornerSpawns.Existing != null ? RoomCornerSpawns.Existing.FloorY : (surface != null ? surface.Height - 0.72f : 0f);

            object target = WarlordBehavior.PickTarget(kind, spawn, hallPos);
            Vector3 dest = WarlordBehavior.TargetPosition(target, hallPos);
            SplinePath path = WarlordBehavior.BuildPath(kind, spawn, dest, hallPos, surface, floorY, ws.RoomMode);
            if (path == null) return null;

            var m = MonsterUnit.Spawn(path, false);
            if (m == null) return null;
            float hpMul = 1f + 0.25f * (waveIndex / 3);
            var b = m.GetComponent<WarlordBehavior>();
            if (b == null) b = m.gameObject.AddComponent<WarlordBehavior>();
            b.Activate(m, kind, hpMul, target, this);
            Active.Add(b);
            if (VfxPool.Instance != null) VfxPool.Instance.Ring(spawn + Vector3.up * 0.01f, 0.24f);
            ProceduralAudio.Fire(Sfx.Horn, spawn);
            WarlordSpawned?.Invoke(b);
            return b;
        }

        void OnMonsterKilled(MonsterUnit m)
        {
            if (m == null) return;
            var b = m.GetComponent<WarlordBehavior>();
            if (b == null || !b.IsActive) return;
            b.OnWarlordKilled();
            Active.Remove(b);
            var gd = GameDirector.Instance;
            if (gd != null && gd.Phase != GamePhase.Victory && gd.Phase != GamePhase.Defeat) gd.AddTimber(b.RewardTimber);
            Vector3 p = m.AimPoint;
            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Ring(p, 0.2f);
                VfxPool.Instance.Burst(p + Vector3.up * 0.02f, new Color(1f, 0.85f, 0.25f));
            }
            ProceduralAudio.Fire(Sfx.Coin, p);
            ProceduralAudio.Fire(Sfx.Victory, p);
            WarlordSlain?.Invoke(b);
        }

        void ResetAll()
        {
            for (int i = Active.Count - 1; i >= 0; i--) if (Active[i] != null) Active[i].Cleanup();
            Active.Clear();
            for (int i = totems.Count - 1; i >= 0; i--) ReleaseTotem(totems[i], false);
            totems.Clear();
        }

        // ------------------------------------------------------------ totems
        public sealed class Totem
        {
            public GameObject go;
            public Transform pulse;
            public Health health;
            public float life, healT, owner;
            public WarlordBehavior shaman;
            public bool alive;
        }

        public const float TotemHealRadius = 0.12f;
        public const float TotemLife = 24f;
        public const float TotemHealPerSec = 2.5f;
        public const float TotemHp = 8f;
        public const float TowerRange = 0.28f;

        public Totem PlaceTotem(Vector3 pos, Vector3 up, WarlordBehavior shaman)
        {
            Totem t = totemPool.Count > 0 ? totemPool.Pop() : CreateTotem();
            t.go.transform.position = pos;
            t.go.transform.rotation = Quaternion.FromToRotation(Vector3.up, up);
            t.go.SetActive(true);
            t.health.Init(TotemHp);
            t.life = TotemLife;
            t.healT = 0f;
            t.shaman = shaman;
            t.alive = true;
            totems.Add(t);
            if (VfxPool.Instance != null) VfxPool.Instance.Ring(pos + up * 0.005f, TotemHealRadius);
            ProceduralAudio.Fire(Sfx.Place, pos);
            return t;
        }

        public int TotemCountFor(WarlordBehavior w)
        {
            int n = 0;
            for (int i = 0; i < totems.Count; i++) if (totems[i].shaman == w && totems[i].alive) n++;
            return n;
        }

        Totem CreateTotem()
        {
            var t = new Totem();
            t.go = new GameObject("WarlordTotem");
            t.go.transform.SetParent(transform, false);
            var k = new BuildingVisuals.Kit(t.go.transform);
            Color wood = new Color(0.36f, 0.24f, 0.12f), glow = new Color(0.35f, 1f, 0.55f), bone = new Color(0.9f, 0.88f, 0.78f);
            k.Cyl(Vector3.zero, 0.006f, 0.05f, wood);
            k.Ball(new Vector3(0f, 0.056f, 0f), 0.02f, bone);
            k.Ball(new Vector3(-0.0045f, 0.058f, 0.008f), 0.005f, new Color(0.05f, 0.05f, 0.05f));
            k.Ball(new Vector3(0.0045f, 0.058f, 0.008f), 0.005f, new Color(0.05f, 0.05f, 0.05f));
            k.Box(new Vector3(0f, 0.032f, 0f), new Vector3(0.026f, 0.003f, 0.003f), wood);
            var orb = k.Ball(new Vector3(0f, 0.075f, 0f), 0.012f, glow);
            orb.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(glow);
            t.pulse = orb.transform;
            t.health = t.go.AddComponent<Health>();
            t.health.Init(TotemHp);
            var tt = t;
            t.health.Died += h => ReleaseTotem(tt, true);
            t.go.SetActive(false);
            return t;
        }

        void ReleaseTotem(Totem t, bool destroyedFx)
        {
            if (t == null || !t.alive) return;
            t.alive = false;
            if (destroyedFx)
            {
                Vector3 p = t.go.transform.position + t.go.transform.up * 0.04f;
                if (VfxPool.Instance != null) VfxPool.Instance.Burst(p, new Color(0.35f, 1f, 0.55f));
                ProceduralAudio.Fire(Sfx.Death, p);
            }
            t.go.SetActive(false);
            totems.Remove(t);
            totemPool.Push(t);
        }

        void TickTotems(float dt)
        {
            if (totems.Count == 0) return;
            totemTick += dt;
            bool doHeal = false, doDamage = false;
            float step = totemTick;
            if (totemTick >= 0.5f) { totemTick = 0f; doHeal = true; doDamage = true; }
            for (int i = totems.Count - 1; i >= 0; i--)
            {
                var t = totems[i];
                if (!t.alive) continue;
                t.life -= dt;
                float s = 1f + Mathf.Sin(Time.time * 6f + i) * 0.2f;
                t.pulse.localScale = Vector3.one * 0.012f * s;
                if (t.life <= 0f) { ReleaseTotem(t, true); continue; }
                if (!doHeal) continue;
                Vector3 p = t.go.transform.position;
                float r2 = TotemHealRadius * TotemHealRadius;
                bool healedAny = false;
                var act = MonsterUnit.Active;
                for (int k = 0; k < act.Count; k++)
                {
                    var m = act[k];
                    if (m == null || !m.IsAlive || m.health == null || m.health.Fraction >= 0.999f) continue;
                    if ((m.transform.position - p).sqrMagnitude > r2) continue;
                    m.health.Heal(TotemHealPerSec * step);
                    healedAny = true;
                }
                if (healedAny && VfxPool.Instance != null && Random.value < 0.4f) VfxPool.Instance.Ring(p + Vector3.up * 0.01f, TotemHealRadius * 0.6f);
                if (!doDamage) continue;
                // defenders destroy totems: towers in range shoot them, barracks defenders stab them
                var units = PlacedUnit.All;
                for (int k = 0; k < units.Count; k++)
                {
                    var u = units[k];
                    if (u == null || u.type != UnitType.ArcherTower || u.health == null || u.health.IsDead) continue;
                    if ((u.transform.position - p).sqrMagnitude > TowerRange * TowerRange) continue;
                    t.health.Damage(1.5f * step);
                    if (t.health.IsDead) break;
                }
                if (!t.alive) continue;
                var defs = DefenderUnit.Active;
                for (int k = 0; k < defs.Count; k++)
                {
                    var d = defs[k];
                    if (d == null || d.health == null || d.health.IsDead) continue;
                    if ((d.transform.position - p).sqrMagnitude > 0.05f * 0.05f) continue;
                    t.health.Damage(2f * step);
                    if (t.health.IsDead) break;
                }
            }
        }

        // ============================================================ behavior
        /// <summary>Per-warlord AI attached to a pooled MonsterUnit. Cleans itself up when the monster leaves the pool.</summary>
        public class WarlordBehavior : MonoBehaviour
        {
            public WarlordKind Kind { get; private set; }
            public string DisplayName { get; private set; }
            public MonsterUnit Monster { get; private set; }
            public bool IsActive { get; private set; }
            public float RewardTimber => Kind == WarlordKind.Breaker ? 30f : Kind == WarlordKind.Shaman ? 25f : 20f;

            WarlordAiPersonalityDirector owner;
            object target;
            int serial;
            Transform ui, visual, barFill;
            TMP_Text label;
            Transform camT;
            float bashT, abilityT, tickT, hpFracShown = -1f, idleT, sabotageT;
            bool suppressHeal;
            float lastHp;

            const float BreakerBonusDps = 5f;
            const float SaboteurBonusDps = 3f;
            const float BarWidth = 0.05f;

            public void Activate(MonsterUnit m, WarlordKind kind, float hpMul, object tgt, WarlordAiPersonalityDirector dir)
            {
                Monster = m;
                Kind = kind;
                owner = dir;
                target = tgt;
                DisplayName = NameFor(kind);
                serial = m.Serial;
                IsActive = true;
                bashT = 0f; abilityT = kind == WarlordKind.Shaman ? 2.5f : 0f; tickT = 0f; idleT = 0f; sabotageT = 0f;
                float baseHp = kind == WarlordKind.Breaker ? 40f : kind == WarlordKind.Shaman ? 22f : 14f;
                m.health.Init(baseHp * hpMul);
                lastHp = m.health.Current;
                m.health.Damaged -= OnDamaged;
                m.health.Damaged += OnDamaged;
                BuildLook();
                BuildUi();
                camT = Camera.main != null ? Camera.main.transform : null;
                enabled = true;
            }

            // ---------------------------------------------------- targeting
            public static object PickTarget(WarlordKind kind, Vector3 spawn, Vector3 hallPos)
            {
                var units = PlacedUnit.All;
                if (kind == WarlordKind.Saboteur)
                {
                    PlacedUnit best = null; float bestScore = float.MaxValue;
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u == null || !u.IsEconomy || u.health == null || u.health.IsDead) continue;
                        float score = 0f;
                        for (int k = 0; k < units.Count; k++)
                        {
                            var o = units[k];
                            if (o == null || o == u || o.health == null || o.health.IsDead) continue;
                            if (o.type != UnitType.ArcherTower && o.type != UnitType.Wall) continue;
                            float d = (o.transform.position - u.transform.position).magnitude;
                            if (d < 0.3f) score += (o.type == UnitType.ArcherTower ? 2f : 1f) * (0.3f - d);
                        }
                        score += 0.05f * (u.transform.position - spawn).magnitude;
                        if (score < bestScore) { bestScore = score; best = u; }
                    }
                    if (best != null) return best;
                    var nodes = ResourceOutpostNode.All;
                    for (int i = 0; i < nodes.Count; i++) if (nodes[i] != null && nodes[i].IsClaimed) return nodes[i];
                    return null;
                }
                if (kind == WarlordKind.Breaker)
                {
                    PlacedUnit best = null; float bd = float.MaxValue;
                    for (int pass = 0; pass < 2 && best == null; pass++)
                    {
                        var want = pass == 0 ? UnitType.ArcherTower : UnitType.Wall;
                        for (int i = 0; i < units.Count; i++)
                        {
                            var u = units[i];
                            if (u == null || u.type != want || u.health == null || u.health.IsDead) continue;
                            float d = (u.transform.position - spawn).sqrMagnitude;
                            if (d < bd) { bd = d; best = u; }
                        }
                    }
                    return best;
                }
                return null; // shaman walks to the hall
            }

            public static Vector3 TargetPosition(object t, Vector3 hallPos)
            {
                var u = t as PlacedUnit; if (u != null) return u.transform.position;
                var n = t as ResourceOutpostNode; if (n != null) return n.Position;
                return hallPos;
            }

            public static SplinePath BuildPath(WarlordKind kind, Vector3 from, Vector3 to, Vector3 hallPos, ITableSurface s, float floorY, bool roomMode)
            {
                if (s == null) return null;
                if (roomMode) return SplinePath.BuildRoute(from, to, s, floorY);
                if (kind == WarlordKind.Saboteur)
                {
                    // flank: swing out sideways to the less defended side of the straight line before closing in
                    Vector3 up = s.Pose.up;
                    Vector3 dir = Vector3.ProjectOnPlane(to - from, up);
                    float len = dir.magnitude;
                    if (len > 0.2f)
                    {
                        Vector3 side = Vector3.Cross(up, dir / len);
                        Vector3 mid = (from + to) * 0.5f;
                        float sc = 0f;
                        var units = PlacedUnit.All;
                        for (int i = 0; i < units.Count; i++)
                        {
                            var u = units[i];
                            if (u == null || u.health == null || u.health.IsDead || (u.type != UnitType.Wall && u.type != UnitType.ArcherTower)) continue;
                            Vector3 r = Vector3.ProjectOnPlane(u.transform.position - mid, up);
                            if (r.magnitude < 0.4f) sc += Vector3.Dot(r, side) > 0f ? 1f : -1f;
                        }
                        float sign = sc > 0f ? -1f : 1f;
                        for (int attempt = 0; attempt < 2; attempt++)
                        {
                            Vector3 wp = s.ProjectOnto(mid + side * sign * Mathf.Min(0.3f, len * 0.4f));
                            if (s.IsPointSupported(wp))
                            {
                                var path = new SplinePath(s);
                                path.Add(s.ProjectOnto(from), PathSegment.Surface, Vector3.up);
                                path.AddSurfaceLeg(wp);
                                path.AddSurfaceLeg(to);
                                return path;
                            }
                            sign = -sign;
                        }
                    }
                }
                return SplinePath.Build(from, to, s);
            }

            // ---------------------------------------------------- visuals
            void BuildLook()
            {
                var bob = Monster.transform.Find("Bob");
                Transform parent = bob != null ? bob : Monster.transform;
                var gob = parent.Find("Goblin");
                if (gob != null) gob.gameObject.SetActive(false);
                if (visual != null) Destroy(visual.gameObject);
                visual = BuildingVisuals.NewRoot(parent, "WarlordLook_" + Kind);
                var k = new BuildingVisuals.Kit(visual);
                Color eye = Color.white, pupil = new Color(0.08f, 0.02f, 0.02f);
                switch (Kind)
                {
                    case WarlordKind.Saboteur:
                    {
                        Color skin = new Color(0.30f, 0.52f, 0.30f), cloak = new Color(0.16f, 0.10f, 0.26f), scarf = new Color(0.75f, 0.12f, 0.12f), steel = new Color(0.75f, 0.78f, 0.82f);
                        visual.localScale = Vector3.one * 1.35f;
                        k.Ellipsoid(new Vector3(0f, 0.016f, 0f), new Vector3(0.026f, 0.03f, 0.024f), cloak);
                        k.Cone(new Vector3(0f, 0.012f, -0.004f), 0.017f, 0.032f, cloak).transform.localRotation = Quaternion.Euler(-12f, 0f, 0f);
                        k.Ball(new Vector3(0f, 0.028f, 0.004f), 0.02f, skin);
                        k.Cone(new Vector3(0f, 0.032f, 0f), 0.014f, 0.014f, cloak);  // hood
                        k.Ball(new Vector3(-0.0045f, 0.029f, 0.012f), 0.006f, eye);
                        k.Ball(new Vector3(0.0045f, 0.029f, 0.012f), 0.006f, eye);
                        k.Ball(new Vector3(-0.0045f, 0.029f, 0.0152f), 0.003f, pupil);
                        k.Ball(new Vector3(0.0045f, 0.029f, 0.0152f), 0.003f, pupil);
                        k.Box(new Vector3(0f, 0.022f, 0.006f), new Vector3(0.02f, 0.005f, 0.012f), scarf);
                        k.Box(new Vector3(-0.016f, 0.015f, 0.012f), new Vector3(0.002f, 0.002f, 0.022f), steel);   // twin daggers
                        k.Box(new Vector3(0.016f, 0.015f, 0.012f), new Vector3(0.002f, 0.002f, 0.022f), steel);
                        break;
                    }
                    case WarlordKind.Breaker:
                    {
                        Color skin = new Color(0.40f, 0.55f, 0.22f), iron = new Color(0.34f, 0.34f, 0.38f), wood = new Color(0.45f, 0.29f, 0.14f), horn = new Color(0.9f, 0.85f, 0.7f);
                        visual.localScale = Vector3.one * 1.75f;
                        k.Ellipsoid(new Vector3(0f, 0.017f, 0f), new Vector3(0.036f, 0.032f, 0.032f), skin);
                        k.Ellipsoid(new Vector3(0f, 0.013f, 0.003f), new Vector3(0.03f, 0.02f, 0.024f), iron);        // breastplate
                        k.Ellipsoid(new Vector3(0f, 0.033f, 0f), new Vector3(0.024f, 0.012f, 0.024f), iron);          // helmet
                        k.Cone(new Vector3(-0.011f, 0.033f, 0f), 0.003f, 0.012f, horn).transform.localRotation = Quaternion.Euler(0f, 0f, 35f);
                        k.Cone(new Vector3(0.011f, 0.033f, 0f), 0.003f, 0.012f, horn).transform.localRotation = Quaternion.Euler(0f, 0f, -35f);
                        k.Ball(new Vector3(-0.0055f, 0.027f, 0.011f), 0.007f, eye);
                        k.Ball(new Vector3(0.0055f, 0.027f, 0.011f), 0.007f, eye);
                        k.Ball(new Vector3(-0.0055f, 0.027f, 0.0148f), 0.0035f, pupil);
                        k.Ball(new Vector3(0.0055f, 0.027f, 0.0148f), 0.0035f, pupil);
                        k.CylRot(new Vector3(0f, 0.016f, 0.024f), 0.016f, 0.004f, wood, new Vector3(90f, 0f, 0f));   // tower shield
                        k.Ball(new Vector3(0f, 0.016f, 0.0275f), 0.007f, iron);
                        break;
                    }
                    default:
                    {
                        Color skin = new Color(0.33f, 0.58f, 0.46f), robe = new Color(0.12f, 0.36f, 0.34f), bone = new Color(0.92f, 0.9f, 0.8f), glow = new Color(0.35f, 1f, 0.55f), wood = new Color(0.4f, 0.27f, 0.13f);
                        visual.localScale = Vector3.one * 1.45f;
                        k.Cone(new Vector3(0f, 0f, 0f), 0.018f, 0.032f, robe);
                        k.Ball(new Vector3(0f, 0.03f, 0.002f), 0.02f, skin);
                        k.Ball(new Vector3(0f, 0.031f, 0.0105f), 0.016f, bone);                                       // bone mask
                        k.Ball(new Vector3(-0.0045f, 0.032f, 0.0155f), 0.004f, pupil);
                        k.Ball(new Vector3(0.0045f, 0.032f, 0.0155f), 0.004f, pupil);
                        for (int i = -2; i <= 2; i++)                                                                  // feather crest
                            k.Cone(new Vector3(i * 0.004f, 0.04f, -0.002f), 0.0018f, 0.014f - Mathf.Abs(i) * 0.002f, i % 2 == 0 ? glow : robe).transform.localRotation = Quaternion.Euler(0f, 0f, -i * 12f);
                        k.CylRot(new Vector3(0.017f, 0.03f, 0.008f), 0.0016f, 0.06f, wood, Vector3.zero);            // staff
                        var orb = k.Ball(new Vector3(0.017f, 0.063f, 0.008f), 0.012f, glow);
                        orb.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(glow);
                        break;
                    }
                }
            }

            void BuildUi()
            {
                if (ui != null) Destroy(ui.gameObject);
                ui = BuildingVisuals.NewRoot(Monster.transform, "WarlordUi");
                ui.localPosition = new Vector3(0f, Kind == WarlordKind.Breaker ? 0.1f : 0.085f, 0f);
                // bar background + fill (unlit, shared mats)
                var bg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                bg.name = "BarBg";
                Destroy(bg.GetComponent<Collider>());
                bg.transform.SetParent(ui, false);
                bg.transform.localScale = new Vector3(BarWidth + 0.003f, 0.0066f, 0.001f);
                Paint(bg, Mats.Unlit(new Color(0.08f, 0.05f, 0.05f)));
                var fill = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fill.name = "BarFill";
                Destroy(fill.GetComponent<Collider>());
                fill.transform.SetParent(ui, false);
                fill.transform.localPosition = new Vector3(0f, 0f, -0.0008f);
                Color c = Kind == WarlordKind.Saboteur ? new Color(0.7f, 0.3f, 0.9f) : Kind == WarlordKind.Breaker ? new Color(0.95f, 0.45f, 0.15f) : new Color(0.3f, 0.95f, 0.5f);
                Paint(fill, Mats.Unlit(c));
                barFill = fill.transform;
                hpFracShown = -1f;
                UpdateBar();
                if (TMP_Settings.defaultFontAsset != null)
                {
                    var lg = new GameObject("Name");
                    lg.transform.SetParent(ui, false);
                    lg.transform.localPosition = new Vector3(0f, 0.0085f, 0f);
                    lg.transform.localScale = Vector3.one * 0.0012f;
                    var tmp = lg.AddComponent<TextMeshPro>();
                    tmp.font = TMP_Settings.defaultFontAsset;
                    tmp.text = DisplayName;
                    tmp.fontSize = 36f;
                    tmp.fontStyle = FontStyles.Bold;
                    tmp.alignment = TextAlignmentOptions.Center;
                    tmp.color = c;
                    tmp.textWrappingMode = TextWrappingModes.NoWrap;
                    label = tmp;
                }
            }

            static void Paint(GameObject go, Material m)
            {
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = m;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }

            void UpdateBar()
            {
                if (barFill == null || Monster == null || Monster.health == null) return;
                float f = Monster.health.Fraction;
                if (Mathf.Abs(f - hpFracShown) < 0.001f) return;
                hpFracShown = f;
                float w = Mathf.Max(0.0004f, BarWidth * f);
                barFill.localScale = new Vector3(w, 0.0046f, 0.001f);
                barFill.localPosition = new Vector3((w - BarWidth) * 0.5f, 0f, -0.0008f);
            }

            // ---------------------------------------------------- behavior loop
            void OnDamaged(Health h)
            {
                if (suppressHeal) { lastHp = h.Current; return; }
                if (Kind == WarlordKind.Breaker)
                {
                    // shield absorbs part of every hit
                    float delta = lastHp - h.Current;
                    if (delta > 0f && !h.IsDead) { suppressHeal = true; h.Heal(delta * 0.3f); suppressHeal = false; }
                }
                lastHp = h.Current;
            }

            void Update()
            {
                if (!IsActive || Monster == null) return;
                if (Monster.Serial != serial || !Monster.IsAlive)
                {
                    if (Monster.Serial != serial) Cleanup();
                    return;
                }
                float dt = Time.deltaTime;
                // billboard UI
                if (camT == null && Camera.main != null) camT = Camera.main.transform;
                if (ui != null && camT != null)
                {
                    Vector3 fwd = ui.position - camT.position;
                    if (fwd.sqrMagnitude > 1e-6f) ui.rotation = Quaternion.LookRotation(fwd, Vector3.up);
                }
                UpdateBar();

                tickT += dt;
                if (Kind == WarlordKind.Shaman) abilityT -= dt;
                if (tickT < 0.25f && !(Kind == WarlordKind.Shaman && abilityT <= 0f)) return;
                float step = tickT;
                tickT = 0f;

                switch (Kind)
                {
                    case WarlordKind.Saboteur: TickSaboteur(step); break;
                    case WarlordKind.Breaker: TickBreaker(step); break;
                    default: TickShaman(); break;
                }
                TickRetarget(step);
            }

            void TickSaboteur(float step)
            {
                Vector3 pos = Monster.transform.position;
                var units = PlacedUnit.All;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || !u.IsEconomy || u.health == null || u.health.IsDead) continue;
                    if (!u.Contacts(pos, 0.03f)) continue;
                    u.health.Damage(SaboteurBonusDps * step);
                    sabotageT += step;
                    if (sabotageT > 1.5f)
                    {
                        sabotageT = 0f;
                        if (VfxPool.Instance != null) VfxPool.Instance.Burst(u.transform.position + u.transform.up * 0.04f, new Color(0.7f, 0.3f, 0.9f));
                        ProceduralAudio.Fire(Sfx.Hit, u.transform.position);
                    }
                    break;
                }
                var nodes = ResourceOutpostNode.All;
                for (int i = 0; i < nodes.Count; i++)
                {
                    var n = nodes[i];
                    if (n == null || !n.IsClaimed) continue;
                    if ((n.Position - pos).sqrMagnitude > 0.06f * 0.06f) continue;
                    if (n.ClaimedUnit == null || n.ClaimedUnit.health == null || n.ClaimedUnit.health.IsDead) { n.Pillage(); }
                    else n.ClaimedUnit.health.Damage(SaboteurBonusDps * step);
                    break;
                }
            }

            void TickBreaker(float step)
            {
                Vector3 pos = Monster.transform.position;
                var units = PlacedUnit.All;
                PlacedUnit hit = null;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || u.health == null || u.health.IsDead) continue;
                    if (u.type != UnitType.Wall && u.type != UnitType.ArcherTower) continue;
                    if (u.Contacts(pos, 0.035f)) { hit = u; break; }
                }
                if (hit == null) { bashT = 0f; return; }
                hit.health.Damage(BreakerBonusDps * step);
                bashT += step;
                if (bashT >= 1.2f)
                {
                    bashT = 0f;
                    hit.health.Damage(6f); // shield bash slam
                    Vector3 p = Vector3.Lerp(pos, hit.transform.position, 0.6f) + Vector3.up * 0.02f;
                    if (VfxPool.Instance != null) { VfxPool.Instance.Ring(p, 0.08f); VfxPool.Instance.Burst(p, new Color(0.8f, 0.7f, 0.5f)); }
                    ProceduralAudio.Fire(Sfx.Thud, p);
                }
            }

            void TickShaman()
            {
                if (abilityT > 0f || owner == null) return;
                abilityT = 7f;
                if (owner.TotemCountFor(this) >= 2) return;
                Vector3 up = Vector3.up;
                var gd = GameDirector.Instance;
                if (Monster.OnFinalSurface && gd != null && gd.Surface != null) up = gd.Surface.Pose.up;
                owner.PlaceTotem(Monster.transform.position, up, this);
            }

            /// <summary>If the chosen target is gone and the monster has run out of path, reroute to the next best target / the hall.</summary>
            void TickRetarget(float step)
            {
                if (Kind == WarlordKind.Shaman) return;
                var path = Monster.Path;
                if (path == null || !Monster.OnFinalSurface) return;
                bool gone;
                if (target is PlacedUnit)
                {
                    var pu = (PlacedUnit)target;
                    gone = pu == null || pu.health == null || pu.health.IsDead;
                }
                else if (target is ResourceOutpostNode)
                {
                    var rn = (ResourceOutpostNode)target;
                    gone = rn == null || !rn.IsClaimed;
                }
                else return; // already heading to the hall
                if (!gone) return;
                Vector3 pos = Monster.transform.position;
                var gd = GameDirector.Instance;
                if (gd == null || gd.Hall == null || gd.Surface == null) return;
                object next = PickTarget(Kind, pos, gd.Hall.transform.position);
                Vector3 dest = TargetPosition(next, gd.Hall.transform.position);
                var np = SplinePath.Build(pos, dest, gd.Surface);
                if (np == null) return;
                float max = Monster.health.Max, frac = Monster.health.Fraction;
                Monster.Launch(np, MonsterKind.Normal); // resets HP/visuals; restore below
                suppressHeal = true;
                Monster.health.Init(max);
                Monster.health.Damage(max * (1f - frac));
                suppressHeal = false;
                lastHp = Monster.health.Current;
                serial = Monster.Serial;
                target = next;
                idleT = 0f;
                var bob = Monster.transform.Find("Bob");
                if (bob != null) { var g = bob.Find("Goblin"); if (g != null) g.gameObject.SetActive(false); }
            }

            public void OnWarlordKilled()
            {
                if (Kind == WarlordKind.Shaman && owner != null)
                    for (int i = 0; i < owner.totems.Count; i++)
                        if (owner.totems[i].shaman == this) owner.totems[i].shaman = null; // totems linger until expiry
                IsActive = false;
            }

            /// <summary>Remove all warlord visuals/state; called on restart, pool release and death cleanup.</summary>
            public void Cleanup()
            {
                IsActive = false;
                if (Monster != null && Monster.health != null) { Monster.health.Damaged -= OnDamaged; }
                if (visual != null) Destroy(visual.gameObject);
                if (ui != null) Destroy(ui.gameObject);
                visual = null; ui = null; barFill = null; label = null;
                Active.Remove(this);
                enabled = false;
            }

            void OnDisable()
            {
                // MonsterUnit returned to its pool (death / DespawnAll / restart)
                if (visual != null || ui != null) Cleanup();
            }
        }
    }
}
