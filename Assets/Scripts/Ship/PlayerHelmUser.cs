using Mirror;
using UnityEngine;
using Game.Player;

namespace Game.Ship
{
    /// <summary>
    /// Player-side use of a ship's helm. The owner looks at the wheel and presses Interact to
    /// take it; while engaged their Move input steers the ship instead of their legs (A/D =
    /// rudder, W/S = set/furl one sail level), look stays free, and Interact again lets go.
    ///
    /// Movement suppression happens in <see cref="NetworkPlayer"/> (which calls
    /// <see cref="HandleInput"/> before sending its move command) and is re-checked server-side.
    /// <see cref="Game.Gameplay.PlayerGrabber"/> also consults us so the shared Interact key
    /// doesn't grab a prop and take the wheel in the same press.
    /// </summary>
    public class PlayerHelmUser : NetworkBehaviour
    {
        [Tooltip("Aim source for finding the wheel (the CameraPivot, so it follows look pitch).")]
        [SerializeField] private Transform aimSource;
        [Tooltip("How far the look-ray reaches when deciding whether the wheel can be taken.")]
        [SerializeField] private float interactRange = 3.5f;
        [Tooltip("Radius of the look-ray, so you don't need pixel-perfect aim.")]
        [SerializeField] private float lookRadius = 0.35f;

        // The helm this player is steering, synced so remote instances (and the server's move
        // command guard) all know the player is wheel-locked.
        [SyncVar] private ShipHelm _helm;

        public bool Engaged => _helm != null;
        /// <summary>Owner-only: a free helm is under the crosshair right now.</summary>
        public bool LookingAtHelm { get; private set; }

        private PlayerInputReader _reader;
        private ShipRider _rider;
        private float _sentRudder;
        private float _prevSailAxis;

        /// <summary>Editor-tooling hook for wiring at build time.</summary>
        public void SetAimSource(Transform value) => aimSource = value;

        private void Awake()
        {
            _reader = GetComponent<PlayerInputReader>();
            _rider = GetComponent<ShipRider>();
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            bool gameplay = Cursor.lockState == CursorLockMode.Locked;
            LookingAtHelm = gameplay && !Engaged && FindHelmInView() != null;

            if (gameplay && _reader != null && _reader.Interact != null
                && _reader.Interact.WasPressedThisFrame())
            {
                if (Engaged) CmdLeaveHelm();
                else if (LookingAtHelm)
                {
                    ShipHelm helm = FindHelmInView();
                    if (helm != null) CmdEngageHelm(helm);
                }
            }
        }

        /// <summary>
        /// Owner-side, called by NetworkPlayer with the frame's sampled input before it is sent.
        /// While engaged: converts Move into helm commands and strips it (plus jump/sprint) from
        /// the state so the body stays planted at the wheel. Returns true if input was consumed.
        /// </summary>
        public bool HandleInput(ref PlayerInputState input)
        {
            if (!Engaged)
            {
                _prevSailAxis = 0f;
                return false;
            }

            float rudder = Mathf.Clamp(input.Move.x, -1f, 1f);

            // W/S edge-triggered: one sail level per press, so holding W doesn't zip to full sail.
            float sailAxis = input.Move.y;
            int sailDelta = 0;
            if (_prevSailAxis < 0.5f && sailAxis >= 0.5f) sailDelta = 1;
            else if (_prevSailAxis > -0.5f && sailAxis <= -0.5f) sailDelta = -1;
            _prevSailAxis = sailAxis;

            if (sailDelta != 0 || Mathf.Abs(rudder - _sentRudder) > 0.01f)
            {
                _sentRudder = rudder;
                CmdControl(rudder, sailDelta);
            }

            input.Move = Vector2.zero;
            input.JumpPressed = false;
            input.Sprint = false;
            return true;
        }

        /// <summary>
        /// Owner-side: the controls line the HUD shows while steering, including live sail level
        /// (worded for the active device). Sail state is synced, so this stays correct even though
        /// the ship is simulated on the server.
        /// </summary>
        public string SteeringContext()
        {
            if (_helm == null || _helm.Ship == null) return "";
            string controls = Game.Gameplay.PlayerGrabber.GamepadActive()
                ? "[Stick ←→]  Steer    [Stick ↑↓]  Sails"
                : "[A/D]  Steer    [W/S]  Sails";
            return $"{controls} {_helm.Ship.SailLevel}/{_helm.Ship.MaxSailLevel}";
        }

        private ShipHelm FindHelmInView()
        {
            Transform eye = aimSource != null ? aimSource : transform;
            if (Physics.SphereCast(eye.position, lookRadius, eye.forward, out RaycastHit hit,
                    interactRange, ~0, QueryTriggerInteraction.Ignore))
            {
                var target = hit.collider.GetComponent<ShipHelmTarget>();
                if (target != null && target.Helm != null && !target.Helm.Occupied)
                    return target.Helm;
            }
            return null;
        }

        [Command]
        private void CmdEngageHelm(ShipHelm helm)
        {
            if (helm == null || _helm != null) return;
            // Must actually be aboard the ship whose wheel this is — no steering from the dock.
            if (_rider == null || _rider.CurrentShip != helm.Ship) return;

            if (helm.TryEngage(netIdentity)) _helm = helm;
        }

        [Command]
        private void CmdLeaveHelm() => ServerLeaveHelm();

        /// <summary>Server: let go of the wheel (also called when leaving the ship or respawning).
        /// The rudder keeps its last deflection — an abandoned wheel stays hard over.</summary>
        [Server]
        public void ServerLeaveHelm()
        {
            if (_helm == null) return;
            _helm.Release(netIdentity);
            _helm = null;
        }

        [Command]
        private void CmdControl(float rudder, int sailDelta)
        {
            if (_helm == null || _helm.Ship == null) return;
            _helm.Ship.SetRudder(rudder);
            if (sailDelta != 0) _helm.Ship.ChangeSail(sailDelta);
        }
    }
}
