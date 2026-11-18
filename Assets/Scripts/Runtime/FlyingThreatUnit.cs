using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    public enum FlyingThreatKind { GoblinAirship, Harpy }

    /// <summary>
    /// Feature 5A: 3D Room-Space Aerial Threats (Goblin Airships &amp; Harpies).
    /// Wraps a pooled <see cref="MonsterUnit"/> (with <c>Path == null</c> so ground spline logic yields to 3D flight)
    /// while remaining in <see cref="MonsterUnit.Active"/> so ArcherTowers, Focus Fire, Defenders, Shockwave,
    /// ScoreKeeper, and WaveSpawner track and target it automatically.
    /// Flies through the 3D air space of the room directly toward the TownHall, bypassing floor splines,
    /// table edges, and walls. Supports physical Quest 3 hand-tracking swat &amp; two-hand mid-air clap mechanics.
    /// </summary>
    public class FlyingThreatUnit : MonoBehaviour
    {
        public const float AirshipHp = 10f;
        public const float HarpyHp = 6f;
        public const float AirshipSpeed = 0.095f;
        public const float HarpySpeed = 0.145f;
        public const float AirshipDps = 2.0f;
        public const float HarpyDps = 1.4f;
        public const float AttackInterval = 1.25f;

        public const float DefaultCruiseAltitude = 0.24f;
        public const float AirshipHoverAltitude = 0.12f;
        public const float HarpyHoverAltitude = 0.085f;
        public const float AttackHorizontalReach = 0.095f;

        // Hand-tracking swat / clap tuning
        public const float SwatRadius = 0.11f;
        public const float DirectTouchRadius = 0.065f;
        public const float MinSwatSpeed = 0.35f;
        public const float MinTouchSpeed = 0.15f;
        public const float ClapMaxSeparation = 0.14f;
        public const float ClapRadius = 0.18f;
        public const float MinClapClosingSpeed = 0.28f;
        public const float CrashDuration = 0.48f;
        public const float CrashSplashRadius = 0.09f;
        public const float CrashSplashDamage = 5f;

        public static readonly List<FlyingThreatUnit> Active = new List<FlyingThreatUnit>();
        public static event Action<FlyingThreatUnit> Swatted;
        public static event Action<FlyingThreatUnit> Killed;

        // Static per-frame hand velocity tracker for swat & clap detection
        static int lastHandTrackFrame = -1;
        static int lastWaveSpawnFrame = -1;
        static readonly bool[] handValid = new bool[2];
        static readonly Vector3[] prevPalmPos = new Vector3[2];
        static readonly Vector3[] curPalmPos = new Vector3[2];
        static readonly Vector3[] curTipPos = new Vector3[2];
        static readonly Vector3[] palmVelocity = new Vector3[2];
        static float prevHandSeparation = 1f;
        static float handClosingSpeed;

        public MonsterUnit Monster { get; private set; }
        public Health health => Monster != null ? Monster.health : null;
        public FlyingThreatKind ThreatKind { get; private set; }
        public bool IsAirship => ThreatKind == FlyingThreatKind.GoblinAirship;
        public bool IsHarpy => ThreatKind == FlyingThreatKind.Harpy;
        public bool IsSwatted => isSwatted;
        public bool IsAlive => launched && !isSwatted && Monster != null && Monster.IsAlive;
        public Vector3 AimPoint => transform.position + Vector3.up * (IsAirship ? 0.025f : 0.018f);

        Transform airVisualRoot;
        Transform airshipVisual;
        Transform harpyVisual;
        Transform propellerPivot;
        Transform leftWingPivot;
        Transform rightWingPivot;
        Transform bombVisual;

        ITableSurface targetSurface;
        Vector3 startAirPos;
        Vector3 hallBasePos;
        float cruiseAltitude;
        float hoverAltitude;
        float flightPhase;
        float orbitAngle;
        float attackTimer;
        float pushTimer;
        Vector3 pushVel;

        bool launched;
        bool isSwatted;
        float crashTimer;
        float crashSmokeTimer;
        Vector3 crashVel;
        Vector3 crashSpin;
        bool shockwaveHooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active.Clear();
            Swatted = null;
            Killed = null;
            lastHandTrackFrame = -1;
            lastWaveSpawnFrame = -1;
            handValid[0] = false;
            handValid[1] = false;
        }

        // ------------------------------------------------------------ spawning API

        /// <summary>
        /// Spawns wave-appropriate 3D aerial threats (Harpies and Goblin Airships) from the 3D airspace
        /// above room portals or around the settlement perimeter. Idempotent within the same frame.
        /// </summary>
        public static void SpawnWaveThreats(int waveIndex, Vector3 hallPos, ITableSurface surface)
        {
            if (Time.frameCount == lastWaveSpawnFrame) return;
            lastWaveSpawnFrame = Time.frameCount;

            int count = waveIndex <= 0 ? 1 : waveIndex == 1 ? 2 : Mathf.Min(5, 2 + (waveIndex - 1));
            var rcs = RoomCornerSpawns.Existing;
            bool hasRoom = rcs != null && rcs.HasRoom && rcs.Corners != null && rcs.Corners.Count > 0;

            for (int i = 0; i < count; i++)
            {
                FlyingThreatKind kind = (waveIndex == 0 && i == 0)
                    ? FlyingThreatKind.Harpy
                    : (i % 2 == 0 ? FlyingThreatKind.GoblinAirship : FlyingThreatKind.Harpy);

                Vector3 spawnPos;
                if (hasRoom)
                {
                    Vector3 portal = rcs.Corners[i % rcs.Corners.Count];
                    Vector3 toHall = hallPos - portal;
                    toHall.y = 0f;
                    Vector3 side = Vector3.Cross(Vector3.up, toHall.sqrMagnitude > 1e-4f ? toHall.normalized : Vector3.forward);
                    float lateral = (i - (count - 1) * 0.5f) * 0.22f;
                    float staggerBack = i * 0.25f;
                    Vector3 dirBack = toHall.sqrMagnitude > 1e-4f ? -toHall.normalized : Vector3.back;
                    spawnPos = portal + side * lateral + dirBack * staggerBack;
                    spawnPos.y = Mathf.Max(hallPos.y, rcs.FloorY + 0.35f) + DefaultCruiseAltitude + (i % 2) * 0.06f;
                }
                else
                {
                    float baseAngle = (i * (360f / Mathf.Max(1, count)) + waveIndex * 47f + 25f) * Mathf.Deg2Rad;
                    float dist = 0.62f + i * 0.14f;
                    Vector3 offset = new Vector3(Mathf.Cos(baseAngle) * dist, DefaultCruiseAltitude + (i % 2) * 0.05f, Mathf.Sin(baseAngle) * dist);
                    spawnPos = hallPos + offset;
                }

                Spawn(spawnPos, hallPos, surface, kind);
            }
        }

        /// <summary>Convenience hook matching Action&lt;int&gt; (e.g. GameDirector.WaveStarted).</summary>
        public static void SpawnForWave(int waveIndex)
        {
            var gd = GameDirector.Instance;
            var hall = TownHall.Current != null ? TownHall.Current : (gd != null ? gd.Hall : null);
            if (hall == null) return;
            ITableSurface surf = gd != null ? gd.Surface : null;
            SpawnWaveThreats(waveIndex, hall.transform.position, surf);
        }

        /// <summary>
        /// Spawns a single FlyingThreatUnit (GoblinAirship or Harpy) backed by a pooled MonsterUnit.
        /// </summary>
        public static FlyingThreatUnit Spawn(Vector3 spawnPos, Vector3 hallPos, ITableSurface surface, FlyingThreatKind kind = FlyingThreatKind.GoblinAirship)
        {
            var m = MonsterUnit.Get();
            if (m == null) return null;

            // Launch with Path == null so MonsterUnit registers in MonsterUnit.Active & initializes Health/Serial
            // while yielding movement updates to FlyingThreatUnit.
            m.Launch(null, kind == FlyingThreatKind.GoblinAirship ? MonsterKind.Ram : MonsterKind.Normal);

            var ft = m.GetComponent<FlyingThreatUnit>();
            if (ft == null) ft = m.gameObject.AddComponent<FlyingThreatUnit>();
            ft.enabled = true;
            ft.Init(m, spawnPos, hallPos, surface, kind);
            return ft;
        }

        public static void DespawnAll()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var ft = Active[i];
                if (ft == null) continue;
                ft.CleanupAndDisable();
            }
            Active.Clear();
        }

        // ------------------------------------------------------------ setup & visuals

        void Init(MonsterUnit monster, Vector3 spawnPos, Vector3 hallPos, ITableSurface surface, FlyingThreatKind kind)
        {
            Monster = monster;
            ThreatKind = kind;
            targetSurface = surface;
            hallBasePos = hallPos;

            hoverAltitude = kind == FlyingThreatKind.GoblinAirship ? AirshipHoverAltitude : HarpyHoverAltitude;
            cruiseAltitude = Mathf.Max(hoverAltitude + 0.08f, spawnPos.y - hallPos.y);
            if (spawnPos.y < hallPos.y + hoverAltitude)
                spawnPos.y = hallPos.y + DefaultCruiseAltitude;

            startAirPos = spawnPos;
            transform.position = startAirPos;
            transform.localScale = Vector3.one;

            Vector3 toHall = hallPos - startAirPos;
            toHall.y = 0f;
            if (toHall.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(toHall.normalized, Vector3.up);

            flightPhase = UnityEngine.Random.value * 10f;
            orbitAngle = Mathf.Atan2(-toHall.z, -toHall.x);
            attackTimer = 0.8f;
            pushTimer = 0f;
            isSwatted = false;
            crashTimer = 0f;
            crashSmokeTimer = 0f;
            launched = true;

            // Set aerial health and hide default ground monster visuals under Bob
            if (Monster.health != null)
            {
                Monster.health.Init(kind == FlyingThreatKind.GoblinAirship ? AirshipHp : HarpyHp);
                Monster.health.Died -= OnMonsterDied;
                Monster.health.Died += OnMonsterDied;
            }
            SetGroundVisualsActive(false);
            EnsureVisuals();
            airVisualRoot.gameObject.SetActive(true);
            airVisualRoot.localPosition = Vector3.zero;
            airVisualRoot.localRotation = Quaternion.identity;
            airshipVisual.gameObject.SetActive(kind == FlyingThreatKind.GoblinAirship);
            harpyVisual.gameObject.SetActive(kind == FlyingThreatKind.Harpy);
            if (bombVisual != null) bombVisual.gameObject.SetActive(true);

            if (!Active.Contains(this)) Active.Add(this);
            TryHookShockwave();
        }

        void SetGroundVisualsActive(bool active)
        {
            var bob = transform.Find("Bob");
            if (bob == null) return;
            for (int i = 0; i < bob.childCount; i++)
                bob.GetChild(i).gameObject.SetActive(active);
        }

        void EnsureVisuals()
        {
            if (airVisualRoot != null) return;
            airVisualRoot = BuildingVisuals.NewRoot(transform, "FlyingVisualRoot");
            airshipVisual = BuildAirshipVisual(airVisualRoot, out propellerPivot, out bombVisual);
            harpyVisual = BuildHarpyVisual(airVisualRoot, out leftWingPivot, out rightWingPivot);
        }

        static Transform BuildAirshipVisual(Transform parent, out Transform propeller, out Transform bomb)
        {
            var root = BuildingVisuals.NewRoot(parent, "GoblinAirship");
            var k = new BuildingVisuals.Kit(root);

            Color canvasRed = new Color(0.72f, 0.22f, 0.16f);
            Color canvasPatch = new Color(0.82f, 0.58f, 0.24f);
            Color leather = new Color(0.32f, 0.20f, 0.11f);
            Color wood = new Color(0.46f, 0.30f, 0.15f);
            Color darkWood = new Color(0.28f, 0.18f, 0.09f);
            Color iron = new Color(0.28f, 0.29f, 0.32f);
            Color goblinSkin = new Color(0.36f, 0.62f, 0.24f);
            Color brass = new Color(0.88f, 0.72f, 0.25f);

            // Balloon envelope
            k.Ellipsoid(new Vector3(0f, 0.036f, 0f), new Vector3(0.038f, 0.032f, 0.068f), canvasRed);
            k.Ellipsoid(new Vector3(0f, 0.036f, 0.011f), new Vector3(0.040f, 0.034f, 0.010f), leather);
            k.Ellipsoid(new Vector3(0f, 0.036f, -0.011f), new Vector3(0.040f, 0.034f, 0.010f), leather);
            k.Ellipsoid(new Vector3(0.012f, 0.044f, 0.002f), new Vector3(0.014f, 0.010f, 0.022f), canvasPatch);

            // Iron nose spike (+Z)
            var nose = k.Cone(new Vector3(0f, 0.036f, 0.033f), 0.007f, 0.016f, iron);
            nose.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            // Tail stabilizing fins (-Z)
            k.Box(new Vector3(0f, 0.048f, -0.026f), new Vector3(0.003f, 0.018f, 0.018f), canvasPatch, new Vector3(-18f, 0f, 0f));
            k.Box(new Vector3(-0.016f, 0.036f, -0.026f), new Vector3(0.018f, 0.003f, 0.016f), canvasPatch);
            k.Box(new Vector3(0.016f, 0.036f, -0.026f), new Vector3(0.018f, 0.003f, 0.016f), canvasPatch);

            // Rigging ropes from envelope to gondola
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    k.CylRot(new Vector3(sx * 0.010f, 0.020f, sz * 0.011f), 0.001f, 0.022f, leather, new Vector3(sz * -12f, 0f, sx * 14f));

            // Gondola basket
            k.Box(new Vector3(0f, 0.008f, 0f), new Vector3(0.020f, 0.011f, 0.030f), wood);
            k.Box(new Vector3(0f, 0.014f, 0f), new Vector3(0.022f, 0.003f, 0.032f), darkWood);

            // Goblin pilot in gondola
            k.Ball(new Vector3(0f, 0.019f, 0.003f), 0.013f, goblinSkin);
            k.Ball(new Vector3(-0.0035f, 0.022f, 0.008f), 0.0045f, brass);
            k.Ball(new Vector3(0.0035f, 0.022f, 0.008f), 0.0045f, brass);
            k.Cone(new Vector3(-0.007f, 0.021f, 0.002f), 0.0022f, 0.007f, goblinSkin).transform.localRotation = Quaternion.Euler(0f, 0f, 55f);
            k.Cone(new Vector3(0.007f, 0.021f, 0.002f), 0.0022f, 0.007f, goblinSkin).transform.localRotation = Quaternion.Euler(0f, 0f, -55f);

            // Under-slung bomb ready to drop
            var bombRoot = BuildingVisuals.NewRoot(root, "Bomb");
            bombRoot.localPosition = new Vector3(0f, 0.001f, 0f);
            var kb = new BuildingVisuals.Kit(bombRoot);
            kb.Ball(Vector3.zero, 0.009f, iron);
            var fuse = kb.Ball(new Vector3(0f, -0.005f, 0f), 0.004f, new Color(1f, 0.55f, 0.1f));
            fuse.GetComponent<MeshRenderer>().sharedMaterial = Mats.Unlit(new Color(1f, 0.6f, 0.15f));
            bomb = bombRoot;

            // Spinning tail propeller
            var propRoot = BuildingVisuals.NewRoot(root, "Propeller");
            propRoot.localPosition = new Vector3(0f, 0.010f, -0.018f);
            var kp = new BuildingVisuals.Kit(propRoot);
            kp.Ball(Vector3.zero, 0.005f, iron);
            kp.Box(Vector3.zero, new Vector3(0.024f, 0.0035f, 0.0015f), wood, new Vector3(15f, 0f, 0f));
            kp.Box(Vector3.zero, new Vector3(0.0035f, 0.024f, 0.0015f), wood, new Vector3(0f, 15f, 0f));
            propeller = propRoot;

            return root;
        }

        static Transform BuildHarpyVisual(Transform parent, out Transform leftWing, out Transform rightWing)
        {
            var root = BuildingVisuals.NewRoot(parent, "Harpy");
            var k = new BuildingVisuals.Kit(root);

            Color skin = new Color(0.45f, 0.58f, 0.25f);
            Color plumage = new Color(0.52f, 0.18f, 0.28f);
            Color wingDark = new Color(0.32f, 0.12f, 0.20f);
            Color talonGold = new Color(0.85f, 0.68f, 0.22f);

            // Body & head
            k.Ellipsoid(new Vector3(0f, 0.016f, 0f), new Vector3(0.022f, 0.022f, 0.028f), skin);
            k.Ellipsoid(new Vector3(0f, 0.014f, 0.004f), new Vector3(0.018f, 0.016f, 0.020f), plumage);
            k.Ball(new Vector3(0f, 0.024f, 0.010f), 0.015f, skin);
            k.Ball(new Vector3(-0.0045f, 0.026f, 0.016f), 0.0055f, new Color(1f, 0.9f, 0.2f));
            k.Ball(new Vector3(0.0045f, 0.026f, 0.016f), 0.0055f, new Color(1f, 0.9f, 0.2f));
            k.Ball(new Vector3(-0.0045f, 0.026f, 0.0182f), 0.0025f, new Color(0.1f, 0.02f, 0.02f));
            k.Ball(new Vector3(0.0045f, 0.026f, 0.0182f), 0.0025f, new Color(0.1f, 0.02f, 0.02f));

            // Crest & ears
            k.Cone(new Vector3(-0.007f, 0.030f, 0.007f), 0.0025f, 0.010f, plumage).transform.localRotation = Quaternion.Euler(-25f, 0f, 35f);
            k.Cone(new Vector3(0.007f, 0.030f, 0.007f), 0.0025f, 0.010f, plumage).transform.localRotation = Quaternion.Euler(-25f, 0f, -35f);

            // Talons
            k.Cone(new Vector3(-0.006f, 0.004f, 0.008f), 0.0025f, 0.009f, talonGold).transform.localRotation = Quaternion.Euler(115f, 0f, 0f);
            k.Cone(new Vector3(0.006f, 0.004f, 0.008f), 0.0025f, 0.009f, talonGold).transform.localRotation = Quaternion.Euler(115f, 0f, 0f);

            // Left Wing
            leftWing = BuildingVisuals.NewRoot(root, "LeftWing");
            leftWing.localPosition = new Vector3(-0.009f, 0.020f, 0.002f);
            var kl = new BuildingVisuals.Kit(leftWing);
            kl.Box(new Vector3(-0.016f, 0f, 0f), new Vector3(0.032f, 0.0025f, 0.018f), plumage, new Vector3(0f, 8f, 0f));
            kl.Box(new Vector3(-0.034f, 0f, -0.003f), new Vector3(0.018f, 0.002f, 0.013f), wingDark, new Vector3(0f, 18f, 0f));

            // Right Wing
            rightWing = BuildingVisuals.NewRoot(root, "RightWing");
            rightWing.localPosition = new Vector3(0.009f, 0.020f, 0.002f);
            var kr = new BuildingVisuals.Kit(rightWing);
            kr.Box(new Vector3(0.016f, 0f, 0f), new Vector3(0.032f, 0.0025f, 0.018f), plumage, new Vector3(0f, -8f, 0f));
            kr.Box(new Vector3(0.034f, 0f, -0.003f), new Vector3(0.018f, 0.002f, 0.013f), wingDark, new Vector3(0f, -18f, 0f));

            return root;
        }

        // ------------------------------------------------------------ lifecycle & callbacks

        void OnEnable()
        {
            TryHookShockwave();
        }

        void OnDisable()
        {
            CleanupAndDisable();
        }

        void OnDestroy()
        {
            UnhookShockwave();
            Active.Remove(this);
            if (Monster != null && Monster.health != null)
                Monster.health.Died -= OnMonsterDied;
        }

        void TryHookShockwave()
        {
            if (shockwaveHooked || ShockwaveAbility.Instance == null) return;
            ShockwaveAbility.Instance.Fired += OnGlobalShockwaveFired;
            shockwaveHooked = true;
        }

        void UnhookShockwave()
        {
            if (!shockwaveHooked) return;
            if (ShockwaveAbility.Instance != null)
                ShockwaveAbility.Instance.Fired -= OnGlobalShockwaveFired;
            shockwaveHooked = false;
        }

        void OnGlobalShockwaveFired()
        {
            if (!IsAlive) return;
            var sw = ShockwaveAbility.Instance;
            var gd = GameDirector.Instance;
            Vector3 center = gd != null && gd.Hall != null ? gd.Hall.transform.position : hallBasePos;
            float rad = sw != null ? sw.radius : 0.6f;
            float pushDist = sw != null ? sw.push : 0.14f;
            Vector3 d = transform.position - center;
            d.y = 0f;
            if (d.sqrMagnitude <= rad * rad)
            {
                Vector3 dir = d.sqrMagnitude > 1e-6f ? d.normalized : -transform.forward;
                Push(dir, pushDist * 1.25f);
            }
        }

        void OnMonsterDied(Health h)
        {
            Killed?.Invoke(this);
        }

        void CleanupAndDisable()
        {
            launched = false;
            isSwatted = false;
            UnhookShockwave();
            Active.Remove(this);
            if (Monster != null && Monster.health != null)
                Monster.health.Died -= OnMonsterDied;
            if (airVisualRoot != null)
                airVisualRoot.gameObject.SetActive(false);
            enabled = false;
        }

        // ------------------------------------------------------------ hand swat & combat

        /// <summary>
        /// Updates per-frame hand velocity tracking and tests open-palm swat and two-hand mid-air clap
        /// against all active FlyingThreatUnits. Called automatically by FlyingThreatUnit.Update().
        /// </summary>
        public static void UpdateHandSwat()
        {
            if (Time.frameCount == lastHandTrackFrame) return;
            lastHandTrackFrame = Time.frameCount;

            var rig = HandRig.Instance;
            if (rig == null || Active.Count == 0) return;

            float dt = Mathf.Max(1e-4f, Time.deltaTime);
            for (int s = 0; s < 2; s++)
            {
                var side = (HandSide)s;
                if (rig.TryGetPalm(side, out Pose palmPose))
                {
                    Vector3 p = palmPose.position;
                    if (!rig.TryGetIndexTip(side, out Vector3 tip))
                        tip = p + palmPose.rotation * Vector3.forward * 0.08f;

                    if (handValid[s])
                    {
                        Vector3 rawVel = (p - curPalmPos[s]) / dt;
                        palmVelocity[s] = Vector3.Lerp(palmVelocity[s], rawVel, 0.75f);
                        prevPalmPos[s] = curPalmPos[s];
                    }
                    else
                    {
                        prevPalmPos[s] = p;
                        palmVelocity[s] = Vector3.zero;
                        handValid[s] = true;
                    }
                    curPalmPos[s] = p;
                    curTipPos[s] = tip;
                }
                else
                {
                    handValid[s] = false;
                    palmVelocity[s] = Vector3.zero;
                }
            }

            // 1. Two-hand clap check in mid-air
            if (handValid[0] && handValid[1])
            {
                float sep = Vector3.Distance(curPalmPos[0], curPalmPos[1]);
                handClosingSpeed = (prevHandSeparation - sep) / dt;
                prevHandSeparation = sep;
                if (sep <= ClapMaxSeparation && handClosingSpeed >= MinClapClosingSpeed)
                {
                    TryClapBetween(curPalmPos[0], curPalmPos[1], ClapRadius);
                }
            }
            else
            {
                prevHandSeparation = 1f;
                handClosingSpeed = 0f;
            }

            // 2. Single-hand open-palm / finger swat check
            for (int s = 0; s < 2; s++)
            {
                if (!handValid[s]) continue;
                Vector3 vel = palmVelocity[s];
                float speed = vel.magnitude;
                if (speed < MinTouchSpeed) continue;

                for (int i = Active.Count - 1; i >= 0; i--)
                {
                    var ft = Active[i];
                    if (ft == null || !ft.IsAlive) continue;
                    Vector3 targetPos = ft.AimPoint;

                    // Test swept palm segment + index tip proximity
                    float palmDist = SegmentPointDistance(prevPalmPos[s], curPalmPos[s], targetPos);
                    float tipDist = Vector3.Distance(curTipPos[s], targetPos);
                    float minDist = Mathf.Min(palmDist, tipDist);

                    if ((minDist <= SwatRadius && speed >= MinSwatSpeed) ||
                        (minDist <= DirectTouchRadius && speed >= MinTouchSpeed))
                    {
                        ft.Swat(vel, false);
                    }
                }
            }
        }

        /// <summary>Public hook to swat any active flying unit within radius of a hand position.</summary>
        public static bool TrySwatAt(Vector3 handWorldPos, Vector3 handVelocity, float radius = SwatRadius)
        {
            bool hitAny = false;
            float r2 = radius * radius;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var ft = Active[i];
                if (ft == null || !ft.IsAlive) continue;
                if ((ft.AimPoint - handWorldPos).sqrMagnitude <= r2)
                {
                    ft.Swat(handVelocity, false);
                    hitAny = true;
                }
            }
            return hitAny;
        }

        /// <summary>Public hook to clap/smash any flying unit between the player's two palms.</summary>
        public static bool TryClapBetween(Vector3 leftPalmPos, Vector3 rightPalmPos, float radius = ClapRadius)
        {
            Vector3 mid = (leftPalmPos + rightPalmPos) * 0.5f;
            bool hitAny = false;
            float r2 = radius * radius;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var ft = Active[i];
                if (ft == null || !ft.IsAlive) continue;
                if ((ft.AimPoint - mid).sqrMagnitude <= r2)
                {
                    Vector3 smashVel = (ft.transform.position - mid).normalized * 0.8f + Vector3.down * 1.4f;
                    ft.Swat(smashVel, true);
                    hitAny = true;
                }
            }
            return hitAny;
        }

        /// <summary>Smashes this flying unit out of the sky with a physics tumble &amp; ground crash explosion.</summary>
        public void Swat(Vector3 handVelocity, bool isClap = false)
        {
            if (!IsAlive) return;
            isSwatted = true;
            crashTimer = 0f;
            crashSmokeTimer = 0f;

            Vector3 horiz = handVelocity;
            horiz.y = 0f;
            if (horiz.sqrMagnitude < 0.04f)
            {
                Vector3 awayFromHall = transform.position - hallBasePos;
                awayFromHall.y = 0f;
                horiz = awayFromHall.sqrMagnitude > 1e-4f ? awayFromHall.normalized * 0.8f : transform.forward * 0.8f;
            }
            else if (horiz.magnitude > 2.2f)
            {
                horiz = horiz.normalized * 2.2f;
            }

            float downSpeed = Mathf.Min(-0.85f, handVelocity.y - 0.6f);
            crashVel = horiz * 1.25f + Vector3.up * downSpeed;
            crashSpin = new Vector3(540f, 380f, 720f);

            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Burst(AimPoint, isClap ? new Color(1f, 0.85f, 0.25f) : new Color(0.95f, 0.55f, 0.2f));
                VfxPool.Instance.Ring(AimPoint, isClap ? 0.16f : 0.11f);
            }
            ProceduralAudio.Fire(Sfx.Thud, transform.position);
            ProceduralAudio.Fire(Sfx.Hit, transform.position);
            Swatted?.Invoke(this);
        }

        public void Push(Vector3 worldDir, float meters)
        {
            if (!IsAlive) return;
            worldDir.y = 0f;
            if (worldDir.sqrMagnitude < 1e-6f) return;
            pushTimer = 0.25f;
            pushVel = worldDir.normalized * (meters / 0.25f);
        }

        public static void OnShockwave(Vector3 center, float radius, float push = 0.14f, float damage = 0f)
        {
            float r2 = radius * radius;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var ft = Active[i];
                if (ft == null || !ft.IsAlive) continue;
                Vector3 d = ft.transform.position - center;
                d.y = 0f;
                if (d.sqrMagnitude > r2) continue;
                Vector3 dir = d.sqrMagnitude > 1e-6f ? d.normalized : -ft.transform.forward;
                ft.Push(dir, push);
                if (damage > 0f && ft.health != null && !ft.health.IsDead)
                    ft.health.Damage(damage);
            }
        }

        public static FlyingThreatUnit FindClosestInRange(Vector3 origin, float range)
        {
            float best = range * range;
            FlyingThreatUnit found = null;
            for (int i = 0; i < Active.Count; i++)
            {
                var ft = Active[i];
                if (ft == null || !ft.IsAlive) continue;
                float d2 = (ft.transform.position - origin).sqrMagnitude;
                if (d2 <= best)
                {
                    best = d2;
                    found = ft;
                }
            }
            return found;
        }

        // ------------------------------------------------------------ update loop

        /// <summary>World-space velocity (m/s), measured per frame; used by anti-air lead tracking.</summary>
        public Vector3 Velocity { get; private set; }
        Vector3 velPrevPos;
        bool velHasPrev;

        void Update()
        {
            if (!launched || Monster == null) return;
            {
                float vdt = Time.deltaTime;
                Vector3 vp = transform.position;
                if (velHasPrev && vdt > 1e-5f) Velocity = Vector3.Lerp(Velocity, (vp - velPrevPos) / vdt, 0.5f);
                else Velocity = Vector3.zero;
                velPrevPos = vp; velHasPrev = true;
            }
            if (! shockwaveHooked) TryHookShockwave();

            UpdateHandSwat();

            float dt = Time.deltaTime;
            if (isSwatted)
            {
                UpdateSwattedCrash(dt);
                return;
            }
            if (!Monster.IsAlive) return;

            var hall = TownHall.Current != null ? TownHall.Current : (GameDirector.Instance != null ? GameDirector.Instance.Hall : null);
            if (hall != null) hallBasePos = hall.transform.position;

            Vector3 pos = transform.position;

            // Shockwave / knockback displacement in mid-air
            if (pushTimer > 0f)
            {
                float step = Mathf.Min(dt, pushTimer);
                pushTimer -= step;
                pos += pushVel * step;
                transform.position = pos;
                AnimateFlight(dt, 1.8f);
                return;
            }

            // Direct 3D air-space flight to TownHall
            Vector3 toHallFlat = hallBasePos - pos;
            toHallFlat.y = 0f;
            float flatDist = toHallFlat.magnitude;

            float desiredAlt = Mathf.Lerp(hoverAltitude, cruiseAltitude, Mathf.Clamp01((flatDist - AttackHorizontalReach) / 0.55f));
            float bobOffset = Mathf.Sin(flightPhase * (IsHarpy ? 5.5f : 2.8f)) * (IsHarpy ? 0.012f : 0.007f);
            float targetY = hallBasePos.y + desiredAlt + bobOffset;

            float speed = IsAirship ? AirshipSpeed : HarpySpeed;

            if (flatDist > AttackHorizontalReach)
            {
                Vector3 dirFlat = toHallFlat / Mathf.Max(1e-5f, flatDist);
                Vector3 side = Vector3.Cross(Vector3.up, dirFlat);
                float weave = IsHarpy ? Mathf.Sin(flightPhase * 3.6f) * 0.03f : Mathf.Sin(flightPhase * 1.8f) * 0.012f;

                // Soft 3D separation from other active flying units
                Vector3 sep = Vector3.zero;
                for (int i = 0; i < Active.Count; i++)
                {
                    var o = Active[i];
                    if (o == null || o == this || !o.IsAlive) continue;
                    Vector3 diff = pos - o.transform.position;
                    float sq = diff.sqrMagnitude;
                    if (sq < 0.0049f && sq > 1e-8f)
                    {
                        float d = Mathf.Sqrt(sq);
                        sep += (diff / d) * (1f - d / 0.07f);
                    }
                }

                Vector3 moveDir = (dirFlat + side * weave + sep * 0.6f).normalized;
                pos += moveDir * (speed * dt);
                pos.y = Mathf.MoveTowards(pos.y, targetY, speed * 1.1f * dt);
                transform.position = pos;

                Vector3 lookDir = new Vector3(moveDir.x, (targetY - pos.y) * 0.4f, moveDir.z);
                if (lookDir.sqrMagnitude > 1e-6f)
                {
                    Quaternion want = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
                    transform.rotation = Quaternion.Slerp(transform.rotation, want, Mathf.Min(1f, dt * 8f));
                }
                orbitAngle = Mathf.Atan2(pos.z - hallBasePos.z, pos.x - hallBasePos.x);
                AnimateFlight(dt, 1f);
            }
            else
            {
                // Hover / circle above the TownHall and bombard / dive-attack it
                float orbitRadius = IsAirship ? 0.055f : 0.07f;
                orbitAngle += (IsAirship ? 0.85f : 1.55f) * dt;
                Vector3 hoverPos = hallBasePos + new Vector3(Mathf.Cos(orbitAngle) * orbitRadius, desiredAlt + bobOffset, Mathf.Sin(orbitAngle) * orbitRadius);
                pos = Vector3.MoveTowards(pos, hoverPos, speed * 1.2f * dt);
                transform.position = pos;

                Vector3 tangent = new Vector3(-Mathf.Sin(orbitAngle), 0f, Mathf.Cos(orbitAngle));
                Vector3 toCenter = (hallBasePos - pos);
                toCenter.y = 0f;
                Vector3 faceDir = Vector3.Lerp(tangent, toCenter.normalized, 0.45f);
                if (faceDir.sqrMagnitude > 1e-6f)
                {
                    Quaternion want = Quaternion.LookRotation(faceDir.normalized, Vector3.up);
                    transform.rotation = Quaternion.Slerp(transform.rotation, want, Mathf.Min(1f, dt * 8f));
                }

                attackTimer -= dt;
                if (attackTimer <= 0f)
                {
                    attackTimer = AttackInterval;
                    PerformAerialAttack(hall);
                }
                AnimateFlight(dt, 1.2f);
            }
        }

        void PerformAerialAttack(TownHall hall)
        {
            if (hall == null || hall.health == null || hall.health.IsDead) return;
            float dmg = (IsAirship ? AirshipDps : HarpyDps) * AttackInterval;
            hall.health.Damage(dmg);

            Vector3 hitPos = hall.transform.position + Vector3.up * 0.06f;
            if (VfxPool.Instance != null)
            {
                VfxPool.Instance.Burst(hitPos, IsAirship ? new Color(1f, 0.5f, 0.15f) : new Color(0.8f, 0.3f, 0.5f));
            }
            ProceduralAudio.Fire(IsAirship ? Sfx.Thud : Sfx.Hit, hitPos);
        }

        void UpdateSwattedCrash(float dt)
        {
            crashTimer += dt;
            crashSmokeTimer += dt;
            crashVel += Physics.gravity * (1.35f * dt);
            transform.position += crashVel * dt;

            if (airVisualRoot != null)
                airVisualRoot.Rotate(crashSpin * dt, Space.Self);

            if (crashSmokeTimer >= 0.09f)
            {
                crashSmokeTimer = 0f;
                if (VfxPool.Instance != null)
                    VfxPool.Instance.Burst(transform.position, new Color(0.45f, 0.35f, 0.25f));
            }

            float groundY = hallBasePos.y;
            if (targetSurface != null)
            {
                Vector3 proj = targetSurface.ProjectOnto(transform.position);
                if (targetSurface.IsPointSupported(proj))
                    groundY = proj.y;
                else if (RoomCornerSpawns.Existing != null && RoomCornerSpawns.Existing.HasRoom)
                    groundY = RoomCornerSpawns.Existing.FloorY;
            }

            if (transform.position.y <= groundY + 0.01f || crashTimer >= CrashDuration)
            {
                Vector3 impactPos = transform.position;
                impactPos.y = Mathf.Max(groundY, impactPos.y);
                if (VfxPool.Instance != null)
                {
                    VfxPool.Instance.Burst(impactPos, new Color(1f, 0.45f, 0.12f));
                    VfxPool.Instance.Ring(impactPos, CrashSplashRadius);
                }
                ProceduralAudio.Fire(Sfx.Thud, impactPos);

                // Collateral crash splash damage to any ground goblins caught under the wreckage
                float r2 = CrashSplashRadius * CrashSplashRadius;
                var monsters = MonsterUnit.Active;
                for (int i = monsters.Count - 1; i >= 0; i--)
                {
                    if (i >= monsters.Count) continue;
                    var m = monsters[i];
                    if (m == null || m == Monster || !m.IsAlive) continue;
                    if ((m.transform.position - impactPos).sqrMagnitude <= r2 && m.health != null)
                        m.health.Damage(CrashSplashDamage);
                }

                if (Monster != null && Monster.health != null && !Monster.health.IsDead)
                    Monster.health.Damage(9999f);
                else
                    CleanupAndDisable();
            }
        }

        void AnimateFlight(float dt, float rate)
        {
            flightPhase += dt * rate;
            if (IsAirship)
            {
                if (propellerPivot != null)
                    propellerPivot.Rotate(0f, 0f, 920f * dt * rate, Space.Self);
                if (airVisualRoot != null)
                {
                    float pitch = Mathf.Sin(flightPhase * 2.4f) * 4f;
                    float roll = Mathf.Cos(flightPhase * 1.9f) * 5f;
                    airVisualRoot.localRotation = Quaternion.Euler(pitch, 0f, roll);
                }
                if (bombVisual != null)
                {
                    float s = 0.85f + 0.15f * Mathf.Clamp01(1f - attackTimer / AttackInterval);
                    bombVisual.localScale = Vector3.one * s;
                }
            }
            else
            {
                float flap = Mathf.Sin(flightPhase * 18f) * 42f;
                if (leftWingPivot != null)
                    leftWingPivot.localRotation = Quaternion.Euler(0f, 0f, flap);
                if (rightWingPivot != null)
                    rightWingPivot.localRotation = Quaternion.Euler(0f, 0f, -flap);
                if (airVisualRoot != null)
                    airVisualRoot.localRotation = Quaternion.Euler(12f + Mathf.Sin(flightPhase * 9f) * 6f, 0f, Mathf.Sin(flightPhase * 4.5f) * 8f);
            }
        }

        static float SegmentPointDistance(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float l2 = ab.sqrMagnitude;
            if (l2 < 1e-8f) return Vector3.Distance(a, p);
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / l2);
            return Vector3.Distance(a + ab * t, p);
        }
    }
}
