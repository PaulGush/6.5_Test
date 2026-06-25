using Mirror;
using UnityEngine;
using Game.Player;

namespace Game.Gameplay
{
    /// <summary>
    /// A trigger volume that launches any player who enters it. The launch is server-authoritative:
    /// only the server reacts (the result replicates via the player's NetworkTransform). Re-fires each
    /// time the player re-enters, so landing back on it bounces again.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class JumpPad : MonoBehaviour
    {
        [Tooltip("Launch velocity (m/s). If 'Local Direction' is on, it's relative to this pad's rotation.")]
        [SerializeField] private Vector3 launchVelocity = new Vector3(0f, 12f, 0f);
        [Tooltip("Interpret the launch velocity in the pad's local space (so a tilted pad fires along its facing).")]
        [SerializeField] private bool localDirection = true;

        private void Reset()
        {
            var col = GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!NetworkServer.active) return;
            NetworkPlayer player = other.GetComponent<NetworkPlayer>();
            if (player == null) return;

            Vector3 v = localDirection ? transform.TransformDirection(launchVelocity) : launchVelocity;
            player.Launch(v);
        }
    }
}
