using TMPro;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// One tray slot (Wall / Archer Tower / Barricade). The token is an ISDK Grabbable placed by the leader.
    /// While grabbed: green/red ghost snapped to the surface below (<= 0.35 m). On release: validated placement
    /// via SettlementBuildArea + timber spend → BuildingFactory.SpawnUnit; the token always returns home. Owner: Agent A.
    /// </summary>
    public class TraySlot : MonoBehaviour
    {
        public UnitType type;
        [Tooltip("Grabbable token (ISDK Grabbable + HandGrabInteractable added by the leader). GrabWatcher is added if missing.")]
        public GameObject token;
        [Tooltip("Optional cost label; created at runtime above the slot if empty.")]
        [OptionalRef] public TMP_Text costLabel;
        public float maxSnapDistance = 0.35f;
        [Tooltip("Attach BuildingFactory.BuildMiniUnit (current civ style) as the token visual; rebuilt when the civ changes.")]
        public bool buildMiniVisual = true;
        public float miniScale = 0.5f;

        GrabWatcher watcher;
        PlacementGhost ghost;
        Transform homeParent;
        Vector3 homeLocalPos;
        Quaternion homeLocalRot;
        Rigidbody tokenBody;
        int returnFrames;
        int lastCost = -1;
        bool active;
        TextMeshPro toast;
        GameObject miniVisual;
        string miniCivId;
        float toastUntil;

        // last evaluated candidate
        bool candValid;
        bool candFound;
        Vector3 candPos;
        ITableSurface candSurface;
        string candReason;

        static readonly Color ValidText = new Color(1f, 0.93f, 0.7f);

        void Awake()
        {
            if (token == null)
            {
                Debug.LogError($"[TraySlot] '{name}' has no token assigned");
                enabled = false;
                return;
            }
            watcher = token.GetComponent<GrabWatcher>();
            if (watcher == null) watcher = token.AddComponent<GrabWatcher>();
            homeParent = token.transform.parent;
            homeLocalPos = token.transform.localPosition;
            homeLocalRot = token.transform.localRotation;
            tokenBody = token.GetComponent<Rigidbody>();
            ghost = PlacementGhost.Create("Ghost_" + type, 0.06f);
            if (costLabel == null) costLabel = CreateLabel("CostLabel", new Vector3(0f, 0.075f, 0f), 0.2f);
            toast = CreateLabel("Toast", new Vector3(0f, 0.11f, 0f), 0.16f);
            toast.color = new Color(1f, 0.45f, 0.4f);
            toast.gameObject.SetActive(false);
            // hidden until GameDirector enters a build-capable phase (Update reveals it)
            token.SetActive(false);
            costLabel.gameObject.SetActive(false);
        }

        TextMeshPro CreateLabel(string n, Vector3 localPos, float fontSize)
        {
            var go = new GameObject(n);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;
            var t = go.AddComponent<TextMeshPro>();
            t.fontSize = fontSize;
            t.alignment = TextAlignmentOptions.Center;
            t.rectTransform.sizeDelta = new Vector2(0.2f, 0.06f);
            t.color = ValidText;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            return t;
        }

        void OnEnable()
        {
            if (watcher == null) return;
            watcher.Grabbed += OnGrabbed;
            watcher.Released += OnReleased;
        }

        void OnDisable()
        {
            if (watcher != null)
            {
                watcher.Grabbed -= OnGrabbed;
                watcher.Released -= OnReleased;
            }
            if (ghost != null) ghost.Hide();
        }

        void OnDestroy()
        {
            if (ghost != null) Destroy(ghost.gameObject);
        }

        static bool PhaseAllowsBuild(GamePhase p) => p == GamePhase.BuildPhase || p == GamePhase.Wave || p == GamePhase.Upgrade;

        string UnitName
        {
            get
            {
                switch (type)
                {
                    case UnitType.Wall: return Strings.WallName;
                    case UnitType.ArcherTower: return Strings.TowerName;
                    case UnitType.Farm: return Strings.FarmName;
                    case UnitType.Mine: return Strings.MineName;
                    case UnitType.Barracks: return "Barracks";
                    case UnitType.AntiAir: return Strings.AntiAirName;
                    default: return Strings.BarricadeName;
                }
            }
        }

        int Cost => UnitDef.Get(type).EffectiveCost;
        int GoldCost => UnitDef.Get(type).EffectiveGoldCost;

        /// <summary>Maps a SettlementBuildArea reason key (key name or already-resolved text) to player text.</summary>
        public static string ReasonText(string key)
        {
            if (string.IsNullOrEmpty(key)) return Strings.PlacementInvalidSupport;
            switch (key)
            {
                case nameof(Strings.PlacementInvalidSupport): return Strings.PlacementInvalidSupport;
                case nameof(Strings.PlacementInvalidOverlap): return Strings.PlacementInvalidOverlap;
                case nameof(Strings.PlacementInvalidOutside): return Strings.PlacementInvalidOutside;
                case nameof(Strings.PlacementInvalidLimit): return Strings.PlacementInvalidLimit;
                case nameof(Strings.PlacementInvalidObstacle): return Strings.PlacementInvalidObstacle;
                case nameof(Strings.NotEnoughTimber): return Strings.NotEnoughTimber;
                default: return key;
            }
        }

        void Update()
        {
            var gd = GameDirector.Instance;
            bool allow = gd != null && PhaseAllowsBuild(gd.Phase);
            if (allow != active && !watcher.IsGrabbed)
            {
                active = allow;
                if (allow) EnsureMiniVisual();
                token.SetActive(allow);
                if (costLabel != null) costLabel.gameObject.SetActive(allow);
                if (!allow) ghost.Hide();
            }

            int cost = Cost;
            bool affordable = gd != null && gd.CanAfford(cost, GoldCost);
            if (cost != lastCost)
            {
                lastCost = cost;
                costLabel.text = UnitName + "\n" + cost + (GoldCost > 0 ? string.Format(Strings.GoldCostSuffix, GoldCost) : "");
            }
            if (costLabel != null) costLabel.alpha = affordable ? 1f : 0.45f;

            if (toast.gameObject.activeSelf && Time.time > toastUntil) toast.gameObject.SetActive(false);

            if (watcher.IsGrabbed && active)
            {
                Evaluate(gd, cost);
                if (type == UnitType.Wall) UpdateWallLine(gd, cost);
                if (candFound) ghost.Show(candPos, candSurface.Pose.up, UnitDef.Get(type).footprint, candValid);
                else ghost.Hide();
                if (type == UnitType.AntiAir && candFound) AntiAirTower.ShowRangeRing(candPos, candSurface.Pose.up, UnitDef.Get(type).range, candValid);
            }
        }

        void EnsureMiniVisual()
        {
            if (!buildMiniVisual || BuildingFactory.Instance == null) return;
            var prof = CivilizationProfile.Current;
            string civ = prof != null ? prof.civId : null;
            if (miniVisual != null && civ == miniCivId) return;
            if (miniVisual != null) Destroy(miniVisual);
            miniVisual = BuildingFactory.Instance.BuildMiniUnit(token.transform, type, miniScale);
            miniCivId = civ;
        }

        void LateUpdate()
        {
            if (returnFrames > 0)
            {
                returnFrames--;
                SnapHome();
            }
        }

        void Evaluate(GameDirector gd, int cost)
        {
            candFound = false;
            candValid = false;
            candReason = null;
            candSurface = null;
            var reg = SurfaceRegistry.Instance;
            if (reg == null) return;
            if (!reg.TryFindSurfaceBelow(token.transform.position, maxSnapDistance, out var s, out var hit)) return;
            candFound = true;
            candSurface = s;
            candPos = hit;
            var area = SettlementBuildArea.Instance;
            if (area == null) { candReason = nameof(Strings.PlacementInvalidOutside); return; }
            if (!area.CanPlace(hit, UnitDef.Get(type).footprint, type, out var reason)) { candReason = reason; return; }
            if (gd == null || !gd.CanAfford(cost, GoldCost)) { candReason = nameof(Strings.NotEnoughTimber); return; }
            candValid = true;
        }

        // ---------- wall line drawing: hold the wall token low over the surface and sweep it — a fortification line
        // of joined wall segments is built along the path (each segment pays its own cost). Lift the token to pause.
        const float LineFootprint = 0.045f;
        const float LineSpacing = UnitDef.WallLength * 0.95f;
        const float LineContactHeight = 0.05f;
        bool lineAnchorSet;
        Vector3 lineEnd;
        int lineSegments;
        float lineDenyUntil;

        void UpdateWallLine(GameDirector gd, int cost)
        {
            if (!candFound || candSurface == null || gd == null) return;
            Vector3 up = candSurface.Pose.up;
            float h = Vector3.Dot(token.transform.position - candPos, up);
            if (h > LineContactHeight) { lineAnchorSet = false; return; }
            if (!lineAnchorSet) { lineEnd = candPos; lineAnchorSet = true; return; }
            Vector3 d = Vector3.ProjectOnPlane(candPos - lineEnd, up);
            if (d.magnitude < LineSpacing) return;
            Vector3 dir = d.normalized;
            Vector3 center = lineEnd + dir * (LineSpacing * 0.5f);
            if (!candSurface.IsPointSupported(center)) { lineEnd = candPos; return; }
            center = candSurface.ProjectOnto(center);
            var area = SettlementBuildArea.Instance;
            string reason = null;
            bool ok = area != null && area.CanPlace(center, LineFootprint, out reason);
            if (ok && gd.TrySpend(cost, 0))
            {
                var rot = Quaternion.LookRotation(Vector3.Cross(dir, up), up); // wall length runs along local X = dir
                var unit = BuildingFactory.Instance != null ? BuildingFactory.Instance.SpawnUnit(UnitType.Wall, center, rot) : null;
                if (unit != null)
                {
                    area.RegisterPlacement(center, LineFootprint);
                    lineSegments++;
                    ProceduralAudio.Fire(Sfx.Hammer, center);
                }
                lineEnd += dir * LineSpacing;
            }
            else
            {
                lineEnd = candPos; // skip the blocked stretch, continue the line further on
                if (Time.time > lineDenyUntil)
                {
                    lineDenyUntil = Time.time + 0.8f;
                    Deny(ok ? nameof(Strings.NotEnoughTimber) : reason);
                }
            }
        }

        void OnGrabbed()
        {
            lineAnchorSet = false;
            lineSegments = 0;
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Click);
        }

        void OnReleased()
        {
            ghost.Hide();
            var gd = GameDirector.Instance;
            if (lineSegments > 0) { lineSegments = 0; lineAnchorSet = false; ReturnHome(); return; } // line already built
            if (!watcher.LastReleaseCancelled && active && gd != null)
            {
                int cost = Cost;
                Evaluate(gd, cost);
                if (candFound)
                {
                    if (candValid && gd.TrySpend(cost, GoldCost))
                    {
                        Vector3 up = candSurface.Pose.up;
                        Vector3 f = Vector3.ProjectOnPlane(token.transform.forward, up);
                        if (f.sqrMagnitude < 1e-4f) f = Vector3.ProjectOnPlane(token.transform.up, up);
                        if (f.sqrMagnitude < 1e-4f) f = Vector3.ProjectOnPlane(Vector3.forward, up);
                        var rot = Quaternion.LookRotation(f.normalized, up);
                        var unit = BuildingFactory.Instance != null ? BuildingFactory.Instance.SpawnUnit(type, candPos, rot) : null;
                        if (unit != null)
                        {
                            if (SettlementBuildArea.Instance != null) SettlementBuildArea.Instance.RegisterPlacement(candPos, UnitDef.Get(type).footprint);
                            ProceduralAudio.Fire(Sfx.Hammer, candPos);
                            if (VfxPool.Instance != null) VfxPool.Instance.Ring(candPos, UnitDef.Get(type).footprint * 1.5f);
                        }
                    }
                    else
                    {
                        Deny(candValid ? nameof(Strings.NotEnoughTimber) : candReason);
                    }
                }
            }
            ReturnHome();
        }

        void Deny(string reasonKey)
        {
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Deny);
            toast.text = ReasonText(reasonKey);
            toast.gameObject.SetActive(true);
            toastUntil = Time.time + 1.6f;
        }

        void SnapHome()
        {
            if (token.transform.parent != homeParent) token.transform.SetParent(homeParent, false);
            token.transform.localPosition = homeLocalPos;
            token.transform.localRotation = homeLocalRot;
            if (tokenBody != null && !tokenBody.isKinematic)
            {
                tokenBody.linearVelocity = Vector3.zero;
                tokenBody.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>Returns the token to its slot now and for the next two frames (overrides any ISDK release motion).</summary>
        public void ReturnHome()
        {
            SnapHome();
            returnFrames = 2;
        }
    }
}
