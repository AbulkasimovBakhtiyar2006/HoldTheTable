using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>One fingertip-drawn stroke; points normalized 0..1 inside the drawing field.</summary>
    [Serializable]
    public class EmblemStroke
    {
        public float width = 0.04f;
        public List<Vector2> points = new List<Vector2>();
    }

    /// <summary>Heraldic layer patterns, rasterized procedurally by EmblemRasterizer (append-only: values are saved in JSON).</summary>
    public enum BannerPattern
    {
        StripeTop, StripeBottom, StripeLeft, StripeRight, Pale, Fess, Cross, Saltire, Bend, BendSinister,
        Chevron, ChiefTriangle, Border, Quarters, PerSaltire, Roundel, Lozenge, Gradient, Checky, HalfHorizontal, HalfVertical
    }

    /// <summary>One pattern layer of the banner, drawn over the field color and under the drawn emblem.</summary>
    [Serializable]
    public class BannerLayer
    {
        public int pattern;
        public Color color = Color.white;

        public BannerLayer() { }
        public BannerLayer(BannerPattern p, Color c) { pattern = (int)p; color = c; }
        public BannerPattern Pattern => (BannerPattern)pattern;
    }

    /// <summary>The player's own civilization: archetype, custom name, banner colors and drawn emblem. Owner: Agent C.</summary>
    [Serializable]
    public class CivilizationProfile
    {
        public const int MaxNameLength = 32;

        public string civId = CivilizationCatalog.DefaultId;
        public string empireName = "";
        public Color bannerColor = new Color(0.72f, 0.08f, 0.12f);
        public Color emblemColor = new Color(0.96f, 0.76f, 0.22f);
        public List<EmblemStroke> strokes = new List<EmblemStroke>();
        public const int MaxLayers = 6;
        /// <summary>Pattern layers over the field color (missing in old JSON → empty list).</summary>
        public List<BannerLayer> layers = new List<BannerLayer>();

        static CivilizationProfile current;

        /// <summary>The active profile. Lazily loaded from disk on first access.</summary>
        public static CivilizationProfile Current
        {
            get
            {
                if (current == null) ProfileStore.Load();
                return current;
            }
            set { current = value ?? CreateDefault(); }
        }

        public static event Action Changed;
        public static void NotifyChanged() => Changed?.Invoke();

        public CivilizationDefinition Definition => CivilizationCatalog.Get(civId);

        /// <summary>The empire name ready for display (sanitized, falls back to the suggested name).</summary>
        public string DisplayName => SanitizeName(empireName, Definition);

        public static CivilizationProfile CreateDefault()
        {
            var def = CivilizationCatalog.Get(CivilizationCatalog.DefaultId);
            return new CivilizationProfile
            {
                civId = def.id,
                empireName = def.suggestedName,
                bannerColor = def.primary,
                emblemColor = def.accent,
                strokes = new List<EmblemStroke>(),
                layers = new List<BannerLayer>()
            };
        }

        /// <summary>Selects an archetype. Keeps a custom name; replaces the name only if it was another archetype's suggestion or empty.</summary>
        public void SelectCivilization(string id)
        {
            var newDef = CivilizationCatalog.Get(id);
            bool nameWasSuggestion = string.IsNullOrWhiteSpace(empireName);
            var defs = CivilizationCatalog.All;
            for (int i = 0; i < defs.Count; i++) if (empireName == defs[i].suggestedName) nameWasSuggestion = true;
            civId = newDef.id;
            if (nameWasSuggestion) empireName = newDef.suggestedName;
        }

        /// <summary>Trim, drop control chars, collapse inner whitespace, max 32 chars; empty -> def.suggestedName.</summary>
        public static string SanitizeName(string raw, CivilizationDefinition def)
        {
            string fallback = def != null ? def.suggestedName : CivilizationCatalog.Get(null).suggestedName;
            if (string.IsNullOrEmpty(raw)) return fallback;
            var sb = new StringBuilder(raw.Length);
            bool lastSpace = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (char.IsControl(c)) continue;
                if (char.IsWhiteSpace(c))
                {
                    if (lastSpace) continue;
                    sb.Append(' ');
                    lastSpace = true;
                }
                else { sb.Append(c); lastSpace = false; }
            }
            string s = sb.ToString().Trim();
            if (s.Length > MaxNameLength) s = s.Substring(0, MaxNameLength).TrimEnd();
            return s.Length == 0 ? fallback : s;
        }

        /// <summary>Repairs invalid data after deserialization.</summary>
        public void Validate()
        {
            if (!CivilizationCatalog.Exists(civId)) civId = CivilizationCatalog.DefaultId;
            empireName = SanitizeName(empireName, Definition);
            if (strokes == null) strokes = new List<EmblemStroke>();
            for (int i = strokes.Count - 1; i >= 0; i--)
            {
                var s = strokes[i];
                if (s == null || s.points == null || s.points.Count == 0) { strokes.RemoveAt(i); continue; }
                s.width = Mathf.Clamp(s.width, 0.005f, 0.3f);
                for (int p = 0; p < s.points.Count; p++)
                    s.points[p] = new Vector2(Mathf.Clamp01(s.points[p].x), Mathf.Clamp01(s.points[p].y));
            }
            bannerColor.a = 1f;
            emblemColor.a = 1f;
            if (layers == null) layers = new List<BannerLayer>();
            int patternCount = Enum.GetValues(typeof(BannerPattern)).Length;
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                var l = layers[i];
                if (l == null || l.pattern < 0 || l.pattern >= patternCount) { layers.RemoveAt(i); continue; }
                l.color.a = 1f;
            }
            while (layers.Count > MaxLayers) layers.RemoveAt(layers.Count - 1);
        }

        public CivilizationProfile Clone()
        {
            var c = new CivilizationProfile
            {
                civId = civId, empireName = empireName, bannerColor = bannerColor, emblemColor = emblemColor,
                strokes = new List<EmblemStroke>(strokes != null ? strokes.Count : 0)
            };
            if (strokes != null)
                for (int i = 0; i < strokes.Count; i++)
                    c.strokes.Add(new EmblemStroke { width = strokes[i].width, points = new List<Vector2>(strokes[i].points) });
            c.layers = new List<BannerLayer>(layers != null ? layers.Count : 0);
            if (layers != null)
                for (int i = 0; i < layers.Count; i++)
                    if (layers[i] != null) c.layers.Add(new BannerLayer { pattern = layers[i].pattern, color = layers[i].color });
            return c;
        }
    }

    /// <summary>Offline local persistence of the profile (JSON in persistentDataPath).</summary>
    public static class ProfileStore
    {
        public static string FilePath => Path.Combine(Application.persistentDataPath, "profile.json");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void LoadAtStartup() => Load();

        public static string SanitizeName(string raw, CivilizationDefinition def) => CivilizationProfile.SanitizeName(raw, def);

        /// <summary>Loads profile.json into CivilizationProfile.Current (default profile when missing/corrupt).</summary>
        public static CivilizationProfile Load()
        {
            CivilizationProfile p = null;
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    if (!string.IsNullOrEmpty(json)) p = JsonUtility.FromJson<CivilizationProfile>(json);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ProfileStore] Could not read profile, using default: " + e.Message);
                p = null;
            }
            if (p == null) p = CivilizationProfile.CreateDefault();
            p.Validate();
            CivilizationProfile.Current = p;
            return p;
        }

        /// <summary>Writes CivilizationProfile.Current to disk. Returns false on IO failure (game continues).</summary>
        public static bool Save()
        {
            var p = CivilizationProfile.Current;
            p.Validate();
            try
            {
                string path = FilePath;
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(p));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ProfileStore] Could not save profile: " + e.Message);
                return false;
            }
        }
    }
}
