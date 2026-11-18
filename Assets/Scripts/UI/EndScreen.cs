using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HoldTheTable
{
    /// <summary>
    /// Victory/Defeat panel with score, High Score, Waves Survived / Endless Wave reached,
    /// a poke RESTART button, and an "ENDLESS SIEGE" button next to RESTART when winning Wave 3.
    /// Hidden at start.
    /// </summary>
    public class EndScreen : MonoBehaviour
    {
        public const string HighScorePrefKey = "HoldTheTable.HighScore";
        public const string BestWavePrefKey = "HoldTheTable.BestWavesSurvived";
        public const string EndlessSiegeLabel = "ENDLESS SIEGE";

        public static EndScreen Instance { get; private set; }

        /// <summary>Static event raised when the player clicks the "ENDLESS SIEGE" button after winning Wave 3.</summary>
        public static event Action EndlessSiegeRequested;

        /// <summary>Instance event raised when the player clicks the "ENDLESS SIEGE" button after winning Wave 3.</summary>
        public event Action OnEndlessSiegeRequested;

        /// <summary>Optional direct callback delegate invoked when "ENDLESS SIEGE" is requested.</summary>
        public Action EndlessSiegeCallback;

        public Canvas canvas;
        public float distance = 0.6f;
        public float below = 0.08f;

        RectTransform content;
        TMP_Text titleText, scoreText, nameText, waveStatsText;
        Image banner;
        Button restartButton;
        Button endlessButton;
        bool built;

        public bool IsShowing => content != null && content.gameObject.activeSelf;
        public bool IsEndlessMode { get; set; }
        public int HighScore { get; private set; }
        public int BestWavesSurvived { get; private set; }
        public int LastWaveReached { get; private set; }
        public int LastWavesSurvived { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Instance = null;
            EndlessSiegeRequested = null;
        }

        void Awake()
        {
            Instance = this;
            LoadPersistentStats();
            Build();
            if (content != null) content.gameObject.SetActive(false);
        }

        void Start()
        {
            if (GameDirector.Instance != null) GameDirector.Instance.GameReset += OnGameReset;
        }

        void OnDestroy()
        {
            if (GameDirector.Instance != null) GameDirector.Instance.GameReset -= OnGameReset;
            if (Instance == this) Instance = null;
        }

        void OnGameReset()
        {
            IsEndlessMode = false;
            Hide();
        }

        void LoadPersistentStats()
        {
            HighScore = PlayerPrefs.GetInt(HighScorePrefKey, 0);
            BestWavesSurvived = PlayerPrefs.GetInt(BestWavePrefKey, 0);
        }

        void SavePersistentStats(int score, int wavesSurvived)
        {
            bool dirty = false;
            if (score > HighScore)
            {
                HighScore = score;
                PlayerPrefs.SetInt(HighScorePrefKey, HighScore);
                dirty = true;
            }
            if (wavesSurvived > BestWavesSurvived)
            {
                BestWavesSurvived = wavesSurvived;
                PlayerPrefs.SetInt(BestWavePrefKey, BestWavesSurvived);
                dirty = true;
            }
            if (dirty) PlayerPrefs.Save();
        }

        void Build()
        {
            if (built) return;
            if (canvas == null) canvas = GetComponentInChildren<Canvas>(true);
            if (canvas == null) { Debug.LogError("[EndScreen] canvas not assigned"); return; }
            built = true;
            var root = (RectTransform)canvas.transform;
            content = UiKit.Rect(root, "EndContent", Vector2.zero, new Vector2(800f, 500f));

            // Layered carved walnut & antique brass memorial plaque
            UiKit.FramedPanel(content, "EndFrame", Vector2.zero, new Vector2(800f, 500f),
                UiKit.WalnutFrame, UiKit.Parchment, UiKit.Gold, 8f, true);

            // Heraldic ribbon across the top
            banner = UiKit.Panel(content, new Vector2(0f, 206f), new Vector2(764f, 22f), UiKit.ButtonColor).GetComponent<Image>();
            banner.raycastTarget = false;
            var bRim = UiKit.Panel(content, new Vector2(0f, 194f), new Vector2(764f, 2f), UiKit.Gold);
            bRim.GetComponent<Image>().raycastTarget = false;

            titleText = UiKit.Label(content, string.Empty, new Vector2(0f, 142f), new Vector2(760f, 80f), 66);
            titleText.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
            nameText = UiKit.Label(content, string.Empty, new Vector2(0f, 92f), new Vector2(760f, 38f), 28);
            nameText.fontStyle = FontStyles.Italic;
            nameText.color = UiKit.InkMuted;

            // Recessed vellum stats plaque
            var statsPlq = UiKit.FramedPanel(content, "StatsPlaque", new Vector2(0f, 16f), new Vector2(740f, 88f),
                UiKit.WalnutFrame, UiKit.ParchmentShade, UiKit.BronzeShadow, 3f, true);
            statsPlq.GetComponent<Image>().raycastTarget = false;

            scoreText = UiKit.Label(statsPlq, string.Empty, new Vector2(0f, 18f), new Vector2(710f, 40f), 34);
            scoreText.fontStyle = FontStyles.Bold;
            scoreText.color = UiKit.Ink;

            waveStatsText = UiKit.Label(statsPlq, string.Empty, new Vector2(0f, -18f), new Vector2(710f, 32f), 24);
            waveStatsText.color = UiKit.InkMuted;

            restartButton = UiKit.Button(content, Strings.Restart, new Vector2(0f, -145f), new Vector2(420f, 100f), OnRestart, 44);
            UiKit.SetButtonColor(restartButton, UiKit.ButtonColor);

            endlessButton = UiKit.Button(content, EndlessSiegeLabel, new Vector2(185f, -145f), new Vector2(340f, 100f), OnEndlessSiege, 34);
            UiKit.SetButtonColor(endlessButton, UiKit.ConfirmGreen);
            endlessButton.gameObject.SetActive(false);
        }

        /// <summary>Shows the Victory/Defeat panel, automatically reading current wave progress from GameDirector.</summary>
        public void Show(bool victory, int score)
        {
            var gd = GameDirector.Instance;
            int waveIdx = gd != null ? gd.WaveIndex : (victory ? GameDirector.WaveCount - 1 : 0);
            bool endless = IsEndlessMode || waveIdx >= GameDirector.WaveCount;
            Show(victory, score, waveIdx, endless);
        }

        /// <summary>
        /// Shows the Victory/Defeat panel with explicit waveIndex (0-based) and endlessMode state.
        /// Displays "ENDLESS SIEGE" next to "RESTART" when winning Wave 3 (victory == true).
        /// </summary>
        public void Show(bool victory, int score, int waveIndex, bool endlessMode)
        {
            Build();
            if (content == null) return;

            IsEndlessMode = endlessMode || waveIndex >= GameDirector.WaveCount;
            LastWaveReached = Mathf.Max(1, waveIndex + 1);
            LastWavesSurvived = victory ? LastWaveReached : Mathf.Max(0, waveIndex);
            SavePersistentStats(score, LastWavesSurvived);

            if (victory)
            {
                titleText.text = Strings.Victory;
                titleText.color = new Color(0.55f, 0.38f, 0.05f);
            }
            else
            {
                titleText.text = IsEndlessMode ? "SIEGE FALLEN" : Strings.Defeat;
                titleText.color = new Color(0.55f, 0.1f, 0.08f);
            }

            var prof = CivilizationProfile.Current;
            nameText.text = prof != null ? prof.empireName : string.Empty;
            if (prof != null) banner.color = prof.bannerColor;

            scoreText.text = $"{Strings.Format(Strings.Score, score)}   •   High Score {HighScore}";

            if (waveStatsText != null)
            {
                if (IsEndlessMode)
                    waveStatsText.text = $"Endless Wave Reached: {LastWaveReached}   •   Waves Survived: {LastWavesSurvived} (Best: {BestWavesSurvived})";
                else
                    waveStatsText.text = $"Wave Reached: {LastWaveReached}/{GameDirector.WaveCount}   •   Waves Survived: {LastWavesSurvived} (Best: {BestWavesSurvived})";
            }

            LayoutButtons(victory);

            PlaceInFront();
            content.gameObject.SetActive(true); // victory/defeat sfx is played by GameDirector
        }

        void LayoutButtons(bool showEndlessSiegeButton)
        {
            if (restartButton == null) return;
            var restartRt = (RectTransform)restartButton.transform;
            restartButton.interactable = true;

            if (showEndlessSiegeButton && endlessButton != null)
            {
                restartRt.anchoredPosition = new Vector2(-185f, -145f);
                restartRt.sizeDelta = new Vector2(340f, 100f);

                var endlessRt = (RectTransform)endlessButton.transform;
                endlessRt.anchoredPosition = new Vector2(185f, -145f);
                endlessRt.sizeDelta = new Vector2(340f, 100f);
                endlessButton.interactable = true;
                endlessButton.gameObject.SetActive(true);
            }
            else
            {
                restartRt.anchoredPosition = new Vector2(0f, -145f);
                restartRt.sizeDelta = new Vector2(420f, 100f);
                if (endlessButton != null) endlessButton.gameObject.SetActive(false);
            }
        }

        public void Hide()
        {
            if (content != null) content.gameObject.SetActive(false);
        }

        void OnRestart()
        {
            restartButton.interactable = false; // no double restart from a lingering poke
            if (endlessButton != null) endlessButton.interactable = false;
            IsEndlessMode = false;
            Hide();
            if (GameDirector.Instance != null) GameDirector.Instance.Restart();
        }

        void OnEndlessSiege()
        {
            if (endlessButton != null) endlessButton.interactable = false;
            if (restartButton != null) restartButton.interactable = false;
            IsEndlessMode = true;
            Hide();
            OnEndlessSiegeRequested?.Invoke();
            EndlessSiegeRequested?.Invoke();
            EndlessSiegeCallback?.Invoke();
        }

        void PlaceInFront()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var ct = cam.transform;
            Vector3 f = ct.forward; f.y = 0f;
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            f.Normalize();
            Vector3 pos = ct.position + f * distance + Vector3.down * below;
            // move the root (canvas + ISDK poke/ray surfaces created under it by the leader) as one unit
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - ct.position, Vector3.up));
        }
    }
}
