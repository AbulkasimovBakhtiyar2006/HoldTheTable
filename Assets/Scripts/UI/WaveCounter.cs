using TMPro;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>Wave x/3 and build countdown texts. Only rewrites TMP text when the displayed values change.</summary>
    public class WaveCounter : MonoBehaviour
    {
        public TMP_Text waveText;
        public TMP_Text countdownText;
        public const int TotalWaves = 3;

        int lastWave = -1;
        int lastCountdown = int.MinValue;

        public void Bind(TMP_Text wave, TMP_Text countdown)
        {
            waveText = wave;
            countdownText = countdown;
            if (waveText != null) waveText.fontStyle = FontStyles.Bold;
            if (countdownText != null) countdownText.fontStyle = FontStyles.Normal;
            lastWave = -1;
            lastCountdown = int.MinValue;
        }

        /// <param name="waveIndex">0-based wave index.</param>
        /// <param name="countdown">seconds left, negative hides the countdown.</param>
        public void Refresh(int waveIndex, float countdown)
        {
            int w = Mathf.Clamp(waveIndex + 1, 1, TotalWaves);
            if (w != lastWave && waveText != null)
            {
                lastWave = w;
                waveText.text = Strings.Format(Strings.Wave, w, TotalWaves);
            }
            int c = countdown < 0f ? -1 : Mathf.CeilToInt(countdown);
            if (c != lastCountdown && countdownText != null)
            {
                lastCountdown = c;
                countdownText.text = c < 0 ? string.Empty : Strings.Format(Strings.Countdown, c);
            }
        }
    }
}
