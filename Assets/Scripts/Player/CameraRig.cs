using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Player
{
    /// <summary>
    /// Scene-level camera rig (one per client). Owns the first- and third-person
    /// <see cref="CinemachineCamera"/>s and the single CinemachineBrain camera, and
    /// toggles between them by swapping priority.
    ///
    /// There is exactly one local view per client. When the local player spawns it raises
    /// <see cref="LocalPlayerReadyChannel"/> with its pivot, and this rig (subscribed to the same
    /// channel) points both cameras at it. The player needs no reference to this rig.
    ///
    /// Camera choice is cosmetic and client-only, so it is intentionally kept out of
    /// the networked <see cref="PlayerInputState"/>.
    /// </summary>
    public class CameraRig : MonoBehaviour
    {
        [SerializeField] private CinemachineCamera firstPerson;
        [SerializeField] private CinemachineCamera thirdPerson;
        [SerializeField] private bool startInFirstPerson = true;

        [Tooltip("Channel the local player raises on spawn (assign the LocalPlayerReadyChannel asset).")]
        [SerializeField] private LocalPlayerReadyChannel localPlayerReady;

        private const int ActivePriority = 20;
        private const int InactivePriority = 10;

        /// <summary>Current view mode of this client's rig. Static because there is exactly one
        /// local view per client; consumed by <see cref="PlayerAvatar"/> to hide the local model.</summary>
        public static bool IsFirstPerson { get; private set; } = true;

        /// <summary>Raised when the local view toggles between first and third person.</summary>
        public static event System.Action<bool> FirstPersonChanged;

        private InputAction _toggle;
        private bool _isFirstPerson;

        private void Awake()
        {
            // Self-contained binding so we don't have to touch the shared input asset
            // for a cosmetic, client-only control.
            _toggle = new InputAction("ToggleCamera", InputActionType.Button);
            _toggle.AddBinding("<Keyboard>/v");
            _toggle.AddBinding("<Gamepad>/buttonNorth");
            ApplyMode(startInFirstPerson);
        }

        /// <summary>Point both cameras at the local player's pivot. Invoked via the ready channel.</summary>
        public void Bind(Transform followTarget)
        {
            if (firstPerson != null) firstPerson.Follow = followTarget;
            if (thirdPerson != null) thirdPerson.Follow = followTarget;
        }

        private void OnEnable()
        {
            if (localPlayerReady != null) localPlayerReady.Raised += Bind;
            _toggle.performed += OnToggle;
            _toggle.Enable();
        }

        private void OnDisable()
        {
            if (localPlayerReady != null) localPlayerReady.Raised -= Bind;
            _toggle.performed -= OnToggle;
            _toggle.Disable();
        }

        private void OnToggle(InputAction.CallbackContext _) => ApplyMode(!_isFirstPerson);

        private void ApplyMode(bool firstPersonMode)
        {
            _isFirstPerson = firstPersonMode;
            if (firstPerson != null) firstPerson.Priority = firstPersonMode ? ActivePriority : InactivePriority;
            if (thirdPerson != null) thirdPerson.Priority = firstPersonMode ? InactivePriority : ActivePriority;
            IsFirstPerson = firstPersonMode;
            FirstPersonChanged?.Invoke(firstPersonMode);
        }
    }
}
