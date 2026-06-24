using System;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// Event channel the local player raises to drive the on-screen grab/drop/throw prompts.
    /// Carries the current prompt <see cref="State"/> plus the live binding display strings, so the
    /// HUD shows the player's actual bindings (honouring rebinds and keyboard-vs-gamepad) instead of
    /// hardcoded keys. Inverts the dependency like <c>LocalPlayerReadyChannel</c>: the runtime-spawned
    /// player and the scene HUD share only this asset, so neither needs a reference to the other.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Grab Prompt Channel", fileName = "GrabPromptChannel")]
    public class GrabPromptChannel : ScriptableObject
    {
        public enum State { None, CanGrab, Holding }

        public event Action Changed;

        public State Current { get; private set; }
        public string GrabBinding { get; private set; } = "";
        public string ThrowBinding { get; private set; } = "";

        /// <summary>Local player pushes its current prompt state + bindings. Raises only on a real change.</summary>
        public void Set(State state, string grabBinding, string throwBinding)
        {
            grabBinding ??= "";
            throwBinding ??= "";
            if (state == Current && grabBinding == GrabBinding && throwBinding == ThrowBinding) return;

            Current = state;
            GrabBinding = grabBinding;
            ThrowBinding = throwBinding;
            Changed?.Invoke();
        }

        private void OnEnable()
        {
            // SO state survives between play-mode sessions in the editor; start clean each load.
            Current = State.None;
            GrabBinding = "";
            ThrowBinding = "";
        }
    }
}
