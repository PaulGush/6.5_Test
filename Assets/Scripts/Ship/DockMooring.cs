using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// A jetty's mooring point — the anchor station. The berth is a trigger volume on this
    /// object (a BoxCollider the builder sizes over the water alongside the planks), so a
    /// ship is "at the dock" when its hull actually overlaps the berth — not when its centre
    /// happens to be near the jetty root, which a long hull parked flush can easily fail.
    ///
    /// Ships park themselves: glide in with every sail furled and the mooring line catches
    /// automatically once the ship is in the berth and slow (<see cref="ShipController.SetAnchored"/>
    /// holds it alongside); setting any sail casts off again. The bollard is the manual
    /// override for both directions — after a manual cast-off the jetty ignores that ship
    /// until it leaves the berth or makes sail, so it isn't instantly re-caught. Anchoring is
    /// deliberately tied to jetties — there is no mid-ocean anchor — so docks are where
    /// voyages start and end.
    ///
    /// Lives on the jetty root (a scene NetworkIdentity) next to the berth collider. The
    /// bollard's collider carries a <see cref="DockMooringTarget"/> pointing back here, the
    /// same marker pattern the helm and sail stations use; interaction arrives via
    /// <see cref="PlayerHelmUser"/> commands. Berth occupancy is tracked from trigger events
    /// on every peer (client ships are kinematic but still fire them), so prompts and the
    /// HUD read correctly without extra sync.
    /// </summary>
    public class DockMooring : NetworkBehaviour
    {
        [Tooltip("A ship moving faster than this (m/s) won't take a mooring line.")]
        [SerializeField] private float maxMooringSpeed = 3.5f;

        [Header("Self-parking")]
        [Tooltip("Auto-moor only below this speed (m/s); the ship must be drifting in.")]
        [SerializeField] private float autoMoorSpeed = 2f;

        // The ship held by this jetty, synced so prompts read correctly on every client.
        [SyncVar] private ShipController _moored;

        // Ships overlapping the berth volume, ref-counted because a hull is several colliders.
        private readonly Dictionary<ShipController, int> _berthed = new();

        // Server-only: ship manually cast off that must clear the berth (or set sail)
        // before the jetty will auto-catch it again.
        private ShipController _released;
        private float _nextScan;

        public bool HasMooredShip => _moored != null;

        /// <summary>The ship this jetty currently holds (null when the line is free).</summary>
        public ShipController MooredShip => _moored;

        /// <summary>Is this ship's hull inside the berth volume? Valid on server and clients.</summary>
        public bool InBerth(ShipController ship) => ship != null && _berthed.ContainsKey(ship);

        /// <summary>Any-client prompt line for a player looking at the bollard.</summary>
        public string PromptText()
        {
            if (_moored != null) return "Cast off";
            ShipController ship = BerthedShip();
            return ship != null ? "Moor ship" : "No ship in berth";
        }

        private void Start()
        {
            // The berth only works if the builder gave this object its trigger volume.
            foreach (Collider c in GetComponents<Collider>())
                if (c.isTrigger) return;
            Debug.LogWarning($"[DockMooring] '{name}' has no berth trigger collider — ships can't be detected.", this);
        }

        private void OnTriggerEnter(Collider other)
        {
            ShipController ship = other.GetComponentInParent<ShipController>();
            if (ship == null) return;
            _berthed.TryGetValue(ship, out int n);
            _berthed[ship] = n + 1;
        }

        private void OnTriggerExit(Collider other)
        {
            ShipController ship = other.GetComponentInParent<ShipController>();
            if (ship == null || !_berthed.TryGetValue(ship, out int n)) return;
            if (n <= 1) _berthed.Remove(ship);
            else _berthed[ship] = n - 1;
        }

        // Trigger enters replay when we're re-enabled (clients get scene objects enabled on
        // spawn), so stale counts must not survive a disable.
        private void OnDisable() => _berthed.Clear();

        /// <summary>Server: moor the nearest eligible ship, or release the one we hold.</summary>
        [Server]
        public void ServerToggle()
        {
            if (_moored != null)
            {
                ServerCastOff();
                return;
            }

            ShipController ship = BerthedShip();
            if (ship == null) return;
            if (Mathf.Abs(ship.ForwardSpeed) > maxMooringSpeed) return; // still making way
            Moor(ship);
        }

        // Self-parking: catch a furled, slow ship drifting into the berth; let a moored
        // ship go the moment its crew sets canvas. Scanned a few times a second.
        [ServerCallback]
        private void Update()
        {
            if (Time.time < _nextScan) return;
            _nextScan = Time.time + 0.25f;
            PruneBerth();

            if (_moored != null)
            {
                if (_moored.SailLevel > 0) // making sail is the cast-off ritual
                {
                    _moored.SetAnchored(false);
                    _moored = null;
                }
                return;
            }

            // A manually cast-off ship re-arms once it leaves the berth or sets sail.
            if (_released != null)
            {
                if (_released.SailLevel > 0 || !InBerth(_released)) _released = null;
            }

            ShipController ship = BerthedShip();
            if (ship == null || ship == _released) return;
            if (ship.SailLevel > 0) return; // under sail: just passing by
            if (ship.LinearSpeed > autoMoorSpeed) return;
            Moor(ship);
        }

        /// <summary>Server: manual cast-off — from the bollard or the ship's own anchor
        /// station. The ship is ignored until it clears the berth or makes sail, so the
        /// line doesn't instantly re-catch it.</summary>
        [Server]
        public void ServerCastOff()
        {
            if (_moored == null) return;
            _released = _moored;
            _moored.SetAnchored(false);
            _moored = null;
        }

        [Server]
        private void Moor(ShipController ship)
        {
            ship.SetAnchored(true);
            _moored = ship;
        }

        // Nearest berthed ship no jetty holds yet (Anchored implies held somewhere, since
        // jetties are the only thing that sets the anchor).
        private ShipController BerthedShip()
        {
            ShipController best = null;
            float bestDist = float.MaxValue;
            foreach (ShipController ship in _berthed.Keys)
            {
                if (ship == null || ship.Anchored) continue;
                float d = Vector3.Distance(transform.position, ship.transform.position);
                if (d < bestDist) { best = ship; bestDist = d; }
            }
            return best;
        }

        // Destroyed ships never fire OnTriggerExit; drop their stale entries.
        private void PruneBerth()
        {
            List<ShipController> dead = null;
            foreach (ShipController ship in _berthed.Keys)
                if (ship == null) (dead ??= new List<ShipController>()).Add(ship);
            if (dead != null)
                foreach (ShipController ship in dead) _berthed.Remove(ship);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.35f);
            foreach (Collider c in GetComponents<Collider>())
                if (c is BoxCollider box && box.isTrigger)
                {
                    Gizmos.matrix = transform.localToWorldMatrix;
                    Gizmos.DrawWireCube(box.center, box.size);
                }
        }
    }
}
