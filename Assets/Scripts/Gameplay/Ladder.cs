using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// Marker for a climbable region: while a player's capsule overlaps this trigger, their
    /// movement sim switches to climb mode (W up, S down, gravity off — see
    /// <see cref="Game.Player.PlayerController"/>). Pure marker, no logic — the server-side
    /// movement tick overlap-checks for it, which keeps climbing working on moving parents
    /// (ship masts) without any event bookkeeping.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class Ladder : MonoBehaviour
    {
        private void Reset()
        {
            var col = GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
        }
    }
}
