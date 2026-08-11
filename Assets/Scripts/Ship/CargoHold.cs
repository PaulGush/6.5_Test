using Mirror;
using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// Counts the cargo riding this ship's deck — anywhere aboard, not a marked zone.
    /// The stow-zone era (trigger volume, glowing frame, coaming lip) is retired:
    /// nothing is lashed, cargo slides loose wherever the crew piles it, and keeping it
    /// aboard through weather IS the job. "Aboard" is simply the deck-carry's own
    /// server-side verdict (<see cref="Game.Gameplay.CargoItem.DeckShip"/> == this
    /// ship), synced for the HUD. Lives on the ship's root (Mirror only syncs
    /// behaviours on the identity object). (CargoItem's lash machinery survives for a
    /// future manual lash-down job.)
    /// </summary>
    public class CargoHold : NetworkBehaviour
    {
        [SyncVar] private int aboardCount;
        [SyncVar] private int aboardValue;

        public int AboardCount => aboardCount;
        public int AboardValue => aboardValue;

        private ShipController _ship;
        private float _nextTick;

        private void Awake() => _ship = GetComponent<ShipController>();

        private void Update()
        {
            if (!NetworkServer.active || _ship == null || Time.time < _nextTick) return;
            _nextTick = Time.time + 0.4f;

            int count = 0, total = 0;
            foreach (var item in FindObjectsByType<Game.Gameplay.CargoItem>(FindObjectsSortMode.None))
            {
                // Migration from the auto-lash era: everything rides loose now.
                if (item.IsStowed) item.ServerSetStowed(null);

                if (item.Grabbable.IsHeld) continue;
                if (item.DeckShip != _ship) continue;
                count++; total += item.Value;
            }
            aboardCount = count;
            aboardValue = total;
        }
    }
}
