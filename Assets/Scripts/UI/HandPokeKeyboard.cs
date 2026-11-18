using System;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HoldTheTable
{
    /// <summary>
    /// QWERTY poke keyboard for the Imperial Scroll: letters, Space, Backspace, Shift (case toggle, auto-capitalizes
    /// the first letter of each word) and Confirm. Every key is a real uGUI Button (finger poke / hand ray via ISDK).
    /// Text is limited to <see cref="CivilizationProfile.MaxNameLength"/> characters.
    /// </summary>
    public class HandPokeKeyboard : MonoBehaviour
    {
        static readonly string[] Rows = { "qwertyuiop", "asdfghjkl", "zxcvbnm" };
        const float Pitch = 64f;
        const float KeyW = 58f;
        const float KeyH = 74f;
        const float RowGap = 8f;

        public event Action<string> TextChanged;
        public event Action Confirmed;

        public TMP_Text display;
        public int maxLength = CivilizationProfile.MaxNameLength;

        readonly StringBuilder text = new StringBuilder(40);
        TMP_Text[] letterLabels;
        char[] letters;
        Button shiftButton;
        bool upper = true;
        bool built;
        float blinkTimer;
        bool caretOn = true;

        public string Text => text.ToString();
        public bool IsBuilt => built;

        public void Build(RectTransform area, TMP_Text displayText)
        {
            display = displayText;
            if (built) { RefreshDisplay(); return; }
            built = true;

            int total = 0;
            for (int r = 0; r < Rows.Length; r++) total += Rows[r].Length;
            letterLabels = new TMP_Text[total];
            letters = new char[total];

            float y0 = 1.5f * (KeyH + RowGap);
            int li = 0;
            for (int r = 0; r < Rows.Length; r++)
            {
                string row = Rows[r];
                float y = y0 - r * (KeyH + RowGap);
                float x0 = -(row.Length - 1) * Pitch * 0.5f;
                for (int i = 0; i < row.Length; i++)
                {
                    char c = row[i];
                    int idx = li;
                    var b = UiKit.ButtonSfx(area, c.ToString(), new Vector2(x0 + i * Pitch, y), new Vector2(KeyW, KeyH), () => TypeLetter(idx), 36, Sfx.KeyTap);
                    UiKit.SetButtonColor(b, UiKit.OakMid);
                    letters[idx] = c;
                    letterLabels[idx] = b.GetComponentInChildren<TMP_Text>(true);
                    li++;
                }
            }

            // bottom row: Shift | Space | Backspace | Confirm (total 608 px, fits inside 680px scroll safe zone)
            float yb = y0 - 3 * (KeyH + RowGap);
            shiftButton = UiKit.ButtonSfx(area, Strings.Shift, new Vector2(-242f, yb), new Vector2(126f, KeyH), ToggleShift, 28, Sfx.KeyTap);
            UiKit.ButtonSfx(area, Strings.Space, new Vector2(-58f, yb), new Vector2(200f, KeyH), TypeSpace, 28, Sfx.KeyTap);
            UiKit.ButtonSfx(area, Strings.Backspace, new Vector2(102f, yb), new Vector2(106f, KeyH), Backspace, 28, Sfx.KeyTap);
            var ok = UiKit.Button(area, Strings.Confirm, new Vector2(230f, yb), new Vector2(136f, KeyH), Confirm, 28);
            UiKit.SetButtonColor(ok, UiKit.ConfirmGreen);
            ApplyCase();
        }

        public void SetText(string value)
        {
            text.Length = 0;
            if (!string.IsNullOrEmpty(value)) text.Append(value.Length > maxLength ? value.Substring(0, maxLength) : value);
            upper = text.Length == 0 || text[text.Length - 1] == ' ';
            ApplyCase();
            RefreshDisplay();
        }

        void TypeLetter(int idx)
        {
            if (text.Length >= maxLength) { Deny(); return; }
            char c = upper ? char.ToUpperInvariant(letters[idx]) : letters[idx];
            text.Append(c);
            if (upper) { upper = false; ApplyCase(); } // one-shot capital, like a phone keyboard
            Changed();
        }

        void TypeSpace()
        {
            if (text.Length == 0 || text[text.Length - 1] == ' ' || text.Length >= maxLength) { Deny(); return; }
            text.Append(' ');
            upper = true;
            ApplyCase();
            Changed();
        }

        void Backspace()
        {
            if (text.Length == 0) { Deny(); return; }
            text.Length -= 1;
            upper = text.Length == 0 || text[text.Length - 1] == ' ';
            ApplyCase();
            Changed();
        }

        void ToggleShift()
        {
            upper = !upper;
            ApplyCase();
        }

        void Confirm() => Confirmed?.Invoke();

        void Deny()
        {
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Deny);
        }

        void Changed()
        {
            caretOn = true;
            blinkTimer = 0f;
            RefreshDisplay();
            TextChanged?.Invoke(text.ToString());
        }

        void ApplyCase()
        {
            if (letterLabels == null) return;
            for (int i = 0; i < letterLabels.Length; i++)
                if (letterLabels[i] != null) letterLabels[i].text = (upper ? char.ToUpperInvariant(letters[i]) : letters[i]).ToString();
            if (shiftButton != null)
                UiKit.SetButtonColor(shiftButton, upper ? UiKit.ActiveTabAmber : UiKit.OakMid);
        }

        void RefreshDisplay()
        {
            if (display == null) return;
            display.text = caretOn ? text + "|" : text + " ";
        }

        void Update()
        {
            if (display == null || !display.gameObject.activeInHierarchy) return;
            blinkTimer += Time.deltaTime;
            if (blinkTimer >= 0.5f)
            {
                blinkTimer = 0f;
                caretOn = !caretOn;
                RefreshDisplay();
            }
        }
    }
}
