using UnityEngine;

namespace HoldTheTable
{
    /// <summary>Hides the building tray base until the empire is founded (the tray is placed beside the settlement then).</summary>
    public class TrayVisibility : MonoBehaviour
    {
        public Renderer baseRenderer;

        void Update()
        {
            var gd = GameDirector.Instance;
            bool show = gd != null && (gd.Phase == GamePhase.BuildPhase || gd.Phase == GamePhase.Wave || gd.Phase == GamePhase.Upgrade);
            if (baseRenderer != null && baseRenderer.enabled != show) baseRenderer.enabled = show;
        }
    }
}
