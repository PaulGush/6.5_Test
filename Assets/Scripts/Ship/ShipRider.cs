using System.Collections;
using Mirror;
using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// Keeps a player attached to the ship they are standing on. The server decides boarding
    /// (via <see cref="ShipDeck"/> trigger volumes) and syncs the ship's netId; every peer then
    /// parents the player under the ship. Because the player's NetworkTransform already syncs in
    /// LOCAL space, "standing still on a wildly manoeuvring deck" costs zero network traffic and
    /// never slides — the ship's own sync carries everyone on it.
    ///
    /// The CharacterController keeps simulating in world space on the server, so the deck moving
    /// underneath is felt (jumps land where the ship now is), while the parenting guarantees the
    /// replicated pose stays glued to the deck.
    /// </summary>
    public class ShipRider : NetworkBehaviour
    {
        [SyncVar(hook = nameof(OnShipChanged))] private uint _shipNetId;

        private ShipController _ship;
        private NetworkTransformBase _netTransform;
        private PlayerHelmUser _helmUser;
        private Coroutine _resolve;

        /// <summary>The ship this player is aboard, or null. Valid on server and clients.</summary>
        public ShipController CurrentShip => _ship;

        private void Awake()
        {
            _netTransform = GetComponent<NetworkTransformBase>();
            _helmUser = GetComponent<PlayerHelmUser>();
        }

        /// <summary>Server: put this player aboard <paramref name="ship"/> (null to go ashore).</summary>
        [Server]
        public void ServerBoard(ShipController ship)
        {
            uint id = ship != null ? ship.netId : 0u;
            if (_shipNetId == id) return;

            // Leaving the ship always releases the helm, whatever caused the departure.
            if (ship == null && _helmUser != null) _helmUser.ServerLeaveHelm();

            _shipNetId = id;
            Apply(ship);
        }

        // Client-side hook. The ship may not be spawned locally yet (late join / spawn ordering),
        // in which case we wait for it.
        private void OnShipChanged(uint _, uint newId)
        {
            if (_resolve != null) { StopCoroutine(_resolve); _resolve = null; }

            if (newId == 0u) { Apply(null); return; }
            if (NetworkClient.spawned.TryGetValue(newId, out NetworkIdentity ni))
                Apply(ni.GetComponent<ShipController>());
            else
                _resolve = StartCoroutine(ResolveWhenSpawned(newId));
        }

        private IEnumerator ResolveWhenSpawned(uint id)
        {
            while (_shipNetId == id)
            {
                if (NetworkClient.spawned.TryGetValue(id, out NetworkIdentity ni))
                {
                    Apply(ni.GetComponent<ShipController>());
                    _resolve = null;
                    yield break;
                }
                yield return null;
            }
            _resolve = null;
        }

        // Idempotent so it can safely run from both the server path and the SyncVar hook (host mode).
        private void Apply(ShipController ship)
        {
            if (_ship == ship) return;
            _ship = ship;

            transform.SetParent(ship != null ? ship.transform : null, worldPositionStays: true);

            // Buffered snapshots were recorded in the old parent's space; interpolating through
            // them after a reparent would drag the player across the deck. Start fresh.
            if (_netTransform != null) _netTransform.ResetState();
        }
    }
}
