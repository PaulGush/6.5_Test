using Mirror;
using UnityEngine;

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

        /// <summary>
        /// Test hook: when set (e.g. by a headless build started with -autowalk), the
        /// local player ignores sampled input and walks straight forward. Used only to
        /// validate movement replication across processes; no effect in normal play.
        /// </summary>
        public static bool DebugAutoWalk;

        private PlayerController _controller;
        private PlayerInputReader _input;

        private void Awake()
        {
            _controller = GetComponent<PlayerController>();
            _input = GetComponent<PlayerInputReader>();
            // Only the local player samples input; enabled for it in OnStartLocalPlayer.
            _input.enabled = false;
        }

        public override void OnStartLocalPlayer()
        {
            _input.enabled = true;
            if (CameraRig.Instance != null && cameraPivot != null)
                CameraRig.Instance.Bind(cameraPivot);
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            PlayerInputState s = _input.Sample();
            if (DebugAutoWalk) s.Move = new Vector2(0f, 1f); // forward, for replication testing
            CmdMove(s.Move, s.Look, s.Sprint, s.JumpPressed, Time.deltaTime);
        }

        // Owner -> server. Server is authoritative over the simulation.
        [Command]
        private void CmdMove(Vector2 move, Vector2 look, bool sprint, bool jumpPressed, float dt)
        {
            // Never trust client-supplied dt blindly; clamp to avoid teleport exploits/hitches.
            dt = Mathf.Clamp(dt, 0f, 0.1f);

            var input = new PlayerInputState
            {
                Move = move,
                Look = look,
                Sprint = sprint,
                JumpPressed = jumpPressed,
            };
            _controller.Tick(input, dt);
        }
    }
}
