using Mirror;
using UnityEngine;
using Game.Gameplay;

namespace Game.Player
{
    /// <summary>
    /// Networked driver for a player (Mirror, server-authoritative).
    ///
    /// The owning client samples local input each frame and ships it to the server
    /// via <see cref="CmdMove"/>; the server is the only place <see cref="PlayerController.Tick"/>
    /// runs, so movement is authoritative. A NetworkTransform on the same prefab
    /// replicates the resulting transform to every client.
    ///
    /// Look/camera is sent through the same path, so on a remote client it is as
    /// laggy as the round-trip until we add client-side prediction (a planned later
    /// step). In host mode (server == local client) it is frame-perfect.
    /// </summary>
    [RequireComponent(typeof(PlayerController))]
    [RequireComponent(typeof(PlayerInputReader))]
    public class NetworkPlayer : NetworkBehaviour
    {
        [Tooltip("The CameraPivot child; the local client's cameras are bound to this on spawn.")]
        [SerializeField] private Transform cameraPivot;

        [Tooltip("Raised on local-player spawn so the CameraRig binds to us (assign the asset).")]
        [SerializeField] private LocalPlayerReadyChannel localPlayerReady;

        [Tooltip("Shared Steam state, read for our display name (assign the SteamSession asset).")]
        [SerializeField] private Game.Net.SteamSession session;

        [Tooltip("Floating name label, instantiated per player (assign the Nameplate prefab).")]
        [SerializeField] private NameplateView nameplatePrefab;

        [Tooltip("If a player falls below this world Y, the server respawns them at their spawn pose.")]
        [SerializeField] private float killY = -10f;

        // Display name, set by the owner from its Steam persona and synced to everyone.
        [SyncVar(hook = nameof(OnNameChanged))]
        private string playerName = "";

        /// <summary>
        /// Test hook: when set (e.g. by a headless build started with -autowalk), the
        /// local player ignores sampled input and walks straight forward. Used only to
        /// validate movement replication across processes; no effect in normal play.
        /// </summary>
        public static bool DebugAutoWalk;

        private PlayerController _controller;
        private PlayerInputReader _input;
        private NetworkTransformBase _netTransform;
        private NameplateView _nameplate;
        private Game.Ship.ShipRider _rider;         // optional: aboard-ship parenting
        private Game.Ship.PlayerHelmUser _helmUser; // optional: steering-station input routing

        // Server-only: the pose to respawn this player at (last checkpoint, or initial spawn).
        private Vector3 _spawnPos;
        private Quaternion _spawnRot;
        // Server-only: the original spawn pose, used by a full run restart (ignores checkpoints).
        private Vector3 _startPos;
        private Quaternion _startRot;
        private RunManager _run; // cached scene run clock (server-side restart)

        private void Awake()
        {
            _controller = GetComponent<PlayerController>();
            _input = GetComponent<PlayerInputReader>();
            _netTransform = GetComponent<NetworkTransformBase>();
            _rider = GetComponent<Game.Ship.ShipRider>();
            _helmUser = GetComponent<Game.Ship.PlayerHelmUser>();
            // Instantiate this player's floating name label from the prefab.
            if (nameplatePrefab != null)
                _nameplate = Instantiate(nameplatePrefab, transform);
            // Only the local player samples input; enabled for it in OnStartLocalPlayer.
            _input.enabled = false;
        }

        public override void OnStartServer()
        {
            // Players spawn at a NetworkStartPosition; remember it as both the checkpoint respawn
            // pose and the fixed start pose (the latter is used by a full run restart).
            _spawnPos = _startPos = transform.position;
            _spawnRot = _startRot = transform.rotation;
        }

        /// <summary>Server: update the respawn pose (called by a checkpoint trigger).</summary>
        [Server]
        public void SetCheckpoint(Vector3 position, Quaternion rotation)
        {
            _spawnPos = position;
            _spawnRot = rotation;
        }

        /// <summary>Server: send this player back to its last checkpoint (called by a hazard volume).</summary>
        [Server]
        public void RespawnAtCheckpoint() => ServerRespawn();

        /// <summary>Server: impart a launch velocity to this player (called by a jump pad).</summary>
        [Server]
        public void Launch(Vector3 velocity) => _controller.Launch(velocity);

        /// <summary>Server: teleport to the original start and clear any checkpoint (full restart).</summary>
        [Server]
        public void RespawnAtStart()
        {
            _spawnPos = _startPos;
            _spawnRot = _startRot;
            ServerRespawn();
        }

        /// <summary>Owner-side entry point (UI button / restart key): ask the server to restart the run.</summary>
        public void RequestRestart()
        {
            if (isLocalPlayer) CmdRequestRestart();
        }

