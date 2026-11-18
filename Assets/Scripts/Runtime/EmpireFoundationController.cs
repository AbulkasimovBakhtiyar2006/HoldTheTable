using System;
using TMPro;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Founding of the empire (Agent A). In GamePhase.HeadquartersPlacement the Town Hall token (ISDK Grabbable placed
    /// by the leader) sits at ImperialScroll.TokenSocket showing BuildingFactory.BuildMiniTownHall for the chosen civ.
    /// Near: grab the token, a green/red ghost appears on the surface under it, release on valid ground confirms.
    /// Far: dominant-hand ray onto any registered room surface + pinch (rising edge) confirms.
    /// Exactly one hall: confirm is guarded and deferred out of ISDK event callbacks.
    /// </summary>
    public class EmpireFoundationController : MonoBehaviour
    {
        public static EmpireFoundationController Instance { get; private set; }

        public const float HallRadius = 0.12f;
        public const float HallHeight = 0.15f;

        [Tooltip("Scene token for the first Town Hall (ISDK Grabbable + HandGrabInteractable added by the leader).")]
        public GameObject hqToken;
        [Tooltip("Root of the 3 TraySlots; moved beside the settlement after founding.")]
        public Transform trayRoot;
        [Tooltip("Max hand-to-surface distance for near placement.")]
        public float nearMaxDistance = 0.35f;
        [Tooltip("Max ray length for far placement.")]
        public float farMaxDistance = 6f;
        [Tooltip("Distance from the hall toward the player at which the tray is placed.")]
        public float trayOffset = 0.25f;

        public bool IsFounded => confirmed;
        public Vector3 HallPosition { get; private set; }
        public ITableSurface FoundedSurface { get; private set; }
        /// <summary>Raised after the hall was spawned and GameDirector.OnHeadquartersPlaced was called.</summary>
        public event Action<Vector3, ITableSurface> Founded;

        GrabWatcher watcher;
        PlacementGhost ghost;
        TextMeshPro hint;
        GameObject miniVisual;
        string builtCivId;
        bool placing;
        bool confirmed;
        bool subscribed;
        readonly bool[] prevPinch = new bool[2];
        float farValidSince = -1f;
        string hintShown;

        bool pendingConfirm;
        Vector3 pendingPos;
        ITableSurface pendingSurface;

        void Awake()
        {
            Instance = this;
            ghost = PlacementGhost.Create("HallGhost", HallHeight);
            hint = CreateHint();
            if (hqToken != null)
            {
                watcher = hqToken.GetComponent<GrabWatcher>();
                if (watcher == null) watcher = hqToken.AddComponent<GrabWatcher>();
            }
            else Debug.LogError("[EmpireFoundationController] hqToken not assigned");
        }

        TextMeshPro CreateHint()
        {
            var go = new GameObject("FoundationHint");
            var t = go.AddComponent<TextMeshPro>();
            t.fontSize = 0.22f;
            t.alignment = TextAlignmentOptions.Center;
            t.rectTransform.sizeDelta = new Vector2(0.5f, 0.08f);
            t.color = new Color(1f, 0.5f, 0.45f);
            t.textWrappingMode = TextWrappingModes.Normal;
            go.SetActive(false);
            return t;
        }

        void Start()
        {
            TrySubscribe();
            if (hqToken != null && !placing) hqToken.SetActive(false);
        }

        void TrySubscribe()
        {
            if (subscribed) return;
            var gd = GameDirector.Instance;
            if (gd == null) return;
            gd.PhaseChanged += OnPhaseChanged;
            gd.GameReset += OnGameReset;
            CivilizationProfile.Changed += OnProfileChanged;
            if (watcher != null)
            {
                watcher.Grabbed += OnTokenGrabbed;
                watcher.Released += OnTokenReleased;
            }
            subscribed = true;
            OnPhaseChanged(gd.Phase);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (subscribed)
            {
                var gd = GameDirector.Instance;
                if (gd != null)
                {
                    gd.PhaseChanged -= OnPhaseChanged;
                    gd.GameReset -= OnGameReset;
                }
                CivilizationProfile.Changed -= OnProfileChanged;
                if (watcher != null)
                {
                    watcher.Grabbed -= OnTokenGrabbed;
                    watcher.Released -= OnTokenReleased;
                }
            }
            if (ghost != null) Destroy(ghost.gameObject);
            if (hint != null) Destroy(hint.gameObject);
        }

        void OnPhaseChanged(GamePhase p)
        {
            if (p == GamePhase.HeadquartersPlacement)
            {
                if (confirmed && GameDirector.Instance != null && GameDirector.Instance.Hall != null) return; // already founded
                confirmed = false;
                pendingConfirm = false;
                placing = true;
                EnsureMiniVisual();
                if (hqToken != null)
                {
                    hqToken.SetActive(true);
                    FollowSocket();
                }
                farValidSince = -1f;
                // a pinch still held from the scroll's Found Empire press must not count as a fresh far-confirm
                var rig = HandRig.Instance;
                prevPinch[0] = rig == null || rig.IsPinching(HandSide.Left);
                prevPinch[1] = rig == null || rig.IsPinching(HandSide.Right);
            }
            else
            {
                placing = false;
                pendingConfirm = false;
                if (hqToken != null && hqToken.activeSelf) hqToken.SetActive(false);
                HidePreview();
                if (p == GamePhase.CivilizationSetup)
                {
                    // New Empire: the old settlement is gone, a new founding will follow
                    confirmed = false;
                    FoundedSurface = null;
                    if (TableAnchorBootstrap.Instance != null) TableAnchorBootstrap.Instance.ClearSettlementAnchor();
                }
            }
        }

        void OnGameReset()
        {
            // RESTART keeps civilization, hall location and tray placement. Nothing to rebuild here.
            pendingConfirm = false;
            HidePreview();
        }

        void OnProfileChanged()
        {
            if (placing && (watcher == null || !watcher.IsGrabbed)) EnsureMiniVisual();
        }

        static CivilizationDefinition CurrentCiv()
        {
            var prof = CivilizationProfile.Current;
            string id = prof != null && !string.IsNullOrEmpty(prof.civId) ? prof.civId : "celestial";
            return CivilizationCatalog.Get(id);
        }

        void EnsureMiniVisual()
        {
            if (hqToken == null || BuildingFactory.Instance == null) return;
            var civ = CurrentCiv();
            string id = civ != null ? civ.id : null;
            if (miniVisual != null && id == builtCivId) return;
            if (miniVisual != null) Destroy(miniVisual);
            miniVisual = BuildingFactory.Instance.BuildMiniTownHall(hqToken.transform, civ);
            builtCivId = id;
        }

        void FollowSocket()
        {
            if (hqToken == null) return;
            var scroll = ImperialScroll.Instance;
            if (scroll != null && scroll.TokenSocket != null)
            {
                hqToken.transform.SetPositionAndRotation(scroll.TokenSocket.position, scroll.TokenSocket.rotation);
            }
            else if (Camera.main != null)
            {
                var cam = Camera.main.transform;
                Vector3 f = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
                hqToken.transform.SetPositionAndRotation(cam.position + f * 0.35f - Vector3.up * 0.2f, Quaternion.LookRotation(f, Vector3.up));
            }
        }

        /// <summary>Validates a hall footprint at a surface point. reasonKey is a Strings key name.</summary>
        public bool ValidateHall(Vector3 pos, ITableSurface s, out string reasonKey)
        {
            reasonKey = null;
            if (s == null || !s.IsFootprintSupported(pos, HallRadius))
            {
                reasonKey = nameof(Strings.PlacementInvalidSupport);
                return false;
            }
            var reg = SurfaceRegistry.Instance;
            if (reg != null && reg.OverlapsObstacle(pos, HallRadius, HallHeight))
            {
                reasonKey = nameof(Strings.PlacementInvalidObstacle);
                return false;
            }
            return true;
        }

        void Update()
        {
            if (!subscribed) TrySubscribe();

            if (pendingConfirm)
            {
                pendingConfirm = false;
                Confirm(pendingPos, pendingSurface);
                return;
            }

            if (!placing || confirmed || hqToken == null) return;

            if (watcher != null && watcher.IsGrabbed) UpdateNear();
            else UpdateFar();
        }

        void LateUpdate()
        {
            if (placing && !confirmed && hqToken != null && hqToken.activeSelf && (watcher == null || !watcher.IsGrabbed))
                FollowSocket();
        }

        void UpdateNear()
        {
            var reg = SurfaceRegistry.Instance;
            if (reg != null && reg.TryFindSurfaceBelow(hqToken.transform.position, nearMaxDistance, out var s, out var hit))
            {
                bool valid = ValidateHall(hit, s, out var reason);
                ShowPreview(hit, s, valid, reason);
            }
            else HidePreview();
        }

        void UpdateFar()
        {
            var rig = HandRig.Instance;
            var reg = SurfaceRegistry.Instance;
            if (rig == null || reg == null) { HidePreview(); return; }

            bool pinchL = rig.IsPinching(HandSide.Left), pinchR = rig.IsPinching(HandSide.Right);
            bool risingL = pinchL && !prevPinch[0], risingR = pinchR && !prevPinch[1];
            prevPinch[0] = pinchL;
            prevPinch[1] = pinchR;

            HandSide side = rig.Dominant;
            if (!rig.IsTracked(side)) side = rig.NonDominant;
            bool rising = side == HandSide.Left ? risingL : risingR;

            // near grab has priority: a hand close to the token is reaching for it, not aiming far
            if (rig.TryGetIndexTip(side, out var tip) && (tip - hqToken.transform.position).sqrMagnitude < 0.15f * 0.15f)
            {
                HidePreview();
                farValidSince = -1f;
                return;
            }

            if (!rig.TryGetRay(side, out var ray) || !reg.TryRaycast(ray, farMaxDistance, out var s, out var hit) ||
                (hit - ray.origin).sqrMagnitude < 0.3f * 0.3f || RayBlockedByScroll(ray, (hit - ray.origin).magnitude))
            {
                HidePreview();
                farValidSince = -1f;
                return;
            }

            bool valid = ValidateHall(hit, s, out var reason);
            ShowPreview(hit, s, valid, reason);
            if (valid) { if (farValidSince < 0f) farValidSince = Time.time; }
            else farValidSince = -1f;

            if (rising)
            {
                if (valid && Time.time - farValidSince >= 0.15f) Confirm(hit, s);
                else if (!valid && ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Deny);
            }
        }

        static bool RayBlockedByScroll(Ray ray, float maxDist)
        {
            var scroll = ImperialScroll.Instance;
            if (scroll == null || scroll.canvas == null || !scroll.canvas.isActiveAndEnabled) return false;
            var rt = scroll.canvas.transform as RectTransform;
            if (rt == null) return false;
            var plane = new Plane(rt.forward, rt.position);
            if (!plane.Raycast(ray, out float d) || d > maxDist) return false;
            Vector3 local = rt.InverseTransformPoint(ray.GetPoint(d));
            return rt.rect.Contains(new Vector2(local.x, local.y));
        }

        void ShowPreview(Vector3 pos, ITableSurface s, bool valid, string reasonKey)
        {
            ghost.Show(pos, s.Pose.up, HallRadius, valid);
            string text = valid ? null : TraySlot.ReasonText(reasonKey);
            if (text == null)
            {
                if (hint.gameObject.activeSelf) hint.gameObject.SetActive(false);
            }
            else
            {
                if (!hint.gameObject.activeSelf) hint.gameObject.SetActive(true);
                if (!ReferenceEquals(text, hintShown)) { hint.text = text; hintShown = text; }
                Vector3 p = pos + s.Pose.up * (HallHeight + 0.06f);
                Vector3 look = Camera.main != null ? p - Camera.main.transform.position : Vector3.forward;
                look.y = 0f;
                if (look.sqrMagnitude < 1e-4f) look = Vector3.forward;
                hint.transform.SetPositionAndRotation(p, Quaternion.LookRotation(look, Vector3.up));
            }
        }

        void HidePreview()
        {
            if (ghost != null) ghost.Hide();
            if (hint != null && hint.gameObject.activeSelf) hint.gameObject.SetActive(false);
        }

        void OnTokenGrabbed()
        {
            if (!placing) return;
            farValidSince = -1f;
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Click);
        }

        void OnTokenReleased()
        {
            if (!placing || confirmed) return;
            HidePreview();
            if (watcher.LastReleaseCancelled) return; // tracking lost: token goes back to the socket (LateUpdate)
            var reg = SurfaceRegistry.Instance;
            if (reg != null && reg.TryFindSurfaceBelow(hqToken.transform.position, nearMaxDistance, out var s, out var hit))
            {
                if (ValidateHall(hit, s, out _))
                {
                    // defer out of the ISDK pointer-event callback; token is hidden next frame
                    pendingConfirm = true;
                    pendingPos = hit;
                    pendingSurface = s;
                    return;
                }
                if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Deny);
            }
            // invalid or in the air: LateUpdate returns the token to the scroll socket
        }

        /// <summary>Abort the current placement attempt: hide preview, token returns to the scroll socket.</summary>
        public void CancelPlacement()
        {
            pendingConfirm = false;
            farValidSince = -1f;
            HidePreview();
            if (placing && !confirmed && hqToken != null && (watcher == null || !watcher.IsGrabbed)) FollowSocket();
        }

        void Confirm(Vector3 pos, ITableSurface s)
        {
            var gd = GameDirector.Instance;
            if (confirmed || !placing || gd == null || gd.Phase != GamePhase.HeadquartersPlacement || s == null) return;
            if (!ValidateHall(pos, s, out _)) return;
            var factory = BuildingFactory.Instance;
            if (factory == null)
            {
                Debug.LogError("[EmpireFoundationController] BuildingFactory missing; cannot found empire");
                return;
            }
            confirmed = true;
            placing = false;
            HidePreview();
            if (hqToken != null) hqToken.SetActive(false);

            Vector3 up = s.Pose.up;
            Vector3 toCam = Camera.main != null ? Vector3.ProjectOnPlane(Camera.main.transform.position - pos, up) : Vector3.zero;
            if (toCam.sqrMagnitude < 1e-4f) toCam = Vector3.ProjectOnPlane(-(s.Pose.rotation * Vector3.forward), up);
            toCam.Normalize();
            var rot = Quaternion.LookRotation(toCam, up);

            HallPosition = pos;
            FoundedSurface = s;
            var hall = factory.SpawnTownHall(pos, rot, CurrentCiv());
            PlaceTray(pos, s, toCam);
            gd.OnHeadquartersPlaced(hall, s);

            // hall Place sfx/ring are played by BuildingFactory.SpawnTownHall / GameDirector; banner + build area by GameDirector
            if (TableAnchorBootstrap.Instance != null) TableAnchorBootstrap.Instance.AnchorSettlement(pos, rot);
            Debug.Log($"[EmpireFoundationController] hall founded at {pos} on {s.Id} (fallback={s.IsFallback})");
            Founded?.Invoke(pos, s);
        }

        static readonly float[] trayAngles = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 135f, -135f, 180f };

        void PlaceTray(Vector3 hallPos, ITableSurface s, Vector3 toCam)
        {
            if (trayRoot == null) return;
            Vector3 up = s.Pose.up;
            Vector3 chosenDir = toCam;
            Vector3 chosen = s.ProjectOnto(hallPos + toCam * trayOffset);
            for (int i = 0; i < trayAngles.Length; i++)
            {
                Vector3 dir = Quaternion.AngleAxis(trayAngles[i], up) * toCam;
                Vector3 c = s.ProjectOnto(hallPos + dir * trayOffset);
                if (s.IsFootprintSupported(c, 0.08f))
                {
                    chosen = c;
                    chosenDir = dir;
                    break;
                }
            }
            // tray faces the player: local forward points from the player toward the hall
            trayRoot.SetPositionAndRotation(chosen, Quaternion.LookRotation(-chosenDir, up));
        }
    }
}
