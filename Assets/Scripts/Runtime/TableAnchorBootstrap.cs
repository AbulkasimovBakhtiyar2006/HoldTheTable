using System;
using System.Collections;
using UnityEngine;
#if HTT_MRUK
using Meta.XR.MRUtilityKit;
#endif

namespace HoldTheTable
{
    /// <summary>
    /// Waits up to <see cref="timeout"/> seconds for MRUK rooms, registers every suitable horizontal surface of the
    /// room, otherwise (or when forced) spawns the procedural FallbackTable. Re-acquired rooms re-register idempotently,
    /// never duplicating surfaces nor resetting the game. Also creates a session spatial anchor at the founded hall.
    /// Owner: Agent A.
    /// </summary>
    public class TableAnchorBootstrap : MonoBehaviour
    {
        public static TableAnchorBootstrap Instance { get; private set; }

        public const string ForceFallbackPrefKey = "htt_force_fallback";

        /// <summary>Global switch (tests / QA): forces the fallback table path even if MRUK is present.</summary>
        public static bool ForceFallback;

        [Tooltip("Seconds to wait for MRUK scene data before spawning the fallback table.")]
        public float timeout = 3f;
        [Tooltip("Force the fallback table path (also via static ForceFallback or PlayerPrefs 'htt_force_fallback'=1).")]
        public bool forceFallback;
        [Tooltip("Minimum size (m) of the smaller side of a plane to count as a play surface.")]
        public float minSurfaceSize = 0.3f;
        [Tooltip("World Y of the physical floor when no MRUK floor is known (floor-level tracking origin).")]
        public float floorY = 0f;
        [Tooltip("Head transform; defaults to Camera.main.")]
        [OptionalRef] public Transform head;

        public bool IsReady { get; private set; }
        public bool UsingFallback { get; private set; }
        public FallbackTable Fallback { get; private set; }
        public event Action Ready;

        GameObject settlementAnchorGo;

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
#if HTT_MRUK
            if (MRUK.Instance != null)
            {
                MRUK.Instance.RoomCreatedEvent.RemoveListener(OnRoomChanged);
                MRUK.Instance.RoomUpdatedEvent.RemoveListener(OnRoomChanged);
            }
#endif
        }

        Transform Head
        {
            get
            {
                if (head == null && Camera.main != null) head = Camera.main.transform;
                return head;
            }
        }

        // Device QA hook: `adb push` an empty file named force_fallback into Application.persistentDataPath.
        bool FallbackForced => forceFallback || ForceFallback || PlayerPrefs.GetInt(ForceFallbackPrefKey, 0) == 1
                               || System.IO.File.Exists(System.IO.Path.Combine(Application.persistentDataPath, "force_fallback"));

        IEnumerator Start()
        {
            // let the XR rig settle a frame so the head pose is real
            yield return null;
            bool force = FallbackForced;
            Debug.Log($"[TableAnchorBootstrap] start forceFallback={force} timeout={timeout}s");
#if HTT_MRUK
            if (!force)
            {
                float t = 0f;
                while (t < timeout)
                {
                    var m = MRUK.Instance;
                    if (m != null && m.IsInitialized && m.Rooms != null && m.Rooms.Count > 0) break;
                    t += Time.unscaledDeltaTime;
                    yield return null;
                }
                var mr = MRUK.Instance;
                if (mr != null)
                {
                    mr.RoomCreatedEvent.AddListener(OnRoomChanged);
                    mr.RoomUpdatedEvent.AddListener(OnRoomChanged);
                    if (mr.IsInitialized) RegisterAllRooms();
                }
                else Debug.LogWarning("[TableAnchorBootstrap] MRUK.Instance is null");
            }
#else
            if (!force) Debug.Log("[TableAnchorBootstrap] built without HTT_MRUK; using fallback table");
#endif
            var reg = SurfaceRegistry.Instance;
            if (force || reg == null || reg.Surfaces.Count == 0) SpawnFallback();
            IsReady = true;
            Debug.Log($"[TableAnchorBootstrap] ready surfaces={(reg != null ? reg.Surfaces.Count : 0)} fallback={UsingFallback}");
            Ready?.Invoke();
        }

#if HTT_MRUK
        void OnRoomChanged(MRUKRoom room)
        {
            if (FallbackForced) return;
            RegisterRoom(room);
        }

