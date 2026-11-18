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
        TMP_Text nameDisplay, summaryName, summaryCiv, foundedHint, stepIndicator;
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

            // red wax seal with authentic fluted wax rim, recessed stamp disc and imperial crown/star relief
            seal = new GameObject("WaxSeal").transform;
            seal.SetParent(rollerBottom, false);
            seal.localPosition = new Vector3(0.12f, -0.05f, -0.012f);
            var ribbon = Prim(PrimitiveType.Cube, "Ribbon", seal, new Vector3(0f, 0.028f, 0.004f), new Vector3(0.014f, 0.052f, 0.002f), Quaternion.identity, new Color(0.68f, 0.10f, 0.08f));
            ribbon.name = "Ribbon";
            Prim(PrimitiveType.Cylinder, "SealDisc", seal, Vector3.zero, new Vector3(0.052f, 0.004f, 0.052f), Quaternion.Euler(90f, 0f, 0f), wax);
            // 8 outer wax squish flutes around perimeter
            for (int f = 0; f < 8; f++)
            {
                float ang = f * (360f / 8f) * Mathf.Deg2Rad;
                float fx = Mathf.Cos(ang) * 0.024f;
                float fy = Mathf.Sin(ang) * 0.024f;
                Prim(PrimitiveType.Sphere, "WaxFlute_" + f, seal, new Vector3(fx, fy, -0.001f), new Vector3(0.012f, 0.012f, 0.005f), Quaternion.identity, wax);
            }
            // recessed inner stamp disc
            Prim(PrimitiveType.Cylinder, "SealEmboss", seal, new Vector3(0f, 0f, -0.0035f), new Vector3(0.036f, 0.002f, 0.036f), Quaternion.Euler(90f, 0f, 0f), new Color(0.44f, 0.04f, 0.04f));
            // concentric relief ring
            Prim(PrimitiveType.Cylinder, "SealInnerRing", seal, new Vector3(0f, 0f, -0.0045f), new Vector3(0.026f, 0.0015f, 0.026f), Quaternion.Euler(90f, 0f, 0f), new Color(0.68f, 0.11f, 0.09f));
            // center imperial crown / fleur relief
            Prim(PrimitiveType.Cube, "CrownBase", seal, new Vector3(0f, -0.004f, -0.0055f), new Vector3(0.015f, 0.0035f, 0.002f), Quaternion.identity, gold);
            Prim(PrimitiveType.Cube, "CrownPeakM", seal, new Vector3(0f, 0.002f, -0.0055f), new Vector3(0.004f, 0.010f, 0.002f), Quaternion.identity, gold);
            Prim(PrimitiveType.Cube, "CrownPeakL", seal, new Vector3(-0.0055f, 0.001f, -0.0055f), new Vector3(0.0035f, 0.007f, 0.002f), Quaternion.Euler(0f, 0f, 15f), gold);
            Prim(PrimitiveType.Cube, "CrownPeakR", seal, new Vector3(0.0055f, 0.001f, -0.0055f), new Vector3(0.0035f, 0.007f, 0.002f), Quaternion.Euler(0f, 0f, -15f), gold);

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
            const int W = 512, H = 640;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { name = "ParchmentTex", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[W * H];
            Color paper = new Color(0.95f, 0.90f, 0.77f), edge = new Color(0.70f, 0.54f, 0.33f), gold = new Color(0.80f, 0.63f, 0.25f);
            uint seed = 99991u;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    float u = x / (float)(W - 1), t = y / (float)(H - 1);
                    float dx = Mathf.Min(u, 1f - u), dy = Mathf.Min(t, 1f - t);
                    float vign = Mathf.Clamp01(Mathf.Min(dx * 8f, dy * 10f));
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                    float n = ((seed & 0xFF) / 255f - 0.5f) * 0.04f;
                    float fiber = Mathf.Sin(t * 320f + Mathf.Sin(u * 28f) * 4f) * 0.010f;
                    Color c = Color.Lerp(edge, paper, Mathf.SmoothStep(0f, 1f, vign));
                    c.r += n + fiber; c.g += n + fiber; c.b += n * 0.6f;

                    // double gold ornament border with corner flourishes
                    float bx = Mathf.Min(x, W - 1 - x), by = Mathf.Min(y, H - 1 - y);
                    float b = Mathf.Min(bx, by);
                    bool line1 = b >= 16 && b < 22;
                    bool line2 = b >= 32 && b < 36;
                    // corner diamonds + mid-edge rhombi
                    bool cornerGem = bx < 60 && by < 60 && Mathf.Abs(bx - 26f) + Mathf.Abs(by - 26f) < 18f;
                    bool midTop = by < 46 && Mathf.Abs(x - W * 0.5f) + Mathf.Abs(by - 26f) < 20f;
                    bool midSide = bx < 46 && Mathf.Abs(y - H * 0.5f) + Mathf.Abs(bx - 26f) < 20f;
                    if (line1 || line2 || cornerGem || midTop || midSide) c = Color.Lerp(c, gold, 0.92f);
                    // scalloped wave pattern between border lines
                    if (b >= 22 && b < 32 && Mathf.Sin((bx < by ? y : x) * 0.25f) > 0.45f) c = Color.Lerp(c, gold, 0.60f);
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

            // illuminated title band with royal rubrication and decorative gold rule
            var title = UiKit.Label(pagesRoot, Strings.Title, new Vector2(0f, 455f), new Vector2(680f, 60f), 42);
            title.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
            title.color = UiKit.RubricCrimson;
            var sub = UiKit.Label(pagesRoot, Strings.Subtitle, new Vector2(0f, 412f), new Vector2(680f, 34f), 22);
            sub.fontStyle = FontStyles.Italic;
            sub.color = UiKit.InkMuted;
            var rule = UiKit.Panel(pagesRoot, new Vector2(0f, 390f), new Vector2(620f, 2.5f), UiKit.Gold);
            rule.GetComponent<Image>().raycastTarget = false;
            var centerGem = UiKit.Panel(pagesRoot, new Vector2(0f, 390f), new Vector2(10f, 10f), UiKit.RubricCrimson);
            centerGem.GetComponent<Image>().raycastTarget = false;
            centerGem.localRotation = Quaternion.Euler(0f, 0f, 45f);

            pages = new RectTransform[4];
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i] = UiKit.Rect(pagesRoot, "Page" + (i + 1), new Vector2(0f, -15f), new Vector2(680f, 790f));
            }

            BuildCivPage(pages[0]);
            BuildNamePage(pages[1]);
            BuildBannerPage(pages[2]);
            BuildFoundPage(pages[3]);

            backButton = UiKit.Button(pagesRoot, Strings.Back, new Vector2(-230f, -430f), new Vector2(190f, 72f), () => ShowPage(page - 1), 32);
            nextButton = UiKit.Button(pagesRoot, Strings.Next, new Vector2(230f, -430f), new Vector2(190f, 72f), OnNext, 32);
            stepIndicator = UiKit.Label(pagesRoot, "I · II · III · IV", new Vector2(0f, -430f), new Vector2(200f, 40f), 24);
            stepIndicator.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;

            ShowPage(0);
        }

        void BuildCivPage(RectTransform p)
        {
            var h = UiKit.Label(p, Strings.ChooseCivilization, new Vector2(0f, 360f), new Vector2(680f, 56f), 38);
            h.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
            h.color = UiKit.Ink;
            var area = UiKit.Rect(p, "CivCards", new Vector2(0f, -30f), new Vector2(680f, 700f));
            if (civilizationSelector == null) civilizationSelector = area.gameObject.AddComponent<CivilizationSelector>();
            civilizationSelector.Build(area);
        }

        void BuildNamePage(RectTransform p)
        {
            var h = UiKit.Label(p, Strings.NameYourEmpire, new Vector2(0f, 360f), new Vector2(680f, 56f), 38);
            h.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
            h.color = UiKit.Ink;

            var field = UiKit.FramedPanel(p, "NameField", new Vector2(0f, 250f), new Vector2(680f, 100f),
                UiKit.WalnutFrame, UiKit.ParchmentLight, UiKit.Gold, 4f, true);
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
            useSystemKeyboard = SystemKeyboardInput.Available;
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
            var h = UiKit.Label(p, Strings.BannerTitle, new Vector2(0f, 360f), new Vector2(680f, 56f), 38);
            h.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
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
            var h = UiKit.Label(p, Strings.FoundEmpire, new Vector2(0f, 360f), new Vector2(680f, 56f), 38);
            h.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
            h.color = UiKit.Ink;

            summaryName = UiKit.Label(p, string.Empty, new Vector2(0f, 285f), new Vector2(680f, 66f), 50);
            summaryName.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
            summaryName.color = UiKit.RubricCrimson;
            summaryCiv = UiKit.Label(p, string.Empty, new Vector2(0f, 230f), new Vector2(680f, 40f), 26);
            summaryCiv.fontStyle = FontStyles.Italic;
            summaryCiv.color = UiKit.InkMuted;

            // Framed heraldic standard display with carved walnut backing and antique brass crossbar & finials
            var backing = UiKit.FramedPanel(p, "StandardBacking", new Vector2(0f, 50f), new Vector2(376f, 260f),
                UiKit.WalnutFrame, UiKit.ParchmentShade, UiKit.Gold, 4f, true);

            var bar = UiKit.Panel(p, new Vector2(0f, 175f), new Vector2(364f, 10f), UiKit.WalnutFrame);
            bar.name = "Crossbar";
            bar.GetComponent<Image>().raycastTarget = false;
            var finL = UiKit.Panel(p, new Vector2(-184f, 175f), new Vector2(16f, 16f), UiKit.Gold);
            finL.GetComponent<Image>().raycastTarget = false;
            var finR = UiKit.Panel(p, new Vector2(184f, 175f), new Vector2(16f, 16f), UiKit.Gold);
            finR.GetComponent<Image>().raycastTarget = false;

            summaryBanner = UiKit.RawImage(p, new Vector2(0f, 50f), new Vector2(348f, 240f), null);

            foundButton = UiKit.Button(p, Strings.FoundEmpire, new Vector2(0f, -170f), new Vector2(440f, 96f), OnFound, 44);
            UiKit.SetButtonColor(foundButton, UiKit.ButtonColor);

            foundedHint = UiKit.Label(p, Strings.PlaceTownHallHint, new Vector2(0f, -135f), new Vector2(660f, 80f), 30);
            foundedHint.color = UiKit.Ink;
            newEmpireButton = UiKit.Button(p, Strings.NewEmpire, new Vector2(0f, -235f), new Vector2(360f, 76f), OnNewEmpire, 36);
            UiKit.SetButtonColor(newEmpireButton, UiKit.OakMid);
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

            if (stepIndicator != null)
            {
                string[] numerals = { "I", "II", "III", "IV" };
                var sb = new System.Text.StringBuilder(64);
                for (int s = 0; s < numerals.Length; s++)
                {
                    if (s > 0) sb.Append("  ·  ");
                    if (s == page) sb.Append("<color=#7A1C17><b>[").Append(numerals[s]).Append("]</b></color>");
                    else sb.Append("<color=#594230>").Append(numerals[s]).Append("</color>");
                }
                stepIndicator.text = sb.ToString();
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
