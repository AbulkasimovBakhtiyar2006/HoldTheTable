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

        MeshRenderer disc, ring, body;
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
