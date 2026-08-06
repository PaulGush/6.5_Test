using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// CharacterController movement simulation: walk/sprint, gravity, jump, and
    /// mouse/stick look. In first person the body owns yaw and the camera pivot
    /// takes pitch. In free look (third person) the pivot takes yaw too — the
    /// camera orbits without turning the body — movement is camera-relative, and
    /// the body turns to face wherever it is walking.
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

        [Header("Climb")]
        [Tooltip("Vertical speed on a ladder (m/s); forward input climbs, back descends.")]
        [SerializeField] private float climbSpeed = 3f;
        [Tooltip("Horizontal input is damped to this fraction while climbing.")]
        [SerializeField] private float climbMoveFactor = 0.4f;

        [Header("Look")]
        [Tooltip("Degrees of rotation per unit of look delta.")]
        [SerializeField] private float lookSensitivity = 0.12f;
        [SerializeField] private float minPitch = -80f;
        [SerializeField] private float maxPitch = 80f;
        [Tooltip("Child transform that receives pitch (the camera mounts here). Yaw is applied to the body, or to this pivot in free look.")]
        [SerializeField] private Transform cameraPivot;
        [Tooltip("How fast the body turns to face the move direction in free look (deg/s).")]
        [SerializeField] private float faceTurnSpeed = 540f;

        private CharacterController _cc;
        private float _pitch;
        private float _camYaw; // free-look camera yaw, relative to the body
        private float _verticalVelocity;
        private Vector3 _externalVel; // launch-imparted horizontal momentum, bleeds off over time
        private readonly Collider[] _ladderBuf = new Collider[8]; // reused by the climb overlap check

        // Crouch: captured standing pose + the current 0..1 crouch blend.
        private float _standHeight, _bottomOffset, _standCamY, _crouchT;
        private Vector3 _standCenter;

        // Visual state read by PlayerAvatar. Only meaningful where Tick runs (the server).
        /// <summary>Current 0..1 crouch blend.</summary>
        public float CrouchBlend => _crouchT;
        /// <summary>True while the last Tick was in ladder-climb mode.</summary>
        public bool IsClimbing { get; private set; }
        /// <summary>CharacterController grounded state from the last Move.</summary>
        public bool IsGrounded => _cc != null && _cc.isGrounded;

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
            _camYaw = 0f;
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
            if (input.FreeLook)
            {
                // Free look: yaw orbits the camera pivot; the body is steered by movement.
                _camYaw += input.Look.x * lookSensitivity;
            }
            else
            {
                // First person: the body owns yaw. Fold any free-look offset back into the
                // body first, so the view direction survives the mode switch.
                if (_camYaw != 0f)
                {
                    transform.Rotate(0f, _camYaw, 0f);
                    _camYaw = 0f;
                }
                transform.Rotate(0f, input.Look.x * lookSensitivity, 0f);
            }

            _pitch = Mathf.Clamp(_pitch - input.Look.y * lookSensitivity, minPitch, maxPitch);
            ApplyPivot();
        }

        private void ApplyPivot()
        {
            if (cameraPivot != null)
                cameraPivot.localRotation = Quaternion.Euler(_pitch, _camYaw, 0f);
        }

        private void ApplyMove(in PlayerInputState input, float dt)
        {
            float speed = input.Crouch ? crouchSpeed : input.Sprint ? sprintSpeed : walkSpeed;
            // Move in the LOOK frame — the body's in first person, the camera's in free
            // look — so WASD always means what the player sees on screen.
            Quaternion frame = transform.rotation * Quaternion.Euler(0f, _camYaw, 0f);
            Vector3 wish = frame * new Vector3(input.Move.x, 0f, input.Move.y);
            wish = Vector3.ClampMagnitude(wish, 1f) * speed;

            // Climb mode: inside a Ladder volume, forward/back input becomes vertical motion and
            // gravity is off. Horizontal input still works (damped) so the player can press into
            // the rungs and, at the top, push over onto the platform. Jump hops off.
            IsClimbing = OnLadder();
            if (IsClimbing)
            {
                wish *= climbMoveFactor;
                _verticalVelocity = input.Move.y * climbSpeed;
                if (input.JumpPressed)
                    _verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
                _externalVel = Vector3.MoveTowards(_externalVel, Vector3.zero, airLaunchDamping * dt);
            }
            // Grounded only resets a downward velocity; a positive (jumping/just-launched) vertical is
            // left alone so a jump pad fired while still touching the pad isn't immediately cancelled.
            else if (_cc.isGrounded && _verticalVelocity <= 0f)
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

            // Free look: the body turns to face where it is walking, and the camera pivot
            // counter-rotates so the view holds steady while the body swings under it.
            // Not while climbing — on a ladder you keep facing the rungs.
            if (input.FreeLook && !IsClimbing && input.Move.sqrMagnitude > 0.0001f)
            {
                float yaw = transform.eulerAngles.y;
                float targetYaw = Mathf.Atan2(wish.x, wish.z) * Mathf.Rad2Deg;
                float delta = Mathf.DeltaAngle(yaw, Mathf.MoveTowardsAngle(yaw, targetYaw, faceTurnSpeed * dt));
                transform.Rotate(0f, delta, 0f);
                _camYaw -= delta;
                ApplyPivot();
            }

            Vector3 velocity = wish + _externalVel + Vector3.up * _verticalVelocity;
            _cc.Move(velocity * dt);
        }

        // Is the capsule (slightly expanded) touching a Ladder trigger volume?
        private bool OnLadder()
        {
            Vector3 center = transform.position + _cc.center;
            float half = Mathf.Max(0f, _cc.height * 0.5f - _cc.radius);
            int n = Physics.OverlapCapsuleNonAlloc(center - Vector3.up * half, center + Vector3.up * half,
                _cc.radius + 0.2f, _ladderBuf, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
                if (_ladderBuf[i].GetComponentInParent<Game.Gameplay.Ladder>() != null) return true;
            return false;
        }
    }
}
