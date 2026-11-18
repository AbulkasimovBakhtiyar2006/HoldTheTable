using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HoldTheTable
{
    /// <summary>Runtime uGUI builders for world-space canvases. All rects use centered anchors/pivot.</summary>
    public static class UiKit
    {
        // Parchment & Vellum Surfaces
        public static readonly Color Parchment      = new Color(0.93f, 0.87f, 0.75f, 1f); // #EDE0BF Aged Vellum
        public static readonly Color ParchmentLight = new Color(0.96f, 0.92f, 0.83f, 1f); // #F5EBD4 Highlighted Card / Input Field
        public static readonly Color ParchmentShade = new Color(0.85f, 0.77f, 0.63f, 1f); // #D9C4A1 Recessed Panel / Card Inner Well
        public static readonly Color ParchmentEdge  = new Color(0.71f, 0.57f, 0.39f, 1f); // #B59163 Burnt Umber Vignette

        // Carved Timber & Dark Frames
        public static readonly Color DarkPanel      = new Color(0.14f, 0.10f, 0.08f, 0.95f); // #241A14 Deep Carved Walnut (HUD/Modal Base)
        public static readonly Color WalnutFrame    = new Color(0.22f, 0.14f, 0.09f, 1f);    // #382417 Carved Walnut Structural Frame
        public static readonly Color OakMid         = new Color(0.34f, 0.22f, 0.14f, 1f);    // #573824 Worn Oak Key/Secondary Button
        public static readonly Color Driftwood      = new Color(0.50f, 0.37f, 0.25f, 1f);    // #805E40 Wood Bevel Highlight

        // Forged Brass, Gold & Bronze Trim
        public static readonly Color Gold           = new Color(0.78f, 0.62f, 0.24f, 1f); // #C79E3D Brushed Antique Brass (Primary Rim)
        public static readonly Color GoldBright     = new Color(0.90f, 0.76f, 0.38f, 1f); // #E6C261 Polished Royal Gold (Active Rim / Header)
        public static readonly Color BronzeShadow   = new Color(0.44f, 0.32f, 0.14f, 1f); // #705224 Cast Bronze Outer Bevel / Drop Shadow

        // Typography (Iron Gall Ink & Illuminated Script)
        public static readonly Color Ink            = new Color(0.16f, 0.11f, 0.08f, 1f); // #291C14 Primary Iron-Gall Ink
        public static readonly Color InkMuted       = new Color(0.35f, 0.26f, 0.19f, 1f); // #594230 Secondary / Subheadings
        public static readonly Color RubricCrimson  = new Color(0.48f, 0.11f, 0.09f, 1f); // #7A1C17 Manuscript Rubrication Title
        public static readonly Color ButtonText     = new Color(0.96f, 0.91f, 0.80f, 1f); // #F5E8CC Warm Ivory Text on Dark/Crimson

        // Tactile Action Buttons
        public static readonly Color ButtonColor    = new Color(0.48f, 0.13f, 0.11f, 1f); // #7A211C Deep Wax-Seal Crimson
        public static readonly Color ConfirmGreen   = new Color(0.19f, 0.34f, 0.20f, 1f); // #305733 Noble Forest Emerald
        public static readonly Color ActiveTabAmber = new Color(0.60f, 0.41f, 0.14f, 1f); // #996924 Gilded Ochre Active Toggle

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

        /// <summary>Procedural 9-sliced chamfered plaque sprite with subtle top highlight bevel, bottom shade bevel, and fine surface grain.</summary>
        public static Sprite PlaqueSprite
        {
            get
            {
                if (plaqueSprite != null) return plaqueSprite;
                const int S = 48;
                var tex = new Texture2D(S, S, TextureFormat.RGBA32, false)
                {
                    name = "UiKitPlaqueTex",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                var px = new Color32[S * S];
                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        int dx = Mathf.Min(x, S - 1 - x);
                        int dy = Mathf.Min(y, S - 1 - y);
                        // 2px chamfered corners
                        int corner = dx + dy;
                        float a = corner < 2 ? 0f : (corner == 2 ? 0.55f : 1f);
                        // subtle inner frame border & top/bottom bevel
                        float lum = 0.97f;
                        if (dx == 0 || dy == 0) lum = 0.80f;
                        else if (y >= S - 3) lum = 1.0f;
                        else if (y <= 2) lum = 0.88f;
                        else if (dx <= 2 || dy <= 2) lum = 0.94f;
                        // subtle linen/wood micro-grain
                        float grain = (((x * 7 + y * 13) & 3) - 1.5f) * 0.006f;
                        byte v = (byte)Mathf.Clamp(Mathf.RoundToInt((lum + grain) * 255f), 0, 255);
                        byte alpha = (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255);
                        px[y * S + x] = new Color32(v, v, v, alpha);
                    }
                }
                tex.SetPixels32(px);
                tex.Apply(false, true);
                plaqueSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(8, 8, 8, 8));
                plaqueSprite.name = "UiKitPlaque";
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
            bool usePlaque = size.x >= 20f && size.y >= 20f;
            img.sprite = usePlaque ? PlaqueSprite : WhiteSprite;
            img.type = usePlaque ? Image.Type.Sliced : Image.Type.Simple;
            img.color = c;
            img.raycastTarget = c.a > 0.01f;
            return rt;
        }

        /// <summary>Multi-layered medieval tabletop frame: outer walnut/bronze plaque + antique brass rim + recessed inner well + optional corner studs.</summary>
        public static RectTransform FramedPanel(RectTransform parent, string name, Vector2 anchoredPos, Vector2 size,
            Color outerColor, Color innerColor, Color rimColor, float inset = 6f, bool cornerGems = false)
        {
            var outer = Panel(parent, anchoredPos, size, outerColor);
            outer.name = name;
            outer.GetComponent<Image>().raycastTarget = false;

            var outline = outer.gameObject.AddComponent<Outline>();
            outline.effectColor = rimColor;
            outline.effectDistance = new Vector2(2f, -2f);

            var shadow = outer.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0.05f, 0.03f, 0.02f, 0.52f);
            shadow.effectDistance = new Vector2(0f, -4f);

            if (inset > 0f && size.x > inset * 2f + 8f && size.y > inset * 2f + 8f)
            {
                var inner = Panel(outer, Vector2.zero, size - new Vector2(inset * 2f, inset * 2f), innerColor);
                inner.name = name + "_Inner";
                inner.GetComponent<Image>().raycastTarget = false;
                var innerRim = inner.gameObject.AddComponent<Outline>();
                innerRim.effectColor = Color.Lerp(rimColor, BronzeShadow, 0.35f);
                innerRim.effectDistance = new Vector2(1.5f, -1.5f);
            }

            if (cornerGems) CornerGems(outer, inset + 4f, 8f, GoldBright);
            return outer;
        }

        /// <summary>Places 4 rotated-45° brass diamond studs in the corners of parent.</summary>
        public static void CornerGems(RectTransform parent, float inset = 10f, float gemSize = 8f, Color? color = null)
        {
            if (parent == null) return;
            Vector2 s = parent.sizeDelta;
            float hx = Mathf.Max(4f, s.x * 0.5f - inset);
            float hy = Mathf.Max(4f, s.y * 0.5f - inset);
            Color c = color ?? GoldBright;
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    var gem = Rect(parent, "CornerGem", new Vector2(sx * hx, sy * hy), new Vector2(gemSize, gemSize));
                    gem.localRotation = Quaternion.Euler(0f, 0f, 45f);
                    var img = gem.gameObject.AddComponent<Image>();
                    img.sprite = WhiteSprite;
                    img.color = c;
                    img.raycastTarget = false;
                }
            }
        }

        /// <summary>Ornamental manuscript horizontal divider rule with bronze drop line, end pips, and central diamond signet.</summary>
        public static RectTransform Divider(RectTransform parent, Vector2 anchoredPos, float width, bool withCenterGem = true)
        {
            var root = Rect(parent, "OrnamentalDivider", anchoredPos, new Vector2(width, 14f));
            var shadowLine = Panel(root, new Vector2(0f, -1.5f), new Vector2(width, 1.5f), BronzeShadow);
            shadowLine.GetComponent<Image>().raycastTarget = false;
            var mainLine = Panel(root, new Vector2(0f, 0.5f), new Vector2(width, 2.5f), Gold);
            mainLine.GetComponent<Image>().raycastTarget = false;

            for (int s = -1; s <= 1; s += 2)
            {
                var pip = Rect(root, "EndPip", new Vector2(s * (width * 0.5f - 3f), 0f), new Vector2(6f, 6f));
                pip.localRotation = Quaternion.Euler(0f, 0f, 45f);
                var pImg = pip.gameObject.AddComponent<Image>();
                pImg.sprite = WhiteSprite;
                pImg.color = GoldBright;
                pImg.raycastTarget = false;
            }

            if (withCenterGem)
            {
                var gem = Rect(root, "CenterGem", Vector2.zero, new Vector2(11f, 11f));
                gem.localRotation = Quaternion.Euler(0f, 0f, 45f);
                var gImg = gem.gameObject.AddComponent<Image>();
                gImg.sprite = WhiteSprite;
                gImg.color = GoldBright;
                gImg.raycastTarget = false;

                var core = Rect(root, "CenterCore", Vector2.zero, new Vector2(5f, 5f));
                core.localRotation = Quaternion.Euler(0f, 0f, 45f);
                var cImg = core.gameObject.AddComponent<Image>();
                cImg.sprite = WhiteSprite;
                cImg.color = RubricCrimson;
                cImg.raycastTarget = false;
            }
            return root;
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
            img.color = ButtonColor;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.95f, 0.89f, 0.77f, 1f); // warm ivory-gold highlight (no HDR >1.0 bloom)
            colors.pressedColor = new Color(0.74f, 0.67f, 0.58f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.55f, 0.50f, 0.45f, 0.65f);
            colors.fadeDuration = 0.06f;
            btn.colors = colors;

            // crisp antique brass rim + tabletop elevation shadow
            var rim = rt.gameObject.AddComponent<Outline>();
            rim.effectColor = Gold;
            rim.effectDistance = new Vector2(2f, -2f);

            var shadow = rt.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0.06f, 0.04f, 0.02f, 0.52f);
            shadow.effectDistance = new Vector2(0f, -3f);

            // subtle top-edge carved plaque bevel highlight
            var bevel = Rect(rt, "BevelHighlight", Vector2.zero, Vector2.zero);
            bevel.anchorMin = new Vector2(0f, 1f);
            bevel.anchorMax = new Vector2(1f, 1f);
            bevel.pivot = new Vector2(0.5f, 1f);
            bevel.anchoredPosition = new Vector2(0f, -2f);
            bevel.sizeDelta = new Vector2(-8f, 2.5f);
            var bImg = bevel.gameObject.AddComponent<Image>();
            bImg.sprite = WhiteSprite;
            bImg.color = new Color(1f, 0.94f, 0.78f, 0.20f);
            bImg.raycastTarget = false;

            var text = Label(rt, label, Vector2.zero, size - new Vector2(10f, 6f), fontSize);
            Fill(text.rectTransform, 5f);
            text.color = ButtonText;
            text.fontStyle = FontStyles.Bold;
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
