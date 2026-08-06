using Mirror;
using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// The steering station. Lives on the ship's root object (Mirror requires NetworkBehaviours
    /// beside the NetworkIdentity); the physical wheel is a child prop marked with a
    /// <see cref="ShipHelmTarget"/> so the interaction ray can single it out from the rest of
    /// the ship's colliders.
    ///
    /// One user at a time, tracked server-side and synced so other players see the wheel as taken.
    /// Steering input arrives via <see cref="PlayerHelmUser"/> commands, not directly here.
    /// The wheel visual spins with the synced rudder value on every client.
    /// </summary>
    public class ShipHelm : NetworkBehaviour
    {
        [Tooltip("The ShipController this wheel steers (same object).")]
        [SerializeField] private ShipController ship;
        [Tooltip("The wheel prop transform, spun to mirror the rudder.")]
        [SerializeField] private Transform wheelVisual;
        [Tooltip("Local axis the wheel spins around.")]
        [SerializeField] private Vector3 wheelSpinAxis = Vector3.forward;
        [Tooltip("Degrees of wheel rotation at full rudder.")]
        [SerializeField] private float wheelSpinDegrees = 240f;
        [Tooltip("How quickly the wheel visual chases the rudder (per second). The synced rudder " +
                 "arrives in coarse steps (keyboard is -1/0/1, sync batches ~10Hz); this smooths them out.")]
        [SerializeField] private float wheelSpinResponse = 8f;

        [SyncVar] private NetworkIdentity _user;

        public ShipController Ship => ship;
        public bool Occupied => _user != null;
        /// <summary>The physical wheel prop (read by the helmsman's visual stance).</summary>
        public Transform WheelVisual => wheelVisual;
        /// <summary>The wheel's spin axis in the wheel's local space (the rim plane's normal).</summary>
        public Vector3 WheelAxis => wheelSpinAxis;

        private Quaternion _wheelBaseRot;
        private float _shownRudder; // locally smoothed rudder driving the wheel visual

        /// <summary>Editor-tooling hook for wiring at build time.</summary>
        public void SetRefs(ShipController shipRef, Transform wheel)
        {
            ship = shipRef;
            wheelVisual = wheel;
        }

        private void Awake()
        {
            if (wheelVisual != null) _wheelBaseRot = wheelVisual.localRotation;
        }

        /// <summary>Server: claim the helm. Fails if someone else is already steering.</summary>
        [Server]
        public bool TryEngage(NetworkIdentity user)
        {
            if (_user != null) return false;
            _user = user;
            return true;
        }

        /// <summary>Server: release the helm (only the current user can).</summary>
        [Server]
        public void Release(NetworkIdentity user)
        {
            if (_user == user) _user = null;
        }

        private void Update()
        {
            if (wheelVisual == null || ship == null) return;

            // Frame-rate-independent exponential chase toward the (stepped) synced rudder.
            _shownRudder = Mathf.Lerp(_shownRudder, ship.Rudder,
                1f - Mathf.Exp(-wheelSpinResponse * Time.deltaTime));
            wheelVisual.localRotation =
                _wheelBaseRot * Quaternion.AngleAxis(_shownRudder * wheelSpinDegrees, wheelSpinAxis);
        }
    }
}
