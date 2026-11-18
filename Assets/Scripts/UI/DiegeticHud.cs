using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HoldTheTable
{
    /// <summary>
    /// Combat HUD on the non-dominant wrist. Kept inside a 30° cone around the real XR camera forward:
    /// if the wrist is outside, the panel slides to the cone boundary and fades; with no hand tracking it parks
    /// softly low-left at 30°. Content scales with the camera FoV (relative to 90°). Canvas size/scale untouched.
    /// </summary>
    public class DiegeticHud : MonoBehaviour
    {
        public static DiegeticHud Instance { get; private set; }

        public Canvas canvas;
        public float wristUpOffset = 0.07f;
        public float maxAngle = 30f;
        public float parkDistance = 0.45f;
        public float followSharpness = 14f;
        public float parkSharpness = 4f;

        WaveCounter waveCounter;
        ScoreDisplay scoreDisplay;
        RectTransform content;
        RectTransform hintRect;
        CanvasGroup group;
        TMP_Text hintText;
        RectTransform hpFill;
        Image hpFillImage;
        Button skipButton;
        Camera cam;
        bool built, subscribed, skipVisible = true, placedOnce;
        string flashText;
        float flashUntil;
        float lastFovScale = -1f;

        void Awake()
        {
            Instance = this;
            Build();
        }

        void Start() { TrySubscribe(); ClimbRoutes.Announcement += OnClimbAnnouncement; }

        void OnClimbAnnouncement(string text, Vector3 where) => Flash(text);

        void OnDestroy()
        {
            if (subscribed && GameDirector.Instance != null) GameDirector.Instance.PhaseChanged -= OnPhaseChanged;
            ClimbRoutes.Announcement -= OnClimbAnnouncement;
            if (Instance == this) Instance = null;
        }

        void TrySubscribe()
        {
            if (subscribed || GameDirector.Instance == null) return;
            GameDirector.Instance.PhaseChanged += OnPhaseChanged;
            subscribed = true;
        }

        void Build()
        {
            if (built) return;
            if (canvas == null) canvas = GetComponentInChildren<Canvas>(true);
            if (canvas == null) { Debug.LogError("[DiegeticHud] canvas not assigned"); return; }
            built = true;
            var root = (RectTransform)canvas.transform;

            content = UiKit.Rect(root, "HudContent", Vector2.zero, new Vector2(400f, 260f));
            group = content.gameObject.AddComponent<CanvasGroup>();

            // Flat dark card with a hairline border. Hierarchy: wave (heading) > hall bar > resources > hint.
            UiKit.FramedPanel(content, "HudBackground", Vector2.zero, new Vector2(400f, 260f),
                UiKit.WalnutFrame, UiKit.DarkPanel, UiKit.WalnutFrame, 3f);

            var ivory = UiKit.ParchmentLight;
            var muted = new Color(0.72f, 0.68f, 0.60f);

            var wave = UiKit.Label(content, string.Empty, new Vector2(-70f, 100f), new Vector2(230f, 44f), 34, TextAlignmentOptions.Left);
            var countdown = UiKit.Label(content, string.Empty, new Vector2(120f, 100f), new Vector2(150f, 44f), 24, TextAlignmentOptions.Right);
            wave.color = UiKit.GoldBright;
            countdown.color = muted;

            var hall = UiKit.Label(content, string.Empty, new Vector2(-90f, 58f), new Vector2(190f, 32f), 24, TextAlignmentOptions.Left);
            var charges = UiKit.Label(content, string.Empty, new Vector2(100f, 58f), new Vector2(190f, 32f), 22, TextAlignmentOptions.Right);

            // hall health bar
            var track = UiKit.Panel(content, new Vector2(0f, 34f), new Vector2(360f, 14f), new Color(1f, 1f, 1f, 0.12f));
            track.name = "HallBarTrack";
            track.GetComponent<Image>().raycastTarget = false;
            hpFill = UiKit.Panel(track, Vector2.zero, new Vector2(360f, 14f), new Color(0.46f, 0.74f, 0.45f));
            hpFill.name = "HallBarFill";
            hpFill.GetComponent<Image>().raycastTarget = false;
            hpFill.anchorMin = new Vector2(0f, 0f); hpFill.anchorMax = new Vector2(1f, 1f);
            hpFill.pivot = new Vector2(0f, 0.5f);
            hpFill.offsetMin = hpFill.offsetMax = Vector2.zero;
            hpFillImage = hpFill.GetComponent<Image>();

            var timber = UiKit.Label(content, string.Empty, new Vector2(-90f, -2f), new Vector2(190f, 34f), 26, TextAlignmentOptions.Left);
            var gold = UiKit.Label(content, string.Empty, new Vector2(100f, -2f), new Vector2(190f, 34f), 26, TextAlignmentOptions.Right);

            var rule = UiKit.Panel(content, new Vector2(0f, -30f), new Vector2(360f, 2f), new Color(1f, 1f, 1f, 0.12f));
            rule.GetComponent<Image>().raycastTarget = false;

            hall.color = ivory; charges.color = muted;
            timber.color = new Color(0.86f, 0.74f, 0.52f);
            gold.color = UiKit.GoldBright;
            foreach (var t in new[] { timber, gold, charges, hall })
            {
                t.enableAutoSizing = true; t.fontSizeMin = 18; t.fontSizeMax = t.fontSize;
                t.textWrappingMode = TextWrappingModes.NoWrap;
            }
            foreach (var t in new[] { wave, countdown }) { t.fontStyle = FontStyles.Bold; t.textWrappingMode = TextWrappingModes.NoWrap; }

            hintText = UiKit.Label(content, string.Empty, new Vector2(-64f, -78f), new Vector2(236f, 76f), 22, TextAlignmentOptions.Left);
            hintText.color = ivory;
            hintText.enableAutoSizing = true; hintText.fontSizeMin = 18; hintText.fontSizeMax = 22;
            hintRect = hintText.rectTransform;

            skipButton = UiKit.Button(content, Strings.Skip, new Vector2(124f, -78f), new Vector2(116f, 64f), OnSkip, 26);
            UiKit.SetButtonColor(skipButton, UiKit.ButtonColor);

            waveCounter = gameObject.AddComponent<WaveCounter>();
            waveCounter.Bind(wave, countdown);
            scoreDisplay = gameObject.AddComponent<ScoreDisplay>();
            scoreDisplay.Bind(timber, hall, charges);
            scoreDisplay.BindGold(gold);
        }

        void OnSkip()
        {
            if (GameDirector.Instance != null && GameDirector.Instance.Phase == GamePhase.BuildPhase)
                GameDirector.Instance.SkipCountdown();
        }

        void OnPhaseChanged(GamePhase p)
        {
            if (p == GamePhase.Wave)
            {
                var gd = GameDirector.Instance;
                Flash(gd != null && gd.WaveIndex >= 2 ? Strings.RamIncoming : Strings.WaveIncoming); // WaveStart sfx: GameDirector
            }
            else if (p == GamePhase.BuildPhase && !placedOnce)
            {
                placedOnce = true;
                var prof = CivilizationProfile.Current;
                if (prof != null) Flash(Strings.Format(Strings.Founded, prof.empireName));
            }
            else if (p == GamePhase.CivilizationSetup || p == GamePhase.HeadquartersPlacement)
            {
                placedOnce = false;
            }
        }

        void Flash(string text)
        {
            flashText = text;
            flashUntil = Time.time + 2.5f;
        }

        void Update()
        {
            if (!built) return;
            if (!subscribed) TrySubscribe();
            UpdateValues();
            if (!orbitChecked) { orbit = GetComponent<OrbitAnchor>(); orbitChecked = true; }
            if (orbit == null) UpdatePose();
            else if (group != null && group.alpha < 1f) group.alpha = 1f;
        }

        OrbitAnchor orbit;
        bool orbitChecked;

        void UpdateValues()
        {
            var gd = GameDirector.Instance;
            if (gd == null) return;
            var phase = gd.Phase;

            int hp = 0, max = 0;
            if (gd.Hall != null && gd.Hall.health != null)
            {
                hp = Mathf.CeilToInt(gd.Hall.health.Current);
                max = Mathf.RoundToInt(gd.Hall.health.Max);
            }
            int charges = ShockwaveAbility.Instance != null ? ShockwaveAbility.Instance.Charges : 0;
            scoreDisplay.RefreshEconomy(Mathf.FloorToInt(gd.Timber), gd.IsCombatPhase ? gd.TimberRate : 0f, Mathf.FloorToInt(gd.Gold), gd.IsCombatPhase ? gd.GoldRate : 0f);
            scoreDisplay.RefreshStatus(hp, max, charges);
            if (hpFill != null && max > 0)
            {
                float r = Mathf.Clamp01(hp / (float)max);
                hpFill.anchorMax = new Vector2(r, 1f);
                hpFillImage.color = r > 0.55f ? new Color(0.46f, 0.74f, 0.45f) : (r > 0.3f ? new Color(0.92f, 0.70f, 0.28f) : new Color(0.88f, 0.35f, 0.28f));
            }
            waveCounter.Refresh(gd.WaveIndex, phase == GamePhase.BuildPhase ? gd.Countdown : -1f);

            bool wantSkip = phase == GamePhase.BuildPhase;
            if (wantSkip != skipVisible)
            {
                skipVisible = wantSkip;
                skipButton.gameObject.SetActive(wantSkip);
                if (hintRect != null)
                {
                    hintRect.anchoredPosition = wantSkip ? new Vector2(-64f, -78f) : new Vector2(0f, -78f);
                    hintRect.sizeDelta = wantSkip ? new Vector2(236f, 76f) : new Vector2(364f, 76f);
                }
            }

            string hint;
            if (Time.time < flashUntil && flashText != null) hint = flashText;
            else hint = HintFor(phase);
            if (!string.Equals(hintText.text, hint)) hintText.text = hint;
        }

        static string HintFor(GamePhase p)
        {
            switch (p)
            {
                case GamePhase.CivilizationSetup: return Strings.ChooseCivilization;
                case GamePhase.HeadquartersPlacement: return Strings.PlaceTownHallHint;
                case GamePhase.BuildPhase: return Strings.PlaceDefensesHint;
                case GamePhase.Wave: return Strings.ShockwaveHint;
                case GamePhase.Upgrade: return Strings.QuartermasterGreeting;
                case GamePhase.Victory: return Strings.Victory;
                case GamePhase.Defeat: return Strings.Defeat;
            }
            return string.Empty;
        }

        void UpdatePose()
        {
            if (cam == null) { cam = Camera.main; if (cam == null) return; }
            var camT = cam.transform;
            Vector3 camPos = camT.position;
            Vector3 fwd = camT.forward;

            // FoV-relative content scale (narrower FoV → smaller panel so it stays inside the view)
            float fovScale = Mathf.Clamp(cam.fieldOfView / 90f, 0.7f, 1.25f);
            if (Mathf.Abs(fovScale - lastFovScale) > 0.01f)
            {
                lastFovScale = fovScale;
                content.localScale = Vector3.one * fovScale;
            }

            var rig = HandRig.Instance;
            Vector3 target;
            float sharp;
            float alpha = 1f;
            if (rig != null && rig.TryGetWrist(rig.NonDominant, out var wrist))
            {
                target = wrist.position + Vector3.up * wristUpOffset;
                sharp = followSharpness;
                Vector3 v = target - camPos;
                float dist = Mathf.Max(0.2f, v.magnitude);
                float ang = Vector3.Angle(fwd, v);
                if (ang > maxAngle)
                {
                    Vector3 dir = Vector3.RotateTowards(fwd, v / dist, maxAngle * Mathf.Deg2Rad, 0f);
                    target = camPos + dir * Mathf.Min(dist, 0.6f);
                    alpha = Mathf.Lerp(1f, 0.35f, Mathf.InverseLerp(maxAngle, maxAngle + 30f, ang));
                    sharp = parkSharpness * 1.5f;
                }
            }
            else
            {
                // park low-left on the 30° cone
                Vector3 dir = Quaternion.AngleAxis(-21f, camT.up) * (Quaternion.AngleAxis(21f, camT.right) * fwd);
                target = camPos + dir * parkDistance;
                sharp = parkSharpness;
                alpha = 0.8f;
            }

            var t = transform; // root carries the canvas and its ISDK interaction surfaces
            float k = 1f - Mathf.Exp(-sharp * Time.deltaTime);
            t.position = Vector3.Lerp(t.position, target, k);
            Vector3 look = t.position - camPos;
            if (look.sqrMagnitude > 1e-6f) t.rotation = Quaternion.Slerp(t.rotation, Quaternion.LookRotation(look, Vector3.up), k);
            group.alpha = Mathf.Lerp(group.alpha, alpha, k);
        }
    }
}