        // Owner -> server: reset the shared run clock, props, and send everyone back to the start.
        [Command]
        private void CmdRequestRestart()
        {
            if (_run == null) _run = FindFirstObjectByType<RunManager>();
            if (_run != null) _run.ResetRun();

            // Drop anything being carried, then return every prop to its spawn pose.
            foreach (PlayerGrabber grabber in FindObjectsByType<PlayerGrabber>(FindObjectsSortMode.None))
                grabber.ServerForceDrop();
            foreach (Grabbable prop in FindObjectsByType<Grabbable>(FindObjectsSortMode.None))
                prop.ResetToStart();

            foreach (NetworkPlayer p in FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None))
                p.RespawnAtStart();
        }

        public override void OnStartClient()
        {
            // Apply whatever name is already synced (covers late joiners / initial value).
            if (_nameplate != null)
                _nameplate.SetName(playerName);
        }

        public override void OnStartLocalPlayer()
        {
            _input.enabled = true;

            // Announce ourselves so the CameraRig binds its cameras to our pivot.
            if (localPlayerReady != null && cameraPivot != null)
                localPlayerReady.Raise(cameraPivot);

            // Tell the server who we are, and hide our own plate so it doesn't block our view.
            CmdSetName(session != null ? session.LocalName : "Player");
            if (_nameplate != null)
                _nameplate.Hide();
        }

        // Owner -> server: claim a display name. Server validates and the SyncVar fans it out.
        [Command]
        private void CmdSetName(string requestedName)
        {
            string clean = string.IsNullOrWhiteSpace(requestedName) ? "Player" : requestedName.Trim();
            if (clean.Length > 24) clean = clean.Substring(0, 24);
            playerName = clean;
        }

        private void OnNameChanged(string oldName, string newName)
        {
            if (_nameplate != null)
                _nameplate.SetName(newName);
        }

        private void Update()
        {
            // The server owns the simulation, so it enforces world bounds for every player.
            if (isServer && transform.position.y < killY)
                ServerRespawn();

            if (!isLocalPlayer) return;

            PlayerInputState s = _input.Sample();
            if (DebugAutoWalk) s.Move = new Vector2(0f, 1f); // forward, for replication testing
            // Third person is free look: the camera orbits and Move steers the body.
            // Camera choice is client-side, so it travels with the input.
            s.FreeLook = !CameraRig.IsFirstPerson;
            // At a ship's helm, Move becomes rudder/sail commands and is stripped from the state.
            if (_helmUser != null) _helmUser.HandleInput(ref s);

            if (isServer)
                // Host: we ARE the server, so tick directly instead of round-tripping through
                // the local connection. Mirror drains that queue after LateUpdate, which would
                // move us AFTER the camera has already updated each frame — the camera rides
                // one stale frame behind the model, which reads as judder in third person.
                ServerMove(s.Move, s.Look, s.Sprint, s.JumpPressed, s.Crouch, s.FreeLook, Time.deltaTime);
            else
                CmdMove(s.Move, s.Look, s.Sprint, s.JumpPressed, s.Crouch, s.FreeLook, Time.deltaTime);
        }

        /// <summary>Server-authoritative respawn: reset the simulation pose and snap clients.</summary>
        [Server]
        private void ServerRespawn()
        {
            // A respawn always puts the player ashore first (unparents + releases any helm),
            // otherwise the teleport pose would be interpreted in the ship's local space.
            if (_rider != null) _rider.ServerBoard(null);
            _controller.Respawn(_spawnPos, _spawnRot);
            // Force a teleport (not an interpolated slide) on every client.
            if (_netTransform != null)
                _netTransform.ServerTeleport(_spawnPos, _spawnRot);
        }

        // Owner -> server. Server is authoritative over the simulation.
        [Command]
        private void CmdMove(Vector2 move, Vector2 look, bool sprint, bool jumpPressed, bool crouch, bool freeLook, float dt)
            => ServerMove(move, look, sprint, jumpPressed, crouch, freeLook, dt);

        // Shared server-side movement step: remote owners arrive here via CmdMove, the
        // host's own player calls it synchronously from Update (see there for why).
        [Server]
        private void ServerMove(Vector2 move, Vector2 look, bool sprint, bool jumpPressed, bool crouch, bool freeLook, float dt)
        {
            // Never trust client-supplied dt blindly; clamp to avoid teleport exploits/hitches.
            dt = Mathf.Clamp(dt, 0f, 0.1f);

            // Server-side enforcement of the wheel lock (the owner already strips these).
            if (_helmUser != null && _helmUser.Engaged)
            {
                move = Vector2.zero;
                jumpPressed = false;
                sprint = false;
            }

            var input = new PlayerInputState
            {
                Move = move,
                Look = look,
                Sprint = sprint,
                Crouch = crouch,
                JumpPressed = jumpPressed,
                FreeLook = freeLook,
            };
            _controller.Tick(input, dt);
        }
    }
}
