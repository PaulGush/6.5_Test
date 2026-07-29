using UnityEngine;
using UnityEngine.UI;

namespace Game.Gameplay
{
    /// <summary>
    /// Data-driven, prefab-based HUD that shows context prompts: the Grab binding when the local
    /// player is looking at a nearby prop, and the Drop/Throw bindings while carrying one.
    ///
    /// Each <see cref="Row"/> is configured in the inspector (which state it appears in, which
    /// action's binding it reads, its label, and its UI objects); the binding text is supplied live
    /// by <see cref="GrabPromptChannel"/>, so nothing here hardcodes a key and it follows rebinds and
    /// keyboard-vs-gamepad. No singleton: the view and the player share only the channel asset.
    /// </summary>
    public class GrabPromptView : MonoBehaviour
    {
        private enum Which { Grab, Throw }

        [System.Serializable]
        private class Row
        {
            [Tooltip("Prompt state this row is visible in.")]
            public GrabPromptChannel.State visibleIn = GrabPromptChannel.State.CanGrab;
            [Tooltip("Which action's binding this row displays.")]
            public Which binding = Which.Grab;
            [Tooltip("Action label shown after the binding, e.g. Grab / Drop / Throw.")]
            public string label = "Grab";
            [Tooltip("Show the channel's free-form context line instead of '[binding] label'.")]
            public bool useContextLine;
            [Tooltip("Row root GameObject, toggled on/off with visibility.")]
            public GameObject root;
            [Tooltip("Text filled with '[binding]  label'.")]
            public Text text;
        }

        [Tooltip("Channel the local player raises (assign the GrabPromptChannel asset).")]
        [SerializeField] private GrabPromptChannel channel;
        [SerializeField] private Row[] rows;

        private void OnEnable()
        {
            if (channel != null) channel.Changed += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            if (channel != null) channel.Changed -= Refresh;
        }

        private void Refresh()
        {
            GrabPromptChannel.State state = channel != null ? channel.Current : GrabPromptChannel.State.None;

            foreach (Row row in rows)
            {
                if (row == null) continue;

                bool show = row.visibleIn == state;
                if (row.root != null) row.root.SetActive(show);

                if (show && row.text != null && channel != null)
                {
                    if (row.useContextLine)
                    {
                        row.text.text = channel.ContextLine;
                    }
                    else
                    {
                        string b = row.binding == Which.Grab ? channel.GrabBinding : channel.ThrowBinding;
                        row.text.text = $"[{b}]  {row.label}";
                    }
                }
            }
        }
    }
}
