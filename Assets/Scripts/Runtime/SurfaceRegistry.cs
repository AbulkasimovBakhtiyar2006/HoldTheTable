using System;
using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// All horizontal up-facing play surfaces of the room (MRUK + fallback) and crude obstacle volumes. Owner: Agent A.
    /// Registration is idempotent by Id, so MRUK re-acquisition never duplicates surfaces or resets the game.
    /// </summary>
    [DefaultExecutionOrder(-150)]
    public class SurfaceRegistry : MonoBehaviour
    {
        public static SurfaceRegistry Instance { get; private set; }

        struct Obstacle
        {
            public string ownerId;
            public Bounds bounds;
        }

        readonly List<ITableSurface> surfaces = new List<ITableSurface>();
        readonly List<Obstacle> obstacles = new List<Obstacle>();

        public IReadOnlyList<ITableSurface> Surfaces => surfaces;
        public event Action SurfacesChanged;

        /// <summary>Plane tolerance below the surface for "below" queries (hand slightly under the plane still counts).</summary>
        const float BelowTolerance = 0.03f;

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public bool HasNonFallback
        {
            get
            {
                for (int i = 0; i < surfaces.Count; i++) if (!surfaces[i].IsFallback) return true;
                return false;
            }
        }

        public ITableSurface Get(string id)
        {
            for (int i = 0; i < surfaces.Count; i++) if (surfaces[i].Id == id) return surfaces[i];
            return null;
        }

        public void Register(ITableSurface s)
        {
            if (s == null) return;
            for (int i = 0; i < surfaces.Count; i++)
            {
                if (surfaces[i].Id == s.Id)
                {
                    if (ReferenceEquals(surfaces[i], s)) return;
                    surfaces[i] = s; // same physical surface re-acquired: replace in place, no duplicate
                    SurfacesChanged?.Invoke();
                    return;
                }
            }
            surfaces.Add(s);
            Debug.Log($"[SurfaceRegistry] registered surface {s.Id} fallback={s.IsFallback} size={s.Size} y={s.Height:F2}");
            SurfacesChanged?.Invoke();
        }

        public void Unregister(string id)
        {
            for (int i = surfaces.Count - 1; i >= 0; i--)
            {
                if (surfaces[i].Id == id)
                {
                    surfaces.RemoveAt(i);
                    SurfacesChanged?.Invoke();
                }
            }
        }

        /// <summary>Registers/replaces a crude world AABB obstacle owned by an id (e.g. an MRUK volume anchor).</summary>
        public void RegisterObstacle(string ownerId, Bounds worldBounds)
        {
            for (int i = 0; i < obstacles.Count; i++)
            {
                if (obstacles[i].ownerId == ownerId)
                {
                    obstacles[i] = new Obstacle { ownerId = ownerId, bounds = worldBounds };
                    return;
                }
            }
            obstacles.Add(new Obstacle { ownerId = ownerId, bounds = worldBounds });
        }

        public void UnregisterObstacle(string ownerId)
        {
            for (int i = obstacles.Count - 1; i >= 0; i--)
                if (obstacles[i].ownerId == ownerId) obstacles.RemoveAt(i);
        }

        public int ObstacleCount => obstacles.Count;

        /// <summary>Read-only access to registered obstacle volumes (used by ClimbRoutes).</summary>
        public bool TryGetObstacle(int i, out string ownerId, out Bounds b)
        {
            if (i < 0 || i >= obstacles.Count) { ownerId = null; b = default; return false; }
            ownerId = obstacles[i].ownerId;
            b = obstacles[i].bounds;
            return true;
        }

        /// <summary>Highest supported surface whose plane is at most maxDist below w (or up to 3 cm above it).</summary>
        public bool TryFindSurfaceBelow(Vector3 w, float maxDist, out ITableSurface s, out Vector3 hit)
        {
            s = null;
            hit = w;
            float best = float.MaxValue;
            for (int i = 0; i < surfaces.Count; i++)
            {
                var surf = surfaces[i];
                var pose = surf.Pose;
                Vector3 up = pose.up;
                float d = Vector3.Dot(w - pose.position, up);
                if (d < -BelowTolerance || d > maxDist) continue;
                Vector3 p = w - up * d;
                if (!surf.IsPointSupported(p)) continue;
                if (d < best)
                {
                    best = d;
                    s = surf;
                    hit = p;
                }
            }
            return s != null;
        }

        /// <summary>Ray vs every surface plane (from above); nearest hit that lies on a supported part of the outline.</summary>
        public bool TryRaycast(Ray r, float maxDist, out ITableSurface s, out Vector3 hit)
        {
            s = null;
            hit = default;
            float best = maxDist;
            for (int i = 0; i < surfaces.Count; i++)
            {
                var surf = surfaces[i];
                var pose = surf.Pose;
                Vector3 up = pose.up;
                float denom = Vector3.Dot(r.direction, up);
                if (denom > -1e-4f) continue; // parallel or hitting from below
                float t = Vector3.Dot(pose.position - r.origin, up) / denom;
                if (t <= 0f || t >= best) continue;
                Vector3 p = r.origin + r.direction * t;
                if (!surf.IsPointSupported(p)) continue;
                best = t;
                s = surf;
                hit = p;
            }
            return s != null;
        }

        /// <summary>
        /// Crude AABB test of a cylinder footprint (center on surface, radius, height upward) against registered
        /// obstacle volumes, excluding the obstacle that owns the surface the footprint stands on.
        /// </summary>
        public bool OverlapsObstacle(Vector3 center, float radius, float height)
        {
            if (OverlapsClutter(center, radius, height)) return true;
            if (obstacles.Count == 0) return false;
            string ownSurface = null;
            if (TryFindSurfaceBelow(center + Vector3.up * 0.02f, 0.1f, out var below, out _)) ownSurface = below.Id;
            var box = new Bounds(center + Vector3.up * (0.01f + height * 0.5f), new Vector3(radius * 2f, Mathf.Max(0.005f, height - 0.02f), radius * 2f));
            for (int i = 0; i < obstacles.Count; i++)
            {
                var o = obstacles[i];
                if (ownSurface != null && o.ownerId == ownSurface) continue;
                var b = o.bounds;
                b.Expand(-0.02f); // shrink by 1 cm per side: scanned volumes are slightly generous
                if (b.size.x <= 0f || b.size.z <= 0f) continue;
                if (b.Intersects(box)) return true;
            }
            return false;
        }
            static readonly Vector2[] clutterSamples =
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, -1f),
            new Vector2(0.7f, 0.7f), new Vector2(-0.7f, 0.7f), new Vector2(0.7f, -0.7f), new Vector2(-0.7f, -0.7f),
        };

        /// <summary>
        /// Small objects lying on a surface (cup, laptop, books…) are not MRUK furniture anchors, but they are in the
        /// room's scanned global mesh. Cast straight down onto the scene mesh at the footprint center and rim:
        /// a hit noticeably above the surface means something is standing there. Objects moved after the room
        /// scan are not in the mesh (limitation of scene data).
        /// </summary>
        public bool OverlapsClutter(Vector3 center, float radius, float height)
        {
#if HTT_MRUK
            var mruk = Meta.XR.MRUtilityKit.MRUK.Instance;
            if (mruk == null || !mruk.IsInitialized) return false;
            var room = mruk.GetCurrentRoom();
            if (room == null) return false;
            var filter = Meta.XR.MRUtilityKit.LabelFilter.Included(Meta.XR.MRUtilityKit.MRUKAnchor.SceneLabels.GLOBAL_MESH);
            float top = Mathf.Max(0.12f, height) + 0.25f;
            for (int i = 0; i < clutterSamples.Length; i++)
            {
                var s = clutterSamples[i] * (radius * 0.9f);
                var origin = center + new Vector3(s.x, top, s.y);
                if (room.Raycast(new Ray(origin, Vector3.down), top + 0.05f, filter, out RaycastHit hit))
                {
                    float rise = hit.point.y - center.y;
                    if (rise > 0.022f && rise < top - 0.01f) return true;
                }
            }
#endif
            return false;
        }
    }
}
