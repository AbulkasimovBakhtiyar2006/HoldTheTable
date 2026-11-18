using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// The empire's banner beside the Town Hall: wooden pole, gold finial, procedurally waving cloth pinned at the pole
    /// (traveling sine waves, amplitude growing toward the free edge — no cloth physics) carrying the player's own
    /// rasterized emblem, readable from both sides (separate back-face vertices with mirrored U), and a world-space
    /// empire-name plaque that yaw-billboards toward the player. Follows CivilizationProfile.Changed.
    /// </summary>
    public class BannerRenderer : MonoBehaviour
    {
        public const float PoleHeight = 0.32f;
        public const float PoleRadius = 0.008f;
        public const float ClothWidth = 0.16f;
        public const float ClothHeight = 0.11f;
        const int NX = 22, NY = 14;
        /// <summary>Label base height above the pole top (m); clears cloth, finial and the hall roof.</summary>
        public const float LabelAbovePoleTop = 0.26f;
        public const float LabelFontSize = 0.32f;
        public const float LabelOutlineWidth = 0.28f;
        public const float PlaqueHeight = 0.05f;
        public const float PlaqueRimPad = 0.008f;

        public static BannerRenderer Current { get; private set; }

        Transform cloth;
        Mesh mesh;
        Vector3[] verts;
        Vector3[] normals;
        Vector2[] baseXY; // rest positions of the front grid (local cloth space)
        MeshRenderer clothRenderer;
        Material clothMat;
        Texture2D emblemTex;
        TextMeshPro label;
        Transform labelRoot;
        Transform plaque, plaqueRim;
        Transform cam;
        float nextLabelUpdate, nextCamLookup;
        float phase;
        bool subscribed;

        public Texture2D EmblemTexture => emblemTex;

        public static BannerRenderer Spawn(Vector3 basePos, Vector3 up, Vector3 faceDir)
        {
            if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
            up.Normalize();
            Vector3 f = Vector3.ProjectOnPlane(faceDir, up);
            if (f.sqrMagnitude < 1e-6f) f = Vector3.ProjectOnPlane(Vector3.forward, up);
            if (f.sqrMagnitude < 1e-6f) f = Vector3.ProjectOnPlane(Vector3.right, up);
            f.Normalize();

            var root = new GameObject("Banner");
            // root: +y = up, +z = away from the viewer standing on the faceDir side (front of cloth looks along faceDir)
            root.transform.SetPositionAndRotation(basePos, Quaternion.LookRotation(-f, up));
            var br = root.AddComponent<BannerRenderer>();
            br.BuildVisuals();
            br.Apply(CivilizationProfile.Current);
            return br;
        }

        void Awake() { phase = Random.Range(0f, 10f); }

        void OnEnable()
        {
            if (!subscribed) { CivilizationProfile.Changed += OnProfileChanged; subscribed = true; }
            Current = this;
        }

        void OnDisable()
        {
            if (subscribed) { CivilizationProfile.Changed -= OnProfileChanged; subscribed = false; }
        }

        void OnDestroy()
        {
            if (subscribed) { CivilizationProfile.Changed -= OnProfileChanged; subscribed = false; }
            if (Current == this) Current = null;
            if (mesh != null) Destroy(mesh);
            if (clothMat != null) Destroy(clothMat);
            if (emblemTex != null) Destroy(emblemTex);
        }

        void OnProfileChanged() { Apply(CivilizationProfile.Current); }

        // ------------------------------------------------------------------ construction
        void BuildVisuals()
        {
            var wood = Mats.Lit(new Color(0.32f, 0.20f, 0.11f));
            var darkWood = Mats.Lit(new Color(0.22f, 0.14f, 0.08f));
            var stone = Mats.Lit(new Color(0.38f, 0.36f, 0.33f));
            var gold = Mats.LitMetallic(new Color(0.82f, 0.66f, 0.28f), 0.36f);

            var pole = Prim(PrimitiveType.Cylinder, "Pole", transform);
            pole.localPosition = new Vector3(0f, PoleHeight * 0.5f, 0f);
            pole.localScale = new Vector3(PoleRadius * 2f, PoleHeight * 0.5f, PoleRadius * 2f);
            pole.GetComponent<MeshRenderer>().sharedMaterial = wood;

            // stepped stone & dark walnut plinth base with brass collar
            var footBase = Prim(PrimitiveType.Cylinder, "Foot", transform);
            footBase.localPosition = new Vector3(0f, 0.005f, 0f);
            footBase.localScale = new Vector3(0.056f, 0.005f, 0.056f);
            footBase.GetComponent<MeshRenderer>().sharedMaterial = stone;

            var footCollar = Prim(PrimitiveType.Cylinder, "FootCollar", transform);
            footCollar.localPosition = new Vector3(0f, 0.013f, 0f);
            footCollar.localScale = new Vector3(0.036f, 0.006f, 0.036f);
            footCollar.GetComponent<MeshRenderer>().sharedMaterial = darkWood;

            var footRing = Prim(PrimitiveType.Cylinder, "FootRing", transform);
            footRing.localPosition = new Vector3(0f, 0.021f, 0f);
            footRing.localScale = new Vector3(0.024f, 0.003f, 0.024f);
            footRing.GetComponent<MeshRenderer>().sharedMaterial = gold;

            // compound royal spearhead finial at the top of the pole
            var finialCollar = Prim(PrimitiveType.Cylinder, "FinialCollar", transform);
            finialCollar.localPosition = new Vector3(0f, PoleHeight + 0.002f, 0f);
            finialCollar.localScale = new Vector3(0.022f, 0.003f, 0.022f);
            finialCollar.GetComponent<MeshRenderer>().sharedMaterial = gold;

            var finial = Prim(PrimitiveType.Sphere, "Finial", transform);
            finial.localPosition = new Vector3(0f, PoleHeight + 0.012f, 0f);
            finial.localScale = Vector3.one * 0.021f;
            finial.GetComponent<MeshRenderer>().sharedMaterial = gold;

            var finialBand = Prim(PrimitiveType.Cylinder, "FinialBand", transform);
            finialBand.localPosition = new Vector3(0f, PoleHeight + 0.012f, 0f);
            finialBand.localScale = new Vector3(0.025f, 0.002f, 0.025f);
            finialBand.GetComponent<MeshRenderer>().sharedMaterial = gold;

            var finialSpire = Prim(PrimitiveType.Cube, "FinialSpire", transform);
            finialSpire.localPosition = new Vector3(0f, PoleHeight + 0.026f, 0f);
            finialSpire.localRotation = Quaternion.Euler(45f, 45f, 0f);
            finialSpire.localScale = new Vector3(0.011f, 0.018f, 0.011f);
            finialSpire.GetComponent<MeshRenderer>().sharedMaterial = gold;

            // 4 brass hoist rings + tie straps along the hoist edge
            float hoistBottom = PoleHeight - 0.02f - ClothHeight;
            for (int r = 0; r < 4; r++)
            {
                float ry = hoistBottom + (r / 3f) * ClothHeight;
                var ring = Prim(PrimitiveType.Cylinder, "HoistRing_" + r, transform);
                ring.localPosition = new Vector3(0f, ry, 0f);
                ring.localScale = new Vector3(0.021f, 0.0022f, 0.021f);
                ring.GetComponent<MeshRenderer>().sharedMaterial = gold;

                var tie = Prim(PrimitiveType.Cube, "HoistTie_" + r, transform);
                tie.localPosition = new Vector3(PoleRadius * 0.85f, ry, 0f);
                tie.localScale = new Vector3(0.008f, 0.0035f, 0.004f);
                tie.GetComponent<MeshRenderer>().sharedMaterial = gold;
            }

            // cloth: hoisted on the pole; local x from the pole edge outward (viewer's right), y up
            var clothGo = new GameObject("Cloth");
            cloth = clothGo.transform;
            cloth.SetParent(transform, false);
            cloth.localPosition = new Vector3(PoleRadius, hoistBottom, 0f);
            cloth.localRotation = Quaternion.identity;
            var mf = clothGo.AddComponent<MeshFilter>();
            clothRenderer = clothGo.AddComponent<MeshRenderer>();
            clothRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            clothRenderer.receiveShadows = false;
            BuildClothMesh();
            mf.sharedMesh = mesh;

            emblemTex = EmblemRasterizer.CreateTexture(EmblemRasterizer.DefaultSize);
            emblemTex.name = "BannerEmblem";
            clothMat = CreateClothMaterial(emblemTex);
            clothRenderer.sharedMaterial = clothMat;

            BuildLabel();
            Deform(0f);
        }

        static Transform Prim(PrimitiveType t, string name, Transform parent)
        {
            var go = GameObject.CreatePrimitive(t);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) { if (Application.isPlaying) Destroy(col); else DestroyImmediate(col); }
            var mr = go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        static Material CreateClothMaterial(Texture2D tex)
        {
            // Simple Lit so the waves read through directional shading; very low warm emission preserves deep tinctures (Sable/Navy/Gules).
            var sh = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (sh == null) return new Material(Mats.UnlitTex(tex)) { name = "BannerCloth" };
            var m = new Material(sh) { name = "BannerCloth" };
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", 0.08f);
            m.SetFloat("_Cull", 2f); // back-face culled; the back side has its own vertices/triangles
            m.SetTexture("_EmissionMap", tex);
            m.SetColor("_EmissionColor", new Color(0.12f, 0.11f, 0.10f, 1f));
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            return m;
        }

        void BuildClothMesh()
        {
            int n = NX * NY;
            verts = new Vector3[n * 2];
            normals = new Vector3[n * 2];
            baseXY = new Vector2[n];
            var uv = new Vector2[n * 2];
            for (int j = 0; j < NY; j++)
            {
                for (int i = 0; i < NX; i++)
                {
                    int v = j * NX + i;
                    float u = i / (float)(NX - 1), t = j / (float)(NY - 1);
                    // Medieval pennon / swallowtail silhouette:
                    // Gentle vertical taper toward the fly + swallowtail V-indent at the outer fly edge (u > 0.72),
                    // keeping UVs 0..1 so any central crest or pattern stays completely unclipped.
                    float edgeTaper = 1f - 0.11f * u * u;
                    float y = ClothHeight * (0.5f + (t - 0.5f) * edgeTaper);
                    float vCenter = 1f - Mathf.Abs(2f * t - 1f);
                    float uNotch = Mathf.Clamp01((u - 0.70f) / 0.30f);
                    float indent = 0.14f * ClothWidth * (uNotch * uNotch) * Mathf.Pow(vCenter, 1.35f);
                    float x = u * ClothWidth - indent;
                    baseXY[v] = new Vector2(x, y);
                    uv[v] = new Vector2(u, t);          // front: read from the faceDir side
                    uv[v + n] = new Vector2(1f - u, t); // back: mirrored so the emblem is not reversed
                }
            }
            int quads = (NX - 1) * (NY - 1);
            var tris = new int[quads * 12];
            int k = 0;
            for (int j = 0; j < NY - 1; j++)
            {
                for (int i = 0; i < NX - 1; i++)
                {
                    int a = j * NX + i, b = a + NX, c = b + 1, d = a + 1; // a=BL b=TL c=TR d=BR
                    // front (viewer looks along +z): clockwise on screen
                    tris[k++] = a; tris[k++] = b; tris[k++] = c;
                    tris[k++] = a; tris[k++] = c; tris[k++] = d;
                    // back: reversed winding on duplicated vertices
                    tris[k++] = a + n; tris[k++] = c + n; tris[k++] = b + n;
                    tris[k++] = a + n; tris[k++] = d + n; tris[k++] = c + n;
                }
            }
            mesh = new Mesh { name = "BannerCloth" };
            mesh.MarkDynamic();
            mesh.vertices = verts;
            mesh.uv = uv;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(new Vector3(ClothWidth * 0.5f, ClothHeight * 0.5f, 0f),
                new Vector3(ClothWidth + 0.06f, ClothHeight + 0.06f, 0.12f));
        }

        void BuildLabel()
        {
            var lr = new GameObject("NameLabel");
            labelRoot = lr.transform;
            labelRoot.SetParent(transform, false);
            labelRoot.localPosition = new Vector3(ClothWidth * 0.5f, PoleHeight + LabelAbovePoleTop, 0f);

            // outer antique brass frame behind the walnut nameplate
            var rimGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            rimGo.name = "PlaqueRim";
            var rcol = rimGo.GetComponent<Collider>();
            if (rcol != null) { if (Application.isPlaying) Destroy(rcol); else DestroyImmediate(rcol); }
            plaqueRim = rimGo.transform;
            plaqueRim.SetParent(labelRoot, false);
            plaqueRim.localPosition = new Vector3(0f, 0f, 0.003f);
            plaqueRim.localScale = new Vector3(0.2f, PlaqueHeight + PlaqueRimPad, 1f);
            var rmr = rimGo.GetComponent<MeshRenderer>();
            rmr.sharedMaterial = Mats.Unlit(new Color(0.80f, 0.64f, 0.28f));
            rmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rmr.receiveShadows = false;

            var pq = GameObject.CreatePrimitive(PrimitiveType.Quad);
            pq.name = "Plaque";
            var col = pq.GetComponent<Collider>();
            if (col != null) { if (Application.isPlaying) Destroy(col); else DestroyImmediate(col); }
            plaque = pq.transform;
            plaque.SetParent(labelRoot, false);
            plaque.localPosition = new Vector3(0f, 0f, 0.002f);
            plaque.localScale = new Vector3(0.19f, PlaqueHeight, 1f);
            var pmr = pq.GetComponent<MeshRenderer>();
            pmr.sharedMaterial = Mats.Unlit(new Color(0.18f, 0.11f, 0.07f));
            pmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pmr.receiveShadows = false;

            var tgo = new GameObject("Text");
            tgo.transform.SetParent(labelRoot, false);
            label = tgo.AddComponent<TextMeshPro>();
            if (TMP_Settings.defaultFontAsset != null) label.font = TMP_Settings.defaultFontAsset;
            label.rectTransform.sizeDelta = new Vector2(0.7f, PlaqueHeight);
            label.fontSize = LabelFontSize;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.color = new Color(0.95f, 0.88f, 0.70f);
            label.outlineWidth = LabelOutlineWidth;
            label.outlineColor = new Color32(20, 10, 4, 255);
            label.text = "";
            var tr = label.GetComponent<MeshRenderer>();
            if (tr != null) { tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; tr.receiveShadows = false; }
        }

        // ------------------------------------------------------------------ profile
        public void Apply(CivilizationProfile p)
        {
            if (p == null || clothRenderer == null) return;
            emblemTex = EmblemRasterizer.RasterizeProfile(p, EmblemRasterizer.DefaultSize, emblemTex);
            if (clothMat != null)
            {
                clothMat.SetTexture("_BaseMap", emblemTex);
                if (clothMat.HasProperty("_EmissionMap")) clothMat.SetTexture("_EmissionMap", emblemTex);
            }
            if (label != null)
            {
                string name = p.DisplayName;
                label.text = name;
                label.ForceMeshUpdate();
                float w = Mathf.Clamp(label.preferredWidth + 0.034f, 0.09f, 0.8f);
                if (plaque != null) plaque.localScale = new Vector3(w, PlaqueHeight, 1f);
                if (plaqueRim != null) plaqueRim.localScale = new Vector3(w + PlaqueRimPad, PlaqueHeight + PlaqueRimPad, 1f);
            }
        }

        // ------------------------------------------------------------------ animation
        float nextFlap;

        void Update()
        {
            if (Time.time > nextFlap) { nextFlap = Time.time + Random.Range(4f, 7f); ProceduralAudio.Fire(Sfx.BannerFlap, transform.position); }
            Deform(Time.time);

            float now = Time.unscaledTime;
            if (now >= nextLabelUpdate && labelRoot != null)
            {
                nextLabelUpdate = now + 0.1f;
                if (cam == null && now >= nextCamLookup)
                {
                    nextCamLookup = now + 1f;
                    var c = Camera.main;
                    if (c != null) cam = c.transform;
                }
                if (cam != null)
                {
                    Vector3 up = transform.up;
                    Vector3 d = Vector3.ProjectOnPlane(labelRoot.position - cam.position, up);
                    if (d.sqrMagnitude > 1e-6f) labelRoot.rotation = Quaternion.LookRotation(d, up);
                }
            }
        }

        void Deform(float time)
        {
            if (mesh == null) return;
            int n = NX * NY;
            float t = time + phase;
            const float amp = 0.015f;               // meters at the free edge
            const float k = Mathf.PI * 2f / 0.125f; // primary wavelength 12.5 cm
            const float w1 = 3.9f, w2 = 6.5f, w3 = 9.2f;
            for (int v = 0; v < n; v++)
            {
                int i = v % NX;
                int j = v / NX;
                float u = i / (float)(NX - 1);      // 0 at the pole (pinned), 1 at the free fly edge
                float yn = j / (float)(NY - 1);
                Vector2 b = baseXY[v];
                float s = u * u * 0.65f + u * 0.35f;
                float ph1 = k * b.x - w1 * t + yn * 0.55f;
                float ph2 = k * 1.65f * b.x - w2 * t + yn * 1.25f;
                float ph3 = k * 2.5f * b.x - w3 * t - yn * 0.90f;
                float wave = Mathf.Sin(ph1) + 0.32f * Mathf.Sin(ph2) + 0.14f * Mathf.Sin(ph3);
                float z = amp * s * wave;
                float dWaveDx = k * Mathf.Cos(ph1) + 0.32f * k * 1.65f * Mathf.Cos(ph2) + 0.14f * k * 2.5f * Mathf.Cos(ph3);
                float dWaveDy = (0.55f * Mathf.Cos(ph1) + 0.32f * 1.25f * Mathf.Cos(ph2) - 0.14f * 0.90f * Mathf.Cos(ph3)) / ClothHeight;
                float dzdx = amp * (s * dWaveDx + (1.3f * u + 0.35f) / ClothWidth * wave);
                float dzdy = amp * s * dWaveDy;
                float flutter = 0.0022f * u * Mathf.Sin(ph2 * 1.2f + 0.9f);
                float droop = -0.0085f * u * (0.65f + 0.35f * u);
                float x = b.x * (1f - 0.035f * u * Mathf.Abs(Mathf.Sin(ph1))); // foreshortening as cloth ripples
                var p = new Vector3(x, b.y + flutter + droop, z);
                verts[v] = p;
                verts[v + n] = p;
                // front faces the viewer at -z; normal = (dz/dx, dz/dy, -1) normalized
                float inv = 1f / Mathf.Sqrt(dzdx * dzdx + dzdy * dzdy + 1f);
                var nf = new Vector3(dzdx * inv, dzdy * inv, -inv);
                normals[v] = nf;
                normals[v + n] = -nf;
            }
            mesh.vertices = verts;
            mesh.normals = normals;
        }
    }
}
