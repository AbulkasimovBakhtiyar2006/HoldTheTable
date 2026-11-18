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
        RawImage summaryBanner;
        Texture2D summaryTex;
        int page;
        bool built, placed, founded, subscribed;
        Coroutine anim;

        void Awake()
        {
            Instance = this;
            if (canvas == null) canvas = GetComponentInChildren<Canvas>(true);
            BuildVisuals();
            BuildUi();
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

        void OnDestroy()
        {
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
        }

        void OnPhaseChanged(GamePhase p)
        {
            if (p == GamePhase.CivilizationSetup)
            {
                founded = false;
                Open();
            }
            else if (p == GamePhase.HeadquartersPlacement || p == GamePhase.BuildPhase)
            {
                if (!founded) { founded = true; ShowPage(3); }
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
            ShowPage(founded ? 3 : 0);
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

            // red wax seal with embossed ring and cross, hanging below the bottom roller on a ribbon
            seal = new GameObject("WaxSeal").transform;
            seal.SetParent(rollerBottom, false);
            seal.localPosition = new Vector3(0.12f, -0.05f, -0.012f);
            var ribbon = Prim(PrimitiveType.Cube, "Ribbon", seal, new Vector3(0f, 0.028f, 0.004f), new Vector3(0.012f, 0.05f, 0.002f), Quaternion.identity, new Color(0.75f, 0.12f, 0.1f));
            ribbon.name = "Ribbon";
            Prim(PrimitiveType.Cylinder, "SealDisc", seal, Vector3.zero, new Vector3(0.05f, 0.004f, 0.05f), Quaternion.Euler(90f, 0f, 0f), wax);
            Prim(PrimitiveType.Cylinder, "SealEmboss", seal, new Vector3(0f, 0f, -0.0045f), new Vector3(0.034f, 0.0015f, 0.034f), Quaternion.Euler(90f, 0f, 0f), new Color(0.48f, 0.03f, 0.04f));
            Prim(PrimitiveType.Cube, "SealCrossA", seal, new Vector3(0f, 0f, -0.006f), new Vector3(0.022f, 0.005f, 0.002f), Quaternion.Euler(0f, 0f, 45f), new Color(0.78f, 0.15f, 0.12f));
            Prim(PrimitiveType.Cube, "SealCrossB", seal, new Vector3(0f, 0f, -0.006f), new Vector3(0.022f, 0.005f, 0.002f), Quaternion.Euler(0f, 0f, -45f), new Color(0.78f, 0.15f, 0.12f));

            // town hall token socket: small gold pedestal to the side of the scroll
            tokenSocket = new GameObject("TokenSocket").transform;
            tokenSocket.SetParent(holder, false);
            tokenSocket.localPosition = new Vector3(ParchW * 0.5f + 0.075f, -0.17f, -0.03f);
            Prim(PrimitiveType.Cylinder, "SocketPedestal", tokenSocket, new Vector3(0f, -0.006f, 0f), new Vector3(0.07f, 0.004f, 0.07f), Quaternion.identity, gold);
            Prim(PrimitiveType.Cylinder, "SocketArm", tokenSocket, new Vector3(-0.045f, -0.006f, 0.015f), new Vector3(0.008f, 0.03f, 0.008f), Quaternion.Euler(0f, 0f, 90f), wood);

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
            if (col != null) Destroy(col);
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
            const int W = 160, H = 200;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { name = "ParchmentTex", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[W * H];
            Color paper = new Color(0.96f, 0.89f, 0.71f), edge = new Color(0.74f, 0.55f, 0.32f), gold = new Color(0.82f, 0.62f, 0.20f);
            uint seed = 99991u;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    float u = x / (float)(W - 1), t = y / (float)(H - 1);
                    float dx = Mathf.Min(u, 1f - u), dy = Mathf.Min(t, 1f - t);
                    float vign = Mathf.Clamp01(Mathf.Min(dx * 7f, dy * 9f));
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                    float n = ((seed & 0xFF) / 255f - 0.5f) * 0.05f;
                    float fiber = Mathf.Sin(t * 140f + Mathf.Sin(u * 13f) * 3f) * 0.012f;
                    Color c = Color.Lerp(edge, paper, Mathf.SmoothStep(0f, 1f, vign));
                    c.r += n + fiber; c.g += n + fiber; c.b += n * 0.7f;

                    // double gold ornament border
                    float bx = Mathf.Min(x, W - 1 - x), by = Mathf.Min(y, H - 1 - y);
                    float b = Mathf.Min(bx, by);
                    bool line1 = b >= 6 && b < 8;
                    bool line2 = b >= 11 && b < 12;
                    // corner diamonds + mid-edge rhombi
                    bool cornerGem = bx < 22 && by < 22 && Mathf.Abs((bx - 9.5f)) + Mathf.Abs((by - 9.5f)) < 6f;
                    bool midTop = by < 18 && Mathf.Abs(x - W * 0.5f) + Mathf.Abs(by - 9.5f) < 7f;
                    bool midSide = bx < 18 && Mathf.Abs(y - H * 0.5f) + Mathf.Abs(bx - 9.5f) < 7f;
                    if (line1 || line2 || cornerGem || midTop || midSide) c = Color.Lerp(c, gold, 0.9f);
                    // scalloped wave pattern between border lines
                    if (b >= 8 && b < 11 && Mathf.Sin((bx < by ? y : x) * 0.6f) > 0.6f) c = Color.Lerp(c, gold, 0.55f);
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

            // title band
            var title = UiKit.Label(pagesRoot, Strings.Title, new Vector2(0f, 455f), new Vector2(760f, 60f), 44);
            title.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
            title.color = new Color(0.50f, 0.12f, 0.06f);
            var sub = UiKit.Label(pagesRoot, Strings.Subtitle, new Vector2(0f, 412f), new Vector2(760f, 34f), 24);
            sub.fontStyle = FontStyles.Italic;
            var rule = UiKit.Panel(pagesRoot, new Vector2(0f, 390f), new Vector2(640f, 3f), UiKit.Gold);
            rule.GetComponent<Image>().raycastTarget = false;

            pages = new RectTransform[4];
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i] = UiKit.Rect(pagesRoot, "Page" + (i + 1), new Vector2(0f, -15f), new Vector2(800f, 790f));
            }

            BuildCivPage(pages[0]);
            BuildNamePage(pages[1]);
            BuildBannerPage(pages[2]);
            BuildFoundPage(pages[3]);

            backButton = UiKit.Button(pagesRoot, Strings.Back, new Vector2(-260f, -450f), new Vector2(240f, 90f), () => ShowPage(page - 1), 36);
            nextButton = UiKit.Button(pagesRoot, Strings.Next, new Vector2(260f, -450f), new Vector2(240f, 90f), OnNext, 36);
            ShowPage(0);
        }

        void BuildCivPage(RectTransform p)
        {
            var h = UiKit.Label(p, Strings.ChooseCivilization, new Vector2(0f, 360f), new Vector2(760f, 56f), 40);
            h.fontStyle = FontStyles.Bold;
            var area = UiKit.Rect(p, "CivCards", new Vector2(0f, -30f), new Vector2(760f, 700f));
            if (civilizationSelector == null) civilizationSelector = area.gameObject.AddComponent<CivilizationSelector>();
            civilizationSelector.Build(area);
        }

        void BuildNamePage(RectTransform p)
        {
            var h = UiKit.Label(p, Strings.NameYourEmpire, new Vector2(0f, 360f), new Vector2(760f, 56f), 40);
            h.fontStyle = FontStyles.Bold;
            var field = UiKit.Panel(p, new Vector2(0f, 250f), new Vector2(740f, 110f), new Color(1f, 0.97f, 0.88f, 1f));
            field.GetComponent<Image>().raycastTarget = false;
            var ol = field.gameObject.AddComponent<Outline>();
            ol.effectColor = UiKit.Gold;
            ol.effectDistance = new Vector2(4f, -4f);
            nameDisplay = UiKit.Label(field, string.Empty, Vector2.zero, new Vector2(710f, 100f), 52);
            nameDisplay.textWrappingMode = TextWrappingModes.NoWrap;
            nameDisplay.overflowMode = TextOverflowModes.Overflow;
            nameDisplay.enableAutoSizing = true;
            nameDisplay.fontSizeMin = 26;
            nameDisplay.fontSizeMax = 52;

            var area = UiKit.Rect(p, "Keyboard", new Vector2(0f, -110f), new Vector2(800f, 380f));
            if (keyboard == null) keyboard = area.gameObject.AddComponent<HandPokeKeyboard>();
            keyboard.Build(area, nameDisplay);
            keyboard.SetText(CivilizationProfile.Current.empireName);
            keyboard.Confirmed += () => { CommitName(); ShowPage(2); };

            // Meta system keyboard replaces the scroll keyboard on device (scroll keyboard stays as editor/unsupported fallback)
            useSystemKeyboard = SystemKeyboardInput.Available;
            if (useSystemKeyboard)
            {
                systemKeyboard = gameObject.AddComponent<SystemKeyboardInput>();
                area.gameObject.SetActive(false);
                UiKit.Button(p, Strings.TypeName, new Vector2(0f, 60f), new Vector2(600f, 130f), OpenSystemKeyboard, 40);
                UiKit.Button(p, Strings.Confirm, new Vector2(0f, -120f), new Vector2(360f, 110f), () => { systemKeyboard.Close(); CommitName(); ShowPage(2); }, 40);
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
            var h = UiKit.Label(p, Strings.BannerTitle, new Vector2(0f, 360f), new Vector2(760f, 56f), 40);
            h.fontStyle = FontStyles.Bold;
            var panel = UiKit.Rect(p, "BannerPanel", new Vector2(0f, -35f), new Vector2(790f, 700f));
            bannerPanel = panel;
            // the drawing logic lives on a sibling (NOT an ancestor of the buttons): an ancestor IDragHandler would
            // capture drags that start on swatch/tool buttons and cancel their poke clicks. E adds a pad forwarder.
            if (drawingCanvas == null)
            {
                var logic = UiKit.Rect(p, "EmblemDrawingLogic", Vector2.zero, Vector2.zero);
                drawingCanvas = logic.gameObject.AddComponent<EmblemDrawingCanvas>();
            }
            if (bannerCustomizer == null) bannerCustomizer = panel.gameObject.AddComponent<BannerCustomizer>();
            bannerCustomizer.Build(panel, drawingCanvas);
            if (!drawingCanvas.IsBuilt)
            {
                var area = UiKit.Rect(panel, "EmblemArea", new Vector2(-120f, 0f), new Vector2(480f, 330f));
                drawingCanvas.Build(area);
            }
            bannerCustomizer.Confirmed += () => ShowPage(3);
        }

        void BuildFoundPage(RectTransform p)
        {
            var h = UiKit.Label(p, Strings.FoundEmpire, new Vector2(0f, 360f), new Vector2(760f, 56f), 40);
            h.fontStyle = FontStyles.Bold;
            summaryName = UiKit.Label(p, string.Empty, new Vector2(0f, 280f), new Vector2(760f, 80f), 56);
            summaryName.fontStyle = FontStyles.Bold;
            summaryName.color = new Color(0.45f, 0.10f, 0.05f);
            summaryCiv = UiKit.Label(p, string.Empty, new Vector2(0f, 220f), new Vector2(760f, 44f), 30);

            // banner preview on a little pole
            var pole = UiKit.Panel(p, new Vector2(-185f, 20f), new Vector2(12f, 300f), new Color(0.36f, 0.22f, 0.10f, 1f));
            pole.GetComponent<Image>().raycastTarget = false;
            var finial = UiKit.Panel(p, new Vector2(-185f, 176f), new Vector2(24f, 24f), UiKit.Gold);
            finial.GetComponent<Image>().raycastTarget = false;
            summaryBanner = UiKit.RawImage(p, new Vector2(-5f, 45f), new Vector2(348f, 240f), null);

            foundButton = UiKit.Button(p, Strings.FoundEmpire, new Vector2(0f, -200f), new Vector2(520f, 140f), OnFound, 52);
            UiKit.SetButtonColor(foundButton, new Color(0.55f, 0.10f, 0.08f, 1f));

            foundedHint = UiKit.Label(p, Strings.PlaceTownHallHint, new Vector2(0f, -150f), new Vector2(720f, 110f), 32);
            newEmpireButton = UiKit.Button(p, Strings.NewEmpire, new Vector2(0f, -300f), new Vector2(380f, 100f), OnNewEmpire, 40);
            UiKit.SetButtonColor(newEmpireButton, new Color(0.30f, 0.22f, 0.14f, 1f));
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
            if (page == 1) CommitName();
            ShowPage(page + 1);
        }

        void ShowPage(int index)
        {
            if (pages == null) return;
            int prev = page;
            if (prev == 1 && index != 1) CommitName();
            page = Mathf.Clamp(index, 0, pages.Length - 1);
            if (page != prev && IsOpen && ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.PageTurn, transform.position);
            for (int i = 0; i < pages.Length; i++) pages[i].gameObject.SetActive(i == page);

            if (page == 0 && civilizationSelector != null) civilizationSelector.Refresh();
            if (page == 1 && keyboard != null) keyboard.SetText(CivilizationProfile.Current.empireName);
            if (page == 1 && prev != 1 && useSystemKeyboard) OpenSystemKeyboard();
            if (page != 1 && systemKeyboard != null) systemKeyboard.Close();
            if (page == 2 && bannerCustomizer != null)
            {
                if (drawingCanvas != null && drawingCanvas.IsBuilt) drawingCanvas.Build(drawingCanvas.Area); // reload saved strokes
                bannerCustomizer.Build(bannerPanel, drawingCanvas);         // refresh swatches/preview
            }
            if (page == 3) RefreshSummary();

            if (backButton != null) backButton.gameObject.SetActive(page > 0 && !founded);
            if (nextButton != null) nextButton.gameObject.SetActive(page < 2 && !founded);
            RefreshFoundedState();
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
            if (backButton != null) backButton.gameObject.SetActive(page > 0 && !founded);
            if (nextButton != null) nextButton.gameObject.SetActive(page < 2 && !founded);
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
            if (!IsOpen || page != 0) Open();
        }
    }
}
