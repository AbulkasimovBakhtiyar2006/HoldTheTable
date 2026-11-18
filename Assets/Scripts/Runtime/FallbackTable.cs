using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Procedural virtual table used when MRUK finds no surface (or fallback is forced). Implements the same
    /// ITableSurface as MRUK planes so the gameplay loop is identical. Owner: Agent A.
    /// The GameObject's transform sits at the center of the table top; local +Y is the surface normal.
    /// </summary>
    public class FallbackTable : MonoBehaviour, ITableSurface
    {
        public Vector2 size = new Vector2(0.9f, 0.6f);
        public Color topColor = new Color(0.55f, 0.38f, 0.22f);
        public Color legColor = new Color(0.36f, 0.24f, 0.14f);
        public Color rimColor = new Color(0.78f, 0.62f, 0.3f);

        const float TopThickness = 0.03f;
        const float PlaneTolerance = 0.06f;
        bool built;

        public string Id => "fallback_table";
        public bool IsFallback => true;
        public Pose Pose => new Pose(transform.position, transform.rotation);
        public float Height => transform.position.y;
        public Vector2 Size => size;

        /// <summary>Creates the table at a world top-center pose. Height of legs = top height above floorY.</summary>
        public static FallbackTable Spawn(Vector3 topCenter, Quaternion yaw, Vector2 size, float floorY)
        {
            var go = new GameObject("FallbackTable");
            go.transform.SetPositionAndRotation(topCenter, yaw);
            var t = go.AddComponent<FallbackTable>();
            t.size = size;
            t.Build(Mathf.Max(0.2f, topCenter.y - floorY));
            return t;
        }

        void Build(float legHeight)
        {
            if (built) return;
            built = true;
            var top = Part(PrimitiveType.Cube, "Top", new Vector3(0f, -TopThickness * 0.5f, 0f), new Vector3(size.x, TopThickness, size.y), Mats.Lit(topColor));
            // thin gold rim so the edge of the battlefield is readable in passthrough
            float rim = 0.012f;
            Part(PrimitiveType.Cube, "RimN", new Vector3(0f, 0.002f, size.y * 0.5f - rim * 0.5f), new Vector3(size.x, 0.004f, rim), Mats.Lit(rimColor));
            Part(PrimitiveType.Cube, "RimS", new Vector3(0f, 0.002f, -size.y * 0.5f + rim * 0.5f), new Vector3(size.x, 0.004f, rim), Mats.Lit(rimColor));
            Part(PrimitiveType.Cube, "RimE", new Vector3(size.x * 0.5f - rim * 0.5f, 0.002f, 0f), new Vector3(rim, 0.004f, size.y), Mats.Lit(rimColor));
            Part(PrimitiveType.Cube, "RimW", new Vector3(-size.x * 0.5f + rim * 0.5f, 0.002f, 0f), new Vector3(rim, 0.004f, size.y), Mats.Lit(rimColor));
            float lh = legHeight - TopThickness;
            float lx = size.x * 0.5f - 0.05f, lz = size.y * 0.5f - 0.05f;
            for (int i = 0; i < 4; i++)
            {
                float sx = (i & 1) == 0 ? -1f : 1f, sz = (i & 2) == 0 ? -1f : 1f;
                Part(PrimitiveType.Cylinder, "Leg" + i, new Vector3(lx * sx, -TopThickness - lh * 0.5f, lz * sz), new Vector3(0.05f, lh * 0.5f, 0.05f), Mats.Lit(legColor));
            }
            top.name = "Top";
        }

        GameObject Part(PrimitiveType type, string n, Vector3 localPos, Vector3 localScale, Material m)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = n;
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        public bool IsPointSupported(Vector3 w)
        {
            var l = transform.InverseTransformPoint(w);
            return Mathf.Abs(l.y) <= PlaneTolerance && Mathf.Abs(l.x) <= size.x * 0.5f && Mathf.Abs(l.z) <= size.y * 0.5f;
        }

        public bool IsFootprintSupported(Vector3 center, float radius) => SurfaceMath.FootprintSupported(this, center, radius);

        public Vector3 ClosestEdgePoint(Vector3 w)
        {
            var l = transform.InverseTransformPoint(w);
            float hx = size.x * 0.5f, hz = size.y * 0.5f;
            float x = Mathf.Clamp(l.x, -hx, hx), z = Mathf.Clamp(l.z, -hz, hz);
            // distance to each edge; snap to nearest
            float dE = hx - x, dW = x + hx, dN = hz - z, dS = z + hz;
            float m = Mathf.Min(Mathf.Min(dE, dW), Mathf.Min(dN, dS));
            if (m == dE) x = hx; else if (m == dW) x = -hx; else if (m == dN) z = hz; else z = -hz;
            return transform.TransformPoint(new Vector3(x, 0f, z));
        }

        public Vector3 ProjectOnto(Vector3 w)
        {
            var l = transform.InverseTransformPoint(w);
            l.y = 0f;
            return transform.TransformPoint(l);
        }
    }
}
