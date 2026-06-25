using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// CharacterController movement simulation: walk/sprint, gravity, jump, and
    /// mouse/stick look (yaw on the body, pitch on a camera pivot).
    ///
    /// This component is a pure, deterministic step — <see cref="Tick"/> over
    /// (current state, input, dt). It does NOT read input or run on its own; a
    /// driver decides when to call it. In the networked game (Phase 3) the
    /// authoritative driver is <see cref="NetworkPlayer"/>, which calls Tick on
    /// the server from input received over the wire.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        [Header("Move")]
        [SerializeField] private float walkSpeed = 5f;
        [SerializeField] private float sprintSpeed = 8f;
        [SerializeField] private float jumpHeight = 1.4f;
        [SerializeField] private float gravity = -20f;

        [Header("Launch")]
        [Tooltip("How fast launch-imparted horizontal momentum bleeds off while airborne (m/s²).")]
        [SerializeField] private float airLaunchDamping = 6f;
        [Tooltip("How fast launch momentum bleeds off once grounded again (m/s²).")]
        [SerializeField] private float groundLaunchDamping = 40f;

        [Header("Look")]
        [Tooltip("Degrees of rotation per unit of look delta.")]
        [SerializeField] private float lookSensitivity = 0.12f;
        [SerializeField] private float minPitch = -80f;
        [SerializeField] private float maxPitch = 80f;
        [Tooltip("Child transform that receives pitch (the camera mounts here). Yaw is applied to the body.")]
        [SerializeField] private Transform cameraPivot;

        private CharacterController _cc;
        private float _pitch;
        private float _verticalVelocity;
        private Vector3 _externalVel; // launch-imparted horizontal momentum, bleeds off over time

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
        }

        /// <summary>Deterministic movement step. The server-authoritative entry point.</summary>
        public void Tick(in PlayerInputState input, float dt)
        {
            ApplyLook(input);
            ApplyMove(input, dt);
        }

        /// <summary>Impart a launch velocity (e.g. a jump pad). Vertical feeds the jump velocity;
        /// horizontal becomes air momentum that bleeds off after landing. Called on the server.</summary>
        public void Launch(Vector3 velocity)
        {
            _verticalVelocity = velocity.y;
            _externalVel = new Vector3(velocity.x, 0f, velocity.z);
        }

        /// <summary>
        /// Hard-teleport to a pose and clear accumulated motion (vertical velocity, pitch).
        /// The CharacterController must be disabled while moving the transform, otherwise it
        /// resists the reposition. Called by the server on respawn; the NetworkTransform on the
        /// same object replicates the snap to clients.
        /// </summary>
        public void Respawn(Vector3 position, Quaternion rotation)
        {
            bool wasEnabled = _cc.enabled;
            _cc.enabled = false;
            transform.SetPositionAndRotation(position, rotation);
            _cc.enabled = wasEnabled;

            _verticalVelocity = 0f;
            _externalVel = Vector3.zero;
            _pitch = 0f;
            if (cameraPivot != null)
                cameraPivot.localRotation = Quaternion.identity;
        }

        private void ApplyLook(in PlayerInputState input)
        {
            // Look deltas are already frame-accumulated, so they are not scaled by dt.
            transform.Rotate(0f, input.Look.x * lookSensitivity, 0f);

            _pitch = Mathf.Clamp(_pitch - input.Look.y * lookSensitivity, minPitch, maxPitch);
            if (cameraPivot != null)
                cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        private void ApplyMove(in PlayerInputState input, float dt)
        {
            float speed = input.Sprint ? sprintSpeed : walkSpeed;
            Vector3 wish = transform.right * input.Move.x + transform.forward * input.Move.y;
            wish = Vector3.ClampMagnitude(wish, 1f) * speed;

            // Grounded only resets a downward velocity; a positive (jumping/just-launched) vertical is
            // left alone so a jump pad fired while still touching the pad isn't immediately cancelled.
            if (_cc.isGrounded && _verticalVelocity <= 0f)
            {
                _verticalVelocity = -2f; // small downward bias keeps isGrounded stable on slopes/steps
                if (input.JumpPressed)
                    _verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
                _externalVel = Vector3.MoveTowards(_externalVel, Vector3.zero, groundLaunchDamping * dt);
            }
            else
            {
                _verticalVelocity += gravity * dt;
                _externalVel = Vector3.MoveTowards(_externalVel, Vector3.zero, airLaunchDamping * dt);
            }

            Vector3 velocity = wish + _externalVel + Vector3.up * _verticalVelocity;
            _cc.Move(velocity * dt);
        }
    }
}
