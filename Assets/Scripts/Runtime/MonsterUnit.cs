using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    public enum MonsterKind { Normal, Ram, Engineer, Harpooner }

    /// <summary>
    /// Pooled procedural monster. Follows a typed SplinePath (floor → around/over furniture → climb / rope / ladder →
    /// hall surface) with separation steering; on the hall surface it stops to attack blocking walls/towers, attacks the
    /// hall in reach, is slowed by barricades and can be pushed off the edge (shockwave). Engineers walk to a ladder site,
    /// build a ladder for 6 s, then attack. The first bare climber onto the hall surface sets a rope. Owner: Agent B.
    /// Spawn with MonsterUnit.Spawn(path, ram) / SpawnEngineer(...) (pooled).
    /// </summary>
    public class MonsterUnit : MonoBehaviour
    {
        public const float NormalHp = 6f, RamHp = 24f, EngineerHp = 8f;
        public const float NormalSpeed = 0.06f;   // on the final (hall) surface
        public const float FloorSpeed = 0.10f;    // floor / furniture tops
        public const float ClimbSpeed = 0.05f;    // bare climb up / down a face
        public const float RopeSpeed = 0.12f;
        public const float LadderSpeed = 0.09f;
        public const float NormalDps = 1f, RamDps = 3f;
        const float SeparationRadius = 0.032f;
        const float PushDuration = 0.2f;

        public static readonly List<MonsterUnit> Active = new List<MonsterUnit>();
        public static event Action<MonsterUnit> Killed;
        /// <summary>Raised once when a monster is shoved/thrown off the supported table outline (WarChronicleTracker).</summary>
        public static event Action<MonsterUnit> FellOffTable;
        public static void ReportFellOff(MonsterUnit m) { FellOffTable?.Invoke(m); }

        static SimplePool<MonsterUnit> pool;
        static Transform poolRoot;
        static int serialCounter;

        public Health health;
        public MonsterKind Kind { get; private set; }
        public bool IsRam => Kind == MonsterKind.Ram;
        public bool IsEngineer => Kind == MonsterKind.Engineer;
        public bool IsHarpooner => Kind == MonsterKind.Harpooner;
        HarpoonerUnit harp;
        public SplinePath Path { get; private set; }
        /// <summary>Ladder this engineer is walking to / building (null otherwise).</summary>
        public ClimbAccess EngineerLadder { get; private set; }
        /// <summary>Changes every Launch — lets projectiles detect pool reuse.</summary>
        public int Serial { get; private set; }
        public bool IsAlive => launched && !dying && health != null && !health.IsDead;
        public float Radius => IsRam ? 0.03f : 0.015f;
        public Vector3 AimPoint => transform.position + transform.up * (IsRam ? 0.025f : 0.017f);

        Transform normalVisual, ramVisual, engineerVisual, harpoonerVisual, bob;
        bool launched, dying;
        float pathDist, attackTimer, bobPhase, fallT, buildT;
        Vector3 pushVel;
        float pushTime;
        bool falling;
        Vector3 fallVel;
        PathSegment lastSeg;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Active.Clear(); Killed = null; FellOffTable = null; pool = null; poolRoot = null; serialCounter = 0; }

        // ------------------------------------------------------------ pool
        static void EnsurePool(int prewarm)
        {
            if (pool == null || poolRoot == null)
            {
                if (poolRoot == null) poolRoot = new GameObject("MonsterPool").transform;
                pool = new SimplePool<MonsterUnit>(Create, prewarm);
            }
        }

        public static MonsterUnit Get()
        {
            EnsurePool(12);
            return pool.Get();
        }

        /// <summary>Builds pooled monsters ahead of time (called when the hall is founded) to avoid a first-wave hitch.</summary>
        public static void Prewarm(int count)
        {
            EnsurePool(0);
            var tmp = new List<MonsterUnit>(count);
            for (int i = pool.FreeCount; i < count; i++) tmp.Add(Create());
            for (int i = 0; i < tmp.Count; i++) pool.Release(tmp[i]);
        }

        public static MonsterUnit Spawn(SplinePath path, bool ram)
        {
            var m = Get();
            m.Launch(path, ram ? MonsterKind.Ram : MonsterKind.Normal);
            return m;
        }

        /// <summary>
        /// Engineer: reserves a ladder site near the hall surface and walks there (obstacle-aware) to build it.
        /// Without a raised hall / free site / ladder slot it attacks like a normal monster.
        /// </summary>
        public static MonsterUnit SpawnEngineer(Vector3 corner, Vector3 hall, ITableSurface hallSurface, float floorY)
        {
            var cr = ClimbRoutes.Instance;
            ClimbAccess ladder = cr != null ? cr.TryReserveLadder(corner, hall, hallSurface, floorY) : null;
            SplinePath path = ladder != null
                ? cr.FloorRoute(corner, ladder.bottom, hallSurface, floorY)
                : SplinePath.BuildRoute(corner, hall, hallSurface, floorY);
            var m = Get();
            m.Launch(path, MonsterKind.Engineer);
            m.EngineerLadder = ladder;
            return m;
        }

        /// <summary>Harpooner: runs to its rope site, fires a harpoon (rope appears instantly), then climbs it. Always works on a raised hall.</summary>
        public static MonsterUnit SpawnHarpooner(Vector3 start, ClimbAccess site, Vector3 hall, ITableSurface hallSurface, float floorY)
        {
            var cr = ClimbRoutes.Instance;
            if (cr == null || site == null) return null;
            SplinePath path = cr.FloorRoute(start, site.shootPos, hallSurface, floorY);
            var m = Get();
            m.Launch(path, MonsterKind.Harpooner);
            m.harp.Begin(site, hall, hallSurface, floorY);
            return m;
        }

        /// <summary>Replaces the current route (harpooner after its shot).</summary>
        internal void AssignPath(SplinePath p)
        {
            if (p == null) return;
            Path = p; pathDist = 0f; pushTime = 0f;
            transform.position = p.EvaluateDistance(0f);
            lastSeg = p.SegmentAt(0f);
        }

        internal void FaceToward(Vector3 dir, float dt) => Face(dir, Vector3.up, dt);
        internal void AnimateRate(float dt, float rate) => Animate(dt, rate);

        /// <summary>A new rope appeared: floor walkers heading for a bare climb on the same surface re-plan onto it.</summary>
        public void RefreshRoute(ClimbAccess rope)
        {
            if (!launched || dying || falling || Path == null || rope == null || Kind == MonsterKind.Engineer || Kind == MonsterKind.Harpooner) return;
            if (!Path.BareClimb || Path.Surface == null || Path.Surface.Id != rope.surfaceId) return;
            if (Segment != PathSegment.Floor) return;
            Replan(transform.position);
        }

        static MonsterUnit Create()
        {
            if (poolRoot == null) poolRoot = new GameObject("MonsterPool").transform;
            var go = new GameObject("Monster");
            go.transform.SetParent(poolRoot, false);
            var m = go.AddComponent<MonsterUnit>();
            m.health = go.AddComponent<Health>();
            m.health.Died += m.OnDied;
            m.bob = BuildingVisuals.NewRoot(go.transform, "Bob");
            m.normalVisual = BuildGoblin(m.bob);
            m.ramVisual = BuildRam(m.bob);
            m.engineerVisual = BuildEngineer(m.bob);
            m.harpoonerVisual = HarpoonerUnit.BuildVisual(m.bob);
            m.harp = go.AddComponent<HarpoonerUnit>();
            go.SetActive(false);
            return m;
        }

        static Transform BuildGoblin(Transform parent)
        {
            var root = BuildingVisuals.NewRoot(parent, "Goblin");
            var k = new BuildingVisuals.Kit(root);
            Color skin = new Color(0.36f, 0.62f, 0.24f), dark = new Color(0.2f, 0.32f, 0.12f);
            k.Ellipsoid(new Vector3(0f, 0.017f, 0f), new Vector3(0.03f, 0.03f, 0.028f), skin);
            k.Ellipsoid(new Vector3(0f, 0.012f, 0.004f), new Vector3(0.022f, 0.016f, 0.024f), new Color(0.6f, 0.75f, 0.4f));
            k.Ball(new Vector3(-0.0065f, 0.024f, 0.012f), 0.009f, Color.white);
            k.Ball(new Vector3(0.0065f, 0.024f, 0.012f), 0.009f, Color.white);
            k.Ball(new Vector3(-0.0065f, 0.024f, 0.0158f), 0.0042f, new Color(0.08f, 0.02f, 0.02f));
            k.Ball(new Vector3(0.0065f, 0.024f, 0.0158f), 0.0042f, new Color(0.08f, 0.02f, 0.02f));
            k.Cone(new Vector3(-0.008f, 0.029f, 0f), 0.003f, 0.01f, dark).transform.localRotation = Quaternion.Euler(0f, 0f, 25f);
            k.Cone(new Vector3(0.008f, 0.029f, 0f), 0.003f, 0.01f, dark).transform.localRotation = Quaternion.Euler(0f, 0f, -25f);
            k.Ellipsoid(new Vector3(-0.007f, 0.002f, 0.002f), new Vector3(0.008f, 0.004f, 0.012f), dark);
            k.Ellipsoid(new Vector3(0.007f, 0.002f, 0.002f), new Vector3(0.008f, 0.004f, 0.012f), dark);
            return root;
        }

        static Transform BuildRam(Transform parent)
        {
            var root = BuildingVisuals.NewRoot(parent, "Ram");
            var k = new BuildingVisuals.Kit(root);
            Color wood = new Color(0.45f, 0.29f, 0.14f), darkWood = new Color(0.28f, 0.17f, 0.08f), iron = new Color(0.3f, 0.3f, 0.33f);
            k.Box(new Vector3(0f, 0.014f, 0f), new Vector3(0.045f, 0.008f, 0.075f), wood);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    k.CylRot(new Vector3(sx * 0.025f, 0.01f, sz * 0.026f), 0.01f, 0.006f, darkWood, new Vector3(0f, 0f, 90f));
            k.GableX(new Vector3(0f, 0.018f, 0f), 0.07f, 0.05f, 0.03f, darkWood).transform.localRotation = Quaternion.identity;
            k.CylRot(new Vector3(0f, 0.026f, 0.012f), 0.007f, 0.1f, wood, new Vector3(90f, 0f, 0f));
            k.Ball(new Vector3(0f, 0.026f, 0.063f), 0.017f, iron);
            k.Ball(new Vector3(-0.012f, 0.05f, -0.03f), 0.016f, new Color(0.36f, 0.62f, 0.24f));
            k.Ball(new Vector3(-0.015f, 0.054f, -0.024f), 0.004f, Color.white);
            k.Ball(new Vector3(-0.008f, 0.054f, -0.024f), 0.004f, Color.white);
            return root;
        }

        static Transform BuildEngineer(Transform parent)
        {
            var root = BuildingVisuals.NewRoot(parent, "Engineer");
            var k = new BuildingVisuals.Kit(root);
            Color skin = new Color(0.42f, 0.55f, 0.22f), apron = new Color(0.45f, 0.3f, 0.16f);
            Color helmet = new Color(0.62f, 0.6f, 0.55f), wood = new Color(0.62f, 0.45f, 0.22f);
            k.Ellipsoid(new Vector3(0f, 0.017f, 0f), new Vector3(0.028f, 0.03f, 0.026f), skin);
            k.Ellipsoid(new Vector3(0f, 0.013f, 0.006f), new Vector3(0.022f, 0.02f, 0.016f), apron);
            k.Ellipsoid(new Vector3(0f, 0.031f, 0f), new Vector3(0.03f, 0.014f, 0.03f), helmet);       // helmet dome
            k.CylRot(new Vector3(0f, 0.027f, 0f), 0.019f, 0.002f, helmet, Vector3.zero);              // helmet brim
            k.Ball(new Vector3(0f, 0.039f, 0f), 0.006f, new Color(0.9f, 0.7f, 0.2f));                // lamp
            k.Ball(new Vector3(-0.006f, 0.023f, 0.012f), 0.008f, Color.white);
            k.Ball(new Vector3(0.006f, 0.023f, 0.012f), 0.008f, Color.white);
            k.Ball(new Vector3(-0.006f, 0.023f, 0.0155f), 0.0038f, new Color(0.08f, 0.02f, 0.02f));
            k.Ball(new Vector3(0.006f, 0.023f, 0.0155f), 0.0038f, new Color(0.08f, 0.02f, 0.02f));
            // ladder poles carried on the back, diagonal
            k.CylRot(new Vector3(-0.008f, 0.03f, -0.016f), 0.0018f, 0.075f, wood, new Vector3(60f, 0f, 0f));
            k.CylRot(new Vector3(0.008f, 0.03f, -0.016f), 0.0018f, 0.075f, wood, new Vector3(60f, 0f, 0f));
            for (int i = -1; i <= 1; i++)
                k.Box(new Vector3(0f, 0.03f + i * 0.016f, -0.016f - i * 0.009f), new Vector3(0.018f, 0.002f, 0.002f), wood);
            return root;
        }

        // ------------------------------------------------------------ lifecycle
        public void Launch(SplinePath path, bool ram) => Launch(path, ram ? MonsterKind.Ram : MonsterKind.Normal);

        public void Launch(SplinePath path, MonsterKind kind)
        {
            Path = path;
            Kind = kind;
            EngineerLadder = null;
            buildT = 0f;
            Serial = ++serialCounter;
            health.Init(kind == MonsterKind.Ram ? RamHp : kind == MonsterKind.Engineer ? EngineerHp : kind == MonsterKind.Harpooner ? HarpoonerUnit.Hp : NormalHp);
            normalVisual.gameObject.SetActive(kind == MonsterKind.Normal);
            ramVisual.gameObject.SetActive(kind == MonsterKind.Ram);
            engineerVisual.gameObject.SetActive(kind == MonsterKind.Engineer);
            harpoonerVisual.gameObject.SetActive(kind == MonsterKind.Harpooner);
            if (harp != null) harp.ResetState();
            pathDist = 0f;
            attackTimer = 0.5f;
            pushTime = 0f;
            falling = false;
            dying = false;
            launched = true;
            bobPhase = UnityEngine.Random.value * 10f;
            bob.localPosition = Vector3.zero;
            bob.localRotation = Quaternion.identity;
            Vector3 start = path != null ? path.EvaluateDistance(0f) : transform.position;
            transform.position = start;
            lastSeg = path != null ? path.SegmentAt(0f) : PathSegment.Surface;
            Vector3 up = lastSeg == PathSegment.Surface ? SurfaceUp() : Vector3.up;
            Vector3 fwd = path != null ? path.TangentAtDistance(0.01f) : transform.forward;
            fwd -= Vector3.Dot(fwd, up) * up;
            if (fwd.sqrMagnitude > 1e-8f) transform.rotation = Quaternion.LookRotation(fwd, up);
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            if (!Active.Contains(this)) Active.Add(this);
            ResourceOutpostNode.TryDivertMonster(this);
        }

        Vector3 SurfaceUp() => Path != null && Path.Surface != null ? Path.Surface.Pose.rotation * Vector3.up : Vector3.up;

        void OnDied(Health h)
        {
            if (dying || !launched) return;
            Die();
        }

        void Die()
        {
            dying = true;
            if (EngineerLadder != null && !EngineerLadder.complete && ClimbRoutes.Existing != null)
                ClimbRoutes.Existing.Abandon(EngineerLadder); // unfinished ladder collapses with its builder
            EngineerLadder = null;
            if (harp != null) harp.OnOwnerDied();
            if (VfxPool.Instance != null) VfxPool.Instance.Burst(AimPoint, IsRam ? new Color(0.5f, 0.35f, 0.2f) : new Color(0.4f, 0.8f, 0.3f));
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Death);
            Killed?.Invoke(this);
            // actual removal is deferred to LateUpdate so callers iterating Active stay safe
        }

        void LateUpdate()
        {
            if (dying) ReturnToPool();
        }

        void ReturnToPool()
        {
            launched = false;
            dying = false;
            falling = false;
            EngineerLadder = null;
            Active.Remove(this);
            if (pool != null) pool.Release(this); else gameObject.SetActive(false);
        }

        /// <summary>Removes all monsters without score events and clears ropes/ladders (restart / new empire / defeat).</summary>
        public static void DespawnAll()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var m = Active[i];
                if (m == null) continue;
                m.launched = false;
                m.dying = false;
                m.falling = false;
                m.EngineerLadder = null;
                if (m.harp != null) m.harp.ResetState();
                if (pool != null) pool.Release(m); else m.gameObject.SetActive(false);
            }
            Active.Clear();
            if (ClimbRoutes.Existing != null) ClimbRoutes.Existing.ClearAll();
            if (HandOfGodController.Existing != null) HandOfGodController.Existing.ResetState();
            GoblinKingBoss.DespawnAll();
            FlyingThreatUnit.DespawnAll();
            SiegeCatapultUnit.DespawnAll();
        }

        /// <summary>Current route segment type.</summary>
        public PathSegment Segment => Path != null ? Path.SegmentAt(pathDist) : PathSegment.Surface;
        public bool OnFinalSurface => Segment == PathSegment.Surface;
        static bool IsVertical(PathSegment s) => s == PathSegment.Climb || s == PathSegment.Descend || s == PathSegment.Rope || s == PathSegment.Ladder;

        /// <summary>Called by ClimbRoutes when a rope/ladder is destroyed: knock off and re-plan if it was on/heading to it.</summary>
        public void OnAccessLost(ClimbAccess a)
        {
            if (!launched || dying || a == null) return;
            if (EngineerLadder == a)
            {
                EngineerLadder = null;
                Replan(transform.position);
                return;
            }
            if (Path == null || Path.Access != a) return;
            var seg = Segment;
            if (seg == PathSegment.Surface) return; // already past it
            Vector3 from = seg == PathSegment.Rope || seg == PathSegment.Ladder ? a.approach : transform.position;
            Replan(from);
        }

        void Replan(Vector3 from)
        {
            var cr = ClimbRoutes.Existing;
            var np = cr != null ? cr.Reroute(from) : null;
            if (np == null) return;
            Path = np;
            pathDist = 0f;
            pushTime = 0f;
            transform.position = np.EvaluateDistance(0f);
            lastSeg = np.SegmentAt(0f);
        }

        /// <summary>
        /// Displace (shockwave). On the hall surface: pushed off the supported outline → falls and dies.
        /// On the floor / furniture top: displaced horizontally. On a climb/rope/ladder: knocked back to the bottom.
        /// </summary>
        public void Push(Vector3 worldDir, float meters)
        {
            if (!IsAlive || falling || Path == null) return;
            var seg = Segment;
            if (IsVertical(seg))
            {
                pathDist = Mathf.Max(0f, Path.RunStart(pathDist) - 0.01f);
                transform.position = Path.EvaluateDistance(pathDist);
                return;
            }
            Vector3 up = seg == PathSegment.Surface ? SurfaceUp() : Vector3.up;
            worldDir -= Vector3.Dot(worldDir, up) * up;
            if (worldDir.sqrMagnitude < 1e-8f) return;
            pushVel = worldDir.normalized * (meters / PushDuration);
            pushTime = PushDuration;
        }

        // ------------------------------------------------------------ update
        void Update()
        {
            if (!launched || dying) return;
            if (HandOfGodController.IsMonsterControlled(this)) return;
            float dt = Time.deltaTime;
            Vector3 pos = transform.position;

            if (falling)
            {
                fallVel += Physics.gravity * dt;
                transform.position = pos + fallVel * dt;
                transform.Rotate(360f * dt, 0f, 200f * dt, Space.Self);
                fallT += dt;
                if (fallT > 0.6f) Die();
                return;
            }
            if (Path == null) return;

            PathSegment seg = Path.SegmentAt(pathDist);
            bool onSurface = seg == PathSegment.Surface;
            bool vertical = IsVertical(seg);
            ITableSurface surface = onSurface ? Path.Surface : null;
            Vector3 up = onSurface ? SurfaceUp() : vertical ? Path.UpAt(pathDist) : Vector3.up;

            // first bare climber onto the hall surface sets a rope for the others
            if (onSurface && lastSeg == PathSegment.Climb && Path.BareClimb && Path.Surface != null && ClimbRoutes.Existing != null)
                ClimbRoutes.Existing.AddRope(Path.Surface.Id, Path.BareClimbTop, Path.BareClimbBottom, Path.ClimbOutward, Path.BareClimbApproach);
            lastSeg = seg;

            // harpooner: shoot the rope at the climb edge first (instant rope), then climb it
            if (Kind == MonsterKind.Harpooner && harp != null && harp.IsActive)
            {
                bool atSite = pathDist >= Path.Length - 0.004f;
                if (harp.Tick(this, dt, atSite)) return;
            }

            // engineer at its ladder site: build for 6 s, then climb it and attack
            if (EngineerLadder != null && pathDist >= Path.Length - 0.003f)
            {
                var cr = ClimbRoutes.Existing;
                if (cr == null) { EngineerLadder = null; return; }
                if (buildT <= 0f) cr.AnnounceLadderStarted(EngineerLadder);
                buildT += dt;
                cr.SetLadderProgress(EngineerLadder, buildT / ClimbRoutes.LadderBuildSeconds);
                Face(EngineerLadder.top - pos, Vector3.up, dt);
                Animate(dt, 1.5f);
                if (buildT >= ClimbRoutes.LadderBuildSeconds)
                {
                    var ladder = EngineerLadder;
                    cr.CompleteLadder(ladder);
                    EngineerLadder = null;
                    var hallT = TownHall.Current;
                    var gd = GameDirector.Instance;
                    ITableSurface hs = Path.Surface != null ? Path.Surface : (gd != null ? gd.Surface : null);
                    if (hallT != null && hs != null)
                    {
                        float floorY = RoomCornerSpawns.Existing != null ? RoomCornerSpawns.Existing.FloorY : ladder.bottom.y;
                        Path = cr.RouteViaAccess(ladder, hallT.transform.position, hs, floorY);
                        pathDist = 0f;
                        lastSeg = PathSegment.Floor;
                    }
                }
                return;
            }

            if (pushTime > 0f)
            {
                float step = Mathf.Min(dt, pushTime);
                pushTime -= step;
                pos += pushVel * step;
                if (surface != null)
                {
                    Vector3 proj = surface.ProjectOnto(pos);
                    if (!surface.IsPointSupported(proj))
                    {
                        falling = true;
                        FellOffTable?.Invoke(this);
                        fallT = 0f;
                        fallVel = pushVel * 0.6f;
                        transform.position = pos;
                        return;
                    }
                    pos = proj;
                }
                transform.position = pos;
                if (pushTime <= 0f) pathDist = Path.ProjectDistance(pos, Path.RunStart(pathDist), Path.RunEnd(pathDist));
                Animate(dt, 2f);
                return;
            }

            float speedMul = GoblinKingBoss.GetSpeedMultiplier(this);
            PlacedUnit blocker = null;
            bool atHall = false;
            TownHall hall = TownHall.Current;
            float dps = IsRam ? RamDps : NormalDps;

            if (onSurface)
            {
                // --- slow from barricades, blocking/attack targets from walls/towers
                var units = PlacedUnit.All;
                float pad = Radius * 0.8f;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || u.health == null || u.health.IsDead) continue;
                    if (u.type == UnitType.Barricade)
                    {
                        if (u.InSlowZone(pos)) speedMul = Mathf.Min(speedMul, u.def.slow);
                    }
                    else if (blocker == null && u.Contacts(pos, pad))
                    {
                        blocker = u;
                    }
                }

                if (hall != null && hall.health != null && !hall.health.IsDead)
                {
                    Vector3 d = hall.transform.position - pos;
                    d -= Vector3.Dot(d, up) * up;
                    atHall = d.magnitude <= hall.AttackReach + Radius * 0.5f;
                }

                if (atHall || blocker != null)
                {
                    Vector3 tp = atHall ? hall.transform.position : blocker.transform.position;
                    Face(tp - pos, up, dt);
                    attackTimer -= dt;
                    if (attackTimer <= 0f)
                    {
                        attackTimer = 1f;
                        if (atHall) hall.health.Damage(dps);
                        else blocker.health.Damage(dps);
                        if (ProceduralAudio.Instance != null && !atHall) ProceduralAudio.Instance.Play(Sfx.Hit);
                    }
                    Animate(dt, 0.4f);
                    return;
                }
            }
            attackTimer = Mathf.Min(attackTimer, 0.5f);

            // --- path following with separation
            float baseSpeed;
            switch (seg)
            {
                case PathSegment.Floor:
                case PathSegment.Top: baseSpeed = FloorSpeed; break;
                case PathSegment.Climb:
                case PathSegment.Descend: baseSpeed = ClimbSpeed; break;
                case PathSegment.Rope: baseSpeed = RopeSpeed; break;
                case PathSegment.Ladder: baseSpeed = LadderSpeed; break;
                default:
                    baseSpeed = Path.SurfaceIsFloor && Path.Length - pathDist > 0.6f ? FloorSpeed : NormalSpeed;
                    break;
            }
            float speed = baseSpeed * (IsRam ? 0.5f : 1f) * speedMul;
            if (Kind == MonsterKind.Harpooner && seg == PathSegment.Floor && harp != null && harp.IsActive) speed *= HarpoonerUnit.RunSpeedMul;
            float look = seg == PathSegment.Floor || seg == PathSegment.Top ? 0.06f : 0.02f;
            // never look ahead across a segment boundary (keeps corners and climbs crisp)
            float lookD = Mathf.Min(pathDist + look, Path.RunEnd(pathDist));
            if (lookD <= pathDist + 1e-4f) lookD = pathDist + 0.005f;
            Vector3 target = Path.EvaluateDistance(lookD);
            Vector3 tan = Path.TangentAtDistance(pathDist);
            Vector3 seek = target - pos;
            if (!vertical) seek -= Vector3.Dot(seek, up) * up;
            Vector3 dir = seek.sqrMagnitude > 1e-8f ? seek.normalized : tan;

            Vector3 sep = Vector3.zero;
            var act = Active;
            float sepScale = seg == PathSegment.Floor ? 1.6f : 1f;
            for (int i = 0; i < act.Count; i++)
            {
                var o = act[i];
                if (o == this || o.dying) continue;
                Vector3 diff = pos - o.transform.position;
                float minD = (SeparationRadius + (o.IsRam || IsRam ? 0.02f : 0f)) * sepScale;
                float sq = diff.sqrMagnitude;
                if (sq < minD * minD && sq > 1e-10f)
                {
                    float dd = Mathf.Sqrt(sq);
                    sep += diff / dd * (1f - dd / minD);
                }
            }
            sep -= Vector3.Dot(sep, up) * up;
            if (vertical) sep -= Vector3.Dot(sep, tan) * tan; // spread sideways along the face / rope / ladder only
            if (seg == PathSegment.Rope || seg == PathSegment.Top) sep *= 0.3f; // single-file on ropes and narrow tops

            Vector3 vel = dir * speed + sep * (baseSpeed * 1.5f);
            Vector3 next = pos + vel * dt;
            if (surface != null)
            {
                Vector3 proj = surface.ProjectOnto(next);
                // separation must never shove a monster off the surface
                if (!surface.IsPointSupported(proj)) proj = surface.ProjectOnto(pos + dir * speed * dt);
                next = proj;
            }
            transform.position = next;
            float adv = Mathf.Max(0f, Vector3.Dot(next - pos, tan));
            if (vertical) adv = Mathf.Max(adv, speed * dt * 0.5f); // never stall on a wall
            pathDist = Mathf.Min(Path.Length, pathDist + adv);
            if (!vertical && !onSurface)
            {
                // floor / furniture top: hold the path height exactly
                Vector3 p2 = transform.position;
                p2.y = Path.EvaluateDistance(pathDist).y;
                transform.position = p2;
            }

            if (seg == PathSegment.Climb || seg == PathSegment.Rope) Face(Vector3.up, up, dt, true);
            else if (seg == PathSegment.Descend) Face(Vector3.down, up, dt, true);
            else if (seg == PathSegment.Ladder) Face(tan, up, dt, true);
            else Face(dir, up, dt);
            Animate(dt, speedMul);
        }

        void Face(Vector3 dir, Vector3 up, float dt, bool raw = false)
        {
            if (!raw) dir -= Vector3.Dot(dir, up) * up;
            if (dir.sqrMagnitude < 1e-8f) return;
            Quaternion want = Quaternion.LookRotation(dir.normalized, up);
            transform.rotation = Quaternion.Slerp(transform.rotation, want, Mathf.Min(1f, dt * 10f));
        }

        void Animate(float dt, float rate)
        {
            bobPhase += dt * 14f * Mathf.Max(0.3f, rate);
            if (IsRam)
            {
                bob.localRotation = Quaternion.Euler(Mathf.Sin(bobPhase * 0.5f) * 3f, 0f, 0f);
            }
            else
            {
                bob.localPosition = new Vector3(0f, Mathf.Abs(Mathf.Sin(bobPhase)) * 0.005f, 0f);
                bob.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(bobPhase) * 8f);
            }
        }
    }
}
