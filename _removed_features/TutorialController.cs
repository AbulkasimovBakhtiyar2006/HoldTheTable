using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HoldTheTable
{
    /// <summary>
    /// Interactive first-play tutorial. Self-bootstrapping singleton (no scene wiring needed).
    /// A floating parchment hint card (world-space canvas, ~28x10 cm) follows the head at front-low-left, billboards softly,
    /// stays away from the ImperialScroll, shows step text + progress dots + a "Skip" touch button and a pulsing highlight
    /// with a guide line toward the relevant object. Steps advance on real detected actions (phase / profile / defense count /
    /// shockwave charges). Touch buttons ("Skip" on the card, "?" near the wrist to replay) use fingertip proximity via HandRig.
    /// Persisted flag: PlayerPrefs "htt_tutorial_done".
    /// </summary>
    public class TutorialController : MonoBehaviour
    {
        public const string PrefKey = "htt_tutorial_done";
        public const int StepCount = 7;

        const float TouchRadius = 0.025f;
        const float Debounce = 0.5f;
        const float WaveDwell = 4f;
        const float CompleteShowSeconds = 3.5f;

        public static TutorialController Instance { get; private set; }
        public bool IsActive => active;
        public int Step => step;

        // ---------------- text (English, local to this file) ----------------
        static readonly string[] Titles =
        {
            "1/7  Choose your civilization",
            "2/7  Name your empire",
            "3/7  Place your Town Hall",
            "4/7  Build a defense",
            "5/7  Hold the table",
            "6/7  Shockwave",
            "7/7  Upgrades",
        };

        static readonly string[] Bodies =
        {
            "Poke a civilization card on the scroll, then go to the next page.",
            "Type a name, draw your emblem, then press Found Empire.",
            "Pinch the Town Hall token and put it on any valid surface.",
            "Grab a wall or tower token from the tray (or open the build panel) and place it near your hall.",
            "Wave {0}s away. Defend the Town Hall!",
            "Spread both palms apart quickly to blast monsters away. You have limited charges.",
            "Between waves, poke one upgrade card. It is applied at once.",
        };

        const string TextDefending = "Defend the Town Hall! Monsters are attacking.";
        const string TextWaveBuild = "Wave incoming in {0}s. Hold the table!";
        const string TextUpgradeWait = "Clear the wave, then poke one upgrade card.";
        const string TextDone = "Tutorial complete. Hold the table!";
        const string TextSkip = "Skip";
        const string TextHelp = "?";

        // ---------------- state ----------------
        bool active;
        int step = -1;
        bool completeShown;
        float completeUntil;
        float wavePhaseSince = -1f;
        bool seenUpgrade;
        bool firedFlag;
        int baseDefense;
        int baseCharges;
        int baseScrollPage;
        string baseCivId;
        float nextPoll;
        float lastPress = -10f;
        float camRetry;
        bool cardPosInit;
        string lastTitle, lastBody;
        int lastCountdown = int.MinValue;
        int lastDotStep = -2;

        GameDirector subDirector;
        ShockwaveAbility subShock;
        Camera cam;

        // card visuals
        GameObject cardRoot;
        RectTransform skipRt;
        Image skipImg;
        TextMeshProUGUI titleTmp, bodyTmp;
        readonly Image[] dots = new Image[StepCount];
        LineRenderer guide;
        Transform highlight;

        // help button visuals
        GameObject helpRoot;
        RectTransform helpRt;
        Image helpImg;

        Vector3 cardPos;
        Quaternion cardRot = Quaternion.identity;
        Vector3 lastFlatFwd = Vector3.forward;

        static readonly Color Ink = new Color(0.20f, 0.12f, 0.05f, 1f);
        static readonly Color Parchment = new Color(0.94f, 0.86f, 0.66f, 0.97f);
        static readonly Color Border = new Color(0.36f, 0.21f, 0.09f, 1f);
        static readonly Color Gold = new Color(0.96f, 0.76f, 0.22f, 1f);
        static readonly Color DotPending = new Color(0.55f, 0.43f, 0.28f, 0.55f);
        static readonly Color DotDone = new Color(0.25f, 0.55f, 0.25f, 1f);

        // ---------------- bootstrap ----------------
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("TutorialController");
            go.AddComponent<TutorialController>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildCard();
            BuildHelpButton();
            SetCardVisible(false);
            if (helpRoot != null) helpRoot.SetActive(false);
            if (PlayerPrefs.GetInt(PrefKey, 0) == 0) StartTutorial();
        }

        void OnDestroy()
        {
            Unsubscribe();
            if (Instance == this) Instance = null;
            if (cardRoot != null) Destroy(cardRoot);
            if (helpRoot != null) Destroy(helpRoot);
        }

        void Unsubscribe()
        {
            if (subDirector != null)
            {
                subDirector.GameReset -= OnGameReset;
                subDirector.PhaseChanged -= OnPhaseChanged;
            }
            subDirector = null;
            if (subShock != null) subShock.Fired -= OnShockFired;
            subShock = null;
        }

        void EnsureSubscribed()
        {
            var gd = GameDirector.Instance;
            if (gd != subDirector)
            {
                if (subDirector != null)
                {
                    subDirector.GameReset -= OnGameReset;
                    subDirector.PhaseChanged -= OnPhaseChanged;
                }
                subDirector = gd;
                if (gd != null)
                {
                    gd.GameReset += OnGameReset;
                    gd.PhaseChanged += OnPhaseChanged;
                }
            }
            var sw = ShockwaveAbility.Instance;
            if (sw != subShock)
            {
                if (subShock != null) subShock.Fired -= OnShockFired;
                subShock = sw;
                if (sw != null) sw.Fired += OnShockFired;
            }
        }

        void OnShockFired() { firedFlag = true; }

        void OnPhaseChanged(GamePhase p)
        {
            if (p == GamePhase.Wave) wavePhaseSince = Time.time;
            else wavePhaseSince = -1f;
            if (p == GamePhase.Upgrade) seenUpgrade = true;
        }

        void OnGameReset()
        {
            if (!active || completeShown) return;
            var gd = GameDirector.Instance;
            int target = gd != null && gd.Phase == GamePhase.CivilizationSetup ? 0 : Mathf.Min(step, 3);
            if (target < step) EnterStep(target, false);
        }

        // ---------------- public control ----------------
        public void StartTutorial()
        {
            active = true;
            completeShown = false;
            seenUpgrade = false;
            cardPosInit = false;
            lastDotStep = -2;
            SetCardVisible(true);
            if (helpRoot != null) helpRoot.SetActive(false);
            EnterStep(0, false);
        }

        public void Skip()
        {
            PlayerPrefs.SetInt(PrefKey, 1);
            PlayerPrefs.Save();
            active = false;
            completeShown = false;
            SetCardVisible(false);
        }

        void FinishAll()
        {
            PlayerPrefs.SetInt(PrefKey, 1);
            PlayerPrefs.Save();
            step = StepCount;
            completeShown = true;
            completeUntil = Time.time + CompleteShowSeconds;
            lastDotStep = -2;
            PlaySfx();
        }

        void PlaySfx()
        {
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Place, cardRoot != null ? cardRoot.transform.position : Vector3.zero);
        }

        // ---------------- steps ----------------
        static int MinStepForPhase(GamePhase p)
        {
            switch (p)
            {
                case GamePhase.CivilizationSetup: return 0;
                case GamePhase.HeadquartersPlacement: return 2;
                case GamePhase.BuildPhase: return 3;
                case GamePhase.Wave: return 4;
                case GamePhase.Upgrade: return 6;
                default: return StepCount;
            }
        }

        void EnterStep(int s, bool sound)
        {
            step = s;
            firedFlag = false;
            lastTitle = lastBody = null;
            lastCountdown = int.MinValue;
            lastDotStep = -2;
            var cp = CivilizationProfile.Current;
            baseCivId = cp != null ? cp.civId : "";
            baseScrollPage = ImperialScroll.Instance != null ? ImperialScroll.Instance.PageIndex : 0;
            var area = SettlementBuildArea.Instance;
            baseDefense = area != null ? area.DefenseCount : 0;
            baseCharges = ShockwaveAbility.Instance != null ? ShockwaveAbility.Instance.Charges : 0;
            var gd = GameDirector.Instance;
            if (s == 6) seenUpgrade = gd != null && gd.Phase == GamePhase.Upgrade;
            if (s == 4 && gd != null && gd.Phase == GamePhase.Wave && wavePhaseSince < 0f) wavePhaseSince = Time.time;
            if (sound) PlaySfx();
        }

        void Advance(int toStep)
        {
            if (toStep >= StepCount) { FinishAll(); return; }
            EnterStep(toStep, true);
        }

        void EvaluateStep()
        {
            var gd = GameDirector.Instance;
            if (gd == null) return;
            var ph = gd.Phase;
            if (ph == GamePhase.Victory || ph == GamePhase.Defeat) { FinishAll(); return; }

            // catch-up: the real game state is ahead of the shown step
            int min = MinStepForPhase(ph);
            if (step < min) { Advance(min); return; }

            switch (step)
            {
                case 0:
                {
                    var cp = CivilizationProfile.Current;
                    bool civChanged = cp != null && cp.civId != baseCivId;
                    bool pageChanged = ImperialScroll.Instance != null && ImperialScroll.Instance.PageIndex != baseScrollPage;
                    if (civChanged || pageChanged) Advance(1);
                    break;
                }
                case 1:
                    if (ph != GamePhase.CivilizationSetup) Advance(2);
                    break;
                case 2:
                    if (ph == GamePhase.BuildPhase || ph == GamePhase.Wave) Advance(3);
                    break;
                case 3:
                    if (Time.unscaledTime >= nextPoll)
                    {
                        nextPoll = Time.unscaledTime + 0.25f;
                        var area = SettlementBuildArea.Instance;
                        int d = area != null ? area.DefenseCount : 0;
                        if (d < baseDefense) baseDefense = d; // defense removed/reset: re-baseline
                        else if (d > baseDefense) Advance(4);
                    }
                    break;
                case 4:
                    if (ph == GamePhase.Wave && wavePhaseSince >= 0f && Time.time - wavePhaseSince >= WaveDwell) Advance(5);
                    break;
                case 5:
                {
                    var sw = ShockwaveAbility.Instance;
                    if (firedFlag || (sw != null && sw.Charges < baseCharges)) Advance(6);
                    else if (ph == GamePhase.BuildPhase) Advance(6); // wave ended without a shockwave: move on
                    break;
                }
                case 6:
                    if (ph == GamePhase.Upgrade) seenUpgrade = true;
                    else if (seenUpgrade) Advance(StepCount);
                    break;
            }
        }

        // ---------------- update ----------------
        void Update()
        {
            EnsureSubscribed();
            if (cam == null)
            {
                if (Time.unscaledTime >= camRetry) { camRetry = Time.unscaledTime + 0.5f; cam = Camera.main; }
            }

            if (!active)
            {
                UpdateHelpButton();
                return;
            }

            if (completeShown)
            {
                if (Time.time >= completeUntil)
                {
                    active = false;
                    completeShown = false;
                    SetCardVisible(false);
                    return;
                }
            }
            else
            {
                EvaluateStep();
            }

            RefreshTexts();
            RefreshDots();
            UpdateCardTransform();
            UpdateGuide();
            UpdateSkipTouch();
        }

        void RefreshTexts()
        {
            string title, body;
            if (completeShown) { title = "Ready"; body = TextDone; }
            else
            {
                int s = Mathf.Clamp(step, 0, StepCount - 1);
                title = Titles[s];
                var gd = GameDirector.Instance;
                if (s == 4)
                {
                    if (gd != null && gd.Phase == GamePhase.Wave) body = TextDefending;
                    else
                    {
                        int secs = gd != null ? Mathf.Max(0, Mathf.CeilToInt(gd.Countdown)) : 0;
                        if (secs != lastCountdown || lastBody == null)
                        {
                            lastCountdown = secs;
                            body = string.Format(TextWaveBuild, secs);
                        }
                        else body = lastBody;
                    }
                }
                else if (s == 6 && !seenUpgrade) body = TextUpgradeWait;
                else body = Bodies[s];
            }
            if (title != lastTitle) { lastTitle = title; if (titleTmp != null) titleTmp.text = title; }
            if (body != lastBody) { lastBody = body; if (bodyTmp != null) bodyTmp.text = body; }
        }

        void RefreshDots()
        {
            int cur = completeShown ? StepCount : step;
            bool pulse = !completeShown;
            if (cur != lastDotStep || pulse)
            {
                lastDotStep = cur;
                float p = 0.65f + 0.35f * Mathf.Sin(Time.time * 6f);
                for (int i = 0; i < StepCount; i++)
                {
                    if (dots[i] == null) continue;
                    if (i < cur) dots[i].color = DotDone;
                    else if (i == cur && pulse) dots[i].color = Color.Lerp(DotPending, Gold, p);
                    else dots[i].color = DotPending;
                }
            }
        }

        void UpdateCardTransform()
        {
            if (cam == null || cardRoot == null) return;
            Transform ct = cam.transform;
            Vector3 head = ct.position;
            Vector3 fwd = ct.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = lastFlatFwd; else { fwd.Normalize(); lastFlatFwd = fwd; }
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 target = head + fwd * 0.65f - right * 0.26f + Vector3.down * 0.24f;

            var scroll = ImperialScroll.Instance;
            if (scroll != null && scroll.IsOpen)
            {
                Vector3 sp = scroll.transform.position;
                Vector3 d = target - sp; d.y = 0f;
                const float minDist = 0.5f;
                if (d.magnitude < minDist)
                {
                    Vector3 dir = d.sqrMagnitude > 0.0004f ? d.normalized : -right;
                    target = new Vector3(sp.x, target.y, sp.z) + dir * minDist;
                }
            }

            float dt = Time.deltaTime;
            if (!cardPosInit) { cardPos = target; cardPosInit = true; cardRot = Quaternion.LookRotation(target - head, Vector3.up); }
            else cardPos = Vector3.Lerp(cardPos, target, 1f - Mathf.Exp(-4f * dt));
            Vector3 toCard = cardPos - head;
            if (toCard.sqrMagnitude > 0.0001f)
                cardRot = Quaternion.Slerp(cardRot, Quaternion.LookRotation(toCard, Vector3.up), 1f - Mathf.Exp(-6f * dt));
            cardRoot.transform.SetPositionAndRotation(cardPos, cardRot);
        }

        bool TryGetTarget(out Vector3 pos)
        {
            pos = default;
            var gd = GameDirector.Instance;
            if (completeShown) return false;
            switch (step)
            {
                case 0:
                case 1:
                {
                    var sc = ImperialScroll.Instance;
                    if (sc == null || !sc.IsOpen) return false;
                    pos = sc.transform.position + Vector3.up * 0.28f;
                    return true;
                }
                case 2:
                {
                    var ef = EmpireFoundationController.Instance;
                    if (ef != null && ef.hqToken != null && ef.hqToken.activeInHierarchy)
                    {
                        pos = ef.hqToken.transform.position + Vector3.up * 0.08f;
                        return true;
                    }
                    var sc = ImperialScroll.Instance;
                    if (sc != null && sc.IsOpen) { pos = sc.transform.position + Vector3.up * 0.28f; return true; }
                    return false;
                }
                case 3:
                {
                    var area = SettlementBuildArea.Instance;
                    if (area != null && area.IsFounded) { pos = area.Center + Vector3.up * 0.10f; return true; }
                    return false;
                }
                case 4:
                case 5:
                    if (gd != null && gd.Hall != null) { pos = gd.Hall.transform.position + Vector3.up * 0.22f; return true; }
                    return false;
                default:
                    return false;
            }
        }

        void UpdateGuide()
        {
            bool has = TryGetTarget(out var t);
            if (highlight != null)
            {
                if (highlight.gameObject.activeSelf != has) highlight.gameObject.SetActive(has);
                if (has)
                {
                    highlight.position = t;
                    float s = 0.045f + 0.015f * Mathf.Sin(Time.time * 6f);
                    highlight.localScale = new Vector3(s, s, s);
                }
            }
            if (guide != null)
            {
                if (guide.enabled != has) guide.enabled = has;
                if (has)
                {
                    guide.SetPosition(0, cardPos + cardRot * (Vector3.up * 0.06f));
                    guide.SetPosition(1, t);
                }
            }
        }

        // ---------------- touch buttons ----------------
        bool TipNear(Vector3 center)
        {
            var hr = HandRig.Instance;
            if (hr == null) return false;
            float r2 = TouchRadius * TouchRadius;
            if (hr.TryGetIndexTip(HandSide.Left, out var l) && (l - center).sqrMagnitude <= r2) return true;
            if (hr.TryGetIndexTip(HandSide.Right, out var r) && (r - center).sqrMagnitude <= r2) return true;
            return false;
        }

        void UpdateSkipTouch()
        {
            if (skipRt == null) return;
            bool near = TipNear(skipRt.position);
            if (skipImg != null) skipImg.color = near ? Gold : new Color(0.62f, 0.42f, 0.2f, 1f);
            if (near && Time.time - lastPress >= Debounce)
            {
                lastPress = Time.time;
                Skip();
            }
        }

        void UpdateHelpButton()
        {
            if (helpRoot == null) return;
            var hr = HandRig.Instance;
            bool show = false;
            if (hr != null && cam != null && hr.TryGetWrist(hr.NonDominant, out var wp))
            {
                show = true;
                Vector3 pos = wp.position + Vector3.up * 0.08f;
                helpRoot.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - cam.transform.position, Vector3.up));
            }
            if (helpRoot.activeSelf != show) helpRoot.SetActive(show);
            if (!show) return;
            bool near = TipNear(helpRt.position);
            if (helpImg != null) helpImg.color = near ? Color.white : Gold;
            if (near && Time.time - lastPress >= Debounce)
            {
                lastPress = Time.time;
                StartTutorial();
            }
        }

        // ---------------- construction ----------------
        void SetCardVisible(bool v)
        {
            if (cardRoot != null) cardRoot.SetActive(v);
            if (highlight != null) highlight.gameObject.SetActive(false);
            if (guide != null) guide.enabled = false;
        }

        static RectTransform NewRect(string name, Transform parent, Vector2 size, Vector2 pos)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
            return rt;
        }

        static Image NewImage(string name, Transform parent, Vector2 size, Vector2 pos, Color c)
        {
            var rt = NewRect(name, parent, size, pos);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
            return img;
        }

        static TextMeshProUGUI NewText(string name, Transform parent, Vector2 size, Vector2 pos, float fontSize, FontStyles style, TextAlignmentOptions align, Color c)
        {
            var rt = NewRect(name, parent, size, pos);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.fontSize = fontSize;
            t.fontStyle = style;
            t.alignment = align;
            t.color = c;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            return t;
        }

        void BuildCard()
        {
            cardRoot = new GameObject("TutorialCard");
            cardRoot.transform.SetParent(transform, false);
            var canvas = cardRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var crt = cardRoot.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(280f, 100f);
            crt.localScale = Vector3.one * 0.001f; // 280 x 100 units = 28 x 10 cm

            NewImage("Border", cardRoot.transform, new Vector2(280f, 100f), Vector2.zero, Border);
            NewImage("Parchment", cardRoot.transform, new Vector2(272f, 92f), Vector2.zero, Parchment);
            titleTmp = NewText("Title", cardRoot.transform, new Vector2(256f, 22f), new Vector2(0f, 33f), 16f, FontStyles.Bold, TextAlignmentOptions.Left, Ink);
            bodyTmp = NewText("Body", cardRoot.transform, new Vector2(256f, 40f), new Vector2(0f, 6f), 11.5f, FontStyles.Normal, TextAlignmentOptions.TopLeft, Ink);
            for (int i = 0; i < StepCount; i++)
                dots[i] = NewImage("Dot" + i, cardRoot.transform, new Vector2(10f, 10f), new Vector2(-118f + i * 15f, -34f), DotPending);

            skipImg = NewImage("Skip", cardRoot.transform, new Vector2(56f, 22f), new Vector2(100f, -34f), new Color(0.62f, 0.42f, 0.2f, 1f));
            skipRt = skipImg.rectTransform;
            var skipTxt = NewText("SkipText", skipRt, new Vector2(56f, 22f), Vector2.zero, 12f, FontStyles.Bold, TextAlignmentOptions.Center, Color.white);
            skipTxt.text = TextSkip;

            // highlight sphere + guide line (world space, outside the card)
            var sph = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sph.name = "TutorialHighlight";
            var col = sph.GetComponent<Collider>();
            if (col != null) Destroy(col);
            sph.GetComponent<Renderer>().sharedMaterial = Mats.Transparent(new Color(1f, 0.85f, 0.25f, 0.45f));
            sph.transform.SetParent(transform, false);
            sph.SetActive(false);
            highlight = sph.transform;

            var lgo = new GameObject("TutorialGuide");
            lgo.transform.SetParent(transform, false);
            guide = lgo.AddComponent<LineRenderer>();
            guide.useWorldSpace = true;
            guide.positionCount = 2;
            guide.startWidth = 0.004f;
            guide.endWidth = 0.002f;
            guide.sharedMaterial = Mats.Transparent(new Color(1f, 0.85f, 0.25f, 0.6f));
            guide.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            guide.receiveShadows = false;
            guide.enabled = false;
        }

        void BuildHelpButton()
        {
            helpRoot = new GameObject("TutorialHelpButton");
            helpRoot.transform.SetParent(transform, false);
            var canvas = helpRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = helpRoot.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(40f, 40f);
            rt.localScale = Vector3.one * 0.001f; // 4 x 4 cm
            NewImage("Ring", helpRoot.transform, new Vector2(40f, 40f), Vector2.zero, Border);
            helpImg = NewImage("Face", helpRoot.transform, new Vector2(34f, 34f), Vector2.zero, Gold);
            helpRt = helpImg.rectTransform;
            var t = NewText("Q", helpRt, new Vector2(34f, 34f), Vector2.zero, 24f, FontStyles.Bold, TextAlignmentOptions.Center, Ink);
            t.text = TextHelp;
        }
    }
}
