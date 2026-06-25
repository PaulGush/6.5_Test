using Mirror;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using Game.Player;

namespace Game.Gameplay
{
    /// <summary>
    /// Screen-space uGUI HUD for the run: a big live timer, a state line (prompt / FINISHED), the best
    /// time, and a Restart control. Reads the scene <see cref="RunManager"/> (a scene reference, fine for
    /// a scene HUD). Restart is routed through the local player's owner Command (the only client-owned
    /// authority) via <see cref="NetworkPlayer.RequestRestart"/>; also bound to the R key.
    /// </summary>
    public class RunHudView : MonoBehaviour
    {
        [SerializeField] private RunManager run;
        [SerializeField] private Text timerText;
        [SerializeField] private Text stateText;
        [SerializeField] private Text bestText;
        [SerializeField] private Button restartButton;

        private void Awake()
        {
            if (restartButton != null) restartButton.onClick.AddListener(Restart);
        }

        private void Update()
        {
            if (run != null) Render();

            // Quick restart key (self-contained, like the camera toggle).
            if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
                Restart();
        }

        private void Render()
        {
            if (timerText != null) timerText.text = Format(run.Elapsed);

            if (stateText != null)
                stateText.text = run.Finished ? "FINISHED" : run.Running ? "" : "Cross the start line to begin";

            if (bestText != null)
                bestText.text = "Best  " + (run.HasBest ? Format(run.BestElapsed) : "--:--.--");
        }

        private void Restart()
        {
            NetworkIdentity local = NetworkClient.localPlayer;
            if (local == null) return;
            NetworkPlayer player = local.GetComponent<NetworkPlayer>();
            if (player != null) player.RequestRestart();
        }

        private static string Format(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int minutes = (int)(seconds / 60);
            double secs = seconds - minutes * 60;
            return string.Format("{0:00}:{1:00.00}", minutes, secs);
        }
    }
}
