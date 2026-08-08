using Mirror;
using UnityEngine;
using Game.Player;

namespace Game.Gameplay
{
    /// <summary>
    /// A lethal trigger volume (spikes, lava, a saw, a bottomless pit you don't want to
    /// rely on killY for). Server-authoritative: kills through the health system, so the
    /// player collapses where they fell, gets the death screen, and respawns at their
    /// checkpoint on request.
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
            if (player != null)
                player.ServerDamage(float.MaxValue, NetworkPlayer.CauseOfDeath.Hazard);
        }
    }
}
