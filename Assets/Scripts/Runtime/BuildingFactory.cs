using System.Collections.Generic;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Creates the town hall and placed units with the current civilization's architecture. Owner: Agent B.
    /// Self-contained: no inspector references required. Put one in the scene (leader).
    /// </summary>
    public class BuildingFactory : MonoBehaviour
    {
        public static BuildingFactory Instance;

        readonly List<PlacedUnit> units = new List<PlacedUnit>();
        Transform settlementRoot;
        TownHall hall;

        public IReadOnlyList<PlacedUnit> Units => units;
        public TownHall Hall => hall;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            EnsureRoot();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        Transform EnsureRoot()
        {
            if (settlementRoot == null)
            {
                settlementRoot = new GameObject("Settlement").transform;
                settlementRoot.SetParent(transform, false);
            }
            return settlementRoot;
        }

        static CivilizationDefinition CurrentCiv()
        {
            var prof = CivilizationProfile.Current;
            if (prof == null || string.IsNullOrEmpty(prof.civId)) return null;
            return CivilizationCatalog.Get(prof.civId);
        }

        /// <summary>Creates (or replaces) the single Imperial House. Never produces duplicates.</summary>
        public TownHall SpawnTownHall(Vector3 pos, Quaternion rot, CivilizationDefinition civ)
        {
            DestroyTownHall();
            var go = new GameObject("TownHall");
            go.transform.SetParent(EnsureRoot(), true);
            go.transform.SetPositionAndRotation(pos, rot);
            hall = go.AddComponent<TownHall>();
            hall.Init(civ != null ? civ : CurrentCiv());
            if (VfxPool.Instance != null) VfxPool.Instance.Ring(pos, BuildingVisuals.HallFootprintRadius * 1.5f);
            if (ProceduralAudio.Instance != null) ProceduralAudio.Instance.Play(Sfx.Place);
            MonsterUnit.Prewarm(20);
            return hall;
        }

        /// <summary>Spawns a defensive unit in the current civilization's style and registers it.</summary>
        public PlacedUnit SpawnUnit(UnitType type, Vector3 pos, Quaternion rot)
        {
            var go = new GameObject(type.ToString());
            go.transform.SetParent(EnsureRoot(), true);
            go.transform.SetPositionAndRotation(pos, rot);
            var u = go.AddComponent<PlacedUnit>();
            u.Init(type, CurrentCiv());
            if (type == UnitType.AntiAir) AntiAirTower.Attach(u, CurrentCiv());
            units.Add(u);
            return u;
        }

        /// <summary>Visual-only half-scale town hall for the placement token (no colliders, no logic).</summary>
        public GameObject BuildMiniTownHall(Transform parent, CivilizationDefinition civ)
        {
            var root = BuildingVisuals.BuildTownHall(parent, civ != null ? civ : CurrentCiv());
            root.name = "MiniTownHall";
            root.localScale = Vector3.one * 0.5f;
            BuildingVisuals.StripColliders(root.gameObject);
            return root.gameObject;
        }

        /// <summary>Visual-only half-scale unit (e.g. for tray tokens / ghosts). No colliders, no logic.</summary>
        public GameObject BuildMiniUnit(Transform parent, UnitType type, float scale = 0.5f)
        {
            Transform root;
            if (type == UnitType.AntiAir) root = AntiAirTower.BuildVisual(parent, CurrentCiv(), out _, out _, out _);
            else root = BuildingVisuals.BuildUnit(parent, type, CurrentCiv(), out _);
            root.name = "Mini" + type;
            root.localScale = Vector3.one * scale;
            BuildingVisuals.StripColliders(root.gameObject);
            return root.gameObject;
        }

        /// <summary>Called by PlacedUnit when its health dies.</summary>
        public void OnUnitDestroyed(PlacedUnit u)
        {
            units.Remove(u);
            if (u != null) Destroy(u.gameObject);
        }

        /// <summary>Destroys every placed unit and in-flight arrow (restart / new empire).</summary>
        public void ClearUnits()
        {
            for (int i = units.Count - 1; i >= 0; i--)
                if (units[i] != null) Destroy(units[i].gameObject);
            units.Clear();
            Projectile.DespawnAll();
            AntiAirTower.DespawnAllMissiles();
        }

        public void DestroyTownHall()
        {
            if (hall != null) Destroy(hall.gameObject);
            hall = null;
        }

        /// <summary>Counts live placed units of a type (Quartermaster stats helper).</summary>
        public int CountOf(UnitType t)
        {
            int n = 0;
            for (int i = 0; i < units.Count; i++) if (units[i] != null && units[i].type == t) n++;
            return n;
        }
    }
}
