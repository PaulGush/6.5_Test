using UnityEngine;
using UnityEngine.UI;

namespace Game.Player
{
    /// <summary>
    /// A data-driven floating name label. Authored as the Nameplate prefab (a world-space Canvas
    /// with a Text); the controller only supplies data via <see cref="SetName"/> and may
    /// <see cref="Hide"/> it. Billboards to face the active camera each frame.
    /// </summary>
    public class NameplateView : MonoBehaviour
    {
        [SerializeField] private Canvas canvas;
        [SerializeField] private Text label;

        [Tooltip("Local offset above the player root where the label sits.")]
        [SerializeField] private Vector3 worldOffset = new Vector3(0f, 2.2f, 0f);

        private Transform _cam;
        private bool _hidden;

        private void OnEnable()
        {
            transform.localPosition = worldOffset;
        }

        public void SetName(string playerName)
        {
            if (label != null)
                label.text = playerName;
        }

        /// <summary>Hide this plate (used for the local player so it doesn't obscure their own view).</summary>
        public void Hide()
        {
            _hidden = true;
            if (canvas != null)
                canvas.enabled = false;
        }

        private void LateUpdate()
        {
            if (_hidden || canvas == null) return;

            if (_cam == null)
            {
                var main = Camera.main;
                if (main == null) return;
                _cam = main.transform;
            }

            // Billboard: align the label with the camera so the text always reads flat.
            transform.rotation = Quaternion.LookRotation(_cam.forward, _cam.up);
        }
    }
}
