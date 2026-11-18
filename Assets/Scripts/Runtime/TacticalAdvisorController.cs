using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Holographic tactical advisor. During BuildPhase and Wave draws pulsing dashed threat lines from the monster spawn
    /// portals (RoomCornerSpawns, or WaveSpawner points / derived edge points on the fallback table) to the nearest edge
    /// of the table surface, each ending in a small marker. During BuildPhase also shows a holographic countdown ring with
    /// an hourglass next to the Town Hall (numerals only); pinching it (HandRig.IsPinching with the index tip within 6 cm)
    /// calls GameDirector.SkipCountdown().
    /// Self-bootstrapping singleton. Pooled LineRenderers / shared Mats materials; no LINQ/Find/GetComponent in Update.
    /// Owner: Tactical Advisor agent.
    /// </summary>
    /// <summary>Deterministic, shared anchor points for the countdown ring, Golden Age ring and obelisk so that they never
    /// coincide with the Town Hall (footprint 0.085 m), the banner (hall + side*0.13 m, side = up x hallToCamera, cloth up high) or each other.</summary>
    public static class OverlayAnchors
    {
        public const float CountdownDistance = 0.30f;   // m from the hall toward the player (banner is on the perpendicular axis)
        public const float CountdownHeight = 0.12f;     // m above the table
        public const float GoldenRingRadius = 0.26f;    // > hall footprint 0.085 and > banner offset 0.205 (pole base inside the ring)
        public const float ObeliskMinFromHall = 0.25f;
        public const float ObeliskMinFromCountdown = 0.25f;

        public static Vector3 ToPlayer(Vector3 camPos, Vector3 hallPos, Vector3 up)
        {
            Vector3 d = Vector3.ProjectOnPlane(camPos - hallPos, up);
            if (d.sqrMagnitude < 1e-4f) d = Vector3.ProjectOnPlane(Vector3.back, up);
            if (d.sqrMagnitude < 1e-4f) d = Vector3.ProjectOnPlane(Vector3.forward, up);
            return d.normalized;
        }

        /// <summary>Hover point of the countdown ring: CountdownDistance toward the player from the hall, CountdownHeight above the table.</summary>
        public static Vector3 CountdownAnchor(Vector3 camPos, Vector3 hallPos, Vector3 up, ITableSurface surf)
        {
            Vector3 dir = ToPlayer(camPos, hallPos, up);
            Vector3 ground = hallPos;
            for (float dist = CountdownDistance; dist >= 0.2f; dist -= 0.03f)
            {
                Vector3 p = hallPos + dir * dist;
                if (surf == null || surf.IsPointSupported(surf.ProjectOnto(p))) { ground = p; break; }
                ground = hallPos + dir * 0.2f;
            }
            if (surf != null) ground = surf.ProjectOnto(ground);
            return ground + up * CountdownHeight;
        }

        /// <summary>True when an obelisk at p keeps clear of the hall and the countdown ring.</summary>
        public static bool ObeliskClear(Vector3 p, Vector3 camPos, Vector3 hallPos, Vector3 up, ITableSurface surf)
        {
            Vector3 flat = Vector3.ProjectOnPlane(p - hallPos, up);
            if (flat.sqrMagnitude < ObeliskMinFromHall * ObeliskMinFromHall) return false;
            Vector3 cd = CountdownAnchor(camPos, hallPos, up, surf);
            Vector3 cf = Vector3.ProjectOnPlane(p - cd, up);
            return cf.sqrMagnitude >= ObeliskMinFromCountdown * ObeliskMinFromCountdown;
        }
    }

    public class TacticalAdvisorController : MonoBehaviour
    {
        public const int MaxLines = 4;
        public const int DashesPerLine = 12;
        public const int RingSegments = 48;
        public const float RingRadius = 0.07f;
        public const float RingHeight = OverlayAnchors.CountdownHeight;
        public const float RingScale = 0.6f;
        public const float PinchRadius = 0.06f;
        const float PointRefreshSeconds = 0.5f;
        const int PulseSteps = 4;

        static TacticalAdvisorController instance;
        static bool quitting;
        public static TacticalAdvisorController Instance => instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            quitting = false;
            if (instance != null) return;
            var existing = FindFirstObjectByType<TacticalAdvisorController>();
            if (existing != null) { instance = existing; return; }
            new GameObject("TacticalAdvisor").AddComponent<TacticalAdvisorController>();
        }

        // ---- pooled visuals
        sealed class ThreatLine
        {
            public GameObject root;
            public LineRenderer[] dashes;
            public Transform marker;
            public MeshRenderer markerRenderer;
            public Vector3 start, end;
            public bool active;
        }

        readonly ThreatLine[] lines = new ThreatLine[MaxLines];
        readonly Material[] pulseMats = new Material[PulseSteps];
        readonly List<Vector3> sources = new List<Vector3>(MaxLines);

        GameObject ringRoot;
        LineRenderer ringBack, ringArc, hourglass;
        TextMesh numeral;
        MeshRenderer numeralRenderer;
        Material ringBackMat, ringArcMat, ringHotMat;
        Vector3 ringCenter;
        bool ringShown;
        int lastNumeral = -1;

        GameDirector subscribedTo;
        Camera cam;
        float refreshTimer;
        bool pinchWasDown;
        bool built;
        Vector3[] ringPts;
        readonly Vector3[] dashPair = new Vector3[2];

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
        }

        void OnApplicationQuit() => quitting = true;

        void OnDestroy()
        {
            Unsubscribe();
            if (instance == this) instance = null;
        }

        void OnDisable() => Unsubscribe();

        void Unsubscribe()
        {
            if (subscribedTo != null) subscribedTo.GameReset -= OnGameReset;
            subscribedTo = null;
        }

        void OnGameReset()
        {
            HideAll();
            sources.Clear();
            refreshTimer = 0f;
            pinchWasDown = false;
            lastNumeral = -1;
        }

        // ------------------------------------------------------------ build visuals (once)
        void Build()
        {
            built = true;
            for (int i = 0; i < PulseSteps; i++)
            {
                float a = 0.25f + 0.2f * i;
                pulseMats[i] = Mats.Transparent(new Color(1f, 0.25f, 0.2f, a));
            }
            ringBackMat = Mats.Transparent(new Color(0.3f, 0.9f, 1f, 0.25f));
            ringArcMat = Mats.Transparent(new Color(0.4f, 1f, 1f, 0.85f));
            ringHotMat = Mats.Transparent(new Color(1f, 0.85f, 0.3f, 0.9f));

            var markerMat = Mats.Unlit(new Color(1f, 0.35f, 0.25f));
            for (int i = 0; i < MaxLines; i++)
            {
                var tl = new ThreatLine { root = new GameObject("ThreatLine_" + i), dashes = new LineRenderer[DashesPerLine] };
                tl.root.transform.SetParent(transform, false);
                for (int d = 0; d < DashesPerLine; d++)
                    tl.dashes[d] = NewLine(tl.root.transform, "dash", 2, 0.012f, pulseMats[0], false);
                var mk = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                mk.name = "marker";
                var col = mk.GetComponent<Collider>();
                if (col != null) Destroy(col);
                mk.transform.SetParent(tl.root.transform, false);
                mk.transform.localScale = Vector3.one * 0.03f;
                tl.markerRenderer = mk.GetComponent<MeshRenderer>();
                tl.markerRenderer.sharedMaterial = markerMat;
                NoShadows(tl.markerRenderer);
                tl.marker = mk.transform;
                tl.root.SetActive(false);
                lines[i] = tl;
            }

            ringPts = new Vector3[RingSegments + 1];
            ringRoot = new GameObject("CountdownRing");
            ringRoot.transform.SetParent(transform, false);
            ringBack = NewLine(ringRoot.transform, "back", RingSegments + 1, 0.006f, ringBackMat, true);
            ringArc = NewLine(ringRoot.transform, "arc", RingSegments + 1, 0.011f, ringArcMat, true);
            ringBack.loop = false;
            ringArc.loop = false;
            hourglass = NewLine(ringRoot.transform, "hourglass", 5, 0.005f, ringArcMat, true);
            hourglass.loop = false;
            // hourglass bow-tie (local, in the ring plane)
            float w = RingRadius * 0.28f, h = RingRadius * 0.42f;
            hourglass.SetPosition(0, new Vector3(-w, h, 0f));
            hourglass.SetPosition(1, new Vector3(w, h, 0f));
            hourglass.SetPosition(2, new Vector3(-w, -h, 0f));
            hourglass.SetPosition(3, new Vector3(w, -h, 0f));
            hourglass.SetPosition(4, new Vector3(-w, h, 0f));
            for (int i = 0; i <= RingSegments; i++)
            {
                float a = i * Mathf.PI * 2f / RingSegments;
                ringPts[i] = new Vector3(Mathf.Sin(a) * RingRadius, Mathf.Cos(a) * RingRadius, 0f);
                ringBack.SetPosition(i, ringPts[i]);
            }

            var tgo = new GameObject("numeral");
            tgo.transform.SetParent(ringRoot.transform, false);
            tgo.transform.localPosition = new Vector3(0f, -RingRadius * 0.72f, 0f);
            numeral = tgo.AddComponent<TextMesh>();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null) numeral.font = font;
            numeral.anchor = TextAnchor.MiddleCenter;
            numeral.alignment = TextAlignment.Center;
            numeral.characterSize = 0.012f;
            numeral.fontSize = 48;
            numeral.color = new Color(0.8f, 1f, 1f, 1f);
            numeralRenderer = tgo.GetComponent<MeshRenderer>();
            if (numeralRenderer != null && font != null) numeralRenderer.sharedMaterial = font.material;
            if (numeralRenderer != null) NoShadows(numeralRenderer);
            ringRoot.SetActive(false);
        }

        static void NoShadows(Renderer r)
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        static LineRenderer NewLine(Transform parent, string name, int count, float width, Material mat, bool local)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = !local;
            lr.positionCount = count;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.numCapVertices = 2;
            lr.sharedMaterial = mat;
            lr.alignment = LineAlignment.View;
            NoShadows(lr);
            return lr;
        }

        // ------------------------------------------------------------ update
        void Update()
        {
            var gd = GameDirector.Instance;
            if (gd == null) return;
            if (subscribedTo != gd)
            {
                Unsubscribe();
                gd.GameReset += OnGameReset;
                subscribedTo = gd;
            }

            var phase = gd.Phase;
            bool threats = phase == GamePhase.BuildPhase || phase == GamePhase.Wave;
            bool ring = phase == GamePhase.BuildPhase && gd.Hall != null;
            if (!threats && !ring)
            {
                if (built) HideAll();
                pinchWasDown = false;
                return;
            }
            if (!built) Build();
            if (cam == null) cam = Camera.main;

            float t = Time.time;
            if (threats)
            {
                refreshTimer -= Time.deltaTime;
                if (refreshTimer <= 0f)
                {
                    refreshTimer = PointRefreshSeconds;
                    RefreshLines(gd);
                }
                AnimateLines(t, phase == GamePhase.Wave ? 2.2f : 1.2f);
            }
            else if (lines[0] != null && lines[0].root.activeSelf) HideLines();

            if (ring) UpdateRing(gd, t);
            else if (ringRoot != null && ringRoot.activeSelf) { ringRoot.SetActive(false); pinchWasDown = false; }
        }

        void HideLines()
        {
            for (int i = 0; i < MaxLines; i++)
                if (lines[i] != null) { lines[i].active = false; lines[i].root.SetActive(false); }
        }

        void HideAll()
        {
            HideLines();
            if (ringRoot != null) ringRoot.SetActive(false);
        }

        // ------------------------------------------------------------ threat lines
        void RefreshLines(GameDirector gd)
        {
            ITableSurface surf = gd.Surface;
            if (surf == null)
            {
                var reg = SurfaceRegistry.Instance;
                if (reg != null && reg.Surfaces.Count > 0) surf = reg.Surfaces[0];
            }
            if (surf == null) { HideLines(); return; }

            Vector3 hall = gd.Hall != null ? gd.Hall.transform.position : surf.Pose.position;
            sources.Clear();
            var rcs = RoomCornerSpawns.Existing;
            bool room = rcs != null && rcs.HasRoom && rcs.Corners.Count > 0;
            if (room)
            {
                for (int i = 0; i < rcs.Corners.Count && sources.Count < MaxLines; i++) sources.Add(rcs.Corners[i] + Vector3.up * 0.02f);
            }
            else
            {
                var ws = WaveSpawner.Instance;
                var pts = ws != null ? ws.SpawnPoints : null;
                Vector3 up = surf.Pose.up;
                if (pts != null && pts.Count > 0)
                {
                    // WaveSpawner points are on the table near the settlement edge: extend outward so the line reads as an approach.
                    int step = Mathf.Max(1, pts.Count / MaxLines);
                    for (int i = 0; i < pts.Count && sources.Count < MaxLines; i += step)
                        sources.Add(OutwardSource(surf, hall, pts[i], up));
                }
                else
                {
                    Vector3 away = cam != null ? Vector3.ProjectOnPlane(hall - cam.transform.position, up) : Vector3.zero;
                    if (away.sqrMagnitude < 1e-4f) away = Vector3.ProjectOnPlane(surf.Pose.rotation * Vector3.forward, up);
                    away.Normalize();
                    for (int k = -1; k <= 1; k++)
                    {
                        Vector3 dir = Quaternion.AngleAxis(k * 70f, up) * away;
                        sources.Add(OutwardSource(surf, hall, hall + dir * 0.4f, up));
                    }
                }
            }

            for (int i = 0; i < MaxLines; i++)
            {
                var tl = lines[i];
                if (i >= sources.Count) { tl.active = false; tl.root.SetActive(false); continue; }
                Vector3 s = sources[i];
                Vector3 edge = surf.ClosestEdgePoint(room ? s : surf.ProjectOnto(s));
                if (!room)
                {
                    // fallback: the line runs from beyond the edge onto the nearest edge point
                    edge = surf.ClosestEdgePoint(s);
                }
                tl.start = s;
                tl.end = edge;
                tl.active = true;
                tl.root.SetActive(true);
                tl.marker.position = edge + surf.Pose.up * 0.012f;
            }
        }

        static Vector3 OutwardSource(ITableSurface surf, Vector3 hall, Vector3 p, Vector3 up)
        {
            Vector3 edge = surf.ClosestEdgePoint(p);
            Vector3 dir = Vector3.ProjectOnPlane(edge - hall, up);
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.ProjectOnPlane(p - hall, up);
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            return edge + dir.normalized * 0.45f + up * 0.02f;
        }

        void AnimateLines(float t, float speed)
        {
            int step = Mathf.Clamp((int)((Mathf.Sin(t * Mathf.PI * 2f * 0.5f * speed) * 0.5f + 0.5f) * PulseSteps), 0, PulseSteps - 1);
            Material pm = pulseMats[step];
            float march = (t * 0.35f * speed) % 1f;
            for (int i = 0; i < MaxLines; i++)
            {
                var tl = lines[i];
                if (!tl.active) continue;
                Vector3 s = tl.start, e = tl.end;
                float len = Vector3.Distance(s, e);
                float lift = Mathf.Clamp(len * 0.12f, 0.02f, 0.2f);
                // dash i covers [u0, u0 + 0.5/N] of the marching parameter
                for (int d = 0; d < DashesPerLine; d++)
                {
                    float u0 = ((d + march) / DashesPerLine);
                    float u1 = u0 + 0.5f / DashesPerLine;
                    if (u1 > 1f) u1 = 1f;
                    var lr = tl.dashes[d];
                    if (u0 >= 1f) { lr.enabled = false; continue; }
                    lr.enabled = true;
                    dashPair[0] = ArcPoint(s, e, u0, lift);
                    dashPair[1] = ArcPoint(s, e, u1, lift);
                    lr.SetPositions(dashPair);
                    lr.sharedMaterial = pm;
                }
                float ms = 0.03f * (1f + 0.35f * Mathf.Sin(t * 5f * speed + i));
                tl.marker.localScale = new Vector3(ms, ms, ms);
            }
        }

        static Vector3 ArcPoint(Vector3 s, Vector3 e, float u, float lift)
        {
            Vector3 p = Vector3.Lerp(s, e, u);
            p.y += lift * 4f * u * (1f - u);
            return p;
        }

        // ------------------------------------------------------------ countdown ring
        void UpdateRing(GameDirector gd, float t)
        {
            if (!ringRoot.activeSelf) { ringRoot.SetActive(true); ringShown = false; }
            Vector3 hallPos = gd.Hall.transform.position;
            Vector3 camPos = cam != null ? cam.transform.position : hallPos + Vector3.back;
            Vector3 upAxis = gd.Surface != null ? gd.Surface.Pose.up : Vector3.up;
            Vector3 target = OverlayAnchors.CountdownAnchor(camPos, hallPos, upAxis, gd.Surface) + upAxis * (0.008f * Mathf.Sin(t * 2f));
            var tr = ringRoot.transform;
            ringCenter = ringShown ? Vector3.Lerp(ringCenter, target, 1f - Mathf.Exp(-8f * Time.deltaTime)) : target;
            ringShown = true;
            tr.localScale = Vector3.one * RingScale;
            tr.position = ringCenter;
            if (cam != null)
            {
                Vector3 toCam = cam.transform.position - ringCenter;
                if (toCam.sqrMagnitude > 1e-4f) tr.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
            }

            float frac = Mathf.Clamp01(gd.Countdown / GameDirector.BuildDuration);
            int n = Mathf.Clamp(Mathf.CeilToInt(frac * RingSegments), 0, RingSegments);
            if (n < 2) { ringArc.positionCount = 0; }
            else
            {
                ringArc.positionCount = n + 1 > RingSegments + 1 ? RingSegments + 1 : n + 1;
                for (int i = 0; i < ringArc.positionCount; i++) ringArc.SetPosition(i, ringPts[i]);
            }

            int secs = Mathf.Max(0, Mathf.CeilToInt(gd.Countdown));
            if (secs != lastNumeral)
            {
                lastNumeral = secs;
                numeral.text = secs.ToString();
            }

            // pinch / poke detection
            bool near = false, pinching = false;
            var hr = HandRig.Instance;
            if (hr != null)
            {
                for (int s = 0; s < 2; s++)
                {
                    var side = s == 0 ? HandSide.Left : HandSide.Right;
                    if (!hr.TryGetIndexTip(side, out Vector3 tip)) continue;
                    if ((tip - ringCenter).sqrMagnitude > PinchRadius * PinchRadius) continue;
                    near = true;
                    if (hr.IsPinching(side)) pinching = true;
                }
            }
            ringArc.sharedMaterial = near ? ringHotMat : ringArcMat;
            hourglass.sharedMaterial = near ? ringHotMat : ringArcMat;
            if (pinching && !pinchWasDown)
            {
                gd.SkipCountdown();
                if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.WaveStart, ringCenter);
            }
            pinchWasDown = pinching;
        }
    }
}
