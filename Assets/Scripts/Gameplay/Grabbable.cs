using Mirror;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// A physics prop that a player can pick up, carry, drop, and throw. When free it is a normal
    /// server-authoritative dynamic body synced by NetworkRigidbody. While held it becomes kinematic
    /// and EVERY client drives its pose locally from the carrier (see PlayerGrabber.LateUpdate):
    /// the carry is fully determined by the carrier's already-synced transform, so the prop's own
    /// per-prop network sync is suspended while held. That avoids the NetworkRigidbody overwriting the
    /// locally-driven pose each frame, which otherwise made the held prop judder near the camera.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Grabbable : NetworkBehaviour
    {
        public Rigidbody Body { get; private set; }
        public Collider Col { get; private set; }

        // The prop's own transform sync; disabled while held (the carrier drives the pose locally).
        private NetworkTransformBase _netTransform;

        [Header("Carry pose")]
        [Tooltip("Local-space point on the prop (in metres from its pivot) that snaps to the carrier's hold anchor. " +
                 "Zero = held by the centre; e.g. one end of a plank so it extends out in front.")]
        [SerializeField] private Vector3 holdLocalGrip = Vector3.zero;
        [Tooltip("Local rotation (euler) applied while carried, relative to the hold anchor. " +
                 "e.g. (0,-90,0) lays a long-X plank out along the carrier's forward.")]
        [SerializeField] private Vector3 holdLocalEuler = Vector3.zero;

        [Tooltip("Extra carry offset in the carrier's anchor space (x = right, y = up, z = forward), " +
                 "applied on top of the player's hold anchor. Push a bulky prop lower and further out " +
                 "(e.g. a box: 0,-0.2,0.8) so it stops blocking the view. Long props gripped at one end " +
                 "(e.g. the plank) usually leave this at zero.")]
        [SerializeField] private Vector3 holdAnchorOffset = Vector3.zero;

        public Vector3 HoldLocalGrip => holdLocalGrip;
        public Quaternion HoldLocalRotation => Quaternion.Euler(holdLocalEuler);
        public Vector3 HoldAnchorOffset => holdAnchorOffset;

        [SyncVar(hook = nameof(OnHeldChanged))] private bool _held;
        public bool IsHeld => _held;

        // Server-only: the prop's pose at spawn, restored by a run restart.
        private Vector3 _startPos;
        private Quaternion _startRot;

        private void Awake()
        {
            Body = GetComponent<Rigidbody>();
            Col = GetComponent<Collider>();
            _netTransform = GetComponent<NetworkTransformBase>();
        }

        // Server: remember where this prop started so a restart can put it back.
        public override void OnStartServer()
        {
            _startPos = transform.position;
            _startRot = transform.rotation;
        }

        // Late joiners (and the host) apply whatever the current held state already is.
        public override void OnStartClient() => ApplyHeldState(_held);

        /// <summary>Server: return the prop to its spawn pose, stationary. Assumes it has already been
        /// released (the carrier dropped it) so it's a free dynamic body again. Teleports (no slide).</summary>
        [Server]
        public void ResetToStart()
        {
            Body.linearVelocity = Vector3.zero;
            Body.angularVelocity = Vector3.zero;
            if (_netTransform != null) _netTransform.ServerTeleport(_startPos, _startRot);
            else transform.SetPositionAndRotation(_startPos, _startRot);
        }

        /// <summary>Server entry point: mark held/released and apply the local physics state now;
        /// the SyncVar fans the change out so every client applies the same state via the hook.</summary>
        [Server]
        public void SetHeld(bool held)
        {
            _held = held;
            ApplyHeldState(held);
        }

        private void OnHeldChanged(bool _, bool held) => ApplyHeldState(held);

        /// <summary>Switch the prop between free dynamic body and carried kinematic body, and suspend
        /// its own transform sync while held (the carrier drives the pose locally on every client).</summary>
        private void ApplyHeldState(bool held)
        {
            // While held the carrier positions the prop every frame on each client, so the prop's own
            // NetworkRigidbody sync would only fight that (and re-introduce the judder). Re-enabled on release.
            if (_netTransform != null) _netTransform.enabled = !held;

            if (held)
            {
                // Flip to kinematic (Discrete is the only mode valid during the switch), then use a
                // kinematic-compatible continuous mode. Interpolation off: the carrier sets the transform
                // every render frame, so letting Unity interpolate would only fight that and re-add lag.
                Body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                Body.isKinematic = true;
                Body.useGravity = false;
                Body.interpolation = RigidbodyInterpolation.None;
                Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }
            else
            {
                Body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                Body.isKinematic = false;
                Body.useGravity = true;
                Body.interpolation = RigidbodyInterpolation.Interpolate;
                Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
        }
    }
}
