using Mirror;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Ship
{
    /// <summary>
    /// HUD status line for the ship the local player is aboard, so docking is never silent:
    /// shows "docking" while a furled ship drifts inside a jetty's catch circle, flips to
    /// "moored" when the line takes hold, and flashes a cast-off notice when it lets go.
    ///
    /// Purely client-local, polled from already-synced state (<see cref="ShipController"/>
    /// SyncVars and jetty positions), so it is correct on host and remote clients alike and
    /// costs no network traffic. Lives on the HUD prefab; wired by the builder.
    /// </summary>
    public class ShipStatusView : MonoBehaviour
    {
        [Tooltip("Text element the status line is written to (row root is toggled with it).")]
        [SerializeField] private Text text;
        [Tooltip("How long the cast-off notice stays up (seconds).")]
        [SerializeField] private float castOffSeconds = 3f;

        private DockMooring[] _docks = { };
        private float _nextDockScan;
        private ShipController _lastShip;
        private bool _lastAnchored;
        private float _castOffUntil;

        /// <summary>Editor-tooling hook for wiring at build time.</summary>
        public void SetText(Text value) => text = value;

        private void Update()
        {
            string line = "";
            ShipController ship = LocalShip();
            if (ship != null)
            {
                // Falling edge of the anchor while we're aboard = the line just let go.
                if (ship == _lastShip && _lastAnchored && !ship.Anchored)
                    _castOffUntil = Time.time + castOffSeconds;
                _lastShip = ship;
                _lastAnchored = ship.Anchored;

                if (ship.Anchored)
                    line = MooredAtDock(ship) ? "Moored — set sails to cast off"
                        : ship.WeighingAnchor ? "Weighing anchor…"
                        : "Anchored — weigh anchor at the bow";
                else if (Time.time < _castOffUntil)
                    line = "Cast off!";
                else if (ship.SailLevel == 0 && NearDock(ship))
                    line = "Docking — mooring line reaching…";
            }
            else
            {
                _lastShip = null;
            }

            if (text != null)
            {
                text.text = line;
                if (text.gameObject.activeSelf != (line.Length > 0))
                    text.gameObject.SetActive(line.Length > 0);
            }
        }

        private ShipController LocalShip()
        {
            NetworkIdentity player = NetworkClient.localPlayer;
            if (player == null) return null;
            ShipRider rider = player.GetComponent<ShipRider>();
            return rider != null ? rider.CurrentShip : null;
        }

        // Inside any jetty's berth volume?
        private bool NearDock(ShipController ship)
        {
            foreach (DockMooring dock in Docks())
                if (dock != null && dock.InBerth(ship))
                    return true;
            return false;
        }

        // Held by a jetty's line (as opposed to riding its own anchor mid-ocean)?
        private bool MooredAtDock(ShipController ship)
        {
            foreach (DockMooring dock in Docks())
                if (dock != null && dock.MooredShip == ship)
                    return true;
            return false;
        }

        // Docks are static scene objects, but they spawn in disabled on clients, so the
        // cache refreshes until it finds them.
        private DockMooring[] Docks()
        {
            if (_docks.Length == 0 && Time.time >= _nextDockScan)
            {
                _nextDockScan = Time.time + 2f;
                _docks = FindObjectsByType<DockMooring>(FindObjectsSortMode.None);
            }
            return _docks;
        }
    }
}
