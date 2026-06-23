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
    /// It is a scene singleton because there is exactly one local view per client:
    /// when the local player spawns, <see cref="NetworkPlayer"/> calls <see cref="Bind"/>
    /// to point both cameras at that player's pivot. Remote players never touch it.
    ///
    /// Camera choice is cosmetic and client-only, so it is intentionally kept out of
    /// the networked <see cref="PlayerInputState"/>.
    /// </summary>
    public class CameraRig : MonoBehaviour
    {
        public static CameraRig Instance { get; private set; }

        [SerializeField] private CinemachineCamera firstPerson;
        [SerializeField] private CinemachineCamera thirdPerson;
        [SerializeField] private bool startInFirstPerson = true;

        private const int ActivePriority = 20;
        private const int InactivePriority = 10;

        private InputAction _toggle;
        private bool _isFirstPerson;

        private void Awake()
        {
            Instance = this;

            // Self-contained binding so we don't have to touch the shared input asset
            // for a cosmetic, client-only control.
            _toggle = new InputAction("ToggleCamera", InputActionType.Button);
            _toggle.AddBinding("<Keyboard>/v");
            _toggle.AddBinding("<Gamepad>/buttonNorth");
            ApplyMode(startInFirstPerson);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>Point both cameras at the local player's pivot. Called on spawn of the local player.</summary>
        public void Bind(Transform followTarget)
        {
            if (firstPerson != null) firstPerson.Follow = followTarget;
            if (thirdPerson != null) thirdPerson.Follow = followTarget;
        }

        private void OnEnable()
        {
            _toggle.performed += OnToggle;
            _toggle.Enable();
        }

        private void OnDisable()
        {
            _toggle.performed -= OnToggle;
            _toggle.Disable();
        }

        private void OnToggle(InputAction.CallbackContext _) => ApplyMode(!_isFirstPerson);

        private void ApplyMode(bool firstPersonMode)
        {
            _isFirstPerson = firstPersonMode;
            if (firstPerson != null) firstPerson.Priority = firstPersonMode ? ActivePriority : InactivePriority;
            if (thirdPerson != null) thirdPerson.Priority = firstPersonMode ? InactivePriority : ActivePriority;
        }
    }
}
