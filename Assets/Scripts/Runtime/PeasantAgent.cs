using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    public enum PeasantCargo { None, WoodLogs, GoldOre }
    public enum PeasantState { WalkingToNode, HarvestingAtNode, CarryingToHall, PanickingToHall, HiddenInHall }

    /// <summary>
    /// Tiny procedural peasant worker that walks back and forth between placed Farms/Mines and the TownHall
    /// during calm phases (BuildPhase / Upgrade / Victory), carrying wood logs or gold ore sacks.
    /// When a combat wave begins, peasants let out a visual/audio panic cue (!) and scurry inside the TownHall
    /// to hide until the wave ends.
    /// Owner: @living-civ (Feature 4).
    /// </summary>
    public class PeasantAgent : MonoBehaviour
    {
        public const float WalkSpeed = 0.056f;
        public const float CarrySpeed = 0.046f;
        public const float FleeSpeed = 0.118f;
        public const float HarvestDuration = 0.9f;
        public const int PeasantsPerBuilding = 2;
        public const int MaxPeasants = 12;

        public static readonly List<PeasantAgent> Active = new List<PeasantAgent>();

        static SimplePool<PeasantAgent> pool;
        static Transform poolRoot;
        static float managerSyncTimer;
        static float lastPanicSfxTime = -10f;

        public PlacedUnit AssignedBuilding { get; private set; }
        public PeasantState State { get; private set; }
        public PeasantCargo Cargo { get; private set; }
        public int WorkerSlot { get; private set; }

        Transform visualRoot;
        Transform bobRoot;
        Transform woodBundleRoot;
        Transform goldSackRoot;
        Transform panicCueRoot;
        CivUnitStyle.Rig rig;
        static CivUnitStyle.Rig lastBuiltRig;
        float workClock;

        ArchitectureStyle lastStyle;
        Color lastBannerColor;
        bool visualBuilt;
        bool activeInSession;
        float stateTimer;
        float bobPhase;
        float emergeScale;
        float panicTimer;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active.Clear();
            pool = null;
            poolRoot = null;
            managerSyncTimer = 0f;
            lastPanicSfxTime = -10f;
        }

        static void EnsurePool(int prewarm = 6)
        {
            if (pool == null || poolRoot == null)
            {
                if (poolRoot == null) poolRoot = new GameObject("PeasantPool").transform;
                pool = new SimplePool<PeasantAgent>(Create, prewarm);
            }
        }

        static PeasantAgent Create()
        {
            if (poolRoot == null) poolRoot = new GameObject("PeasantPool").transform;
            var go = new GameObject("PeasantAgent");
            go.transform.SetParent(poolRoot, false);
            var p = go.AddComponent<PeasantAgent>();
            p.bobRoot = BuildingVisuals.NewRoot(go.transform, "Bob");
            go.SetActive(false);
            return p;
        }

        /// <summary>Spawns a peasant assigned to haul wood logs (Farm) or gold ore (Mine) to the TownHall.</summary>
        public static PeasantAgent Spawn(PlacedUnit economyBuilding, int workerSlot = 0)
        {
            if (economyBuilding == null) return null;
            LivingSettlementCoordinator.EnsureInstance();
            EnsurePool(6);
            var p = pool.Get();
            p.Launch(economyBuilding, workerSlot);
            return p;
        }

        /// <summary>Despawns all active peasants immediately (used on Restart / NewEmpire / Defeat).</summary>
        public static void DespawnAll()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var p = Active[i];
                if (p == null) continue;
                p.activeInSession = false;
                p.AssignedBuilding = null;
                if (pool != null) pool.Release(p);
                else p.gameObject.SetActive(false);
            }
            Active.Clear();
        }

        /// <summary>Triggers the wave-start panic cue on all active peasants so they scurry inside the TownHall.</summary>
        public static void TriggerWavePanic()
        {
            bool playedAudio = false;
            for (int i = 0; i < Active.Count; i++)
            {
                var p = Active[i];
                if (p == null || !p.activeInSession) continue;
                p.BeginPanic(!playedAudio);
                playedAudio = true;
            }
        }

        /// <summary>Notifies all peasants of a GameDirector phase transition.</summary>
        public static void NotifyPhaseChanged(GamePhase phase)
        {
            if (phase == GamePhase.Wave)
            {
                TriggerWavePanic();
            }
            else if (phase == GamePhase.BuildPhase || phase == GamePhase.Upgrade || phase == GamePhase.Victory)
            {
                for (int i = 0; i < Active.Count; i++)
                {
                    var p = Active[i];
                    if (p != null && p.activeInSession && (p.State == PeasantState.HiddenInHall || p.State == PeasantState.PanickingToHall))
                        p.EmergeFromHall();
                }
            }
        }

        /// <summary>
        /// Periodic manager tick (called automatically by LivingSettlementCoordinator or optionally from GameDirector.Update).
        /// Spawns peasants for placed Farms/Mines and retires peasants whose building was destroyed.
        /// </summary>
        public static void TickManager(float dt)
        {
            var gd = GameDirector.Instance;
            var hall = TownHall.Current;
            if (hall == null || hall.health == null || hall.health.IsDead)
            {
                if (Active.Count > 0) DespawnAll();
                return;
            }

            if (gd != null && (gd.Phase == GamePhase.CivilizationSetup || gd.Phase == GamePhase.HeadquartersPlacement || gd.Phase == GamePhase.Defeat))
            {
                if (Active.Count > 0) DespawnAll();
                return;
            }

            managerSyncTimer -= dt;
            if (managerSyncTimer > 0f) return;
            managerSyncTimer = 0.4f;

            // Retire peasants whose assigned Farm/Mine was destroyed
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var p = Active[i];
                if (p == null || !p.activeInSession || p.AssignedBuilding == null ||
                    p.AssignedBuilding.health == null || p.AssignedBuilding.health.IsDead)
                {
                    if (p != null) p.ReturnToPool();
                    else Active.RemoveAt(i);
                }
            }

            // Do not spawn new peasants mid-wave; wait for calm phases
            if (gd != null && gd.Phase == GamePhase.Wave) return;

            var units = PlacedUnit.All;
            for (int i = 0; i < units.Count; i++)
            {
                if (Active.Count >= MaxPeasants) break;
                var u = units[i];
                if (u == null || u.health == null || u.health.IsDead) continue;
                if (u.type != UnitType.Farm && u.type != UnitType.Mine) continue;

                int assigned = CountPeasantsFor(u);
                for (int slot = assigned; slot < PeasantsPerBuilding && Active.Count < MaxPeasants; slot++)
                {
                    Spawn(u, slot);
                }
            }
        }

        static int CountPeasantsFor(PlacedUnit u)
        {
            int c = 0;
            for (int i = 0; i < Active.Count; i++)
            {
                var p = Active[i];
                if (p != null && p.activeInSession && p.AssignedBuilding == u) c++;
            }
            return c;
        }

        void Launch(PlacedUnit building, int slot)
        {
            AssignedBuilding = building;
            WorkerSlot = slot;
            activeInSession = true;
            bobPhase = Random.value * 10f;
            emergeScale = 0.25f;
            stateTimer = slot * 0.45f;
            panicTimer = 0f;

            RebuildVisualIfNeeded();
            SetCargo(PeasantCargo.None);
            SetPanicVisible(false);
            if (rig != null)
            {
                rig.SetTool(building.type == UnitType.Mine ? CivTool.Pickaxe : (slot % 2 == 0 ? CivTool.Hoe : CivTool.Hammer));
                rig.ShowTool(true);
                rig.Idle();
            }

            var hall = TownHall.Current;
            Vector3 startPos = hall != null ? GetHallDoorPosition(hall, slot) : building.transform.position;
            if (CurrentSurface() != null) startPos = CurrentSurface().ProjectOnto(startPos);
            transform.position = startPos;

            Vector3 up = SurfaceUp();
            Vector3 toNode = building.transform.position - startPos;
            toNode -= Vector3.Dot(toNode, up) * up;
            if (toNode.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(toNode.normalized, up);

            State = PeasantState.WalkingToNode;
            if (visualRoot != null)
            {
                visualRoot.gameObject.SetActive(true);
                visualRoot.localScale = Vector3.one * emergeScale;
            }
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            if (!Active.Contains(this)) Active.Add(this);
        }

        void RebuildVisualIfNeeded()
        {
            BuildingVisuals.Resolve(null, out var style, out var pal);
            if (visualBuilt && visualRoot != null && style == lastStyle && ColorsClose(pal.banner, lastBannerColor))
                return;

            if (visualRoot != null) Destroy(visualRoot.gameObject);
            lastStyle = style;
            lastBannerColor = pal.banner;
            visualRoot = BuildPeasantVisual(bobRoot, style, pal, out woodBundleRoot, out goldSackRoot, out panicCueRoot);
            rig = lastBuiltRig;
            visualBuilt = true;
        }

        static bool ColorsClose(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.02f;
        }

        /// <summary>Builds the procedural peasant mesh along with toggleable wood logs, gold ore sack, and (!) panic cue.</summary>
        public static Transform BuildPeasantVisual(
            Transform parent,
            ArchitectureStyle style,
            BuildingVisuals.Palette pal,
            out Transform woodBundle,
            out Transform goldSack,
            out Transform panicCue)
        {
            var root = BuildingVisuals.NewRoot(parent, "PeasantVisual");
            var rig = CivUnitStyle.BuildWorker(root, style, pal);
            lastBuiltRig = rig;
            Color straw = new Color(0.88f, 0.76f, 0.40f);
            Color wood = new Color(0.46f, 0.30f, 0.15f);
            Color gold = new Color(1.0f, 0.82f, 0.20f);
            Color burlap = new Color(0.68f, 0.54f, 0.34f);
            Color cloth = Color.Lerp(pal.ground, pal.main, 0.45f);

            // 1. Wood Logs bundle (for Farm peasants)
            var woodRoot = BuildingVisuals.NewRoot(root, "WoodBundle");
            var wk = new BuildingVisuals.Kit(woodRoot);
            wk.CylRot(new Vector3(0f, 0.014f, 0.0085f), 0.0024f, 0.018f, wood, new Vector3(0f, 0f, 90f));
            wk.CylRot(new Vector3(0f, 0.014f, 0.0125f), 0.0024f, 0.018f, wood * 0.85f, new Vector3(0f, 0f, 90f));
            wk.CylRot(new Vector3(0f, 0.0175f, 0.0105f), 0.0023f, 0.017f, wood, new Vector3(0f, 0f, 90f));
            wk.Box(new Vector3(0f, 0.0155f, 0.0105f), new Vector3(0.004f, 0.0065f, 0.0065f), straw);
            woodRoot.localPosition = new Vector3(0f, 0.0045f, 0.0015f);
            woodBundle = woodRoot;

            // 2. Gold Ore Sack (for Mine peasants)
            var goldRoot = BuildingVisuals.NewRoot(root, "GoldSack");
            var gk = new BuildingVisuals.Kit(goldRoot);
            gk.Ellipsoid(new Vector3(0f, 0.014f, 0.0095f), new Vector3(0.012f, 0.011f, 0.011f), burlap);
            gk.Ball(new Vector3(-0.0025f, 0.0195f, 0.0095f), 0.0052f, gold);
            gk.Ball(new Vector3(0.0025f, 0.019f, 0.0105f), 0.0048f, gold);
            gk.Ball(new Vector3(0f, 0.0205f, 0.008f), 0.0044f, new Color(1f, 0.9f, 0.35f));
            goldRoot.localPosition = new Vector3(0f, 0.0045f, 0.0015f);
            goldSack = goldRoot;

            // 3. Panic Visual Cue: bright floating "!" icon + raised panicked arms
            var panicRoot = BuildingVisuals.NewRoot(root, "PanicCue");
            var pk = new BuildingVisuals.Kit(panicRoot);
            Color alertColor = new Color(1f, 0.25f, 0.12f);
            Color alertRim = new Color(1f, 0.92f, 0.2f);
            // Floating exclamation mark "!" above head
            pk.Box(new Vector3(0f, 0.054f, 0f), new Vector3(0.0036f, 0.011f, 0.0036f), alertRim);
            pk.Box(new Vector3(0f, 0.054f, 0f), new Vector3(0.0026f, 0.0095f, 0.0042f), alertColor);
            pk.Ball(new Vector3(0f, 0.045f, 0f), 0.0042f, alertRim);
            panicCue = panicRoot;

            return root;
        }

        void SetCargo(PeasantCargo cargo)
        {
            Cargo = cargo;
            if (rig != null) rig.ShowTool(cargo == PeasantCargo.None);
            if (woodBundleRoot != null) woodBundleRoot.gameObject.SetActive(cargo == PeasantCargo.WoodLogs);
            if (goldSackRoot != null) goldSackRoot.gameObject.SetActive(cargo == PeasantCargo.GoldOre);
        }

        void SetPanicVisible(bool visible)
        {
            if (panicCueRoot != null) panicCueRoot.gameObject.SetActive(visible);
        }

        public void BeginPanic(bool playAudio)
        {
            if (State == PeasantState.HiddenInHall || State == PeasantState.PanickingToHall) return;
            State = PeasantState.PanickingToHall;
            panicTimer = 0f;
            SetCargo(PeasantCargo.None);
            SetPanicVisible(true);

            if (VfxPool.Instance != null)
                VfxPool.Instance.Burst(transform.position + transform.up * 0.025f, new Color(1f, 0.85f, 0.25f));

            if (playAudio && Time.unscaledTime - lastPanicSfxTime > 0.35f)
            {
                lastPanicSfxTime = Time.unscaledTime;
                ProceduralAudio.Fire(Sfx.Climb, transform.position);
            }
        }

        public void EmergeFromHall()
        {
            var hall = TownHall.Current;
            if (hall != null)
            {
                Vector3 doorPos = GetHallDoorPosition(hall, WorkerSlot);
                if (CurrentSurface() != null) doorPos = CurrentSurface().ProjectOnto(doorPos);
                transform.position = doorPos;
            }
            SetCargo(PeasantCargo.None);
            SetPanicVisible(false);
            State = PeasantState.WalkingToNode;
            emergeScale = 0.2f;
            if (visualRoot != null)
            {
                visualRoot.gameObject.SetActive(true);
                visualRoot.localScale = Vector3.one * emergeScale;
            }
        }

        void ReturnToPool()
        {
            activeInSession = false;
            AssignedBuilding = null;
            Active.Remove(this);
            if (pool != null) pool.Release(this);
            else gameObject.SetActive(false);
        }

        ITableSurface CurrentSurface()
        {
            if (GameDirector.Instance != null && GameDirector.Instance.Surface != null)
                return GameDirector.Instance.Surface;
            return SettlementBuildArea.Instance != null ? SettlementBuildArea.Instance.Surface : null;
        }

        Vector3 SurfaceUp()
        {
            var s = CurrentSurface();
            return s != null ? s.Pose.up : Vector3.up;
        }

        static Vector3 GetHallDoorPosition(TownHall hall, int slot)
        {
            float angle = ((slot % 4) - 1.5f) * 24f * Mathf.Deg2Rad;
            Vector3 fwd = hall.transform.forward;
            Vector3 right = hall.transform.right;
            Vector3 dir = (fwd * Mathf.Cos(angle) + right * Mathf.Sin(angle)).normalized;
            return hall.transform.position + dir * (hall.FootprintRadius * 0.72f);
        }

        Vector3 GetBuildingWorkPoint(PlacedUnit b)
        {
            float angle = (WorkerSlot * 140f + 20f) * Mathf.Deg2Rad;
            Vector3 offset = (b.transform.forward * Mathf.Cos(angle) + b.transform.right * Mathf.Sin(angle)) * 0.032f;
            return b.transform.position + offset;
        }

        void Update()
        {
            if (!activeInSession) return;

            var hall = TownHall.Current;
            if (hall == null || hall.health == null || hall.health.IsDead ||
                AssignedBuilding == null || AssignedBuilding.health == null || AssignedBuilding.health.IsDead)
            {
                ReturnToPool();
                return;
            }

            var gd = GameDirector.Instance;
            if (gd != null && gd.Phase == GamePhase.Wave && State != PeasantState.PanickingToHall && State != PeasantState.HiddenInHall)
            {
                BeginPanic(true);
            }

            if (State == PeasantState.HiddenInHall)
            {
                if (gd != null && gd.Phase != GamePhase.Wave)
                    EmergeFromHall();
                return;
            }

            float dt = Time.deltaTime;
            Vector3 up = SurfaceUp();
            ITableSurface surface = CurrentSurface();

            // Smoothly scale up when emerging from TownHall door
            if (visualRoot != null && emergeScale < 1f && State != PeasantState.PanickingToHall)
            {
                emergeScale = Mathf.Min(1f, emergeScale + dt * 3.2f);
                visualRoot.localScale = Vector3.one * emergeScale;
            }

            switch (State)
            {
                case PeasantState.WalkingToNode:
                {
                    if (stateTimer > 0f) { stateTimer -= dt; return; }
                    Vector3 workPt = GetBuildingWorkPoint(AssignedBuilding);
                    if (MoveToward(workPt, WalkSpeed, surface, up, dt, 1f))
                    {
                        State = PeasantState.HarvestingAtNode;
                        stateTimer = HarvestDuration;
                        workClock = 0f;
                    }
                    break;
                }

                case PeasantState.HarvestingAtNode:
                {
                    Face(AssignedBuilding.transform.position - transform.position, up, dt);
                    workClock += dt;
                    if (rig != null) rig.Work(workClock, 0.45f);
                    if (bobRoot != null)
                    {
                        float sw = Mathf.Sin(workClock * (Mathf.PI * 2f / 0.45f));
                        bobRoot.localPosition = Vector3.zero;
                        bobRoot.localRotation = Quaternion.Euler(8f + sw * 7f, 0f, 0f);
                    }
                    stateTimer -= dt;
                    if (stateTimer <= 0f)
                    {
                        SetCargo(AssignedBuilding.type == UnitType.Farm ? PeasantCargo.WoodLogs : PeasantCargo.GoldOre);
                        State = PeasantState.CarryingToHall;
                    }
                    break;
                }

                case PeasantState.CarryingToHall:
                {
                    Vector3 doorPt = GetHallDoorPosition(hall, WorkerSlot);
                    if (MoveToward(doorPt, CarrySpeed, surface, up, dt, 0.85f))
                    {
                        // Delivered cargo to TownHall
                        SetCargo(PeasantCargo.None);
                        State = PeasantState.WalkingToNode;
                        stateTimer = 0.25f;
                    }
                    break;
                }

                case PeasantState.PanickingToHall:
                {
                    panicTimer += dt;
                    if (panicCueRoot != null)
                    {
                        float bounce = 1f + Mathf.Sin(panicTimer * 28f) * 0.18f;
                        panicCueRoot.localScale = new Vector3(bounce, bounce, bounce);
                    }

                    Vector3 hallCenter = hall.transform.position;
                    Vector3 toHall = hallCenter - transform.position;
                    toHall -= Vector3.Dot(toHall, up) * up;
                    float dist = toHall.magnitude;
                    float hideRadius = hall.FootprintRadius * 0.58f;

                    // Shrink into the TownHall doorway as they approach
                    if (visualRoot != null && dist < hall.FootprintRadius * 1.1f)
                    {
                        float s = Mathf.Clamp01((dist - hideRadius * 0.5f) / (hall.FootprintRadius * 0.8f));
                        visualRoot.localScale = Vector3.one * Mathf.Max(0.2f, s);
                    }

                    if (dist <= hideRadius || MoveToward(hallCenter, FleeSpeed, surface, up, dt, 2.2f))
                    {
                        State = PeasantState.HiddenInHall;
                        SetPanicVisible(false);
                        if (visualRoot != null) visualRoot.gameObject.SetActive(false);
                    }
                    break;
                }
            }
        }

        bool MoveToward(Vector3 target, float speed, ITableSurface surface, Vector3 up, float dt, float animRate)
        {
            Vector3 pos = transform.position;
            Vector3 diff = target - pos;
            diff -= Vector3.Dot(diff, up) * up;
            float dist = diff.magnitude;
            if (dist <= 0.012f) return true;

            Vector3 dir = diff / dist;

            // Gentle separation from other peasants
            Vector3 sep = Vector3.zero;
            const float sepRad = 0.022f;
            for (int i = 0; i < Active.Count; i++)
            {
                var o = Active[i];
                if (o == null || o == this || !o.activeInSession || o.State == PeasantState.HiddenInHall) continue;
                Vector3 d = pos - o.transform.position;
                d -= Vector3.Dot(d, up) * up;
                float sq = d.sqrMagnitude;
                if (sq < sepRad * sepRad && sq > 1e-10f)
                {
                    float m = Mathf.Sqrt(sq);
                    sep += (d / m) * (1f - m / sepRad);
                }
            }

            Vector3 vel = dir * speed + sep * (speed * 0.8f);
            Vector3 next = pos + vel * dt;
            if (surface != null)
            {
                Vector3 proj = surface.ProjectOnto(next);
                if (surface.IsPointSupported(proj)) next = proj;
                else next = surface.ProjectOnto(pos + dir * speed * dt);
            }

            transform.position = next;
            Face(dir, up, dt);

            bobPhase += dt * 16f * animRate;
            bool panicking = State == PeasantState.PanickingToHall;
            if (rig != null)
            {
                if (panicking) rig.Panic(bobPhase * 1.4f);
                else rig.Walk(bobPhase * 0.75f, 1f, Cargo != PeasantCargo.None);
            }
            if (bobRoot != null)
            {
                bobRoot.localPosition = new Vector3(0f, Mathf.Abs(Mathf.Sin(bobPhase * 0.75f)) * 0.0020f, 0f);
                bobRoot.localRotation = Quaternion.Euler(Cargo != PeasantCargo.None ? -6f : 2f, 0f, Mathf.Sin(bobPhase * 0.75f) * (panicking ? 9f : 3.5f));
            }

            return dist <= speed * dt + 0.012f;
        }

        void Face(Vector3 dir, Vector3 up, float dt)
        {
            dir -= Vector3.Dot(dir, up) * up;
            if (dir.sqrMagnitude < 1e-8f) return;
            Quaternion want = Quaternion.LookRotation(dir.normalized, up);
            transform.rotation = Quaternion.Slerp(transform.rotation, want, Mathf.Min(1f, dt * 12f));
        }
    }
}
