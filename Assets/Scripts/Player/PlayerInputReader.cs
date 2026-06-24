using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Player
{
    /// <summary>
    /// Reads the "Player" action map from the project's InputActionAsset and
    /// produces a <see cref="PlayerInputState"/> on demand via <see cref="Sample"/>.
    ///
    /// Kept deliberately separate from movement so that in Phase 3 only the
    /// owning client runs this; remote/server instances receive the same struct
    /// over the network instead of sampling local devices.
    /// </summary>
    public class PlayerInputReader : MonoBehaviour
    {
        [Tooltip("The project InputActionAsset (InputSystem_Actions). A per-instance clone is made at runtime.")]
        [SerializeField] private InputActionAsset actions;

        private InputActionMap _playerMap;
        private InputAction _move, _look, _sprint, _jump;
        private bool _jumpQueued;

        /// <summary>Grab/drop action (default E / gamepad north). Edge-read by <see cref="PlayerGrabber"/>.</summary>
        public InputAction Interact { get; private set; }
        /// <summary>Throw action (default Left Button / gamepad west). Edge-read by <see cref="PlayerGrabber"/>.</summary>
        public InputAction Throw { get; private set; }

        private void Awake()
        {
            if (actions == null)
            {
                Debug.LogError($"{nameof(PlayerInputReader)}: no InputActionAsset assigned.", this);
                enabled = false;
                return;
            }

            // Clone so multiple players (Phase 3) never share enabled-state / control schemes.
            actions = Instantiate(actions);
            _playerMap = actions.FindActionMap("Player", throwIfNotFound: true);
            _move = _playerMap.FindAction("Move", throwIfNotFound: true);
            _look = _playerMap.FindAction("Look", throwIfNotFound: true);
            _sprint = _playerMap.FindAction("Sprint", throwIfNotFound: true);
            _jump = _playerMap.FindAction("Jump", throwIfNotFound: true);
            // Grab/throw reuse the template's Interact/Attack actions so they're rebindable and
            // already bound for keyboard+mouse and gamepad. PlayerGrabber edge-reads them.
            Interact = _playerMap.FindAction("Interact", throwIfNotFound: true);
            Throw = _playerMap.FindAction("Attack", throwIfNotFound: true);

            _jump.performed += OnJumpPerformed;
        }

        private void OnEnable() => _playerMap?.Enable();
        private void OnDisable() => _playerMap?.Disable();

        private void OnDestroy()
        {
            if (_jump != null) _jump.performed -= OnJumpPerformed;
        }

        private void OnJumpPerformed(InputAction.CallbackContext _) => _jumpQueued = true;

        /// <summary>Reads the current intent and clears edge-triggered flags.</summary>
        public PlayerInputState Sample()
        {
            var state = new PlayerInputState
            {
                Move = _move.ReadValue<Vector2>(),
                Look = _look.ReadValue<Vector2>(),
                Sprint = _sprint.IsPressed(),
                JumpPressed = _jumpQueued,
            };
            _jumpQueued = false;
            return state;
        }
    }
}
