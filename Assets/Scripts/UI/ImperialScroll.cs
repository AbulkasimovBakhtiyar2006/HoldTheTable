using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HoldTheTable
{
    /// <summary>
    /// World-space Imperial Scroll beside the player (the scene root is made grabbable by the leader).
    /// Procedural visuals: warm curled parchment, two wooden rollers with gold caps, gold ornament border, red wax seal,
    /// and an unfurl animation. Pages: Civilization → Name (HandPokeKeyboard) → Banner (BannerCustomizer + EmblemDrawingCanvas)
    /// → Found Empire. After founding: placement hint + New Empire. All UI is built once in Awake and reused on reopen.
    /// </summary>
    public class ImperialScroll : MonoBehaviour
    {
        public static ImperialScroll Instance { get; private set; }

        [Header("Scene references (leader)")]
        public Canvas canvas;
        [Tooltip("Optional grab collider/handle; positioned on the top roller.")]
        public Transform grabHandle;

        [Header("Optional pre-placed UI components (created at runtime when empty)")]
        [OptionalRef] public CivilizationSelector civilizationSelector;
        [OptionalRef] public HandPokeKeyboard keyboard;
        [OptionalRef] public EmblemDrawingCanvas drawingCanvas;
        [OptionalRef] public BannerCustomizer bannerCustomizer;

        [Header("Behaviour")]
        public bool openOnStart = true; // GameDirector.Start also calls Open(); Open() is idempotent
        public float unfurlSeconds = 0.8f;
        public float openDistance = 0.45f;
        public float sideOffset = 0.25f;
        public float belowEye = 0.18f;

        public Transform TokenSocket => tokenSocket;
        public bool IsOpen { get; private set; }
        public int PageIndex => page;

        // page indices: 0-3 = setup pages (dots), then start / settings / end pages
        const int PageCiv = 0, PageName = 1, PageBanner = 2, PageFound = 3, PageStart = 4, PageSettings = 5, PageEnd = 6, PageCount = 7, SetupPageCount = 4;

        const float ParchW = 0.43f;
        const float ParchH = 0.53f;

        // visuals
        Transform visuals, parchment, rollerTop, rollerBottom, seal, tokenSocket;
        // ui
        RectTransform pagesRoot, bannerPanel;
        CanvasGroup pagesGroup;
        RectTransform[] pages;
        Button backButton, nextButton, foundButton, newEmpireButton;
        TMP_Text nameDisplay, summaryName, summaryCiv, foundedHint;
        Image[] stepDots;
        RawImage summaryBanner;
        Texture2D summaryTex;
        Button startButton, settingsButton, rightHandButton, leftHandButton, endRestartButton, endEndlessButton, endNewEmpireButton;
        TMP_Text endTitle, endScoreText, endWaveText;
        bool launchMenu = true;   // first launch: show the start page until the player presses Start Game
        bool endActive, endEndlessOffered;
        bool settingsSubscribed;
        int page;
        bool built, placed, founded, subscribed;
        Coroutine anim;

        void Awake()
        {
            Instance = this;
            if (canvas == null) canvas = GetComponentInChildren<Canvas>(true);
            BuildVisuals();
            BuildUi();
            BuildRolledIcon();
            SetVisible(false, true);
        }

        void Start()
        {
            TrySubscribe();
            if (openOnStart && !IsOpen)
            {
                var gd = GameDirector.Instance;
                if (gd == null || gd.Phase == GamePhase.CivilizationSetup) Open();
            }
        }

        void OnEnable()
        {
            if (!settingsSubscribed) { GameSettings.Changed += RefreshSettingsButtons; settingsSubscribed = true; }
        }

        void OnDisable()
        {
            if (settingsSubscribed) { GameSettings.Changed -= RefreshSettingsButtons; settingsSubscribed = false; }
        }

        void OnDestroy()
        {
            if (settingsSubscribed) { GameSettings.Changed -= RefreshSettingsButtons; settingsSubscribed = false; }
            if (subscribed && GameDirector.Instance != null) GameDirector.Instance.PhaseChanged -= OnPhaseChanged;
            if (Instance == this) Instance = null;
        }

        void TrySubscribe()
        {
            if (subscribed || GameDirector.Instance == null) return;
            GameDirector.Instance.PhaseChanged += OnPhaseChanged;
            subscribed = true;
        }

        void Update()
        {
            if (!subscribed) TrySubscribe();
            UpdateRolledIcon();
        }

        // ------------------------------------------------------------------ rolled-up scroll (collapsed state)

        Transform rolledIcon;
        float iconCooldown;
        const float IconTouchRadius = 0.07f;

        void BuildRolledIcon()
        {
            var root = new GameObject("RolledScrollIcon").transform;
            root.SetParent(transform, false);
            root.localPosition = Vector3.zero;
            Vector3 ls = transform.lossyScale;
            root.localScale = new Vector3(1f / Mathf.Max(1e-4f, ls.x), 1f / Mathf.Max(1e-4f, ls.y), 1f / Mathf.Max(1e-4f, ls.z));
            Color wood = new Color(0.40f, 0.22f, 0.09f);
            Color gold = new Color(0.90f, 0.70f, 0.25f);
            Color paper = new Color(0.98f, 0.90f, 0.68f);
            Prim(PrimitiveType.Cylinder, "Paper", root, Vector3.zero, new Vector3(0.05f, 0.07f, 0.05f), Quaternion.Euler(0f, 0f, 90f), paper);
            Prim(PrimitiveType.Cylinder, "Rod", root, Vector3.zero, new Vector3(0.018f, 0.09f, 0.018f), Quaternion.Euler(0f, 0f, 90f), wood);
            Prim(PrimitiveType.Sphere, "CapL", root, new Vector3(-0.092f, 0f, 0f), Vector3.one * 0.03f, Quaternion.identity, gold);
            Prim(PrimitiveType.Sphere, "CapR", root, new Vector3(0.092f, 0f, 0f), Vector3.one * 0.03f, Quaternion.identity, gold);
            Prim(PrimitiveType.Cylinder, "Seal", root, new Vector3(0f, 0f, -0.026f), new Vector3(0.022f, 0.003f, 0.022f), Quaternion.Euler(90f, 0f, 0f), new Color(0.62f, 0.06f, 0.07f));
            rolledIcon = root;
            root.gameObject.SetActive(false);
        }

        // Closed scroll = small rolled scroll floating where the scroll was; touch it with a fingertip to unroll again.
        void UpdateRolledIcon()
        {
            if (rolledIcon == null) return;
            bool show = built && !IsOpen && anim == null;
            if (rolledIcon.gameObject.activeSelf != show) rolledIcon.gameObject.SetActive(show);
            if (!show) return;
            Vector3 p = transform.position + Vector3.up * (0.02f * Mathf.Sin(Time.unscaledTime * 2f));
            rolledIcon.position = p;
            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 d = p - cam.transform.position; d.y = 0f;
                if (d.sqrMagnitude > 1e-4f) rolledIcon.rotation = Quaternion.LookRotation(d.normalized, Vector3.up);
            }
            if (iconCooldown > 0f) { iconCooldown -= Time.unscaledDeltaTime; return; }
            var hr = HandRig.Instance;
            if (hr == null) return;
            for (int h = 0; h < 2; h++)
            {
                var side = h == 0 ? HandSide.Left : HandSide.Right;
                if (hr.TryGetIndexTip(side, out var tip) && (tip - p).sqrMagnitude < IconTouchRadius * IconTouchRadius)
                {
                    iconCooldown = 0.8f;
                    Open();
                    return;
                }
            }
        }

        void OnPhaseChanged(GamePhase p)
        {
            if (p == GamePhase.CivilizationSetup)
            {
                founded = false;
                endActive = false;
                Open();
            }
            else if (p == GamePhase.HeadquartersPlacement || p == GamePhase.BuildPhase)
            {
                if (endActive) HideEnd();
                if (!founded) { founded = true; ShowPage(PageFound); }
                RefreshFoundedState();
            }
        }

        // ------------------------------------------------------------------ public API

        public void Open()
        {
            if (!built) return;
            var orbit = GetComponent<OrbitAnchor>();
            if (orbit != null) { orbit.Recenter(); placed = true; }
            else if (!placed) PlaceBesidePlayer();
            founded = GameDirector.Instance != null && GameDirector.Instance.Phase != GamePhase.CivilizationSetup;
            // load saved profile values into every page
            if (keyboard != null) keyboard.SetText(CivilizationProfile.Current.empireName);
            if (civilizationSelector != null) civilizationSelector.Refresh();
            int target = endActive ? PageEnd : (founded ? PageFound : (launchMenu ? PageStart : PageCiv));
            bool keepMenu = IsOpen && !endActive && !founded && launchMenu && (page == PageStart || page == PageSettings);
            if (!keepMenu) ShowPage(target);
            if (IsOpen) return; // already unfurled (e.g. GameDirector.Start + PhaseChanged): just reset the page
            SetVisible(true, false);
            IsOpen = true;
            if (anim != null) StopCoroutine(anim);
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.ScrollUnfurl, transform.position);
            if (isActiveAndEnabled) anim = StartCoroutine(Unfurl(true));
            else { SetRollerSpread(1f); pagesGroup.alpha = 1f; pagesGroup.interactable = pagesGroup.blocksRaycasts = true; }
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            if (anim != null) StopCoroutine(anim);
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.ScrollUnfurl, transform.position);
            if (isActiveAndEnabled) anim = StartCoroutine(Unfurl(false));
            else SetVisible(false, true);
        }

        /// <summary>Victory/Defeat result on the END page; unfurls the scroll near the player. Called by EndScreen.</summary>
        public void ShowEnd(bool victory, string title, string scoreLine, string waveLine, bool offerEndless)
        {
            endActive = true;
            endEndlessOffered = offerEndless;
            if (endTitle != null)
            {
                endTitle.text = title;
                endTitle.color = victory ? new Color(0.55f, 0.38f, 0.05f) : new Color(0.55f, 0.10f, 0.08f);
            }
            if (endScoreText != null) endScoreText.text = scoreLine;
            if (endWaveText != null) endWaveText.text = waveLine;
            LayoutEndButtons();
            Open();
        }

        /// <summary>Leaves the END page (restart / endless / new empire / reset) and rolls the scroll up.</summary>
        public void HideEnd()
        {
            bool wasEnd = endActive;
            endActive = false;
            if (!wasEnd && page != PageEnd) return;
            if (page == PageEnd) ShowPage(founded ? PageFound : PageCiv);
            Close();
        }

        void LayoutEndButtons()
        {
            if (endRestartButton == null) return;
            endRestartButton.interactable = true;
            endEndlessButton.interactable = true;
            endNewEmpireButton.interactable = true;
            var rr = (RectTransform)endRestartButton.transform;
            var er = (RectTransform)endEndlessButton.transform;
            if (endEndlessOffered)
            {
                rr.anchoredPosition = new Vector2(-175f, -90f); rr.sizeDelta = new Vector2(330f, 100f);
                er.anchoredPosition = new Vector2(175f, -90f); er.sizeDelta = new Vector2(330f, 100f);
                endEndlessButton.gameObject.SetActive(true);
            }
            else
            {
                rr.anchoredPosition = new Vector2(0f, -90f); rr.sizeDelta = new Vector2(420f, 100f);
                endEndlessButton.gameObject.SetActive(false);
            }
        }

        void OnEndRestart()
        {
            endRestartButton.interactable = false; // no double restart from a lingering poke
            endEndlessButton.interactable = false;
            endNewEmpireButton.interactable = false;
            if (EndScreen.Instance != null) EndScreen.Instance.RequestRestart();
            else { HideEnd(); if (GameDirector.Instance != null) GameDirector.Instance.Restart(); }
        }

        void OnEndEndless()
        {
            endRestartButton.interactable = false;
            endEndlessButton.interactable = false;
            endNewEmpireButton.interactable = false;
            if (EndScreen.Instance != null) EndScreen.Instance.RequestEndlessSiege();
        }

        void OnEndNewEmpire()
        {
            endRestartButton.interactable = false;
            endEndlessButton.interactable = false;
            endNewEmpireButton.interactable = false;
            founded = false;
            if (EndScreen.Instance != null) EndScreen.Instance.RequestNewEmpire();
            else { HideEnd(); if (GameDirector.Instance != null) GameDirector.Instance.NewEmpire(); }
            if (!IsOpen || page != PageCiv) Open();
        }

        // ------------------------------------------------------------------ placement

        void PlaceBesidePlayer()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var ct = cam.transform;
            Vector3 f = ct.forward; f.y = 0f;
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            f.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, f);
            float side = (HandRig.Instance != null && HandRig.Instance.Dominant == HandSide.Left) ? -1f : 1f;
            Vector3 pos = ct.position + f * openDistance + right * (sideOffset * side) + Vector3.down * belowEye;
            Vector3 look = pos - ct.position; look.y *= 0.5f;
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(look.normalized, Vector3.up));
            placed = true;
        }

        // ------------------------------------------------------------------ visuals

        void BuildVisuals()
        {
            var holder = new GameObject("ScrollVisuals").transform;
            holder.SetParent(transform, false);
            if (canvas != null) holder.SetPositionAndRotation(canvas.transform.position, canvas.transform.rotation);
            Vector3 ls = transform.lossyScale;
            holder.localScale = new Vector3(1f / Mathf.Max(1e-4f, ls.x), 1f / Mathf.Max(1e-4f, ls.y), 1f / Mathf.Max(1e-4f, ls.z));
            visuals = holder;

            Color wood = new Color(0.40f, 0.22f, 0.09f);
            Color gold = new Color(0.90f, 0.70f, 0.25f);
            Color wax = new Color(0.62f, 0.06f, 0.07f);

            // parchment: bent grid mesh with procedural texture (warm paper, vignette, gold ornament border)
            var pgo = new GameObject("Parchment");
            pgo.transform.SetParent(holder, false);
            pgo.AddComponent<MeshFilter>().sharedMesh = BuildParchmentMesh(ParchW, ParchH, 14, 20);
            var pmr = pgo.AddComponent<MeshRenderer>();
            pmr.sharedMaterial = Mats.UnlitTexDoubleSided(BuildParchmentTexture());
            NoShadows(pmr);
            parchment = pgo.transform;

            // rollers + gold end caps
            rollerTop = BuildRoller("RollerTop", holder, wood, gold);
            rollerBottom = BuildRoller("RollerBottom", holder, wood, gold);

            // red wax seal with authentic fluted wax rim, recessed stamp disc and imperial crown/star relief
            seal = new GameObject("WaxSeal").transform;
            seal.SetParent(rollerBottom, false);
            seal.localPosition = new Vector3(0.12f, -0.05f, -0.012f);
            Prim(PrimitiveType.Cylinder, "SealDisc", seal, Vector3.zero, new Vector3(0.050f, 0.004f, 0.050f), Quaternion.Euler(90f, 0f, 0f), wax);
            Prim(PrimitiveType.Cylinder, "SealInner", seal, new Vector3(0f, 0f, -0.003f), new Vector3(0.034f, 0.002f, 0.034f), Quaternion.Euler(90f, 0f, 0f), new Color(0.50f, 0.05f, 0.06f));

            // town hall token socket: small gold pedestal to the side of the scroll
            tokenSocket = new GameObject("TokenSocket").transform;
            tokenSocket.SetParent(holder, false);
            tokenSocket.localPosition = new Vector3(ParchW * 0.5f + 0.075f, -0.17f, -0.03f);

            SetRollerSpread(1f);
            if (grabHandle != null)
            {
                grabHandle.SetParent(transform, true);
                grabHandle.position = rollerTop.position;
                grabHandle.rotation = rollerTop.rotation;
            }
        }

        Transform BuildRoller(string name, Transform parent, Color wood, Color gold)
        {
            var root = new GameObject(name).transform;
            root.SetParent(parent, false);
            float len = ParchW + 0.03f;
            Prim(PrimitiveType.Cylinder, "Rod", root, Vector3.zero, new Vector3(0.026f, len * 0.5f, 0.026f), Quaternion.Euler(0f, 0f, 90f), wood);
            for (int s = -1; s <= 1; s += 2)
            {
                Prim(PrimitiveType.Cylinder, "Collar", root, new Vector3(s * (len * 0.5f - 0.004f), 0f, 0f), new Vector3(0.032f, 0.004f, 0.032f), Quaternion.Euler(0f, 0f, 90f), gold);
                Prim(PrimitiveType.Sphere, "Cap", root, new Vector3(s * (len * 0.5f + 0.012f), 0f, 0f), new Vector3(0.03f, 0.03f, 0.03f), Quaternion.identity, gold);
                Prim(PrimitiveType.Sphere, "Finial", root, new Vector3(s * (len * 0.5f + 0.03f), 0f, 0f), new Vector3(0.014f, 0.014f, 0.014f), Quaternion.identity, gold);
            }
            return root;
        }

        static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 lp, Vector3 ls, Quaternion lr, Color c)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) DestroyImmediate(col);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = lp;
            go.transform.localRotation = lr;
            go.transform.localScale = ls;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = Mats.Lit(c);
            NoShadows(mr);
            return go;
        }

        static void NoShadows(Renderer r)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        /// <summary>Grid mesh behind the canvas (z+ is away from the viewer); side edges curl back, top/bottom roll toward the rollers.</summary>
        static Mesh BuildParchmentMesh(float w, float h, int nx, int ny)
        {
            var mesh = new Mesh { name = "ParchmentMesh" };
            var v = new Vector3[(nx + 1) * (ny + 1)];
            var uv = new Vector2[v.Length];
            for (int j = 0; j <= ny; j++)
            {
                for (int i = 0; i <= nx; i++)
                {
                    float u = i / (float)nx, t = j / (float)ny;
                    float ex = Mathf.Abs(u * 2f - 1f);
                    float ey = Mathf.Abs(t * 2f - 1f);
                    float z = 0.004f + 0.016f * Mathf.Pow(ex, 4f) + 0.010f * Mathf.Pow(ey, 6f) + 0.0015f * Mathf.Sin(t * 9f + u * 3f);
                    v[j * (nx + 1) + i] = new Vector3((u - 0.5f) * w, (t - 0.5f) * h, z);
                    uv[j * (nx + 1) + i] = new Vector2(u, t);
                }
            }
            var tris = new int[nx * ny * 6];
            int k = 0;
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int a = j * (nx + 1) + i, b = a + 1, c = a + nx + 1, d = c + 1;
                    tris[k++] = a; tris[k++] = c; tris[k++] = b;
                    tris[k++] = b; tris[k++] = c; tris[k++] = d;
                }
            mesh.vertices = v;
            mesh.uv = uv;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Texture2D BuildParchmentTexture()
        {
            const int W = 512, H = 640;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { name = "ParchmentTex", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[W * H];
            Color paper = new Color(0.945f, 0.918f, 0.855f), edge = new Color(0.80f, 0.74f, 0.62f), gold = new Color(0.72f, 0.60f, 0.36f);
            uint seed = 99991u;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    float u = x / (float)(W - 1), t = y / (float)(H - 1);
                    float dx = Mathf.Min(u, 1f - u), dy = Mathf.Min(t, 1f - t);
                    float vign = Mathf.Clamp01(Mathf.Min(dx * 14f, dy * 16f));
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                    float n = ((seed & 0xFF) / 255f - 0.5f) * 0.04f;
                    float fiber = 0f;
                    Color c = Color.Lerp(edge, paper, Mathf.SmoothStep(0f, 1f, vign));
                    c.r += n + fiber; c.g += n + fiber; c.b += n * 0.6f;

                    // single hairline inset border, nothing else
                    float bx = Mathf.Min(x, W - 1 - x), by = Mathf.Min(y, H - 1 - y);
                    float bd = Mathf.Min(bx, by);
                    if (bd >= 14 && bd < 16) c = Color.Lerp(c, gold, 0.55f);
                    c.a = 1f;
                    px[y * W + x] = c;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        void SetRollerSpread(float k)
        {
            float half = Mathf.Lerp(0.02f, ParchH * 0.5f + 0.012f, k);
            rollerTop.localPosition = new Vector3(0f, half, 0.008f);
            rollerBottom.localPosition = new Vector3(0f, -half, 0.008f);
            rollerTop.localRotation = Quaternion.Euler(k * 360f, 0f, 0f);
            rollerBottom.localRotation = Quaternion.Euler(-k * 360f, 0f, 0f);
            parchment.localScale = new Vector3(1f, Mathf.Max(0.001f, k), 1f);
            if (pagesRoot != null) pagesRoot.localScale = new Vector3(1f, Mathf.Max(0.001f, k), 1f);
            if (seal != null) seal.localRotation = Quaternion.identity;
        }

        IEnumerator Unfurl(bool open)
        {
            float t = 0f;
            float dur = Mathf.Max(0.05f, unfurlSeconds);
            if (pagesGroup != null) pagesGroup.interactable = false;
            while (t < dur)
            {
                t += Time.deltaTime;
                float u = Mathf.Clamp01(t / dur);
                float k = open ? u : 1f - u;
                float e = k * k * (3f - 2f * k);
                SetRollerSpread(e);
                if (pagesGroup != null) pagesGroup.alpha = Mathf.Clamp01((e - 0.55f) / 0.45f);
                yield return null;
            }
            SetRollerSpread(open ? 1f : 0f);
            if (pagesGroup != null)
            {
                pagesGroup.alpha = open ? 1f : 0f;
                pagesGroup.interactable = open;
                pagesGroup.blocksRaycasts = open;
            }
            if (!open) SetVisible(false, true);
            anim = null;
        }

        void SetVisible(bool on, bool immediate)
        {
            if (visuals != null)
            {
                for (int i = 0; i < visuals.childCount; i++)
                {
                    var c = visuals.GetChild(i);
                    if (c == tokenSocket) continue; // the town hall token anchor must stay valid
                    c.gameObject.SetActive(on);
                }
            }
            if (pagesRoot != null) pagesRoot.gameObject.SetActive(on);
            if (on && pagesGroup != null) { pagesGroup.blocksRaycasts = true; }
        }

        // ------------------------------------------------------------------ UI

        void BuildUi()
        {
            if (built) return;
            if (canvas == null) canvas = GetComponentInChildren<Canvas>(true);
            if (canvas == null) { Debug.LogError("[ImperialScroll] canvas not assigned"); return; }
            built = true;
            var root = (RectTransform)canvas.transform;
            pagesRoot = UiKit.Rect(root, "ScrollPages", Vector2.zero, new Vector2(800f, 1000f));
            pagesGroup = pagesRoot.gameObject.AddComponent<CanvasGroup>();

            // header: title + subtitle + hairline
            var title = UiKit.Label(pagesRoot, Strings.Title, new Vector2(-110f, 450f), new Vector2(460f, 64f), UiKit.FontTitle, TextAlignmentOptions.Left);
            title.fontStyle = FontStyles.Bold;
            title.color = UiKit.Ink;
            var sub = UiKit.Label(pagesRoot, Strings.Subtitle, new Vector2(-110f, 408f), new Vector2(460f, 34f), 22, TextAlignmentOptions.Left);
            sub.color = UiKit.InkMuted;
            var rule = UiKit.Panel(pagesRoot, new Vector2(0f, 380f), new Vector2(680f, 2f), UiKit.ParchmentEdge);
            rule.GetComponent<Image>().raycastTarget = false;

            pages = new RectTransform[PageCount];
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i] = UiKit.Rect(pagesRoot, "Page" + (i + 1), new Vector2(0f, -15f), new Vector2(680f, 790f));
            }

            BuildCivPage(pages[0]);
            BuildNamePage(pages[1]);
            BuildBannerPage(pages[2]);
            BuildFoundPage(pages[3]);
            BuildStartPage(pages[PageStart]);
            BuildSettingsPage(pages[PageSettings]);
            BuildEndPage(pages[PageEnd]);

            backButton = UiKit.Button(pagesRoot, Strings.Back, new Vector2(-230f, -430f), new Vector2(200f, 76f), OnBack, 32);
            UiKit.StyleSecondary(backButton);
            nextButton = UiKit.Button(pagesRoot, Strings.Next, new Vector2(230f, -430f), new Vector2(200f, 76f), OnNext, 32);
            var rollUp = UiKit.Button(pagesRoot, Strings.RollUp, new Vector2(270f, 446f), new Vector2(140f, 56f), Close, 24);
            UiKit.StyleSecondary(rollUp);
            stepDots = new Image[SetupPageCount];
            for (int d = 0; d < SetupPageCount; d++)
            {
                var dot = UiKit.Panel(pagesRoot, new Vector2((d - 1.5f) * 34f, -430f), new Vector2(18f, 18f), UiKit.ParchmentShade);
                dot.name = "StepDot" + d;
                dot.GetComponent<Image>().raycastTarget = false;
                stepDots[d] = dot.GetComponent<Image>();
            }

            ShowPage(PageStart);
        }

        void BuildStartPage(RectTransform p)
        {
            var intro = UiKit.Label(p, Strings.StartIntro, new Vector2(0f, 250f), new Vector2(600f, 130f), UiKit.FontBody);
            intro.color = UiKit.InkMuted;
            startButton = UiKit.Button(p, Strings.StartGame, new Vector2(0f, 60f), new Vector2(560f, 150f), OnStartGame, 56);
            UiKit.SetButtonColor(startButton, UiKit.ButtonColor);
            settingsButton = UiKit.Button(p, Strings.Settings, new Vector2(0f, -140f), new Vector2(420f, 100f), () => ShowPage(PageSettings), 40);
            UiKit.StyleSecondary(settingsButton);
        }

        void OnStartGame()
        {
            launchMenu = false;
            ShowPage(PageCiv);
        }

        void BuildSettingsPage(RectTransform p)
        {
            var h = UiKit.Label(p, Strings.Settings, new Vector2(0f, 360f), new Vector2(680f, 56f), UiKit.FontHeading, TextAlignmentOptions.Left);
            h.fontStyle = FontStyles.Bold;
            h.color = UiKit.Ink;
            var sub = UiKit.Label(p, Strings.MenuHand, new Vector2(0f, 270f), new Vector2(680f, 48f), UiKit.FontHeading - 4, TextAlignmentOptions.Left);
            sub.color = UiKit.InkMuted;
            rightHandButton = UiKit.Button(p, Strings.RightHanded, new Vector2(-175f, 140f), new Vector2(330f, 130f), () => GameSettings.MenuHand = HandSide.Right, 38);
            leftHandButton = UiKit.Button(p, Strings.LeftHanded, new Vector2(175f, 140f), new Vector2(330f, 130f), () => GameSettings.MenuHand = HandSide.Left, 38);
            var hint = UiKit.Label(p, Strings.MenuHandHint, new Vector2(0f, -20f), new Vector2(640f, 170f), UiKit.FontBody);
            hint.color = UiKit.Ink;
            RefreshSettingsButtons();
        }

        void RefreshSettingsButtons()
        {
            if (rightHandButton == null || leftHandButton == null) return;
            bool right = GameSettings.MenuHand == HandSide.Right;
            StyleToggle(rightHandButton, right);
            StyleToggle(leftHandButton, !right);
        }

        static void StyleToggle(Button b, bool on)
        {
            UiKit.SetButtonColor(b, on ? UiKit.ConfirmGreen : UiKit.ParchmentShade);
            var t = b.GetComponentInChildren<TMP_Text>(true);
            if (t != null) t.color = on ? UiKit.ButtonText : UiKit.Ink;
            UiKit.SetSelected(b, on, UiKit.GoldBright);
        }

        void BuildEndPage(RectTransform p)
        {
            endTitle = UiKit.Label(p, string.Empty, new Vector2(0f, 270f), new Vector2(680f, 110f), UiKit.FontDisplay + 12);
            endTitle.fontStyle = FontStyles.Bold;
            endTitle.enableAutoSizing = true; endTitle.fontSizeMin = 36; endTitle.fontSizeMax = UiKit.FontDisplay + 12;
            var plaque = UiKit.FramedPanel(p, "StatsPlaque", new Vector2(0f, 110f), new Vector2(640f, 150f),
                UiKit.ParchmentEdge, UiKit.ParchmentLight, UiKit.ParchmentEdge, 3f);
            plaque.GetComponent<Image>().raycastTarget = false;
            endScoreText = UiKit.Label(plaque, string.Empty, new Vector2(0f, 30f), new Vector2(610f, 56f), UiKit.FontHeading - 2);
            endScoreText.fontStyle = FontStyles.Bold;
            endScoreText.enableAutoSizing = true; endScoreText.fontSizeMin = 20; endScoreText.fontSizeMax = UiKit.FontHeading - 2;
            endWaveText = UiKit.Label(plaque, string.Empty, new Vector2(0f, -32f), new Vector2(610f, 56f), UiKit.FontCaption - 2);
            endWaveText.color = UiKit.InkMuted;
            endWaveText.enableAutoSizing = true; endWaveText.fontSizeMin = 16; endWaveText.fontSizeMax = UiKit.FontCaption - 2;

            endRestartButton = UiKit.Button(p, Strings.Restart, new Vector2(0f, -90f), new Vector2(420f, 100f), OnEndRestart, 38);
            UiKit.SetButtonColor(endRestartButton, UiKit.ButtonColor);
            endEndlessButton = UiKit.Button(p, Strings.EndlessSiege, new Vector2(175f, -90f), new Vector2(330f, 100f), OnEndEndless, 32);
            UiKit.SetButtonColor(endEndlessButton, UiKit.ConfirmGreen);
            endEndlessButton.gameObject.SetActive(false);
            endNewEmpireButton = UiKit.Button(p, Strings.NewEmpire, new Vector2(0f, -220f), new Vector2(360f, 84f), OnEndNewEmpire, 34);
            UiKit.StyleSecondary(endNewEmpireButton);
        }

        void OnBack()
        {
            switch (page)
            {
                case PageCiv: ShowPage(PageStart); break;
                case PageSettings: ShowPage(PageStart); break;
                case PageName: ShowPage(PageCiv); break;
                case PageBanner: ShowPage(PageName); break;
                case PageFound: ShowPage(PageBanner); break;
            }
        }

        void BuildCivPage(RectTransform p)
        {
            var h = UiKit.Label(p, Strings.ChooseCivilization, new Vector2(0f, 360f), new Vector2(680f, 56f), UiKit.FontHeading, TextAlignmentOptions.Left);
            h.fontStyle = FontStyles.Bold;
            h.color = UiKit.Ink;
            var area = UiKit.Rect(p, "CivCards", new Vector2(0f, -30f), new Vector2(680f, 700f));
            if (civilizationSelector == null) civilizationSelector = area.gameObject.AddComponent<CivilizationSelector>();
            civilizationSelector.Build(area);
        }

        void BuildNamePage(RectTransform p)
        {
            var h = UiKit.Label(p, Strings.NameYourEmpire, new Vector2(0f, 360f), new Vector2(680f, 56f), UiKit.FontHeading, TextAlignmentOptions.Left);
            h.fontStyle = FontStyles.Bold;
            h.color = UiKit.Ink;

            var field = UiKit.FramedPanel(p, "NameField", new Vector2(0f, 250f), new Vector2(680f, 100f),
                UiKit.ParchmentEdge, UiKit.ParchmentLight, UiKit.ParchmentEdge, 3f);
            nameDisplay = UiKit.Label(field, string.Empty, Vector2.zero, new Vector2(640f, 90f), 48);
            nameDisplay.textWrappingMode = TextWrappingModes.NoWrap;
            nameDisplay.overflowMode = TextOverflowModes.Overflow;
            nameDisplay.enableAutoSizing = true;
            nameDisplay.fontSizeMin = 24;
            nameDisplay.fontSizeMax = 48;
            nameDisplay.fontStyle = FontStyles.Bold;
            nameDisplay.color = UiKit.Ink;

            var area = UiKit.Rect(p, "Keyboard", new Vector2(0f, -110f), new Vector2(680f, 380f));
            if (keyboard == null) keyboard = area.gameObject.AddComponent<HandPokeKeyboard>();
            keyboard.Build(area, nameDisplay);
            keyboard.SetText(CivilizationProfile.Current.empireName);
            keyboard.Confirmed += () => { CommitName(); ShowPage(2); };

            // Meta system keyboard replaces the scroll keyboard on device (scroll keyboard stays as editor/unsupported fallback)
            useSystemKeyboard = false; // user: always use the built-in scroll keyboard, not the Meta system keyboard
            if (useSystemKeyboard)
            {
                systemKeyboard = gameObject.AddComponent<SystemKeyboardInput>();
                area.gameObject.SetActive(false);
                UiKit.Button(p, Strings.TypeName, new Vector2(0f, 60f), new Vector2(560f, 120f), OpenSystemKeyboard, 38);
                UiKit.Button(p, Strings.Confirm, new Vector2(0f, -120f), new Vector2(340f, 100f), () => { systemKeyboard.Close(); CommitName(); ShowPage(2); }, 38);
            }
        }

        bool useSystemKeyboard;
        SystemKeyboardInput systemKeyboard;

        void OpenSystemKeyboard()
        {
            if (systemKeyboard == null) return;
            systemKeyboard.Open(keyboard != null ? keyboard.Text : CivilizationProfile.Current.empireName, CivilizationProfile.MaxNameLength,
                t => { if (keyboard != null) keyboard.SetText(t); },
                t => { if (keyboard != null) keyboard.SetText(t); CommitName(); });
        }

        void BuildBannerPage(RectTransform p)
        {
            var h = UiKit.Label(p, Strings.BannerTitle, new Vector2(0f, 360f), new Vector2(680f, 56f), UiKit.FontHeading, TextAlignmentOptions.Left);
            h.fontStyle = FontStyles.Bold;
            h.color = UiKit.Ink;
            var panel = UiKit.Rect(p, "BannerPanel", new Vector2(0f, -35f), new Vector2(680f, 700f));
            bannerPanel = panel;
            if (drawingCanvas == null)
            {
                var logic = UiKit.Rect(p, "EmblemDrawingLogic", Vector2.zero, Vector2.zero);
                drawingCanvas = logic.gameObject.AddComponent<EmblemDrawingCanvas>();
            }
            if (bannerCustomizer == null) bannerCustomizer = panel.gameObject.AddComponent<BannerCustomizer>();
            bannerCustomizer.Build(panel, drawingCanvas);
            bannerCustomizer.Confirmed += () => ShowPage(3);
        }

        void BuildFoundPage(RectTransform p)
        {
            var h = UiKit.Label(p, Strings.FoundEmpire, new Vector2(0f, 360f), new Vector2(680f, 56f), UiKit.FontHeading, TextAlignmentOptions.Left);
            h.fontStyle = FontStyles.Bold;
            h.color = UiKit.Ink;

            summaryName = UiKit.Label(p, string.Empty, new Vector2(0f, 290f), new Vector2(680f, 64f), UiKit.FontDisplay - 6);
            summaryName.fontStyle = FontStyles.Bold;
            summaryName.color = UiKit.RubricCrimson;
            summaryName.enableAutoSizing = true; summaryName.fontSizeMin = 28; summaryName.fontSizeMax = UiKit.FontDisplay - 6;
            summaryName.textWrappingMode = TextWrappingModes.NoWrap;
            summaryCiv = UiKit.Label(p, string.Empty, new Vector2(0f, 236f), new Vector2(680f, 40f), UiKit.FontCaption + 2);
            summaryCiv.color = UiKit.InkMuted;

            // banner preview on a plain card
            UiKit.FramedPanel(p, "StandardBacking", new Vector2(0f, 60f), new Vector2(380f, 270f),
                UiKit.ParchmentEdge, UiKit.ParchmentLight, UiKit.ParchmentEdge, 3f);
            summaryBanner = UiKit.RawImage(p, new Vector2(0f, 60f), new Vector2(348f, 240f), null);

            foundButton = UiKit.Button(p, Strings.FoundEmpire, new Vector2(0f, -170f), new Vector2(440f, 96f), OnFound, 44);
            UiKit.SetButtonColor(foundButton, UiKit.ButtonColor);

            foundedHint = UiKit.Label(p, Strings.PlaceTownHallHint, new Vector2(0f, -135f), new Vector2(620f, 90f), UiKit.FontBody);
            foundedHint.color = UiKit.Ink;
            newEmpireButton = UiKit.Button(p, Strings.NewEmpire, new Vector2(0f, -235f), new Vector2(360f, 76f), OnNewEmpire, 36);
            UiKit.StyleSecondary(newEmpireButton);
        }

        void CommitName()
        {
            var prof = CivilizationProfile.Current;
            string raw = keyboard != null ? keyboard.Text : prof.empireName;
            prof.empireName = CivilizationProfile.SanitizeName(raw, prof.Definition);
            if (keyboard != null) keyboard.SetText(prof.empireName);
            CivilizationProfile.NotifyChanged();
        }

        void OnNext()
        {
            if (page > PageBanner) return;
            if (page == PageName) CommitName();
            ShowPage(page + 1);
        }

        void ShowPage(int index)
        {
            if (pages == null) return;
            int prev = page;
            if (prev == PageName && index != PageName) CommitName();
            page = Mathf.Clamp(index, 0, pages.Length - 1);
            if (page != prev && IsOpen && ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.PageTurn, transform.position);
            for (int i = 0; i < pages.Length; i++) pages[i].gameObject.SetActive(i == page);

            if (stepDots != null)
                for (int d = 0; d < stepDots.Length; d++)
                {
                    bool setupPage = page < SetupPageCount;
                    stepDots[d].gameObject.SetActive(setupPage);
                    stepDots[d].color = d == page ? UiKit.ButtonColor : (d < page ? UiKit.InkMuted : UiKit.ParchmentShade);
                    ((RectTransform)stepDots[d].transform).sizeDelta = d == page ? new Vector2(26f, 18f) : new Vector2(18f, 18f);
                }

            if (page == 0 && civilizationSelector != null) civilizationSelector.Refresh();
            if (page == 1 && keyboard != null) keyboard.SetText(CivilizationProfile.Current.empireName);
            if (page == 1 && prev != 1 && useSystemKeyboard) OpenSystemKeyboard();
            if (page != 1 && systemKeyboard != null) systemKeyboard.Close();
            if (page == 2 && bannerCustomizer != null)
            {
                if (drawingCanvas != null && drawingCanvas.IsBuilt) drawingCanvas.Build(drawingCanvas.Area); // reload saved strokes
                bannerCustomizer.Build(bannerPanel, drawingCanvas);         // refresh swatches/preview
            }
            if (page == PageFound) RefreshSummary();
            if (page == PageSettings) RefreshSettingsButtons();
            RefreshNavButtons();
            RefreshFoundedState();
        }

        void RefreshNavButtons()
        {
            if (backButton != null) backButton.gameObject.SetActive(!founded && page != PageStart && page != PageEnd);
            if (nextButton != null) nextButton.gameObject.SetActive(!founded && page <= PageName);
        }

        void RefreshSummary()
        {
            var prof = CivilizationProfile.Current;
            summaryName.text = prof.DisplayName;
            summaryCiv.text = Strings.Format(Strings.SummaryCivilization, prof.Definition.DisplayName);
            summaryTex = EmblemRasterizer.RasterizeProfile(prof, 256, summaryTex);
            summaryBanner.texture = summaryTex;
        }

        void RefreshFoundedState()
        {
            if (foundButton == null) return;
            foundButton.gameObject.SetActive(!founded);
            foundedHint.gameObject.SetActive(founded);
            newEmpireButton.gameObject.SetActive(founded);
            RefreshNavButtons();
            if (founded)
            {
                var gd = GameDirector.Instance;
                bool placing = gd != null && gd.Phase == GamePhase.HeadquartersPlacement;
                foundedHint.text = placing ? Strings.PlaceTownHallHint : Strings.Format(Strings.Founded, CivilizationProfile.Current.DisplayName);
            }
        }

        void OnFound()
        {
            if (founded) return;
            CommitName();
            founded = true;
            RefreshSummary();
            ShowPage(3);
            CivilizationProfile.NotifyChanged();
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Horn);
            if (GameDirector.Instance != null) GameDirector.Instance.OnProfileConfirmed();
            RefreshFoundedState();
        }

        void OnNewEmpire()
        {
            founded = false;
            if (GameDirector.Instance != null) GameDirector.Instance.NewEmpire(); // → PhaseChanged(CivilizationSetup) → Open()
            if (!IsOpen || page != PageCiv) Open();
        }
    }
}
