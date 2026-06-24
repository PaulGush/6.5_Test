using Mirror;
using UnityEngine;
using Game.Player;

namespace Game.Gameplay
{
    /// <summary>
    /// A level trigger volume that drives the run. Server-only:
    ///  - Start: begins the run clock when a player enters.
    ///  - Checkpoint: sets the entering player's respawn pose to this point.
    ///  - Finish: ends the run.
    /// Needs an isTrigger collider; a player's CharacterController entering fires OnTriggerEnter.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class RunTrigger : MonoBehaviour
    {
        public enum Kind { Start, Checkpoint, Finish }

        [SerializeField] private Kind kind;
        [SerializeField] private RunManager run;
        [Tooltip("Respawn pose for a Checkpoint (defaults to this trigger's transform).")]
        [SerializeField] private Transform respawnPoint;

        private void OnTriggerEnter(Collider other)
        {
            if (!NetworkServer.active) return;

            var player = other.GetComponentInParent<NetworkPlayer>();
            if (player == null) return;

            switch (kind)
            {
                case Kind.Start:
                    if (run != null) run.StartRun();
                    break;
                case Kind.Checkpoint:
                    Transform p = respawnPoint != null ? respawnPoint : transform;
                    player.SetCheckpoint(p.position, p.rotation);
                    break;
                case Kind.Finish:
                    if (run != null) run.FinishRun();
                    break;
            }
        }
    }
}
