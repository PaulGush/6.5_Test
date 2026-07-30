using Mirror;
using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// One mast's sails as a crew station. A player at the mast toggles this mast between furled
    /// and unfurled (swapping the Synty furled/set sail meshes); the ship's speed comes from how
    /// many masts are currently unfurled (see <see cref="ShipController.SailLevel"/>), so setting
    /// canvas is genuinely a per-mast job, not a helm setting.
    ///
    /// Lives on the ship's root object (Mirror requires NetworkBehaviours beside the
    /// NetworkIdentity), one instance per mast; the interaction collider at the mast base is
    /// marked with a <see cref="ShipSailTarget"/> pointing back here.
    /// </summary>
    public class ShipSailStation : NetworkBehaviour
    {
        [Tooltip("Visual roots shown while this mast's sails are furled.")]
        [SerializeField] private GameObject[] furledVisuals = { };
        [Tooltip("Visual roots shown while this mast's sails are set.")]
        [SerializeField] private GameObject[] setVisuals = { };

        [SyncVar(hook = nameof(OnUnfurledChanged))] private bool _unfurled;

        public bool Unfurled => _unfurled;

        private void Awake() => Apply(_unfurled);

        public override void OnStartClient() => Apply(_unfurled);

        [Server]
        public void Toggle()
        {
            _unfurled = !_unfurled;
            Apply(_unfurled); // hooks cover clients; make sure the server/host applies too
        }

        private void OnUnfurledChanged(bool _, bool unfurled) => Apply(unfurled);

        private void Apply(bool unfurled)
        {
            if (furledVisuals != null)
                foreach (GameObject go in furledVisuals)
                    if (go != null) go.SetActive(!unfurled);
            if (setVisuals != null)
                foreach (GameObject go in setVisuals)
                    if (go != null) go.SetActive(unfurled);
        }
    }
}
