using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// Minimal on-screen run timer. Reads the scene <see cref="RunManager"/> (server-authoritative,
    /// replicated via SyncVars) and shows the live elapsed time, the final time once finished, and
    /// the best time. Dev HUD drawn with IMGUI so it needs no Canvas wiring; swap for a uGUI view
    /// during polish if desired.
    /// </summary>
    public class RunHud : MonoBehaviour
    {
        [SerializeField] private RunManager run;

        private GUIStyle _style;

        private void OnGUI()
        {
            if (run == null) return;
            _style ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };

            string line;
            if (run.Finished) line = $"FINISHED   {Format(run.Elapsed)}";
            else if (run.Running) line = $"TIME   {Format(run.Elapsed)}";
            else line = "Cross the start line to begin";

            GUI.Label(new Rect(20, 20, 600, 40), line, _style);
            if (run.HasBest)
                GUI.Label(new Rect(20, 56, 600, 40), $"BEST   {Format(run.BestElapsed)}", _style);
        }

        private static string Format(double t)
        {
            int minutes = (int)(t / 60);
            double seconds = t - minutes * 60;
            return $"{minutes:00}:{seconds:00.00}";
        }
    }
}
