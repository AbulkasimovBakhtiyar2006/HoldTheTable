using System.Collections;
using System.IO;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Device QA hook (leader-owned). Inactive unless an empty file named "stress_test" exists in
    /// Application.persistentDataPath (pushed via adb). Then it founds the empire automatically on the largest
    /// non-floor surface and keeps 30 monsters active so frame timing can be measured on the Quest.
    /// It is NOT a substitute for the hands-only playthrough.
    /// </summary>
    public class StressTestHook : MonoBehaviour
    {
        public int unitCount = 30;
        float logTimer; int frames; float worst;

        void Start()
        {
            if (!File.Exists(Path.Combine(Application.persistentDataPath, "stress_test"))) { enabled = false; return; }
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            Debug.Log("[Stress] armed");
            float t = 0f;
            while ((SurfaceRegistry.Instance == null || SurfaceRegistry.Instance.Surfaces.Count == 0 || t < 5f)) { t += Time.deltaTime; yield return null; }
            ITableSurface best = null; float bestArea = 0f;
            var list = SurfaceRegistry.Instance.Surfaces;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                float area = s.Size.x * s.Size.y;
                bool floorLike = s.Pose.position.y < 0.2f && !s.IsFallback;
                if (floorLike) area *= 0.01f;
                if (area > bestArea) { bestArea = area; best = s; }
            }
            var gd = GameDirector.Instance;
            gd.OnProfileConfirmed();
            yield return null;
            Vector3 c = best.Pose.position;
            var civ = CivilizationCatalog.Get(CivilizationProfile.Current.civId);
            var hall = BuildingFactory.Instance.SpawnTownHall(c, Quaternion.identity, civ);
            gd.OnHeadquartersPlaced(hall, best);
            hall.health.Init(1000000f);
            Debug.Log($"[Stress] founded on {best.Id} at {c}");

            int spawned = 0, guard = 0;
            while (spawned < unitCount && guard++ < 2000)
            {
                float a = Random.value * Mathf.PI * 2f;
                float r = Random.Range(0.25f, 0.45f);
                var p = best.ProjectOnto(c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
                if (!best.IsPointSupported(p)) continue;
                var m = MonsterUnit.Spawn(SplinePath.Build(p, c, best), spawned == unitCount - 1);
                if (m != null) spawned++;
                yield return new WaitForSeconds(0.05f);
            }
            Debug.Log($"[Stress] spawned {spawned}");
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            frames++; logTimer += dt; if (dt > worst) worst = dt;
            if (logTimer >= 1f)
            {
                Debug.Log($"[Stress] fps={frames / logTimer:F1} worstMs={worst * 1000f:F1} active={MonsterUnit.Active.Count}");
                frames = 0; logTimer = 0f; worst = 0f;
            }
        }
    }
}
