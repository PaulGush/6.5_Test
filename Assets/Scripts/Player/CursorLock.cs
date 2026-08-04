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
        // after alt-tabbing. The OnApplicationFocus callback plus an any-input backstop fix that.
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

            // Backstop: any gameplay input re-asserts focus and recaptures, even if the
            // focus-regain event never arrived from the WM. Keyboard/other buttons count —
            // you are clearly playing if you're pressing WASD — but a click that lands on
            // UI stays a UI click.
            var mouse = Mouse.current;
            if (playing && !_menuOpen)
            {
                bool anyKey = keyboard != null && keyboard.anyKey.wasPressedThisFrame
                    && !keyboard.escapeKey.wasPressedThisFrame;
                bool anyClick = mouse != null
                    && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame
                        || mouse.middleButton.wasPressedThisFrame)
                    && !IsPointerOverUI();
                if (anyKey || anyClick) _hasFocus = true;
            }

            bool lockCursor = playing && !_menuOpen && _hasFocus;
            // Re-assert whenever the actual state drifted from the desired one (the OS can
            // drop the lock without telling us); avoid rewriting it every frame otherwise,
            // which flickers on some Linux compositors.
            CursorLockMode wantMode = lockCursor ? CursorLockMode.Locked : CursorLockMode.None;
            if (Cursor.lockState != wantMode) Cursor.lockState = wantMode;
            if (Cursor.visible == lockCursor) Cursor.visible = !lockCursor;
        }

        private static bool IsPointerOverUI()
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            return es != null && es.IsPointerOverGameObject();
        }
    }
}
