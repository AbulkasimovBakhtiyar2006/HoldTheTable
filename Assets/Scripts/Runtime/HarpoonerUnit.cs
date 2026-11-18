using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Behaviour + procedural look of the goblin Harpooner (a MonsterUnit of Kind Harpooner, so towers/defenders/shockwave
    /// target it like any monster). Spawns before the wave, runs to its reserved rope site, aims ~0.45 s, fires a
    /// visible harpoon with a rope trail (~0.5 s flight); on impact ClimbRoutes.DeployRope makes the rope appear
    /// instantly. If it dies first, ClimbRoutes deploys the rope after a short delay. Afterwards it climbs the rope and
    /// fights like a goblin. Lives on the pooled monster GameObject. Owner: Agent B.
    /// </summary>
    public class HarpoonerUnit : MonoBehaviour
    {
        public enum Phase { Idle, Walk, Aim, Flight, Done }

        public const float Hp = 7f;
        public const float AimTime = 0.45f;
        public const float FlightTime = 0.5f;
        public const float RunSpeedMul = 2.2f;
        public const float FallbackDelay = 1.0f;

        public Phase phase = Phase.Idle;
        public ClimbAccess Site { get; private set; }
        public bool IsActive => phase != Phase.Idle && phase != Phase.Done && Site != null;

        Vector3 hall, muzzle, target;
        ITableSurface surface;
        float floorY, t, linger;
        Transform harpoon;
        LineRenderer line;
        static Material ropeMat;

        // ------------------------------------------------------------ look
        public static Transform BuildVisual(Transform parent)
        {
            var root = BuildingVisuals.NewRoot(parent, "Harpooner");
            root.localScale = Vector3.one * 1.2f;
            var k = new BuildingVisuals.Kit(root);
            Color skin = new Color(0.34f, 0.58f, 0.26f), dark = new Color(0.2f, 0.3f, 0.12f);
            Color coat = new Color(0.16f, 0.3f, 0.42f), leather = new Color(0.4f, 0.26f, 0.12f), iron = new Color(0.55f, 0.57f, 0.6f);
            k.Ellipsoid(new Vector3(0f, 0.017f, 0f), new Vector3(0.03f, 0.03f, 0.028f), skin);
            k.Ellipsoid(new Vector3(0f, 0.012f, 0.004f), new Vector3(0.024f, 0.018f, 0.024f), coat);       // sailor coat
            k.Ball(new Vector3(-0.0065f, 0.024f, 0.012f), 0.009f, Color.white);
            k.Ball(new Vector3(0.0065f, 0.024f, 0.012f), 0.009f, Color.white);
            k.Ball(new Vector3(-0.0065f, 0.024f, 0.0158f), 0.0042f, new Color(0.08f, 0.02f, 0.02f));
            k.Ball(new Vector3(0.0065f, 0.024f, 0.0158f), 0.0042f, new Color(0.08f, 0.02f, 0.02f));
            k.Cone(new Vector3(-0.008f, 0.029f, 0f), 0.003f, 0.01f, dark).transform.localRotation = Quaternion.Euler(0f, 0f, 25f);
            k.Cone(new Vector3(0.008f, 0.029f, 0f), 0.003f, 0.01f, dark).transform.localRotation = Quaternion.Euler(0f, 0f, -25f);
            k.CylRot(new Vector3(0f, 0.03f, 0f), 0.017f, 0.003f, leather, Vector3.zero);                      // hat brim
            k.Cyl(new Vector3(0f, 0.031f, 0f), 0.011f, 0.011f, leather);                                      // hat crown
            k.Ellipsoid(new Vector3(-0.007f, 0.002f, 0.002f), new Vector3(0.008f, 0.004f, 0.012f), dark);
            k.Ellipsoid(new Vector3(0.007f, 0.002f, 0.002f), new Vector3(0.008f, 0.004f, 0.012f), dark);
            // harpoon gun (shoulder launcher) + coiled rope on the back
            k.CylRot(new Vector3(0.011f, 0.02f, 0.016f), 0.0032f, 0.034f, leather, new Vector3(90f, 0f, 0f));
            k.Cone(new Vector3(0.011f, 0.02f, 0.033f), 0.0034f, 0.01f, iron).transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            k.CylRot(new Vector3(0f, 0.018f, -0.014f), 0.009f, 0.006f, new Color(0.62f, 0.5f, 0.3f), new Vector3(90f, 0f, 0f));
            return root;
        }

        void EnsureHarpoon()
        {
            if (harpoon != null) return;
            var go = new GameObject("Harpoon");
            go.transform.SetParent(transform, false);
            var k = new BuildingVisuals.Kit(go.transform);
            Color iron = new Color(0.7f, 0.72f, 0.76f), wood = new Color(0.5f, 0.34f, 0.16f);
            k.CylRot(new Vector3(0f, 0f, -0.008f), 0.0016f, 0.024f, wood, new Vector3(90f, 0f, 0f));
            var tip = k.Cone(new Vector3(0f, 0f, 0.004f), 0.0045f, 0.012f, iron);
            tip.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            harpoon = go.transform;
            var lgo = new GameObject("HarpoonRope");
            lgo.transform.SetParent(transform, false);
            line = lgo.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 3;
            line.startWidth = 0.0035f; line.endWidth = 0.0035f;
            if (ropeMat == null) ropeMat = Mats.Unlit(new Color(0.62f, 0.46f, 0.25f));
            line.sharedMaterial = ropeMat;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            HideFx();
        }

        void HideFx()
        {
            if (harpoon != null) harpoon.gameObject.SetActive(false);
            if (line != null) line.enabled = false;
            linger = 0f;
        }

        // ------------------------------------------------------------ lifecycle
        public void ResetState()
        {
            phase = Phase.Idle;
            Site = null;
            HideFx();
        }

        public void Begin(ClimbAccess site, Vector3 hallPos, ITableSurface surf, float floor)
        {
            EnsureHarpoon();
            HideFx();
            Site = site; hall = hallPos; surface = surf; floorY = floor;
            phase = site != null ? Phase.Walk : Phase.Idle;
            t = 0f;
        }

        /// <summary>Owner died: if the rope was not yet fired, ClimbRoutes deploys it after a short delay.</summary>
        public void OnOwnerDied()
        {
            if (IsActive && ClimbRoutes.Existing != null) ClimbRoutes.Existing.ScheduleFallbackRope(Site, FallbackDelay);
            phase = Phase.Done;
            // let a harpoon in flight vanish with its owner (the fallback rope appears regardless)
            if (harpoon != null) harpoon.gameObject.SetActive(false);
        }

        void Update()
        {
            if (linger > 0f && line != null)
            {
                linger -= Time.deltaTime;
                if (linger <= 0f) HideFx();
            }
        }

        /// <summary>Per-frame driver called by MonsterUnit while active. Returns true when it consumed the frame.</summary>
        public bool Tick(MonsterUnit m, float dt, bool atSite)
        {
            var cr = ClimbRoutes.Existing != null ? ClimbRoutes.Existing : ClimbRoutes.Instance;
            if (Site == null || cr == null) { phase = Phase.Done; return false; }
            // rope already exists (fallback fired / another harpoon): go climb it right away
            if (Site.complete && phase != Phase.Flight) { Finish(m, cr, Site); return true; }
            switch (phase)
            {
                case Phase.Walk:
                    if (!atSite) return false;
                    phase = Phase.Aim; t = 0f;
                    return true;
                case Phase.Aim:
                    t += dt;
                    m.FaceToward(Site.top - m.transform.position, dt);
                    m.AnimateRate(dt, 0.6f);
                    if (t >= AimTime) Fire(m);
                    return true;
                case Phase.Flight:
                    t += dt;
                    float u = Mathf.Clamp01(t / FlightTime);
                    Vector3 p = Vector3.Lerp(muzzle, target, u) + Vector3.up * (Mathf.Sin(u * Mathf.PI) * 0.05f);
                    Vector3 prev = harpoon.position;
                    harpoon.position = p;
                    Vector3 v = p - prev;
                    if (v.sqrMagnitude > 1e-10f) harpoon.rotation = Quaternion.LookRotation(v.normalized);
                    Vector3 mid = Vector3.Lerp(muzzle, p, 0.5f) + Vector3.down * 0.012f * (1f - u * 0.5f);
                    line.SetPosition(0, muzzle); line.SetPosition(1, mid); line.SetPosition(2, p);
                    m.FaceToward(Site.top - m.transform.position, dt);
                    m.AnimateRate(dt, 0.3f);
                    if (u >= 1f)
                    {
                        var live = cr.DeployRope(Site);
                        if (VfxPool.Instance != null) VfxPool.Instance.Burst(target, new Color(0.8f, 0.8f, 0.85f));
                        ProceduralAudio.Fire(Sfx.Hit, target);
                        linger = 0.35f;
                        harpoon.gameObject.SetActive(false);
                        Finish(m, cr, live != null ? live : Site);
                    }
                    return true;
            }
            return false;
        }

        void Fire(MonsterUnit m)
        {
            muzzle = m.AimPoint + m.transform.forward * 0.02f;
            target = Site.top;
            harpoon.gameObject.SetActive(true);
            harpoon.position = muzzle;
            line.enabled = true;
            line.SetPosition(0, muzzle); line.SetPosition(1, muzzle); line.SetPosition(2, muzzle);
            ProceduralAudio.Fire(Sfx.Arrow, muzzle);
            if (VfxPool.Instance != null) VfxPool.Instance.Burst(muzzle, new Color(1f, 0.9f, 0.6f));
            phase = Phase.Flight; t = 0f;
        }

        void Finish(MonsterUnit m, ClimbRoutes cr, ClimbAccess rope)
        {
            phase = Phase.Done;
            var hallT = TownHall.Current;
            Vector3 hp = hallT != null ? hallT.transform.position : hall;
            ITableSurface s = surface != null ? surface : (m.Path != null ? m.Path.Surface : null);
            if (s != null && rope != null && rope.complete)
                m.AssignPath(cr.RouteViaAccess(rope, hp, s, floorY));
        }
    }
}
