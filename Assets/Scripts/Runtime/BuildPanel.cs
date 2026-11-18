using TMPro;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Hands-only build panel: a wooden/parchment world-space board floating at the non-dominant wrist with one 3D touch
    /// button per UnitType plus rotate-left/right and cancel. Buttons are pressed by the dominant index fingertip
    /// (proximity + 0.5 s debounce; no uGUI/ISDK so it can be created at runtime). After choosing a type, a green/red ghost
    /// follows the dominant hand's pointing ray on the table; a pinch (rising edge) places it using the same code path as
    /// TraySlot.OnReleased (TrySpend -> BuildingFactory.SpawnUnit -> SettlementBuildArea.RegisterPlacement + sfx/ring).
    /// Placement mode persists until Cancel. Self-bootstrapping singleton; coexists with the tray tokens. Owner: BuildPanel agent.
    /// </summary>
    public class BuildPanel : MonoBehaviour
    {
        // ---- local texts (no shared Strings edits) ----
        const string TitleText = "BUILD";
        const string RotateLeftText = "Rotate\nleft";
        const string RotateRightText = "Rotate\nright";
        const string CancelText = "Cancel";
        const string BarracksText = "Barracks";
        const string PinchHintFormat = "Pinch to place: {0}";
        const string PickHintText = "Touch a building";

        // ---- layout (meters, panel local space; viewer on -Z side, text readable from there) ----
        const float PanelW = 0.30f, PanelH = 0.18f, PanelD = 0.012f;
        const float BtnW = 0.054f, BtnH = 0.060f, ColPitch = 0.058f;
        const float Row1Y = 0.022f, Row2Y = -0.052f;
        const float PadZ = -0.010f, PadDepth = 0.012f;
        const float PressMaxZ = 0.030f;          // behind-face limit
        const float PressRange = 0.025f;         // fingertip within 2.5 cm in front of the pad face
        const float HoverRange = 0.08f;
        const float Debounce = 0.5f;
        const int MaxTypeButtons = 7;
        const float RayMax = 3f;

        const int KindType = 0, KindRotL = 1, KindRotR = 2, KindCancel = 3;

        sealed class Btn
        {
            public int kind;
            public UnitType type;
            public Transform root;
            public Transform pad;
            public MeshRenderer rend;
            public TextMeshPro label;
            public float cx, cy;
            public int lastCost = -1, lastGold = -1;
            public bool lastAfford = true;
            public int state = -1; // 0 normal 1 hover 2 selected 3 disabled
        }

        public static BuildPanel Instance { get; private set; }

        static Material matBoard, matParch, matNormal, matHover, matSelected, matDisabled, matCancel;
        static Material matArrowOk, matArrowBad;

        Btn[] btns;
        Transform panel;
        TextMeshPro title, status;
        PlacementGhost ghost;
        Transform arrow;
        MeshRenderer arrowRend;
        bool built;

        GameDirector boundGd;
        Camera cam;
        float nextCamLookup;
        bool panelPlaced;

        int selected = -1;       // index into btns (type button) or -1
        int yawSteps;
        float lastPressTime = -10f;
        bool prevPinch;

        // candidate
        bool candFound, candValid;
        Vector3 candPos;
        ITableSurface candSurface;
        string candReason;

        float toastUntil;
        string lastStatus;
        int hintSel = -1;
        string hintText;

        // ------------------------------------------------------------------ bootstrap
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Instance = null;
            matBoard = matParch = matNormal = matHover = matSelected = matDisabled = matCancel = null;
            matArrowOk = matArrowBad = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (Instance != null) return;
            var existing = FindFirstObjectByType<BuildPanel>();
            if (existing != null) { Instance = existing; return; }
            var go = new GameObject("BuildPanel");
            go.AddComponent<BuildPanel>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            GameSettings.Changed += OnSettingsChanged;
            settingsHooked = true;
        }

        bool settingsHooked;

        void OnSettingsChanged()
        {
            // menu/action hands swapped: cancel any placement in progress and re-attach the board to the new wrist
            Cancel();
            panelPlaced = false;
        }

        void OnDisable() { Unbind(); }

        void OnDestroy()
        {
            if (settingsHooked) { GameSettings.Changed -= OnSettingsChanged; settingsHooked = false; }
            Unbind();
            if (ghost != null) Destroy(ghost.gameObject);
            if (arrow != null) Destroy(arrow.gameObject);
            if (panel != null) Destroy(panel.gameObject);
            if (Instance == this) Instance = null;
        }

        void Bind(GameDirector gd)
        {
            Unbind();
            boundGd = gd;
            if (gd != null) gd.GameReset += OnGameReset;
        }

        void Unbind()
        {
            if (boundGd != null) boundGd.GameReset -= OnGameReset;
            boundGd = null;
        }

        void OnGameReset()
        {
            Cancel();
            yawSteps = 0;
            panelPlaced = false;
            toastUntil = 0f;
        }

        // ------------------------------------------------------------------ build geometry
        static Material Pick(ref Material m, Color c)
        {
            if (m == null) m = Mats.Lit(c);
            return m;
        }

        static Material MBoard => Pick(ref matBoard, new Color(0.26f, 0.15f, 0.08f));
        static Material MParch => Pick(ref matParch, new Color(0.88f, 0.79f, 0.58f));
        static Material MNormal => Pick(ref matNormal, new Color(0.45f, 0.28f, 0.14f));
        static Material MHover => Pick(ref matHover, new Color(0.62f, 0.42f, 0.2f));
        static Material MSelected => Pick(ref matSelected, new Color(0.78f, 0.62f, 0.18f));
        static Material MDisabled => Pick(ref matDisabled, new Color(0.24f, 0.2f, 0.17f));
        static Material MCancel => Pick(ref matCancel, new Color(0.5f, 0.2f, 0.14f));

        static MeshRenderer Box(Transform parent, string n, Vector3 lp, Vector3 ls, Material m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = n;
            var c = go.GetComponent<Collider>();
            if (c != null) Destroy(c);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = lp;
            go.transform.localScale = ls;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        static TextMeshPro Label(Transform parent, string n, Vector3 lp, Vector2 size, float fontSize, Color col)
        {
            var go = new GameObject(n);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = lp;
            var t = go.AddComponent<TextMeshPro>();
            t.fontSize = fontSize;
            t.alignment = TextAlignmentOptions.Center;
            t.rectTransform.sizeDelta = size;
            t.color = col;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Overflow;
            return t;
        }

        void BuildPanelGeometry()
        {
            built = true;
            var root = new GameObject("BuildPanelBoard");
            panel = root.transform;
            Box(panel, "Board", Vector3.zero, new Vector3(PanelW, PanelH, PanelD), MBoard);
            Box(panel, "Parchment", new Vector3(0f, 0f, -0.0065f), new Vector3(PanelW - 0.014f, PanelH - 0.014f, 0.002f), MParch);

            title = Label(panel, "Title", new Vector3(0f, 0.073f, -0.009f), new Vector2(0.2f, 0.02f), 0.16f, new Color(0.25f, 0.14f, 0.07f));
            title.fontStyle = FontStyles.Bold;
            title.text = TitleText;

            status = Label(panel, "Status", new Vector3(0f, PanelH * 0.5f + 0.016f, -0.004f), new Vector2(0.4f, 0.025f), 0.14f, new Color(1f, 0.93f, 0.7f));
            status.textWrappingMode = TextWrappingModes.NoWrap;
            status.text = string.Empty;

            int typeCount = Mathf.Min(UnitDef.TypeCount, MaxTypeButtons) - 1; // Wall is not offered (user: ugly)
            btns = new Btn[typeCount + 3];
            int bi = 0;
            for (int i = 0; i < typeCount; i++) btns[bi++] = MakeButton(KindType, (UnitType)(i + 1), i);
            btns[bi++] = MakeButton(KindRotL, UnitType.Wall, typeCount);
            btns[bi++] = MakeButton(KindRotR, UnitType.Wall, typeCount + 1);
            btns[bi++] = MakeButton(KindCancel, UnitType.Wall, typeCount + 2);

            ghost = PlacementGhost.Create("BuildPanelGhost", 0.06f);
            var a = GameObject.CreatePrimitive(PrimitiveType.Cube);
            a.name = "BuildPanelArrow";
            var ac = a.GetComponent<Collider>();
            if (ac != null) Destroy(ac);
            arrowRend = a.GetComponent<MeshRenderer>();
            arrowRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            arrowRend.receiveShadows = false;
            arrow = a.transform;
            arrow.localScale = new Vector3(0.008f, 0.003f, 0.024f);
            a.SetActive(false);

            panel.gameObject.SetActive(false);
        }

        Btn MakeButton(int kind, UnitType type, int slot)
        {
            int col = slot % 5, row = slot / 5;
            var b = new Btn { kind = kind, type = type };
            b.cx = (col - 2) * ColPitch;
            b.cy = row == 0 ? Row1Y : Row2Y;
            var go = new GameObject("Btn_" + (kind == KindType ? type.ToString() : "Ctl" + kind));
            go.transform.SetParent(panel, false);
            go.transform.localPosition = new Vector3(b.cx, b.cy, 0f);
            b.root = go.transform;
            b.rend = Box(b.root, "Pad", new Vector3(0f, 0f, PadZ), new Vector3(BtnW, BtnH, PadDepth), kind == KindCancel ? MCancel : MNormal);
            b.pad = b.rend.transform;
            b.label = Label(b.root, "Label", new Vector3(0f, 0f, PadZ - PadDepth * 0.5f - 0.001f),
                new Vector2(BtnW - 0.004f, BtnH - 0.004f), 0.105f, new Color(1f, 0.94f, 0.76f));
            b.label.alignment = TextAlignmentOptions.Center;
            switch (kind)
            {
                case KindRotL: b.label.text = RotateLeftText; break;
                case KindRotR: b.label.text = RotateRightText; break;
                case KindCancel: b.label.text = CancelText; break;
            }
            return b;
        }

        static string TypeName(UnitType t)
        {
            switch (t)
            {
                case UnitType.Wall: return Strings.WallName;
                case UnitType.ArcherTower: return Strings.TowerName;
                case UnitType.Farm: return Strings.FarmName;
                case UnitType.Mine: return Strings.MineName;
                case UnitType.Barracks: return BarracksText;
                case UnitType.AntiAir: return Strings.AntiAirName;
                default: return Strings.BarricadeName;
            }
        }

        // ------------------------------------------------------------------ frame
        void Update()
        {
            var gd = GameDirector.Instance;
            if (gd != boundGd) Bind(gd);

            bool visible = gd != null && gd.IsCombatPhase;
            if (!visible)
            {
                if (built && panel.gameObject.activeSelf)
                {
                    Cancel();
                    panel.gameObject.SetActive(false);
                    panelPlaced = false;
                }
                prevPinch = false;
                return;
            }

            if (!built) BuildPanelGeometry();
            if (!panel.gameObject.activeSelf) panel.gameObject.SetActive(true);

            var rig = HandRig.Instance;
            UpdatePanelPose(rig);
            RefreshButtons(gd);
            bool poked = UpdatePoke(rig, gd);
            UpdatePlacement(rig, gd, poked);
            UpdateStatus();
        }

        void UpdatePanelPose(HandRig rig)
        {
            if (cam == null && Time.unscaledTime >= nextCamLookup)
            {
                cam = Camera.main;
                nextCamLookup = Time.unscaledTime + 1f;
            }
            Vector3 head = cam != null ? cam.transform.position : new Vector3(0f, 1.4f, 0f);
            Vector3 headFwd = cam != null ? cam.transform.forward : Vector3.forward;
            Vector3 flatFwd = Vector3.ProjectOnPlane(headFwd, Vector3.up);
            if (flatFwd.sqrMagnitude < 1e-4f) flatFwd = Vector3.forward;
            flatFwd.Normalize();

            Vector3 target;
            if (rig != null && rig.TryGetWrist(GameSettings.MenuHand, out var wrist))
            {
                Vector3 toHead = Vector3.ProjectOnPlane(head - wrist.position, Vector3.up);
                if (toHead.sqrMagnitude < 1e-4f) toHead = -flatFwd;
                target = wrist.position + Vector3.up * 0.15f + toHead.normalized * 0.04f;
            }
            else if (panelPlaced)
            {
                target = panel.position; // menu hand briefly lost: hold position, wait for it to come back
            }
            else
            {
                Vector3 side = Vector3.Cross(Vector3.up, flatFwd) * (GameSettings.MenuHand == HandSide.Left ? -1f : 1f);
                target = head + flatFwd * 0.45f + Vector3.down * 0.28f + side * 0.12f;
            }

            Vector3 look = target - head;
            if (look.sqrMagnitude < 1e-4f) look = flatFwd;
            Quaternion rot = Quaternion.LookRotation(look, Vector3.up);

            if (!panelPlaced)
            {
                panel.SetPositionAndRotation(target, rot);
                panelPlaced = true;
            }
            else
            {
                float k = 1f - Mathf.Exp(-30f * Time.unscaledDeltaTime);
                panel.SetPositionAndRotation(Vector3.Lerp(panel.position, target, k), Quaternion.Slerp(panel.rotation, rot, k));
            }
        }

        void RefreshButtons(GameDirector gd)
        {
            for (int i = 0; i < btns.Length; i++)
            {
                var b = btns[i];
                if (b.kind != KindType) continue;
                var def = UnitDef.Get(b.type);
                int cost = def.EffectiveCost, gold = def.EffectiveGoldCost;
                bool afford = gd.CanAfford(cost, gold);
                if (cost != b.lastCost || gold != b.lastGold)
                {
                    b.lastCost = cost;
                    b.lastGold = gold;
                    b.label.text = TypeName(b.type) + "\n" + cost + (gold > 0 ? string.Format(Strings.GoldCostSuffix, gold) : string.Empty);
                }
                if (afford != b.lastAfford || b.lastCost < 0)
                {
                    b.lastAfford = afford;
                    b.label.alpha = afford ? 1f : 0.45f;
                }
            }
        }

        void SetState(Btn b, int state)
        {
            if (b.state == state) return;
            b.state = state;
            Material m;
            switch (state)
            {
                case 1: m = MHover; break;
                case 2: m = MSelected; break;
                case 3: m = MDisabled; break;
                default: m = b.kind == KindCancel ? MCancel : MNormal; break;
            }
            b.rend.sharedMaterial = m;
        }

        /// <summary>Returns true while a fingertip is on the panel (used to ignore the pinch of a button press).</summary>
        bool UpdatePoke(HandRig rig, GameDirector gd)
        {
            bool tipOn = false;
            bool haveTip = false;
            Vector3 lp = default;
            if (rig != null && rig.TryGetIndexTip(GameSettings.ActionHand, out var tip))
            {
                haveTip = true;
                lp = panel.InverseTransformPoint(tip);
            }
            // panel scale is 1, so local meters == world meters

            bool canPress = Time.unscaledTime - lastPressTime >= Debounce;
            for (int i = 0; i < btns.Length; i++)
            {
                var b = btns[i];
                bool hover = false, press = false;
                if (haveTip)
                {
                    float dx = Mathf.Abs(lp.x - b.cx), dy = Mathf.Abs(lp.y - b.cy);
                    if (dx < BtnW * 0.5f + 0.004f && dy < BtnH * 0.5f + 0.004f && lp.z > PadZ - HoverRange && lp.z < PressMaxZ)
                    {
                        hover = true;
                        tipOn = true;
                        float faceZ = PadZ - PadDepth * 0.5f;
                        if (lp.z > faceZ - PressRange) press = true;
                    }
                }

                bool disabled = b.kind == KindType && !b.lastAfford;
                int st = (selected == i) ? 2 : disabled ? 3 : hover ? 1 : 0;
                SetState(b, st);
                b.pad.localPosition = new Vector3(0f, 0f, PadZ + (hover && press ? 0.006f : 0f));

                if (press && canPress)
                {
                    lastPressTime = Time.unscaledTime;
                    canPress = false;
                    OnButton(i, gd);
                }
            }
            return tipOn || Time.unscaledTime - lastPressTime < Debounce;
        }

        void OnButton(int i, GameDirector gd)
        {
            var b = btns[i];
            switch (b.kind)
            {
                case KindType:
                    if (!b.lastAfford)
                    {
                        Toast(Strings.NotEnoughTimber);
                        ProceduralAudio.Fire(Sfx.Deny);
                        return;
                    }
                    selected = i;
                    ProceduralAudio.Fire(Sfx.Click);
                    break;
                case KindRotL:
                    yawSteps = (yawSteps + 3) & 3;
                    ProceduralAudio.Fire(Sfx.Click);
                    break;
                case KindRotR:
                    yawSteps = (yawSteps + 1) & 3;
                    ProceduralAudio.Fire(Sfx.Click);
                    break;
                default:
                    Cancel();
                    ProceduralAudio.Fire(Sfx.Click);
                    break;
            }
        }

        void Cancel()
        {
            selected = -1;
            hintSel = -1;
            candFound = false;
            candValid = false;
            if (ghost != null) ghost.Hide();
            if (arrow != null && arrow.gameObject.activeSelf) arrow.gameObject.SetActive(false);
            prevPinch = false;
        }

        // ------------------------------------------------------------------ placement
        void UpdatePlacement(HandRig rig, GameDirector gd, bool panelTouched)
        {
            bool pinch = rig != null && rig.IsPinching(GameSettings.ActionHand);
            bool rising = pinch && !prevPinch;
            prevPinch = pinch;

            if (selected < 0 || rig == null)
            {
                if (ghost.Visible) ghost.Hide();
                if (arrow.gameObject.activeSelf) arrow.gameObject.SetActive(false);
                return;
            }

            var type = btns[selected].type;
            var def = UnitDef.Get(type);
            int cost = def.EffectiveCost, gold = def.EffectiveGoldCost;
            Evaluate(rig, gd, type, def.footprint, cost, gold);

            if (!candFound)
            {
                ghost.Hide();
                arrow.gameObject.SetActive(false);
                return;
            }

            Vector3 up = candSurface.Pose.up;
            Quaternion rot = PlacementRotation(up);
            ghost.ShowBuilding(type, candPos, rot, up, def.footprint, candValid);
            if (type == UnitType.AntiAir) AntiAirTower.ShowRangeRing(candPos, up, def.range, candValid);

            if (matArrowOk == null) matArrowOk = Mats.Transparent(new Color(0.25f, 1f, 0.35f, 0.75f));
            if (matArrowBad == null) matArrowBad = Mats.Transparent(new Color(1f, 0.22f, 0.2f, 0.75f));
            arrowRend.sharedMaterial = candValid ? matArrowOk : matArrowBad;
            arrow.SetPositionAndRotation(candPos + up * 0.004f + rot * Vector3.forward * (def.footprint + 0.014f), rot);
            if (!arrow.gameObject.activeSelf) arrow.gameObject.SetActive(true);

            if (rising && !panelTouched)
            {
                if (candValid && gd.TrySpend(cost, gold))
                {
                    var f = BuildingFactory.Instance;
                    var unit = f != null ? f.SpawnUnit(type, candPos, rot) : null;
                    if (unit != null)
                    {
                        var area = SettlementBuildArea.Instance;
                        if (area != null) area.RegisterPlacement(candPos, def.footprint);
                        ProceduralAudio.Fire(Sfx.Hammer, candPos);
                        if (VfxPool.Instance != null) VfxPool.Instance.Ring(candPos, def.footprint * 1.5f);
                    }
                }
                else
                {
                    ProceduralAudio.Fire(Sfx.Deny);
                    Toast(TraySlot.ReasonText(candValid ? nameof(Strings.NotEnoughTimber) : candReason));
                }
            }
        }

        Quaternion PlacementRotation(Vector3 up)
        {
            Vector3 f = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, up) : Vector3.zero;
            if (f.sqrMagnitude < 1e-4f) f = Vector3.ProjectOnPlane(Vector3.forward, up);
            if (f.sqrMagnitude < 1e-4f) f = Vector3.ProjectOnPlane(Vector3.right, up);
            return Quaternion.AngleAxis(yawSteps * 90f, up) * Quaternion.LookRotation(f.normalized, up);
        }

        void Evaluate(HandRig rig, GameDirector gd, UnitType type, float footprint, int cost, int gold)
        {
            candFound = false;
            candValid = false;
            candReason = null;
            candSurface = null;

            var reg = SurfaceRegistry.Instance;
            if (reg == null) return;

            ITableSurface s = null;
            Vector3 hit = default;
            bool got = false;
            if (rig.TryGetRay(GameSettings.ActionHand, out var ray) && reg.TryRaycast(ray, RayMax, out s, out hit)) got = true;
            else if (rig.TryGetIndexTip(GameSettings.ActionHand, out var tip) && reg.TryFindSurfaceBelow(tip, 0.35f, out s, out hit)) got = true;
            if (!got) return;

            candFound = true;
            candSurface = s;
            candPos = s.ProjectOnto(hit);

            var area = SettlementBuildArea.Instance;
            if (area == null) { candReason = nameof(Strings.PlacementInvalidOutside); return; }
            if (!area.CanPlace(candPos, footprint, type, out var reason)) { candReason = reason; return; }
            if (!gd.CanAfford(cost, gold)) { candReason = nameof(Strings.NotEnoughTimber); return; }
            candValid = true;
        }

        // ------------------------------------------------------------------ status text
        void Toast(string s)
        {
            toastUntil = Time.unscaledTime + 1.6f;
            SetStatus(s, new Color(1f, 0.45f, 0.4f));
        }

        void SetStatus(string s, Color c)
        {
            if (s == lastStatus) { status.color = c; return; }
            lastStatus = s;
            status.text = s;
            status.color = c;
        }

        void UpdateStatus()
        {
            if (Time.unscaledTime < toastUntil) return;
            if (selected < 0) { SetStatus(PickHintText, new Color(1f, 0.93f, 0.7f)); return; }
            if (candFound && !candValid)
                SetStatus(TraySlot.ReasonText(candReason), new Color(1f, 0.45f, 0.4f));
            else
                {
                if (hintSel != selected) { hintSel = selected; hintText = string.Format(PinchHintFormat, TypeName(btns[selected].type)); }
                SetStatus(hintText, new Color(0.6f, 1f, 0.6f));
            }
        }
    }
}
