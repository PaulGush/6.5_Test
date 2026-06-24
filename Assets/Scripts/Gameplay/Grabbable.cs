using Mirror;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// A physics prop that a player can pick up, carry, drop, and throw. Server-authoritative:
    /// the server owns the Rigidbody simulation (synced to clients by NetworkRigidbody). While
    /// held, the server makes it kinematic and drives its pose from the carrier's hold anchor.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Grabbable : NetworkBehaviour
    {
        public Rigidbody Body { get; private set; }
        public Collider Col { get; private set; }

        [Header("Carry pose")]
        [Tooltip("Local-space point on the prop (in metres from its pivot) that snaps to the carrier's hold anchor. " +
                 "Zero = held by the centre; e.g. one end of a plank so it extends out in front.")]
        [SerializeField] private Vector3 holdLocalGrip = Vector3.zero;
        [Tooltip("Local rotation (euler) applied while carried, relative to the hold anchor. " +
                 "e.g. (0,-90,0) lays a long-X plank out along the carrier's forward.")]
        [SerializeField] private Vector3 holdLocalEuler = Vector3.zero;

        public Vector3 HoldLocalGrip => holdLocalGrip;
        public Quaternion HoldLocalRotation => Quaternion.Euler(holdLocalEuler);

        [SyncVar] private bool _held;
        public bool IsHeld => _held;

        private void Awake()
        {
            Body = GetComponent<Rigidbody>();
            Col = GetComponent<Collider>();
        }

        /// <summary>Server: mark held/released. The prop stays a dynamic body either way so it keeps
        /// colliding with the world; while held gravity is off (the carrier drives it by velocity)
        /// and continuous detection guards against tunnelling.</summary>
        [Server]
        public void SetHeld(bool held)
        {
            _held = held;
            Body.useGravity = !held;
            Body.collisionDetectionMode = held
                ? CollisionDetectionMode.ContinuousDynamic
                : CollisionDetectionMode.Discrete;
        }
    }
}
