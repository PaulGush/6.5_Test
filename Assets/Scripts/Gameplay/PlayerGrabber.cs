using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using Game.Player;

namespace Game.Gameplay
{
    /// <summary>
    /// Server-authoritative grab/carry/drop/throw of <see cref="Grabbable"/> props.
    /// The owner edge-reads the rebindable Interact (grab/drop) and Attack (throw) actions and sends
    /// Commands; the server carries the prop as a real dynamic body — driven toward the hold anchor by
    /// velocity each physics step — so it is blocked by the floor/walls (no clipping) and still bumps
    /// other props and players. Collisions between the held prop and its own carrier are ignored, so a
    /// player can't shove themselves around (or fly) with what they're holding. Input is gated to
    /// locked-cursor gameplay so menu clicks don't throw.
    ///
    /// The owner also publishes the on-screen prompt state (looking at a prop / carrying one) plus the
    /// live binding strings to a <see cref="GrabPromptChannel"/> that a HUD view renders.
    /// </summary>
    public class PlayerGrabber : NetworkBehaviour
    {
        [Tooltip("Where a held prop is carried (a transform in front of the player).")]
        [SerializeField] private Transform holdAnchor;
        [Tooltip("Aim source for throwing + looking at props (the CameraPivot, so it follows look pitch).")]
        [SerializeField] private Transform aimSource;
        [SerializeField] private float grabRadius = 1.1f;
        [SerializeField] private float throwForce = 12f;
        [Tooltip("Cap on how fast a carried prop chases the hold anchor (m/s). Keeps it from tunnelling.")]
        [SerializeField] private float followMaxSpeed = 20f;
        [Tooltip("Cap on how fast a carried prop spins toward its carry orientation (deg/s).")]
        [SerializeField] private float followMaxAngularSpeed = 720f;

        [Header("Prompt")]
        [Tooltip("Channel the HUD listens to (assign the GrabPromptChannel asset).")]
        [SerializeField] private GrabPromptChannel promptChannel;
        [Tooltip("How far the look-ray reaches when deciding whether a prop can be grabbed.")]
        [SerializeField] private float interactRange = 3.5f;
        [Tooltip("Radius of the look-ray, so you don't need pixel-perfect aim.")]
        [SerializeField] private float lookRadius = 0.3f;

        // Server: whether a prop is currently held; synced so the owning client's HUD knows to show
        // the Drop/Throw prompts even on a remote (non-host) client.
        [SyncVar] private bool _holding;

        private Grabbable _held;            // server-only
        private Collider _carrier;          // this player's own collider, ignored while carrying
        private PlayerInputReader _reader;  // source of the rebindable Interact/Attack actions

        private void Awake()
        {
            _carrier = GetComponent<CharacterController>();
            _reader = GetComponent<PlayerInputReader>();
        }

        public override void OnStopLocalPlayer()
        {
            if (promptChannel != null) promptChannel.Set(GrabPromptChannel.State.None, "", "");
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            bool gameplay = Cursor.lockState == CursorLockMode.Locked; // not the menu
            if (gameplay && _reader != null)
            {
                if (_reader.Interact != null && _reader.Interact.WasPressedThisFrame()) CmdGrabOrDrop();
                if (_reader.Throw != null && _reader.Throw.WasPressedThisFrame()) CmdThrow();
            }

            UpdatePrompt(gameplay);
        }

        /// <summary>Owner-only: work out which prompt to show and push it (with live bindings) to the HUD.</summary>
        private void UpdatePrompt(bool gameplay)
        {
            if (promptChannel == null) return;

            if (!gameplay)
            {
                promptChannel.Set(GrabPromptChannel.State.None, "", "");
                return;
            }

            GrabPromptChannel.State state =
                _holding ? GrabPromptChannel.State.Holding :
                LookingAtGrabbable() ? GrabPromptChannel.State.CanGrab :
                GrabPromptChannel.State.None;

            string grab = _reader != null ? DisplayBinding(_reader.Interact) : "";
            string throwBind = _reader != null ? DisplayBinding(_reader.Throw) : "";
            promptChannel.Set(state, grab, throwBind);
        }

        /// <summary>Owner-only client check: is a free prop within range along the look direction?</summary>
        private bool LookingAtGrabbable()
        {
            Transform eye = aimSource != null ? aimSource : transform;
            if (Physics.SphereCast(eye.position, lookRadius, eye.forward, out RaycastHit hit,
                    interactRange, ~0, QueryTriggerInteraction.Ignore))
            {
                var g = hit.collider.GetComponentInParent<Grabbable>();
                return g != null && !g.IsHeld;
            }
            return false;
        }

        /// <summary>Display string for an action's binding in the currently-active control scheme.</summary>
        private static string DisplayBinding(InputAction action)
        {
            if (action == null) return "";
            string group = GamepadActive() ? "Gamepad" : "Keyboard&Mouse";

            var bindings = action.bindings;
            for (int i = 0; i < bindings.Count; i++)
            {
                InputBinding b = bindings[i];
                if (b.isComposite || b.isPartOfComposite) continue;
                if (!string.IsNullOrEmpty(b.groups) && b.groups.Contains(group))
                    return action.GetBindingDisplayString(i, InputBinding.DisplayStringOptions.DontIncludeInteractions);
            }
            return action.GetBindingDisplayString();
        }

