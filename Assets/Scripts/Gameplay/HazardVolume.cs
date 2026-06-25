using Mirror;
using UnityEngine;
using Game.Player;

namespace Game.Gameplay
{
    /// <summary>
    /// A trigger volume that sends any player who touches it back to their last checkpoint (spikes,
    /// lava, a saw, a bottomless pit you don't want to rely on killY for). Server-authoritative:
    /// only the server respawns the player, which replicates via the existing teleport path.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class HazardVolume : MonoBehaviour
    {
        private void Reset()
        {
            var col = GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!NetworkServer.active) return;
            NetworkPlayer player = other.GetComponent<NetworkPlayer>();
            if (player != null) player.RespawnAtCheckpoint();
        }
    }
}
