using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Anti-Air Battery (UnitType.AntiAir): a flak tower with a rotating skyward launcher. Shoots ONLY FlyingThreatUnit
    /// (airships / harpies) with fast pooled lead-tracking flak missiles. Regular towers are untouched.
    /// Attached to the PlacedUnit by BuildingFactory.SpawnUnit via <see cref="Attach"/>. Owner: anti-air agent.
    /// </summary>
    public class AntiAirTower : MonoBehaviour
    {
        public const float MissileSpeed = 0.85f;     // m/s, flyers cruise at ~0.1-0.15 m/s
        public const float FlakSplashRadius = 0.035f;
        public const float FlakSplashFactor = 0.5f;
        const float TurnSpeed = 280f;                // deg/s
        const float FireConeDeg = 14f;

        public PlacedUnit unit;
        public Transform turret;     // yaws
        public Transform barrelPivot; // pitches
        public Transform muzzle;
        float cooldown = 0.4f;
        float recoil;

        // ------------------------------------------------------------------ attach / visuals
        /// <summary>Replaces the unit's default visual with the anti-air look and adds the targeting component.</summary>
        public static AntiAirTower Attach(PlacedUnit u, CivilizationDefinition civ)
        {
            if (u == null) return null;
            if (u.visual != null) { u.visual.gameObject.SetActive(false); Destroy(u.visual.gameObject); }
            var root = BuildVisual(u.transform, civ, out var tur, out var pivot, out var mz);
            root.localScale = Vector3.one * 0.2f; // PlacedUnit pop animation scales it up
            u.visual = root;
            u.muzzle = mz;
            var t = u.GetComponent<AntiAirTower>();
            if (t == null) t = u.gameObject.AddComponent<AntiAirTower>();
            t.unit = u; t.turret = tur; t.barrelPivot = pivot; t.muzzle = mz;
            return t;
        }

        /// <summary>Civ-flavoured procedural anti-air battery. Origin = ground centre, ~0.15 m tall.</summary>
        public static Transform BuildVisual(Transform parent, CivilizationDefinition civ, out Transform turret, out Transform pivot, out Transform muzzle)
        {
            BuildingVisuals.Resolve(civ, out var style, out var p);
            var root = BuildingVisuals.NewRoot(parent, "AntiAirVisual");
            var k = new BuildingVisuals.Kit(root);
            // base + plinth
            k.Cyl(new Vector3(0f, 0f, 0f), 0.034f, 0.012f, p.stone);
            Color body = style == ArchitectureStyle.Timber ? p.dark : p.main;
            k.Cyl(new Vector3(0f, 0.012f, 0f), 0.024f, 0.05f, body);
            k.Cyl(new Vector3(0f, 0.056f, 0f), 0.028f, 0.008f, p.trim);
            // 4 legs / braces
            for (int i = 0; i < 4; i++)
            {
                float a = i * 90f + 45f;
                var v = Quaternion.Euler(0f, a, 0f) * Vector3.forward * 0.026f;
                k.Box(new Vector3(v.x, 0.03f, v.z), new Vector3(0.006f, 0.05f, 0.006f), p.dark, new Vector3(0f, a, 0f));
            }

            var tur = new GameObject("Turret").transform;
            tur.SetParent(root, false);
            tur.localPosition = new Vector3(0f, 0.064f, 0f);
            var piv = new GameObject("BarrelPivot").transform;
            piv.SetParent(tur, false);
            piv.localPosition = new Vector3(0f, 0.014f, 0f);
            piv.localRotation = Quaternion.Euler(-62f, 0f, 0f); // skyward

            var tk = new BuildingVisuals.Kit(tur);
            tk.Cyl(new Vector3(0f, 0f, 0f), 0.02f, 0.014f, style == ArchitectureStyle.StoneTowers ? p.stone : p.roof);
            tk.Box(new Vector3(0f, 0.008f, -0.012f), new Vector3(0.03f, 0.016f, 0.014f), p.dark); // breech block
            tk.Ball(new Vector3(0f, 0.016f, 0f), 0.014f, p.trim);

            // barrels (tall, along local +Z of the pivot)
            var bk = new BuildingVisuals.Kit(piv);
            int barrels = style == ArchitectureStyle.TieredRoofs ? 4 : (style == ArchitectureStyle.Timber ? 2 : 3);
            Color barrelCol = style == ArchitectureStyle.StoneTowers ? new Color(0.3f, 0.3f, 0.34f) : new Color(0.2f, 0.18f, 0.17f);
            for (int i = 0; i < barrels; i++)
            {
                float a = (i / (float)barrels) * Mathf.PI * 2f + Mathf.PI * 0.25f;
                float x = barrels == 2 ? (i == 0 ? -0.008f : 0.008f) : Mathf.Cos(a) * 0.007f;
                float y = barrels == 2 ? 0f : Mathf.Sin(a) * 0.007f;
                bk.CylRot(new Vector3(x, y, 0.03f), 0.0035f, 0.075f, barrelCol, new Vector3(90f, 0f, 0f));
                bk.Cone(new Vector3(x, y, 0.066f), 0.0048f, 0.008f, p.trim).transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            }
            bk.Box(new Vector3(0f, 0f, 0.012f), new Vector3(0.026f, 0.026f, 0.008f), p.dark); // barrel clamp
            bk.Ball(new Vector3(0f, 0f, 0.058f), 0.006f, p.banner);

            var mz = new GameObject("Muzzle").transform;
            mz.SetParent(piv, false);
            mz.localPosition = new Vector3(0f, 0f, 0.075f);

            // civ banner pennant on the plinth
            k.Box(new Vector3(0.03f, 0.075f, 0f), new Vector3(0.0025f, 0.03f, 0.0025f), p.dark);
            k.Box(new Vector3(0.0375f, 0.083f, 0f), new Vector3(0.014f, 0.014f, 0.0015f), p.banner);

            turret = tur; pivot = piv; muzzle = mz;
            return root;
        }

        // ------------------------------------------------------------------ targeting
        void OnDisable() { }

        void Update()
        {
            if (unit == null || unit.health == null || unit.health.IsDead || unit.def == null) return;
            float dt = Time.deltaTime;
            Vector3 me = transform.position;
            float range = unit.def.range;

            // nearest living flyer in range (no allocation)
            FlyingThreatUnit target = null;
            float best = range * range;
            var list = FlyingThreatUnit.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var f = list[i];
                if (f == null || !f.IsAlive) continue;
                float d = (f.AimPoint - me).sqrMagnitude;
                if (d <= best) { best = d; target = f; }
            }

            if (recoil > 0f) recoil = Mathf.Max(0f, recoil - dt * 6f);
            if (muzzle != null && barrelPivot != null)
                barrelPivot.localPosition = new Vector3(0f, 0.014f, -recoil * 0.006f);

            cooldown -= dt;
            if (target == null)
            {
                // idle sweep
                if (turret != null) turret.Rotate(0f, 18f * dt, 0f, Space.Self);
                if (cooldown < 0f) cooldown = 0f;
                return;
            }

            Vector3 origin = muzzle != null ? muzzle.position : me + Vector3.up * 0.1f;
            Vector3 aim = LeadPoint(origin, target);
            AimAt(aim, dt, out float err);
            if (cooldown > 0f || err > FireConeDeg) return;

            FlakMissile.Fire(origin, target, aim, unit.def.EffectiveDamage);
            unit.shots++;
            recoil = 1f;
            cooldown = Mathf.Max(0.2f, unit.def.fireInterval);
        }

        /// <summary>Iterated first-order intercept point for a constant-speed missile.</summary>
        public static Vector3 LeadPoint(Vector3 origin, FlyingThreatUnit t)
        {
            Vector3 pos = t.AimPoint;
            Vector3 vel = t.Velocity;
            Vector3 p = pos;
            for (int i = 0; i < 3; i++)
            {
                float time = Vector3.Distance(origin, p) / MissileSpeed;
                p = pos + vel * time;
            }
            return p;
        }

        void AimAt(Vector3 world, float dt, out float errDeg)
        {
            errDeg = 0f;
            if (turret == null || barrelPivot == null) return;
            Vector3 up = transform.up;
            Vector3 dirW = world - barrelPivot.position;
            Vector3 flat = Vector3.ProjectOnPlane(dirW, up);
            if (flat.sqrMagnitude > 1e-8f)
            {
                var want = Quaternion.LookRotation(flat.normalized, up);
                turret.rotation = Quaternion.RotateTowards(turret.rotation, want, TurnSpeed * dt);
            }
            Vector3 local = turret.InverseTransformDirection(dirW.normalized);
            float pitch = -Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(pitch, -85f, -10f);
            var cur = barrelPivot.localRotation;
            barrelPivot.localRotation = Quaternion.RotateTowards(cur, Quaternion.Euler(pitch, 0f, 0f), TurnSpeed * dt);
            errDeg = Vector3.Angle(muzzle != null ? muzzle.forward : barrelPivot.forward, dirW);
        }

        // ------------------------------------------------------------------ placement ring
        static AntiAirRangeRing ring;

        /// <summary>Call every frame while the placement ghost is shown; ring auto-hides when not refreshed.</summary>
        public static void ShowRangeRing(Vector3 center, Vector3 up, float radius, bool valid)
        {
            if (ring == null)
            {
                var go = new GameObject("AntiAirRangeRing");
                ring = go.AddComponent<AntiAirRangeRing>();
                ring.Build();
            }
            ring.Show(center, up, radius, valid);
        }

        public static void DespawnAllMissiles() => FlakMissile.DespawnAll();
    }

    /// <summary>Thin looped LineRenderer showing the flak range on the placement ghost.</summary>
    public class AntiAirRangeRing : MonoBehaviour
    {
        const int Segs = 48;
        LineRenderer lr;
        readonly Vector3[] pts = new Vector3[Segs];
        int lastFrame = -10;
        bool lastValid, hasMat;

        public void Build()
        {
            lr = gameObject.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = true;
            lr.positionCount = Segs;
            lr.widthMultiplier = 0.004f;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.enabled = false;
        }

        public void Show(Vector3 c, Vector3 up, float r, bool valid)
        {
            if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
            Vector3 a = Vector3.Cross(up, Vector3.right);
            if (a.sqrMagnitude < 1e-4f) a = Vector3.Cross(up, Vector3.forward);
            a.Normalize();
            Vector3 b = Vector3.Cross(up, a);
            for (int i = 0; i < Segs; i++)
            {
                float t = i / (float)Segs * Mathf.PI * 2f;
                pts[i] = c + up * 0.004f + (a * Mathf.Cos(t) + b * Mathf.Sin(t)) * r;
            }
            lr.SetPositions(pts);
            if (!hasMat || valid != lastValid)
            {
                var col = valid ? new Color(0.35f, 0.9f, 1f, 0.9f) : new Color(1f, 0.35f, 0.3f, 0.9f);
                lr.sharedMaterial = Mats.Transparent(col);
                lr.startColor = lr.endColor = col;
                lastValid = valid; hasMat = true;
            }
            lr.enabled = true;
            lastFrame = Time.frameCount;
        }

        void Update()
        {
            if (lr != null && lr.enabled && Time.frameCount - lastFrame > 1) lr.enabled = false;
        }
    }

    /// <summary>Pooled fast flak missile: homes on the (lead-predicted) point, detonates in a small burst, splash hits nearby flyers.</summary>
    public class FlakMissile : MonoBehaviour
    {
        static SimplePool<FlakMissile> pool;
        static Transform poolRoot;
        static readonly List<FlakMissile> live = new List<FlakMissile>();

        FlyingThreatUnit target;
        int serial;
        float damage, life;
        Vector3 vel;
        Vector3 smokeLast;
        float smokeT;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { pool = null; poolRoot = null; live.Clear(); }

        static FlakMissile Create()
        {
            if (poolRoot == null) poolRoot = new GameObject("FlakPool").transform;
            var go = new GameObject("Flak");
            go.transform.SetParent(poolRoot, false);
            var k = new BuildingVisuals.Kit(go.transform);
            k.CylRot(Vector3.zero, 0.0022f, 0.028f, new Color(0.85f, 0.85f, 0.88f), new Vector3(90f, 0f, 0f));
            k.Cone(new Vector3(0f, 0f, 0.014f), 0.0028f, 0.008f, new Color(0.9f, 0.25f, 0.15f)).transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            k.Ball(new Vector3(0f, 0f, -0.016f), 0.009f, new Color(1f, 0.75f, 0.2f)); // exhaust glow
            return go.AddComponent<FlakMissile>();
        }

        public static void Fire(Vector3 origin, FlyingThreatUnit target, Vector3 aim, float damage)
        {
            if (target == null) return;
            if (pool == null || poolRoot == null) pool = new SimplePool<FlakMissile>(Create, 6);
            var m = pool.Get();
            m.target = target;
            m.serial = target.Monster != null ? target.Monster.Serial : -1;
            m.damage = damage;
            m.life = 1.6f;
            Vector3 d = aim - origin;
            m.vel = (d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.up) * AntiAirTower.MissileSpeed;
            m.transform.SetPositionAndRotation(origin, Quaternion.LookRotation(m.vel));
            m.smokeLast = origin; m.smokeT = 0f;
            live.Add(m);
            if (VfxPool.Instance != null) VfxPool.Instance.Burst(origin, new Color(1f, 0.8f, 0.35f));
            ProceduralAudio.Fire(Sfx.Arrow, origin);
        }

        public static void DespawnAll()
        {
            for (int i = live.Count - 1; i >= 0; i--)
                if (live[i] != null && pool != null) pool.Release(live[i]);
            live.Clear();
        }

        bool TargetValid => target != null && target.IsAlive && target.Monster != null && target.Monster.Serial == serial;

        void Update()
        {
            float dt = Time.deltaTime;
            life -= dt;
            Vector3 pos = transform.position;
            if (TargetValid)
            {
                Vector3 aim = AntiAirTower.LeadPoint(pos, target);
                Vector3 want = aim - pos;
                if (want.sqrMagnitude > 1e-8f)
                {
                    // strong homing: steer velocity toward lead point
                    Vector3 desired = want.normalized * AntiAirTower.MissileSpeed;
                    vel = Vector3.RotateTowards(vel, desired, 9f * dt, 0f);
                }
                // proximity detonation (flak fuse)
                if ((target.AimPoint - pos).sqrMagnitude < 0.014f * 0.014f) { Detonate(true); return; }
            }
            else if (life > 0.25f) life = 0.25f; // target gone: self-destruct soon

            Vector3 step = vel * dt;
            // swept check so a fast missile never tunnels through the target
            if (TargetValid)
            {
                Vector3 to = target.AimPoint - pos;
                float proj = Vector3.Dot(to, step.normalized);
                if (proj > 0f && proj < step.magnitude)
                {
                    float perp2 = (to - step.normalized * proj).sqrMagnitude;
                    if (perp2 < 0.014f * 0.014f) { Detonate(true); return; }
                }
            }
            pos += step;
            transform.position = pos;
            if (vel.sqrMagnitude > 1e-8f) transform.rotation = Quaternion.LookRotation(vel);

            smokeT += dt;
            if (smokeT > 0.05f && VfxPool.Instance != null)
            {
                smokeT = 0f;
                VfxPool.Instance.Burst(pos, new Color(0.8f, 0.8f, 0.82f));
            }
            if (life <= 0f) Detonate(false);
        }

        void Detonate(bool hit)
        {
            Vector3 p = transform.position;
            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Burst(p, new Color(1f, 0.6f, 0.15f));
                VfxPool.Instance.Ring(p, AntiAirTower.FlakSplashRadius * 1.4f);
            }
            ProceduralAudio.Fire(Sfx.Hit, p);
            if (hit && TargetValid && target.health != null) target.health.Damage(damage);
            // flak splash on other flyers (allocation-free)
            var list = FlyingThreatUnit.Active;
            float r2 = AntiAirTower.FlakSplashRadius * AntiAirTower.FlakSplashRadius;
            for (int i = 0; i < list.Count; i++)
            {
                var f = list[i];
                if (f == null || f == target || !f.IsAlive || f.health == null) continue;
                if ((f.AimPoint - p).sqrMagnitude <= r2) f.health.Damage(damage * AntiAirTower.FlakSplashFactor);
            }
            live.Remove(this);
            if (pool != null) pool.Release(this);
        }
    }
}
