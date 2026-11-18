using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HoldTheTable
{
    /// <summary>
    /// Fingertip emblem drawing pad on the Imperial Scroll. Poke contact (uGUI PointerDown from the ISDK canvas module)
    /// starts a stroke, Drag extends it, PointerUp / Exit / tracking-loss cancel ends it. Points are normalized to the
    /// pad rect; samples outside the pad are ignored and tracking glitches (big jumps) break the stroke instead of
    /// drawing a long connecting line. The committed strokes live in CivilizationProfile.Current.strokes.
    /// </summary>
    public class EmblemDrawingCanvas : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler, IPointerExitHandler, IInitializePotentialDragHandler
    {
        public const float ThinWidth = 0.02f;
        public const float ThickWidth = 0.05f;
        const float MinSampleDist = 0.004f;
        const float MaxJump = 0.25f;
        const float EdgeTolerance = 0.03f; // samples this far outside are clamped onto the frame; further → ignored
        const float LiveNotifyInterval = 0.12f;
        const float PenSoundInterval = 0.14f;

        public float BrushWidth = ThinWidth;
        public event Action Changed;

        readonly List<EmblemStroke> strokes = new List<EmblemStroke>();
        public List<EmblemStroke> Strokes => strokes;
        public bool IsBuilt => rawImage != null;
        public RectTransform Area { get; private set; }
        public Texture2D Texture => texture;
        public bool IsDrawing => current != null;

        RawImage rawImage;
        RectTransform padRect;
        Texture2D texture;
        Color32[] pixels;
        TMP_Text hint;
        Color bgColor = new Color(0.55f, 0.12f, 0.10f, 1f);
        Color fgColor = new Color(0.95f, 0.80f, 0.30f, 1f);
        List<BannerLayer> bgLayers;
        EmblemStroke current;
        int activePointer = int.MinValue;
        bool hasLast;
        Vector2 last;
        float nextLiveNotify, nextPenSound;
        bool pixelsDirty;
        bool writingProfile;
        bool subscribed;

        public void Build(RectTransform area)
        {
            if (area == null) return;
            if (rawImage != null && Area == area) { LoadFromProfile(); return; }
            Area = area;

            // Carved walnut frame with antique brass rim behind the drawing pad
            var frame = UiKit.FramedPanel(area, "EmblemFrame", Vector2.zero, Vector2.zero,
                UiKit.WalnutFrame, UiKit.DarkPanel, UiKit.Gold, 4f, true);
            UiKit.Fill((RectTransform)frame.transform, 0f);
            frame.GetComponent<Image>().raycastTarget = false;

            padRect = UiKit.Rect(area, "EmblemPad", Vector2.zero, Vector2.zero);
            UiKit.Fill(padRect, 6f);
            rawImage = padRect.gameObject.AddComponent<RawImage>();
            rawImage.raycastTarget = true;
            // uGUI bubbles events to ancestors; only add a forwarder when this component is not above the pad
            if (!padRect.IsChildOf(transform))
            {
                var fwd = padRect.gameObject.AddComponent<EmblemPadForwarder>();
                fwd.owner = this;
            }

            texture = EmblemRasterizer.CreateTexture(EmblemRasterizer.DefaultSize);
            texture.name = "EmblemPadTex";
            pixels = new Color32[texture.width * texture.height];
            rawImage.texture = texture;

            hint = UiKit.Label(padRect, Strings.DrawEmblem, Vector2.zero, new Vector2(Mathf.Max(100f, area.rect.width - 40f), 120f), 30);
            hint.color = new Color(1f, 1f, 1f, 0.55f);
            hint.raycastTarget = false;

            if (!subscribed) { CivilizationProfile.Changed += OnProfileChanged; subscribed = true; }
            LoadFromProfile();
        }

        void OnDestroy()
        {
            if (subscribed) { CivilizationProfile.Changed -= OnProfileChanged; subscribed = false; }
            if (texture != null) Destroy(texture);
        }

        void OnDisable()
        {
            // scroll page closed / hidden mid-stroke → finish the stroke cleanly
            if (current != null) EndStroke();
        }

        void OnProfileChanged()
        {
            if (writingProfile || rawImage == null) return;
            if (current != null) { ReadColors(); FullRedraw(); return; }
            LoadFromProfile();
        }

        void ReadColors()
        {
            var p = CivilizationProfile.Current;
            if (p == null) return;
            bgColor = p.bannerColor;
            fgColor = p.emblemColor;
            bgLayers = p.layers;
        }

        /// <summary>Reload strokes + colors from the profile and redraw everything.</summary>
        public void LoadFromProfile()
        {
            var p = CivilizationProfile.Current;
            if (p != null)
            {
                ReadColors();
                strokes.Clear();
                strokes.AddRange(EmblemRasterizer.Copy(p.strokes));
            }
            FullRedraw();
            Changed?.Invoke();
        }

        public void Undo()
        {
            if (current != null) EndStroke();
            if (strokes.Count == 0) return;
            strokes.RemoveAt(strokes.Count - 1);
            FullRedraw();
            Commit();
        }

        public void Clear()
        {
            if (current != null) { current = null; activePointer = int.MinValue; }
            if (strokes.Count == 0) { FullRedraw(); return; }
            strokes.Clear();
            FullRedraw();
            Commit();
        }

        public void SetBrush(bool thick) { BrushWidth = thick ? ThickWidth : ThinWidth; }

        void FullRedraw()
        {
            if (texture == null) return;
            int size = texture.width;
            EmblemRasterizer.FillBackground(pixels, size, bgColor, bgLayers, EmblemRasterizer.Aspect);
            for (int i = 0; i < strokes.Count; i++)
                EmblemRasterizer.StampStroke(pixels, size, strokes[i], fgColor, EmblemRasterizer.Aspect);
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            pixelsDirty = false;
            if (hint != null)
            {
                hint.enabled = strokes.Count == 0;
                float lum = bgColor.r * 0.299f + bgColor.g * 0.587f + bgColor.b * 0.114f;
                hint.color = lum > 0.55f ? new Color(0.18f, 0.12f, 0.08f, 0.65f) : new Color(0.96f, 0.92f, 0.84f, 0.65f);
            }
        }

        void LateUpdate()
        {
            if (pixelsDirty && texture != null)
            {
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                pixelsDirty = false;
            }
            if (current != null && Time.unscaledTime >= nextLiveNotify)
            {
                nextLiveNotify = Time.unscaledTime + LiveNotifyInterval;
                Changed?.Invoke();
            }
        }

        // ---------------------------------------------------------------- pointer handling
        public void OnInitializePotentialDrag(PointerEventData e) { e.useDragThreshold = false; }

        public void OnPointerDown(PointerEventData e)
        {
            if (rawImage == null) return;
            if (current != null && e.pointerId != activePointer) return; // second hand ignored while drawing
            if (current != null) EndStroke();
            if (!TryGetNormalized(e, out var p)) return;
            activePointer = e.pointerId;
            BeginStroke(p);
        }

        public void OnDrag(PointerEventData e)
        {
            if (rawImage == null || e.pointerId != activePointer) return;
            if (!TryGetNormalized(e, out var p))
            {
                // finger slid off the pad: stop extending, the next on-pad sample starts a new segment chain
                if (current != null) EndStroke(keepPointer: true);
                return;
            }
            if (current == null)
            {
                BeginStroke(p); // re-entered the pad while still pressed
                return;
            }
            AddSample(p);
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (e.pointerId != activePointer) return;
            if (current != null && TryGetNormalized(e, out var p)) AddSample(p);
            EndStroke();
            activePointer = int.MinValue;
        }

        public void OnPointerExit(PointerEventData e)
        {
            if (e.pointerId != activePointer || current == null) return;
            EndStroke(keepPointer: true);
        }

        bool TryGetNormalized(PointerEventData e, out Vector2 n)
        {
            n = default;
            if (padRect == null) return false;
            Vector2 local;
            Vector3 wp = e.pointerCurrentRaycast.worldPosition;
            bool ok = false;
            if (wp != Vector3.zero && e.pointerCurrentRaycast.gameObject != null)
            {
                Vector3 l = padRect.InverseTransformPoint(wp);
                local = new Vector2(l.x, l.y);
                ok = true;
            }
            else
            {
                Camera cam = e.pressEventCamera != null ? e.pressEventCamera : e.enterEventCamera;
                ok = RectTransformUtility.ScreenPointToLocalPointInRectangle(padRect, e.position, cam, out local);
            }
            if (!ok) return false;
            Rect r = padRect.rect;
            if (r.width <= 1f || r.height <= 1f) return false;
            float x = (local.x - r.xMin) / r.width;
            float y = (local.y - r.yMin) / r.height;
            if (x < -EdgeTolerance || x > 1f + EdgeTolerance || y < -EdgeTolerance || y > 1f + EdgeTolerance) return false;
            n = new Vector2(Mathf.Clamp01(x), Mathf.Clamp01(y));
            return true;
        }

        void BeginStroke(Vector2 p)
        {
            current = EmblemRasterizer.MakeStroke(BrushWidth, 64);
            current.points.Add(p);
            strokes.Add(current);
            last = p; hasLast = true;
            EmblemRasterizer.StampSegment(pixels, texture.width, p, p, current.width, fgColor, EmblemRasterizer.Aspect);
            pixelsDirty = true;
            if (hint != null) hint.enabled = false;
            PenSound(true);
        }

        void AddSample(Vector2 p)
        {
            if (current == null) return;
            float d = hasLast ? Vector2.Distance(p, last) : 0f;
            if (hasLast && d < MinSampleDist) return;
            if (hasLast && d > MaxJump)
            {
                // tracking glitch: close this stroke and start a new one at the new location (no connecting line)
                EndStroke(keepPointer: true);
                BeginStroke(p);
                return;
            }
            current.points.Add(p);
            EmblemRasterizer.StampSegment(pixels, texture.width, last, p, current.width, fgColor, EmblemRasterizer.Aspect);
            pixelsDirty = true;
            last = p; hasLast = true;
            PenSound(false);
        }

        void EndStroke(bool keepPointer = false)
        {
            if (current == null) return;
            current = null;
            hasLast = false;
            if (!keepPointer) activePointer = int.MinValue;
            Commit();
        }

        void PenSound(bool force)
        {
            if (!force && Time.unscaledTime < nextPenSound) return;
            nextPenSound = Time.unscaledTime + PenSoundInterval;
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Pen);
        }

        /// <summary>Write the committed strokes into the profile and notify listeners.</summary>
        void Commit()
        {
            var p = CivilizationProfile.Current;
            if (p != null)
            {
                p.strokes = EmblemRasterizer.Copy(strokes);
                writingProfile = true;
                try { CivilizationProfile.NotifyChanged(); }
                finally { writingProfile = false; }
            }
            Changed?.Invoke();
        }
    }

    /// <summary>Sits on the pad RawImage so poke events reach the canvas wherever the canvas component lives.</summary>
    public class EmblemPadForwarder : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler, IPointerExitHandler, IInitializePotentialDragHandler
    {
        public EmblemDrawingCanvas owner;
        public void OnPointerDown(PointerEventData e) { if (owner != null) owner.OnPointerDown(e); }
        public void OnDrag(PointerEventData e) { if (owner != null) owner.OnDrag(e); }
        public void OnPointerUp(PointerEventData e) { if (owner != null) owner.OnPointerUp(e); }
        public void OnPointerExit(PointerEventData e) { if (owner != null) owner.OnPointerExit(e); }
        public void OnInitializePotentialDrag(PointerEventData e) { if (owner != null) owner.OnInitializePotentialDrag(e); }
    }
}