        /// <summary>True if a gamepad was the most recently used device (so we show gamepad glyphs).</summary>
        private static bool GamepadActive()
        {
            Gamepad pad = Gamepad.current;
            if (pad == null) return false;

            double kbm = 0;
            if (Keyboard.current != null) kbm = Keyboard.current.lastUpdateTime;
            if (Mouse.current != null) kbm = System.Math.Max(kbm, Mouse.current.lastUpdateTime);
            return pad.lastUpdateTime >= kbm;
        }

        private void FixedUpdate()
        {
            if (!isServer || _held == null || holdAnchor == null) return;

            Rigidbody body = _held.Body;

            // Build the carry anchor from the player's yaw plus a pitch that is clamped so the prop
            // can tilt up but never dips below horizontal. Looking further down just leaves it level,
            // which also keeps it from being driven into the floor. (holdAnchor is a child of the
            // CameraPivot/aimSource, which carries the raw look pitch.)
            Quaternion anchorRot;
            Vector3 anchorPos;
            if (aimSource != null)
            {
                float pitch = aimSource.localEulerAngles.x;
                if (pitch > 180f) pitch -= 360f;   // to signed degrees; looking up is negative
                pitch = Mathf.Min(pitch, 0f);      // clamp the downward (positive) half to level
                anchorRot = transform.rotation * Quaternion.Euler(pitch, 0f, 0f);
                anchorPos = aimSource.position + anchorRot * holdAnchor.localPosition;
            }
            else
            {
                anchorRot = holdAnchor.rotation;
                anchorPos = holdAnchor.position;
            }

            // Orient by the carry rotation, then place so the grip point sits on the anchor
            // (a plank gripped at one end extends forward, clear of the camera).
            Quaternion targetRot = anchorRot * _held.HoldLocalRotation;
            Vector3 targetPos = anchorPos - targetRot * _held.HoldLocalGrip;

            // Drive the prop with velocity (not MovePosition) so the physics solver still stops it
            // against the floor and walls instead of letting it clip through. Speed is capped so a
            // far target can't fling it through thin geometry in a single step.
            Vector3 toTarget = targetPos - body.position;
            Vector3 desiredVel = toTarget / Time.fixedDeltaTime;
            if (desiredVel.sqrMagnitude > followMaxSpeed * followMaxSpeed)
                desiredVel = desiredVel.normalized * followMaxSpeed;
            body.linearVelocity = desiredVel;

            // Spin toward the target orientation, likewise capped.
            Quaternion deltaRot = targetRot * Quaternion.Inverse(body.rotation);
            deltaRot.ToAngleAxis(out float angleDeg, out Vector3 axis);
            if (angleDeg > 180f) angleDeg -= 360f;
            if (Mathf.Abs(angleDeg) > 0.05f && axis.sqrMagnitude > 1e-6f)
            {
                float angSpeed = Mathf.Clamp(angleDeg, -followMaxAngularSpeed, followMaxAngularSpeed) * Mathf.Deg2Rad / Time.fixedDeltaTime;
                body.angularVelocity = axis.normalized * angSpeed;
            }
            else
            {
                body.angularVelocity = Vector3.zero;
            }
        }

        [Command]
        private void CmdGrabOrDrop()
        {
            if (_held != null) { Release(); return; }

            _held = FindNearestGrabbable();
            if (_held != null)
            {
                _held.SetHeld(true);
                IgnoreCarrierCollision(true);
                _holding = true;
            }
        }

        [Command]
        private void CmdThrow()
        {
            if (_held == null) return;

            Rigidbody body = _held.Body;
            Vector3 dir = (aimSource != null ? aimSource.forward : transform.forward).normalized;
            Release();
            body.linearVelocity = dir * throwForce + Vector3.up * 2f;
        }

        [Server]
        private Grabbable FindNearestGrabbable()
        {
            Collider[] hits = Physics.OverlapSphere(holdAnchor.position, grabRadius, ~0, QueryTriggerInteraction.Ignore);
            Grabbable best = null;
            float bestSqr = float.MaxValue;
            foreach (var h in hits)
            {
                var g = h.GetComponentInParent<Grabbable>();
                if (g == null || g.IsHeld) continue;
                // Measure to the nearest point on the collider, not its pivot, so long props
                // (e.g. the plank) can be grabbed by whichever end the player is standing at.
                float sqr = (h.ClosestPoint(holdAnchor.position) - holdAnchor.position).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = g; }
            }
            return best;
        }

        [Server]
        private void Release()
        {
            if (_held == null) return;
            IgnoreCarrierCollision(false);
            _held.SetHeld(false);
            _held = null;
            _holding = false;
        }

        /// <summary>Toggle collision between the currently-held prop and this player's own body.</summary>
        [Server]
        private void IgnoreCarrierCollision(bool ignore)
        {
            if (_held == null || _carrier == null || _held.Col == null) return;
            Physics.IgnoreCollision(_held.Col, _carrier, ignore);
        }
    }
}
