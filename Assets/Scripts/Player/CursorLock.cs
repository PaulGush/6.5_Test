using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Player
{
    /// <summary>
    /// Captures the mouse cursor during gameplay so mouse-look works, and frees it for
    /// the menu. Rules:
    ///  - Not in a session (no local player) -> cursor free, so the menu is clickable.
    ///  - In a session and the window is focused -> cursor locked + hidden for mouse-look.
    ///  - Escape toggles the cursor free while playing (e.g. to click Leave); Escape again re-locks.
    ///  - Losing window focus frees the cursor; regaining focus re-locks it.
    ///
    /// Uses the new Input System (Keyboard.current) — this project has legacy input disabled.
    /// </summary>
    public class CursorLock : MonoBehaviour
    {
        private bool _menuOpen;
        private bool _wasPlaying;
        private bool _hasFocus = true;

        // Event-driven focus tracking. Application.isFocused is unreliable on some Linux
        // window managers (focus-regain not reported), which left the cursor stuck unlocked
        // after alt-tabbing. The OnApplicationFocus callback plus a click backstop fix that.
        private void OnApplicationFocus(bool hasFocus) => _hasFocus = hasFocus;

        private void Update()
        {
            bool playing = NetworkClient.active && NetworkClient.localPlayer != null;

            // Grab the cursor the moment we enter a session.
            if (playing && !_wasPlaying) _menuOpen = false;
            _wasPlaying = playing;

            var keyboard = Keyboard.current;
            if (playing && keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                _menuOpen = !_menuOpen;

            // Backstop: a click in the game world (not on a UI button) re-asserts focus and
            // recaptures, even if the focus-regain event never arrived from the WM.
            var mouse = Mouse.current;
            if (playing && !_menuOpen && mouse != null && mouse.leftButton.wasPressedThisFrame
                && !IsPointerOverUI())
                _hasFocus = true;

            bool lockCursor = playing && !_menuOpen && _hasFocus;
            Cursor.lockState = lockCursor ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !lockCursor;
        }

        private static bool IsPointerOverUI()
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            return es != null && es.IsPointerOverGameObject();
        }
    }
}
