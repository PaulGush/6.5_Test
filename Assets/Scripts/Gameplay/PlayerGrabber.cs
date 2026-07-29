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
        [Tooltip("Gap kept between a carried prop and a wall when its path forward is blocked (m).")]
        [SerializeField] private float carrySkin = 0.02f;

        [Header("Prompt")]
        [Tooltip("Channel the HUD listens to (assign the GrabPromptChannel asset).")]
        [SerializeField] private GrabPromptChannel promptChannel;
        [Tooltip("How far the look-ray reaches when deciding whether a prop can be grabbed.")]
        [SerializeField] private float interactRange = 3.5f;
        [Tooltip("Radius of the look-ray, so you don't need pixel-perfect aim.")]
        [SerializeField] private float lookRadius = 0.3f;

        // The prop currently held by this player, synced to everyone so (a) the owner's HUD shows the
        // Drop/Throw prompts even on a remote client and (b) every client carries it locally in LateUpdate.
        [SyncVar] private Grabbable _heldSync;

        private Grabbable _held;            // server-only handle for grab/throw logic
        private Collider _carrier;          // this player's own collider, ignored while carrying
        private PlayerInputReader _reader;  // source of the rebindable Interact/Attack actions
        private Game.Ship.PlayerHelmUser _helmUser; // shares the Interact key; wheel wins over grab
        private readonly Collider[] _overlapBuf = new Collider[16]; // reused by the carry depenetration

        private void Awake()
        {
            _carrier = GetComponent<CharacterController>();
            _reader = GetComponent<PlayerInputReader>();
            _helmUser = GetComponent<Game.Ship.PlayerHelmUser>();
        }

        public override void OnStopLocalPlayer()
        {
            if (promptChannel != null) promptChannel.Set(GrabPromptChannel.State.None, "", "");
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            bool gameplay = Cursor.lockState == CursorLockMode.Locked; // not the menu
            // The helm shares the Interact key: while steering (or aiming at a free wheel),
            // the press belongs to PlayerHelmUser, not the grab.
            bool helmBusy = _helmUser != null && (_helmUser.Engaged || _helmUser.LookingAtHelm);
            if (gameplay && !helmBusy && _reader != null)
            {
                if (_reader.Interact != null && _reader.Interact.WasPressedThisFrame()) CmdGrabOrDrop();
                if (_reader.Throw != null && _reader.Throw.WasPressedThisFrame()) CmdThrow();
            }

            UpdatePrompt(gameplay);
        }

        /// <summary>Owner-only: work out which prompt to show and push it (with live bindings) to the
        /// HUD. Single publisher for the whole channel — grab AND helm states — so the two systems
        /// can never fight over what's on screen.</summary>
        private void UpdatePrompt(bool gameplay)
        {
            if (promptChannel == null) return;

            if (!gameplay)
            {
                promptChannel.Set(GrabPromptChannel.State.None, "", "");
                return;
            }

            string grab = _reader != null ? DisplayBinding(_reader.Interact) : "";

            // Helm outranks grab: it owns the Interact key while engaged or in view.
            if (_helmUser != null && _helmUser.Engaged)
            {
                promptChannel.Set(GrabPromptChannel.State.Steering, grab, "", _helmUser.SteeringContext());
                return;
            }
            if (_helmUser != null && _helmUser.LookingAtHelm)
            {
                promptChannel.Set(GrabPromptChannel.State.CanSteer, grab, "");
                return;
            }

            GrabPromptChannel.State state =
                _heldSync != null ? GrabPromptChannel.State.Holding :
                LookingAtGrabbable() ? GrabPromptChannel.State.CanGrab :
                GrabPromptChannel.State.None;

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

        /// <summary>True if a gamepad was the most recently used device (so we show gamepad glyphs).
        /// Internal so the helm prompt can word its controls line for the active device too.</summary>
        internal static bool GamepadActive()
        {
            Gamepad pad = Gamepad.current;
            if (pad == null) return false;

            double kbm = 0;
            if (Keyboard.current != null) kbm = Keyboard.current.lastUpdateTime;
            if (Mouse.current != null) kbm = System.Math.Max(kbm, Mouse.current.lastUpdateTime);
            return pad.lastUpdateTime >= kbm;
        }

        // Carry the held prop by transform at render rate, exactly like the player body is moved, so
        // it tracks the camera smoothly. (Driving it through physics at the fixed step made it visibly
        // step/judder near the camera, since rendering runs far faster than the physics rate.) This runs
        // on EVERY client off the synced carrier pose (the prop's own sync is suspended while held), so
        // remote players see a smooth carry too. The prop is kinematic while held (see Grabbable); a
        // sweep stops it short of walls so it can't clip through static geometry. LateUpdate runs after
        // the body/look update this frame.
        private void LateUpdate()
        {
            if (_heldSync == null || holdAnchor == null) return;

            Rigidbody body = _heldSync.Body;

            // Build the carry anchor from the player's yaw plus a pitch that is clamped so the prop
            // can tilt up but never dips below horizontal. Looking further down just leaves it level,
            // which also keeps it from being driven into the floor. (holdAnchor is a child of the
            // CameraPivot/aimSource, which carries the raw look pitch.)
            // Per-prop carry offset (anchor space) lets bulky props (e.g. a box) hang lower and
            // further out so they don't fill the screen, without moving the shared hold anchor.
            Vector3 propOffset = _heldSync.HoldAnchorOffset;

            Quaternion anchorRot;
            Vector3 anchorPos;
            if (aimSource != null)
            {
                float pitch = aimSource.localEulerAngles.x;
                if (pitch > 180f) pitch -= 360f;   // to signed degrees; looking up is negative
                pitch = Mathf.Min(pitch, 0f);      // clamp the downward (positive) half to level
                anchorRot = transform.rotation * Quaternion.Euler(pitch, 0f, 0f);
                anchorPos = aimSource.position + anchorRot * (holdAnchor.localPosition + propOffset);
            }
            else
            {
                anchorRot = holdAnchor.rotation;
                anchorPos = holdAnchor.position + anchorRot * propOffset;
            }

            // Orient by the carry rotation, then place so the grip point sits on the anchor
            // (a plank gripped at one end extends forward, clear of the camera).
            Quaternion targetRot = anchorRot * _heldSync.HoldLocalRotation;
            Vector3 targetPos = anchorPos - targetRot * _heldSync.HoldLocalGrip;

            // Sweep from the prop's current spot toward the target; if a wall is in the way, stop just
            // short of it so a kinematic carry can't push the prop through static geometry. The carrier's
            // own colliders are ignored (we already pass through them while carrying).
            Vector3 from = body.position;
            Vector3 delta = targetPos - from;
            float dist = delta.magnitude;
            if (dist > 1e-4f)
            {
                Vector3 dir = delta / dist;
                if (body.SweepTest(dir, out RaycastHit hit, dist, QueryTriggerInteraction.Ignore)
                    && hit.collider != _carrier && !hit.collider.transform.IsChildOf(transform))
                {
                    targetPos = from + dir * Mathf.Max(0f, hit.distance - carrySkin);
                }
            }

            // Rotation/look can still swing the prop into geometry (the SweepTest above only covers the
            // straight translation). Push it back out of any wall it now overlaps so turning can't clip it
            // through — it slides along the surface instead.
            targetPos = ResolvePenetration(_heldSync.Col, targetPos, targetRot);

            body.transform.SetPositionAndRotation(targetPos, targetRot);
        }

        /// <summary>Move <paramref name="pos"/> out of any static/kinematic geometry the prop would overlap
        /// at the given pose, so a kinematic carry can't be rotated or shoved through walls. Ignores the
        /// carrier, the prop itself, and free dynamic props (the physics solver handles those).</summary>
        private Vector3 ResolvePenetration(Collider propCol, Vector3 pos, Quaternion rot)
        {
            if (propCol == null) return pos;

            Vector3 half = propCol.bounds.extents + Vector3.one * 0.05f;
            for (int iter = 0; iter < 3; iter++)
            {
                int n = Physics.OverlapBoxNonAlloc(pos, half, _overlapBuf, rot, ~0, QueryTriggerInteraction.Ignore);
                bool moved = false;
                for (int i = 0; i < n; i++)
                {
                    Collider o = _overlapBuf[i];
                    if (o == propCol || o == _carrier) continue;
                    if (o.transform.IsChildOf(transform)) continue;                               // carrier's own colliders
                    if (o.attachedRigidbody != null && !o.attachedRigidbody.isKinematic) continue; // free dynamic prop: let the solver handle it
                    if (Physics.ComputePenetration(propCol, pos, rot, o, o.transform.position, o.transform.rotation,
                            out Vector3 dir, out float depth))
                    {
                        pos += dir * depth;
                        moved = true;
                    }
                }
                if (!moved) break;
            }
            return pos;
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
                _heldSync = _held; // sync the handle so every client carries it locally
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
            _heldSync = null;
        }

        /// <summary>Server: drop whatever this player is carrying (used by a run restart before props
        /// are reset to their start poses).</summary>
        [Server]
        public void ServerForceDrop() => Release();

        /// <summary>Toggle collision between the currently-held prop and this player's own body.</summary>
        [Server]
        private void IgnoreCarrierCollision(bool ignore)
        {
            if (_held == null || _carrier == null || _held.Col == null) return;
            Physics.IgnoreCollision(_held.Col, _carrier, ignore);
        }
    }
}
