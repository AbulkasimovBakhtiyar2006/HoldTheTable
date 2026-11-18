using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;
using Object = UnityEngine.Object;

namespace HoldTheTable
{
    public enum HandSide { Left, Right }

    /// <summary>
    /// Hands-only input facade (Agent A). Discovers the ISDK hands (Oculus.Interaction.Input.Hand) and the rig's
    /// RayInteractors once (re-scans at most once per second while something is missing), never in the hot path.
    /// Falls back to OVRHand when no ISDK hand exists for a side.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class HandRig : MonoBehaviour
    {
        public static HandRig Instance { get; private set; }

        [Tooltip("Dominant hand: far placement ray/pinch. HUD goes to the other wrist.")]
        public HandSide dominant = HandSide.Right;

        public HandSide Dominant => dominant;
        public HandSide NonDominant => dominant == HandSide.Right ? HandSide.Left : HandSide.Right;

        readonly IHand[] hands = new IHand[2];
        readonly MonoBehaviour[] handBehaviours = new MonoBehaviour[2];
        readonly RayInteractor[] rays = new RayInteractor[2];
        readonly OVRHand[] ovrHands = new OVRHand[2];
        float nextScan;

        void Awake()
        {
            Instance = this;
        }

        void Start()
        {
            Scan();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (Time.unscaledTime < nextScan) return;
            bool missing = false;
            for (int i = 0; i < 2; i++)
                if ((handBehaviours[i] == null && ovrHands[i] == null) || rays[i] == null) missing = true;
            if (missing) Scan();
            nextScan = Time.unscaledTime + 1f;
        }

        static T[] FindAll<T>(bool includeInactive) where T : Object
        {
            var inactive = includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude;
#if UNITY_6000_4_OR_NEWER
            return FindObjectsByType<T>(inactive);
#else
            return FindObjectsByType<T>(inactive, FindObjectsSortMode.None);
#endif
        }

        static int Rank(Hand h)
        {
            var t = h.GetType();
            if (t == typeof(Hand)) return 3;
            if (t == typeof(SyntheticHand)) return 0;
            return 1;
        }

        static bool PathContains(Transform t, string token)
        {
            for (int depth = 0; t != null && depth < 6; depth++, t = t.parent)
                if (t.name.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static int SideFromPath(Transform t)
        {
            bool l = PathContains(t, "Left"), r = PathContains(t, "Right");
            if (l && !r) return 0;
            if (r && !l) return 1;
            return -1;
        }

        /// <summary>Discovery of hands/rays. Called at Start and at most once a second while something is missing.</summary>
        public void Scan()
        {
            int[] bestRank = { -1, -1 };
            var found = FindAll<Hand>(false);
            foreach (var h in found)
            {
                int side;
                try { side = h.Handedness == Handedness.Left ? 0 : 1; }
                catch { side = SideFromPath(h.transform); }
                int pathSide = SideFromPath(h.transform);
                if (pathSide >= 0 && pathSide != side) side = pathSide;
                if (side < 0) continue;
                int r = Rank(h);
                if (r > bestRank[side])
                {
                    bestRank[side] = r;
                    hands[side] = h;
                    handBehaviours[side] = h;
                }
            }

            var rayList = FindAll<RayInteractor>(true);
            foreach (var ri in rayList)
            {
                if (PathContains(ri.transform, "Controller")) continue;
                int side = SideFromPath(ri.transform);
                if (side < 0) continue;
                if (rays[side] == null) rays[side] = ri;
            }

            var ovr = FindAll<OVRHand>(false);
            foreach (var oh in ovr)
            {
                int side = -1;
                try
                {
                    var st = ((OVRSkeleton.IOVRSkeletonDataProvider)oh).GetSkeletonType().ToString();
                    if (st.Contains("Left")) side = 0; else if (st.Contains("Right")) side = 1;
                }
                catch { }
                if (side < 0) side = SideFromPath(oh.transform);
                if (side >= 0 && ovrHands[side] == null) ovrHands[side] = oh;
            }
        }

        IHand GetHand(HandSide s)
        {
            int i = (int)s;
            var mb = handBehaviours[i];
            if (mb == null || !mb.isActiveAndEnabled) return null;
            return hands[i];
        }

        OVRHand GetOvr(HandSide s)
        {
            var o = ovrHands[(int)s];
            return o != null && o.isActiveAndEnabled ? o : null;
        }

        public bool IsTracked(HandSide s)
        {
            var h = GetHand(s);
            if (h != null) return h.IsConnected && h.IsTrackedDataValid;
            var o = GetOvr(s);
            return o != null && o.IsTracked && o.IsDataValid;
        }

        public bool IsPinching(HandSide s)
        {
            var h = GetHand(s);
            if (h != null) return h.IsConnected && h.IsTrackedDataValid && h.GetIndexFingerIsPinching();
            var o = GetOvr(s);
            return o != null && o.IsTracked && o.GetFingerIsPinching(OVRHand.HandFinger.Index);
        }

        public bool TryGetPalm(HandSide s, out Pose pose)
        {
            var h = GetHand(s);
            if (h != null && h.IsTrackedDataValid && h.GetJointPose(HandJointId.HandPalm, out pose)) return true;
            return TryGetOvrPose(s, out pose);
        }

        public bool TryGetWrist(HandSide s, out Pose pose)
        {
            var h = GetHand(s);
            if (h != null && h.IsTrackedDataValid)
            {
                if (h.GetJointPose(HandJointId.HandWristRoot, out pose)) return true;
                if (h.GetRootPose(out pose)) return true;
            }
            return TryGetOvrPose(s, out pose);
        }

        public bool TryGetIndexTip(HandSide s, out Vector3 pos)
        {
            var h = GetHand(s);
            if (h != null && h.IsTrackedDataValid && h.GetJointPose(HandJointId.HandIndexTip, out var p))
            {
                pos = p.position;
                return true;
            }
            if (TryGetOvrPose(s, out var op))
            {
                pos = op.position + op.rotation * Vector3.forward * 0.09f;
                return true;
            }
            pos = default;
            return false;
        }

        bool TryGetOvrPose(HandSide s, out Pose pose)
        {
            var o = GetOvr(s);
            if (o != null && o.IsTracked)
            {
                pose = new Pose(o.transform.position, o.transform.rotation);
                return true;
            }
            pose = Pose.identity;
            return false;
        }

        /// <summary>Far-selection ray: rig RayInteractor if present & active, else ISDK pointer pose, else OVR pointer pose.</summary>
        public bool TryGetRay(HandSide s, out Ray ray)
        {
            ray = default;
            if (!IsTracked(s)) return false;
            var ri = rays[(int)s];
            if (ri != null && ri.isActiveAndEnabled)
            {
                var r = ri.Ray;
                if (r.direction.sqrMagnitude > 0.5f)
                {
                    ray = r;
                    return true;
                }
            }
            var h = GetHand(s);
            if (h != null && h.IsPointerPoseValid && h.GetPointerPose(out var pp))
            {
                ray = new Ray(pp.position, pp.rotation * Vector3.forward);
                return true;
            }
            var o = GetOvr(s);
            if (o != null && o.IsPointerPoseValid && o.PointerPose != null)
            {
                ray = new Ray(o.PointerPose.position, o.PointerPose.forward);
                return true;
            }
            return false;
        }

        /// <summary>Closest tracked hand to a world point (used to attribute grabs). Returns false if none tracked.</summary>
        public bool TryGetClosestHand(Vector3 w, out HandSide side)
        {
            side = dominant;
            float best = float.MaxValue;
            bool any = false;
            for (int i = 0; i < 2; i++)
            {
                var hs = (HandSide)i;
                if (!TryGetIndexTip(hs, out var tip)) continue;
                float d = (tip - w).sqrMagnitude;
                if (d < best) { best = d; side = hs; any = true; }
            }
            return any;
        }
    }
}
