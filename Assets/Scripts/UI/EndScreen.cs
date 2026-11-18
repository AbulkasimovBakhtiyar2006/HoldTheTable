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
        public const string EndlessSiegeLabel = Strings.EndlessSiege;

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

        bool showing;

        public bool IsShowing => showing;
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

        /// <summary>The result is shown on the Imperial Scroll now; this component only hosts the stats/events. Its own canvas stays off.</summary>
        void Build()
        {
            if (canvas == null) canvas = GetComponentInChildren<Canvas>(true);
            if (canvas != null) canvas.enabled = false;
        }

        /// <summary>Shows the Victory/Defeat result on the scroll, automatically reading current wave progress from GameDirector.</summary>
        public void Show(bool victory, int score)
        {
            var gd = GameDirector.Instance;
            int waveIdx = gd != null ? gd.WaveIndex : (victory ? GameDirector.WaveCount - 1 : 0);
            bool endless = IsEndlessMode || waveIdx >= GameDirector.WaveCount;
            Show(victory, score, waveIdx, endless);
        }

        /// <summary>
        /// Shows the result on the Imperial Scroll's END page with explicit waveIndex (0-based) and endlessMode state.
        /// The ENDLESS SIEGE button is offered when victory == true.
        /// </summary>
        public void Show(bool victory, int score, int waveIndex, bool endlessMode)
        {
            Build();
            IsEndlessMode = endlessMode || waveIndex >= GameDirector.WaveCount;
            LastWaveReached = Mathf.Max(1, waveIndex + 1);
            LastWavesSurvived = victory ? LastWaveReached : Mathf.Max(0, waveIndex);
            SavePersistentStats(score, LastWavesSurvived);

            string title = victory ? Strings.Victory : (IsEndlessMode ? Strings.SiegeFallen : Strings.Defeat);
            string scoreLine = Strings.Format(Strings.ScoreBest, Strings.Format(Strings.Score, score), HighScore);
            string waveLine = IsEndlessMode
                ? Strings.Format(Strings.EndlessWaveStats, LastWaveReached, LastWavesSurvived)
                : Strings.Format(Strings.WaveStats, LastWaveReached, GameDirector.WaveCount, LastWavesSurvived);

            var scroll = ImperialScroll.Instance;
            if (scroll == null) { Debug.LogWarning("[EndScreen] ImperialScroll missing: result not shown"); return; }
            showing = true;
            scroll.ShowEnd(victory, title, scoreLine, waveLine, victory);
        }

        public void Hide()
        {
            showing = false;
            if (ImperialScroll.Instance != null) ImperialScroll.Instance.HideEnd();
        }

        /// <summary>RESTART from the scroll's end page (same behaviour as the old poke button).</summary>
        public void RequestRestart()
        {
            IsEndlessMode = false;
            Hide();
            if (GameDirector.Instance != null) GameDirector.Instance.Restart();
        }

        /// <summary>ENDLESS SIEGE from the scroll's end page (same events as the old poke button).</summary>
        public void RequestEndlessSiege()
        {
            IsEndlessMode = true;
            Hide();
            OnEndlessSiegeRequested?.Invoke();
            EndlessSiegeRequested?.Invoke();
            EndlessSiegeCallback?.Invoke();
        }

        /// <summary>NEW EMPIRE from the scroll's end page.</summary>
        public void RequestNewEmpire()
        {
            IsEndlessMode = false;
            Hide();
            if (GameDirector.Instance != null) GameDirector.Instance.NewEmpire();
        }
    }
}
