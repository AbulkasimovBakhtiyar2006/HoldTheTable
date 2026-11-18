using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HoldTheTable
{
    /// <summary>
    /// Records per-second gold/timber income samples and extended match stats (kills, goblins knocked off the table,
    /// ladders/ropes toppled, buildings raised, keep damage) and renders a "War Chronicle" panel (polyline graph baked
    /// into one shared Texture2D + stat lines) under the EndScreen canvas on Victory/Defeat.
    /// Self-creating singleton (no scene wiring needed). Resets on GameReset / new empire founding.
    /// </summary>
    public class WarChronicleTracker : MonoBehaviour
    {
        public const int MaxSamples = 1800;
        const int GraphW = 470, GraphH = 190, GraphPad = 8;

        public static WarChronicleTracker Instance { get; private set; }

        // ---- public stats
        public int Kills { get; private set; }
        public int KnockedOffTable { get; private set; }
        public int LaddersToppled { get; private set; }
        public int BuildingsBuilt { get; private set; }
        public float KeepDamageTaken { get; private set; }
        public float GoldEarned { get; private set; }
        public float TimberEarned { get; private set; }
        public float MatchSeconds { get; private set; }
        public int SampleCount => count;
        public float SampleInterval => interval;
        public float GetGoldSample(int i) => gold[i];
        public float GetTimberSample(int i) => timber[i];

        readonly float[] gold = new float[MaxSamples];
        readonly float[] timber = new float[MaxSamples];
        int count;
        float interval = 1f;
        float acc;

        GameDirector gd;
        GamePhase prevPhase = GamePhase.CivilizationSetup;
        Health hallHealth;
        float lastHallHp;
        int lastUnitCount;
        bool subscribed;

        // ---- graph / panel
        Texture2D tex;
        Color32[] pixels;
        RectTransform panel;
        RawImage graphImage;
        TMP_Text statsText, goldLegend, timberLegend;
        Canvas panelCanvas;

        static readonly Color32 BgCol = new Color32(217, 196, 161, 255);
        static readonly Color32 GridCol = new Color32(181, 145, 99, 255);
        static readonly Color32 GoldCol = new Color32(176, 120, 8, 255);
        static readonly Color32 TimberCol = new Color32(38, 110, 48, 255);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (Instance != null) return;
            var go = new GameObject("WarChronicleTracker");
            go.AddComponent<WarChronicleTracker>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void OnEnable()
        {
            if (subscribed) return;
            subscribed = true;
            MonsterUnit.Killed += OnKilled;
            MonsterUnit.FellOffTable += OnFellOff;
            ClimbRoutes.Announcement += OnAnnouncement;
        }

        void OnDisable()
        {
            if (!subscribed) return;
            subscribed = false;
            MonsterUnit.Killed -= OnKilled;
            MonsterUnit.FellOffTable -= OnFellOff;
            ClimbRoutes.Announcement -= OnAnnouncement;
            Unbind();
        }

        void OnDestroy()
        {
            if (tex != null) Destroy(tex);
            if (panel != null) Destroy(panel.gameObject);
            if (Instance == this) Instance = null;
        }

        void OnKilled(MonsterUnit m) { Kills++; }
        void OnFellOff(MonsterUnit m) { KnockedOffTable++; }
        void OnAnnouncement(string text, Vector3 pos)
        {
            if ((object)text == (object)Strings.ClimbAccessDestroyed || text == Strings.ClimbAccessDestroyed) LaddersToppled++;
        }

        void Unbind()
        {
            if (gd != null) { gd.PhaseChanged -= OnPhaseChanged; gd.GameReset -= OnGameReset; }
            gd = null;
        }

        void Bind(GameDirector d)
        {
            Unbind();
            gd = d;
            if (gd == null) return;
            gd.PhaseChanged += OnPhaseChanged;
            gd.GameReset += OnGameReset;
            prevPhase = gd.Phase;
        }

        void OnGameReset() { ResetAll(); HidePanel(); }

        void OnPhaseChanged(GamePhase p)
        {
            if (p == GamePhase.BuildPhase && (prevPhase == GamePhase.HeadquartersPlacement || prevPhase == GamePhase.CivilizationSetup))
                ResetAll(); // empire founded: fresh chronicle
            if (p == GamePhase.Victory || p == GamePhase.Defeat) ShowPanel();
            else HidePanel();
            prevPhase = p;
        }

        /// <summary>Clears all samples and counters.</summary>
        public void ResetAll()
        {
            count = 0; interval = 1f; acc = 0f;
            Kills = 0; KnockedOffTable = 0; LaddersToppled = 0; BuildingsBuilt = 0;
            KeepDamageTaken = 0f; GoldEarned = 0f; TimberEarned = 0f; MatchSeconds = 0f;
            lastHallHp = hallHealth != null ? hallHealth.Current : 0f;
            lastUnitCount = BuildingFactory.Instance != null ? BuildingFactory.Instance.Units.Count : 0;
        }

        void Update()
        {
            var d = GameDirector.Instance;
            if (d != gd) Bind(d);
            if (gd == null) return;

            // keep damage (polled so heals between hits do not hide damage)
            var h = gd.Hall != null ? gd.Hall.health : null;
            if (h != hallHealth) { hallHealth = h; lastHallHp = h != null ? h.Current : 0f; }
            if (hallHealth != null)
            {
                float cur = hallHealth.Current;
                if (cur < lastHallHp) KeepDamageTaken += lastHallHp - cur;
                lastHallHp = cur;
            }

            // buildings built (count increases of the factory's unit list)
            int units = BuildingFactory.Instance != null ? BuildingFactory.Instance.Units.Count : 0;
            if (units > lastUnitCount) BuildingsBuilt += units - lastUnitCount;
            lastUnitCount = units;

            var ph = gd.Phase;
            if (ph != GamePhase.BuildPhase && ph != GamePhase.Wave && ph != GamePhase.Upgrade) return;

            float dt = Time.deltaTime;
            MatchSeconds += dt;
            GoldEarned += gd.GoldRate * dt;
            TimberEarned += gd.TimberRate * dt;
            acc += dt;
            while (acc >= interval)
            {
                acc -= interval;
                if (count >= MaxSamples) Decimate();
                gold[count] = gd.GoldRate;
                timber[count] = gd.TimberRate;
                count++;
            }
        }

        void Decimate()
        {
            int n = count / 2;
            for (int i = 0; i < n; i++)
            {
                gold[i] = (gold[2 * i] + gold[2 * i + 1]) * 0.5f;
                timber[i] = (timber[2 * i] + timber[2 * i + 1]) * 0.5f;
            }
            count = n;
            interval *= 2f;
        }

        // ------------------------------------------------------------------ panel
        void EnsurePanel()
        {
            var es = EndScreen.Instance;
            if (es == null || es.canvas == null) return;
            if (panel != null && panelCanvas == es.canvas) return;
            if (panel != null) Destroy(panel.gameObject);
            panelCanvas = es.canvas;
            var root = (RectTransform)panelCanvas.transform;
            // sits directly under the 800x500 EndContent plaque
            panel = UiKit.FramedPanel(root, "WarChroniclePanel", new Vector2(0f, -425f), new Vector2(800f, 340f),
                UiKit.ParchmentEdge, UiKit.Parchment, UiKit.ParchmentEdge, 3f);
            var title = UiKit.Label(panel, Strings.ChronicleTitle, new Vector2(0f, 138f), new Vector2(740f, 44f), 34);
            title.fontStyle = FontStyles.Bold;
            title.color = UiKit.RubricCrimson;

            if (tex == null)
            {
                tex = new Texture2D(GraphW, GraphH, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "WarChronicleGraph" };
                pixels = new Color32[GraphW * GraphH];
            }
            graphImage = UiKit.RawImage(panel, new Vector2(-135f, 8f), new Vector2(GraphW, GraphH), tex);
            goldLegend = UiKit.Label(panel, string.Empty, new Vector2(-250f, -105f), new Vector2(230f, 28f), 20);
            goldLegend.color = new Color(GoldCol.r / 255f, GoldCol.g / 255f, GoldCol.b / 255f);
            timberLegend = UiKit.Label(panel, string.Empty, new Vector2(-20f, -105f), new Vector2(230f, 28f), 20);
            timberLegend.color = new Color(TimberCol.r / 255f, TimberCol.g / 255f, TimberCol.b / 255f);
            statsText = UiKit.Label(panel, string.Empty, new Vector2(262f, 8f), new Vector2(250f, 220f), 22, TextAlignmentOptions.Left);
            statsText.overflowMode = TextOverflowModes.Overflow;
            panel.gameObject.SetActive(false);
        }

        /// <summary>Builds (once) and shows the War Chronicle panel under the EndScreen plaque, refreshing graph and stats.</summary>
        public void ShowPanel()
        {
            EnsurePanel();
            if (panel == null) return;
            RenderGraph();
            RefreshTexts();
            panel.gameObject.SetActive(true);
        }

        public void HidePanel()
        {
            if (panel != null) panel.gameObject.SetActive(false);
        }

        void RefreshTexts()
        {
            float gp = 0f, tp = 0f;
            for (int i = 0; i < count; i++) { if (gold[i] > gp) gp = gold[i]; if (timber[i] > tp) tp = timber[i]; }
            goldLegend.text = Strings.Format(Strings.ChronicleGoldLegend, gp.ToString("0.0"));
            timberLegend.text = Strings.Format(Strings.ChronicleTimberLegend, tp.ToString("0.0"));
            int secs = Mathf.RoundToInt(MatchSeconds);
            statsText.text =
                Strings.Format(Strings.ChronicleKills, Kills) + "\n" +
                Strings.Format(Strings.ChronicleKnockedOff, KnockedOffTable) + "\n" +
                Strings.Format(Strings.ChronicleLadders, LaddersToppled) + "\n" +
                Strings.Format(Strings.ChronicleBuilt, BuildingsBuilt) + "\n" +
                Strings.Format(Strings.ChronicleDamage, Mathf.RoundToInt(KeepDamageTaken)) + "\n" +
                Strings.Format(Strings.ChronicleEarned, Mathf.RoundToInt(GoldEarned), Mathf.RoundToInt(TimberEarned)) + "\n" +
                Strings.Format(Strings.ChronicleDuration, secs / 60, (secs % 60).ToString("00"));
        }

        // ------------------------------------------------------------------ graph
        /// <summary>Bakes both income polylines (each normalised to its own peak) into the shared texture. Called on show only.</summary>
        public void RenderGraph()
        {
            if (tex == null) return;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = BgCol;
            for (int g = 0; g <= 4; g++) // horizontal grid
            {
                int y = GraphPad + g * (GraphH - 2 * GraphPad) / 4;
                for (int x = GraphPad; x < GraphW - GraphPad; x++) pixels[y * GraphW + x] = GridCol;
            }
            for (int x = GraphPad; x < GraphW - GraphPad; x++) pixels[GraphPad * GraphW + x] = new Color32(112, 82, 36, 255); // baseline
            DrawCurve(timber, TimberCol);
            DrawCurve(gold, GoldCol);
            tex.SetPixels32(pixels);
            tex.Apply(false);
        }

        void DrawCurve(float[] data, Color32 col)
        {
            if (count == 0) return;
            float max = 0.0001f;
            for (int i = 0; i < count; i++) if (data[i] > max) max = data[i];
            int w = GraphW - 2 * GraphPad, hgt = GraphH - 2 * GraphPad;
            int px = 0, py = 0;
            for (int i = 0; i < count; i++)
            {
                int x = GraphPad + (count > 1 ? Mathf.RoundToInt(i * (w - 1f) / (count - 1f)) : 0);
                int y = GraphPad + Mathf.RoundToInt(data[i] / max * (hgt - 1));
                if (i == 0) Plot(x, y, col); else Line(px, py, x, y, col);
                px = x; py = y;
            }
        }

        void Line(int x0, int y0, int x1, int y1, Color32 c)
        {
            int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                Plot(x0, y0, c);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        // 3x3 brush for a readable line on a headset
        void Plot(int x, int y, Color32 c)
        {
            for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                {
                    int xx = x + ox, yy = y + oy;
                    if (xx < 0 || yy < 0 || xx >= GraphW || yy >= GraphH) continue;
                    pixels[yy * GraphW + xx] = c;
                }
        }
    }
}
