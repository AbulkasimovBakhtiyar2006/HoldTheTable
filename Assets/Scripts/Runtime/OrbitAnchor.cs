using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Keeps a world-space panel on a personal orbit around the player's head: fixed radius, chosen yaw/pitch,
    /// always facing the player, lazily following when the player turns away. Grabbing (ISDK Grabbable via
    /// GrabWatcher) lets the player propose a new spot; on release the spot is projected back onto the orbit.
    /// Waits for a valid head pose so nothing spawns at the tracking origin (floor).
    /// </summary>
    public class OrbitAnchor : MonoBehaviour
    {
        [Tooltip("Yaw relative to the player's view, degrees (+ = right).")]
        public float yaw = 25f;
        [Tooltip("Pitch relative to eye level, degrees (- = below).")]
        public float pitch = -15f;
        public float radius = 0.5f;
        public float minRadius = 0.3f, maxRadius = 0.7f;
        public float minPitch = -55f, maxPitch = 25f;
        [Tooltip("Head turn beyond this many degrees from the panel's reference makes it follow.")]
        public float followAngle = 50f;
        public float sharpness = 8f;
        [OptionalRef] public GrabWatcher grab;

        Transform head;
        bool ready;
        float refYaw;          // head yaw the panel is anchored to
        float startTime;

        public bool IsReady => ready;

        void Awake()
        {
            if (grab == null) grab = GetComponentInChildren<GrabWatcher>(true);
            startTime = Time.time;
        }

        void OnEnable()
        {
            if (grab == null) grab = GetComponentInChildren<GrabWatcher>(true);
            if (grab != null) grab.Released += OnReleased;
        }

        void OnDisable()
        {
            if (grab != null) grab.Released -= OnReleased;
        }

        static float HeadYaw(Transform h)
        {
            Vector3 f = h.forward; f.y = 0f;
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        bool HeadValid()
        {
            if (head == null) { var c = Camera.main; if (c == null) return false; head = c.transform; }
            // tracking origin is the floor: a real seated/standing head is well above it
            return head.position.y > 0.4f || Time.time - startTime > 4f;
        }

        Vector3 TargetPosition()
        {
            Quaternion q = Quaternion.Euler(-pitch, refYaw + yaw, 0f);
            return head.position + q * Vector3.forward * radius;
        }

        /// <summary>Snap in front of the player now (used on open / new empire).</summary>
        public void Recenter()
        {
            if (!HeadValid()) return;
            refYaw = HeadYaw(head);
            transform.position = TargetPosition();
            Face(1f);
            ready = true;
        }

        void OnReleased()
        {
            if (head == null) return;
            Vector3 v = transform.position - head.position;
            float d = v.magnitude;
            if (d < 1e-3f) return;
            radius = Mathf.Clamp(d, minRadius, maxRadius);
            refYaw = HeadYaw(head);
            float worldYaw = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
            yaw = Mathf.DeltaAngle(refYaw, worldYaw);
            pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(v.y / d, -1f, 1f)) * Mathf.Rad2Deg, minPitch, maxPitch);
        }

        void Face(float k)
        {
            Vector3 look = transform.position - head.position;
            if (look.sqrMagnitude < 1e-6f) return;
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(look.normalized, Vector3.up), k);
        }

        void LateUpdate()
        {
            if (!ready) { Recenter(); return; }
            if (grab != null && grab.IsGrabbed) return; // the hand proposes a spot freely; projected on release

            float hy = HeadYaw(head);
            if (Mathf.Abs(Mathf.DeltaAngle(refYaw + yaw, hy)) > followAngle + Mathf.Abs(yaw))
                refYaw = Mathf.MoveTowardsAngle(refYaw, hy, 180f * Time.deltaTime);

            float k = 1f - Mathf.Exp(-sharpness * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, TargetPosition(), k);
            Face(k);
        }
    }
}
