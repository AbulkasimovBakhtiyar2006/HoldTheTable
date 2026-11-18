using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Translucent green/red placement preview: footprint disc + column silhouette (shared transparent materials).
    /// Created at runtime by the placement controllers (no colliders, no shadows). Owner: Agent A.
    /// </summary>
    public class PlacementGhost : MonoBehaviour
    {
        static readonly Color ValidDisc = new Color(0.25f, 1f, 0.35f, 0.45f);
        static readonly Color InvalidDisc = new Color(1f, 0.22f, 0.2f, 0.45f);
        static readonly Color ValidBody = new Color(0.25f, 1f, 0.35f, 0.22f);
        static readonly Color InvalidBody = new Color(1f, 0.22f, 0.2f, 0.22f);

        static readonly Color ValidModel = new Color(0.25f, 1f, 0.35f, 0.5f);
        static readonly Color InvalidModel = new Color(1f, 0.22f, 0.2f, 0.5f);

        MeshRenderer disc, ring, body;
        // cached visual-only building models, one per UnitType (built lazily)
        GameObject[] models;
        MeshRenderer[][] modelRends;
        int activeModel = -1;
        bool modelValid;
        bool modelHasState;
        string modelCivId;
        bool subscribed;
        bool lastValid;
        bool hasState;
        public float silhouetteHeight = 0.08f;
        public bool Visible { get; private set; }
        public bool LastValid => lastValid;

        public static PlacementGhost Create(string name, float silhouetteHeight)
        {
            var go = new GameObject(name);
            var g = go.AddComponent<PlacementGhost>();
            g.silhouetteHeight = silhouetteHeight;
            g.Build();
            g.Hide();
            return g;
        }

        void Awake()
        {
            if (!subscribed) { CivilizationProfile.Changed += InvalidateModels; subscribed = true; }
        }

        void OnDestroy()
        {
            if (subscribed) { CivilizationProfile.Changed -= InvalidateModels; subscribed = false; }
        }

        void InvalidateModels()
        {
            if (models == null) return;
            for (int i = 0; i < models.Length; i++)
            {
                if (models[i] != null) Destroy(models[i]);
                models[i] = null;
                modelRends[i] = null;
            }
            activeModel = -1;
            modelHasState = false;
        }

        static string CurrentCivId()
        {
            var prof = CivilizationProfile.Current;
            return prof != null ? prof.civId : null;
        }

        bool EnsureModel(UnitType type)
        {
            int idx = (int)type;
            if (models == null)
            {
                models = new GameObject[UnitDef.TypeCount];
                modelRends = new MeshRenderer[UnitDef.TypeCount][];
            }
            string civ = CurrentCivId();
            if (!ReferenceEquals(civ, modelCivId) && civ != modelCivId) { InvalidateModels(); }
            modelCivId = civ;
            if (models[idx] != null) return true;
            var f = BuildingFactory.Instance;
            if (f == null) return false;
            var m = f.BuildMiniUnit(transform, type, 1f); // same visual as PlacedUnit (final scale 1), colliders stripped
            m.name = "GhostModel_" + type;
            m.transform.localPosition = Vector3.zero;
            m.transform.localRotation = Quaternion.identity;
            var rs = m.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                rs[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                rs[i].receiveShadows = false;
            }
            m.SetActive(false);
            models[idx] = m;
            modelRends[idx] = rs;
            return true;
        }

        void ApplyModelTint(int idx, bool valid)
        {
            var mat = Mats.Transparent(valid ? ValidModel : InvalidModel);
            var rs = modelRends[idx];
            for (int i = 0; i < rs.Length; i++) rs[i].sharedMaterial = mat;
        }

        void Build()
        {
            disc = Part(PrimitiveType.Cylinder, "Disc", new Vector3(0f, 0.002f, 0f), new Vector3(1f, 0.001f, 1f));
            ring = Part(PrimitiveType.Cylinder, "Edge", new Vector3(0f, 0.001f, 0f), new Vector3(1.12f, 0.0008f, 1.12f));
            body = Part(PrimitiveType.Cylinder, "Silhouette", new Vector3(0f, 0.5f, 0f), new Vector3(0.7f, 0.5f, 0.7f));
            Apply(true);
        }

        MeshRenderer Part(PrimitiveType t, string n, Vector3 lp, Vector3 ls)
        {
            var go = GameObject.CreatePrimitive(t);
            go.name = n;
            var c = go.GetComponent<Collider>();
            if (c != null) Destroy(c);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = lp;
            go.transform.localScale = ls;
            var r = go.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        void Apply(bool valid)
        {
            disc.sharedMaterial = Mats.Transparent(valid ? ValidDisc : InvalidDisc);
            ring.sharedMaterial = Mats.Transparent(valid ? ValidDisc : InvalidDisc);
            body.sharedMaterial = Mats.Transparent(valid ? ValidBody : InvalidBody);
            lastValid = valid;
            hasState = true;
        }

        /// <summary>Show at a surface point. radius = footprint radius (m); up = surface normal.</summary>
        public void Show(Vector3 pos, Vector3 up, float radius, bool valid)
        {
            if (!hasState || valid != lastValid) Apply(valid);
            HideModels();
            body.gameObject.SetActive(true);
            if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
            Vector3 f = Vector3.ProjectOnPlane(Vector3.forward, up);
            if (f.sqrMagnitude < 1e-6f) f = Vector3.ProjectOnPlane(Vector3.right, up);
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(f, up));
            transform.localScale = new Vector3(radius * 2f, silhouetteHeight, radius * 2f);
            // keep disc/ring thin regardless of silhouette height
            float inv = 1f / Mathf.Max(0.001f, silhouetteHeight);
            disc.transform.localScale = new Vector3(1f, 0.001f * inv, 1f);
            ring.transform.localScale = new Vector3(1.12f, 0.0008f * inv, 1.12f);
            disc.transform.localPosition = new Vector3(0f, 0.002f * inv, 0f);
            ring.transform.localPosition = new Vector3(0f, 0.001f * inv, 0f);
            if (!Visible)
            {
                gameObject.SetActive(true);
                Visible = true;
            }
        }

        void HideModels()
        {
            if (activeModel >= 0 && models != null && models[activeModel] != null) models[activeModel].SetActive(false);
            activeModel = -1;
        }

        /// <summary>
        /// Show the real building model (semi-transparent green/red) at pos with the given rotation, plus a thin flat
        /// footprint ring (radius = footprint). Falls back to the plain silhouette if the factory is unavailable.
        /// </summary>
        public void ShowBuilding(UnitType type, Vector3 pos, Quaternion rot, Vector3 up, float footprint, bool valid)
        {
            if (!EnsureModel(type))
            {
                Show(pos, up, footprint, valid);
                return;
            }
            int idx = (int)type;
            if (!hasState || valid != lastValid) Apply(valid);
            if (activeModel != idx)
            {
                HideModels();
                models[idx].SetActive(true);
                activeModel = idx;
                modelHasState = false;
            }
            if (!modelHasState || valid != modelValid)
            {
                ApplyModelTint(idx, valid);
                modelValid = valid;
                modelHasState = true;
            }
            body.gameObject.SetActive(false);
            transform.SetPositionAndRotation(pos, rot);
            transform.localScale = Vector3.one;
            // thin flat footprint disc + ring (local units == meters)
            float d = footprint * 2f;
            disc.transform.localScale = new Vector3(d, 0.001f, d);
            ring.transform.localScale = new Vector3(d * 1.12f, 0.0008f, d * 1.12f);
            disc.transform.localPosition = new Vector3(0f, 0.002f, 0f);
            ring.transform.localPosition = new Vector3(0f, 0.001f, 0f);
            if (!Visible)
            {
                gameObject.SetActive(true);
                Visible = true;
            }
        }

        public void Hide()
        {
            if (Visible || gameObject.activeSelf)
            {
                gameObject.SetActive(false);
                Visible = false;
            }
        }
    }
}
