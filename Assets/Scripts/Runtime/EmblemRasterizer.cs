using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// CPU rasterizer for fingertip-drawn emblems. Strokes are normalized (0..1) polylines; width is normalized to the
    /// texture height. Distances are measured in "banner space" (x scaled by Aspect) so a round brush stays round on
    /// the 16:11 cloth, the drawing pad and the scroll preview, which all use the same texture layout.
    /// </summary>
    public static class EmblemRasterizer
    {
        public const int DefaultSize = 256;
        /// <summary>Width / height of the banner cloth, drawing pad and preview.</summary>
        public const float Aspect = 16f / 11f;

        static Color32[] scratch;

        public static Texture2D Rasterize(List<EmblemStroke> strokes, Color bg, Color fg, int size, Texture2D reuse)
        {
            return Rasterize(strokes, bg, fg, size, reuse, Aspect);
        }

        public static Texture2D Rasterize(List<EmblemStroke> strokes, Color bg, Color fg, int size, Texture2D reuse, float aspect)
        {
            size = Mathf.Clamp(size, 16, 2048);
            Texture2D tex = reuse;
            if (tex == null || tex.width != size || tex.height != size)
                tex = CreateTexture(size);

            int n = size * size;
            if (scratch == null || scratch.Length != n) scratch = new Color32[n];
            FillBackground(scratch, size, bg, null, aspect);
            return Finish(tex, size, strokes, fg, aspect);
        }

        /// <summary>Field color → pattern layers (in order) → drawn emblem strokes on top.</summary>
        public static Texture2D Rasterize(List<EmblemStroke> strokes, Color bg, Color fg, int size, Texture2D reuse, List<BannerLayer> layers)
        {
            size = Mathf.Clamp(size, 16, 2048);
            Texture2D tex = reuse;
            if (tex == null || tex.width != size || tex.height != size)
                tex = CreateTexture(size);
            int n = size * size;
            if (scratch == null || scratch.Length != n) scratch = new Color32[n];
            FillBackground(scratch, size, bg, layers, Aspect);
            return Finish(tex, size, strokes, fg, Aspect);
        }

        /// <summary>The complete banner of a profile (field, layers, emblem; suggested crest when nothing at all is designed).</summary>
        public static Texture2D RasterizeProfile(CivilizationProfile p, int size, Texture2D reuse)
        {
            if (p == null) return Rasterize(SuggestedStrokes(), new Color(0.62f, 0.10f, 0.10f), new Color(1f, 0.82f, 0.25f), size, reuse);
            return Rasterize(StrokesForBanner(p.strokes, p.layers), p.bannerColor, p.emblemColor, size, reuse, p.layers);
        }

        /// <summary>Strokes to draw: the player's own, else the suggested crest — unless pattern layers already make a design.</summary>
        public static List<EmblemStroke> StrokesForBanner(List<EmblemStroke> strokes, List<BannerLayer> layers)
        {
            if (!IsEmpty(strokes)) return strokes;
            return layers != null && layers.Count > 0 ? null : SuggestedStrokes();
        }

        static Texture2D Finish(Texture2D tex, int size, List<EmblemStroke> strokes, Color fg, float aspect)
        {
            if (strokes != null)
            {
                for (int s = 0; s < strokes.Count; s++)
                    StampStroke(scratch, size, strokes[s], fg, aspect);
            }
            tex.SetPixels32(scratch);
            tex.Apply(false, false);
            return tex;
        }

        // ------------------------------------------------------------------ pattern layers
        static Color32[] bgCache;
        static int bgCacheKey, bgCacheSize;

        static int LayersKey(int size, Color field, List<BannerLayer> layers)
        {
            unchecked
            {
                int h = size * 31 + field.GetHashCode();
                if (layers != null)
                    for (int i = 0; i < layers.Count; i++)
                        if (layers[i] != null) h = h * 397 ^ (layers[i].pattern * 7919 + layers[i].color.GetHashCode());
                return h;
            }
        }

        /// <summary>Fills px with the field color and composites the pattern layers in order (cached for repeated calls).</summary>
        public static void FillBackground(Color32[] px, int size, Color field, List<BannerLayer> layers, float aspect)
        {
            int key = LayersKey(size, field, layers);
            if (bgCache != null && bgCacheSize == size && bgCacheKey == key && bgCache.Length == px.Length)
            {
                System.Array.Copy(bgCache, px, px.Length);
                return;
            }
            Fill(px, field);
            if (layers != null)
                for (int i = 0; i < layers.Count; i++)
                    if (layers[i] != null) StampPattern(px, size, (BannerPattern)layers[i].pattern, layers[i].color, aspect);
            ApplyFabricWeaveAndSelvage(px, size);
            if (bgCache == null || bgCache.Length != px.Length) bgCache = new Color32[px.Length];
            System.Array.Copy(px, bgCache, px.Length);
            bgCacheKey = key; bgCacheSize = size;
        }

        /// <summary>Applies a woven medieval linen cross-grain, subtle vertical drape shading, and a stitched gold selvage hem.</summary>
        static void ApplyFabricWeaveAndSelvage(Color32[] px, int size)
        {
            if (size <= 0) return;
            int hem = Mathf.Max(2, size / 72);
            Color32 gold = BannerPalette.Or;
            float gr = gold.r, gg = gold.g, gb = gold.b;
            float invSize = 1f / size;
            for (int y = 0; y < size; y++)
            {
                float v = (y + 0.5f) * invSize;
                float drape = 0.965f + 0.035f * Mathf.Sin(v * Mathf.PI);
                int by = y < size - 1 - y ? y : size - 1 - y;
                int row = y * size;
                for (int x = 0; x < size; x++)
                {
                    int bx = x < size - 1 - x ? x : size - 1 - x;
                    int edgeDist = bx < by ? bx : by;
                    // deterministic warp/weft herringbone linen micro-weave
                    int checker = (x + y) & 1;
                    int twill = ((x >> 1) + y) & 3;
                    float weave = drape + (checker == 0 ? 0.022f : -0.022f) + (twill == 0 ? 0.014f : (twill == 2 ? -0.014f : 0f));

                    int idx = row + x;
                    Color32 c = px[idx];
                    float r = Mathf.Clamp(c.r * weave, 0f, 255f);
                    float g = Mathf.Clamp(c.g * weave, 0f, 255f);
                    float b = Mathf.Clamp(c.b * weave, 0f, 255f);

                    if (edgeDist < hem)
                    {
                        // burnished gold selvage trim along all four borders
                        const float trimBlend = 0.42f;
                        r = r + (gr - r) * trimBlend;
                        g = g + (gg - g) * trimBlend;
                        b = b + (gb - b) * trimBlend;
                    }
                    else if (edgeDist == hem)
                    {
                        // dark stitched seam line just inside the gold hem
                        r *= 0.84f;
                        g *= 0.84f;
                        b *= 0.84f;
                    }
                    c.r = (byte)r;
                    c.g = (byte)g;
                    c.b = (byte)b;
                    px[idx] = c;
                }
            }
        }

        public static void StampPattern(Color32[] px, int size, BannerPattern pattern, Color color, float aspect)
        {
            if (aspect <= 0.01f) aspect = 1f;
            Color32 c = color;
            float cr = c.r, cg = c.g, cb = c.b, ca = c.a;
            float pix = 1f / size;
            for (int y = 0; y < size; y++)
            {
                float v = (y + 0.5f) * pix;
                int row = y * size;
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) * pix;
                    float cov = Coverage(pattern, u, v, aspect, pix);
                    if (cov <= 0f) continue;
                    if (cov > 1f) cov = 1f;
                    int i = row + x;
                    Color32 o = px[i];
                    o.r = (byte)(o.r + (cr - o.r) * cov);
                    o.g = (byte)(o.g + (cg - o.g) * cov);
                    o.b = (byte)(o.b + (cb - o.b) * cov);
                    o.a = (byte)(o.a + (ca - o.a) * cov);
                    px[i] = o;
                }
            }
        }

        /// <summary>Antialiased coverage 0..1 of a pattern at normalized (u,v); distances in banner space (x = u*aspect, y = v).</summary>
        public static float Coverage(BannerPattern p, float u, float v, float A, float pix)
        {
            float x = u * A, y = v, cx = A * 0.5f;
            float d; // signed distance, negative inside (units of banner height)
            float diagN = 1f / Mathf.Sqrt(1f + A * A);
            switch (p)
            {
                case BannerPattern.StripeTop: d = 0.68f - y; break;
                case BannerPattern.StripeBottom: d = y - 0.32f; break;
                case BannerPattern.StripeLeft: d = x - 0.30f * A; break;
                case BannerPattern.StripeRight: d = 0.70f * A - x; break;
                case BannerPattern.Pale: d = Mathf.Abs(x - cx) - 0.18f; break;
                case BannerPattern.Fess: d = Mathf.Abs(y - 0.5f) - 0.17f; break;
                case BannerPattern.Cross: d = Mathf.Min(Mathf.Abs(x - cx), Mathf.Abs(y - 0.5f)) - 0.09f; break;
                case BannerPattern.Saltire:
                    d = Mathf.Min(Mathf.Abs(x - y * A), Mathf.Abs(x - (1f - y) * A)) * diagN - 0.085f; break;
                case BannerPattern.Bend: d = Mathf.Abs(x - (1f - y) * A) * diagN - 0.12f; break;
                case BannerPattern.BendSinister: d = Mathf.Abs(x - y * A) * diagN - 0.12f; break;
                case BannerPattern.Chevron:
                {
                    // inverted V: (0,0.05) → (cx,0.70) → (A,0.05)
                    float t = Mathf.Abs(x - cx);
                    float lineY = 0.70f - t * (0.65f / cx);
                    float slopeN = 1f / Mathf.Sqrt(1f + (0.65f / cx) * (0.65f / cx));
                    d = Mathf.Abs(y - lineY) * slopeN - 0.10f;
                    break;
                }
                case BannerPattern.ChiefTriangle:
                {
                    // triangle hanging from the top edge, apex at the center
                    float edgeY = 0.5f + Mathf.Abs(x - cx) * (0.5f / cx);
                    d = (edgeY - y) / Mathf.Sqrt(1f + (0.5f / cx) * (0.5f / cx));
                    break;
                }
                case BannerPattern.Border:
                    d = Mathf.Min(Mathf.Min(x, A - x), Mathf.Min(y, 1f - y)) - 0.09f; break;
                case BannerPattern.Quarters:
                {
                    float e = Mathf.Min(Mathf.Abs(x - cx), Mathf.Abs(y - 0.5f));
                    d = ((x < cx) == (y > 0.5f)) ? -e : e;
                    break;
                }
                case BannerPattern.PerSaltire:
                {
                    float a = (x - y * A) * diagN, b = (x - (1f - y) * A) * diagN;
                    float e = Mathf.Min(Mathf.Abs(a), Mathf.Abs(b));
                    d = (a * b > 0f) ? e : -e; // top and bottom triangles inside
                    break;
                }
                case BannerPattern.Roundel:
                {
                    float dx = x - cx, dy = y - 0.5f;
                    d = Mathf.Sqrt(dx * dx + dy * dy) - 0.27f; break;
                }
                case BannerPattern.Lozenge:
                    d = (Mathf.Abs(x - cx) / (0.42f * A) + Mathf.Abs(y - 0.5f) / 0.42f - 1f) * 0.3f; break;
                case BannerPattern.Gradient:
                {
                    float g = 1f - v; // full color at the bottom fading to the field at the top
                    return g * g * (3f - 2f * g);
                }
                case BannerPattern.Checky:
                {
                    float fu = u * 6f, fv = v * 4f;
                    int iu = (int)fu, iv = (int)fv;
                    fu -= iu; fv -= iv;
                    float e = Mathf.Min(Mathf.Min(fu, 1f - fu) * A / 6f, Mathf.Min(fv, 1f - fv) / 4f);
                    d = ((iu + iv) & 1) == 0 ? -e : e;
                    break;
                }
                case BannerPattern.HalfHorizontal: d = y - 0.5f; break;
                case BannerPattern.HalfVertical: d = x - cx; break;
                default: return 0f;
            }
            return Mathf.Clamp01(0.5f - d / pix);
        }

        // ------------------------------------------------------------------ thumbnails (cached, built once)
        public const int ThumbSize = 96;
        static readonly Dictionary<int, Texture2D> patternThumbs = new Dictionary<int, Texture2D>();
        static readonly Dictionary<int, Texture2D> presetThumbs = new Dictionary<int, Texture2D>();
        static readonly List<BannerLayer> tmpLayers = new List<BannerLayer>(1);

        public static Texture2D PatternThumb(BannerPattern p)
        {
            if (patternThumbs.TryGetValue((int)p, out var t) && t != null) return t;
            tmpLayers.Clear();
            tmpLayers.Add(new BannerLayer(p, BannerPalette.Sable));
            t = Rasterize(null, BannerPalette.Argent, Color.white, ThumbSize, null, tmpLayers);
            t.name = "PatternThumb_" + p;
            patternThumbs[(int)p] = t;
            return t;
        }

        public static Texture2D PresetThumb(int index)
        {
            if (presetThumbs.TryGetValue(index, out var t) && t != null) return t;
            var pr = BannerPresets.All[index];
            t = Rasterize(null, pr.field, pr.emblem, ThumbSize, null, pr.layers);
            t.name = "PresetThumb_" + index;
            presetThumbs[index] = t;
            return t;
        }

        public static Texture2D CreateTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
            {
                name = "Emblem",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            return tex;
        }

        public static void Fill(Color32[] px, Color bg)
        {
            Color32 c = bg;
            for (int i = 0; i < px.Length; i++) px[i] = c;
        }

        public static void StampStroke(Color32[] px, int size, EmblemStroke stroke, Color fg, float aspect)
        {
            if (stroke == null || stroke.points == null || stroke.points.Count == 0) return;
            var pts = stroke.points;
            if (pts.Count == 1)
            {
                StampSegment(px, size, pts[0], pts[0], stroke.width, fg, aspect);
                return;
            }
            for (int i = 1; i < pts.Count; i++)
                StampSegment(px, size, pts[i - 1], pts[i], stroke.width, fg, aspect);
        }

        /// <summary>Antialiased capsule from a to b (normalized coords). Blends fg over existing pixels by coverage.</summary>
        public static void StampSegment(Color32[] px, int size, Vector2 a, Vector2 b, float widthNorm, Color fg, float aspect)
        {
            if (aspect <= 0.01f) aspect = 1f;
            // banner space: y in [0,size], x in [0,size*aspect]
            float sx = size * aspect, sy = size;
            float ax = a.x * sx, ay = a.y * sy, bx = b.x * sx, by = b.y * sy;
            float r = Mathf.Max(0.75f, widthNorm * size * 0.5f);
            float minX = Mathf.Min(ax, bx) - r - 1f, maxX = Mathf.Max(ax, bx) + r + 1f;
            float minY = Mathf.Min(ay, by) - r - 1f, maxY = Mathf.Max(ay, by) + r + 1f;
            // convert x range back to texture pixel columns
            int x0 = Mathf.Max(0, Mathf.FloorToInt(minX / aspect));
            int x1 = Mathf.Min(size - 1, Mathf.CeilToInt(maxX / aspect));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(minY));
            int y1 = Mathf.Min(size - 1, Mathf.CeilToInt(maxY));
            if (x0 > x1 || y0 > y1) return;

            float dx = bx - ax, dy = by - ay;
            float len2 = dx * dx + dy * dy;
            float inv = len2 > 1e-6f ? 1f / len2 : 0f;
            Color32 f = fg;
            float fr = f.r, fgc = f.g, fb = f.b, fa = f.a;

            for (int y = y0; y <= y1; y++)
            {
                float py = y + 0.5f;
                int row = y * size;
                for (int x = x0; x <= x1; x++)
                {
                    float pxv = (x + 0.5f) * aspect;
                    float t = ((pxv - ax) * dx + (py - ay) * dy) * inv;
                    if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                    float cx = ax + dx * t - pxv, cy = ay + dy * t - py;
                    float d = Mathf.Sqrt(cx * cx + cy * cy);
                    float cov = r + 0.5f - d;
                    if (cov <= 0f) continue;
                    if (cov > 1f) cov = 1f;
                    int i = row + x;
                    Color32 o = px[i];
                    o.r = (byte)(o.r + (fr - o.r) * cov);
                    o.g = (byte)(o.g + (fgc - o.g) * cov);
                    o.b = (byte)(o.b + (fb - o.b) * cov);
                    o.a = (byte)(o.a + (fa - o.a) * cov);
                    px[i] = o;
                }
            }
        }

        public static bool IsEmpty(List<EmblemStroke> strokes)
        {
            if (strokes == null) return true;
            for (int i = 0; i < strokes.Count; i++)
                if (strokes[i] != null && strokes[i].points != null && strokes[i].points.Count > 0) return false;
            return true;
        }

        /// <summary>Returns strokes, or the suggested crest when the drawing is empty (an empty drawing never blocks founding).</summary>
        public static List<EmblemStroke> StrokesOrSuggested(List<EmblemStroke> strokes)
        {
            return IsEmpty(strokes) ? SuggestedStrokes() : strokes;
        }

        public static EmblemStroke MakeStroke(float width, int capacity = 32)
        {
            var s = new EmblemStroke();
            s.width = width;
            s.points = new List<Vector2>(capacity);
            return s;
        }

        public static List<EmblemStroke> Copy(List<EmblemStroke> src)
        {
            var dst = new List<EmblemStroke>(src != null ? src.Count : 0);
            if (src == null) return dst;
            for (int i = 0; i < src.Count; i++)
            {
                var s = src[i];
                if (s == null || s.points == null) continue;
                var c = MakeStroke(s.width, s.points.Count);
                c.points.AddRange(s.points);
                dst.Add(c);
            }
            return dst;
        }

        /// <summary>Default crest: a stylized crown with jewels over a star-pointed band. Fresh list each call.</summary>
        public static List<EmblemStroke> SuggestedStrokes()
        {
            var list = new List<EmblemStroke>(8);
            const float w = 0.045f;
            // crown outline (x narrowed because the cloth is wider than tall)
            list.Add(Poly(w, 0.36f, 0.30f, 0.33f, 0.66f, 0.42f, 0.50f, 0.50f, 0.74f, 0.58f, 0.50f, 0.67f, 0.66f, 0.64f, 0.30f, 0.36f, 0.30f));
            // jewelled band
            list.Add(Poly(w * 0.7f, 0.37f, 0.39f, 0.63f, 0.39f));
            // jewels on the three tips
            list.Add(Circle(0.33f, 0.71f, 0.028f, w * 0.8f));
            list.Add(Circle(0.50f, 0.80f, 0.030f, w * 0.8f));
            list.Add(Circle(0.67f, 0.71f, 0.028f, w * 0.8f));
            // base rule
            list.Add(Poly(w * 0.8f, 0.30f, 0.21f, 0.70f, 0.21f));
            return list;
        }

        static EmblemStroke Poly(float width, params float[] xy)
        {
            var s = MakeStroke(width, xy.Length / 2);
            for (int i = 0; i + 1 < xy.Length; i += 2) s.points.Add(new Vector2(xy[i], xy[i + 1]));
            return s;
        }

        static EmblemStroke Circle(float cx, float cy, float ry, float width)
        {
            const int seg = 24;
            var s = MakeStroke(width, seg + 1);
            float rx = ry / Aspect;
            for (int i = 0; i <= seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                s.points.Add(new Vector2(cx + Mathf.Cos(a) * rx, cy + Mathf.Sin(a) * ry));
            }
            return s;
        }
    }

    /// <summary>Heraldic tinctures offered for field, layers and emblem.</summary>
    public static class BannerPalette
    {
        static Color H(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);

        public static readonly Color Argent   = H(0xF0EAE1); // Warm Silver / Unbleached Linen
        public static readonly Color Or       = H(0xD9A628); // Burnished Heraldic Gold
        public static readonly Color Gules    = H(0xA61C24); // Deep Heraldic Madder Crimson
        public static readonly Color Azure    = H(0x1B457B); // Woad Royal Blue
        public static readonly Color Vert     = H(0x1E5E35); // Lincoln Forest Green
        public static readonly Color Sable    = H(0x1A1817); // Charcoal Black
        public static readonly Color Purpure  = H(0x5A2366); // Tyrian Imperial Purple
        public static readonly Color Tenne    = H(0xA85319); // Tawny Ochre
        public static readonly Color Sanguine = H(0x5E121A); // Dried Blood Burgundy
        public static readonly Color Celeste  = H(0x5B8BB2); // Heraldic Slate-Sky Blue
        public static readonly Color Rose     = H(0xAD4C68); // Medieval Tudor Rose / Damask
        public static readonly Color Teal     = H(0x165E5B); // Deep Sea Viridian
        public static readonly Color Brown    = H(0x59381E); // Raw Umber
        public static readonly Color Grey     = H(0x6E6A63); // Ash Stone
        public static readonly Color Ivory    = H(0xE3D4B4); // Aged Vellum Cream
        public static readonly Color Navy     = H(0x122242); // Midnight Indigo
        public static readonly Color Citrine  = H(0xC99B2C); // Saffron Amber / Warm Ochre
        public static readonly Color Copper   = H(0x9C522B); // Hammered Copper

        /// <summary>18 colors, shown as two rows of 9.</summary>
        public static readonly Color[] All =
        {
            Argent, Or, Gules, Azure, Vert, Sable, Purpure, Tenne, Sanguine,
            Celeste, Rose, Teal, Brown, Grey, Ivory, Navy, Citrine, Copper
        };
    }

    /// <summary>Built-in (offline) ready-made banners. Tapping one replaces field + layers + emblem color; drawn strokes stay.</summary>
    public static class BannerPresets
    {
        public sealed class Preset
        {
            public string name;
            public Color field, emblem;
            public List<BannerLayer> layers;
        }

        static Preset P(string name, Color field, Color emblem, params BannerLayer[] layers)
        {
            return new Preset { name = name, field = field, emblem = emblem, layers = new List<BannerLayer>(layers) };
        }

        static BannerLayer L(BannerPattern p, Color c) => new BannerLayer(p, c);

        public static readonly Preset[] All =
        {
            P(BannerStrings.PresetDragonGuard, BannerPalette.Sanguine, BannerPalette.Or,
                L(BannerPattern.ChiefTriangle, BannerPalette.Sable), L(BannerPattern.Border, BannerPalette.Or)),
            P(BannerStrings.PresetNorthernCross, BannerPalette.Azure, BannerPalette.Argent,
                L(BannerPattern.Cross, BannerPalette.Argent), L(BannerPattern.StripeBottom, BannerPalette.Navy)),
            P(BannerStrings.PresetSunDynasty, BannerPalette.Gules, BannerPalette.Ivory,
                L(BannerPattern.Gradient, BannerPalette.Or), L(BannerPattern.Roundel, BannerPalette.Or), L(BannerPattern.Border, BannerPalette.Sanguine)),
            P(BannerStrings.PresetForestClans, BannerPalette.Vert, BannerPalette.Ivory,
                L(BannerPattern.Chevron, BannerPalette.Brown), L(BannerPattern.StripeBottom, BannerPalette.Brown)),
            P(BannerStrings.PresetIronBastion, BannerPalette.Grey, BannerPalette.Or,
                L(BannerPattern.Checky, BannerPalette.Sable), L(BannerPattern.Fess, BannerPalette.Grey), L(BannerPattern.Border, BannerPalette.Sable)),
            P(BannerStrings.PresetSeaKings, BannerPalette.Navy, BannerPalette.Argent,
                L(BannerPattern.Gradient, BannerPalette.Teal), L(BannerPattern.Fess, BannerPalette.Celeste), L(BannerPattern.Border, BannerPalette.Argent)),
            P(BannerStrings.PresetRoyalQuarters, BannerPalette.Azure, BannerPalette.Ivory,
                L(BannerPattern.Quarters, BannerPalette.Gules), L(BannerPattern.Roundel, BannerPalette.Or)),
            P(BannerStrings.PresetCrimsonSaltire, BannerPalette.Argent, BannerPalette.Sable,
                L(BannerPattern.Saltire, BannerPalette.Gules)),
            P(BannerStrings.PresetGoldenChevron, BannerPalette.Purpure, BannerPalette.Argent,
                L(BannerPattern.Chevron, BannerPalette.Or), L(BannerPattern.StripeTop, BannerPalette.Sable)),
            P(BannerStrings.PresetNightWatch, BannerPalette.Sable, BannerPalette.Argent,
                L(BannerPattern.Lozenge, BannerPalette.Navy), L(BannerPattern.Border, BannerPalette.Grey)),
            P(BannerStrings.PresetJadeEmpire, BannerPalette.Teal, BannerPalette.Or,
                L(BannerPattern.Pale, BannerPalette.Ivory), L(BannerPattern.StripeLeft, BannerPalette.Vert), L(BannerPattern.StripeRight, BannerPalette.Vert)),
            P(BannerStrings.PresetEmberHost, BannerPalette.Tenne, BannerPalette.Ivory,
                L(BannerPattern.PerSaltire, BannerPalette.Sanguine), L(BannerPattern.Bend, BannerPalette.Or)),
        };

        /// <summary>Applies preset i to the profile: field, emblem color and a copy of the layers. Strokes are kept.</summary>
        public static void ApplyTo(CivilizationProfile p, int i)
        {
            if (p == null || i < 0 || i >= All.Length) return;
            var pr = All[i];
            p.bannerColor = pr.field;
            p.emblemColor = pr.emblem;
            if (p.layers == null) p.layers = new List<BannerLayer>();
            p.layers.Clear();
            for (int k = 0; k < pr.layers.Count && k < CivilizationProfile.MaxLayers; k++)
                p.layers.Add(new BannerLayer { pattern = pr.layers[k].pattern, color = pr.layers[k].color });
        }
    }
}
