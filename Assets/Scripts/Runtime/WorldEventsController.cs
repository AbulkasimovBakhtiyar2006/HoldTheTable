using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Self-bootstrapping world events (no scene wiring):
    ///  1) Golden Age: 8 monster kills in a row without the Town Hall taking damage -> 18 s gold ring around the hall,
    ///     Timber income x2 (extra = TimberRate * dt granted every frame).
    ///  2) Ruin obelisk: during BuildPhase a procedural obelisk stands at the table edge; poking it 5 times with an index
    ///     fingertip (4 cm, 0.3 s debounce) grants +15 timber and removes it (once per wave index).
    /// GameDirector exposes no public timber-grant API (Timber has a private setter), so timber is credited via a cached
    /// delegate to the Timber setter (fallback: the ResourceRefundRequested event delegate). See Grant().
    /// All geometry is built once and reused; no coroutines, no per-frame Find/GetComponent/LINQ.
    /// </summary>
    public sealed class WorldEventsController : MonoBehaviour
    {
        public const int StreakTarget = 8;
        public const float GoldenAgeSeconds = 18f;
        public const int PokesRequired = 5;
        public const float PokeRadius = 0.04f;
        public const float PokeDebounce = 0.3f;
        public const int ObeliskTimber = 15;

        public static WorldEventsController Instance { get; private set; }

        /// <summary>True while the Golden Age bonus is running.</summary>
        public bool GoldenAgeActive => goldenTimer > 0f;
        public float GoldenAgeRemaining => Mathf.Max(0f, goldenTimer);
        public int KillStreak => streak;
        public bool ObeliskActive => obeliskRoot != null && obeliskRoot.gameObject.activeSelf;
        public int ObeliskPokes => pokes;

        /// <summary>Raised with true when Golden Age starts, false when it ends/reset.</summary>
        public static event Action<bool> GoldenAgeChanged;

        // ---- state ----
        GameDirector gd;
        Health hallHealth;
        int streak;
        float goldenTimer;
        int pokes;
        float lastPokeTime = -10f;
        int obeliskWaveDone = -1;     // WaveIndex for which the obelisk was already collected
        float carry;                  // fractional timber carry
        float punch;                  // obelisk hit feedback 0..1

        // ---- reflection hooks (resolved once) ----

        // ---- visuals ----
        LineRenderer ring;
        Transform obeliskRoot, shaft, capstone;
        Transform[] pips;
        Renderer[] pipRenderers;
        Material pipOff, pipOn, stoneMat, runeMat;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            var existing = FindFirstObjectByType<WorldEventsController>();
            if (existing != null) { Instance = existing; return; }
            var go = new GameObject("WorldEventsController");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<WorldEventsController>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; GoldenAgeChanged = null; }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildVisuals();
        }

        void OnEnable() { MonsterUnit.Killed += OnMonsterKilled; }

        void OnDisable()
        {
            MonsterUnit.Killed -= OnMonsterKilled;
            ReleaseAllStuns();
            Unbind();
        }

        void OnDestroy()
        {
            Unbind();
            if (Instance == this) Instance = null;
        }

        // ---------------- binding ----------------

        void Unbind()
        {
            if (gd != null)
            {
                gd.GameReset -= OnGameReset;
                gd.PhaseChanged -= OnPhaseChanged;
            }
            gd = null;
            if (hallHealth != null) hallHealth.Damaged -= OnHallDamaged;
            hallHealth = null;
        }

        void Bind(GameDirector director)
        {
            Unbind();
            gd = director;
            if (gd == null) return;
            gd.GameReset += OnGameReset;
            gd.PhaseChanged += OnPhaseChanged;
        }

        void RefreshHallBinding()
        {
            Health h = gd.Hall != null ? gd.Hall.health : null;
            if (h == hallHealth) return;
            if (hallHealth != null) hallHealth.Damaged -= OnHallDamaged;
            hallHealth = h;
            if (hallHealth != null) hallHealth.Damaged += OnHallDamaged;
        }

        // ---------------- events ----------------

        void OnMonsterKilled(MonsterUnit m)
        {
            if (gd == null || gd.Phase != GamePhase.Wave) return;
            streak++;
            if (streak >= StreakTarget)
            {
                streak = 0;
                StartGoldenAge();
            }
        }

        void OnHallDamaged(Health h) { streak = 0; }

        void OnGameReset()
        {
            streak = 0;
            carry = 0f;
            pokes = 0;
            punch = 0f;
            obeliskWaveDone = -1;
            EndGoldenAge();
            HideObelisk();
            ResetWonder();
            // Hall object may be recreated; force rebind next Update.
            if (hallHealth != null) { hallHealth.Damaged -= OnHallDamaged; hallHealth = null; }
        }

        void OnPhaseChanged(GamePhase p)
        {
            if (p == GamePhase.BuildPhase) TrySpawnObelisk();
            else HideObelisk();
            if (p == GamePhase.Victory || p == GamePhase.Defeat || p == GamePhase.CivilizationSetup || p == GamePhase.HeadquartersPlacement)
            {
                if (p != GamePhase.Victory && p != GamePhase.Defeat) ResetWonder();
                else CancelWonderDrag();
                streak = 0;
                EndGoldenAge();
            }
        }

        // ---------------- Update ----------------

        void Update()
        {
            var cur = GameDirector.Instance;
            if (cur != gd) Bind(cur);
            if (gd == null) return;
            RefreshHallBinding();

            float dt = Time.deltaTime;
            var phase = gd.Phase;

            if (goldenTimer > 0f)
            {
                if (phase == GamePhase.BuildPhase || phase == GamePhase.Wave)
                {
                    Grant(gd.TimberRate * dt);   // doubles the base income (GameDirector already added 1x)
                    goldenTimer -= dt;
                }
                if (goldenTimer <= 0f) EndGoldenAge();
                else UpdateRing();
            }

            if (ObeliskActive) UpdateObelisk(dt);
            UpdateWonder(dt, phase);
        }

        // ---------------- Golden Age ----------------

        void StartGoldenAge()
        {
            bool was = goldenTimer > 0f;
            goldenTimer = GoldenAgeSeconds;
            PositionRing();
            ring.enabled = true;
            if (!was) GoldenAgeChanged?.Invoke(true);
        }

        void EndGoldenAge()
        {
            bool was = goldenTimer > 0f;
            goldenTimer = 0f;
            if (ring != null) ring.enabled = false;
            if (was) GoldenAgeChanged?.Invoke(false);
        }

        void PositionRing()
        {
            var hall = gd != null ? gd.Hall : null;
            if (hall == null) return;
            var t = ring.transform;
            t.SetPositionAndRotation(hall.transform.position + hall.transform.up * 0.012f, hall.transform.rotation);
        }

        void UpdateRing()
        {
            if (ring == null || !ring.enabled) return;
            PositionRing();
            float t = Time.time;
            float fade = Mathf.Clamp01(goldenTimer / 2f);          // shrink out in the last 2 s
            float w = (0.010f + 0.004f * Mathf.Sin(t * 6f)) * fade;
            ring.widthMultiplier = Mathf.Max(0.001f, w);
            float s = 1f + 0.04f * Mathf.Sin(t * 3f);
            ring.transform.localScale = new Vector3(s, 1f, s);
        }

        // ---------------- timber grant ----------------

        /// <summary>Credits timber (fractions are carried) via GameDirector.AddTimber.</summary>
        bool Grant(float amount)
        {
            if (gd == null || amount <= 0f) return false;
            carry += amount;
            int whole = Mathf.FloorToInt(carry);
            if (whole <= 0) return true;
            carry -= whole;
            gd.AddTimber(whole);
            return true;
        }

        // ---------------- obelisk ----------------

        void TrySpawnObelisk()
        {
            if (gd == null || gd.Surface == null) return;
            if (obeliskWaveDone == gd.WaveIndex) return;
            var s = gd.Surface;
            Vector3 center = s.Pose.position;
            Vector3 up = s.Pose.up;
            Vector3 hallPos = gd.Hall != null ? gd.Hall.transform.position : center;

            // Pick an edge direction that rotates with the wave index, away from the hall when possible.
            Vector3 right = Vector3.ProjectOnPlane(s.Pose.rotation * Vector3.right, up).normalized;
            Vector3 fwd = Vector3.Cross(right, up).normalized;
            float ang = (gd.WaveIndex * 97f + 40f) * Mathf.Deg2Rad;
            Vector3 dir = (right * Mathf.Cos(ang) + fwd * Mathf.Sin(ang));
            Vector3 probe = center + dir * 5f;
            Vector3 edge = s.ClosestEdgePoint(probe);
            Vector3 inward = Vector3.ProjectOnPlane(center - edge, up);
            Vector3 pos = edge + (inward.sqrMagnitude > 1e-6f ? inward.normalized * 0.05f : Vector3.zero);
            if (!s.IsPointSupported(pos)) pos = edge + (inward.sqrMagnitude > 1e-6f ? inward.normalized * 0.09f : Vector3.zero);
            if ((pos - hallPos).sqrMagnitude < 0.04f * 0.04f && inward.sqrMagnitude > 1e-6f) pos = edge;
            // keep clear of hall + countdown ring: rotate the edge probe in 30 degree steps until supported and clear
            var camT = Camera.main;
            Vector3 camPos = camT != null ? camT.transform.position : hallPos + Vector3.back;
            if (gd.Hall != null && !OverlayAnchors.ObeliskClear(pos, camPos, hallPos, up, s))
            {
                for (int k = 1; k <= 12; k++)
                {
                    float a2 = ang + k * 30f * Mathf.Deg2Rad;
                    Vector3 d2 = right * Mathf.Cos(a2) + fwd * Mathf.Sin(a2);
                    Vector3 e2 = s.ClosestEdgePoint(center + d2 * 5f);
                    Vector3 in2 = Vector3.ProjectOnPlane(center - e2, up);
                    Vector3 c2 = e2 + (in2.sqrMagnitude > 1e-6f ? in2.normalized * 0.05f : Vector3.zero);
                    if (!s.IsPointSupported(c2)) c2 = e2 + (in2.sqrMagnitude > 1e-6f ? in2.normalized * 0.09f : Vector3.zero);
                    if (s.IsPointSupported(c2) && OverlayAnchors.ObeliskClear(c2, camPos, hallPos, up, s)) { pos = c2; ang = a2; break; }
                }
            }

            pokes = 0;
            punch = 0f;
            obeliskRoot.SetPositionAndRotation(pos, Quaternion.FromToRotation(Vector3.up, up) * Quaternion.Euler(0f, ang * Mathf.Rad2Deg, 0f));
            ResetObeliskVisual();
            obeliskRoot.gameObject.SetActive(true);
        }

        void HideObelisk()
        {
            if (obeliskRoot != null) obeliskRoot.gameObject.SetActive(false);
        }

        void UpdateObelisk(float dt)
        {
            // pulse feedback
            if (punch > 0f) punch = Mathf.Max(0f, punch - dt * 4f);
            float idle = 1f + 0.015f * Mathf.Sin(Time.time * 2.5f);
            float k = 1f + 0.25f * punch;
            shaft.localScale = new Vector3(0.026f * k, 0.10f * idle, 0.026f * k);
            capstone.localRotation = Quaternion.Euler(0f, Time.time * 90f, 0f) * Quaternion.Euler(45f, 0f, 45f);

            var hr = HandRig.Instance;
            if (hr == null) return;
            if (Time.time - lastPokeTime < PokeDebounce) return;

            Vector3 c = obeliskRoot.position + obeliskRoot.up * 0.06f;
            float r2 = PokeRadius * PokeRadius;
            if (hr.TryGetIndexTip(HandSide.Left, out var l) && (l - c).sqrMagnitude <= r2) { RegisterPoke(); return; }
            if (hr.TryGetIndexTip(HandSide.Right, out var rt) && (rt - c).sqrMagnitude <= r2) RegisterPoke();
        }

        void RegisterPoke()
        {
            lastPokeTime = Time.time;
            pokes++;
            punch = 1f;
            for (int i = 0; i < pips.Length; i++)
                pipRenderers[i].sharedMaterial = i < pokes ? pipOn : pipOff;
            if (pokes >= PokesRequired)
            {
                Grant(ObeliskTimber);
                obeliskWaveDone = gd != null ? gd.WaveIndex : 0;
                HideObelisk();
            }
        }

        void ResetObeliskVisual()
        {
            for (int i = 0; i < pips.Length; i++) pipRenderers[i].sharedMaterial = pipOff;
        }

        // =====================================================================================
        // WONDERS: one buildable civilization wonder per game (Jade Bell / Yggdrasil / Tesla Tower).
        // Offered as a floating pinch-token next to the hall; drag it (near pinch, or far ray + pinch) to a valid
        // spot inside the settlement, release to build (timber + gold). Ghost is green/red. Resets on restart.
        // =====================================================================================

        enum WonderKind { None, JadeBell, Yggdrasil, Tesla }
        enum WonderState { Hidden, Offered, Dragging, Building, Active }

        public const int WonderTimber = 70;
        public const int WonderGold = 15;
        public const float WonderFootprint = 0.06f;
        const float WonderGrabRadius = 0.07f;
        const float WonderBuildSeconds = 3.2f;
        const float BellInterval = 10f, BellRadius = 0.40f, BellStun = 2.6f, BellDamage = 1f;
        const float TeslaInterval = 1.2f, TeslaRange = 0.55f, TeslaHop = 0.14f, TeslaDamage = 2.5f;
        const int TeslaChain = 4;
        const float TreeRadiusMin = 0.35f, TreeRadiusMax = 0.60f, TreeGrowSeconds = 120f;

        public bool WonderBuilt => wState == WonderState.Building || wState == WonderState.Active;
        public bool WonderActive => wState == WonderState.Active;

        sealed class WonderParts
        {
            public Transform root, swing, top, pulse;
            public Transform[] canopy;
            public Renderer topRenderer;
            public Material topIdle, topHot;
        }

        WonderState wState = WonderState.Hidden;
        WonderKind wKind = WonderKind.None;
        string wCivId;
        WonderParts tokenParts, placedParts;
        Transform tokenRoot, placedRoot;
        TextMesh tokenLabel;
        PlacementGhost wGhost;
        Vector3 wPos, wUp = Vector3.up, tokenHome;
        HandSide dragSide;
        bool dragFar, dragValid;
        bool[] wPrevPinch = new bool[2];
        float wTimer, wActiveTime, wHammerT, wCooldown;
        readonly List<MonsterUnit> stunned = new List<MonsterUnit>(16);
        readonly List<float> stunEnd = new List<float>(16);
        readonly MonsterUnit[] chain = new MonsterUnit[TeslaChain + 1];
        float healPulseT;
        Material jadeMat, goldMat, woodMat, leafMat, copperMat, glowMat, glowHotMat;
        string lastLabelKey;
        bool wMatsBuilt;

        static WonderKind KindFor(CivilizationDefinition d)
        {
            if (d == null) return WonderKind.JadeBell;
            if (d.IsVerdant) return WonderKind.Yggdrasil;
            if (d.IsIron) return WonderKind.Tesla;
            return WonderKind.JadeBell;   // Celestial / Heavenly Kingdom (default)
        }

        static string WonderName(WonderKind k) => k == WonderKind.Yggdrasil ? Strings.WonderYggdrasilName : k == WonderKind.Tesla ? Strings.WonderTeslaName : Strings.WonderJadeBellName;

        void EnsureWonderMats()
        {
            if (wMatsBuilt) return;
            wMatsBuilt = true;
            jadeMat = Mats.Lit(new Color(0.28f, 0.72f, 0.52f, 1f));
            goldMat = Mats.LitMetallic(new Color(0.95f, 0.78f, 0.25f, 1f));
            woodMat = Mats.Lit(new Color(0.42f, 0.28f, 0.14f, 1f));
            leafMat = Mats.Lit(new Color(0.22f, 0.62f, 0.25f, 1f));
            copperMat = Mats.LitMetallic(new Color(0.72f, 0.42f, 0.22f, 1f));
            glowMat = Mats.Unlit(new Color(0.45f, 0.85f, 1f, 1f));
            glowHotMat = Mats.Unlit(new Color(1f, 1f, 1f, 1f));
        }

        // ---------------- reset / cancel ----------------

        void ResetWonder()
        {
            ReleaseAllStuns();
            CancelWonderDrag();
            if (placedRoot != null) Destroy(placedRoot.gameObject);
            placedRoot = null; placedParts = null;
            if (tokenRoot != null) tokenRoot.gameObject.SetActive(false);
            if (wGhost != null) wGhost.Hide();
            wState = WonderState.Hidden;
            wTimer = 0f; wActiveTime = 0f; wCooldown = 0f; healPulseT = 0f;
            wPrevPinch[0] = wPrevPinch[1] = false;
        }

        void CancelWonderDrag()
        {
            if (wState == WonderState.Dragging) wState = WonderState.Offered;
            if (wGhost != null) wGhost.Hide();
        }

        // ---------------- main tick ----------------

        const bool WonderEnabled = false; // user: the levitating civilization wonder near the hall is removed

        void UpdateWonder(float dt, GamePhase phase)
        {
            if (!WonderEnabled) return;
            PruneStuns();
            bool playing = phase == GamePhase.BuildPhase || phase == GamePhase.Wave || phase == GamePhase.Upgrade;
            var hr = HandRig.Instance;
            bool pl = false, pr = false, risingL = false, risingR = false;
            if (hr != null)
            {
                pl = hr.IsPinching(HandSide.Left); pr = hr.IsPinching(HandSide.Right);
                risingL = pl && !wPrevPinch[0]; risingR = pr && !wPrevPinch[1];
                wPrevPinch[0] = pl; wPrevPinch[1] = pr;
            }

            switch (wState)
            {
                case WonderState.Hidden:
                    if (playing && gd.Hall != null && gd.Surface != null) OfferWonder();
                    break;
                case WonderState.Offered:
                    if (!playing) { if (tokenRoot != null) tokenRoot.gameObject.SetActive(false); wState = WonderState.Hidden; break; }
                    TickToken(dt);
                    if (hr != null) TryBeginDrag(hr, risingL, risingR);
                    break;
                case WonderState.Dragging:
                    TickToken(dt);
                    TickDrag(hr, dt, dragSide == HandSide.Left ? pl : pr);
                    break;
                case WonderState.Building:
                    TickBuild(dt);
                    break;
                case WonderState.Active:
                    wActiveTime += dt;
                    TickWonderVisual(dt);
                    if (phase == GamePhase.Wave || phase == GamePhase.BuildPhase) TickWonderEffect(dt);
                    break;
            }
        }

        // ---------------- offer / token ----------------

        void OfferWonder()
        {
            EnsureWonderMats();
            var def = CivilizationCatalog.Active;
            WonderKind k = KindFor(def);
            if (tokenRoot == null || wCivId != def.id)
            {
                if (tokenRoot != null) Destroy(tokenRoot.gameObject);
                tokenParts = BuildWonder(k, transform, "WonderToken");
                tokenRoot = tokenParts.root;
                tokenRoot.localScale = Vector3.one * 0.5f;
                BuildTokenLabel();
                wCivId = def.id;
                lastLabelKey = null;
            }
            wKind = k;
            if (wGhost == null) wGhost = PlacementGhost.Create("WonderGhost", 0.10f);
            var s = gd.Surface;
            Vector3 up = s.Pose.up;
            Vector3 right = Vector3.ProjectOnPlane(s.Pose.rotation * Vector3.right, up);
            right = right.sqrMagnitude > 1e-6f ? right.normalized : Vector3.right;
            Vector3 hp = gd.Hall.transform.position;
            Vector3 home = s.ProjectOnto(hp + right * 0.22f);
            if (!s.IsPointSupported(home)) home = s.ProjectOnto(hp - right * 0.22f);
            tokenHome = home + up * 0.09f;
            tokenRoot.position = tokenHome;
            wUp = up;
            tokenRoot.gameObject.SetActive(true);
            wState = WonderState.Offered;
        }

        void BuildTokenLabel()
        {
            tokenLabel = null;
            try
            {
                var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (font == null) return;
                var go = new GameObject("WonderLabel");
                go.transform.SetParent(tokenRoot, false);
                go.transform.localPosition = new Vector3(0f, 0.16f, 0f);
                go.transform.localScale = Vector3.one * 2f;   // compensate token 0.5 scale
                var tm = go.AddComponent<TextMesh>();
                tm.font = font;
                tm.fontSize = 48;
                tm.characterSize = 0.0035f;
                tm.anchor = TextAnchor.LowerCenter;
                tm.alignment = TextAlignment.Center;
                tm.color = new Color(1f, 0.92f, 0.6f, 1f);
                go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                tokenLabel = tm;
            }
            catch (Exception) { tokenLabel = null; }
        }

        void TickToken(float dt)
        {
            if (tokenRoot == null) return;
            tokenRoot.Rotate(wUp, 40f * dt, Space.World);
            if (wState == WonderState.Offered)
            {
                float bob = 0.008f * Mathf.Sin(Time.time * 2.2f);
                tokenRoot.position = tokenHome + wUp * bob;
            }
            if (tokenLabel != null)
            {
                bool afford = gd.CanAfford(WonderTimber, WonderGold);
                string key = afford ? "a" : "n";
                if (lastLabelKey != key)
                {
                    lastLabelKey = key;
                    tokenLabel.text = Strings.Format(Strings.WonderOffer, WonderName(wKind), WonderTimber, WonderGold);
                    tokenLabel.color = afford ? new Color(1f, 0.92f, 0.6f, 1f) : new Color(1f, 0.5f, 0.45f, 1f);
                }
                var cam = Camera.main;
                if (cam != null)
                {
                    var lt = tokenLabel.transform;
                    Vector3 d = lt.position - cam.transform.position;
                    if (d.sqrMagnitude > 1e-6f) lt.rotation = Quaternion.LookRotation(d, wUp);
                }
            }
        }

        // ---------------- drag / placement ----------------

        void TryBeginDrag(HandRig hr, bool risingL, bool risingR)
        {
            if (risingL && GrabCheck(hr, HandSide.Left)) return;
            if (risingR) GrabCheck(hr, HandSide.Right);
        }

        bool GrabCheck(HandRig hr, HandSide side)
        {
            Vector3 c = tokenRoot.position + wUp * 0.03f;
            bool near = hr.TryGetIndexTip(side, out var tip) && (tip - c).sqrMagnitude <= WonderGrabRadius * WonderGrabRadius;
            bool far = false;
            if (!near && hr.TryGetRay(side, out var ray))
            {
                Vector3 oc = c - ray.origin;
                float t = Vector3.Dot(oc, ray.direction);
                far = t > 0f && (oc - ray.direction * t).sqrMagnitude <= 0.09f * 0.09f;
            }
            if (!near && !far) return false;
            dragSide = side;
            dragFar = far;
            wState = WonderState.Dragging;
            ProceduralAudio.Fire(Sfx.Click);
            return true;
        }

        void TickDrag(HandRig hr, float dt, bool stillPinching)
        {
            var s = gd.Surface;
            if (hr == null || s == null || gd.Hall == null) { CancelWonderDrag(); return; }
            Vector3 up = s.Pose.up;
            Vector3 hand;
            bool have;
            if (dragFar)
            {
                have = hr.TryGetRay(dragSide, out var ray);
                hand = ray.origin;
                if (have)
                {
                    var plane = new Plane(up, s.Pose.position);
                    if (plane.Raycast(ray, out float d)) hand = ray.origin + ray.direction * d;
                    else have = false;
                }
            }
            else have = hr.TryGetIndexTip(dragSide, out hand);
            if (!have) { if (!stillPinching) ReleaseDrag(false); return; }

            Vector3 ground = s.ProjectOnto(hand);
            tokenRoot.position = Vector3.Lerp(tokenRoot.position, dragFar ? ground + up * 0.06f : hand + up * 0.02f, 1f - Mathf.Exp(-18f * dt));
            dragValid = ValidWonderSpot(ground, out _);
            wGhost.Show(ground, up, WonderFootprint, dragValid);
            wPos = ground; wUp = up;
            if (!stillPinching) ReleaseDrag(true);
        }

        bool ValidWonderSpot(Vector3 p, out string reason)
        {
            reason = null;
            var area = SettlementBuildArea.Instance;
            if (area == null || !area.IsFounded) { reason = Strings.PlacementInvalidOutside; return false; }
            if (!area.CanPlace(p, WonderFootprint, out reason))
            {
                if (reason == Strings.PlacementInvalidLimit) { reason = null; return true; }   // wonder ignores the defense cap
                return false;
            }
            return true;
        }

        void ReleaseDrag(bool tryPlace)
        {
            wGhost.Hide();
            if (tryPlace && dragValid)
            {
                if (gd.TrySpend(WonderTimber, WonderGold)) { BeginBuild(); return; }
                ProceduralAudio.Fire(Sfx.Deny);
            }
            else if (tryPlace) ProceduralAudio.Fire(Sfx.Deny);
            tokenRoot.position = tokenHome;
            wState = WonderState.Offered;
        }

        // ---------------- build-up ----------------

        void BeginBuild()
        {
            tokenRoot.gameObject.SetActive(false);
            if (placedRoot != null) Destroy(placedRoot.gameObject);
            placedParts = BuildWonder(wKind, transform, "WonderBuilt");
            placedRoot = placedParts.root;
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, wUp);
            placedRoot.SetPositionAndRotation(wPos, rot);
            placedRoot.localScale = new Vector3(1f, 0.02f, 1f);
            var area = SettlementBuildArea.Instance;
            if (area != null) area.RegisterPlacement(wPos, WonderFootprint);
            wTimer = 0f; wHammerT = 0f; wActiveTime = 0f; wCooldown = wKind == WonderKind.JadeBell ? BellInterval * 0.6f : 0.8f;
            wState = WonderState.Building;
            ProceduralAudio.Fire(Sfx.Place, wPos);
            CivilizationPassives.SpawnPulseRing(wPos + wUp * 0.004f, 0.15f, new Color(1f, 0.85f, 0.4f), 0.6f);
        }

        void TickBuild(float dt)
        {
            wTimer += dt;
            float u = Mathf.Clamp01(wTimer / WonderBuildSeconds);
            float e = 1f - Mathf.Pow(1f - u, 3f);
            float over = 1f + 0.08f * Mathf.Sin(u * Mathf.PI);
            placedRoot.localScale = new Vector3(Mathf.Lerp(0.6f, 1f, e), Mathf.Max(0.02f, e * over), Mathf.Lerp(0.6f, 1f, e));
            wHammerT -= dt;
            if (wHammerT <= 0f)
            {
                wHammerT = 0.45f;
                ProceduralAudio.Fire(Sfx.Hammer, wPos);
                if (VfxPool.Instance != null) VfxPool.Instance.Burst(wPos + wUp * 0.02f + UnityEngine.Random.insideUnitSphere * 0.02f, new Color(1f, 0.85f, 0.4f));
            }
            if (u >= 1f)
            {
                placedRoot.localScale = Vector3.one;
                wState = WonderState.Active;
                ProceduralAudio.Fire(Sfx.Horn, wPos);
                if (VfxPool.Instance != null) { VfxPool.Instance.Ring(wPos, 0.2f); VfxPool.Instance.Burst(wPos + wUp * 0.08f, new Color(1f, 0.9f, 0.5f)); }
                CivilizationPassives.SpawnPulseRing(wPos + wUp * 0.004f, 0.3f, new Color(1f, 0.9f, 0.5f), 0.8f);
            }
        }

        // ---------------- active visuals ----------------

        void TickWonderVisual(float dt)
        {
            if (placedParts == null || placedRoot == null) return;
            float t = Time.time;
            if (wKind == WonderKind.JadeBell)
            {
                float charge = 1f - Mathf.Clamp01(wCooldown / BellInterval);
                float amp = 2f + 10f * charge * charge;
                if (placedParts.swing != null) placedParts.swing.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(t * 2.4f) * amp);
            }
            else if (wKind == WonderKind.Yggdrasil)
            {
                float g = Mathf.Clamp01(wActiveTime / TreeGrowSeconds);
                float s = Mathf.Lerp(1f, 1.7f, g);
                placedRoot.localScale = new Vector3(s, s, s);
                if (placedParts.canopy != null)
                    for (int i = 0; i < placedParts.canopy.Length; i++)
                    {
                        var c = placedParts.canopy[i];
                        if (c != null) c.localRotation = Quaternion.Euler(Mathf.Sin(t * 0.9f + i) * 3f, 0f, Mathf.Cos(t * 0.8f + i * 1.7f) * 3f);
                    }
            }
            else if (wKind == WonderKind.Tesla)
            {
                if (placedParts.top != null) placedParts.top.localRotation = Quaternion.Euler(0f, t * 120f, 0f);
            }
        }

        // ---------------- active effects ----------------

        void TickWonderEffect(float dt)
        {
            wCooldown -= dt;
            switch (wKind)
            {
                case WonderKind.JadeBell:
                    if (wCooldown <= 0f) { wCooldown = BellInterval; BellToll(); }
                    break;
                case WonderKind.Yggdrasil:
                    TreeHeal(dt);
                    break;
                case WonderKind.Tesla:
                    if (wCooldown <= 0f) { wCooldown = TeslaInterval; if (TeslaStrike()) wCooldown = TeslaInterval; else wCooldown = 0.25f; }
                    SetTeslaGlow(wCooldown > TeslaInterval - 0.14f);
                    break;
            }
        }

        void SetTeslaGlow(bool hot)
        {
            if (placedParts == null || placedParts.topRenderer == null) return;
            var m = hot ? placedParts.topHot : placedParts.topIdle;
            if (placedParts.topRenderer.sharedMaterial != m) placedParts.topRenderer.sharedMaterial = m;
        }

        // Jade Bell: AoE stun (component disabled; auto-released by PruneStuns).
        void BellToll()
        {
            ProceduralAudio.Fire(Sfx.Horn, wPos);
            ProceduralAudio.Fire(Sfx.Shockwave, wPos);
            if (VfxPool.Instance != null) VfxPool.Instance.Ring(wPos, BellRadius);
            CivilizationPassives.SpawnPulseRing(wPos + wUp * 0.004f, BellRadius, new Color(0.4f, 1f, 0.75f), 0.7f);
            float r2 = BellRadius * BellRadius;
            var list = MonsterUnit.Active;
            float until = Time.time + BellStun;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var m = list[i];
                if (m == null || !m.IsAlive) continue;
                if ((m.transform.position - wPos).sqrMagnitude > r2) continue;
                if (VfxPool.Instance != null) VfxPool.Instance.Burst(m.AimPoint, new Color(0.4f, 1f, 0.75f));
                int idx = stunned.IndexOf(m);
                if (idx >= 0) stunEnd[idx] = until;
                else if (m.enabled) { m.enabled = false; stunned.Add(m); stunEnd.Add(until); }
                m.health.Damage(BellDamage);
            }
        }

        // A stunned monster is released when its timer ends, it dies, is pooled, or the game resets.
        void PruneStuns()
        {
            if (stunned.Count == 0) return;
            float now = Time.time;
            for (int i = stunned.Count - 1; i >= 0; i--)
            {
                var m = stunned[i];
                bool drop = m == null || now >= stunEnd[i] || !m.gameObject.activeInHierarchy || !m.IsAlive;
                if (!drop) continue;
                if (m != null) m.enabled = true;
                stunned.RemoveAt(i);
                stunEnd.RemoveAt(i);
            }
        }

        void ReleaseAllStuns()
        {
            for (int i = 0; i < stunned.Count; i++) if (stunned[i] != null) stunned[i].enabled = true;
            stunned.Clear();
            stunEnd.Clear();
        }

        // Yggdrasil: heal everything player-owned in a radius that grows with the tree.
        void TreeHeal(float dt)
        {
            float g = Mathf.Clamp01(wActiveTime / TreeGrowSeconds);
            float radius = Mathf.Lerp(TreeRadiusMin, TreeRadiusMax, g);
            float r2 = radius * radius;
            float rate = Mathf.Lerp(1.2f, 2.6f, g);
            float amt = rate * dt;
            var units = PlacedUnit.All;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.health == null) continue;
                if ((u.transform.position - wPos).sqrMagnitude <= r2) u.health.Heal(amt);
            }
            var defs = DefenderUnit.Active;
            for (int i = 0; i < defs.Count; i++)
            {
                var d = defs[i];
                if (d == null || !d.IsAlive) continue;
                if ((d.transform.position - wPos).sqrMagnitude <= r2) d.health.Heal(amt);
            }
            var hall = gd.Hall;
            if (hall != null && hall.health != null && (hall.transform.position - wPos).sqrMagnitude <= r2) hall.health.Heal(amt * 0.7f);

            healPulseT -= dt;
            if (healPulseT <= 0f)
            {
                healPulseT = 2.5f;
                CivilizationPassives.SpawnPulseRing(wPos + wUp * 0.004f, radius, new Color(0.35f, 0.95f, 0.4f), 0.9f);
                if (VfxPool.Instance != null)
                    VfxPool.Instance.Burst(wPos + wUp * Mathf.Lerp(0.12f, 0.2f, g), new Color(0.4f, 0.95f, 0.4f));
                ProceduralAudio.Fire(Sfx.Harvest, wPos);
            }
        }

        // Tesla Tower: chain lightning from the coil tip through up to TeslaChain monsters.
        bool TeslaStrike()
        {
            Vector3 tip = wPos + wUp * 0.15f;
            var list = MonsterUnit.Active;
            MonsterUnit first = null;
            float best = TeslaRange * TeslaRange;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                if (m == null || !m.IsAlive) continue;
                float d = (m.AimPoint - tip).sqrMagnitude;
                if (d < best) { best = d; first = m; }
            }
            if (first == null) return false;
            int n = 0;
            chain[n++] = first;
            Vector3 from = tip;
            MonsterUnit cur = first;
            float dmg = TeslaDamage;
            ProceduralAudio.Fire(Sfx.Hit, tip);
            for (int hop = 0; hop < TeslaChain && cur != null; hop++)
            {
                CivilizationPassives.SpawnLightningArc(from, cur.AimPoint, 0.22f);
                if (VfxPool.Instance != null) VfxPool.Instance.Burst(cur.AimPoint, new Color(0.5f, 0.9f, 1f));
                var h = cur.health;
                from = cur.AimPoint;
                if (h != null) h.Damage(dmg);
                dmg *= 0.8f;
                MonsterUnit next = null;
                float nb = TeslaHop * TeslaHop;
                for (int i = 0; i < list.Count; i++)
                {
                    var m = list[i];
                    if (m == null || !m.IsAlive) continue;
                    bool used = false;
                    for (int k = 0; k < n; k++) if (chain[k] == m) { used = true; break; }
                    if (used) continue;
                    float d = (m.AimPoint - from).sqrMagnitude;
                    if (d < nb) { nb = d; next = m; }
                }
                if (next != null && n < chain.Length) chain[n++] = next;
                cur = next;
            }
            for (int k = 0; k < chain.Length; k++) chain[k] = null;
            return true;
        }

        // ---------------- wonder geometry ----------------

        WonderParts BuildWonder(WonderKind k, Transform parent, string name)
        {
            EnsureWonderMats();
            var p = new WonderParts();
            var root = new GameObject(name).transform;
            root.SetParent(parent, false);
            p.root = root;
            switch (k)
            {
                case WonderKind.Yggdrasil: BuildTree(p); break;
                case WonderKind.Tesla: BuildTesla(p); break;
                default: BuildBell(p); break;
            }
            return p;
        }

        void BuildBell(WonderParts p)
        {
            var r = p.root;
            Prim(PrimitiveType.Cylinder, r, new Vector3(0f, 0.006f, 0f), new Vector3(0.12f, 0.006f, 0.12f), jadeMat);
            Prim(PrimitiveType.Cube, r, new Vector3(-0.034f, 0.05f, 0f), new Vector3(0.008f, 0.09f, 0.008f), woodMat);
            Prim(PrimitiveType.Cube, r, new Vector3(0.034f, 0.05f, 0f), new Vector3(0.008f, 0.09f, 0.008f), woodMat);
            Prim(PrimitiveType.Cube, r, new Vector3(0f, 0.097f, 0f), new Vector3(0.084f, 0.008f, 0.012f), woodMat);
            Prim(PrimitiveType.Cube, r, new Vector3(0f, 0.108f, 0f), new Vector3(0.10f, 0.007f, 0.03f), goldMat);
            Prim(PrimitiveType.Cube, r, new Vector3(0f, 0.116f, 0f), new Vector3(0.06f, 0.007f, 0.022f), goldMat);
            var swing = new GameObject("BellSwing").transform;
            swing.SetParent(r, false);
            swing.localPosition = new Vector3(0f, 0.093f, 0f);
            p.swing = swing;
            Prim(PrimitiveType.Cylinder, swing, new Vector3(0f, -0.006f, 0f), new Vector3(0.004f, 0.006f, 0.004f), goldMat);
            Prim(PrimitiveType.Sphere, swing, new Vector3(0f, -0.034f, 0f), new Vector3(0.05f, 0.052f, 0.05f), jadeMat);
            Prim(PrimitiveType.Cylinder, swing, new Vector3(0f, -0.052f, 0f), new Vector3(0.056f, 0.006f, 0.056f), goldMat);
            Prim(PrimitiveType.Sphere, swing, new Vector3(0f, -0.058f, 0f), new Vector3(0.011f, 0.011f, 0.011f), goldMat);
        }

        void BuildTree(WonderParts p)
        {
            var r = p.root;
            Prim(PrimitiveType.Cylinder, r, new Vector3(0f, 0.004f, 0f), new Vector3(0.11f, 0.004f, 0.11f), leafMat);
            Prim(PrimitiveType.Cylinder, r, new Vector3(0f, 0.04f, 0f), new Vector3(0.016f, 0.04f, 0.016f), woodMat);
            for (int i = 0; i < 5; i++)
            {
                float a = i * 72f;
                var b = Prim(PrimitiveType.Cylinder, r, Vector3.zero, new Vector3(0.007f, 0.03f, 0.007f), woodMat);
                b.localRotation = Quaternion.Euler(0f, a, 0f) * Quaternion.Euler(0f, 0f, 38f);
                b.localPosition = Quaternion.Euler(0f, a, 0f) * new Vector3(0.016f, 0.094f, 0f);
                var rt = Prim(PrimitiveType.Cylinder, r, Vector3.zero, new Vector3(0.007f, 0.02f, 0.007f), woodMat);
                rt.localRotation = Quaternion.Euler(0f, a + 36f, 0f) * Quaternion.Euler(0f, 0f, 70f);
                rt.localPosition = Quaternion.Euler(0f, a + 36f, 0f) * new Vector3(0.022f, 0.008f, 0f);
            }
            var canopy = new Transform[6];
            var cRoot = new GameObject("Canopy").transform;
            cRoot.SetParent(r, false);
            cRoot.localPosition = new Vector3(0f, 0.11f, 0f);
            p.pulse = cRoot;
            Vector3[] offs = { new Vector3(0f, 0.04f, 0f), new Vector3(0.035f, 0.008f, 0.01f), new Vector3(-0.03f, 0.012f, 0.02f), new Vector3(0.01f, 0.01f, -0.036f), new Vector3(-0.02f, 0.03f, -0.025f), new Vector3(0.025f, 0.032f, 0.025f) };
            float[] sz = { 0.07f, 0.055f, 0.055f, 0.05f, 0.048f, 0.048f };
            for (int i = 0; i < canopy.Length; i++)
            {
                var piv = new GameObject("Leaf" + i).transform;
                piv.SetParent(cRoot, false);
                piv.localPosition = offs[i];
                Prim(PrimitiveType.Sphere, piv, Vector3.zero, new Vector3(sz[i], sz[i] * 0.8f, sz[i]), i % 2 == 0 ? leafMat : jadeMat);
                canopy[i] = piv;
            }
            p.canopy = canopy;
        }

        void BuildTesla(WonderParts p)
        {
            var r = p.root;
            Prim(PrimitiveType.Cylinder, r, new Vector3(0f, 0.006f, 0f), new Vector3(0.11f, 0.006f, 0.11f), Mats.LitMetallic(new Color(0.42f, 0.45f, 0.5f, 1f)));
            Prim(PrimitiveType.Cylinder, r, new Vector3(0f, 0.03f, 0f), new Vector3(0.05f, 0.02f, 0.05f), copperMat);
            Prim(PrimitiveType.Cylinder, r, new Vector3(0f, 0.07f, 0f), new Vector3(0.034f, 0.028f, 0.034f), Mats.LitMetallic(new Color(0.5f, 0.53f, 0.58f, 1f)));
            Prim(PrimitiveType.Cylinder, r, new Vector3(0f, 0.11f, 0f), new Vector3(0.022f, 0.026f, 0.022f), copperMat);
            for (int i = 0; i < 3; i++)
                Prim(PrimitiveType.Cylinder, r, new Vector3(0f, 0.05f + i * 0.035f, 0f), new Vector3(0.05f - i * 0.01f, 0.003f, 0.05f - i * 0.01f), goldMat);
            for (int i = 0; i < 4; i++)
            {
                float a = i * 90f * Mathf.Deg2Rad;
                Vector3 pos = new Vector3(Mathf.Cos(a) * 0.04f, 0.045f, Mathf.Sin(a) * 0.04f);
                Prim(PrimitiveType.Cylinder, r, pos, new Vector3(0.007f, 0.025f, 0.007f), copperMat);
                Prim(PrimitiveType.Sphere, r, pos + Vector3.up * 0.03f, Vector3.one * 0.012f, glowMat);
            }
            var top = new GameObject("TeslaTop").transform;
            top.SetParent(r, false);
            top.localPosition = new Vector3(0f, 0.15f, 0f);
            p.top = top;
            var orb = Prim(PrimitiveType.Sphere, top, Vector3.zero, Vector3.one * 0.03f, glowMat);
            p.topRenderer = orb.GetComponent<Renderer>();
            p.topIdle = glowMat;
            p.topHot = glowHotMat;
            for (int i = 0; i < 3; i++)
            {
                var rg = Prim(PrimitiveType.Cylinder, top, new Vector3(0f, -0.012f + i * 0.012f, 0f), new Vector3(0.05f - i * 0.012f, 0.0015f, 0.05f - i * 0.012f), goldMat);
                rg.localRotation = Quaternion.Euler(i * 12f, 0f, i * 9f);
            }
        }

        // ---------------- procedural geometry (built once) ----------------

        static Transform Prim(PrimitiveType type, Transform parent, Vector3 lp, Vector3 ls, Material m)
        {
            var go = GameObject.CreatePrimitive(type);
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = lp;
            t.localScale = ls;
            go.GetComponent<Renderer>().sharedMaterial = m;
            return t;
        }

        void BuildVisuals()
        {
            Color gold = new Color(1f, 0.82f, 0.25f, 1f);
            stoneMat = Mats.Lit(new Color(0.42f, 0.41f, 0.39f, 1f));
            runeMat = Mats.Unlit(gold);
            pipOff = Mats.Unlit(new Color(0.25f, 0.25f, 0.28f, 1f));
            pipOn = Mats.Unlit(gold);

            // Golden Age ring: closed loop of 48 points, local XZ plane, radius around the hall footprint.
            var ringGo = new GameObject("GoldenRing");
            ringGo.transform.SetParent(transform, false);
            ring = ringGo.AddComponent<LineRenderer>();
            ring.useWorldSpace = false;
            ring.loop = true;
            const int n = 48;
            ring.positionCount = n;
            float radius = OverlayAnchors.GoldenRingRadius;
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                ring.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius));
            }
            ring.widthMultiplier = 0.01f;
            ring.sharedMaterial = Mats.Unlit(gold);
            ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ring.receiveShadows = false;
            ring.enabled = false;

            // Obelisk: base slab, tapered shaft, rotating gold capstone, 5 progress pips on the base.
            var root = new GameObject("RuinObelisk").transform;
            root.SetParent(transform, false);
            obeliskRoot = root;
            Prim(PrimitiveType.Cube, root, new Vector3(0f, 0.008f, 0f), new Vector3(0.06f, 0.016f, 0.06f), stoneMat);
            shaft = Prim(PrimitiveType.Cube, root, new Vector3(0f, 0.016f + 0.05f, 0f), new Vector3(0.026f, 0.10f, 0.026f), stoneMat);
            capstone = Prim(PrimitiveType.Cube, root, new Vector3(0f, 0.016f + 0.10f + 0.016f, 0f), new Vector3(0.022f, 0.022f, 0.022f), runeMat);
            capstone.localRotation = Quaternion.Euler(45f, 0f, 45f);

            pips = new Transform[PokesRequired];
            pipRenderers = new Renderer[PokesRequired];
            for (int i = 0; i < PokesRequired; i++)
            {
                float x = (i - (PokesRequired - 1) * 0.5f) * 0.011f;
                pips[i] = Prim(PrimitiveType.Sphere, root, new Vector3(x, 0.02f, 0.032f), Vector3.one * 0.007f, pipOff);
                pipRenderers[i] = pips[i].GetComponent<Renderer>();
            }
            root.gameObject.SetActive(false);
        }
    }
}
