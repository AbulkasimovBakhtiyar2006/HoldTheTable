using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>Pooled arrow that flies on a shallow arc to a monster and damages it on arrival. Owner: Agent B.</summary>
    public class Projectile : MonoBehaviour
    {
        const float Speed = 0.9f;      // m/s
        const float ArcHeight = 0.04f;

        static SimplePool<Projectile> pool;
        static Transform poolRoot;
        static readonly List<Projectile> live = new List<Projectile>();

        MonsterUnit target;
        int targetSerial;
        Vector3 from, lastTargetPos;
        float damage, t, duration;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { pool = null; poolRoot = null; live.Clear(); }

        static Projectile Create()
        {
            if (poolRoot == null) poolRoot = new GameObject("ArrowPool").transform;
            var go = new GameObject("Arrow");
            go.transform.SetParent(poolRoot, false);
            var k = new BuildingVisuals.Kit(go.transform);
            k.CylRot(new Vector3(0f, 0f, 0f), 0.0012f, 0.035f, new Color(0.45f, 0.3f, 0.15f), new Vector3(90f, 0f, 0f));
            k.Cone(new Vector3(0f, 0f, 0.0175f), 0.0025f, 0.008f, new Color(0.25f, 0.25f, 0.28f)).transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            k.Box(new Vector3(0f, 0f, -0.016f), new Vector3(0.006f, 0.0008f, 0.007f), new Color(0.95f, 0.95f, 0.9f));
            return go.AddComponent<Projectile>();
        }

        public static void Fire(Vector3 origin, MonsterUnit target, float damage)
        {
            if (target == null) return;
            ProceduralAudio.Fire(Sfx.Arrow, origin);
            if (pool == null || poolRoot == null) pool = new SimplePool<Projectile>(Create, 8);
            var p = pool.Get();
            p.from = origin;
            p.target = target;
            p.targetSerial = target.Serial;
            p.lastTargetPos = target.AimPoint;
            p.damage = damage;
            p.t = 0f;
            p.duration = Mathf.Max(0.08f, Vector3.Distance(origin, p.lastTargetPos) / Speed);
            p.transform.position = origin;
            live.Add(p);
        }

        public static void DespawnAll()
        {
            for (int i = live.Count - 1; i >= 0; i--)
            {
                var p = live[i];
                if (p != null && pool != null) pool.Release(p);
            }
            live.Clear();
        }

        void Update()
        {
            bool targetValid = target != null && target.Serial == targetSerial && target.IsAlive;
            if (targetValid) lastTargetPos = target.AimPoint;
            t += Time.deltaTime / duration;
            float u = Mathf.Clamp01(t);
            Vector3 pos = Vector3.Lerp(from, lastTargetPos, u) + Vector3.up * (Mathf.Sin(u * Mathf.PI) * ArcHeight);
            Vector3 dir = pos - transform.position;
            if (dir.sqrMagnitude > 1e-10f) transform.rotation = Quaternion.LookRotation(dir);
            transform.position = pos;
            if (u < 1f) return;

            if (targetValid)
            {
                target.health.Damage(damage);
                if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Hit);
            }
            live.Remove(this);
            pool.Release(this);
        }
    }
}
