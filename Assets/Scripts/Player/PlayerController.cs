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

        [Header("Crouch")]
        [Tooltip("CharacterController height while crouched.")]
        [SerializeField] private float crouchHeight = 1f;
        [Tooltip("Move speed while crouched (m/s).")]
        [SerializeField] private float crouchSpeed = 2.5f;
        [Tooltip("How quickly the crouch transition plays (units of the 0..1 blend per second).")]
        [SerializeField] private float crouchLerpSpeed = 8f;
        [Tooltip("How far the camera lowers when fully crouched (m).")]
        [SerializeField] private float crouchCameraDrop = 0.6f;

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

        // Crouch: captured standing pose + the current 0..1 crouch blend.
        private float _standHeight, _bottomOffset, _standCamY, _crouchT;
        private Vector3 _standCenter;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _standHeight = _cc.height;
            _standCenter = _cc.center;
            _bottomOffset = _standCenter.y - _standHeight * 0.5f; // feet offset from the transform origin
            if (cameraPivot != null) _standCamY = cameraPivot.localPosition.y;
        }

        /// <summary>Deterministic movement step. The server-authoritative entry point.</summary>
        public void Tick(in PlayerInputState input, float dt)
        {
            ApplyLook(input);
            ApplyCrouch(input, dt);
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
            // Reset crouch back to standing.
            _crouchT = 0f;
            _cc.height = _standHeight;
            _cc.center = _standCenter;
            if (cameraPivot != null)
            {
                cameraPivot.localRotation = Quaternion.identity;
                Vector3 lp = cameraPivot.localPosition; lp.y = _standCamY; cameraPivot.localPosition = lp;
            }
        }

        // Blend toward crouched/standing, shrinking the controller (feet stay planted) and lowering the
        // camera. When the crouch button is released, only stand back up if there is headroom above.
        private void ApplyCrouch(in PlayerInputState input, float dt)
        {
            float target = input.Crouch ? 1f : (CanStand() ? 0f : 1f);
            _crouchT = Mathf.MoveTowards(_crouchT, target, crouchLerpSpeed * dt);

            float h = Mathf.Lerp(_standHeight, crouchHeight, _crouchT);
            _cc.height = h;
            Vector3 c = _standCenter;
            c.y = _bottomOffset + h * 0.5f;
            _cc.center = c;

            if (cameraPivot != null)
            {
                Vector3 lp = cameraPivot.localPosition;
                lp.y = _standCamY - crouchCameraDrop * _crouchT;
                cameraPivot.localPosition = lp;
            }
        }

        // Is there room above to return to full height? Sweeps the standing capsule upward from the
        // feet; colliders overlapping the start sphere (the floor, our own crouched body) are ignored by
        // SphereCast, so only a genuine ceiling above blocks standing.
        private bool CanStand()
        {
            if (_standHeight - _cc.height <= 0.02f) return true;
            float r = Mathf.Max(0.05f, _cc.radius - 0.05f);
            Vector3 feet = transform.position + new Vector3(0f, _cc.center.y - _cc.height * 0.5f, 0f);
            Vector3 from = feet + Vector3.up * r;
            float castDist = _standHeight - 2f * r;
            return !Physics.SphereCast(from, r, Vector3.up, out RaycastHit _, castDist, ~0, QueryTriggerInteraction.Ignore);
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
            float speed = input.Crouch ? crouchSpeed : input.Sprint ? sprintSpeed : walkSpeed;
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
