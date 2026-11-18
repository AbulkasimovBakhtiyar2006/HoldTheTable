using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Physical wooden trophy stand with a velvet cloth, placed beside the ImperialScroll. Six miniature cups unlock from real
    /// achievements (ScoreKeeper / GameDirector / boss events), persisted in PlayerPrefs. Locked cups are dim ghosts; unlocked ones
    /// are gold with a glint sweep. Pinch/poke a cup to float its title. Own synthesized chimes (AudioDsp) for unlock/clink/glint.
    /// Self-creating (Ensure); optional manual wiring: add this component to any scene object.
    /// </summary>
    public class ImperialTrophiesAndAudioJuiceController : MonoBehaviour
    {
        public static ImperialTrophiesAndAudioJuiceController Instance { get; private set; }

        public enum Trophy { FirstBlood, Slayer, Builder, Victory, Unbroken, GiantKiller }

        const string PrefKey = "htt_trophies_v1";
        const int SlayerKills = 30;
        const int BuilderCount = 8;
        const int Count = 6;

        public float sideOffset = 0.5f;       // metres to the right of the scroll
        public float followSpeed = 4f;
        public float pokeRadius = 0.035f;
        public float pinchRadius = 0.07f;
        public float hoverHeight = 0.12f;     // stand legs float this far above the surface
        public float grabRadius = 0.07f;      // pinch must start within this distance of the stand body
        public float maxPlayerDistance = 1.2f;

        readonly bool[] unlocked = new bool[Count];
        readonly Transform[] cups = new Transform[Count];
        readonly Renderer[][] cupRenderers = new Renderer[Count][];
        readonly Transform[] glints = new Transform[Count];
        readonly float[] popT = new float[Count];
        readonly float[] glintT = new float[Count];
        readonly bool[] touching = new bool[Count];

        Transform stand;
        TextMeshPro titleLabel;
        float titleUntil;
        int titleCup = -1;
        AudioSource audioSrc;
        AudioClip clipUnlock, clipClink, clipGlint;
        bool poseInit;
        Vector3 lastScrollPos;

        // hover / drag state (anchor = logical stand position, hovering above the surface; bob is added on top)
        static Vector3 savedAnchor;      // persisted for the session
        static bool hasSaved;
        Vector3 anchor;
        Vector3 grabOffset;
        int dragHand = -1;
        float lostT;
        float liftT, hoverK, grabK;
        readonly bool[] pinchPrev = new bool[2];
        bool standHovered;
        Renderer glowDisc, glowRim;
        MaterialPropertyBlock glowMpb;
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly Color GlowIdle = new Color(0.35f, 0.8f, 1f, 1f);
        static readonly Color GlowHover = new Color(0.75f, 1f, 1f, 1f);
        static readonly Color GlowGrab = new Color(1f, 0.85f, 0.35f, 1f);

        // per-run tracking
        float minHallFrac = 1f;
        bool hallSeen;
        int maxBuildings;
        float pollT;
        GameDirector boundDirector;
        ScoreKeeper boundScore;
        bool bossSubscribed;
        readonly Queue<int> fanfare = new Queue<int>();
        float fanfareGap;

        static bool quitting;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate() { Ensure(); }

        /// <summary>Returns the singleton, creating it if missing.</summary>
        public static ImperialTrophiesAndAudioJuiceController Ensure()
        {
            if (Instance != null) return Instance;
            if (quitting || !Application.isPlaying) return null;
            var ex = FindFirstObjectByType<ImperialTrophiesAndAudioJuiceController>();
            if (ex != null) return ex;
            return new GameObject("ImperialTrophies").AddComponent<ImperialTrophiesAndAudioJuiceController>();
        }

        public bool IsUnlocked(Trophy t) => unlocked[(int)t];
        public int UnlockedCount { get { int n = 0; for (int i = 0; i < Count; i++) if (unlocked[i]) n++; return n; } }
        public event Action<Trophy> TrophyUnlocked;

        /// <summary>Unlock from external code (e.g. leader hooks). Returns true if newly unlocked.</summary>
        public bool Unlock(Trophy t)
        {
            int i = (int)t;
            if (unlocked[i]) return false;
            unlocked[i] = true;
            Save();
            ApplyVisual(i);
            popT[i] = 1f;
            fanfare.Enqueue(i);
            TrophyUnlocked?.Invoke(t);
            return true;
        }

        /// <summary>Clears saved trophies (debug / New Empire if desired).</summary>
        public void ResetAll()
        {
            for (int i = 0; i < Count; i++) { unlocked[i] = false; ApplyVisual(i); }
            Save();
        }

        // ---------------------------------------------------------------- lifecycle

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            Load();
            BuildAudio();
            BuildStand();
            for (int i = 0; i < Count; i++) ApplyVisual(i);
        }

        void OnEnable()
        {
            if (!bossSubscribed) { GoblinKingBoss.BossDefeated += OnBossDefeated; bossSubscribed = true; }
        }

        void OnDisable()
        {
            if (bossSubscribed) { GoblinKingBoss.BossDefeated -= OnBossDefeated; bossSubscribed = false; }
            Unbind();
        }

        void OnDestroy() { if (Instance == this) Instance = null; }
        void OnApplicationQuit() { quitting = true; }

        void Unbind()
        {
            if (boundDirector != null) { boundDirector.PhaseChanged -= OnPhase; boundDirector.GameReset -= OnReset; }
            if (boundScore != null) boundScore.Changed -= OnScore;
            boundDirector = null; boundScore = null;
        }

        void Bind()
        {
            var gd = GameDirector.Instance;
            if (gd != boundDirector)
            {
                if (boundDirector != null) { boundDirector.PhaseChanged -= OnPhase; boundDirector.GameReset -= OnReset; }
                boundDirector = gd;
                if (gd != null) { gd.PhaseChanged += OnPhase; gd.GameReset += OnReset; }
            }
            var sk = ScoreKeeper.Instance;
            if (sk != boundScore)
            {
                if (boundScore != null) boundScore.Changed -= OnScore;
                boundScore = sk;
                if (sk != null) sk.Changed += OnScore;
            }
        }

        // ---------------------------------------------------------------- achievements

        void OnReset() { minHallFrac = 1f; hallSeen = false; maxBuildings = 0; }

        void OnScore()
        {
            var sk = boundScore;
            if (sk == null) return;
            if (sk.Kills >= 1) Unlock(Trophy.FirstBlood);
            if (sk.Kills >= SlayerKills) Unlock(Trophy.Slayer);
        }

        void OnBossDefeated(GoblinKingBoss b) { Unlock(Trophy.GiantKiller); }

        void OnPhase(GamePhase p)
        {
            if (p == GamePhase.HeadquartersPlacement || p == GamePhase.CivilizationSetup) OnReset();
            if (p != GamePhase.Victory) return;
            PollRun();
            Unlock(Trophy.Victory);
            if (hallSeen && minHallFrac >= 0.999f) Unlock(Trophy.Unbroken);
        }

        void PollRun()
        {
            var gd = boundDirector;
            if (gd == null || !gd.IsCombatPhase && gd.Phase != GamePhase.Victory) return;
            if (gd.Hall != null && gd.Hall.health != null)
            {
                hallSeen = true;
                float f = gd.Hall.health.Fraction;
                if (f < minHallFrac) minHallFrac = f;
            }
            int n = PlacedUnit.All.Count;
            if (n > maxBuildings) maxBuildings = n;
            if (maxBuildings >= BuilderCount) Unlock(Trophy.Builder);
        }

        // ---------------------------------------------------------------- update

        void Update()
        {
            Bind();
            pollT -= Time.unscaledDeltaTime;
            if (pollT <= 0f) { pollT = 0.2f; PollRun(); }

            FollowScroll();
            AnimateCups();
            UpdatePokes();
            UpdateFanfare();

            if (titleLabel != null)
            {
                bool show = titleCup >= 0 && Time.unscaledTime < titleUntil;
                if (titleLabel.gameObject.activeSelf != show) titleLabel.gameObject.SetActive(show);
                if (show)
                {
                    var cam = Camera.main;
                    var t = titleLabel.transform;
                    t.position = cups[titleCup].position + Vector3.up * 0.1f;
                    if (cam != null) t.rotation = Quaternion.LookRotation(t.position - cam.transform.position, Vector3.up);
                }
            }
        }

        // The stand stays hidden during civilization setup / Town Hall placement and appears on the table beside the hall
        // once the empire is founded (never floating in the air near the scroll).
        void FollowScroll()
        {
            var gd = GameDirector.Instance;
            bool show = gd != null && gd.Hall != null && gd.Surface != null &&
                        (gd.Phase == GamePhase.BuildPhase || gd.Phase == GamePhase.Wave || gd.Phase == GamePhase.Upgrade ||
                         gd.Phase == GamePhase.Victory || gd.Phase == GamePhase.Defeat);
            if (stand.gameObject.activeSelf != show) stand.gameObject.SetActive(show);
            if (!show) { poseInit = false; EndDrag(false); return; }

            var cam = Camera.main;
            if (!poseInit)
            {
                ITableSurface surf = gd.Surface;
                Vector3 pos;
                if (hasSaved && SavedStillValid(cam)) pos = savedAnchor;
                else
                {
                    Vector3 hall = gd.Hall.transform.position;
                    Vector3 camRight = cam != null ? Vector3.ProjectOnPlane(cam.transform.right, Vector3.up).normalized : Vector3.right;
                    Vector3 edge = surf.ClosestEdgePoint(surf.ProjectOnto(hall + camRight * 0.7f));
                    Vector3 inward = hall - edge; inward.y = 0f;
                    pos = edge + (inward.sqrMagnitude > 1e-6f ? inward.normalized : Vector3.zero) * 0.07f;
                    pos = surf.ProjectOnto(pos) + Vector3.up * hoverHeight;
                }
                anchor = pos;
                stand.position = pos;
                stand.rotation = Quaternion.LookRotation(cam != null ? FlatDir(pos, cam.transform.position) : Vector3.forward, Vector3.up);
                dragHand = -1; liftT = 0f;
                pinchPrev[0] = pinchPrev[1] = true; // a pinch already held at spawn must not grab
                poseInit = true;
            }
            UpdateDrag(cam);
            ApplyHover(cam);
        }

        bool SavedStillValid(Camera cam)
        {
            if (cam != null)
            {
                Vector3 d = savedAnchor - cam.transform.position; d.y = 0f;
                if (d.magnitude > maxPlayerDistance + 0.1f) return false;
            }
            var reg = SurfaceRegistry.Instance;
            return reg != null && reg.TryFindSurfaceBelow(savedAnchor, 0.5f, out _, out _);
        }

        bool NearStand(Vector3 p)
        {
            Vector3 l = stand.InverseTransformPoint(p) - new Vector3(0f, 0.12f, 0f);
            Vector3 half = new Vector3(0.23f, 0.12f, 0.11f);
            Vector3 o = new Vector3(Mathf.Max(0f, Mathf.Abs(l.x) - half.x), Mathf.Max(0f, Mathf.Abs(l.y) - half.y), Mathf.Max(0f, Mathf.Abs(l.z) - half.z));
            return o.sqrMagnitude <= grabRadius * grabRadius;
        }

        void UpdateDrag(Camera cam)
        {
            float dt = Time.unscaledDeltaTime;
            var rig = HandRig.Instance;
            standHovered = false;
            if (rig != null)
            {
                if (dragHand < 0)
                {
                    for (int h = 0; h < 2; h++)
                    {
                        var side = (HandSide)h;
                        if (!rig.IsTracked(side) || !rig.TryGetIndexTip(side, out var tip)) { pinchPrev[h] = false; continue; }
                        bool pin = rig.IsPinching(side);
                        bool near = NearStand(tip);
                        if (near) standHovered = true;
                        if (pin && !pinchPrev[h] && near)
                        {
                            dragHand = h;
                            grabOffset = anchor - tip;
                            lostT = 0f;
                            PlayClip(clipClink, stand.position, 0.5f);
                            pinchPrev[h] = pin;
                            break;
                        }
                        pinchPrev[h] = pin;
                    }
                }
                else
                {
                    var side = (HandSide)dragHand;
                    Vector3 tip = default;
                    bool ok = rig.IsTracked(side) && rig.IsPinching(side) && rig.TryGetIndexTip(side, out tip);
                    if (ok)
                    {
                        lostT = 0f;
                        standHovered = true;
                        Vector3 goal = ResolveGoal(tip + grabOffset, cam);
                        anchor = Vector3.Lerp(anchor, goal, 1f - Mathf.Exp(-14f * dt));
                    }
                    else
                    {
                        lostT += dt;
                        if (lostT > 0.15f) EndDrag(true);
                    }
                }
            }
            else if (dragHand >= 0) EndDrag(true);

            liftT = Mathf.MoveTowards(liftT, dragHand >= 0 ? 1f : 0f, dt * 6f);
            hoverK = Mathf.MoveTowards(hoverK, standHovered ? 1f : 0f, dt * 6f);
            grabK = Mathf.MoveTowards(grabK, dragHand >= 0 ? 1f : 0f, dt * 8f);
        }

        // Clamp to the player's reach and keep hovering above the surface below (or keep current height if none).
        Vector3 ResolveGoal(Vector3 target, Camera cam)
        {
            if (cam != null)
            {
                Vector3 c = cam.transform.position;
                Vector3 d = target - c; d.y = 0f;
                float m = d.magnitude;
                if (m > maxPlayerDistance) { d *= maxPlayerDistance / m; target.x = c.x + d.x; target.z = c.z + d.z; }
            }
            float y = anchor.y;
            var reg = SurfaceRegistry.Instance;
            if (reg != null)
            {
                Vector3 probe = new Vector3(target.x, Mathf.Max(anchor.y, target.y) + 0.3f, target.z);
                if (reg.TryFindSurfaceBelow(probe, 1.5f, out _, out var hit)) y = hit.y + hoverHeight;
            }
            return new Vector3(target.x, y, target.z);
        }

        void EndDrag(bool save)
        {
            if (dragHand < 0) return;
            dragHand = -1;
            lostT = 0f;
            if (save) { savedAnchor = anchor; hasSaved = true; }
        }

        void ApplyHover(Camera cam)
        {
            float t = Time.unscaledTime;
            float dt = Time.unscaledDeltaTime;
            float bob = Mathf.Sin(t * 1.7f) * 0.008f * (1f - 0.6f * liftT);
            stand.position = anchor + Vector3.up * (bob + 0.03f * liftT);
            if (dragHand >= 0 && cam != null)
            {
                var want = Quaternion.LookRotation(FlatDir(anchor, cam.transform.position), Vector3.up);
                stand.rotation = Quaternion.Slerp(stand.rotation, want, 1f - Mathf.Exp(-4f * dt));
            }

            // slow holographic glow; brighter on hover, warm gold while grabbed
            if (glowDisc == null) return;
            float pulse = 0.5f + 0.5f * Mathf.Sin(t * 0.9f);
            Color c = Color.Lerp(GlowIdle, GlowHover, hoverK);
            c = Color.Lerp(c, GlowGrab, grabK);
            float boost = 1f + 0.6f * hoverK + 0.4f * grabK;
            if (glowMpb == null) glowMpb = new MaterialPropertyBlock();
            c.a = (0.14f + 0.14f * pulse) * boost;
            glowMpb.SetColor(BaseColorId, c);
            glowDisc.SetPropertyBlock(glowMpb);
            c.a = (0.2f + 0.2f * pulse) * boost;
            glowMpb.SetColor(BaseColorId, c);
            glowRim.SetPropertyBlock(glowMpb);
        }

        static Vector3 FlatDir(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from; d.y = 0f;
            return d.sqrMagnitude < 1e-4f ? Vector3.forward : d.normalized;
        }

        void AnimateCups()
        {
            float t = Time.unscaledTime;
            float dt = Time.unscaledDeltaTime;
            for (int i = 0; i < Count; i++)
            {
                var c = cups[i];
                if (c == null) continue;
                float pop = popT[i];
                if (pop > 0f) popT[i] = Mathf.Max(0f, pop - dt * 1.4f);
                float s = 1f + (unlocked[i] ? 0.35f * Mathf.Sin(pop * Mathf.PI) * pop : 0f);
                c.localScale = Vector3.one * s;
                if (unlocked[i])
                {
                    c.localRotation = Quaternion.Euler(0f, t * 25f + i * 40f, 0f);
                    // glint: bright spark orbiting the rim, periodic flash
                    glintT[i] += dt;
                    float period = 3.2f + i * 0.35f;
                    float u = (glintT[i] % period) / period;
                    var g = glints[i];
                    if (g != null)
                    {
                        float flash = u < 0.18f ? Mathf.Sin(u / 0.18f * Mathf.PI) : 0f;
                        g.gameObject.SetActive(flash > 0.02f);
                        float a = u / 0.18f * Mathf.PI;
                        g.localPosition = new Vector3(Mathf.Cos(a) * 0.028f, 0.052f + 0.018f * Mathf.Sin(a * 0.5f), Mathf.Sin(a) * 0.028f);
                        g.localScale = Vector3.one * (0.004f + 0.014f * flash);
                        if (flash > 0.9f && Mathf.Abs(u - 0.09f) < 0.01f) PlayClip(clipGlint, c.position, 0.25f);
                    }
                }
                else
                {
                    c.localRotation = Quaternion.identity;
                    if (glints[i] != null && glints[i].gameObject.activeSelf) glints[i].gameObject.SetActive(false);
                }
            }
        }

        void UpdatePokes()
        {
            var rig = HandRig.Instance;
            if (rig == null) return;
            if (dragHand >= 0 || !stand.gameObject.activeSelf)
            {
                // no cup titles while the stand is being dragged
                for (int i = 0; i < Count; i++) touching[i] = true;
                return;
            }
            for (int i = 0; i < Count; i++)
            {
                bool hit = false;
                Vector3 cp = cups[i].position + Vector3.up * 0.03f;
                for (int h = 0; h < 2 && !hit; h++)
                {
                    var side = (HandSide)h;
                    if (!rig.IsTracked(side) || !rig.TryGetIndexTip(side, out var tip)) continue;
                    float d = Vector3.Distance(tip, cp);
                    if (d < pokeRadius) hit = true;
                }
                if (hit && !touching[i])
                {
                    ShowTitle(i);
                    PlayClip(clipClink, cups[i].position, unlocked[i] ? 0.8f : 0.4f);
                }
                touching[i] = hit;
            }
        }

        void ShowTitle(int i)
        {
            if (titleLabel == null) return;
            titleCup = i;
            titleUntil = Time.unscaledTime + 3.5f;
            titleLabel.text = unlocked[i]
                ? "<color=#FFD24A>" + TitleOf(i) + "</color>\n<size=70%>" + DescOf(i) + "</size>"
                : "<color=#AAAAAA>" + Strings.TrophyLocked + "</color>\n<size=70%>" + DescOf(i) + "</size>";
        }

        void UpdateFanfare()
        {
            fanfareGap -= Time.unscaledDeltaTime;
            if (fanfare.Count == 0 || fanfareGap > 0f) return;
            int i = fanfare.Dequeue();
            fanfareGap = 1.2f;
            PlayClip(clipUnlock, cups[i].position, 1f);
            ShowTitle(i);
            titleUntil = Time.unscaledTime + 4.5f;
            glintT[i] = 0f;
        }

        // ---------------------------------------------------------------- text

        static string TitleOf(int i)
        {
            switch ((Trophy)i)
            {
                case Trophy.FirstBlood: return Strings.TrophyFirstBlood;
                case Trophy.Slayer: return Strings.TrophySlayer;
                case Trophy.Builder: return Strings.TrophyBuilder;
                case Trophy.Victory: return Strings.TrophyVictory;
                case Trophy.Unbroken: return Strings.TrophyUnbroken;
                default: return Strings.TrophyGiantKiller;
            }
        }

        static string DescOf(int i)
        {
            switch ((Trophy)i)
            {
                case Trophy.FirstBlood: return Strings.TrophyFirstBloodDesc;
                case Trophy.Slayer: return Strings.Format(Strings.TrophySlayerDesc, SlayerKills);
                case Trophy.Builder: return Strings.Format(Strings.TrophyBuilderDesc, BuilderCount);
                case Trophy.Victory: return Strings.TrophyVictoryDesc;
                case Trophy.Unbroken: return Strings.TrophyUnbrokenDesc;
                default: return Strings.TrophyGiantKillerDesc;
            }
        }

        // ---------------------------------------------------------------- persistence

        void Load()
        {
            try
            {
                string s = PlayerPrefs.GetString(PrefKey, "");
                if (string.IsNullOrEmpty(s)) return;
                foreach (var part in s.Split(','))
                    if (int.TryParse(part, out int v) && v >= 0 && v < Count) unlocked[v] = true;
            }
            catch (Exception e) { Debug.LogWarning("[Trophies] load failed: " + e.Message); }
        }

        void Save()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < Count; i++) if (unlocked[i]) { if (sb.Length > 0) sb.Append(','); sb.Append(i); }
                PlayerPrefs.SetString(PrefKey, sb.ToString());
                PlayerPrefs.Save();
            }
            catch (Exception e) { Debug.LogWarning("[Trophies] save failed: " + e.Message); }
        }

        // ---------------------------------------------------------------- geometry

        static readonly Color Wood = new Color(0.36f, 0.22f, 0.11f);
        static readonly Color WoodDark = new Color(0.24f, 0.14f, 0.07f);
        static readonly Color Velvet = new Color(0.45f, 0.06f, 0.12f);
        static readonly Color Gold = new Color(1f, 0.78f, 0.22f);
        static readonly Color GhostCol = new Color(0.55f, 0.58f, 0.65f, 0.28f);

        static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 lp, Vector3 ls, Material m)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = lp;
            go.transform.localScale = ls;
            go.GetComponent<Renderer>().sharedMaterial = m;
            return go;
        }

        void BuildStand()
        {
            stand = new GameObject("TrophyStand").transform;
            stand.SetParent(transform, false);
            var wood = Mats.Lit(Wood);
            var dark = Mats.Lit(WoodDark);
            var velvet = Mats.Lit(Velvet);
            var trim = Mats.LitMetallic(Gold, 0.5f);

            const float w = 0.46f, d = 0.2f;
            // legs
            float[] lx = { -w * 0.5f + 0.02f, w * 0.5f - 0.02f };
            float[] lz = { -d * 0.5f + 0.02f, d * 0.5f - 0.02f };
            for (int a = 0; a < 2; a++) for (int b = 0; b < 2; b++)
                Prim(PrimitiveType.Cube, "Leg", stand, new Vector3(lx[a], 0.05f, lz[b]), new Vector3(0.025f, 0.1f, 0.025f), dark);
            // frame + velvet shelf
            Prim(PrimitiveType.Cube, "Frame", stand, new Vector3(0f, 0.105f, 0f), new Vector3(w, 0.015f, d), wood);
            Prim(PrimitiveType.Cube, "Velvet", stand, new Vector3(0f, 0.116f, 0f), new Vector3(w - 0.03f, 0.008f, d - 0.03f), velvet);
            // gold trim front
            Prim(PrimitiveType.Cube, "Trim", stand, new Vector3(0f, 0.112f, -d * 0.5f - 0.001f), new Vector3(w, 0.006f, 0.004f), trim);
            // back rail (tilted backboard)
            var back = Prim(PrimitiveType.Cube, "Back", stand, new Vector3(0f, 0.17f, d * 0.5f - 0.005f), new Vector3(w, 0.11f, 0.01f), dark);
            back.transform.localRotation = Quaternion.Euler(-8f, 0f, 0f);
            Prim(PrimitiveType.Cube, "BackVelvet", stand, new Vector3(0f, 0.17f, d * 0.5f - 0.012f), new Vector3(w - 0.04f, 0.09f, 0.004f), velvet)
                .transform.localRotation = Quaternion.Euler(-8f, 0f, 0f);

            // cups in 2 rows of 3
            for (int i = 0; i < Count; i++)
            {
                int col = i % 3, row = i / 3;
                var slot = new GameObject("CupSlot_" + i).transform;
                slot.SetParent(stand, false);
                slot.localPosition = new Vector3((col - 1) * 0.14f, 0.12f, row == 0 ? -0.04f : 0.04f);
                var cup = BuildCup(i, slot);
                cups[i] = cup;
            }

            // hologram pedestal glow: a soft disc projected under the stand + a thin rim around the shelf (shadow-free)
            var disc = Prim(PrimitiveType.Cylinder, "HoloDisc", stand, new Vector3(0f, -hoverHeight + 0.004f, 0f), new Vector3(0.56f, 0.001f, 0.34f), Mats.Transparent(new Color(0.35f, 0.8f, 1f, 0.2f)));
            var rim = Prim(PrimitiveType.Cube, "HoloRim", stand, new Vector3(0f, 0.105f, 0f), new Vector3(w + 0.02f, 0.02f, d + 0.02f), Mats.Transparent(new Color(0.35f, 0.8f, 1f, 0.25f)));
            glowDisc = disc.GetComponent<Renderer>();
            glowRim = rim.GetComponent<Renderer>();
            var allR = stand.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < allR.Length; r++)
            {
                allR[r].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                allR[r].receiveShadows = false;
            }

            // floating title label
            var lg = new GameObject("TrophyTitle");
            lg.transform.SetParent(transform, false);
            titleLabel = lg.AddComponent<TextMeshPro>();
            titleLabel.fontSize = 0.22f;
            titleLabel.alignment = TextAlignmentOptions.Center;
            titleLabel.rectTransform.sizeDelta = new Vector2(0.4f, 0.12f);
            titleLabel.textWrappingMode = TextWrappingModes.Normal;
            titleLabel.richText = true;
            lg.SetActive(false);
        }

        Transform BuildCup(int idx, Transform slot)
        {
            var root = new GameObject("Cup").transform;
            root.SetParent(slot, false);
            var rs = new List<Renderer>();
            var m = Mats.LitMetallic(Gold, 0.7f);
            void Add(GameObject g) { rs.Add(g.GetComponent<Renderer>()); }

            Add(Prim(PrimitiveType.Cylinder, "Foot", root, new Vector3(0f, 0.004f, 0f), new Vector3(0.05f, 0.004f, 0.05f), m));
            Add(Prim(PrimitiveType.Cylinder, "Stem", root, new Vector3(0f, 0.02f, 0f), new Vector3(0.01f, 0.014f, 0.01f), m));
            Add(Prim(PrimitiveType.Sphere, "Knob", root, new Vector3(0f, 0.022f, 0f), new Vector3(0.022f, 0.014f, 0.022f), m));
            Add(Prim(PrimitiveType.Sphere, "Bowl", root, new Vector3(0f, 0.048f, 0f), new Vector3(0.05f, 0.05f, 0.05f), m));
            Add(Prim(PrimitiveType.Cylinder, "Rim", root, new Vector3(0f, 0.068f, 0f), new Vector3(0.048f, 0.003f, 0.048f), m));
            Add(Prim(PrimitiveType.Sphere, "HandleL", root, new Vector3(-0.032f, 0.052f, 0f), new Vector3(0.012f, 0.022f, 0.006f), m));
            Add(Prim(PrimitiveType.Sphere, "HandleR", root, new Vector3(0.032f, 0.052f, 0f), new Vector3(0.012f, 0.022f, 0.006f), m));
            // per-trophy crest on the bowl so each cup reads differently
            switch ((Trophy)idx)
            {
                case Trophy.FirstBlood: Add(Prim(PrimitiveType.Cube, "Crest", root, new Vector3(0f, 0.05f, -0.025f), new Vector3(0.006f, 0.03f, 0.004f), m)); break;
                case Trophy.Slayer: Add(Prim(PrimitiveType.Cube, "CrestA", root, new Vector3(0f, 0.05f, -0.025f), new Vector3(0.026f, 0.005f, 0.004f), m)); break;
                case Trophy.Builder: Add(Prim(PrimitiveType.Cube, "Crest", root, new Vector3(0f, 0.05f, -0.025f), new Vector3(0.014f, 0.014f, 0.004f), m)); break;
                case Trophy.Victory: Add(Prim(PrimitiveType.Sphere, "Crest", root, new Vector3(0f, 0.082f, 0f), new Vector3(0.016f, 0.016f, 0.016f), m)); break;
                case Trophy.Unbroken: Add(Prim(PrimitiveType.Cylinder, "Crest", root, new Vector3(0f, 0.05f, -0.025f), new Vector3(0.016f, 0.002f, 0.016f), m).transform.gameObject); break;
                default: Add(Prim(PrimitiveType.Cube, "Crest", root, new Vector3(0f, 0.084f, 0f), new Vector3(0.006f, 0.02f, 0.006f), m)); break;
            }
            cupRenderers[idx] = rs.ToArray();

            var gl = Prim(PrimitiveType.Sphere, "Glint", root, new Vector3(0.02f, 0.06f, 0f), Vector3.one * 0.004f, Mats.Unlit(new Color(1f, 0.97f, 0.8f)));
            gl.SetActive(false);
            glints[idx] = gl.transform;
            return root;
        }

        void ApplyVisual(int i)
        {
            var rs = cupRenderers[i];
            if (rs == null) return;
            var m = unlocked[i] ? Mats.LitMetallic(Gold, 0.7f) : Mats.Transparent(GhostCol);
            for (int k = 0; k < rs.Length; k++) if (rs[k] != null) rs[k].sharedMaterial = m;
        }

        // ---------------------------------------------------------------- audio

        void BuildAudio()
        {
            var go = new GameObject("TrophyAudio");
            go.transform.SetParent(transform, false);
            audioSrc = go.AddComponent<AudioSource>();
            audioSrc.playOnAwake = false;
            audioSrc.spatialBlend = 1f;
            audioSrc.rolloffMode = AudioRolloffMode.Linear;
            audioSrc.minDistance = 0.3f;
            audioSrc.maxDistance = 6f;
            audioSrc.dopplerLevel = 0f;

            // fanfare: rising bell arpeggio C6 E6 G6 C7 with shimmer
            float[] d = AudioDsp.Buf(2.2f);
            float[] notes = { 1046.5f, 1318.5f, 1568f, 2093f };
            for (int n = 0; n < notes.Length; n++)
            {
                AudioDsp.Sine(d, n * 0.11f, notes[n], notes[n], 0.32f, 0.004f, 2.6f);
                AudioDsp.Sine(d, n * 0.11f, notes[n] * 2.76f, notes[n] * 2.76f, 0.08f, 0.002f, 6f);
            }
            AudioDsp.Sine(d, 0.5f, 523.25f, 523.25f, 0.25f, 0.01f, 1.6f);
            AudioDsp.Sine(d, 0.5f, 783.99f, 783.99f, 0.18f, 0.01f, 1.6f);
            AudioDsp.Normalize(d, 0.8f);
            clipUnlock = MakeClip("TrophyUnlock", d);

            // clink: two metallic partials
            d = AudioDsp.Buf(0.6f);
            AudioDsp.Sine(d, 0f, 2637f, 2637f, 0.35f, 0.001f, 9f);
            AudioDsp.Sine(d, 0f, 3951f, 3951f, 0.2f, 0.001f, 12f);
            AudioDsp.Sine(d, 0.03f, 1760f, 1760f, 0.18f, 0.001f, 7f);
            AudioDsp.Normalize(d, 0.7f);
            clipClink = MakeClip("TrophyClink", d);

            // glint: tiny high ping
            d = AudioDsp.Buf(0.5f);
            AudioDsp.Sine(d, 0f, 4186f, 4186f, 0.3f, 0.002f, 8f);
            AudioDsp.Sine(d, 0.05f, 5274f, 5274f, 0.15f, 0.002f, 10f);
            AudioDsp.Normalize(d, 0.5f);
            clipGlint = MakeClip("TrophyGlint", d);
        }

        static AudioClip MakeClip(string name, float[] data)
        {
            var c = AudioClip.Create(name, data.Length, 1, AudioDsp.Rate, false);
            c.SetData(data, 0);
            return c;
        }

        void PlayClip(AudioClip clip, Vector3 pos, float vol)
        {
            if (clip == null || audioSrc == null) return;
            float master = ProceduralAudio.Instance != null ? ProceduralAudio.Instance.volume : 0.7f;
            audioSrc.transform.position = pos;
            audioSrc.pitch = 1f;
            audioSrc.PlayOneShot(clip, master * vol);
        }
    }
}
