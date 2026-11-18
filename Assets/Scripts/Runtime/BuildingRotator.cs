using UnityEngine;

namespace HoldTheTable
{
    /// <summary>
    /// Hands-only rotation of placed buildings and the Town Hall: pinch next to a building (index tip within
    /// grabRadius of it) and move the pinching hand around it — the building turns to follow the hand's angle.
    /// Works outside combat waves too; tray tokens and the scroll are excluded by distance.
    /// </summary>
    public class BuildingRotator : MonoBehaviour
    {
        public static BuildingRotator Instance { get; private set; }
        public float grabRadius = 0.07f;
        public float minLever = 0.015f;

        readonly bool[] wasPinching = new bool[2];
        readonly Transform[] target = new Transform[2];
        readonly float[] lastAngle = new float[2];

        void Awake() { Instance = this; }

        void Update()
        {
            var rig = HandRig.Instance;
            var gd = GameDirector.Instance;
            if (rig == null || gd == null) return;
            bool allowed = gd.Phase == GamePhase.BuildPhase || gd.Phase == GamePhase.Wave || gd.Phase == GamePhase.Upgrade
                           || gd.Phase == GamePhase.Victory || gd.Phase == GamePhase.Defeat;
            for (int h = 0; h < 2; h++)
            {
                var side = (HandSide)h;
                bool pinch = allowed && rig.IsTracked(side) && rig.IsPinching(side) && rig.TryGetIndexTip(side, out _);
                rig.TryGetIndexTip(side, out var tip);
                if (pinch && !wasPinching[h]) Begin(h, tip);
                else if (pinch && target[h] != null) Drag(h, tip);
                else if (!pinch) target[h] = null;
                wasPinching[h] = pinch;
            }
        }

        void Begin(int h, Vector3 tip)
        {
            target[h] = null;
            float best = grabRadius * grabRadius;
            var units = PlacedUnit.All;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null) continue;
                float d = (u.transform.position + u.transform.up * 0.03f - tip).sqrMagnitude;
                if (d < best) { best = d; target[h] = u.transform; }
            }
            var hall = TownHall.Current;
            if (hall != null)
            {
                float d = (hall.transform.position + hall.transform.up * 0.06f - tip).sqrMagnitude;
                if (d < (grabRadius * 1.6f) * (grabRadius * 1.6f) && d < best * 2.56f) target[h] = hall.transform;
            }
            if (target[h] == null) return;
            Vector3 lever = Vector3.ProjectOnPlane(tip - target[h].position, target[h].up);
            if (lever.magnitude < minLever) { target[h] = null; return; }
            lastAngle[h] = Mathf.Atan2(lever.x, lever.z) * Mathf.Rad2Deg;
            ProceduralAudio.Instance?.Play(Sfx.Click);
        }

        void Drag(int h, Vector3 tip)
        {
            var t = target[h];
            Vector3 lever = Vector3.ProjectOnPlane(tip - t.position, t.up);
            if (lever.magnitude < minLever) return;
            float a = Mathf.Atan2(lever.x, lever.z) * Mathf.Rad2Deg;
            float delta = Mathf.DeltaAngle(lastAngle[h], a);
            lastAngle[h] = a;
            t.Rotate(t.up, delta, Space.World);
        }
    }
}
