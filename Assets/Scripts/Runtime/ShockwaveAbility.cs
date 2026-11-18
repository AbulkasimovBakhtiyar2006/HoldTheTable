using System;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Two-palm outward sweep: both palms start close (&lt; 0.35 m apart) and the horizontal separation grows by
    /// at least 0.25 m within 0.5 s while each palm moves away from the shared midpoint. 2 charges (+ upgrades), 1 s cooldown,
    /// damages and pushes every monster within 0.6 m of the impact point on the settlement surface.
    /// </summary>
    public class ShockwaveAbility : MonoBehaviour
    {
        public static ShockwaveAbility Instance { get; private set; }

        public const int BaseCharges = 2;
        public float window = 0.5f;
        public float maxStartSeparation = 0.35f;
        public float minSeparationGain = 0.25f;
        public float minPerHandOutward = 0.06f;
        public float cooldown = 1f;
        public float radius = 0.6f;
        public float damage = 4f;
        public float push = 0.12f;
        public float hallSnapDistance = 0.6f;

        public event Action Fired;

        struct Sample { public float t; public Vector3 l, r; }
        const int Cap = 64;
        readonly Sample[] buf = new Sample[Cap];
        int head, count;
        int used;
        float lastFire = -10f;
        bool subscribed;

        public int Charges
        {
            get
            {
                int extra = UpgradeState.Instance != null ? UpgradeState.Instance.ExtraCharges : 0;
                return Mathf.Max(0, BaseCharges + extra - used);
            }
        }

        public float CooldownRemaining => Mathf.Max(0f, cooldown - (Time.time - lastFire));

        void Awake() { Instance = this; }

        void Start() { TrySubscribe(); }

        void TrySubscribe()
        {
            if (subscribed || GameDirector.Instance == null) return;
            GameDirector.Instance.GameReset += ResetCharges;
            subscribed = true;
        }

        void OnDestroy()
        {
            if (subscribed && GameDirector.Instance != null) GameDirector.Instance.GameReset -= ResetCharges;
            if (Instance == this) Instance = null;
        }

        public void ResetCharges()
        {
            used = 0;
            count = 0;
            lastFire = -10f;
        }

        void Update()
        {
            if (!subscribed) TrySubscribe();
            var rig = HandRig.Instance;
            var gd = GameDirector.Instance;
            if (rig == null || gd == null) return;

            if (!rig.TryGetPalm(HandSide.Left, out var pl) || !rig.TryGetPalm(HandSide.Right, out var pr))
            {
                count = 0; // tracking lost: never stitch a gesture across the gap
                return;
            }

            float now = Time.time;
            buf[head] = new Sample { t = now, l = pl.position, r = pr.position };
            head = (head + 1) % Cap;
            if (count < Cap) count++;

            if (gd.Phase != GamePhase.Wave && gd.Phase != GamePhase.BuildPhase) return;
            if (Charges <= 0 || now - lastFire < cooldown) return;

            float curSep = HorizontalDistance(pl.position, pr.position);
            Vector3 curMid = (pl.position + pr.position) * 0.5f;
            for (int k = 1; k < count; k++)
            {
                int idx = (head - 1 - k + Cap * 2) % Cap;
                var s = buf[idx];
                if (now - s.t > window) break;
                float startSep = HorizontalDistance(s.l, s.r);
                if (startSep >= maxStartSeparation) continue;
                if (curSep - startSep < minSeparationGain) continue;

                // each palm must move outward (horizontally) relative to the starting midpoint
                Vector3 mid0 = (s.l + s.r) * 0.5f;
                Vector3 outL = Flat(s.l - mid0).normalized;
                Vector3 outR = Flat(s.r - mid0).normalized;
                if (outL.sqrMagnitude < 0.5f) outL = -Flat(pr.position - pl.position).normalized;
                if (outR.sqrMagnitude < 0.5f) outR = Flat(pr.position - pl.position).normalized;
                float dl = Vector3.Dot(Flat(pl.position - s.l), outL);
                float dr = Vector3.Dot(Flat(pr.position - s.r), outR);
                if (dl < minPerHandOutward || dr < minPerHandOutward) continue;

                Fire(curMid);
                break;
            }
        }

        void Fire(Vector3 palmMid)
        {
            var gd = GameDirector.Instance;
            used++;
            lastFire = Time.time;
            count = 0;

            Vector3 center = palmMid;
            if (gd.Surface != null) center = gd.Surface.ProjectOnto(palmMid);
            if (gd.Hall != null)
            {
                Vector3 hallPos = gd.Hall.transform.position;
                if (HorizontalDistance(center, hallPos) > hallSnapDistance) center = hallPos;
            }

            float r2 = radius * radius;
            var list = MonsterUnit.Active;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (i >= list.Count) continue;
                var m = list[i];
                if (m == null) continue;
                Vector3 d = m.transform.position - center;
                d.y = 0f;
                if (d.sqrMagnitude > r2) continue;
                Vector3 dir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
                m.Push(dir, push);
                if (m.health != null && !m.health.IsDead) m.health.Damage(damage);
            }

            if (ClimbRoutes.Existing != null) ClimbRoutes.Existing.OnShockwave(center, radius);
            if (VfxPool.Instance != null) VfxPool.Instance.Ring(center, radius);
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Shockwave, center);
            Fired?.Invoke();
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
