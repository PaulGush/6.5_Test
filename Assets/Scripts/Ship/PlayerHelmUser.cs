using Mirror;
using UnityEngine;
using Game.Player;

namespace Game.Ship
{
    /// <summary>
    /// Player-side use of the ship's crew stations. The helm: look at the wheel, press Interact
    /// to take it; while engaged, A/D steers the rudder, look stays free, Interact lets go.
    /// Sail stations: look at a mast's station marker and press Interact to furl/unfurl that
    /// mast — ship speed is how many masts fly canvas, so sail trim is a separate job from
    /// steering.
    ///
    /// Movement suppression happens in <see cref="NetworkPlayer"/> (which calls
    /// <see cref="HandleInput"/> before sending its move command) and is re-checked server-side.
    /// <see cref="Game.Gameplay.PlayerGrabber"/> also consults us so the shared Interact key
    /// doesn't grab a prop and work a station in the same press.
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
        /// <summary>Owner-only: a mast's sail station is under the crosshair right now.</summary>
        public bool LookingAtSail => _sailInView != null;

        private PlayerInputReader _reader;
        private ShipRider _rider;
        private ShipHelm _helmInView;       // owner-only, refreshed each frame
        private ShipSailTarget _sailInView; // owner-only, refreshed each frame
        private float _sentRudder;

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
            _helmInView = null;
            _sailInView = null;
            if (gameplay && !Engaged) FindStationInView();
            LookingAtHelm = _helmInView != null;

            if (gameplay && _reader != null && _reader.Interact != null
                && _reader.Interact.WasPressedThisFrame())
            {
                if (Engaged) CmdLeaveHelm();
                else if (_helmInView != null) CmdEngageHelm(_helmInView);
                else if (_sailInView != null) CmdToggleSail(_sailInView.Station);
            }
        }

        /// <summary>
        /// Owner-side, called by NetworkPlayer with the frame's sampled input before it is sent.
        /// While engaged: converts Move.x into rudder commands and strips movement (plus
        /// jump/sprint) from the state so the body stays planted at the wheel. Returns true if
        /// input was consumed. Sails are NOT set here — that's the mast stations' job.
        /// </summary>
        public bool HandleInput(ref PlayerInputState input)
        {
            if (!Engaged) return false;

            float rudder = Mathf.Clamp(input.Move.x, -1f, 1f);
            if (Mathf.Abs(rudder - _sentRudder) > 0.01f)
            {
                _sentRudder = rudder;
                CmdSteer(rudder);
            }

            input.Move = Vector2.zero;
            input.JumpPressed = false;
            input.Sprint = false;
            return true;
        }

        /// <summary>
        /// Owner-side: the controls line the HUD shows while steering, including live sail count
        /// (worded for the active device). Sail state is synced, so this stays correct even though
        /// the sails are trimmed by other players at the masts.
        /// </summary>
        public string SteeringContext()
        {
            if (_helm == null || _helm.Ship == null) return "";
            string controls = Game.Gameplay.PlayerGrabber.GamepadActive()
                ? "[Stick ←→]  Steer"
                : "[A/D]  Steer";
            return $"{controls}    Sails {_helm.Ship.SailLevel}/{_helm.Ship.MaxSailLevel}";
        }

        /// <summary>Owner-side: prompt text when looking at a mast's sail station.</summary>
        public string SailPromptText() =>
            _sailInView != null && _sailInView.Station != null
                ? (_sailInView.Station.Unfurled ? "Furl sails" : "Unfurl sails")
                : "";

        // One look-ray serves both station types; at most one can be under the crosshair.
        private void FindStationInView()
        {
            Transform eye = aimSource != null ? aimSource : transform;
            if (!Physics.SphereCast(eye.position, lookRadius, eye.forward, out RaycastHit hit,
                    interactRange, ~0, QueryTriggerInteraction.Ignore))
                return;

            var helmTarget = hit.collider.GetComponent<ShipHelmTarget>();
            if (helmTarget != null && helmTarget.Helm != null && !helmTarget.Helm.Occupied)
            {
                _helmInView = helmTarget.Helm;
                return;
            }

            var sailTarget = hit.collider.GetComponent<ShipSailTarget>();
            if (sailTarget != null && sailTarget.Station != null)
                _sailInView = sailTarget;
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
        private void CmdSteer(float rudder)
        {
            if (_helm == null || _helm.Ship == null) return;
            _helm.Ship.SetRudder(rudder);
        }

        [Command]
        private void CmdToggleSail(ShipSailStation station)
        {
            if (station == null || _rider == null) return;
            // Must be aboard the ship this mast belongs to (the station sits on the ship root).
            if (station.GetComponent<ShipController>() != _rider.CurrentShip) return;
            station.Toggle();
        }
    }
}
