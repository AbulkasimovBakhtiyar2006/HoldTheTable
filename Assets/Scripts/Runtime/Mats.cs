using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>Shared, cached URP materials. Shaders are listed in Always Included Shaders by the leader's ProjectSetup.</summary>
    public static class Mats
    {
        static readonly Dictionary<long, Material> cache = new Dictionary<long, Material>();
        static readonly Dictionary<Texture, Material> texCache = new Dictionary<Texture, Material>();
        static readonly Dictionary<Texture, Material> texCache2 = new Dictionary<Texture, Material>();
        static Shader lit, unlit;

        static Shader LitShader => lit != null ? lit : (lit = Shader.Find("Universal Render Pipeline/Simple Lit") ?? Shader.Find("Universal Render Pipeline/Lit"));
        static Shader UnlitShader => unlit != null ? unlit : (unlit = Shader.Find("Universal Render Pipeline/Unlit"));

        static long Key(Color c, int kind)
        {
            Color32 k = c;
            return ((long)kind << 32) | ((long)k.r << 24) | ((long)k.g << 16) | ((long)k.b << 8) | k.a;
        }

        public static Material Lit(Color c)
        {
            long k = Key(c, 1);
            if (cache.TryGetValue(k, out var m) && m != null) return m;
            m = new Material(LitShader) { name = "Lit_" + ColorUtility.ToHtmlStringRGB(c), enableInstancing = true };
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", 0.12f);
            if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", new Color(0.14f, 0.13f, 0.11f, 1f));
            cache[k] = m;
            return m;
        }

        public static Material LitMetallic(Color c, float smoothness = 0.36f)
        {
            long k = Key(c, 4);
            if (cache.TryGetValue(k, out var m) && m != null) return m;
            m = new Material(LitShader) { name = "Metal_" + ColorUtility.ToHtmlStringRGB(c), enableInstancing = true };
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smoothness);
            if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", Color.Lerp(c, Color.white, 0.35f));
            cache[k] = m;
            return m;
        }

        public static Material Unlit(Color c)
        {
            long k = Key(c, 2);
            if (cache.TryGetValue(k, out var m) && m != null) return m;
            m = new Material(UnlitShader) { name = "Unlit_" + ColorUtility.ToHtmlStringRGB(c), enableInstancing = true };
            m.SetColor("_BaseColor", c);
            cache[k] = m;
            return m;
        }

        public static Material Transparent(Color c)
        {
            long k = Key(c, 3);
            if (cache.TryGetValue(k, out var m) && m != null) return m;
            m = new Material(UnlitShader) { name = "Transp_" + ColorUtility.ToHtmlStringRGBA(c) };
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            m.SetColor("_BaseColor", c);
            cache[k] = m;
            return m;
        }

        public static Material UnlitTex(Texture t) => TexMat(t, false);
        public static Material UnlitTexDoubleSided(Texture t) => TexMat(t, true);

        static Material TexMat(Texture t, bool twoSided)
        {
            var dict = twoSided ? texCache2 : texCache;
            if (dict.TryGetValue(t, out var m) && m != null) return m;
            m = new Material(UnlitShader) { name = "Tex_" + t.name };
            m.SetTexture("_BaseMap", t);
            m.SetColor("_BaseColor", Color.white);
            if (twoSided) m.SetFloat("_Cull", 0f);
            dict[t] = m;
            return m;
        }
    }
}
