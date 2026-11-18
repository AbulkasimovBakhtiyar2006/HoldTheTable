using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Spawns waves (6/10/16, one Ram mid wave 3). With a scanned MRUK room: round-robin from the room-corner
    /// portals (RoomCornerSpawns) along floor → climb → surface routes. Without a room (fallback table / no scan):
    /// at the local battlefield boundary around the settlement, a ring of radius min(0.55, distance to nearest edge)
    /// around the Town Hall, clipped to supported points. Owner: Agent C, room-corner mode: Agent B.
    /// </summary>
    public class WaveSpawner : MonoBehaviour
    {
        public static readonly int[] WaveCounts = { 6, 10, 16 };
        public const int WaveTotal = 3;
        public const float MaxRingRadius = 0.55f;
        public const float MinRingRadius = 0.18f;
        const int CandidateAngles = 24;

        static WaveSpawner instance;
        static bool quitting;
        public static WaveSpawner Instance
        {
            get
            {
                if (instance == null && !quitting && Application.isPlaying)
                    instance = new GameObject("WaveSpawner").AddComponent<WaveSpawner>();
                return instance;
            }
        }

        [Tooltip("Seconds between spawns inside a wave.")]
        public float spawnInterval = 1.2f;

        readonly List<Vector3> spawnPoints = new List<Vector3>();
        readonly List<Vector3> scratch = new List<Vector3>();

        int toSpawn, spawned, ramAt = -1, waveIndex, nextPoint;
        float timer;
        bool running;
        bool roomMode;
        float floorY;
        int engineersLeft;
        // harpooners / climb squad (table mode)
        public const float HarpoonHoldMax = 7f, HarpoonStartDist = 1.1f, TableFloorDrop = 0.75f;
        int harpoonersLeft, harpoonerIndex, tableSquadLeft;
        float harpTimer, holdUntil, climbFloorY;
        bool climbMode;
        Vector3 squadOrigin;
        Vector3 hallPos;
        ITableSurface surface;

        public bool Running => running;
        public int Spawned => spawned;
        public int ToSpawn => toSpawn;
        public bool AllSpawned => spawned >= toSpawn;
        public bool WaveCleared => running && spawned >= toSpawn && MonsterUnit.Active.Count == 0;
        public IReadOnlyList<Vector3> SpawnPoints => spawnPoints;
        /// <summary>True when this wave spawns from the scanned room's corner portals.</summary>
        public bool RoomMode => roomMode;
        public event System.Action RamSpawned;

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(this); return; }
            instance = this;
        }

        void OnApplicationQuit() => quitting = true;
        void OnDestroy() { if (instance == this) instance = null; }

        public static int CountFor(int wave) => WaveCounts[Mathf.Clamp(wave, 0, WaveCounts.Length - 1)];

        public void BeginWave(int index, Vector3 hall, ITableSurface s)
        {
            waveIndex = Mathf.Clamp(index, 0, WaveCounts.Length - 1);
            hallPos = hall;
            surface = s;
            toSpawn = WaveCounts[waveIndex];
            spawned = 0;
            timer = 0.4f;
            running = true;
            ComputeSpawnPoints();
            SetupHarpooners();
            // engineers (room mode, raised hall only): 1 in wave 2, 2 in wave 3, spawned first, in addition to the count
            engineersLeft = 0;
            if (ClimbRoutes.Instance != null) ClimbRoutes.Instance.OnWaveStart();
            if (roomMode)
            {
                bool raised = surface != null && surface.Pose.position.y > floorY + 0.15f;
                if (raised) engineersLeft = waveIndex == 1 ? 1 : waveIndex >= 2 ? 2 : 0;
            }
            int baseCount = toSpawn;
            toSpawn += engineersLeft;
            ramAt = waveIndex == WaveCounts.Length - 1 ? engineersLeft + baseCount / 2 : -1;
            nextPoint = Random.Range(0, Mathf.Max(1, spawnPoints.Count));
            Debug.Log($"[WaveSpawner] Wave {waveIndex + 1}: {toSpawn} monsters, {spawnPoints.Count} spawn points ({(roomMode ? "room corners" : "settlement edge")}), ram@{ramAt}");
        }

        public void StopWave()
        {
            running = false;
            harpoonersLeft = 0;
            tableSquadLeft = 0;
            toSpawn = 0;
            spawned = 0;
        }

        void Update()
        {
            if (!running) return;
            // Harpooners always go first: one per wave minimum, more on bigger waves.
            if (harpoonersLeft > 0)
            {
                harpTimer -= Time.deltaTime;
                if (harpTimer <= 0f)
                {
                    harpTimer = 0.5f;
                    SpawnHarpooner();
                    harpoonersLeft--;
                    holdUntil = Time.time + HarpoonHoldMax;
                }
                return;
            }
            if (spawned >= toSpawn) return;
            // main troops wait (bounded) until the harpoon ropes are up, so everyone climbs a rope from the start
            var cr0 = ClimbRoutes.Existing;
            if (cr0 != null && cr0.PendingRopeCount > 0 && Time.time < holdUntil) return;
            timer -= Time.deltaTime;
            if (timer > 0f) return;
            timer = spawnInterval;
            SpawnOne(spawned == ramAt);
            spawned++;
        }

        void SpawnOne(bool ram)
        {
            if (spawnPoints.Count == 0) ComputeSpawnPoints();
            Vector3 pos = spawnPoints[nextPoint % spawnPoints.Count];
            if (roomMode && engineersLeft > 0)
            {
                engineersLeft--;
                nextPoint = (nextPoint + 1) % spawnPoints.Count;
                if (VfxPool.Instance != null) VfxPool.Instance.Ring(pos + Vector3.up * 0.01f, 0.2f);
                MonsterUnit.SpawnEngineer(pos, hallPos, surface, floorY);
                return;
            }
            SplinePath path;
            if (!ram && tableSquadLeft > 0 && climbMode)
            {
                // table mode: part of the wave arrives from the floor and must climb the harpoon rope
                tableSquadLeft--;
                Vector3 sp = squadOrigin + new Vector3(Random.Range(-0.12f, 0.12f), 0f, Random.Range(-0.12f, 0.12f));
                sp.y = climbFloorY;
                if (VfxPool.Instance != null) VfxPool.Instance.Ring(sp + Vector3.up * 0.01f, 0.2f);
                var mc = MonsterUnit.Spawn(SplinePath.BuildRoute(sp, hallPos, surface, climbFloorY), false);
                if (mc == null) Debug.LogWarning("[WaveSpawner] climber spawn returned null");
                return;
            }
            if (roomMode)
            {
                // round-robin across the room-corner portals
                nextPoint = (nextPoint + 1) % spawnPoints.Count;
                path = SplinePath.BuildRoute(pos, hallPos, surface, floorY);
                if (VfxPool.Instance != null) VfxPool.Instance.Ring(pos + Vector3.up * 0.01f, 0.2f);
            }
            else
            {
                // Next spawn from a different, roughly random point so monsters arrive from several directions.
                nextPoint = (nextPoint + 1 + Random.Range(0, Mathf.Max(1, spawnPoints.Count / 2))) % spawnPoints.Count;
                path = SplinePath.Build(pos, hallPos, surface);
            }
            var m = MonsterUnit.Spawn(path, ram);
            if (m == null) { Debug.LogWarning("[WaveSpawner] MonsterUnit.Spawn returned null"); return; }
            if (ram)
            {
                RamSpawned?.Invoke();
                if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.WaveStart, pos);
            }
        }

        void SetupHarpooners()
        {
            harpoonersLeft = 0; tableSquadLeft = 0; harpoonerIndex = 0; harpTimer = 0f; holdUntil = 0f; climbMode = false;
            if (surface == null) return;
            if (roomMode) climbFloorY = floorY;
            else climbFloorY = surface.Pose.position.y - TableFloorDrop; // table mode: floor ~75 cm below the tabletop
            climbMode = surface.Pose.position.y > climbFloorY + 0.15f;
            if (!climbMode) return;
            harpoonersLeft = Mathf.Clamp(1 + waveIndex, 1, 3);
            if (!roomMode) tableSquadLeft = Mathf.Min(4, Mathf.Max(2, toSpawn / 3));
        }

        void SpawnHarpooner()
        {
            if (surface == null) return;
            var cr = ClimbRoutes.Instance;
            if (cr == null) return;
            Vector3 corner;
            if (roomMode && spawnPoints.Count > 0)
            {
                corner = spawnPoints[(nextPoint + harpoonerIndex) % spawnPoints.Count];
            }
            else
            {
                Vector3 away = Vector3.forward;
                var cam = Camera.main;
                if (cam != null) { away = hallPos - cam.transform.position; away.y = 0f; }
                if (away.sqrMagnitude < 1e-4f) away = Vector3.forward;
                corner = hallPos + (Quaternion.Euler(0f, harpoonerIndex * 50f - 25f, 0f) * away.normalized) * 1.6f;
            }
            corner.y = climbFloorY;
            var site = cr.ReserveRopeSite(corner, hallPos, surface, climbFloorY, harpoonerIndex);
            harpoonerIndex++;
            if (site == null) return;
            // start within HarpoonStartDist of the firing spot so the rope goes up within seconds
            Vector3 toCorner = corner - site.shootPos; toCorner.y = 0f;
            Vector3 start = toCorner.magnitude > HarpoonStartDist ? site.shootPos + toCorner.normalized * HarpoonStartDist : corner;
            start.y = climbFloorY;
            if (harpoonerIndex == 1) squadOrigin = start;
            if (VfxPool.Instance != null) VfxPool.Instance.Ring(start + Vector3.up * 0.01f, 0.2f);
            var m = MonsterUnit.SpawnHarpooner(start, site, hallPos, surface, climbFloorY);
            if (m == null) cr.ScheduleFallbackRope(site, 0.1f);
        }

        /// <summary>Local battlefield boundary around the hall, restricted to supported surface points.</summary>
        void ComputeSpawnPoints()
        {
            spawnPoints.Clear();
            scratch.Clear();
            roomMode = false;
            var rcs = RoomCornerSpawns.Instance;
            if (rcs != null && rcs.Refresh() && rcs.Corners.Count > 0)
            {
                roomMode = true;
                floorY = rcs.FloorY;
                for (int i = 0; i < rcs.Corners.Count; i++) spawnPoints.Add(rcs.Corners[i]);
                return;
            }
            if (surface == null) { spawnPoints.Add(hallPos + Vector3.forward * 0.3f); return; }

            Vector3 up = surface.Pose.up;
            Vector3 right = Vector3.ProjectOnPlane(surface.Pose.rotation * Vector3.right, up).normalized;
            if (right.sqrMagnitude < 0.5f) right = Vector3.Cross(up, Vector3.forward).normalized;
            Vector3 fwd = Vector3.Cross(right, up).normalized;

            Vector3 edge = surface.ClosestEdgePoint(hallPos);
            float edgeDist = Vector3.ProjectOnPlane(edge - hallPos, up).magnitude;
            float radius = Mathf.Min(MaxRingRadius, edgeDist - 0.03f);
            bool smallTable = radius < MinRingRadius;
            if (smallTable) radius = MaxRingRadius;

            Vector3 toCam = Vector3.zero;
            var cam = Camera.main;
            if (cam != null) toCam = Vector3.ProjectOnPlane(cam.transform.position - hallPos, up).normalized;

            for (int i = 0; i < CandidateAngles; i++)
            {
                float a = i * Mathf.PI * 2f / CandidateAngles;
                Vector3 dir = right * Mathf.Cos(a) + fwd * Mathf.Sin(a);
                Vector3 p = surface.ProjectOnto(hallPos + dir * radius);
                if (smallTable || !surface.IsPointSupported(p))
                {
                    // Small table / irregular shape: walk to the real edge in this direction, then step inward.
                    Vector3 e = surface.ClosestEdgePoint(hallPos + dir * radius);
                    Vector3 inward = Vector3.ProjectOnPlane(hallPos - e, up);
                    if (inward.sqrMagnitude < 1e-6f) continue;
                    p = surface.ProjectOnto(e + inward.normalized * 0.03f);
                    if (!surface.IsPointSupported(p)) continue;
                }
                float d = Vector3.ProjectOnPlane(p - hallPos, up).magnitude;
                if (d < 0.15f) continue;
                bool dup = false;
                for (int k = 0; k < scratch.Count; k++) if ((scratch[k] - p).sqrMagnitude < 0.0016f) { dup = true; break; }
                if (dup) continue;
                var reg = SurfaceRegistry.Instance;
                if (reg != null && reg.OverlapsObstacle(p, 0.03f, 0.06f)) continue;
                scratch.Add(p);
                // Prefer the far side of the settlement (not spawning between the player and the hall).
                if (toCam.sqrMagnitude > 0.5f && Vector3.Dot(dir, toCam) > 0.7f) continue;
                spawnPoints.Add(p);
            }
            if (spawnPoints.Count < 3) { spawnPoints.Clear(); spawnPoints.AddRange(scratch); }
            if (spawnPoints.Count == 0)
            {
                Vector3 away = toCam.sqrMagnitude > 0.5f ? -toCam : fwd;
                spawnPoints.Add(surface.ProjectOnto(hallPos + away * 0.3f));
            }
        }
    }
}
