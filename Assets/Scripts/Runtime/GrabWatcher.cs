using System;
using Oculus.Interaction;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Observes ISDK Grabbable(s) on this object/children (added by the leader in the editor) and exposes a simple
    /// grabbed/released state. If the grabbing hand loses tracking during the grab (or the interactor cancels),
    /// the release is flagged as cancelled so callers never place by accident. Owner: Agent A.
    /// </summary>
    public class GrabWatcher : MonoBehaviour
    {
        public bool IsGrabbed { get; private set; }
        /// <summary>True when the last release came from Cancel or from lost hand tracking: do not place.</summary>
        public bool LastReleaseCancelled { get; private set; }
        /// <summary>Hand that started the current/last grab (closest tracked index tip at select time).</summary>
        public HandSide GrabbingSide { get; private set; }

        public event Action Grabbed;
        public event Action Released;

        [Tooltip("Seconds of lost tracking during a grab before the grab counts as cancelled.")]
        public float trackingLossGrace = 0.2f;

        Grabbable[] grabbables = Array.Empty<Grabbable>();
        int selectCount;
        bool cancelFlag;
        bool sideKnown;
        float untrackedTime;
        bool subscribed;

        // ---- manual pinch-drag path (works even when ISDK poke/ray hover on a nearby canvas wins interactor priority)
        [Tooltip("Extra reach around the grab colliders for a pinch to start a manual drag (m).")]
        public float pinchReach = 0.03f;
        Collider[] grabZones = Array.Empty<Collider>();
        readonly bool[] prevPinch = new bool[2];
        bool manual;
        HandSide manualSide;
        Vector3 manualOffset;
        Quaternion manualRotOffset;
        static readonly int[] claimFrame = { -1, -1 };

        void CollectGrabZones()
        {
            var all = GetComponentsInChildren<Collider>(true);
            int n = 0;
            for (int i = 0; i < all.Length; i++) if (all[i].GetComponentInParent<Canvas>(true) == null) n++;
            grabZones = new Collider[n];
            n = 0;
            for (int i = 0; i < all.Length; i++) if (all[i].GetComponentInParent<Canvas>(true) == null) grabZones[n++] = all[i];
        }

        bool PinchInZone(Vector3 p)
        {
            float r2 = pinchReach * pinchReach;
            for (int i = 0; i < grabZones.Length; i++)
            {
                var c = grabZones[i];
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                Vector3 cp = c.ClosestPoint(p);
                if ((cp - p).sqrMagnitude <= r2) return true;
            }
            return false;
        }

        void UpdateManual()
        {
            var rig = HandRig.Instance;
            if (rig == null) return;
            for (int h = 0; h < 2; h++)
            {
                var side = (HandSide)h;
                bool pinch = rig.IsTracked(side) && rig.IsPinching(side);
                bool rising = pinch && !prevPinch[h];
                prevPinch[h] = pinch;
                if (manual)
                {
                    if (side != manualSide) continue;
                    if (!pinch || !rig.TryGetIndexTip(side, out var tipNow))
                    {
                        manual = false;
                        cancelFlag = !rig.IsTracked(side);
                        selectCount = 0;
                        EndGrab();
                        continue;
                    }
                    Quaternion handRot = rig.TryGetWrist(side, out var wp) ? wp.rotation : Quaternion.identity;
                    transform.SetPositionAndRotation(tipNow + handRot * manualOffset, handRot * manualRotOffset);
                    continue;
                }
                if (!rising || IsGrabbed || claimFrame[h] == Time.frameCount) continue;
                if (!rig.TryGetIndexTip(side, out var tip) || !PinchInZone(tip)) continue;
                claimFrame[h] = Time.frameCount;
                Quaternion hr = rig.TryGetWrist(side, out var w) ? w.rotation : Quaternion.identity;
                Quaternion inv = Quaternion.Inverse(hr);
                manualOffset = inv * (transform.position - tip);
                manualRotOffset = inv * transform.rotation;
                manual = true;
                manualSide = side;
                BeginGrab(tip);
                GrabbingSide = side; sideKnown = true;
            }
        }

        void LateUpdate() => UpdateManual();

        void Awake()
        {
            CollectGrabZones();
            grabbables = GetComponentsInChildren<Grabbable>(true);
            if (grabbables.Length == 0)
                Debug.LogWarning($"[GrabWatcher] no ISDK Grabbable found on '{name}' or its children");
        }

        void OnEnable()
        {
            if (subscribed) return;
            for (int i = 0; i < grabbables.Length; i++)
                if (grabbables[i] != null) grabbables[i].WhenPointerEventRaised += OnPointerEvent;
            subscribed = true;
        }

        void OnDisable()
        {
            if (subscribed)
            {
                for (int i = 0; i < grabbables.Length; i++)
                    if (grabbables[i] != null) grabbables[i].WhenPointerEventRaised -= OnPointerEvent;
                subscribed = false;
            }
            if (IsGrabbed)
            {
                // object hidden mid-grab: treat as cancel
                manual = false;
                selectCount = 0;
                cancelFlag = true;
                EndGrab();
            }
        }

        void OnPointerEvent(PointerEvent evt)
        {
            switch (evt.Type)
            {
                case PointerEventType.Select:
                    if (manual) { manual = false; } // ISDK took over: let the Grabbable drive the transform
                    selectCount++;
                    if (selectCount == 1 && !IsGrabbed) BeginGrab(evt.Pose.position);
                    break;
                case PointerEventType.Unselect:
                    if (selectCount > 0) selectCount--;
                    if (selectCount == 0 && IsGrabbed) EndGrab();
                    break;
                case PointerEventType.Cancel:
                    if (selectCount > 0)
                    {
                        selectCount--;
                        cancelFlag = true;
                    }
                    if (selectCount == 0 && IsGrabbed) EndGrab();
                    break;
            }
        }

        void BeginGrab(Vector3 at)
        {
            cancelFlag = false;
            untrackedTime = 0f;
            var rig = HandRig.Instance;
            HandSide side = HandSide.Right;
            sideKnown = rig != null && rig.TryGetClosestHand(at, out side);
            if (sideKnown) GrabbingSide = side;
            IsGrabbed = true;
            Grabbed?.Invoke();
        }

        void EndGrab()
        {
            var rig = HandRig.Instance;
            if (sideKnown && rig != null && !rig.IsTracked(GrabbingSide)) cancelFlag = true;
            LastReleaseCancelled = cancelFlag;
            IsGrabbed = false;
            cancelFlag = false;
            Released?.Invoke();
        }

        void Update()
        {
            if (!IsGrabbed || !sideKnown) return;
            var rig = HandRig.Instance;
            if (rig == null) return;
            if (rig.IsTracked(GrabbingSide)) untrackedTime = 0f;
            else
            {
                untrackedTime += Time.unscaledDeltaTime;
                if (untrackedTime >= trackingLossGrace) cancelFlag = true;
            }
        }
    }
}
