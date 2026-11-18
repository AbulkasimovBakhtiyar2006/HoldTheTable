using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>Pooled lightweight VFX: expanding rings (LineRenderer) and cube-debris bursts. No shadows, no ParticleSystem.</summary>
    public class VfxPool : MonoBehaviour
    {
        public static VfxPool Instance { get; private set; }

        public int ringPoolSize = 6;
        public int shardPoolSize = 96;
        public int shardsPerBurst = 8;
        public Color ringColor = new Color(0.55f, 0.85f, 1f, 1f);
        public float ringDuration = 0.5f;
        public float shardLife = 0.6f;

        const int RingSegments = 48;

        class RingFx { public LineRenderer lr; public Vector3 center; public float radius, t; public bool active; }
        class Shard { public Transform tr; public MeshRenderer mr; public Vector3 vel, spin; public float t; public bool active; }

        readonly List<RingFx> rings = new List<RingFx>();
        readonly List<Shard> shards = new List<Shard>();
        readonly Vector3[] ringPts = new Vector3[RingSegments];
        int nextShard;
        uint seed = 1234567u;

        void Awake()
        {
            Instance = this;
            var ringMat = Mats.Unlit(ringColor);
            for (int i = 0; i < ringPoolSize; i++)
            {
                var go = new GameObject("VfxRing_" + i);
                go.transform.SetParent(transform, false);
                var lr = go.AddComponent<LineRenderer>();
                lr.useWorldSpace = true;
                lr.loop = true;
                lr.positionCount = RingSegments;
                lr.sharedMaterial = ringMat;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.numCornerVertices = 0;
                lr.alignment = LineAlignment.TransformZ;
                go.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward); // lie flat (normal = up)
                lr.enabled = false;
                rings.Add(new RingFx { lr = lr });
            }

            var cubeMesh = BuildCubeMesh();
            for (int i = 0; i < shardPoolSize; i++)
            {
                var go = new GameObject("VfxShard_" + i);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = cubeMesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.sharedMaterial = Mats.Unlit(Color.white);
                go.SetActive(false);
                shards.Add(new Shard { tr = go.transform, mr = mr });
            }
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        public void Ring(Vector3 center, float radius)
        {
            RingFx r = null;
            for (int i = 0; i < rings.Count; i++) if (!rings[i].active) { r = rings[i]; break; }
            if (r == null) r = rings[0];
            r.center = center + Vector3.up * 0.004f;
            r.radius = radius;
            r.t = 0f;
            r.active = true;
            r.lr.enabled = true;
            UpdateRing(r);
        }

        public void Burst(Vector3 pos, Color color)
        {
            var mat = Mats.Unlit(color);
            for (int i = 0; i < shardsPerBurst; i++)
            {
                var s = shards[nextShard];
                nextShard = (nextShard + 1) % shards.Count;
                s.active = true;
                s.t = 0f;
                s.tr.position = pos;
                s.tr.localScale = Vector3.one * 0.012f;
                s.tr.rotation = Quaternion.Euler(Rand() * 360f, Rand() * 360f, 0f);
                float a = (i / (float)shardsPerBurst + Rand() * 0.1f) * Mathf.PI * 2f;
                s.vel = new Vector3(Mathf.Cos(a), 1.2f + Rand(), Mathf.Sin(a)) * (0.35f + 0.25f * Rand());
                s.spin = new Vector3(Rand() * 720f, Rand() * 720f, 0f);
                s.mr.sharedMaterial = mat;
                s.tr.gameObject.SetActive(true);
            }
        }

        void Update()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < rings.Count; i++)
            {
                var r = rings[i];
                if (!r.active) continue;
                r.t += dt;
                if (r.t >= ringDuration) { r.active = false; r.lr.enabled = false; continue; }
                UpdateRing(r);
            }
            for (int i = 0; i < shards.Count; i++)
            {
                var s = shards[i];
                if (!s.active) continue;
                s.t += dt;
                if (s.t >= shardLife) { s.active = false; s.tr.gameObject.SetActive(false); continue; }
                s.vel += Physics.gravity * (dt * 0.5f);
                s.tr.position += s.vel * dt;
                s.tr.Rotate(s.spin * dt, Space.Self);
                s.tr.localScale = Vector3.one * (0.012f * (1f - s.t / shardLife));
            }
        }

        void UpdateRing(RingFx r)
        {
            float u = r.t / ringDuration;
            float ease = 1f - (1f - u) * (1f - u);
            float rad = Mathf.Lerp(0.15f, 1f, ease) * r.radius;
            for (int k = 0; k < RingSegments; k++)
            {
                float a = k / (float)RingSegments * Mathf.PI * 2f;
                ringPts[k] = r.center + new Vector3(Mathf.Cos(a) * rad, 0f, Mathf.Sin(a) * rad);
            }
            r.lr.SetPositions(ringPts);
            float w = Mathf.Lerp(0.03f, 0.002f, u);
            r.lr.startWidth = w;
            r.lr.endWidth = w;
        }

        float Rand()
        {
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            return (seed & 0xFFFFFF) / (float)0xFFFFFF;
        }

        static Mesh BuildCubeMesh()
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(tmp);
            return mesh;
        }
    }
}
