#pragma warning disable CS0619
using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Feature 2: "Hand of God" Tactile AR Quest 3 Hand-Tracking Interactions.
    /// 1. Siege Ladder / Rope Finger Poke: Poke or swipe an active ladder/rope at the table edge to topple it and knock climbing goblins off with fall damage.
    /// 2. Pinch &amp; Flick Goblins: Pinch a normal goblin (4s cooldown), lift it, and flick/throw it off the table or bowl it into a crowd of goblins.
    /// 3. Focus Fire Pointing: Point with the index finger at a dangerous enemy (Ram, Engineer, Boss, etc.) to place a glowing target reticle above it
    ///    and command all ArcherTowers in range to prioritize and fire a synchronized volley at that target.
    /// Self-creating singleton. Owner: @hand-god.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class HandOfGodController : MonoBehaviour
    {
        static HandOfGodController instance;
        static bool quitting;

        /// <summary>Existing instance without auto-creation (null if none).</summary>
        public static HandOfGodController Existing => instance;

        /// <summary>Singleton instance; auto-creates at runtime if needed.</summary>
        public static HandOfGodController Instance
        {
            get
            {
                if (instance == null && !quitting && Application.isPlaying)
                {
                    var go = new GameObject("HandOfGodController");
                    instance = go.AddComponent<HandOfGodController>();
                }
                return instance;
            }
        }

        /// <summary>Ensures the controller exists in the scene. Safe to call from GameDirector.Start/Awake.</summary>
        public static HandOfGodController Ensure() => Instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            instance = null;
            quitting = false;
            LadderKnockedDown = null;
            GoblinPinched = null;
            GoblinFlicked = null;
            GoblinBowledHit = null;
            FocusTargetChanged = null;
            FocusVolleyFired = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoBootstrap()
        {
            if (Application.isPlaying && instance == null)
                _ = Instance;
        }

        // ------------------------------------------------------------ configuration
        [Header("1. Ladder & Rope Finger Poke")]
        public float ladderPokeRadius = 0.085f;
        public float ladderDirectContactRadius = 0.048f;
        public float ladderMinSwipeSpeed = 0.055f;
        public float ladderFallDamage = 3.5f;
        public float ladderPokeCooldown = 0.25f;

        [Header("2. Pinch & Flick Goblins")]
        public float pinchCooldown = 4.0f;
        public float pinchGrabRadius = 0.075f;
        public float maxHoldDuration = 3.5f;
        public float flickVelocityMultiplier = 1.85f;
        public float minFlickSpeed = 0.65f;
        public float maxFlickSpeed = 4.2f;
        public float bowlingHitRadius = 0.068f;
        public float bowlingCollisionDamage = 4.5f;
        public float bowlingKnockback = 0.14f;
        public float selfThrowBaseDamage = 3.5f;
        public bool onlyNormalGoblins = true;

        [Header("3. Focus Fire Pointing")]
        public float focusPointConeDeg = 18f;
        public float focusMaxDistance = 2.8f;
        public float focusDirectPokeRadius = 0.095f;
        public float focusDwellSeconds = 0.14f;
        public float focusDuration = 6.0f;
        public float focusVolleyCooldown = 3.0f;
        public float focusRangeBonusMul = 1.15f;

        // ------------------------------------------------------------ events
        public static event Action<ClimbAccess> LadderKnockedDown;
        public static event Action<MonsterUnit> GoblinPinched;
        public static event Action<MonsterUnit, Vector3> GoblinFlicked;
        public static event Action<MonsterUnit, MonsterUnit> GoblinBowledHit;
        public static event Action<MonsterUnit> FocusTargetChanged;
        public static event Action<MonsterUnit, int> FocusVolleyFired;

        // ------------------------------------------------------------ hand tracking sample buffer
        struct HandSample
        {
            public float t;
            public Vector3 tip;
            public Vector3 palm;
            public bool valid;
        }

        const int SampleCap = 16;
        readonly HandSample[,] samples = new HandSample[2, SampleCap];
        readonly int[] sampleHead = new int[2];
        readonly int[] sampleCount = new int[2];
        readonly bool[] prevPinching = new bool[2];
        float lastLadderPokeTime = -10f;

        // ------------------------------------------------------------ pinch & flick state
        MonsterUnit heldGoblin;
        int heldSerial;
        HandSide heldSide;
        float heldStartTime;
        float lastPinchReleaseTime = -10f;
        Transform pinchVisualRoot;

        sealed class FlickedGoblin
        {
            public MonsterUnit monster;
            public int serial;
            public Vector3 pos;
            public Vector3 vel;
            public float elapsed;
            public bool rollingOnTable;
            public int hitCount;
            public readonly List<int> hitSerials = new List<int>(8);
        }

        readonly List<FlickedGoblin> flickedGoblins = new List<FlickedGoblin>(4);

        // ------------------------------------------------------------ focus fire state
        MonsterUnit focusedTarget;
        int focusedSerial;
        float focusExpireTime;
        float lastVolleyTime = -10f;

        MonsterUnit dwellCandidate;
        int dwellCandidateSerial;
        float dwellTime;

        readonly HashSet<int> pendingVolleyTowers = new HashSet<int>();
        Transform reticleRoot;
        Transform reticleSpinner;
        Transform reticleBeacon;
        Transform reticleGroundRing;

        bool directorSubscribed;
        GrabWatcher[] cachedWatchers = Array.Empty<GrabWatcher>();
        float nextWatcherScan;

        // ------------------------------------------------------------ public properties
        /// <summary>Currently pinched goblin held by the player (null if none).</summary>
        public MonsterUnit HeldGoblin => IsHeldGoblinValid() ? heldGoblin : null;

        /// <summary>Remaining cooldown in seconds before another goblin can be pinched.</summary>
        public float PinchCooldownRemaining => Mathf.Max(0f, pinchCooldown - (Time.time - lastPinchReleaseTime));

        /// <summary>True when the Pinch &amp; Flick ability is off cooldown and no goblin is currently held.</summary>
        public bool CanPinchGoblin => PinchCooldownRemaining <= 0f && !IsHeldGoblinValid();

        /// <summary>Currently marked Focus Fire enemy target (null if none or expired/dead).</summary>
        public static MonsterUnit FocusedTarget => instance != null && instance.IsFocusTargetValid() ? instance.focusedTarget : null;

        /// <summary>Remaining duration of the active Focus Fire target reticle.</summary>
        public float FocusRemainingSeconds => IsFocusTargetValid() ? Mathf.Max(0f, focusExpireTime - Time.time) : 0f;

        // ------------------------------------------------------------ static hooks for @integrator
        /// <summary>
        /// Hook for MonsterUnit.Update(): returns true when `m` is currently pinched in the player's fingers
        /// or flying/rolling as a flicked bowling ball, so MonsterUnit can skip normal spline movement.
        /// </summary>
        public static bool IsMonsterControlled(MonsterUnit m)
        {
            if (instance == null || m == null) return false;
            if (instance.heldGoblin == m && instance.IsHeldGoblinValid()) return true;
            for (int i = 0; i < instance.flickedGoblins.Count; i++)
            {
                var fg = instance.flickedGoblins[i];
                if (fg.monster == m && fg.serial == m.Serial && m.IsAlive) return true;
            }
            return false;
        }

        /// <summary>
        /// Hook for PlacedUnit (ArcherTower): returns the active Focus Fire target if one is locked, alive,
        /// and within range of `towerPos` (using focusRangeBonusMul); otherwise returns null.
        /// </summary>
        public static MonsterUnit GetFocusedTargetInRange(Vector3 towerPos, float range)
        {
            if (instance == null || !instance.IsFocusTargetValid()) return null;
            var t = instance.focusedTarget;
            float effRange = range * Mathf.Max(1f, instance.focusRangeBonusMul);
            if ((t.transform.position - towerPos).sqrMagnitude <= effRange * effRange)
                return t;
            return null;
        }

        /// <summary>
        /// Hook for PlacedUnit (ArcherTower): tries to get the active Focus Fire target in range.
        /// </summary>
        public static bool TryGetFocusedTargetInRange(Vector3 towerPos, float range, out MonsterUnit target)
        {
            target = GetFocusedTargetInRange(towerPos, range);
            return target != null;
        }

        /// <summary>
        /// Convenience target selector for ArcherTowers: returns FocusedTarget if alive and in range,
        /// otherwise finds the closest alive MonsterUnit within `range`.
        /// </summary>
        public static MonsterUnit SelectTowerTarget(Vector3 towerPos, float range)
        {
            var focused = GetFocusedTargetInRange(towerPos, range);
            if (focused != null) return focused;

            float best = range * range;
            MonsterUnit target = null;
            var list = MonsterUnit.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                if (m == null || !m.IsAlive) continue;
                float d = (m.transform.position - towerPos).sqrMagnitude;
                if (d <= best) { best = d; target = m; }
            }
            return target;
        }

        /// <summary>
        /// Hook for PlacedUnit.Update(): returns true once per ArcherTower when a new synchronized Focus Fire volley
        /// is ordered, allowing the tower to immediately reset its cooldown and fire at `target`.
        /// Any tower that does not consume this in Update will automatically fire its volley in HandOfGodController.LateUpdate.
        /// </summary>
        public static bool TryConsumeVolley(PlacedUnit tower, out MonsterUnit target)
        {
            target = null;
            if (instance == null || tower == null || !instance.IsFocusTargetValid()) return false;
            int id = tower.GetHashCode();
            if (!instance.pendingVolleyTowers.Remove(id)) return false;
            target = instance.focusedTarget;
            return true;
        }

        /// <summary>Returns true if `m` is a dangerous priority enemy (Ram, Engineer, Boss, Catapult, Flying threat, or high-HP unit).</summary>
        public static bool IsPriorityThreat(MonsterUnit m)
        {
            if (m == null || !m.IsAlive) return false;
            if (m.IsRam || m.IsEngineer) return true;
            if (m.health != null && m.health.Max > MonsterUnit.NormalHp + 1f) return true;
            if (m.GetComponent("GoblinKingBoss") != null ||
                m.GetComponent("FlyingThreatUnit") != null ||
                m.GetComponent("SiegeCatapultUnit") != null)
                return true;
            return false;
        }

        // ------------------------------------------------------------ Unity lifecycle
        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            BuildReticleVisual();
            BuildPinchVisual();
        }

        void Start()
        {
            TrySubscribeDirector();
        }

        void OnApplicationQuit() => quitting = true;

        void OnDestroy()
        {
            if (directorSubscribed && GameDirector.Instance != null)
                GameDirector.Instance.GameReset -= ResetState;
            if (reticleRoot != null) Destroy(reticleRoot.gameObject);
            if (reticleGroundRing != null) Destroy(reticleGroundRing.gameObject);
            if (pinchVisualRoot != null) Destroy(pinchVisualRoot.gameObject);
            if (instance == this) instance = null;
        }

        void TrySubscribeDirector()
        {
            if (directorSubscribed || GameDirector.Instance == null) return;
            GameDirector.Instance.GameReset += ResetState;
            directorSubscribed = true;
        }

        /// <summary>Clears any held/flicked goblins, active focus target, and resets timers (called on Restart / NewEmpire).</summary>
        public void ResetState()
        {
            heldGoblin = null;
            heldSerial = 0;
            flickedGoblins.Clear();
            focusedTarget = null;
            focusedSerial = 0;
            focusExpireTime = 0f;
            dwellCandidate = null;
            dwellTime = 0f;
            pendingVolleyTowers.Clear();
            lastPinchReleaseTime = -10f;
            lastVolleyTime = -10f;
            if (reticleRoot != null) reticleRoot.gameObject.SetActive(false);
            if (reticleGroundRing != null) reticleGroundRing.gameObject.SetActive(false);
            if (pinchVisualRoot != null) pinchVisualRoot.gameObject.SetActive(false);
        }

        void Update()
        {
            if (!directorSubscribed) TrySubscribeDirector();

            var gd = GameDirector.Instance;
            if (gd != null && !gd.IsCombatPhase)
            {
                if (heldGoblin != null || focusedTarget != null || flickedGoblins.Count > 0)
                    ResetState();
                return;
            }

            float dt = Time.deltaTime;
            SampleHands();
            UpdateLadderPoke();
            UpdatePinchAndFlick(dt);
            UpdateFlickedGoblins(dt);
            UpdateFocusFirePointing(dt);
            UpdateReticleAnimation(dt);
        }

        void LateUpdate()
        {
            // Keep held goblin pinned to the pinching fingertips even if MonsterUnit.Update ran
            if (IsHeldGoblinValid())
            {
                var rig = HandRig.Instance;
                if (rig != null && rig.TryGetIndexTip(heldSide, out var tip))
                {
                    Vector3 holdPos = tip + Vector3.down * 0.012f;
                    heldGoblin.transform.position = holdPos;
                    float w = Time.time * 38f;
                    heldGoblin.transform.rotation = Quaternion.Euler(Mathf.Sin(w) * 24f, Time.time * 220f, Mathf.Cos(w * 1.15f) * 24f);
                    if (pinchVisualRoot != null)
                    {
                        if (!pinchVisualRoot.gameObject.activeSelf) pinchVisualRoot.gameObject.SetActive(true);
                        pinchVisualRoot.position = holdPos + Vector3.up * 0.015f;
                        pinchVisualRoot.Rotate(0f, 240f * Time.deltaTime, 0f, Space.World);
                    }
                }
            }
            else if (pinchVisualRoot != null && pinchVisualRoot.gameObject.activeSelf)
            {
                pinchVisualRoot.gameObject.SetActive(false);
            }

            // Ensure flicked goblins keep their physics position
            for (int i = 0; i < flickedGoblins.Count; i++)
            {
                var fg = flickedGoblins[i];
                if (fg.monster != null && fg.monster.IsAlive && fg.monster.Serial == fg.serial)
                    fg.monster.transform.position = fg.pos;
            }

            // Execute any pending synchronized volley shots that weren't already consumed by PlacedUnit.Update
            FlushPendingVolley();
        }

        // ------------------------------------------------------------ hand sampling
        void SampleHands()
        {
            var rig = HandRig.Instance;
            if (rig == null) return;
            float now = Time.time;

            for (int h = 0; h < 2; h++)
            {
                var side = (HandSide)h;
                if (!rig.IsTracked(side) || !rig.TryGetIndexTip(side, out var tip))
                {
                    sampleCount[h] = 0;
                    continue;
                }
                Vector3 palmPos = rig.TryGetPalm(side, out var palm) ? palm.position : tip;
                int head = sampleHead[h];
                samples[h, head] = new HandSample { t = now, tip = tip, palm = palmPos, valid = true };
                sampleHead[h] = (head + 1) % SampleCap;
                if (sampleCount[h] < SampleCap) sampleCount[h]++;
            }
        }

        Vector3 ComputeTipVelocity(HandSide side, float lookbackWindow = 0.10f)
        {
            int h = (int)side;
            int cnt = sampleCount[h];
            if (cnt < 2) return Vector3.zero;

            int newestIdx = (sampleHead[h] - 1 + SampleCap) % SampleCap;
            var newest = samples[h, newestIdx];
            if (!newest.valid) return Vector3.zero;

            int oldestIdx = newestIdx;
            for (int k = 1; k < cnt; k++)
            {
                int idx = (newestIdx - k + SampleCap) % SampleCap;
                var s = samples[h, idx];
                if (!s.valid) break;
                oldestIdx = idx;
                if (newest.t - s.t >= lookbackWindow) break;
            }

            if (oldestIdx == newestIdx) return Vector3.zero;
            var oldest = samples[h, oldestIdx];
            float dt = newest.t - oldest.t;
            return dt > 1e-4f ? (newest.tip - oldest.tip) / dt : Vector3.zero;
        }

        Vector3 ComputePalmVelocity(HandSide side, float lookbackWindow = 0.10f)
        {
            int h = (int)side;
            int cnt = sampleCount[h];
            if (cnt < 2) return Vector3.zero;

            int newestIdx = (sampleHead[h] - 1 + SampleCap) % SampleCap;
            var newest = samples[h, newestIdx];
            if (!newest.valid) return Vector3.zero;

            int oldestIdx = newestIdx;
            for (int k = 1; k < cnt; k++)
            {
                int idx = (newestIdx - k + SampleCap) % SampleCap;
                var s = samples[h, idx];
                if (!s.valid) break;
                oldestIdx = idx;
                if (newest.t - s.t >= lookbackWindow) break;
            }

            if (oldestIdx == newestIdx) return Vector3.zero;
            var oldest = samples[h, oldestIdx];
            float dt = newest.t - oldest.t;
            return dt > 1e-4f ? (newest.palm - oldest.palm) / dt : Vector3.zero;
        }

        bool IsHandGrabbingToken(HandSide side)
        {
            if (Time.unscaledTime >= nextWatcherScan)
            {
                nextWatcherScan = Time.unscaledTime + 1.5f;
#if UNITY_6000_4_OR_NEWER
                cachedWatchers = FindObjectsByType<GrabWatcher>(FindObjectsInactive.Exclude);
#else
                cachedWatchers = FindObjectsByType<GrabWatcher>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#endif
            }
            for (int i = 0; i < cachedWatchers.Length; i++)
            {
                var w = cachedWatchers[i];
                if (w != null && w.IsGrabbed && w.GrabbingSide == side) return true;
            }
            return false;
        }

        // ------------------------------------------------------------ 1. Ladder & Rope Finger Poke
        void UpdateLadderPoke()
        {
            var cr = ClimbRoutes.Existing;
            var rig = HandRig.Instance;
            if (cr == null || rig == null || cr.Accesses.Count == 0) return;
            if (Time.time - lastLadderPokeTime < ladderPokeCooldown) return;

            for (int h = 0; h < 2; h++)
            {
                var side = (HandSide)h;
                if (!rig.IsTracked(side)) continue;
                if (IsHeldGoblinValid() && heldSide == side) continue;
                if (IsHandGrabbingToken(side)) continue;

                if (rig.TryGetIndexTip(side, out var tip))
                {
                    Vector3 tipVel = ComputeTipVelocity(side, 0.09f);
                    float radius = tipVel.sqrMagnitude >= ladderMinSwipeSpeed * ladderMinSwipeSpeed
                        ? ladderPokeRadius
                        : ladderDirectContactRadius;

                    if (cr.TryPokeAccess(tip, tipVel, radius, ladderFallDamage, out var knocked))
                    {
                        lastLadderPokeTime = Time.time;
                        LadderKnockedDown?.Invoke(knocked);
                        return;
                    }
                }

                // Also allow a backhand/palm swipe at the table edge to knock down ladders
                if (!rig.IsPinching(side) && rig.TryGetPalm(side, out var palm))
                {
                    Vector3 palmVel = ComputePalmVelocity(side, 0.09f);
                    if (palmVel.sqrMagnitude >= (ladderMinSwipeSpeed * 1.25f) * (ladderMinSwipeSpeed * 1.25f))
                    {
                        if (cr.TryPokeAccess(palm.position, palmVel, ladderPokeRadius * 0.9f, ladderFallDamage, out var knocked))
                        {
                            lastLadderPokeTime = Time.time;
                            LadderKnockedDown?.Invoke(knocked);
                            return;
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------ 2. Pinch & Flick Goblins
        bool IsHeldGoblinValid() => heldGoblin != null && heldGoblin.IsAlive && heldGoblin.Serial == heldSerial;

        void UpdatePinchAndFlick(float dt)
        {
            var rig = HandRig.Instance;
            if (rig == null) return;

            // Check if currently holding a goblin
            if (heldGoblin != null)
            {
                if (!IsHeldGoblinValid())
                {
                    heldGoblin = null;
                    if (pinchVisualRoot != null) pinchVisualRoot.gameObject.SetActive(false);
                }
                else
                {
                    Vector3 tipNow = Vector3.zero;
                    bool stillTracked = rig.IsTracked(heldSide) && rig.TryGetIndexTip(heldSide, out tipNow);
                    bool stillPinching = stillTracked && rig.IsPinching(heldSide);
                    bool timedOut = Time.time - heldStartTime >= maxHoldDuration;

                    if (stillPinching && !timedOut)
                    {
                        Vector3 holdPos = tipNow + Vector3.down * 0.012f;
                        heldGoblin.transform.position = holdPos;
                    }
                    else
                    {
                        ReleaseAndFlickHeldGoblin();
                    }
                }
            }

            // Detect pinch start on a normal goblin
            for (int h = 0; h < 2; h++)
            {
                var side = (HandSide)h;
                bool pinching = rig.IsTracked(side) && rig.IsPinching(side);
                bool rising = pinching && !prevPinching[h];
                prevPinching[h] = pinching;

                if (!rising || !CanPinchGoblin) continue;
                if (IsHandGrabbingToken(side)) continue;
                if (!rig.TryGetIndexTip(side, out var tip)) continue;

                MonsterUnit candidate = FindPinchableGoblin(tip, pinchGrabRadius);
                if (candidate != null)
                {
                    BeginPinchGoblin(candidate, side, tip);
                    break;
                }
            }
        }

        MonsterUnit FindPinchableGoblin(Vector3 tip, float radius)
        {
            float bestDistSq = radius * radius;
            MonsterUnit best = null;
            var list = MonsterUnit.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                if (m == null || !m.IsAlive) continue;
                if (onlyNormalGoblins && (m.Kind != MonsterKind.Normal || IsPriorityThreat(m))) continue;
                if (IsMonsterControlled(m)) continue;

                float dSq = Mathf.Min((m.AimPoint - tip).sqrMagnitude, (m.transform.position - tip).sqrMagnitude);
                if (dSq <= bestDistSq)
                {
                    bestDistSq = dSq;
                    best = m;
                }
            }
            return best;
        }

        void BeginPinchGoblin(MonsterUnit goblin, HandSide side, Vector3 tip)
        {
            heldGoblin = goblin;
            heldSerial = goblin.Serial;
            heldSide = side;
            heldStartTime = Time.time;

            if (VfxPool.Instance != null)
                VfxPool.Instance.Burst(goblin.AimPoint, new Color(0.55f, 0.95f, 0.4f));
            ProceduralAudio.Fire(Sfx.Click, tip);

            if (pinchVisualRoot != null)
            {
                pinchVisualRoot.position = tip;
                pinchVisualRoot.gameObject.SetActive(true);
            }

            GoblinPinched?.Invoke(goblin);
        }

        void ReleaseAndFlickHeldGoblin()
        {
            var goblin = heldGoblin;
            int serial = heldSerial;
            HandSide side = heldSide;
            heldGoblin = null;
            heldSerial = 0;
            lastPinchReleaseTime = Time.time;

            if (pinchVisualRoot != null) pinchVisualRoot.gameObject.SetActive(false);
            if (goblin == null || !goblin.IsAlive || goblin.Serial != serial) return;

            Vector3 rawVel = ComputeTipVelocity(side, 0.11f);
            Vector3 launchVel = rawVel * flickVelocityMultiplier;
            float speed = launchVel.magnitude;

            if (speed < minFlickSpeed)
            {
                // Gentle drop or light flick: push away from the Town Hall or along hand forward
                Vector3 fallbackDir = rawVel.sqrMagnitude > 1e-4f ? rawVel.normalized : Vector3.forward;
                if (TownHall.Current != null)
                {
                    Vector3 fromHall = goblin.transform.position - TownHall.Current.transform.position;
                    fromHall.y = 0f;
                    if (fromHall.sqrMagnitude > 1e-4f) fallbackDir = fromHall.normalized;
                }
                launchVel = fallbackDir * minFlickSpeed + Vector3.up * 0.25f;
            }
            else
            {
                if (speed > maxFlickSpeed) launchVel = launchVel.normalized * maxFlickSpeed;
                // Add a slight upward loft so flicks travel cleanly across the table or over the rim
                launchVel.y = Mathf.Clamp(launchVel.y + 0.18f, -1.5f, 1.8f);
            }

            var fg = new FlickedGoblin
            {
                monster = goblin,
                serial = serial,
                pos = goblin.transform.position,
                vel = launchVel,
                elapsed = 0f,
                rollingOnTable = false,
                hitCount = 0
            };
            flickedGoblins.Add(fg);

            if (VfxPool.Instance != null)
                VfxPool.Instance.Ring(goblin.transform.position, 0.06f);
            ProceduralAudio.Fire(Sfx.Arrow, goblin.transform.position);
            GoblinFlicked?.Invoke(goblin, launchVel);
        }

        void UpdateFlickedGoblins(float dt)
        {
            for (int i = flickedGoblins.Count - 1; i >= 0; i--)
            {
                var fg = flickedGoblins[i];
                var m = fg.monster;
                if (m == null || !m.IsAlive || m.Serial != fg.serial)
                {
                    flickedGoblins.RemoveAt(i);
                    continue;
                }

                fg.elapsed += dt;
                ITableSurface surface = m.Path != null && m.Path.Surface != null
                    ? m.Path.Surface
                    : (GameDirector.Instance != null ? GameDirector.Instance.Surface : null);

                if (!fg.rollingOnTable)
                {
                    fg.vel += Physics.gravity * (0.68f * dt);
                    fg.pos += fg.vel * dt;
                    m.transform.position = fg.pos;
                    m.transform.Rotate(520f * dt, 340f * dt, 260f * dt, Space.Self);

                    if (surface != null)
                    {
                        Vector3 proj = surface.ProjectOnto(fg.pos);
                        if (surface.IsPointSupported(proj) && fg.pos.y <= proj.y + 0.016f && fg.vel.y <= 0.05f)
                        {
                            Vector3 flatVel = new Vector3(fg.vel.x, 0f, fg.vel.z);
                            if (flatVel.sqrMagnitude >= 0.12f * 0.12f)
                            {
                                fg.rollingOnTable = true;
                                fg.pos = proj;
                                fg.vel = flatVel;
                                m.transform.position = fg.pos;
                                if (VfxPool.Instance != null) VfxPool.Instance.Ring(proj, 0.05f);
                                ProceduralAudio.Fire(Sfx.Thud, proj);
                            }
                            else
                            {
                                FinishFlickedGoblin(i, fg, false);
                                continue;
                            }
                        }
                    }
                }
                else
                {
                    // Bowling ball roll across the table surface
                    fg.pos += fg.vel * dt;
                    fg.vel = Vector3.MoveTowards(fg.vel, Vector3.zero, 1.75f * dt);
                    m.transform.position = fg.pos;
                    m.transform.Rotate(720f * dt, 0f, 360f * dt, Space.Self);

                    if (surface != null)
                    {
                        Vector3 proj = surface.ProjectOnto(fg.pos);
                        if (!surface.IsPointSupported(proj))
                        {
                            // Rolled off the table edge!
                            fg.rollingOnTable = false;
                            fg.vel += Vector3.down * 0.35f;
                        }
                        else
                        {
                            fg.pos = proj;
                            m.transform.position = fg.pos;
                        }
                    }
                }

                // Check bowling collisions with other goblins
                CheckBowlingCollisions(fg);

                // Check termination (fell off table, hit floor, or finished rolling)
                float floorY = RoomCornerSpawns.Existing != null
                    ? RoomCornerSpawns.Existing.FloorY
                    : (surface != null ? surface.Height - 0.72f : -0.72f);

                bool fellOffTable = surface != null &&
                                    !surface.IsPointSupported(surface.ProjectOnto(fg.pos)) &&
                                    fg.pos.y < surface.Height - 0.14f;
                bool hitFloor = fg.pos.y <= floorY + 0.015f && (surface == null || fg.pos.y < surface.Height - 0.08f);

                if (fellOffTable || hitFloor)
                {
                    FinishFlickedGoblin(i, fg, true);
                    continue;
                }

                if ((fg.rollingOnTable && fg.vel.sqrMagnitude < 0.04f * 0.04f) || fg.elapsed >= 1.35f)
                {
                    bool offEdge = surface != null && !surface.IsPointSupported(surface.ProjectOnto(fg.pos));
                    FinishFlickedGoblin(i, fg, offEdge);
                }
            }
        }

        void CheckBowlingCollisions(FlickedGoblin fg)
        {
            var bowler = fg.monster;
            if (bowler == null || !bowler.IsAlive) return;

            var list = MonsterUnit.Active;
            Vector3 flatVel = new Vector3(fg.vel.x, 0f, fg.vel.z);
            Vector3 travelDir = flatVel.sqrMagnitude > 1e-4f ? flatVel.normalized : Vector3.forward;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (i >= list.Count) continue;
                var other = list[i];
                if (other == null || other == bowler || !other.IsAlive) continue;
                if (fg.hitSerials.Contains(other.Serial)) continue;

                float hitR = bowlingHitRadius + other.Radius;
                Vector3 diff = other.transform.position - fg.pos;
                if (Mathf.Abs(diff.y) > 0.075f) continue;
                diff.y = 0f;
                if (diff.sqrMagnitude > hitR * hitR) continue;

                fg.hitSerials.Add(other.Serial);
                fg.hitCount++;

                Vector3 knockDir = diff.sqrMagnitude > 1e-6f
                    ? (diff.normalized + travelDir * 0.65f).normalized
                    : travelDir;

                other.Push(knockDir, bowlingKnockback);
                if (other.health != null && !other.health.IsDead)
                    other.health.Damage(bowlingCollisionDamage);

                if (VfxPool.Instance != null)
                    VfxPool.Instance.Burst(other.AimPoint, new Color(0.45f, 0.95f, 0.35f));
                ProceduralAudio.Fire(Sfx.Hit, other.transform.position);
                ProceduralAudio.Fire(Sfx.Thud, other.transform.position);

                fg.vel *= 0.86f;
                GoblinBowledHit?.Invoke(bowler, other);
            }
        }

        void FinishFlickedGoblin(int index, FlickedGoblin fg, bool fatalFall)
        {
            var m = fg.monster;
            flickedGoblins.RemoveAt(index);
            if (m == null || !m.IsAlive || m.Serial != fg.serial) return;

            if (VfxPool.Instance != null)
                VfxPool.Instance.Burst(m.AimPoint, new Color(0.42f, 0.82f, 0.3f));
            ProceduralAudio.Fire(Sfx.Thud, m.transform.position);

            if (fatalFall)
            {
                if (m.health != null && !m.health.IsDead)
                    m.health.Damage(999f);
                return;
            }

            float selfDmg = selfThrowBaseDamage + fg.hitCount * 2.0f;
            if (m.health != null && !m.health.IsDead)
                m.health.Damage(selfDmg);

            if (m.IsAlive)
            {
                Vector3 flatDir = new Vector3(fg.vel.x, 0f, fg.vel.z);
                if (flatDir.sqrMagnitude < 1e-4f) flatDir = Vector3.forward;
                m.transform.rotation = Quaternion.LookRotation(flatDir.normalized, Vector3.up);
                // Short push causes MonsterUnit to re-project its pathDist onto its SplinePath (or fall if unsupported)
                m.Push(flatDir.normalized, 0.025f);
            }
        }

        // ------------------------------------------------------------ 3. Focus Fire Finger Pointing
        bool IsFocusTargetValid() =>
            focusedTarget != null &&
            focusedTarget.IsAlive &&
            focusedTarget.Serial == focusedSerial &&
            Time.time < focusExpireTime;

        void UpdateFocusFirePointing(float dt)
        {
            if (focusedTarget != null && !IsFocusTargetValid())
            {
                focusedTarget = null;
                pendingVolleyTowers.Clear();
                if (reticleRoot != null) reticleRoot.gameObject.SetActive(false);
                if (reticleGroundRing != null) reticleGroundRing.gameObject.SetActive(false);
            }

            var rig = HandRig.Instance;
            if (rig == null || MonsterUnit.Active.Count == 0) return;

            MonsterUnit bestCandidate = null;
            float bestScore = float.MinValue;
            bool immediateLock = false;

            for (int h = 0; h < 2; h++)
            {
                var side = (HandSide)h;
                if (!rig.IsTracked(side) || rig.IsPinching(side)) continue;
                if (IsHeldGoblinValid() && heldSide == side) continue;
                if (IsHandGrabbingToken(side)) continue;
                if (!rig.TryGetIndexTip(side, out var indexTip)) continue;

                // Skip pointing while hand is sweeping rapidly (e.g. Shockwave gesture)
                Vector3 tipVel = ComputeTipVelocity(side, 0.08f);
                if (tipVel.sqrMagnitude > 0.75f * 0.75f) continue;

                bool hasPalm = rig.TryGetPalm(side, out var palm);
                Vector3 fingerDir = Vector3.zero;
                bool fingerExtended = false;
                if (hasPalm)
                {
                    Vector3 palmToTip = indexTip - palm.position;
                    float ext = palmToTip.magnitude;
                    if (ext >= 0.062f)
                    {
                        fingerDir = palmToTip / ext;
                        fingerExtended = true;
                    }
                }

                bool hasIsdkRay = rig.TryGetRay(side, out var isdkRay);
                if (!fingerExtended && !hasIsdkRay) continue;

                var candidate = EvaluatePointedEnemy(indexTip, fingerExtended ? fingerDir : isdkRay.direction, hasIsdkRay ? isdkRay : default, hasIsdkRay, out float score, out bool directPoke);
                if (candidate != null && score > bestScore)
                {
                    bestScore = score;
                    bestCandidate = candidate;
                    if (directPoke) immediateLock = true;
                }
            }

            if (bestCandidate != null)
            {
                if (immediateLock)
                {
                    SetFocusTarget(bestCandidate, true);
                    dwellCandidate = null;
                    dwellTime = 0f;
                }
                else if (dwellCandidate == bestCandidate && dwellCandidateSerial == bestCandidate.Serial)
                {
                    dwellTime += dt;
                    if (dwellTime >= focusDwellSeconds)
                    {
                        SetFocusTarget(bestCandidate, true);
                        dwellTime = 0f;
                    }
                }
                else
                {
                    dwellCandidate = bestCandidate;
                    dwellCandidateSerial = bestCandidate.Serial;
                    dwellTime = dt;
                }
            }
            else
            {
                dwellTime = Mathf.Max(0f, dwellTime - dt * 2f);
                if (dwellTime <= 0f) dwellCandidate = null;
            }
        }

        MonsterUnit EvaluatePointedEnemy(Vector3 indexTip, Vector3 fingerDir, Ray isdkRay, bool hasIsdkRay, out float bestScore, out bool isDirectPoke)
        {
            bestScore = float.MinValue;
            isDirectPoke = false;
            MonsterUnit best = null;

            float directR2 = focusDirectPokeRadius * focusDirectPokeRadius;
            var list = MonsterUnit.Active;

            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                if (m == null || !m.IsAlive) continue;
                if (IsMonsterControlled(m)) continue;

                Vector3 aim = m.AimPoint;
                bool priority = IsPriorityThreat(m);

                // 1. Direct index fingertip point/poke right above or onto the enemy
                float tipDistSq = (aim - indexTip).sqrMagnitude;
                if (tipDistSq <= directR2)
                {
                    float s = 100f - Mathf.Sqrt(tipDistSq) * 100f + (priority ? 50f : 0f);
                    if (s > bestScore)
                    {
                        bestScore = s;
                        best = m;
                        isDirectPoke = true;
                    }
                    continue;
                }

                // 2. Ray cone pointing along index finger (or ISDK pointer ray)
                Vector3 toEnemy = aim - indexTip;
                float dist = toEnemy.magnitude;
                if (dist > focusMaxDistance || dist < 1e-4f) continue;

                float angleFinger = Vector3.Angle(fingerDir, toEnemy / dist);
                float angleRay = hasIsdkRay ? Vector3.Angle(isdkRay.direction, (aim - isdkRay.origin).normalized) : 180f;
                float angle = Mathf.Min(angleFinger, angleRay);

                float maxCone = priority ? focusPointConeDeg : focusPointConeDeg * 0.55f;
                if (angle > maxCone) continue;

                // Prioritize dangerous enemies (Ram, Engineer, Boss, Catapult, Flying) heavily within the aim cone
                float score = (maxCone - angle) * 3f - dist * 1.5f + (priority ? 45f : 0f);
                if (m.IsRam || (m.health != null && m.health.Max >= MonsterUnit.RamHp)) score += 10f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = m;
                    isDirectPoke = false;
                }
            }

            return best;
        }

        /// <summary>
        /// Explicitly locks Focus Fire onto `target`, placing the glowing reticle above it and triggering a synchronized
        /// ArcherTower volley if off cooldown.
        /// </summary>
        public void SetFocusTarget(MonsterUnit target, bool triggerVolley = true)
        {
            if (target == null || !target.IsAlive) return;

            bool isNewTarget = !IsFocusTargetValid() || focusedTarget != target || focusedSerial != target.Serial;
            focusedTarget = target;
            focusedSerial = target.Serial;
            focusExpireTime = Time.time + focusDuration;

            if (reticleRoot != null && !reticleRoot.gameObject.activeSelf)
                reticleRoot.gameObject.SetActive(true);
            if (reticleGroundRing != null && !reticleGroundRing.gameObject.activeSelf)
                reticleGroundRing.gameObject.SetActive(true);

            bool volleyReady = Time.time - lastVolleyTime >= focusVolleyCooldown;
            if (isNewTarget)
            {
                if (VfxPool.Instance != null)
                    VfxPool.Instance.Ring(target.transform.position, Mathf.Max(0.06f, target.Radius * 2.8f));
                ProceduralAudio.Fire(Sfx.Click, target.AimPoint);
                FocusTargetChanged?.Invoke(target);
            }

            if (triggerVolley && (isNewTarget || volleyReady) && Time.time - lastVolleyTime >= 0.75f)
            {
                QueueSynchronizedVolley(target);
            }
        }

        void QueueSynchronizedVolley(MonsterUnit target)
        {
            if (target == null || !target.IsAlive) return;
            pendingVolleyTowers.Clear();

            var units = PlacedUnit.All;
            Vector3 targetPos = target.transform.position;
            int queued = 0;

            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.type != UnitType.ArcherTower || u.def == null || u.health == null || u.health.IsDead)
                    continue;

                float effRange = u.def.range * Mathf.Max(1f, focusRangeBonusMul);
                if ((u.transform.position - targetPos).sqrMagnitude <= effRange * effRange)
                {
                    pendingVolleyTowers.Add(u.GetHashCode());
                    queued++;
                }
            }

            if (queued > 0)
            {
                lastVolleyTime = Time.time;
            }
        }

        void FlushPendingVolley()
        {
            if (pendingVolleyTowers.Count == 0) return;
            if (!IsFocusTargetValid())
            {
                pendingVolleyTowers.Clear();
                return;
            }

            var target = focusedTarget;
            var units = PlacedUnit.All;
            int firedCount = 0;

            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.type != UnitType.ArcherTower || u.def == null || u.health == null || u.health.IsDead)
                    continue;
                if (!pendingVolleyTowers.Remove(u.GetHashCode()))
                    continue;

                Vector3 origin = u.muzzle != null ? u.muzzle.position : u.transform.position + Vector3.up * 0.09f;
                Projectile.Fire(origin, target, u.def.EffectiveDamage);
                u.shots++;
                firedCount++;

                if (VfxPool.Instance != null)
                    VfxPool.Instance.Burst(origin, new Color(1f, 0.85f, 0.25f));
            }

            pendingVolleyTowers.Clear();
            if (firedCount > 0)
            {
                FocusVolleyFired?.Invoke(target, firedCount);
            }
        }

        // ------------------------------------------------------------ procedural visuals (reticle & pinch indicator)
        void BuildReticleVisual()
        {
            var rootGo = new GameObject("FocusFireReticle");
            reticleRoot = rootGo.transform;

            Color crimson = new Color(1f, 0.22f, 0.12f, 1f);
            Color gold = new Color(1f, 0.86f, 0.22f, 1f);
            Color glowDisc = new Color(1f, 0.25f, 0.12f, 0.36f);

            var unlitCrimson = Mats.Unlit(crimson);
            var unlitGold = Mats.Unlit(gold);

            reticleSpinner = BuildingVisuals.NewRoot(reticleRoot, "Spinner");
            var ks = new BuildingVisuals.Kit(reticleSpinner);

            for (int i = 0; i < 4; i++)
            {
                float ang = i * 90f;
                Quaternion q = Quaternion.Euler(0f, ang, 0f);
                // Inward pointing tactical chevron/dagger
                var cone = ks.Cone(q * new Vector3(0f, 0f, 0.030f), 0.0045f, 0.015f, crimson);
                cone.transform.localRotation = q * Quaternion.Euler(-90f, 0f, 0f);
                cone.GetComponent<MeshRenderer>().sharedMaterial = unlitCrimson;

                // Outer frame arc bar
                float arcAng = ang + 45f;
                Quaternion qa = Quaternion.Euler(0f, arcAng, 0f);
                var bar = ks.Box(qa * new Vector3(0f, 0f, 0.027f), new Vector3(0.016f, 0.003f, 0.003f), gold, new Vector3(0f, arcAng, 0f));
                bar.GetComponent<MeshRenderer>().sharedMaterial = unlitGold;
            }

            reticleBeacon = BuildingVisuals.NewRoot(reticleRoot, "Beacon");
            var kb = new BuildingVisuals.Kit(reticleBeacon);
            var pointer = kb.Cone(new Vector3(0f, 0.022f, 0f), 0.007f, 0.020f, gold);
            pointer.transform.localRotation = Quaternion.Euler(180f, 0f, 0f);
            pointer.GetComponent<MeshRenderer>().sharedMaterial = unlitGold;

            var gem = kb.Ball(new Vector3(0f, 0.027f, 0f), 0.009f, crimson);
            gem.GetComponent<MeshRenderer>().sharedMaterial = unlitCrimson;

            // Ground target ring at enemy's feet
            var groundGo = new GameObject("FocusFireGroundRing");
            reticleGroundRing = groundGo.transform;
            var kg = new BuildingVisuals.Kit(reticleGroundRing);
            var disc = kg.CylRot(Vector3.zero, 0.032f, 0.002f, glowDisc, Vector3.zero);
            disc.GetComponent<MeshRenderer>().sharedMaterial = Mats.Transparent(glowDisc);

            reticleRoot.gameObject.SetActive(false);
            reticleGroundRing.gameObject.SetActive(false);
        }

        void BuildPinchVisual()
        {
            var go = new GameObject("PinchHoldIndicator");
            pinchVisualRoot = go.transform;
            var k = new BuildingVisuals.Kit(pinchVisualRoot);
            Color lime = new Color(0.5f, 1f, 0.35f, 1f);
            var mat = Mats.Unlit(lime);
            for (int i = 0; i < 3; i++)
            {
                float a = i * 120f * Mathf.Deg2Rad;
                var orb = k.Ball(new Vector3(Mathf.Cos(a) * 0.018f, 0f, Mathf.Sin(a) * 0.018f), 0.0055f, lime);
                orb.GetComponent<MeshRenderer>().sharedMaterial = mat;
            }
            pinchVisualRoot.gameObject.SetActive(false);
        }

        void UpdateReticleAnimation(float dt)
        {
            if (!IsFocusTargetValid())
            {
                if (reticleRoot != null && reticleRoot.gameObject.activeSelf)
                    reticleRoot.gameObject.SetActive(false);
                if (reticleGroundRing != null && reticleGroundRing.gameObject.activeSelf)
                    reticleGroundRing.gameObject.SetActive(false);
                return;
            }

            var target = focusedTarget;
            float t = Time.time;
            float scaleMul = Mathf.Max(1f, target.transform.localScale.y);
            float heightOffset = (target.IsRam ? 0.055f : 0.042f) * scaleMul;
            reticleRoot.position = target.AimPoint + Vector3.up * heightOffset;

            if (reticleSpinner != null)
            {
                reticleSpinner.Rotate(0f, 165f * dt, 0f, Space.Self);
                float pulse = 1f + Mathf.Sin(t * 9f) * 0.10f;
                reticleSpinner.localScale = Vector3.one * pulse;
            }

            if (reticleBeacon != null)
            {
                reticleBeacon.localPosition = new Vector3(0f, Mathf.Sin(t * 7.5f) * 0.006f, 0f);
                reticleBeacon.Rotate(0f, -120f * dt, 0f, Space.Self);
            }

            if (reticleGroundRing != null)
            {
                Vector3 up = target.Path != null && target.Path.Surface != null
                    ? target.Path.Surface.Pose.up
                    : Vector3.up;
                reticleGroundRing.position = target.transform.position + up * 0.0025f;
                reticleGroundRing.up = up;
                float ringScale = (target.IsRam ? 1.45f : 1.0f) * scaleMul * (1f + Mathf.Sin(t * 7f) * 0.08f);
                reticleGroundRing.localScale = new Vector3(ringScale, 1f, ringScale);
            }
        }
    }
}