        void RegisterAllRooms()
        {
            var rooms = MRUK.Instance.Rooms;
            for (int i = 0; i < rooms.Count; i++) RegisterRoom(rooms[i]);
        }

        void RegisterRoom(MRUKRoom room)
        {
            var reg = SurfaceRegistry.Instance;
            if (room == null || reg == null) return;
            int added = 0, obstacles = 0;
            var anchors = room.Anchors;
            for (int i = 0; i < anchors.Count; i++)
            {
                var a = anchors[i];
                if (a == null) continue;
                var s = MrukSurface.TryCreate(a, minSurfaceSize);
                if (s != null)
                {
                    reg.Register(s);
                    added++;
                }
                if (MrukSurface.TryGetWorldVolume(a, out var b))
                {
                    reg.RegisterObstacle(MrukSurface.IdFor(a), b);
                    obstacles++;
                }
                if (s != null && a.HasAnyLabel(MRUKAnchor.SceneLabels.FLOOR)) floorY = s.Height;
            }
            Debug.Log($"[TableAnchorBootstrap] MRUK room '{room.name}': {added} surfaces, {obstacles} obstacle volumes");
        }
#endif

        public void SpawnFallback()
        {
            var reg = SurfaceRegistry.Instance;
            if (Fallback != null)
            {
                if (reg != null) reg.Register(Fallback);
                return;
            }
            var h = Head;
            Vector3 headPos = h != null ? h.position : new Vector3(0f, 1.2f, 0f);
            Vector3 fwd = h != null ? Vector3.ProjectOnPlane(h.forward, Vector3.up) : Vector3.forward;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            float y = Mathf.Max(headPos.y - 0.72f, floorY + 0.65f);
            var center = new Vector3(headPos.x, y, headPos.z) + fwd * 0.45f;
            Fallback = FallbackTable.Spawn(center, Quaternion.LookRotation(fwd, Vector3.up), new Vector2(0.9f, 0.6f), floorY);
            UsingFallback = true;
            if (reg != null) reg.Register(Fallback);
            Debug.Log($"[TableAnchorBootstrap] fallback table spawned at {center}");
        }

        /// <summary>Session-local spatial anchor at the founded settlement. Failure never blocks the game; result is logged.</summary>
        public void AnchorSettlement(Vector3 pos, Quaternion rot)
        {
            ClearSettlementAnchor();
            settlementAnchorGo = new GameObject("SettlementSpatialAnchor");
            settlementAnchorGo.transform.SetPositionAndRotation(pos, rot);
            if (Application.isEditor || !OVRPlugin.initialized || !OVRManager.isHmdPresent)
            {
                Debug.Log("[TableAnchorBootstrap] spatial anchor skipped (no headset runtime); session-local transform used");
                return;
            }
            try
            {
                var anchor = settlementAnchorGo.AddComponent<OVRSpatialAnchor>();
                WaitAnchor(anchor);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TableAnchorBootstrap] spatial anchor unavailable: " + e.Message);
            }
        }

        async void WaitAnchor(OVRSpatialAnchor anchor)
        {
            bool ok = false;
            try { ok = await anchor.WhenCreatedAsync(); }
            catch (Exception e) { Debug.LogWarning("[TableAnchorBootstrap] spatial anchor failed: " + e.Message); }
            if (anchor == null) return;
            if (ok) Debug.Log("[TableAnchorBootstrap] spatial anchor created uuid=" + anchor.Uuid);
            else Debug.LogWarning("[TableAnchorBootstrap] spatial anchor creation failed; continuing with session-local transform");
        }

        public void ClearSettlementAnchor()
        {
            if (settlementAnchorGo != null) Destroy(settlementAnchorGo);
            settlementAnchorGo = null;
        }
    }
}
