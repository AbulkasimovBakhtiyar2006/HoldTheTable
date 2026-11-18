using UnityEngine;

namespace HoldTheTable
{
    /// <summary>Score = kills*10 + ceil(hall HP)*25 + floor(timber) + floor(gold). Owner: Agent C.</summary>
    public class ScoreKeeper : MonoBehaviour
    {
        static ScoreKeeper instance;
        static bool quitting;
        public static ScoreKeeper Instance
        {
            get
            {
                if (instance == null && !quitting && Application.isPlaying)
                    instance = new GameObject("ScoreKeeper").AddComponent<ScoreKeeper>();
                return instance;
            }
        }

        public int Kills { get; private set; }
        public int LastScore { get; private set; }
        public event System.Action Changed;

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(this); return; }
            instance = this;
        }

        void OnEnable() => MonsterUnit.Killed += OnKilled;
        void OnDisable() => MonsterUnit.Killed -= OnKilled;
        void OnApplicationQuit() => quitting = true;
        void OnDestroy() { if (instance == this) instance = null; }

        void OnKilled(MonsterUnit m)
        {
            var gd = GameDirector.Instance;
            if (gd != null && (gd.Phase == GamePhase.Victory || gd.Phase == GamePhase.Defeat)) return;
            Kills++;
            Changed?.Invoke();
        }

        public void ResetScore()
        {
            Kills = 0;
            LastScore = 0;
            Changed?.Invoke();
        }

        public int Compute()
        {
            var gd = GameDirector.Instance;
            float hallHp = 0f, timber = 0f, gold = 0f;
            if (gd != null)
            {
                if (gd.Hall != null && gd.Hall.health != null) hallHp = Mathf.Max(0f, gd.Hall.health.Current);
                timber = Mathf.Max(0f, gd.Timber);
                gold = Mathf.Max(0f, gd.Gold);
            }
            LastScore = Kills * 10 + Mathf.CeilToInt(hallHp) * 25 + Mathf.FloorToInt(timber) + Mathf.FloorToInt(gold);
            return LastScore;
        }
    }
}
