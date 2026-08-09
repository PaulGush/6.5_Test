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
        /// <summary>The helm this player is steering, or null. Synced, so valid on every client.</summary>
        public ShipHelm CurrentHelm => _helm;
        /// <summary>Owner-only: a free helm is under the crosshair right now.</summary>
        public bool LookingAtHelm { get; private set; }
        /// <summary>Owner-only: a mast's sail station is under the crosshair right now.</summary>
        public bool LookingAtSail => _sailInView != null;
        /// <summary>Owner-only: a jetty's mooring bollard is under the crosshair right now.</summary>
        public bool LookingAtMooring => _mooringInView != null;
        /// <summary>Owner-only: the ship's bow anchor station is under the crosshair right now.</summary>
        public bool LookingAtAnchor => _anchorInView != null;

        private PlayerInputReader _reader;
        private ShipRider _rider;
        private Game.Gameplay.PlayerGrabber _grabber; // hands full = stations don't take the key
        private ShipHelm _helmInView;             // owner-only, refreshed each frame
        private ShipSailTarget _sailInView;       // owner-only, refreshed each frame
        private DockMooringTarget _mooringInView; // owner-only, refreshed each frame
        private ShipAnchorTarget _anchorInView;   // owner-only, refreshed each frame
        private float _sentRudder;

        /// <summary>Editor-tooling hook for wiring at build time.</summary>
        public void SetAimSource(Transform value) => aimSource = value;

        private void Awake()
        {
            _reader = GetComponent<PlayerInputReader>();
            _rider = GetComponent<ShipRider>();
            _grabber = GetComponent<Game.Gameplay.PlayerGrabber>();
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            bool gameplay = Cursor.lockState == CursorLockMode.Locked;
            _helmInView = null;
            _sailInView = null;
            _mooringInView = null;
            _anchorInView = null;
            // Hands full: the Interact press belongs to the grabber (setting the crate
            // down), even with a mast or bollard under the crosshair — carrying cargo to
            // the hold must never furl the sails. All the *InView flags stay false, so
            // PlayerGrabber's helm-defers-first check agrees with us automatically.
            bool handsFull = _grabber != null && _grabber.IsHolding;
            if (gameplay && !Engaged && !handsFull) FindStationInView();
            LookingAtHelm = _helmInView != null;

            if (gameplay && _reader != null && _reader.Interact != null
                && _reader.Interact.WasPressedThisFrame())
            {
                if (Engaged) CmdLeaveHelm();
                else if (_helmInView != null) CmdEngageHelm(_helmInView);
                else if (_sailInView != null) CmdToggleSail(_sailInView.Station);
                else if (_mooringInView != null) CmdToggleMooring(_mooringInView.Mooring);
                else if (_anchorInView != null) CmdToggleAnchor(_anchorInView.Ship);
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

        /// <summary>Owner-side: prompt text when looking at a jetty's mooring bollard.</summary>
        public string MooringPromptText() =>
            _mooringInView != null && _mooringInView.Mooring != null
                ? _mooringInView.Mooring.PromptText()
                : "";

        /// <summary>Owner-side: prompt text when looking at the bow anchor station.</summary>
        public string AnchorPromptText()
        {
            if (_anchorInView == null || _anchorInView.Ship == null) return "";
            ShipController ship = _anchorInView.Ship;
            if (!ship.Anchored) return "Drop anchor";
            return ship.WeighingAnchor ? "Let go the anchor" : "Weigh anchor";
        }

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
            {
                _sailInView = sailTarget;
                return;
            }

            var mooringTarget = hit.collider.GetComponent<DockMooringTarget>();
            if (mooringTarget != null && mooringTarget.Mooring != null)
            {
                _mooringInView = mooringTarget;
                return;
            }

            var anchorTarget = hit.collider.GetComponent<ShipAnchorTarget>();
            if (anchorTarget != null && anchorTarget.Ship != null)
                _anchorInView = anchorTarget;
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
        private void CmdToggleMooring(DockMooring mooring)
        {
            if (mooring == null) return;
            // Must actually be at the jetty (or on the ship alongside it) — no remote mooring.
            if (Vector3.Distance(transform.position, mooring.transform.position) > 12f) return;
            mooring.ServerToggle();
        }

        [Command]
        private void CmdToggleAnchor(ShipController ship)
        {
            if (ship == null || _rider == null) return;
            // Must be aboard this ship — the anchor is worked from its own bow, not the dock.
            if (_rider.CurrentShip != ship) return;
            ship.ServerToggleAnchor();
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
