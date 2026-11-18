using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HoldTheTable
{
    /// <summary>Runtime uGUI builders for world-space canvases. All rects use centered anchors/pivot.</summary>
    public static class UiKit
    {
        // ---- Design tokens -------------------------------------------------------------------------
        // Light surfaces (scroll paper): one warm neutral ramp, no decoration.
        public static readonly Color Parchment      = new Color(0.945f, 0.918f, 0.855f, 1f); // page
        public static readonly Color ParchmentLight = new Color(0.985f, 0.973f, 0.945f, 1f); // card / input
        public static readonly Color ParchmentShade = new Color(0.882f, 0.847f, 0.773f, 1f); // recessed / divider
        public static readonly Color ParchmentEdge  = new Color(0.800f, 0.757f, 0.667f, 1f); // hairline border

        // Dark surfaces (HUD, modals)
        public static readonly Color DarkPanel      = new Color(0.075f, 0.067f, 0.059f, 0.90f);
        public static readonly Color WalnutFrame    = new Color(0.165f, 0.145f, 0.125f, 1f);  // raised dark / hairline on dark
        public static readonly Color OakMid         = new Color(0.31f, 0.27f, 0.23f, 1f);     // secondary button
        public static readonly Color Driftwood      = new Color(0.50f, 0.45f, 0.39f, 1f);

        // Single accent (gold) used sparingly; BronzeShadow doubles as a subtle line colour.
        public static readonly Color Gold           = new Color(0.80f, 0.64f, 0.27f, 1f);
        public static readonly Color GoldBright     = new Color(0.94f, 0.78f, 0.38f, 1f);
        public static readonly Color BronzeShadow   = new Color(0.72f, 0.66f, 0.55f, 1f);

        // Typography
        public static readonly Color Ink            = new Color(0.125f, 0.105f, 0.090f, 1f);
        public static readonly Color InkMuted       = new Color(0.40f, 0.35f, 0.30f, 1f);
        public static readonly Color RubricCrimson  = new Color(0.62f, 0.17f, 0.13f, 1f);     // emphasis / primary
        public static readonly Color ButtonText     = new Color(0.99f, 0.97f, 0.93f, 1f);

        // Actions
        public static readonly Color ButtonColor    = new Color(0.62f, 0.17f, 0.13f, 1f);     // primary
        public static readonly Color ConfirmGreen   = new Color(0.20f, 0.42f, 0.28f, 1f);
        public static readonly Color ActiveTabAmber = new Color(0.76f, 0.54f, 0.16f, 1f);

        // Type scale (px on the 800-wide canvases) -- use these instead of ad-hoc sizes.
        public const int FontDisplay = 56, FontTitle = 44, FontHeading = 36, FontBody = 28, FontCaption = 24;

        static Sprite whiteSprite;
        static Sprite plaqueSprite;

        public static Sprite WhiteSprite
        {
            get
            {
                if (whiteSprite != null) return whiteSprite;
                var tex = Texture2D.whiteTexture;
                whiteSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                whiteSprite.name = "UiKitWhite";
                return whiteSprite;
            }
        }

        /// <summary>Procedural 9-sliced rounded rectangle (anti-aliased, white) tinted via Image.color.</summary>
        public static Sprite PlaqueSprite
        {
            get
            {
                if (plaqueSprite != null) return plaqueSprite;
                const int S = 64; const float R = 22f;
                var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
                {
                    name = "UiKitRoundTex", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
                };
                var px = new Color32[S * S];
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        float cx = Mathf.Clamp(x + 0.5f, R, S - R), cy = Mathf.Clamp(y + 0.5f, R, S - R);
                        float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                        byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(R - d + 0.5f) * 255f);
                        px[y * S + x] = new Color32(255, 255, 255, a);
                    }
                tex.SetPixels32(px);
                tex.Apply(false, true);
                plaqueSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(R, R, R, R));
                plaqueSprite.name = "UiKitRound";
                return plaqueSprite;
            }
        }

        public static RectTransform Rect(RectTransform parent, string name, Vector2 anchoredPos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent != null ? parent.gameObject.layer : 5;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            return rt;
        }

        public static RectTransform Panel(RectTransform parent, Vector2 anchoredPos, Vector2 size, Color c)
        {
            var rt = Rect(parent, "Panel", anchoredPos, size);
            var img = rt.gameObject.AddComponent<Image>();
            bool usePlaque = size.x >= 14f && size.y >= 14f;
            img.sprite = usePlaque ? PlaqueSprite : WhiteSprite;
            img.type = usePlaque ? Image.Type.Sliced : Image.Type.Simple;
            if (usePlaque) img.pixelsPerUnitMultiplier = 22f / Mathf.Clamp(Mathf.Min(size.x, size.y) * 0.25f, 5f, 22f); // corner radius ≈ 25% of short side, max 22px
            img.color = c;
            img.raycastTarget = c.a > 0.01f;
            return rt;
        }

        /// <summary>Flat card: 2px border colour, fill inset, one soft drop shadow. (Name kept for existing callers.)</summary>
        public static RectTransform FramedPanel(RectTransform parent, string name, Vector2 anchoredPos, Vector2 size,
            Color outerColor, Color innerColor, Color rimColor, float inset = 6f, bool cornerGems = false)
        {
            var outer = Panel(parent, anchoredPos, size, rimColor);
            outer.name = name;
            outer.GetComponent<Image>().raycastTarget = false;
            var shadow = outer.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.28f);
            shadow.effectDistance = new Vector2(0f, -6f);

            float b = Mathf.Min(inset, 3f);
            if (b > 0f && size.x > b * 2f + 8f && size.y > b * 2f + 8f)
            {
                var inner = Panel(outer, Vector2.zero, size - new Vector2(b * 2f, b * 2f), innerColor);
                inner.name = name + "_Inner";
                inner.GetComponent<Image>().raycastTarget = false;
            }
            else outer.GetComponent<Image>().color = innerColor;
            return outer;
        }

        /// <summary>Deprecated ornament: intentionally empty. Kept so older call sites compile.</summary>
        public static void CornerGems(RectTransform parent, float inset = 10f, float gemSize = 8f, Color? color = null) { }

        /// <summary>Hairline section divider.</summary>
        public static RectTransform Divider(RectTransform parent, Vector2 anchoredPos, float width, bool withCenterGem = true)
        {
            var line = Panel(parent, anchoredPos, new Vector2(width, 2f), ParchmentEdge);
            line.name = "Divider";
            line.GetComponent<Image>().raycastTarget = false;
            return line;
        }

        /// <summary>Small rounded tag/pill with text, e.g. status chips.</summary>
        public static TMP_Text Pill(RectTransform parent, string text, Vector2 pos, Vector2 size, Color fill, Color textColor, int fontSize = 22)
        {
            var p = Panel(parent, pos, size, fill);
            p.name = "Pill";
            p.GetComponent<Image>().raycastTarget = false;
            var t = Label(p, text, Vector2.zero, size, fontSize);
            t.color = textColor; t.fontStyle = FontStyles.Bold;
            return t;
        }

        public static Button Button(RectTransform parent, string label, Vector2 pos, Vector2 size, Action onClick, int fontSize = 36)
            => ButtonSfx(parent, label, pos, size, onClick, fontSize, Sfx.Click);

        /// <summary>Same as Button but plays the given sound on click (e.g. Sfx.KeyTap for keyboard keys).</summary>
        public static Button ButtonSfx(RectTransform parent, string label, Vector2 pos, Vector2 size, Action onClick, int fontSize, Sfx clickSfx)
        {
            size.x = Mathf.Max(size.x, 36f);
            size.y = Mathf.Max(size.y, 36f);
            var rt = Rect(parent, "Button_" + label, pos, size);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = PlaqueSprite;
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 22f / Mathf.Clamp(Mathf.Min(size.x, size.y) * 0.28f, 6f, 22f);
            img.color = ButtonColor;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.12f, 1.08f, 1.04f, 1f);
            colors.pressedColor = new Color(0.78f, 0.74f, 0.70f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.55f, 0.50f, 0.45f, 0.65f);
            colors.fadeDuration = 0.06f;
            btn.colors = colors;

            var shadow = rt.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.25f);
            shadow.effectDistance = new Vector2(0f, -4f);

            // selection ring, off until SetSelected(true)
            var sel = rt.gameObject.AddComponent<Outline>();
            sel.effectColor = ButtonColor;
            sel.effectDistance = new Vector2(3f, -3f);
            sel.enabled = false;

            var text = Label(rt, label, Vector2.zero, size - new Vector2(10f, 6f), fontSize);
            Fill(text.rectTransform, 8f);
            text.color = ButtonText;
            text.fontStyle = FontStyles.Bold;
            text.enableAutoSizing = true; text.fontSizeMax = fontSize; text.fontSizeMin = Mathf.Max(14, fontSize * 0.6f);
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;

            if (onClick != null)
            {
                btn.onClick.AddListener(() =>
                {
                    if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(clickSfx);
                    onClick();
                });
            }
            return btn;
        }

        public static TMP_Text Label(RectTransform parent, string text, Vector2 pos, Vector2 size, int fontSize, TextAlignmentOptions align = TextAlignmentOptions.Center)
        {
            var rt = Rect(parent, "Label", pos, size);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null) t.font = TMP_Settings.defaultFontAsset;
            t.text = text;
            t.fontSize = fontSize;
            t.alignment = align;
            t.color = Ink;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        public static RawImage RawImage(RectTransform parent, Vector2 pos, Vector2 size, Texture tex)
        {
            var rt = Rect(parent, "RawImage", pos, size);
            var ri = rt.gameObject.AddComponent<RawImage>();
            ri.texture = tex;
            ri.raycastTarget = false;
            return ri;
        }

        public static void SetButtonLabel(Button b, string label)
        {
            var t = b.GetComponentInChildren<TMP_Text>(true);
            if (t != null) t.text = label;
        }

        /// <summary>Shows/hides the accent selection ring on a button created by UiKit.Button.</summary>
        public static void SetSelected(Button b, bool on, Color? ring = null)
        {
            if (b == null) return;
            var o = b.GetComponent<Outline>();
            if (o == null) return;
            o.enabled = on;
            if (ring.HasValue) o.effectColor = ring.Value;
        }

        /// <summary>Quiet button for light surfaces (Back, Roll up, Undo): sand fill, dark text.</summary>
        public static void StyleSecondary(Button b)
        {
            if (b == null) return;
            SetButtonColor(b, ParchmentShade);
            var t = b.GetComponentInChildren<TMP_Text>(true);
            if (t != null) t.color = Ink;
        }

        public static void SetButtonColor(Button b, Color c)
        {
            if (b != null && b.targetGraphic != null) b.targetGraphic.color = c;
        }

        /// <summary>Stretch a child rect to fill its parent with a uniform inset (pixels).</summary>
        public static void Fill(RectTransform rt, float inset = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(-2f * inset, -2f * inset);
        }
    }
}
