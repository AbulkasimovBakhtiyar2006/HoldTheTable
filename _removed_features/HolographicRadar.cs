using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Holographic mini-room radar floating above the non-dominant wrist (head-relative fallback when untracked).
    /// Centered on the player and yaw-aligned with the view (top of the disc = where you look).
    /// Visuals (all procedural, textures baked once at Awake, shared materials, shared quad mesh):
    ///  - ornate bezel ring (ticks, gems, forward/right/back/left glyphs) tinted by CivilizationProfile.Current (emblem = bezel, banner = underlay),
    ///  - glass dome with fresnel-style edge glow, range rings, soft pulsing rings, rotating sweep beam with fading trail, scanline shimmer, idle bob,
    ///  - blip classes: goblin (red disc), climber/engineer (lime triangle), flying (orange diamond), ram/siege (purple square),
    ///    warlord (gold star), boss (crimson ring + core); ping ripple on every new contact; blips swell when the sweep passes,
    ///  - own keep (civ-tinted castle icon + halo), own buildings (cyan squares / gold rings), surfaces (soft cyan rectangles),
    ///  - rim danger arrows (12 sectors) for off-radar threats plus the original behind-the-player chevron,
    ///  - smooth fade/scale in and out. Visible only while GameDirector.IsCombatPhase.
    /// Owner: HolographicRadar.cs only. No text (glyphs are drawn), no per-frame allocations, 10 Hz data refresh with per-frame smoothing.
    /// Public API unchanged: HolographicRadar.Instance, DiscDiameter, WorldRange.
    /// </summary>
    public sealed class HolographicRadar : MonoBehaviour
    {
        public const float DiscDiameter = 0.18f;
        public const float WorldRange = 3.0f;          // meters from the player mapped to the disc rim
        const float Scale = (DiscDiameter * 0.5f) / WorldRange;
        const float TickInterval = 0.1f;               // 10 Hz
        const int MaxDots = 64;
        const int MaxSurfaces = 16;
        const int MaxBuildings = 48;
        const float BehindDot = -0.25f;
        const float HeightAbove = 0.12f;
        const float BezelInner = 0.806f;               // disc radius / bezel outer radius
        const float BezelScale = 1f / BezelInner;      // bezel quad size relative to DiscDiameter
        const float SweepDegPerSec = 95f;
        const int Sectors = 12;
        const int RippleCount = 8;
        const int Q = 3000;                            // base render queue

        public static HolographicRadar Instance { get; private set; }

        // ---- types
        sealed class Blip { public Transform t; public GameObject go; public Vector3 target; public float ang; }

        sealed class Pool
        {
            public Transform parent; public Material mat; public float size = 0.01f; public string name = "B";
            public bool bump, smooth; public int n;
            public readonly List<Blip> items = new List<Blip>(16);

            public Blip Get(int i)
            {
                while (items.Count <= i) items.Add(Create());
                return items[i];
            }

            Blip Create()
            {
                var go = MakeQuad(name, parent, mat);
                go.SetActive(false);
                go.transform.localScale = new Vector3(size, size, 1f);
                return new Blip { t = go.transform, go = go };
            }

            public void Set(int i, Vector3 lp)
            {
                var b = Get(i);
                if (!b.go.activeSelf) { b.t.localPosition = lp; b.go.SetActive(true); }
                b.target = lp;
                b.ang = Mathf.Atan2(lp.x, lp.z) * Mathf.Rad2Deg;
            }

            public void Finish(int count)
            {
                for (int i = count; i < items.Count; i++) if (items[i].go.activeSelf) items[i].go.SetActive(false);
                n = count;
            }

            public void HideAll() { Finish(0); }
        }

        sealed class Ripple { public Transform t; public GameObject go; public Material m; public float age; public float dur; public float size; public bool on; public Color col; }

        // ---- shared static assets
        static Mesh quadMesh;
        static readonly Quaternion FlatRot = Quaternion.Euler(90f, 0f, 0f);
        static readonly int IdBaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int IdBaseMap = Shader.PropertyToID("_BaseMap");

        // ---- scene objects
        Transform root, body;
        Transform hallDot, hallHalo;
        Transform scanBar, sweepQuad;
        Pool poolGoblin, poolClimber, poolFlying, poolSiege, poolWarlord, poolBoss, poolBossCore, poolSurf, poolBldDef, poolBldEco;
        readonly Ripple[] ripples = new Ripple[RippleCount];
        readonly Transform[] pulseRings = new Transform[2];
        readonly Material[] pulseMats = new Material[2];
        readonly Transform[] sectorArrows = new Transform[Sectors];
        readonly Renderer[] sectorRends = new Renderer[Sectors];
        readonly int[] sectorSev = new int[Sectors];
        readonly int[] sectorApplied = new int[Sectors];
        readonly int[] sectorCnt = new int[Sectors];
        Material faceMat, arrowMat, arrowEliteMat, haloMat;

        // original behind-the-player chevron
        Transform arrow;
        Transform arrowA, arrowB;
        Renderer arrowRendA, arrowRendB;
        Material arrowMatBright, arrowMatDim;

        // fade registry (static-color materials; alpha multiplied by fade)
        readonly Material[] regM = new Material[40];
        readonly Color[] regC = new Color[40];
        int regN;
        int iBezel, iUnder, iKeep, iHalo;
        bool civDirty = true;
        float appliedFade = -1f;

        Camera cam;
        GameDirector subscribed;
        float tickTimer;
        bool visible;
        bool arrowOn;
        float fade;
        float sweepAng;
        Quaternion cachedYaw = Quaternion.identity;
        Vector3 targetPos;
        Quaternion targetRot = Quaternion.identity;
        bool hasTarget;

        // threat scan scratch
        readonly HashSet<int> seen = new HashSet<int>();
        readonly int[] skip = new int[96];
        int skipN;
        bool behindFound;
        float bestBehindSq;
        Vector3 behindRel;
        Vector3 scanCamPos, scanCamFwd;

        // ---------- bootstrap ----------

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; quadMesh = null; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("HolographicRadar");
            go.AddComponent<HolographicRadar>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            BuildVisuals();
            CivilizationProfile.Changed += OnCivChanged;
            root.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            CivilizationProfile.Changed -= OnCivChanged;
            Unsubscribe();
            if (Instance == this) Instance = null;
        }

        void OnCivChanged() { civDirty = true; }

        // ---------- texture baking ----------

        static float Sat(float x) { return x < 0f ? 0f : (x > 1f ? 1f : x); }
        static float Smooth(float a, float b, float x) { float t = Sat((x - a) / (b - a)); return t * t * (3f - 2f * t); }
        static float Line(float r, float c, float w) { return 1f - Smooth(w * 0.5f, w * 0.5f + 0.006f, Mathf.Abs(r - c)); }
        static Color32 C(float r, float g, float b, float a)
        {
            return new Color32((byte)(Sat(r) * 255f), (byte)(Sat(g) * 255f), (byte)(Sat(b) * 255f), (byte)(Sat(a) * 255f));
        }

        static Texture2D NewTex(int w, int h, string n)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            t.name = n;
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            return t;
        }

        static Texture2D BakeDisc()
        {
            const int N = 128;
            var tex = NewTex(N, N, "Radar_Backing");
            var buf = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float mask = 1f - Smooth(0.97f, 1f, r);
                    buf[y * N + x] = C(0.02f, 0.08f, 0.14f, (0.30f + 0.26f * r * r) * mask);
                }
            tex.SetPixels32(buf); tex.Apply(false, true);
            return tex;
        }

        static Texture2D BakeFace()
        {
            const int N = 256;
            var tex = NewTex(N, N, "Radar_Face");
            var buf = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float mask = 1f - Smooth(0.975f, 1f, r);
                    float fres = r * r * r * r * r;                       // fresnel-like edge glow
                    float a = 0.06f + 0.32f * fres;
                    float lines = 0.40f * Line(r, 0.333f, 0.012f) + 0.32f * Line(r, 0.666f, 0.012f) + 0.55f * Line(r, 0.962f, 0.022f);
                    a += lines;
                    float cross = Mathf.Min(Mathf.Abs(u), Mathf.Abs(v));
                    a += 0.15f * (1f - Smooth(0.003f, 0.010f, cross)) * Smooth(0.07f, 0.13f, r);
                    float gx = Mathf.Abs(Mathf.Repeat(u / 0.18f, 1f) - 0.5f) * 0.18f, gy = Mathf.Abs(Mathf.Repeat(v / 0.18f, 1f) - 0.5f) * 0.18f;
                    a += 0.13f * (1f - Smooth(0.004f, 0.011f, Mathf.Sqrt(gx * gx + gy * gy)));     // dot grid
                    float th = Mathf.Atan2(u, v);                                                // clockwise from up
                    float glint = Smooth(-1.40f, -1.15f, th) * (1f - Smooth(-0.55f, -0.35f, th)) *
                                  Smooth(0.80f, 0.86f, r) * (1f - Smooth(0.92f, 0.95f, r));
                    a += 0.38f * glint;
                    float k = Sat(fres * 1.2f + lines);
                    buf[y * N + x] = C(Mathf.Lerp(0.22f, 0.78f, k), Mathf.Lerp(0.74f, 1f, k), 1f, Sat(a) * mask);
                }
            tex.SetPixels32(buf); tex.Apply(false, true);
            return tex;
        }

        static Texture2D BakeBezel()
        {
            const int N = 256;
            var tex = NewTex(N, N, "Radar_Bezel");
            var buf = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float band = Smooth(BezelInner - 0.004f, BezelInner + 0.002f, r) * (1f - Smooth(0.985f, 1f, r));
                    if (band <= 0f) { buf[y * N + x] = C(0, 0, 0, 0); continue; }
                    float lum = 0.50f + 0.35f * Line(r, BezelInner + 0.014f, 0.016f) + 0.45f * Line(r, 0.972f, 0.02f)
                                - 0.22f * Line(r, 0.905f, 0.012f);
                    float alpha = 0.62f;
                    float deg = Mathf.Atan2(u, v) * Mathf.Rad2Deg; if (deg < 0f) deg += 360f;
                    // minor ticks every 6 deg, major every 30 deg
                    float dMin = Mathf.Abs(Mathf.Repeat(deg + 3f, 6f) - 3f) * Mathf.Deg2Rad * r;
                    float dMaj = Mathf.Abs(Mathf.Repeat(deg + 15f, 30f) - 15f) * Mathf.Deg2Rad * r;
                    float minor = (1f - Smooth(0.0028f, 0.0045f, dMin)) * Smooth(0.846f, 0.852f, r) * (1f - Smooth(0.886f, 0.892f, r));
                    float major = (1f - Smooth(0.0048f, 0.0068f, dMaj)) * Smooth(0.832f, 0.838f, r) * (1f - Smooth(0.930f, 0.936f, r));
                    float tick = Mathf.Max(minor, major);
                    lum = Mathf.Lerp(lum, 1f, tick); alpha = Mathf.Lerp(alpha, 1f, tick);
                    // glyphs at 0/90/180/270 (forward is larger with a pointer), gems at 45 deg steps
                    for (int g = 0; g < 4; g++)
                    {
                        float ga = g * 90f * Mathf.Deg2Rad;
                        float gcx = Mathf.Sin(ga) * 0.905f, gcy = Mathf.Cos(ga) * 0.905f;
                        float dx = u - gcx, dy = v - gcy;
                        float sz = g == 0 ? 0.068f : 0.048f;
                        float dia = (Mathf.Abs(dx) + Mathf.Abs(dy)) - sz;
                        float cov = 1f - Smooth(-0.004f, 0.004f, dia);
                        float carve = 1f - Smooth(sz + 0.004f, sz + 0.012f, Mathf.Abs(dx) + Mathf.Abs(dy));
                        if (carve > 0f) { lum = Mathf.Lerp(lum, 0.15f, carve * 0.8f); alpha = Mathf.Max(alpha, carve * 0.9f); }
                        if (cov > 0f) { lum = Mathf.Lerp(lum, 1f, cov); alpha = Mathf.Lerp(alpha, 1f, cov); }
                        if (g == 0)
                        {
                            float inner = 1f - Smooth(-0.004f, 0.004f, (Mathf.Abs(dx) + Mathf.Abs(dy)) - sz * 0.42f);
                            lum = Mathf.Lerp(lum, 0.25f, inner);
                        }
                    }
                    for (int g = 0; g < 4; g++)
                    {
                        float ga = (45f + g * 90f) * Mathf.Deg2Rad;
                        float dx = u - Mathf.Sin(ga) * 0.905f, dy = v - Mathf.Cos(ga) * 0.905f;
                        float gem = 1f - Smooth(0.016f, 0.022f, Mathf.Sqrt(dx * dx + dy * dy));
                        lum = Mathf.Lerp(lum, 1f, gem); alpha = Mathf.Lerp(alpha, 1f, gem);
                    }
                    buf[y * N + x] = C(lum, lum, lum, alpha * band);
                }
            tex.SetPixels32(buf); tex.Apply(false, true);
            return tex;
        }

        static Texture2D BakeUnder()
        {
            const int N = 128;
            var tex = NewTex(N, N, "Radar_BezelUnder");
            var buf = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float band = Smooth(BezelInner - 0.012f, BezelInner, r) * (1f - Smooth(0.985f, 1f, r));
                    float shade = 1f - 0.35f * Smooth(BezelInner, 1f, r);
                    buf[y * N + x] = C(shade, shade, shade, 0.88f * band);
                }
            tex.SetPixels32(buf); tex.Apply(false, true);
            return tex;
        }

        static Texture2D BakeSweep()
        {
            const int N = 128;
            var tex = NewTex(N, N, "Radar_Sweep");
            var buf = new Color32[N * N];
            const float trail = 1.25f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float mask = 1f - Smooth(0.96f, 0.99f, r);
                    float th = Mathf.Atan2(u, v);          // leading edge at 0, trail on the negative side
                    float t = -th;
                    float a = 0f;
                    if (t >= 0f && t < trail) { float f = 1f - t / trail; a = f * f * 0.55f; }
                    float lead = 1f - Smooth(0.010f, 0.030f, Mathf.Abs(th));
                    if (th > -0.05f) a = Mathf.Max(a, lead * 0.95f);
                    a *= (0.35f + 0.65f * r) * mask;
                    if (v < 0f && Mathf.Abs(th) > 1.5f) a = 0f;
                    buf[y * N + x] = C(0.55f, 1f, 0.8f, a);
                }
            tex.SetPixels32(buf); tex.Apply(false, true);
            return tex;
        }

        static Texture2D BakeRing()
        {
            const int N = 128;
            var tex = NewTex(N, N, "Radar_Ring");
            var buf = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float a = 1f - Smooth(0f, 0.04f, Mathf.Abs(r - 0.955f));
                    buf[y * N + x] = C(1f, 1f, 1f, a);
                }
            tex.SetPixels32(buf); tex.Apply(false, true);
            return tex;
        }

        static Texture2D BakeBar()
        {
            const int H = 32;
            var tex = NewTex(4, H, "Radar_ScanBar");
            var buf = new Color32[4 * H];
            for (int y = 0; y < H; y++)
            {
                float v = (y + 0.5f) / H * 2f - 1f;
                float a = 1f - v * v; a *= a;
                for (int x = 0; x < 4; x++) buf[y * 4 + x] = C(0.7f, 1f, 1f, a);
            }
            tex.SetPixels32(buf); tex.Apply(false, true);
            return tex;
        }

        static float Box(float x, float y, float cx, float cy, float hx, float hy)
        {
            float qx = Mathf.Abs(x - cx) - hx, qy = Mathf.Abs(y - cy) - hy;
            float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f);
        }

        // 0 circle, 1 ring, 2 square, 3 diamond, 4 triangle(up), 5 star4, 6 keep
        static float Sdf(int id, float x, float y)
        {
            switch (id)
            {
                case 0: return Mathf.Sqrt(x * x + y * y) - 0.55f;
                case 1: return Mathf.Abs(Mathf.Sqrt(x * x + y * y) - 0.5f) - 0.11f;
                case 2: return Mathf.Max(Mathf.Abs(x), Mathf.Abs(y)) - 0.5f;
                case 3: return (Mathf.Abs(x) + Mathf.Abs(y)) * 0.7071f - 0.55f;
                case 4:
                    {
                        float l = (-1.05f * x + 0.55f * (y - 0.6f)) / 1.185f;
                        float r = (1.05f * x + 0.55f * (y - 0.6f)) / 1.185f;
                        float b = -y - 0.45f;
                        return Mathf.Max(l, Mathf.Max(r, b));
                    }
                case 5:
                    {
                        float a = Mathf.Pow(Mathf.Abs(x), 0.55f) + Mathf.Pow(Mathf.Abs(y), 0.55f) - Mathf.Pow(0.78f, 0.55f);
                        return a * 0.45f;
                    }
                default:
                    {
                        float d = Box(x, y, 0f, -0.15f, 0.55f, 0.4f);
                        d = Mathf.Min(d, Box(x, y, -0.4f, 0.42f, 0.13f, 0.17f));
                        d = Mathf.Min(d, Box(x, y, 0f, 0.42f, 0.13f, 0.17f));
                        d = Mathf.Min(d, Box(x, y, 0.4f, 0.42f, 0.13f, 0.17f));
                        return Mathf.Max(d, -Box(x, y, 0f, -0.4f, 0.14f, 0.2f));
                    }
            }
        }

        static Texture2D BakeShape(int id)
        {
            const int N = 48;
            var tex = NewTex(N, N, "Radar_Shape" + id);
            var buf = new Color32[N * N];
            float px = 2f / N;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                    float d = Sdf(id, u, v);
                    float cov = Sat(0.5f - d / px);
                    float halo = d > 0f ? 0.32f * Sat(1f - d * 4.5f) : 0f;
                    buf[y * N + x] = C(1f, 1f, 1f, Mathf.Max(cov, halo));
                }
            tex.SetPixels32(buf); tex.Apply(false, true);
            return tex;
        }

        // ---------- material / quad helpers ----------

        static GameObject MakeQuad(string n, Transform parent, Material m)
        {
            if (quadMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quadMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                Destroy(tmp);
            }
            var go = new GameObject(n);
            go.transform.SetParent(parent, false);
            go.transform.localRotation = FlatRot;
            go.AddComponent<MeshFilter>().sharedMesh = quadMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return go;
        }

        static Material NewMat(Texture2D tex, Color col, int queueOffset, bool additive, string n)
        {
            var m = new Material(Mats.Unlit(Color.white).shader) { name = "Radar_" + n };
            if (tex != null) m.SetTexture(IdBaseMap, tex);
            m.SetColor(IdBaseColor, col);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", additive ? (float)UnityEngine.Rendering.BlendMode.One : (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = Q + queueOffset;
            return m;
        }

        int Reg(Material m, Color c)
        {
            if (regN >= regM.Length) return -1;
            regM[regN] = m; regC[regN] = c;
            return regN++;
        }

        Material RegMat(Texture2D tex, Color col, int q, bool additive, string n, out int idx)
        {
            var m = NewMat(tex, col, q, additive, n);
            idx = Reg(m, col);
            return m;
        }

        void ApplyFade()
        {
            for (int i = 0; i < regN; i++)
            {
                var c = regC[i];
                regM[i].SetColor(IdBaseColor, new Color(c.r, c.g, c.b, c.a * fade));
            }
            appliedFade = fade;
        }

        void SetDyn(Material m, Color c, float a)
        {
            m.SetColor(IdBaseColor, new Color(c.r, c.g, c.b, a * fade));
        }

        Pool MakePool(string n, Texture2D tex, Color col, float size, int q, bool bump, bool smooth)
        {
            int idx;
            var m = RegMat(tex, col, q, false, n, out idx);
            return new Pool { parent = body, mat = m, size = size, name = n, bump = bump, smooth = smooth };
        }

        // ---------- build ----------

        void BuildVisuals()
        {
            root = new GameObject("RadarRoot").transform;
            root.SetParent(transform, false);
            body = new GameObject("RadarBody").transform;
            body.SetParent(root, false);

            var texDisc = BakeDisc(); var texFace = BakeFace(); var texBezel = BakeBezel(); var texUnder = BakeUnder();
            var texSweep = BakeSweep(); var texRing = BakeRing(); var texBar = BakeBar();
            var shCircle = BakeShape(0); var shRing = BakeShape(1); var shSquare = BakeShape(2); var shDiamond = BakeShape(3);
            var shTri = BakeShape(4); var shStar = BakeShape(5); var shKeep = BakeShape(6);

            int ix;
            float D = DiscDiameter;

            // dome stack
            var under = MakeQuad("BezelUnder", body, RegMat(texUnder, new Color(0.72f, 0.08f, 0.12f, 1f), 0, false, "Under", out iUnder));
            under.transform.localScale = new Vector3(D * BezelScale, D * BezelScale, 1f);
            under.transform.localPosition = new Vector3(0f, -0.0010f, 0f);

            var backing = MakeQuad("Backing", body, RegMat(texDisc, Color.white, 1, false, "Backing", out ix));
            backing.transform.localScale = new Vector3(D, D, 1f);
            backing.transform.localPosition = new Vector3(0f, -0.0004f, 0f);

            faceMat = NewMat(texFace, Color.white, 2, false, "Face");
            var face = MakeQuad("Face", body, faceMat);
            face.transform.localScale = new Vector3(D, D, 1f);

            // pulsing range rings
            for (int i = 0; i < 2; i++)
            {
                pulseMats[i] = NewMat(texRing, new Color(0.4f, 0.95f, 1f, 0f), 3, true, "Pulse" + i);
                var pr = MakeQuad("Pulse" + i, body, pulseMats[i]);
                pr.transform.localPosition = new Vector3(0f, 0.0003f, 0f);
                pulseRings[i] = pr.transform;
            }

            // surfaces / buildings
            poolSurf = MakePool("Surf", shSquare, new Color(0.3f, 0.85f, 1f, 0.26f), 1f, 4, false, false);
            poolBldDef = MakePool("BldDef", shSquare, new Color(0.65f, 0.97f, 1f, 0.90f), 0.0060f, 5, false, false);
            poolBldEco = MakePool("BldEco", shRing, new Color(1f, 0.86f, 0.36f, 0.95f), 0.0085f, 5, false, false);

            // sweep beam + scanline shimmer (additive)
            var sw = MakeQuad("Sweep", body, RegMat(texSweep, new Color(0.5f, 1f, 0.8f, 0.60f), 6, true, "Sweep", out ix));
            sw.transform.localScale = new Vector3(D, D, 1f);
            sw.transform.localPosition = new Vector3(0f, 0.0010f, 0f);
            sweepQuad = sw.transform;
            var bar = MakeQuad("ScanBar", body, RegMat(texBar, new Color(0.6f, 1f, 1f, 0.20f), 7, true, "Bar", out ix));
            bar.transform.localPosition = new Vector3(0f, 0.0012f, 0f);
            scanBar = bar.transform;

            // keep icon + halo
            haloMat = RegMat(texRing, new Color(0.9f, 0.6f, 0.4f, 0.8f), 8, true, "KeepHalo", out iHalo);
            var halo = MakeQuad("KeepHalo", body, haloMat);
            halo.transform.localScale = new Vector3(0.026f, 0.026f, 1f);
            halo.SetActive(false);
            hallHalo = halo.transform;
            var keep = MakeQuad("Hall", body, RegMat(shKeep, new Color(0.96f, 0.76f, 0.22f, 1f), 9, false, "Keep", out iKeep));
            keep.transform.localScale = new Vector3(0.0165f, 0.0165f, 1f);
            keep.SetActive(false);
            hallDot = keep.transform;

            // player marker (forward-pointing)
            var me = MakeQuad("Player", body, RegMat(shTri, new Color(1f, 1f, 1f, 1f), 10, false, "Player", out ix));
            me.transform.localScale = new Vector3(0.0105f, 0.0105f, 1f);
            me.transform.localPosition = new Vector3(0f, 0.0030f, 0f);

            // hostile blips
            poolGoblin = MakePool("Goblin", shCircle, new Color(1f, 0.20f, 0.16f, 1f), 0.0080f, 11, true, true);
            poolClimber = MakePool("Climber", shTri, new Color(0.62f, 1f, 0.25f, 1f), 0.0110f, 11, true, true);
            poolSiege = MakePool("Siege", shSquare, new Color(0.78f, 0.32f, 1f, 1f), 0.0105f, 12, true, true);
            poolFlying = MakePool("Flying", shDiamond, new Color(1f, 0.62f, 0.10f, 1f), 0.0120f, 12, true, true);
            poolWarlord = MakePool("Warlord", shStar, new Color(1f, 0.84f, 0.25f, 1f), 0.0150f, 13, true, true);
            poolBoss = MakePool("Boss", shRing, new Color(1f, 0.18f, 0.24f, 1f), 0.0270f, 13, true, true);
            poolBossCore = MakePool("BossCore", shCircle, new Color(1f, 0.92f, 0.9f, 1f), 0.0085f, 14, false, true);

            // ping ripples
            for (int i = 0; i < RippleCount; i++)
            {
                var rm = NewMat(texRing, new Color(1f, 0.4f, 0.3f, 0f), 15, true, "Ripple" + i);
                var go = MakeQuad("Ripple" + i, body, rm);
                go.SetActive(false);
                ripples[i] = new Ripple { t = go.transform, go = go, m = rm };
            }

            // rim danger arrows
            arrowMat = NewMat(shTri, new Color(1f, 0.35f, 0.15f, 1f), 16, false, "Danger");
            arrowEliteMat = NewMat(shTri, new Color(1f, 0.15f, 0.35f, 1f), 16, false, "DangerElite");
            for (int s = 0; s < Sectors; s++)
            {
                float a = s * (360f / Sectors);
                var go = MakeQuad("Danger" + s, body, arrowMat);
                float rad = a * Mathf.Deg2Rad;
                float rr = D * 0.5f + 0.0035f;
                go.transform.localPosition = new Vector3(Mathf.Sin(rad) * rr, 0.0030f, Mathf.Cos(rad) * rr);
                go.transform.localRotation = Quaternion.Euler(0f, a, 0f) * FlatRot;
                go.transform.localScale = new Vector3(0.011f, 0.011f, 1f);
                go.SetActive(false);
                sectorArrows[s] = go.transform;
                sectorRends[s] = go.GetComponent<Renderer>();
            }

            // original behind-the-player chevron (opaque unlit bars)
            arrowMatBright = Mats.Unlit(new Color(1f, 0.25f, 0.2f, 1f));
            arrowMatDim = Mats.Unlit(new Color(0.55f, 0.08f, 0.06f, 1f));
            arrow = new GameObject("BehindArrow").transform;
            arrow.SetParent(body, false);
            arrowA = Prim(PrimitiveType.Cube, arrowMatBright, arrow, "A");
            arrowB = Prim(PrimitiveType.Cube, arrowMatBright, arrow, "B");
            arrowA.localScale = new Vector3(0.003f, 0.001f, 0.012f);
            arrowB.localScale = new Vector3(0.003f, 0.001f, 0.012f);
            arrowA.localPosition = new Vector3(-0.0035f, 0.003f, -0.0035f);
            arrowB.localPosition = new Vector3(0.0035f, 0.003f, -0.0035f);
            arrowA.localRotation = Quaternion.Euler(0f, 40f, 0f);
            arrowB.localRotation = Quaternion.Euler(0f, -40f, 0f);
            arrowRendA = arrowA.GetComponent<Renderer>();
            arrowRendB = arrowB.GetComponent<Renderer>();
            arrow.gameObject.SetActive(false);

            // bezel last so the ornate ring draws over the underlay and the rim arrows
            var bz = MakeQuad("Bezel", body, RegMat(texBezel, new Color(0.96f, 0.76f, 0.22f, 1f), 1, false, "Bezel", out iBezel));
            bz.transform.localScale = new Vector3(D * BezelScale, D * BezelScale, 1f);
            bz.transform.localPosition = new Vector3(0f, 0.0006f, 0f);

            // bar width is updated per frame; scale Y fixed
            scanBar.localScale = new Vector3(D, 0.006f, 1f);
        }

        static Transform Prim(PrimitiveType type, Material m, Transform parent, string n)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = n;
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        // ---------- civilization tint ----------

        void ApplyCiv()
        {
            civDirty = false;
            Color acc = new Color(0.96f, 0.76f, 0.22f), ban = new Color(0.72f, 0.08f, 0.12f);
            var p = CivilizationProfile.Current;
            if (p != null) { acc = p.emblemColor; ban = p.bannerColor; }
            acc = Color.Lerp(acc, Color.white, 0.10f);
            float lum = ban.r * 0.3f + ban.g * 0.59f + ban.b * 0.11f;
            if (lum < 0.14f) ban = Color.Lerp(ban, Color.white, 0.22f);
            Color halo = Color.Lerp(ban, Color.white, 0.45f);
            if (iBezel >= 0) regC[iBezel] = new Color(acc.r, acc.g, acc.b, 1f);
            if (iUnder >= 0) regC[iUnder] = new Color(ban.r, ban.g, ban.b, 1f);
            if (iKeep >= 0) regC[iKeep] = new Color(acc.r, acc.g, acc.b, 1f);
            if (iHalo >= 0) regC[iHalo] = new Color(halo.r, halo.g, halo.b, 0.8f);
            appliedFade = -1f;
        }

        // ---------- lifecycle ----------

        void EnsureSubscribed()
        {
            var gd = GameDirector.Instance;
            if (gd == subscribed) return;
            Unsubscribe();
            subscribed = gd;
            if (subscribed != null) subscribed.GameReset += OnGameReset;
        }

        void Unsubscribe()
        {
            if (subscribed != null) subscribed.GameReset -= OnGameReset;
            subscribed = null;
        }

        void OnGameReset()
        {
            tickTimer = 0f;
            hasTarget = false;
            seen.Clear();
            HidePools();
            for (int i = 0; i < RippleCount; i++) { ripples[i].on = false; ripples[i].go.SetActive(false); }
            for (int s = 0; s < Sectors; s++) { sectorSev[s] = 0; sectorApplied[s] = 0; sectorArrows[s].gameObject.SetActive(false); }
            if (hallDot != null) hallDot.gameObject.SetActive(false);
            if (hallHalo != null) hallHalo.gameObject.SetActive(false);
            if (arrow != null) arrow.gameObject.SetActive(false);
            arrowOn = false;
        }

        void HidePools()
        {
            poolGoblin.HideAll(); poolClimber.HideAll(); poolFlying.HideAll(); poolSiege.HideAll();
            poolWarlord.HideAll(); poolBoss.HideAll(); poolBossCore.HideAll();
            poolSurf.HideAll(); poolBldDef.HideAll(); poolBldEco.HideAll();
        }

        void Update()
        {
            EnsureSubscribed();
            bool want = subscribed != null && subscribed.IsCombatPhase;
            float dt = Time.unscaledDeltaTime;
            if (want != visible)
            {
                visible = want;
                if (want) { root.gameObject.SetActive(true); tickTimer = TickInterval; hasTarget = false; }
            }
            fade = Mathf.MoveTowards(fade, want ? 1f : 0f, dt * 3.5f);
            if (!want && fade <= 0f)
            {
                if (root.gameObject.activeSelf) root.gameObject.SetActive(false);
                return;
            }
            if (!root.gameObject.activeSelf) root.gameObject.SetActive(true);

            if (civDirty) ApplyCiv();
            if (!Mathf.Approximately(fade, appliedFade)) ApplyFade();

            if (want)
            {
                tickTimer += dt;
                if (tickTimer >= TickInterval) { tickTimer = 0f; Tick(); }
            }

            float t = Time.unscaledTime;
            if (hasTarget)
            {
                float k = 1f - Mathf.Exp(-14f * dt);
                root.position = Vector3.Lerp(root.position, targetPos, k);
                root.rotation = Quaternion.Slerp(root.rotation, targetRot, k);
            }

            // idle bob + pop-in scale
            float ease = 1f - (1f - fade) * (1f - fade);
            body.localPosition = new Vector3(0f, Mathf.Sin(t * 1.6f) * 0.0025f, 0f);
            body.localRotation = Quaternion.Euler(Mathf.Sin(t * 1.1f) * 1.2f, 0f, Mathf.Sin(t * 0.9f + 1f) * 1.2f);
            body.localScale = Vector3.one * (0.8f + 0.2f * ease);

            AnimateDome(t, dt);
            AnimateBlips(t, dt);
            AnimateArrows(t);
        }

        void AnimateDome(float t, float dt)
        {
            const float D = DiscDiameter;
            SetDyn(faceMat, Color.white, 0.90f + 0.10f * Mathf.Sin(t * 1.7f));

            sweepAng = Mathf.Repeat(sweepAng + SweepDegPerSec * dt, 360f);
            sweepQuad.localRotation = Quaternion.Euler(0f, sweepAng, 0f) * FlatRot;

            for (int i = 0; i < 2; i++)
            {
                float ph = Mathf.Repeat(t / 3.4f + i * 0.5f, 1f);
                float s = D * (0.18f + 0.82f * ph);
                pulseRings[i].localScale = new Vector3(s, s, 1f);
                float a = 0.30f * Mathf.Pow(1f - ph, 1.5f) * Smooth(0f, 0.12f, ph);
                SetDyn(pulseMats[i], new Color(0.4f, 0.95f, 1f, 1f), a);
            }

            const float R = D * 0.5f;
            float z = Mathf.Lerp(-R * 0.96f, R * 0.96f, Mathf.Repeat(t / 3.8f, 1f));
            float chord = 2f * Mathf.Sqrt(Mathf.Max(0f, R * R - z * z)) * 0.97f;
            scanBar.localPosition = new Vector3(0f, 0.0012f, z);
            scanBar.localScale = new Vector3(Mathf.Max(0.001f, chord), 0.006f, 1f);

            if (hallHalo.gameObject.activeSelf)
            {
                float s = 0.026f * (1f + 0.14f * Mathf.Sin(t * 3.2f));
                hallHalo.localScale = new Vector3(s, s, 1f);
            }

            for (int i = 0; i < RippleCount; i++)
            {
                var rp = ripples[i];
                if (!rp.on) continue;
                rp.age += dt;
                float k = rp.age / rp.dur;
                if (k >= 1f) { rp.on = false; rp.go.SetActive(false); continue; }
                float e = 1f - (1f - k) * (1f - k);
                float s = Mathf.Lerp(0.006f, rp.size, e);
                rp.t.localScale = new Vector3(s, s, 1f);
                float a = (1f - k) * (1f - k) * 0.95f;
                SetDyn(rp.m, rp.col, a);
            }
        }

        void AnimateBlips(float t, float dt)
        {
            float k = 1f - Mathf.Exp(-16f * dt);
            AnimatePool(poolGoblin, k); AnimatePool(poolClimber, k); AnimatePool(poolFlying, k);
            AnimatePool(poolSiege, k); AnimatePool(poolWarlord, k); AnimatePool(poolBoss, k); AnimatePool(poolBossCore, k);
            // boss ring breathes
            float bs = poolBoss.size * (1f + 0.22f * Mathf.Sin(t * 5f));
            for (int i = 0; i < poolBoss.n; i++) poolBoss.items[i].t.localScale = new Vector3(bs, bs, 1f);
        }

        void AnimatePool(Pool p, float k)
        {
            for (int i = 0; i < p.n; i++)
            {
                var b = p.items[i];
                if (p.smooth) b.t.localPosition = Vector3.Lerp(b.t.localPosition, b.target, k);
                if (p.bump)
                {
                    float d = Mathf.Repeat(sweepAng - b.ang, 360f);
                    float s = d < 50f ? 1f + 0.7f * (1f - d / 50f) : 1f;
                    float sz = p.size * s;
                    b.t.localScale = new Vector3(sz, sz, 1f);
                }
            }
        }

        void AnimateArrows(float t)
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(t * 8f);
            for (int s = 0; s < Sectors; s++)
            {
                int sev = sectorSev[s];
                if (sev <= 0) continue;
                float sz = 0.011f * (1f + 0.35f * pulse) * (1f + 0.10f * Mathf.Min(sectorCnt[s], 5));
                if (sev >= 3) sz *= 1.3f;
                sectorArrows[s].localScale = new Vector3(sz, sz, 1f);
            }
            var am = arrowMat; var ae = arrowEliteMat;
            Color c = new Color(1f, 0.35f, 0.15f, 1f), ce = new Color(1f, 0.15f, 0.35f, 1f);
            SetDyn(am, c, 0.65f + 0.35f * pulse);
            SetDyn(ae, ce, 0.75f + 0.25f * pulse);

            if (arrowOn)
            {
                float p = 0.5f + 0.5f * Mathf.Sin(t * 9f);
                float s = 1f + 0.5f * p;
                arrow.localScale = new Vector3(s, 1f, s);
                var m = p > 0.5f ? arrowMatBright : arrowMatDim;
                arrowRendA.sharedMaterial = m;
                arrowRendB.sharedMaterial = m;
            }
        }

        // ---------- 10 Hz refresh ----------

        void Tick()
        {
            if (cam == null) cam = Camera.main;
            if (cam == null) return;

            Transform ct = cam.transform;
            Vector3 camPos = ct.position;
            Vector3 fwd = ct.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) { fwd = ct.up; fwd.y = 0f; }
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            cachedYaw = Quaternion.LookRotation(fwd, Vector3.up);
            Quaternion invYaw = Quaternion.Inverse(cachedYaw);

            UpdateAnchor(camPos);
            UpdateHall(camPos, invYaw);
            UpdateSurfaces(camPos, invYaw);
            UpdateBuildings(camPos, invYaw);
            bool behind = UpdateThreats(camPos, invYaw, fwd);
            if (behind != arrowOn)
            {
                arrowOn = behind;
                arrow.gameObject.SetActive(behind);
            }
            if (seen.Count > 4000) seen.Clear();
        }

        void UpdateAnchor(Vector3 camPos)
        {
            Vector3 pos;
            var hr = HandRig.Instance;
            Pose wrist;
            if (hr != null && hr.TryGetWrist(hr.NonDominant, out wrist))
            {
                pos = wrist.position + Vector3.up * HeightAbove;
            }
            else
            {
                // head-relative fallback: left, below, and ahead of the face
                pos = camPos + cachedYaw * new Vector3(-0.26f, -0.24f, 0.42f);
            }
            targetPos = pos;
            targetRot = cachedYaw * Quaternion.Euler(-40f, 0f, 0f);
            if (!hasTarget)
            {
                root.position = targetPos;
                root.rotation = targetRot;
                hasTarget = true;
            }
        }

        Vector3 ToRadar(Vector3 world, Vector3 camPos, Quaternion invYaw, float y, out bool clamped)
        {
            Vector3 rel = invYaw * (world - camPos);
            float x = rel.x * Scale, z = rel.z * Scale;
            float max = DiscDiameter * 0.5f - 0.004f;
            float m = Mathf.Sqrt(x * x + z * z);
            clamped = m > max;
            if (clamped) { float f = max / m; x *= f; z *= f; }
            return new Vector3(x, y, z);
        }

        void UpdateHall(Vector3 camPos, Quaternion invYaw)
        {
            var gd = subscribed;
            TownHall hall = gd != null ? gd.Hall : null;
            if (hall == null) hall = TownHall.Current;
            if (hall == null) { hallDot.gameObject.SetActive(false); hallHalo.gameObject.SetActive(false); return; }
            bool c;
            Vector3 lp = ToRadar(hall.transform.position, camPos, invYaw, 0.0050f, out c);
            hallDot.localPosition = lp;
            hallHalo.localPosition = new Vector3(lp.x, 0.0040f, lp.z);
            hallDot.gameObject.SetActive(true);
            hallHalo.gameObject.SetActive(true);
        }

        void UpdateSurfaces(Vector3 camPos, Quaternion invYaw)
        {
            var reg = SurfaceRegistry.Instance;
            int used = 0;
            if (reg != null)
            {
                var list = reg.Surfaces;
                float rangeSq = WorldRange * WorldRange;
                for (int i = 0; i < list.Count && used < MaxSurfaces; i++)
                {
                    var s = list[i];
                    if (s == null) continue;
                    Pose p = s.Pose;
                    Vector3 d = p.position - camPos; d.y = 0f;
                    if (d.sqrMagnitude > rangeSq) continue;

                    Vector3 sf = p.rotation * Vector3.forward; sf.y = 0f;
                    if (sf.sqrMagnitude < 1e-4f) sf = Vector3.forward;
                    Quaternion surfYaw = Quaternion.LookRotation(sf.normalized, Vector3.up);

                    bool c;
                    Vector3 lp = ToRadar(p.position, camPos, invYaw, 0.0016f, out c);
                    poolSurf.Set(used, lp);
                    var b = poolSurf.items[used];
                    b.t.localPosition = lp;
                    b.t.localRotation = invYaw * surfYaw * FlatRot;
                    float maxSide = DiscDiameter;
                    b.t.localScale = new Vector3(Mathf.Min(s.Size.x * Scale, maxSide), Mathf.Min(s.Size.y * Scale, maxSide), 1f);
                    used++;
                }
            }
            poolSurf.Finish(used);
        }

        void UpdateBuildings(Vector3 camPos, Quaternion invYaw)
        {
            var all = PlacedUnit.All;
            int nd = 0, ne = 0;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null) continue;
                if (p.health != null && p.health.IsDead) continue;
                bool c;
                Vector3 lp = ToRadar(p.transform.position, camPos, invYaw, 0.0030f, out c);
                if (c) continue;
                if (p.IsEconomy) { if (ne < MaxBuildings) { poolBldEco.Set(ne, lp); ne++; } }
                else if (nd < MaxBuildings) { poolBldDef.Set(nd, lp); nd++; }
            }
            poolBldDef.Finish(nd);
            poolBldEco.Finish(ne);
        }

        // ---------- threats ----------

        bool IsSkipped(int serial)
        {
            for (int i = 0; i < skipN; i++) if (skip[i] == serial) return true;
            return false;
        }

        void AddSkip(int serial) { if (skipN < skip.Length) skip[skipN++] = serial; }

        /// <summary>Contact bookkeeping: ping ripple on first sight, off-radar sector accumulation, behind-player test.</summary>
        void Notice(int serial, Vector3 wp, Vector3 lp, bool clamped, int severity)
        {
            if (seen.Add(serial)) SpawnRipple(lp, severity);

            if (clamped)
            {
                float ang = Mathf.Atan2(lp.x, lp.z) * Mathf.Rad2Deg;
                int sec = Mathf.RoundToInt(Mathf.Repeat(ang, 360f) / (360f / Sectors)) % Sectors;
                if (severity > sectorSev[sec]) sectorSev[sec] = severity;
                sectorCnt[sec]++;
            }

            Vector3 d = wp - scanCamPos; d.y = 0f;
            float sq = d.sqrMagnitude;
            if (sq > 0.09f && Vector3.Dot(d / Mathf.Sqrt(sq), scanCamFwd) < BehindDot && sq < bestBehindSq)
            {
                behindFound = true; bestBehindSq = sq; behindRel = d;
            }
        }

        void SpawnRipple(Vector3 lp, int severity)
        {
            Ripple r = null;
            for (int i = 0; i < RippleCount; i++) if (!ripples[i].on) { r = ripples[i]; break; }
            if (r == null) return;
            r.on = true; r.age = 0f;
            r.dur = severity >= 3 ? 1.3f : 0.8f;
            r.size = severity >= 3 ? 0.06f : (severity == 2 ? 0.040f : 0.030f);
            r.col = severity >= 3 ? new Color(1f, 0.35f, 0.4f, 1f) : (severity == 2 ? new Color(1f, 0.85f, 0.35f, 1f) : new Color(1f, 0.45f, 0.3f, 1f));
            r.t.localPosition = new Vector3(lp.x, 0.0050f, lp.z);
            r.t.localScale = new Vector3(0.006f, 0.006f, 1f);
            r.go.SetActive(true);
        }

        bool UpdateThreats(Vector3 camPos, Quaternion invYaw, Vector3 camFwdFlat)
        {
            scanCamPos = camPos; scanCamFwd = camFwdFlat;
            behindFound = false; bestBehindSq = float.MaxValue;
            skipN = 0;
            for (int s = 0; s < Sectors; s++) { sectorSev[s] = 0; sectorCnt[s] = 0; }
            bool c;
            Vector3 lp;

            // boss
            int nBoss = 0;
            var bosses = GoblinKingBoss.Active;
            for (int i = 0; i < bosses.Count && nBoss < 4; i++)
            {
                var b = bosses[i];
                if (b == null || !b.IsAlive || b.Unit == null) continue;
                Vector3 wp = b.Unit.transform.position;
                lp = ToRadar(wp, camPos, invYaw, 0.0070f, out c);
                poolBoss.Set(nBoss, lp);
                poolBossCore.Set(nBoss, new Vector3(lp.x, 0.0075f, lp.z));
                Notice(b.Unit.Serial, wp, lp, c, 3);
                AddSkip(b.Unit.Serial);
                nBoss++;
            }
            poolBoss.Finish(nBoss); poolBossCore.Finish(nBoss);

            // warlords
            int nW = 0;
            var wl = WarlordAiPersonalityDirector.Active;
            for (int i = 0; i < wl.Count && nW < 8; i++)
            {
                var w = wl[i];
                if (w == null || !w.IsActive || w.Monster == null || !w.Monster.IsAlive) continue;
                Vector3 wp = w.Monster.transform.position;
                lp = ToRadar(wp, camPos, invYaw, 0.0066f, out c);
                poolWarlord.Set(nW, lp);
                Notice(w.Monster.Serial, wp, lp, c, 2);
                AddSkip(w.Monster.Serial);
                nW++;
            }
            poolWarlord.Finish(nW);

            // flying threats
            int nF = 0;
            var fly = FlyingThreatUnit.Active;
            for (int i = 0; i < fly.Count && nF < 16; i++)
            {
                var f = fly[i];
                if (f == null || !f.IsAlive) continue;
                Vector3 wp = f.transform.position;
                lp = ToRadar(wp, camPos, invYaw, 0.0062f, out c);
                poolFlying.Set(nF, lp);
                Notice(f.Monster.Serial, wp, lp, c, 1);
                AddSkip(f.Monster.Serial);
                nF++;
            }
            poolFlying.Finish(nF);

            // siege (catapult / shaman)
            int nS = 0;
            var sg = SiegeCatapultUnit.Active;
            for (int i = 0; i < sg.Count && nS < 16; i++)
            {
                var s = sg[i];
                if (s == null || !s.IsAlive) continue;
                Vector3 wp = s.transform.position;
                lp = ToRadar(wp, camPos, invYaw, 0.0058f, out c);
                poolSiege.Set(nS, lp);
                Notice(s.Monster.Serial, wp, lp, c, 1);
                AddSkip(s.Monster.Serial);
                nS++;
            }

            // ground monsters: goblins, rams (siege shape), engineers (climbers)
            int nG = 0, nC = 0;
            var mon = MonsterUnit.Active;
            for (int i = 0; i < mon.Count; i++)
            {
                var m = mon[i];
                if (m == null || !m.IsAlive) continue;
                if (IsSkipped(m.Serial)) continue;
                Vector3 wp = m.transform.position;
                if (m.IsRam)
                {
                    if (nS >= 24) continue;
                    lp = ToRadar(wp, camPos, invYaw, 0.0058f, out c);
                    poolSiege.Set(nS, lp); nS++;
                    Notice(m.Serial, wp, lp, c, 2);
                }
                else if (m.IsEngineer)
                {
                    if (nC >= 16) continue;
                    lp = ToRadar(wp, camPos, invYaw, 0.0050f, out c);
                    poolClimber.Set(nC, lp); nC++;
                    Notice(m.Serial, wp, lp, c, 1);
                }
                else
                {
                    if (nG >= MaxDots) continue;
                    lp = ToRadar(wp, camPos, invYaw, 0.0044f, out c);
                    poolGoblin.Set(nG, lp); nG++;
                    Notice(m.Serial, wp, lp, c, 1);
                }
            }
            poolSiege.Finish(nS); poolClimber.Finish(nC); poolGoblin.Finish(nG);

            // rim danger arrows
            for (int s = 0; s < Sectors; s++)
            {
                int sev = sectorSev[s];
                if (sev == sectorApplied[s]) continue;
                sectorApplied[s] = sev;
                sectorArrows[s].gameObject.SetActive(sev > 0);
                if (sev > 0) sectorRends[s].sharedMaterial = sev >= 3 ? arrowEliteMat : arrowMat;
            }

            if (behindFound)
            {
                Vector3 loc = invYaw * behindRel; loc.y = 0f;
                if (loc.sqrMagnitude < 1e-6f) loc = Vector3.back;
                loc.Normalize();
                float r = DiscDiameter * 0.5f + 0.012f;
                arrow.localPosition = new Vector3(loc.x * r, 0.003f, loc.z * r);
                arrow.localRotation = Quaternion.LookRotation(loc, Vector3.up);
            }
            return behindFound;
        }
    }
}
