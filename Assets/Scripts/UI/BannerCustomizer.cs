using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HoldTheTable
{
    /// <summary>
    /// Heraldic banner editor on the Imperial Scroll (Banner page). Layout inside the given panel:
    ///  left  — tabs [Presets | Patterns | Emblem]: preset thumbnails, pattern thumbnails (+ Undo layer / Clear layers),
    ///          fingertip drawing pad (+ Thin / Thick / Undo / Clear);
    ///  right — live banner preview, paint target toggle [Field | Layer | Emblem], Confirm;
    ///  bottom — 18 heraldic tinctures (2 rows of 9) painting the selected target.
    /// Composite order everywhere: field color → pattern layers → drawn emblem. Everything writes CivilizationProfile.Current
    /// and calls NotifyChanged(), so the pad, the preview and the placed banner all update.
    /// </summary>
    public class BannerCustomizer : MonoBehaviour
    {
        public enum PaintTarget { Field, Layer, Emblem }

        /// <summary>Kept for compatibility: both are the full heraldic palette.</summary>
        public static readonly Color[] BannerColors = BannerPalette.All;
        public static readonly Color[] EmblemColors = BannerPalette.All;

        static readonly Color SelectedTool = new Color(0.22f, 0.11f, 0.05f, 1f);
        const float MinButton = 70f;

        public event Action Confirmed;
        public RawImage Preview => preview;
        public PaintTarget Target => target;
        public Color LayerColor => layerColor;

        RectTransform panel;
        EmblemDrawingCanvas canvas;
        RawImage preview;
        Texture2D previewTex;
        TMP_Text layerCountLabel;
        readonly List<Button> swatches = new List<Button>(18);
        readonly List<Button> tabButtons = new List<Button>(3);
        readonly List<RectTransform> tabPages = new List<RectTransform>(3);
        readonly List<Button> targetButtons = new List<Button>(3);
        Button thinBtn, thickBtn;
        PaintTarget target = PaintTarget.Field;
        Color layerColor = BannerPalette.Or;
        int tab;
        bool subscribedProfile;
        EmblemDrawingCanvas subscribedCanvas;

        public void Build(RectTransform panel, EmblemDrawingCanvas drawingCanvas)
        {
            if (panel == null) return;
            if (this.panel == panel && preview != null)
            {
                if (drawingCanvas != null && drawingCanvas != canvas) { canvas = drawingCanvas; SubscribeCanvas(); }
                Refresh();
                return;
            }
            this.panel = panel;
            canvas = drawingCanvas;

            Rect r = panel.rect;
            float w = Mathf.Max(r.width, 600f), h = Mathf.Max(r.height, 560f);
            const float m = 8f, gap = 8f;

            // ---- bottom: palette, 2 rows of 9
            var colors = BannerPalette.All;
            int perRow = (colors.Length + 1) / 2;
            float sw = Mathf.Floor(Mathf.Min(78f, (w - 2f * m - (perRow - 1) * 6f) / perRow));
            sw = Mathf.Max(sw, MinButton);
            float palBottom = -h * 0.5f + m;
            float rowStep = sw + 6f;
            swatches.Clear();
            for (int i = 0; i < colors.Length; i++)
            {
                int row = i / perRow, col = i % perRow;
                int idx = i;
                float total = perRow * sw + (perRow - 1) * 6f;
                var pos = new Vector2(-total * 0.5f + sw * 0.5f + col * (sw + 6f), palBottom + sw * 0.5f + (1 - row) * rowStep);
                var b = UiKit.Button(panel, "", pos, new Vector2(sw, sw), () => PickColor(idx), 20);
                ((RectTransform)b.transform).sizeDelta = new Vector2(sw, sw);
                b.name = "Swatch_" + i;
                UiKit.SetButtonColor(b, colors[i]);
                swatches.Add(b);
            }
            float regionBottom = palBottom + 2f * sw + 6f + 10f;
            float regionTop = h * 0.5f - m;
            float regionH = regionTop - regionBottom;

            // ---- right column: preview standard, target toggle, confirm
            float rc = Mathf.Min(220f, w * 0.32f);
            float rcx = w * 0.5f - m - rc * 0.5f;
            float pw = rc - 24f, ph = pw / EmblemRasterizer.Aspect;
            float y = regionTop;
            float clothY = y - 16f - ph * 0.5f;

            // Recessed dark walnut & brass backing for the banner preview
            var previewBacking = UiKit.FramedPanel(panel, "PreviewBacking", new Vector2(rcx, clothY), new Vector2(pw + 16f, ph + 16f),
                UiKit.WalnutFrame, UiKit.ParchmentShade, UiKit.Gold, 2.5f, true);
            previewBacking.GetComponent<Image>().raycastTarget = false;

            // Brass hanging crossbar with round finials
            var crossbar = UiKit.Panel(panel, new Vector2(rcx, clothY + ph * 0.5f + 4f), new Vector2(pw + 14f, 6f), UiKit.WalnutFrame);
            crossbar.name = "PreviewCrossbar";
            crossbar.GetComponent<Image>().raycastTarget = false;
            var finL = UiKit.Panel(panel, new Vector2(rcx - pw * 0.5f - 8f, clothY + ph * 0.5f + 4f), new Vector2(10f, 10f), UiKit.Gold);
            finL.GetComponent<Image>().raycastTarget = false;
            var finR = UiKit.Panel(panel, new Vector2(rcx + pw * 0.5f + 8f, clothY + ph * 0.5f + 4f), new Vector2(10f, 10f), UiKit.Gold);
            finR.GetComponent<Image>().raycastTarget = false;

            previewTex = EmblemRasterizer.CreateTexture(EmblemRasterizer.DefaultSize);
            previewTex.name = "BannerPreviewTex";
            preview = UiKit.RawImage(panel, new Vector2(rcx, clothY), new Vector2(pw, ph), previewTex);
            preview.name = "BannerPreview";
            preview.raycastTarget = false;
            y = clothY - ph * 0.5f - 24f;

            var paintLbl = UiKit.Label(panel, BannerStrings.PaintTarget, new Vector2(rcx, y - 13f), new Vector2(rc, 26f), 22);
            paintLbl.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
            paintLbl.color = UiKit.Ink;
            y -= 30f;
            float tbh = Mathf.Max(MinButton, Mathf.Min(72f, (y - regionBottom - 96f - 2f * 6f) / 3f));
            string[] targetNames = { BannerStrings.TargetField, BannerStrings.TargetLayer, BannerStrings.TargetEmblem };
            targetButtons.Clear();
            for (int i = 0; i < 3; i++)
            {
                var t = (PaintTarget)i;
                var b = UiKit.Button(panel, targetNames[i], new Vector2(rcx, y - tbh * 0.5f), new Vector2(rc, tbh), () => SetTarget(t), 28);
                ((RectTransform)b.transform).sizeDelta = new Vector2(rc, tbh);
                targetButtons.Add(b);
                y -= tbh + 6f;
            }
            float confirmH = Mathf.Max(MinButton, Mathf.Min(88f, y - regionBottom - 4f));
            var confirm = UiKit.Button(panel, Strings.Confirm, new Vector2(rcx, regionBottom + confirmH * 0.5f), new Vector2(rc, confirmH), OnConfirm, 34);
            ((RectTransform)confirm.transform).sizeDelta = new Vector2(rc, confirmH);
            UiKit.SetButtonColor(confirm, UiKit.ConfirmGreen);

            // ---- left: tabs + pages
            float lw = w - 2f * m - 10f - rc;
            float lx = -w * 0.5f + m + lw * 0.5f;
            const float tabH = 72f;
            string[] tabNames = { BannerStrings.TabPresets, BannerStrings.TabPatterns, BannerStrings.TabEmblem };
            float tw = (lw - 2f * gap) / 3f;
            tabButtons.Clear(); tabPages.Clear();
            float pageTop = regionTop - tabH - 10f;
            float pageH = pageTop - regionBottom;
            var pageCenter = new Vector2(lx, (pageTop + regionBottom) * 0.5f);
            for (int i = 0; i < 3; i++)
            {
                int ti = i;
                var b = UiKit.Button(panel, tabNames[i], new Vector2(lx - lw * 0.5f + tw * 0.5f + i * (tw + gap), regionTop - tabH * 0.5f),
                    new Vector2(tw, tabH), () => SetTab(ti), 30);
                tabButtons.Add(b);
                var page = UiKit.Rect(panel, "BannerTab_" + i, pageCenter, new Vector2(lw, pageH));
                tabPages.Add(page);
            }
            BuildPresetsPage(tabPages[0], lw, pageH);
            BuildPatternsPage(tabPages[1], lw, pageH);
            BuildEmblemPage(tabPages[2], lw, pageH);

            if (!subscribedProfile) { CivilizationProfile.Changed += Refresh; subscribedProfile = true; }
            SubscribeCanvas();
            SetBrush(canvas != null && canvas.BrushWidth >= EmblemDrawingCanvas.ThickWidth - 0.001f);
            SetTarget(PaintTarget.Field);
            SetTab(0);
        }

        void SubscribeCanvas()
        {
            if (canvas == null || subscribedCanvas == canvas) return;
            if (subscribedCanvas != null) subscribedCanvas.Changed -= RefreshPreview;
            canvas.Changed += RefreshPreview;
            subscribedCanvas = canvas;
        }

        void BuildPresetsPage(RectTransform page, float pw, float ph)
        {
            var presets = BannerPresets.All;
            const int cols = 4;
            int rows = (presets.Length + cols - 1) / cols;
            const float g = 8f;
            float bw = (pw - (cols - 1) * g) / cols;
            float bh = Mathf.Max(MinButton, (ph - (rows - 1) * g) / rows);
            float x0 = -pw * 0.5f + bw * 0.5f, y0 = ph * 0.5f - bh * 0.5f;
            for (int i = 0; i < presets.Length; i++)
            {
                int idx = i;
                var pos = new Vector2(x0 + (i % cols) * (bw + g), y0 - (i / cols) * (bh + g));
                var b = UiKit.Button(page, "", pos, new Vector2(bw, bh), () => PickPreset(idx), 18);
                ((RectTransform)b.transform).sizeDelta = new Vector2(bw, bh);
                b.name = "Preset_" + i;
                UiKit.SetButtonColor(b, new Color(0.30f, 0.18f, 0.10f, 1f));
                var brt = (RectTransform)b.transform;
                float tw = bw - 12f, th = Mathf.Min(tw / EmblemRasterizer.Aspect, bh - 40f);
                tw = th * EmblemRasterizer.Aspect;
                var img = UiKit.RawImage(brt, new Vector2(0f, bh * 0.5f - 6f - th * 0.5f), new Vector2(tw, th), EmblemRasterizer.PresetThumb(i));
                img.raycastTarget = false;
                var lbl = UiKit.Label(brt, presets[i].name, new Vector2(0f, -bh * 0.5f + 16f), new Vector2(bw - 6f, 28f), 19);
                lbl.color = UiKit.ButtonText;
                lbl.textWrappingMode = TextWrappingModes.NoWrap;
            }
        }

        void BuildPatternsPage(RectTransform page, float pw, float ph)
        {
            int count = Enum.GetValues(typeof(BannerPattern)).Length;
            const int cols = 5;
            int rows = (count + cols - 1) / cols;
            const float g = 8f;
            const float toolsH = 76f;
            float bw = (pw - (cols - 1) * g) / cols;
            float bh = Mathf.Min(bw / EmblemRasterizer.Aspect + 10f, (ph - toolsH - 10f - (rows - 1) * g) / rows);
            float x0 = -pw * 0.5f + bw * 0.5f, y0 = ph * 0.5f - bh * 0.5f;
            for (int i = 0; i < count; i++)
            {
                var pat = (BannerPattern)i;
                var pos = new Vector2(x0 + (i % cols) * (bw + g), y0 - (i / cols) * (bh + g));
                var b = UiKit.Button(page, "", pos, new Vector2(bw, bh), () => AddLayer(pat), 18);
                ((RectTransform)b.transform).sizeDelta = new Vector2(bw, bh);
                b.name = "Pattern_" + pat;
                UiKit.SetButtonColor(b, UiKit.OakMid);
                float th = Mathf.Min(bh - 10f, (bw - 10f) / EmblemRasterizer.Aspect);
                var img = UiKit.RawImage((RectTransform)b.transform, Vector2.zero, new Vector2(th * EmblemRasterizer.Aspect, th), EmblemRasterizer.PatternThumb(pat));
                img.raycastTarget = false;
            }
            float ty = -ph * 0.5f + toolsH * 0.5f;
            float tbw = (pw - 2f * g) / 3f;
            UiKit.Button(page, BannerStrings.UndoLayer, new Vector2(-pw * 0.5f + tbw * 0.5f, ty), new Vector2(tbw, toolsH), UndoLayer, 26);
            UiKit.Button(page, BannerStrings.ClearLayers, new Vector2(-pw * 0.5f + tbw * 1.5f + g, ty), new Vector2(tbw, toolsH), ClearLayers, 26);
            layerCountLabel = UiKit.Label(page, "", new Vector2(-pw * 0.5f + tbw * 2.5f + 2f * g, ty), new Vector2(tbw, toolsH), 26);
            layerCountLabel.color = UiKit.Ink;
        }

        void BuildEmblemPage(RectTransform page, float pw, float ph)
        {
            const float g = 8f, toolsH = 76f;
            float padH = ph - toolsH - 10f;
            float padW = Mathf.Min(pw, padH * EmblemRasterizer.Aspect);
            padH = padW / EmblemRasterizer.Aspect;
            if (canvas != null && !canvas.IsBuilt)
            {
                var area = UiKit.Rect(page, "EmblemArea", new Vector2(0f, ph * 0.5f - padH * 0.5f), new Vector2(padW, padH));
                canvas.Build(area);
            }
            float ty = -ph * 0.5f + toolsH * 0.5f;
            float bw = (pw - 3f * g) / 4f;
            float x0 = -pw * 0.5f + bw * 0.5f;
            thinBtn = UiKit.Button(page, Strings.BrushThin, new Vector2(x0, ty), new Vector2(bw, toolsH), () => SetBrush(false), 30);
            thickBtn = UiKit.Button(page, Strings.BrushThick, new Vector2(x0 + (bw + g), ty), new Vector2(bw, toolsH), () => SetBrush(true), 30);
            UiKit.Button(page, Strings.Undo, new Vector2(x0 + 2f * (bw + g), ty), new Vector2(bw, toolsH), OnUndo, 30);
            UiKit.Button(page, Strings.Clear, new Vector2(x0 + 3f * (bw + g), ty), new Vector2(bw, toolsH), OnClear, 30);
        }

        void OnDestroy()
        {
            if (subscribedProfile) { CivilizationProfile.Changed -= Refresh; subscribedProfile = false; }
            if (subscribedCanvas != null) { subscribedCanvas.Changed -= RefreshPreview; subscribedCanvas = null; }
            if (previewTex != null) Destroy(previewTex);
        }

        // ------------------------------------------------------------------ actions
        public void SetTab(int i)
        {
            tab = Mathf.Clamp(i, 0, tabPages.Count - 1);
            for (int k = 0; k < tabPages.Count; k++)
            {
                tabPages[k].gameObject.SetActive(k == tab);
                Highlight(tabButtons[k], k == tab, true);
            }
            if (tab == 1 && target == PaintTarget.Field) SetTarget(PaintTarget.Layer);
            if (tab == 2 && target != PaintTarget.Emblem) SetTarget(PaintTarget.Emblem);
        }

        public void SetTarget(PaintTarget t)
        {
            target = t;
            for (int k = 0; k < targetButtons.Count; k++) Highlight(targetButtons[k], k == (int)t, true);
            Refresh();
        }

        void PickColor(int idx)
        {
            var p = CivilizationProfile.Current;
            if (p == null) return;
            Color c = BannerPalette.All[idx];
            switch (target)
            {
                case PaintTarget.Field: p.bannerColor = c; break;
                case PaintTarget.Emblem: p.emblemColor = c; break;
                case PaintTarget.Layer:
                    layerColor = c;
                    if (p.layers != null && p.layers.Count > 0) p.layers[p.layers.Count - 1].color = c; // recolor newest layer
                    break;
            }
            CivilizationProfile.NotifyChanged();
        }

        void PickPreset(int i)
        {
            var p = CivilizationProfile.Current;
            if (p == null) return;
            BannerPresets.ApplyTo(p, i);
            CivilizationProfile.NotifyChanged();
        }

        void AddLayer(BannerPattern pat)
        {
            var p = CivilizationProfile.Current;
            if (p == null) return;
            if (p.layers == null) p.layers = new List<BannerLayer>();
            if (p.layers.Count >= CivilizationProfile.MaxLayers)
            {
                if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Deny);
                return;
            }
            p.layers.Add(new BannerLayer(pat, layerColor));
            CivilizationProfile.NotifyChanged();
        }

        void UndoLayer()
        {
            var p = CivilizationProfile.Current;
            if (p == null || p.layers == null || p.layers.Count == 0) return;
            p.layers.RemoveAt(p.layers.Count - 1);
            CivilizationProfile.NotifyChanged();
        }

        void ClearLayers()
        {
            var p = CivilizationProfile.Current;
            if (p == null || p.layers == null || p.layers.Count == 0) return;
            p.layers.Clear();
            CivilizationProfile.NotifyChanged();
        }

        void SetBrush(bool thick)
        {
            if (canvas != null) canvas.SetBrush(thick);
            if (thinBtn != null) Highlight(thinBtn, !thick, true);
            if (thickBtn != null) Highlight(thickBtn, thick, true);
        }

        void OnUndo() { if (canvas != null) canvas.Undo(); }
        void OnClear() { if (canvas != null) canvas.Clear(); }

        void OnConfirm()
        {
            var p = CivilizationProfile.Current;
            if (p != null)
            {
                if (canvas != null && !EmblemRasterizer.IsEmpty(canvas.Strokes))
                    p.strokes = EmblemRasterizer.Copy(canvas.Strokes);
                // empty design never blocks founding: no strokes and no layers → suggested crest (visible on pad + preview)
                if (EmblemRasterizer.IsEmpty(p.strokes) && (p.layers == null || p.layers.Count == 0))
                    p.strokes = EmblemRasterizer.SuggestedStrokes();
                CivilizationProfile.NotifyChanged();
                ProfileStore.Save();
            }
            Refresh();
            Confirmed?.Invoke();
        }

        // ------------------------------------------------------------------ view
        void Refresh()
        {
            var p = CivilizationProfile.Current;
            if (p != null)
            {
                Color c = target == PaintTarget.Field ? p.bannerColor : target == PaintTarget.Emblem ? p.emblemColor
                    : (p.layers != null && p.layers.Count > 0 ? p.layers[p.layers.Count - 1].color : layerColor);
                HighlightClosest(swatches, BannerPalette.All, c);
                if (layerCountLabel != null)
                    layerCountLabel.text = string.Format(BannerStrings.LayerCount, p.layers != null ? p.layers.Count : 0, CivilizationProfile.MaxLayers);
            }
            RefreshPreview();
        }

        void RefreshPreview()
        {
            if (preview == null) return;
            var p = CivilizationProfile.Current;
            if (p == null) { previewTex = EmblemRasterizer.RasterizeProfile(null, EmblemRasterizer.DefaultSize, previewTex); preview.texture = previewTex; return; }
            List<EmblemStroke> src = canvas != null && canvas.IsBuilt ? canvas.Strokes : p.strokes; // live, incl. the stroke being drawn
            previewTex = EmblemRasterizer.Rasterize(EmblemRasterizer.StrokesForBanner(src, p.layers), p.bannerColor, p.emblemColor,
                EmblemRasterizer.DefaultSize, previewTex, p.layers);
            preview.texture = previewTex;
        }

        static void HighlightClosest(List<Button> buttons, Color[] colors, Color target)
        {
            int best = -1; float bestD = 0.03f;
            for (int i = 0; i < colors.Length; i++)
            {
                float d = Mathf.Abs(colors[i].r - target.r) + Mathf.Abs(colors[i].g - target.g) + Mathf.Abs(colors[i].b - target.b);
                if (d < bestD) { bestD = d; best = i; }
            }
            for (int i = 0; i < buttons.Count; i++) Highlight(buttons[i], i == best, false);
        }

        static void Highlight(Button b, bool on, bool tool)
        {
            if (b == null) return;
            var rim = b.GetComponent<Outline>();
            if (rim != null)
            {
                rim.effectColor = on ? UiKit.GoldBright : UiKit.Gold;
                rim.effectDistance = on ? new Vector2(3f, -3f) : new Vector2(1.5f, -1.5f);
            }
            if (tool) UiKit.SetButtonColor(b, on ? UiKit.ActiveTabAmber : UiKit.WalnutFrame);
        }
    }
}
