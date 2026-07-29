using Mirror;
using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// Trigger volume over a ship's walkable deck. Any player entering it boards the ship
    /// (parented + local-space sync via <see cref="ShipRider"/>); leaving it — jumping off,
    /// falling overboard, being respawned away — goes ashore. Server-only decisions.
    ///
    /// Make the volume generously tall so a jump on deck never counts as leaving.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class ShipDeck : MonoBehaviour
    {
        [Tooltip("The ship this deck belongs to (the root ShipController).")]
        [SerializeField] private ShipController ship;

        /// <summary>Editor-tooling hook for wiring the ship reference at build time.</summary>
        public void SetShip(ShipController value) => ship = value;

        private void Reset()
        {
            var col = GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!NetworkServer.active || ship == null) return;
            var rider = other.GetComponentInParent<ShipRider>();
            if (rider != null) rider.ServerBoard(ship);
        }

        private void OnTriggerExit(Collider other)
        {
            if (!NetworkServer.active || ship == null) return;
            var rider = other.GetComponentInParent<ShipRider>();
            if (rider != null && rider.CurrentShip == ship) rider.ServerBoard(null);
        }
    }
}
