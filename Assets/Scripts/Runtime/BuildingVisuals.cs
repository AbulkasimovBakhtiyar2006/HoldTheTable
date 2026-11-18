using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Procedural, collider-free geometry for the town hall, defensive units and monsters.
    /// Silhouettes differ per UnitType and per ArchitectureStyle. Shared materials via Mats. Owner: Agent B.
    /// </summary>
    public static class BuildingVisuals
    {
        public const float HallFootprintRadius = 0.085f;
        public const float HallHeight = 0.20f;

        // ---------------------------------------------------------------- palette
        public struct Palette
        {
            public Color main, dark, roof, trim, stone, banner, ground;
        }

        public static Palette PaletteFor(ArchitectureStyle style, Color primary, Color accent, Color banner)
        {
            var p = new Palette { banner = banner };
            switch (style)
            {
                case ArchitectureStyle.TieredRoofs:
                    p.main = new Color(0.92f, 0.89f, 0.82f);   // #EBE3D2 warm lime plaster
                    p.dark = new Color(0.37f, 0.10f, 0.08f);   // #5E1914 lacquered oxblood timber
                    p.roof = primary;
                    p.trim = accent;
                    p.stone = new Color(0.54f, 0.52f, 0.47f);  // #8A8479 warm granite
                    p.ground = new Color(0.43f, 0.40f, 0.35f); // #6E665A earth plinth
                    break;
                case ArchitectureStyle.Timber:
                    p.main = new Color(0.39f, 0.25f, 0.14f);   // #634124 aged oak
                    p.dark = new Color(0.21f, 0.13f, 0.07f);   // #362111 charred pine
                    p.roof = new Color(0.62f, 0.48f, 0.26f);   // #9E7B42 weathered thatch
                    p.trim = primary;
                    p.stone = new Color(0.42f, 0.40f, 0.36f);  // #6B655B fieldstone
                    p.ground = new Color(0.34f, 0.26f, 0.18f); // dark loam
                    break;
                default: // StoneTowers
                    p.main = new Color(0.51f, 0.50f, 0.48f);   // #82807A warm castle limestone
                    p.dark = new Color(0.24f, 0.23f, 0.22f);   // #3D3B38 dark ironstone
                    p.roof = Color.Lerp(new Color(0.22f, 0.26f, 0.32f), primary, 0.28f); // slate roof
                    p.trim = accent;
                    p.stone = new Color(0.37f, 0.36f, 0.34f);  // #5E5C57 dressed ashlar
                    p.ground = new Color(0.31f, 0.30f, 0.28f); // cobble base
                    break;
            }
            return p;
        }

        /// <summary>Resolves the current civilization (or override) into style + colors. Null-safe.</summary>
        public static void Resolve(CivilizationDefinition civOverride, out ArchitectureStyle style, out Palette pal)
        {
            CivilizationDefinition civ = civOverride;
            var prof = CivilizationProfile.Current;
            if (civ == null && prof != null && !string.IsNullOrEmpty(prof.civId)) civ = CivilizationCatalog.Get(prof.civId);
            style = civ != null ? civ.style : ArchitectureStyle.TieredRoofs;
            Color primary = civ != null ? civ.primary : new Color(0.58f, 0.14f, 0.12f);
            Color accent = civ != null ? civ.accent : new Color(0.80f, 0.64f, 0.28f);
            Color banner = prof != null ? prof.bannerColor : primary;
            banner.a = 1f;
            primary.a = 1f;
            accent.a = 1f;
            pal = PaletteFor(style, primary, accent, banner);
        }

        // ---------------------------------------------------------------- kit
        /// <summary>Tiny builder that creates collider-free primitives under a root.</summary>
        public sealed class Kit
        {
            public readonly Transform root;
            public Kit(Transform root) { this.root = root; }

            GameObject Prim(PrimitiveType t, string name)
            {
                var go = GameObject.CreatePrimitive(t);
                go.name = name;
                var col = go.GetComponent<Collider>();
                if (col != null) Object.DestroyImmediate(col);
                go.transform.SetParent(root, false);
                return go;
            }

            static void Paint(GameObject go, Material m)
            {
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = m;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            }

            public GameObject Box(Vector3 center, Vector3 size, Color c, Vector3 euler = default)
            {
                var go = Prim(PrimitiveType.Cube, "box");
                go.transform.localPosition = center;
                go.transform.localRotation = Quaternion.Euler(euler);
                go.transform.localScale = size;
                Paint(go, Mats.Lit(c));
                return go;
            }

            /// <summary>Upright cylinder whose base sits at basePos.</summary>
            public GameObject Cyl(Vector3 basePos, float radius, float height, Color c)
            {
                var go = Prim(PrimitiveType.Cylinder, "cyl");
                go.transform.localPosition = basePos + Vector3.up * (height * 0.5f);
                go.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
                Paint(go, Mats.Lit(c));
                return go;
            }

            /// <summary>Cylinder centered at center, axis rotated by euler (default axis = local up).</summary>
            public GameObject CylRot(Vector3 center, float radius, float length, Color c, Vector3 euler)
            {
                var go = Prim(PrimitiveType.Cylinder, "cyl");
                go.transform.localPosition = center;
                go.transform.localRotation = Quaternion.Euler(euler);
                go.transform.localScale = new Vector3(radius * 2f, length * 0.5f, radius * 2f);
                Paint(go, Mats.Lit(c));
                return go;
            }

            public GameObject Ball(Vector3 center, float diameter, Color c)
            {
                var go = Prim(PrimitiveType.Sphere, "ball");
                go.transform.localPosition = center;
                go.transform.localScale = Vector3.one * diameter;
                Paint(go, Mats.Lit(c));
                return go;
            }

            public GameObject Ellipsoid(Vector3 center, Vector3 size, Color c)
            {
                var go = Prim(PrimitiveType.Sphere, "ellipsoid");
                go.transform.localPosition = center;
                go.transform.localScale = size;
                Paint(go, Mats.Lit(c));
                return go;
            }

            public GameObject MeshObj(Mesh mesh, Vector3 basePos, Vector3 scale, Color c, Vector3 euler = default, Material overrideMat = null)
            {
                var go = new GameObject("mesh");
                go.transform.SetParent(root, false);
                go.transform.localPosition = basePos;
                go.transform.localRotation = Quaternion.Euler(euler);
                go.transform.localScale = scale;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>();
                Paint(go, overrideMat != null ? overrideMat : Mats.Lit(c));
                return go;
            }

            /// <summary>Upturned pagoda roof, base half-size `half`, height `h`.</summary>
            public GameObject PagodaRoof(Vector3 basePos, float half, float h, Color c) => MeshObj(RoofMesh, basePos, new Vector3(half, h, half), c);
            public GameObject Pyramid(Vector3 basePos, float half, float h, Color c) => MeshObj(PyramidMesh, basePos, new Vector3(half, h, half), c);
            public GameObject Cone(Vector3 basePos, float radius, float h, Color c) => MeshObj(ConeMesh, basePos, new Vector3(radius, h, radius), c);
            /// <summary>Gable (triangular prism) roof with ridge along local X.</summary>
            public GameObject GableX(Vector3 basePos, float lengthX, float widthZ, float h, Color c) => MeshObj(GableMesh, basePos, new Vector3(widthZ, h, lengthX), c, new Vector3(0f, 90f, 0f));
        }

        // ---------------------------------------------------------------- meshes
        static readonly Dictionary<string, Mesh> meshCache = new Dictionary<string, Mesh>();

        static Mesh Cached(string key, System.Func<Mesh> make)
        {
            if (meshCache.TryGetValue(key, out var m) && m != null) return m;
            m = make();
            m.name = key;
            meshCache[key] = m;
            return m;
        }

        // profile normalized: height 1, base "radius" 1 (= half-size for square)
        public static Mesh RoofMesh => Cached("htt_pagoda", () => Lathe(4, true, new[] { 1.0f, 0.74f, 0.46f, 0.30f }, new[] { 0f, 0.38f, 0.74f, 1f }, true));
        public static Mesh PyramidMesh => Cached("htt_pyramid", () => Lathe(4, true, new[] { 1f, 0f }, new[] { 0f, 1f }, false));
        public static Mesh ConeMesh => Cached("htt_cone", () => Lathe(10, false, new[] { 1f, 0f }, new[] { 0f, 1f }, false));
        public static Mesh GableMesh => Cached("htt_gable", BuildGable);

        sealed class MeshBuf
        {
            public readonly List<Vector3> v = new List<Vector3>();
            public readonly List<int> t = new List<int>();

            public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
            {
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f) { var tmp = b; b = c; c = tmp; }
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(c);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
            {
                Tri(a, b, c, outward);
                Tri(a, c, d, outward);
            }

            public Mesh ToMesh()
            {
                var m = new Mesh();
                m.SetVertices(v);
                m.SetTriangles(t, 0);
                m.RecalculateNormals();
                m.RecalculateBounds();
                return m;
            }
        }

        static Mesh Lathe(int sides, bool square, float[] r, float[] h, bool topCap)
        {
            var b = new MeshBuf();
            float off = square ? Mathf.PI * 0.25f : 0f;
            float rs = square ? Mathf.Sqrt(2f) : 1f;
            for (int i = 0; i < r.Length - 1; i++)
            {
                for (int s = 0; s < sides; s++)
                {
                    float a0 = off + s * Mathf.PI * 2f / sides, a1 = off + (s + 1) * Mathf.PI * 2f / sides;
                    Vector3 v00 = new Vector3(Mathf.Cos(a0) * r[i] * rs, h[i], Mathf.Sin(a0) * r[i] * rs);
                    Vector3 v01 = new Vector3(Mathf.Cos(a1) * r[i] * rs, h[i], Mathf.Sin(a1) * r[i] * rs);
                    Vector3 v10 = new Vector3(Mathf.Cos(a0) * r[i + 1] * rs, h[i + 1], Mathf.Sin(a0) * r[i + 1] * rs);
                    Vector3 v11 = new Vector3(Mathf.Cos(a1) * r[i + 1] * rs, h[i + 1], Mathf.Sin(a1) * r[i + 1] * rs);
                    Vector3 mid = (v00 + v01 + v10 + v11) * 0.25f;
                    Vector3 outward = new Vector3(mid.x, 0f, mid.z);
                    if (r[i + 1] <= 0f) b.Tri(v00, v01, v10, outward);
                    else b.Quad(v00, v10, v11, v01, outward);
                }
            }
            for (int s = 0; s < sides; s++)
            {
                float a0 = off + s * Mathf.PI * 2f / sides, a1 = off + (s + 1) * Mathf.PI * 2f / sides;
                b.Tri(new Vector3(0f, h[0], 0f), new Vector3(Mathf.Cos(a0) * r[0] * rs, h[0], Mathf.Sin(a0) * r[0] * rs),
                    new Vector3(Mathf.Cos(a1) * r[0] * rs, h[0], Mathf.Sin(a1) * r[0] * rs), Vector3.down);
                int L = r.Length - 1;
                if (topCap && r[L] > 0f)
                    b.Tri(new Vector3(0f, h[L], 0f), new Vector3(Mathf.Cos(a0) * r[L] * rs, h[L], Mathf.Sin(a0) * r[L] * rs),
                        new Vector3(Mathf.Cos(a1) * r[L] * rs, h[L], Mathf.Sin(a1) * r[L] * rs), Vector3.up);
            }
            return b.ToMesh();
        }

        static Mesh BuildGable()
        {
            // base width 1 along X (-0.5..0.5), height 1 (apex at x=0), length 1 along Z.
            var b = new MeshBuf();
            Vector3 c = new Vector3(0f, 0.33f, 0f);
            Vector3 l0 = new Vector3(-0.5f, 0f, -0.5f), r0 = new Vector3(0.5f, 0f, -0.5f), a0 = new Vector3(0f, 1f, -0.5f);
            Vector3 l1 = new Vector3(-0.5f, 0f, 0.5f), r1 = new Vector3(0.5f, 0f, 0.5f), a1 = new Vector3(0f, 1f, 0.5f);
            b.Quad(l0, a0, a1, l1, ((l0 + a1) * 0.5f) - c);
            b.Quad(r0, a0, a1, r1, ((r0 + a1) * 0.5f) - c);
            b.Tri(l0, r0, a0, Vector3.back);
            b.Tri(l1, r1, a1, Vector3.forward);
            b.Quad(l0, r0, r1, l1, Vector3.down);
            return b.ToMesh();
        }

        // ---------------------------------------------------------------- helpers
        public static Transform NewRoot(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        static void Pennant(Kit k, Vector3 basePos, float poleH, Palette p)
        {
            Color brass = new Color(0.80f, 0.64f, 0.28f);
            k.Cyl(basePos, 0.0016f, poleH, p.dark);
            k.Ball(basePos + Vector3.up * (poleH + 0.002f), 0.0045f, brass);
            k.Box(basePos + new Vector3(0.009f, poleH - 0.007f, 0f), new Vector3(0.016f, 0.012f, 0.0016f), p.banner);
            k.Box(basePos + new Vector3(0.009f, poleH - 0.001f, 0f), new Vector3(0.017f, 0.0018f, 0.002f), brass);
            k.Box(basePos + new Vector3(0.020f, poleH - 0.004f, 0f), new Vector3(0.008f, 0.0045f, 0.0014f), p.banner);
            k.Box(basePos + new Vector3(0.020f, poleH - 0.010f, 0f), new Vector3(0.008f, 0.0045f, 0.0014f), p.banner);
        }

        // ---------------------------------------------------------------- town hall
        /// <summary>Builds the Imperial House (town hall) visual under parent. Origin = ground center, +Z = front.</summary>
        public static Transform BuildTownHall(Transform parent, CivilizationDefinition civOverride)
        {
            Resolve(civOverride, out var style, out var p);
            var root = NewRoot(parent, "TownHallVisual");
            var k = new Kit(root);
            switch (style)
            {
                case ArchitectureStyle.TieredRoofs: HallTiered(k, p); break;
                case ArchitectureStyle.Timber: HallTimber(k, p); break;
                default: HallStone(k, p); break;
            }
            return root;
        }

        static void HallTiered(Kit k, Palette p)
        {
            k.Box(new Vector3(0f, 0.01f, 0f), new Vector3(0.17f, 0.02f, 0.17f), p.stone);
            k.Box(new Vector3(0f, 0.005f, 0.095f), new Vector3(0.05f, 0.01f, 0.03f), p.stone);
            // floor 1
            k.Box(new Vector3(0f, 0.045f, 0f), new Vector3(0.11f, 0.05f, 0.11f), p.main);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    k.Cyl(new Vector3(sx * 0.056f, 0.02f, sz * 0.056f), 0.006f, 0.05f, p.dark);
            k.Cyl(new Vector3(-0.02f, 0.02f, 0.058f), 0.004f, 0.05f, p.dark);
            k.Cyl(new Vector3(0.02f, 0.02f, 0.058f), 0.004f, 0.05f, p.dark);
            k.Box(new Vector3(0f, 0.038f, 0.0557f), new Vector3(0.028f, 0.036f, 0.002f), p.dark);
            k.Ball(new Vector3(-0.035f, 0.058f, 0.068f), 0.011f, p.banner); // lanterns
            k.Ball(new Vector3(0.035f, 0.058f, 0.068f), 0.011f, p.banner);
            k.PagodaRoof(new Vector3(0f, 0.07f, 0f), 0.096f, 0.04f, p.roof);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    k.Ball(new Vector3(sx * 0.094f, 0.073f, sz * 0.094f), 0.008f, p.trim);
            // floor 2
            k.Box(new Vector3(0f, 0.12f, 0f), new Vector3(0.07f, 0.036f, 0.07f), p.main);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    k.Cyl(new Vector3(sx * 0.036f, 0.1f, sz * 0.036f), 0.004f, 0.04f, p.dark);
            k.Box(new Vector3(0f, 0.12f, 0.0357f), new Vector3(0.05f, 0.012f, 0.002f), p.trim);
            k.PagodaRoof(new Vector3(0f, 0.137f, 0f), 0.068f, 0.032f, p.roof);
            // floor 3
            k.Box(new Vector3(0f, 0.163f, 0f), new Vector3(0.036f, 0.02f, 0.036f), p.dark);
            k.PagodaRoof(new Vector3(0f, 0.172f, 0f), 0.042f, 0.022f, p.roof);
            k.Cyl(new Vector3(0f, 0.19f, 0f), 0.002f, 0.022f, p.trim);
            k.Ball(new Vector3(0f, 0.213f, 0f), 0.009f, p.trim);
        }

        static void HallTimber(Kit k, Palette p)
        {
            k.Box(new Vector3(0f, 0.004f, 0f), new Vector3(0.17f, 0.008f, 0.17f), p.ground);
            // palisade ring with a gate gap at the front (+Z)
            const int logs = 14;
            for (int i = 0; i < logs; i++)
            {
                float a = i * Mathf.PI * 2f / logs;
                Vector3 dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                if (dir.z > 0.9f) continue; // gate
                Vector3 bp = dir * 0.078f + Vector3.up * 0.008f;
                k.Cyl(bp, 0.0065f, 0.045f, i % 2 == 0 ? p.main : p.dark);
                k.Cone(bp + Vector3.up * 0.045f, 0.0065f, 0.012f, p.main);
            }
            // longhouse
            k.Box(new Vector3(0f, 0.035f, -0.008f), new Vector3(0.095f, 0.054f, 0.065f), p.main);
            for (int i = 0; i < 3; i++)
                k.Box(new Vector3(0f, 0.018f + i * 0.016f, 0.0255f), new Vector3(0.096f, 0.004f, 0.003f), p.dark);
            k.Box(new Vector3(0f, 0.028f, 0.0255f), new Vector3(0.022f, 0.04f, 0.004f), p.dark);
            k.GableX(new Vector3(0f, 0.062f, -0.008f), 0.11f, 0.085f, 0.05f, p.roof);
            // crossed gable horns in civ colour
            for (int sx = -1; sx <= 1; sx += 2)
            {
                k.Box(new Vector3(sx * 0.056f, 0.112f, -0.008f), new Vector3(0.004f, 0.035f, 0.004f), p.trim, new Vector3(30f, 0f, 0f));
                k.Box(new Vector3(sx * 0.056f, 0.112f, -0.008f), new Vector3(0.004f, 0.035f, 0.004f), p.trim, new Vector3(-30f, 0f, 0f));
            }
            // tall watchtower behind
            Vector3 t = new Vector3(-0.045f, 0.008f, -0.05f);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    k.Cyl(t + new Vector3(sx * 0.013f, 0f, sz * 0.013f), 0.0035f, 0.125f, p.dark);
            k.Box(t + new Vector3(0f, 0.125f, 0f), new Vector3(0.042f, 0.007f, 0.042f), p.main);
            k.Box(t + new Vector3(0f, 0.14f, 0f), new Vector3(0.03f, 0.024f, 0.03f), p.main);
            k.Pyramid(t + new Vector3(0f, 0.152f, 0f), 0.026f, 0.034f, p.roof);
            Pennant(k, t + new Vector3(0f, 0.18f, 0f), 0.03f, p);
            Pennant(k, new Vector3(0.06f, 0.06f, 0.06f), 0.035f, p);
        }

        static void HallStone(Kit k, Palette p)
        {
            k.Box(new Vector3(0f, 0.006f, 0f), new Vector3(0.165f, 0.012f, 0.165f), p.stone);
            k.Box(new Vector3(0f, 0.057f, 0f), new Vector3(0.11f, 0.09f, 0.11f), p.main);
            k.Box(new Vector3(0f, 0.09f, 0f), new Vector3(0.113f, 0.006f, 0.113f), p.trim);
            // merlons along the keep top
            for (int side = 0; side < 4; side++)
            {
                Quaternion q = Quaternion.Euler(0f, side * 90f, 0f);
                for (int i = -1; i <= 1; i++)
                    k.Box(q * new Vector3(i * 0.024f, 0.109f, 0.05f), new Vector3(0.013f, 0.014f, 0.01f), p.main, new Vector3(0f, side * 90f, 0f));
                k.Box(q * new Vector3(-0.012f, 0.06f, 0.0555f), new Vector3(0.006f, 0.014f, 0.002f), p.dark, new Vector3(0f, side * 90f, 0f));
                k.Box(q * new Vector3(0.012f, 0.06f, 0.0555f), new Vector3(0.006f, 0.014f, 0.002f), p.dark, new Vector3(0f, side * 90f, 0f));
            }
            k.Box(new Vector3(0f, 0.036f, 0.0556f), new Vector3(0.03f, 0.048f, 0.003f), p.dark);
            k.Ball(new Vector3(0f, 0.06f, 0.0556f), 0.03f, p.dark).transform.localScale = new Vector3(0.03f, 0.03f, 0.003f);
            // corner towers with slate cones
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 b = new Vector3(sx * 0.057f, 0.012f, sz * 0.057f);
                    k.Cyl(b, 0.021f, 0.12f, p.main);
                    k.Cyl(b + Vector3.up * 0.118f, 0.024f, 0.006f, p.trim);
                    k.Cone(b + Vector3.up * 0.124f, 0.025f, 0.045f, p.roof);
                }
            // central donjon
            k.Box(new Vector3(0f, 0.135f, 0f), new Vector3(0.05f, 0.06f, 0.05f), p.main);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    k.Box(new Vector3(sx * 0.019f, 0.171f, sz * 0.019f), new Vector3(0.012f, 0.012f, 0.012f), p.main);
            k.Box(new Vector3(0f, 0.14f, 0.0255f), new Vector3(0.008f, 0.018f, 0.002f), p.dark);
            Pennant(k, new Vector3(0f, 0.165f, 0f), 0.05f, p);
        }

        // ---------------------------------------------------------------- units
        /// <summary>Builds a unit visual. Origin = ground center; walls run along local X. muzzle = arrow spawn (towers) or root.</summary>
        public static Transform BuildUnit(Transform parent, UnitType type, CivilizationDefinition civOverride, out Transform muzzle)
        {
            if (type == UnitType.Barracks)
                return BarracksSpawner.BuildBarracksVisual(parent, civOverride, out muzzle);

            Resolve(civOverride, out var style, out var p);
            var root = NewRoot(parent, type + "Visual");
            var k = new Kit(root);
            Vector3 muzzlePos = Vector3.up * 0.05f;
            switch (type)
            {
                case UnitType.Wall: Wall(k, style, p); break;
                case UnitType.ArcherTower: muzzlePos = Tower(k, style, p); break;
                case UnitType.Farm: Farm(k, style, p); muzzlePos = Vector3.up * 0.04f; break;
                case UnitType.Mine: Mine(k, style, p); muzzlePos = Vector3.up * 0.05f; break;
                default: Barricade(k, style, p); break;
            }
            var m = new GameObject("Muzzle").transform;
            m.SetParent(root, false);
            m.localPosition = muzzlePos;
            muzzle = m;
            return root;
        }

        static void Wall(Kit k, ArchitectureStyle s, Palette p)
        {
            const float L = UnitDef.WallLength;
            switch (s)
            {
                case ArchitectureStyle.TieredRoofs:
                    k.Box(new Vector3(0f, 0.006f, 0f), new Vector3(L, 0.012f, 0.03f), p.stone);
                    k.Box(new Vector3(0f, 0.03f, 0f), new Vector3(L - 0.004f, 0.036f, 0.02f), p.main);
                    k.Box(new Vector3(0f, 0.015f, 0f), new Vector3(L - 0.002f, 0.006f, 0.022f), p.dark);
                    k.GableX(new Vector3(0f, 0.048f, 0f), L + 0.006f, 0.034f, 0.014f, p.roof);
                    k.Cyl(new Vector3(-L * 0.48f, 0.012f, 0f), 0.0045f, 0.038f, p.dark);
                    k.Cyl(new Vector3(L * 0.48f, 0.012f, 0f), 0.0045f, 0.038f, p.dark);
                    k.Ball(new Vector3(0f, 0.063f, 0f), 0.006f, p.trim);
                    break;
                case ArchitectureStyle.Timber:
                    const int n = 8;
                    for (int i = 0; i < n; i++)
                    {
                        float x = -L * 0.5f + L * (i + 0.5f) / n;
                        float h = 0.042f + ((i * 37) % 5) * 0.002f;
                        k.Cyl(new Vector3(x, 0f, 0f), 0.0062f, h, i % 2 == 0 ? p.main : p.dark);
                        k.Cone(new Vector3(x, h, 0f), 0.0062f, 0.012f, p.main);
                    }
                    k.Box(new Vector3(0f, 0.016f, 0.0065f), new Vector3(L, 0.005f, 0.003f), p.dark);
                    k.Box(new Vector3(0f, 0.032f, 0.0065f), new Vector3(L, 0.005f, 0.003f), p.trim);
                    break;
                default:
                    k.Box(new Vector3(0f, 0.022f, 0f), new Vector3(L, 0.044f, UnitDef.WallThickness), p.main);
                    k.Box(new Vector3(0f, 0.004f, 0f), new Vector3(L + 0.004f, 0.008f, UnitDef.WallThickness + 0.006f), p.stone);
                    k.Box(new Vector3(0f, 0.034f, 0f), new Vector3(L + 0.001f, 0.004f, UnitDef.WallThickness + 0.001f), p.trim);
                    for (int i = 0; i < 4; i++)
                        k.Box(new Vector3(-0.0375f + i * 0.025f, 0.05f, 0f), new Vector3(0.014f, 0.012f, UnitDef.WallThickness + 0.002f), p.main);
                    break;
            }
        }

        static void Archer(Kit k, Vector3 at, Palette p)
        {
            k.Cyl(at, 0.0045f, 0.012f, p.trim);
            k.Ball(at + Vector3.up * 0.016f, 0.008f, new Color(0.93f, 0.78f, 0.62f));
            k.CylRot(at + new Vector3(0.006f, 0.012f, 0.002f), 0.0008f, 0.02f, p.dark, new Vector3(0f, 0f, 10f)); // bow
        }

        static Vector3 Tower(Kit k, ArchitectureStyle s, Palette p)
        {
            switch (s)
            {
                case ArchitectureStyle.TieredRoofs:
                    k.Box(new Vector3(0f, 0.005f, 0f), new Vector3(0.05f, 0.01f, 0.05f), p.stone);
                    for (int sx = -1; sx <= 1; sx += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                            k.Cyl(new Vector3(sx * 0.018f, 0.01f, sz * 0.018f), 0.0035f, 0.1f, p.dark);
                    k.Box(new Vector3(0f, 0.035f, 0f), new Vector3(0.03f, 0.04f, 0.03f), p.main);
                    k.PagodaRoof(new Vector3(0f, 0.055f, 0f), 0.034f, 0.014f, p.roof);
                    k.Box(new Vector3(0f, 0.072f, 0f), new Vector3(0.046f, 0.005f, 0.046f), p.main);
                    Archer(k, new Vector3(0f, 0.075f, 0f), p);
                    k.PagodaRoof(new Vector3(0f, 0.105f, 0f), 0.038f, 0.026f, p.roof);
                    k.Ball(new Vector3(0f, 0.133f, 0f), 0.007f, p.trim);
                    return new Vector3(0f, 0.09f, 0.01f);
                case ArchitectureStyle.Timber:
                    for (int sx = -1; sx <= 1; sx += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                            k.Cyl(new Vector3(sx * 0.019f, 0f, sz * 0.019f), 0.004f, 0.11f, p.dark);
                    k.Box(new Vector3(0f, 0.04f, 0.019f), new Vector3(0.05f, 0.004f, 0.004f), p.main, new Vector3(0f, 0f, 35f));
                    k.Box(new Vector3(0f, 0.04f, -0.019f), new Vector3(0.05f, 0.004f, 0.004f), p.main, new Vector3(0f, 0f, -35f));
                    k.Box(new Vector3(0f, 0.08f, 0f), new Vector3(0.054f, 0.007f, 0.054f), p.main);
                    for (int side = 0; side < 4; side++)
                    {
                        Quaternion q = Quaternion.Euler(0f, side * 90f, 0f);
                        k.Box(q * new Vector3(0f, 0.09f, 0.025f), new Vector3(0.054f, 0.012f, 0.004f), p.main, new Vector3(0f, side * 90f, 0f));
                    }
                    Archer(k, new Vector3(0f, 0.084f, 0f), p);
                    k.Pyramid(new Vector3(0f, 0.11f, 0f), 0.036f, 0.032f, p.roof);
                    k.Box(new Vector3(0f, 0.096f, 0.027f), new Vector3(0.012f, 0.008f, 0.002f), p.trim);
                    return new Vector3(0f, 0.1f, 0.01f);
                default:
                    k.Cyl(new Vector3(0f, 0f, 0f), 0.024f, 0.012f, p.stone);
                    k.Cyl(new Vector3(0f, 0.01f, 0f), 0.021f, 0.085f, p.main);
                    k.Cyl(new Vector3(0f, 0.06f, 0f), 0.0215f, 0.005f, p.trim);
                    k.Cyl(new Vector3(0f, 0.093f, 0f), 0.025f, 0.006f, p.main);
                    for (int i = 0; i < 6; i++)
                    {
                        float a = i * 60f;
                        Quaternion q = Quaternion.Euler(0f, a, 0f);
                        k.Box(q * new Vector3(0f, 0.104f, 0.021f), new Vector3(0.011f, 0.013f, 0.008f), p.main, new Vector3(0f, a, 0f));
                    }
                    k.Box(new Vector3(0f, 0.045f, 0.0205f), new Vector3(0.004f, 0.014f, 0.002f), p.dark);
                    k.Box(new Vector3(0f, 0.075f, 0.0205f), new Vector3(0.004f, 0.012f, 0.002f), p.dark);
                    Archer(k, new Vector3(0f, 0.099f, 0f), p);
                    return new Vector3(0f, 0.11f, 0.01f);
            }
        }

        static void Barricade(Kit k, ArchitectureStyle s, Palette p)
        {
            float r = UnitDef.Get(UnitType.Barricade).range;
            Color zone = Color.Lerp(p.ground, p.trim, 0.35f);
            zone.a = 0.09f;
            // subtle grounded slow-zone disk showing the barricade radius
            k.CylRot(new Vector3(0f, 0.0012f, 0f), r, 0.002f, zone, Vector3.zero).GetComponent<MeshRenderer>().sharedMaterial = Mats.Transparent(zone);

            switch (s)
            {
                case ArchitectureStyle.Timber:
                case ArchitectureStyle.TieredRoofs:
                {
                    Color stake = s == ArchitectureStyle.TieredRoofs ? new Color(0.62f, 0.50f, 0.32f) : p.main;
                    int pairs = s == ArchitectureStyle.TieredRoofs ? 4 : 5;
                    k.CylRot(new Vector3(0f, 0.012f, 0f), 0.004f, 0.085f, p.dark, new Vector3(0f, 0f, 90f));
                    for (int i = 0; i < pairs; i++)
                    {
                        float x = -0.036f + 0.072f * i / (pairs - 1);
                        k.CylRot(new Vector3(x, 0.012f, 0f), 0.0026f, 0.042f, stake, new Vector3(40f, 0f, 0f));
                        k.CylRot(new Vector3(x, 0.012f, 0f), 0.0026f, 0.042f, stake, new Vector3(-40f, 0f, 0f));
                    }
                    if (s == ArchitectureStyle.TieredRoofs)
                    {
                        k.Ball(new Vector3(-0.045f, 0.012f, 0f), 0.008f, p.trim);
                        k.Ball(new Vector3(0.045f, 0.012f, 0f), 0.008f, p.trim);
                    }
                    else
                    {
                        k.Ellipsoid(new Vector3(0.03f, 0.005f, 0.02f), new Vector3(0.02f, 0.01f, 0.014f), p.stone);
                    }
                    break;
                }
                default:
                {
                    Vector3[] rocks =
                    {
                        new Vector3(-0.03f, 0.007f, 0.004f), new Vector3(-0.012f, 0.008f, -0.006f), new Vector3(0.008f, 0.009f, 0.005f),
                        new Vector3(0.027f, 0.007f, -0.003f), new Vector3(0.0f, 0.016f, 0f),
                    };
                    for (int i = 0; i < rocks.Length; i++)
                        k.Box(rocks[i], Vector3.one * (0.016f + (i % 3) * 0.003f), i % 2 == 0 ? p.main : p.stone, new Vector3(i * 23f, i * 41f, i * 17f));
                    for (int i = 0; i < 4; i++)
                    {
                        float x = -0.033f + i * 0.022f;
                        k.Cone(new Vector3(x, 0.006f, (i % 2 == 0 ? 0.012f : -0.012f)), 0.003f, 0.03f, p.dark);
                    }
                    k.Box(new Vector3(0f, 0.004f, 0.014f), new Vector3(0.07f, 0.003f, 0.003f), p.trim);
                    break;
                }
            }
        }

        static void Farm(Kit k, ArchitectureStyle s, Palette p)
        {
            Color soil = new Color(0.32f, 0.22f, 0.13f);
            Color paddyWater = new Color(0.23f, 0.33f, 0.36f); // #3A545C muted slate-teal water
            Color crop, cropTip;
            switch (s)
            {
                case ArchitectureStyle.TieredRoofs:
                    crop = new Color(0.37f, 0.48f, 0.23f);    // #5E7A3A olive-sage rice
                    cropTip = new Color(0.49f, 0.60f, 0.32f); // #7D9952 warm sage tip
                    break;
                case ArchitectureStyle.Timber:
                    crop = new Color(0.72f, 0.58f, 0.26f);    // #B89442 golden wheat stalk
                    cropTip = new Color(0.79f, 0.62f, 0.24f); // #C99E3E warm ochre grain
                    break;
                default:
                    crop = new Color(0.27f, 0.43f, 0.24f);    // #456E3D herb/cabbage leaf
                    cropTip = new Color(0.39f, 0.55f, 0.34f); // #638C57 muted leaf crown
                    break;
            }
            k.Box(new Vector3(0f, 0.002f, 0f), new Vector3(0.095f, 0.004f, 0.095f), p.ground);
            // two field patches with crop rows (front half), water strip for paddies
            for (int f = 0; f < 2; f++)
            {
                float fx = f == 0 ? -0.023f : 0.023f;
                k.Box(new Vector3(fx, 0.005f, 0.018f), new Vector3(0.04f, 0.003f, 0.05f), s == ArchitectureStyle.TieredRoofs ? paddyWater : soil);
                for (int r = 0; r < 4; r++)
                {
                    float x = fx - 0.015f + r * 0.01f;
                    k.Box(new Vector3(x, 0.01f, 0.018f), new Vector3(0.005f, 0.008f, 0.044f), crop);
                    if (s == ArchitectureStyle.StoneTowers)
                        for (int c = 0; c < 3; c++) k.Ball(new Vector3(x, 0.014f, 0.003f + c * 0.015f), 0.007f, cropTip);
                    else k.Box(new Vector3(x, 0.0155f, 0.018f), new Vector3(0.006f, 0.003f, 0.044f), cropTip);
                }
            }
            // barn at the back
            k.Box(new Vector3(0f, 0.016f, -0.03f), new Vector3(0.04f, 0.028f, 0.026f), s == ArchitectureStyle.TieredRoofs ? p.main : p.dark);
            k.Box(new Vector3(0f, 0.012f, -0.0165f), new Vector3(0.012f, 0.018f, 0.002f), s == ArchitectureStyle.TieredRoofs ? p.dark : p.main);
            if (s == ArchitectureStyle.TieredRoofs) k.PagodaRoof(new Vector3(0f, 0.03f, -0.03f), 0.026f, 0.016f, p.roof);
            else k.GableX(new Vector3(0f, 0.03f, -0.03f), 0.046f, 0.032f, 0.018f, p.roof);
            // fence posts (stone wall for StoneTowers)
            for (int i = 0; i < 5; i++)
            {
                float x = -0.045f + i * 0.0225f;
                if (s == ArchitectureStyle.StoneTowers) k.Box(new Vector3(x, 0.006f, 0.047f), new Vector3(0.02f, 0.008f, 0.006f), p.stone);
                else k.Cyl(new Vector3(x, 0.002f, 0.047f), 0.0018f, 0.014f, p.dark);
            }
            k.Box(new Vector3(0.035f, 0.022f, -0.038f), new Vector3(0.012f, 0.008f, 0.0015f), p.banner);
        }

        static void Mine(Kit k, ArchitectureStyle s, Palette p)
        {
            Color rock = s == ArchitectureStyle.Timber ? new Color(0.46f, 0.41f, 0.35f) : s == ArchitectureStyle.TieredRoofs ? new Color(0.52f, 0.49f, 0.45f) : new Color(0.40f, 0.39f, 0.38f);
            Color rockDark = rock * 0.74f; rockDark.a = 1f;
            Color gold = new Color(0.83f, 0.65f, 0.22f), iron = new Color(0.26f, 0.25f, 0.24f);
            // rock mound
            k.Ellipsoid(new Vector3(0f, 0.018f, -0.012f), new Vector3(0.085f, 0.05f, 0.07f), rock);
            k.Ellipsoid(new Vector3(-0.025f, 0.03f, -0.02f), new Vector3(0.04f, 0.04f, 0.04f), rockDark);
            k.Ellipsoid(new Vector3(0.022f, 0.035f, -0.018f), new Vector3(0.035f, 0.035f, 0.035f), rock);
            k.Ball(new Vector3(0.03f, 0.03f, 0.005f), 0.008f, gold); // ore vein
            k.Ball(new Vector3(-0.032f, 0.022f, 0.01f), 0.006f, gold);
            // entrance with timber/stone frame
            k.Box(new Vector3(0f, 0.014f, 0.02f), new Vector3(0.022f, 0.026f, 0.004f), new Color(0.05f, 0.04f, 0.04f));
            Color frame = s == ArchitectureStyle.StoneTowers ? p.main : p.dark;
            k.Box(new Vector3(-0.013f, 0.015f, 0.023f), new Vector3(0.004f, 0.03f, 0.004f), frame);
            k.Box(new Vector3(0.013f, 0.015f, 0.023f), new Vector3(0.004f, 0.03f, 0.004f), frame);
            k.Box(new Vector3(0f, 0.031f, 0.023f), new Vector3(0.032f, 0.005f, 0.005f), s == ArchitectureStyle.TieredRoofs ? p.roof : frame);
            // rails + cart full of ore
            k.Box(new Vector3(-0.005f, 0.002f, 0.04f), new Vector3(0.002f, 0.002f, 0.035f), iron);
            k.Box(new Vector3(0.005f, 0.002f, 0.04f), new Vector3(0.002f, 0.002f, 0.035f), iron);
            k.Box(new Vector3(0f, 0.01f, 0.045f), new Vector3(0.02f, 0.01f, 0.016f), p.main);
            k.CylRot(new Vector3(-0.011f, 0.005f, 0.04f), 0.004f, 0.003f, iron, new Vector3(0f, 0f, 90f));
            k.CylRot(new Vector3(0.011f, 0.005f, 0.04f), 0.004f, 0.003f, iron, new Vector3(0f, 0f, 90f));
            k.CylRot(new Vector3(-0.011f, 0.005f, 0.05f), 0.004f, 0.003f, iron, new Vector3(0f, 0f, 90f));
            k.CylRot(new Vector3(0.011f, 0.005f, 0.05f), 0.004f, 0.003f, iron, new Vector3(0f, 0f, 90f));
            k.Ball(new Vector3(-0.004f, 0.017f, 0.045f), 0.008f, gold);
            k.Ball(new Vector3(0.005f, 0.017f, 0.043f), 0.007f, gold);
            // pickaxe leaning on the mound
            k.CylRot(new Vector3(0.03f, 0.016f, 0.025f), 0.0015f, 0.032f, p.dark, new Vector3(20f, 0f, -25f));
            k.Box(new Vector3(0.037f, 0.031f, 0.03f), new Vector3(0.018f, 0.003f, 0.003f), iron, new Vector3(0f, 0f, -25f));
            k.Box(new Vector3(-0.03f, 0.05f, -0.02f), new Vector3(0.012f, 0.008f, 0.0015f), p.banner);
            k.Cyl(new Vector3(-0.036f, 0.035f, -0.02f), 0.001f, 0.025f, p.dark);
        }

        /// <summary>Destroys every collider under root (e.g. for the placement token mini hall).</summary>
        public static void StripColliders(GameObject root)
        {
            var cols = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++) Object.DestroyImmediate(cols[i]);
        }
    }
}
