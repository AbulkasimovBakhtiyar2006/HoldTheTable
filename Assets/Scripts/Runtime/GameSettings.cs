using System;
using UnityEngine;

namespace HoldTheTable
{
    /// <summary>Persistent player settings (offline, PlayerPrefs). Owner: Leader.</summary>
    public static class GameSettings
    {
        const string MenuHandKey = "htt_menu_hand"; // 0 = right-handed (build menu on RIGHT wrist), 1 = left-handed (menu on LEFT wrist)

        public static event Action Changed;

        /// <summary>Wrist that carries the build menu. The other hand pokes buttons and places buildings.</summary>
        public static HandSide MenuHand
        {
            get { return PlayerPrefs.GetInt(MenuHandKey, 0) == 1 ? HandSide.Left : HandSide.Right; }
            set
            {
                int v = value == HandSide.Left ? 1 : 0;
                if (PlayerPrefs.GetInt(MenuHandKey, 0) == v) return;
                PlayerPrefs.SetInt(MenuHandKey, v);
                PlayerPrefs.Save();
                Changed?.Invoke();
            }
        }

        /// <summary>The hand that interacts with the build menu / places buildings.</summary>
        public static HandSide ActionHand { get { return MenuHand == HandSide.Right ? HandSide.Left : HandSide.Right; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Changed = null; }
    }
}
