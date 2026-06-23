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

            if (_cc.isGrounded)
            {
                _verticalVelocity = -2f; // small downward bias keeps isGrounded stable on slopes/steps
                if (input.JumpPressed)
                    _verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
            else
            {
                _verticalVelocity += gravity * dt;
            }

            Vector3 velocity = wish + Vector3.up * _verticalVelocity;
            _cc.Move(velocity * dt);
        }
    }
}
