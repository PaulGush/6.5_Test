using System;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// Event channel the local player raises to drive the on-screen interaction prompts
    /// (grab/drop/throw, and the ship helm's take/steer/let-go). Carries the current prompt
    /// <see cref="State"/> plus the live binding display strings, so the HUD shows the player's
    /// actual bindings (honouring rebinds and keyboard-vs-gamepad) instead of hardcoded keys.
    /// A free-form <see cref="ContextLine"/> covers prompts that aren't a single binding (the
    /// steering controls + live sail level). Inverts the dependency like
    /// <c>LocalPlayerReadyChannel</c>: the runtime-spawned player and the scene HUD share only
    /// this asset, so neither needs a reference to the other.
    /// </summary>
    [CreateAssetMenu(menuName = "Game/Grab Prompt Channel", fileName = "GrabPromptChannel")]
    public class GrabPromptChannel : ScriptableObject
    {
        // Only append here: the values are serialized in the HUD prefab's row config.
        public enum State { None, CanGrab, Holding, CanSteer, Steering, CanUseStation }

        public event Action Changed;

        public State Current { get; private set; }
        public string GrabBinding { get; private set; } = "";
        public string ThrowBinding { get; private set; } = "";
        /// <summary>Free-form extra prompt line (e.g. steering controls); "" when unused.</summary>
        public string ContextLine { get; private set; } = "";

        /// <summary>Local player pushes its current prompt state + bindings. Raises only on a real change.</summary>
        public void Set(State state, string grabBinding, string throwBinding, string contextLine = "")
        {
            grabBinding ??= "";
            throwBinding ??= "";
            contextLine ??= "";
            if (state == Current && grabBinding == GrabBinding && throwBinding == ThrowBinding
                && contextLine == ContextLine) return;

            Current = state;
            GrabBinding = grabBinding;
            ThrowBinding = throwBinding;
            ContextLine = contextLine;
            Changed?.Invoke();
        }

        private void OnEnable()
        {
            // SO state survives between play-mode sessions in the editor; start clean each load.
            Current = State.None;
            GrabBinding = "";
            ThrowBinding = "";
            ContextLine = "";
        }
    }
}
