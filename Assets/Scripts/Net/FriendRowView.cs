using System;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Net
{
    /// <summary>
    /// A data-driven friend row. Given a display name, an in-game flag, and an invite callback,
    /// it fills out its own label and wires its button. Lives on the FriendRow prefab so its look
    /// can be edited in the editor; the controller only supplies data via <see cref="Bind"/>.
    /// </summary>
    public class FriendRowView : MonoBehaviour
    {
        [SerializeField] private Text label;
        [SerializeField] private Button button;

        public void Bind(string displayName, bool inGame, Action onInvite)
        {
            if (label != null)
                label.text = inGame ? displayName + "   • in game" : displayName;

            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                if (onInvite != null)
                    button.onClick.AddListener(() => onInvite());
            }
        }
    }
}
