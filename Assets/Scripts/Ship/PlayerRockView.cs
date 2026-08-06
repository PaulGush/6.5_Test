using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// Rocks the player's visual model with the deck of the ship they stand on. The ship's
    /// physics root, colliders and network sync stay flat (<see cref="ShipFloatView"/> rocks
    /// only the hull visuals), so without this the avatar reads as bolted to a gimbal while
    /// the deck heaves under its feet. Purely visual: the CharacterController, camera pivot
    /// and replicated transform never move, and every peer computes the same motion from the
    /// ship's own float view.
    /// </summary>
    public class PlayerRockView : MonoBehaviour
    {
        [Tooltip("Visual model to rock (defaults to the Animator child, like PlayerAvatar).")]
        [SerializeField] private Transform modelRoot;
        [Tooltip("How quickly the rock fades in/out when boarding or going ashore (1/s).")]
        [SerializeField] private float blendRate = 3f;

        private ShipRider _rider;
        private ShipController _lastShip;
        private ShipFloatView _float; // held through the fade-out after going ashore
        private Vector3 _restPos;
        private Quaternion _restRot;
        private float _weight;

        private void Awake()
        {
            _rider = GetComponent<ShipRider>();
            if (modelRoot == null)
            {
                Animator found = GetComponentInChildren<Animator>();
                if (found != null) modelRoot = found.transform;
            }
            if (_rider == null || modelRoot == null)
            {
                enabled = false;
                return;
            }
            _restPos = modelRoot.localPosition;
            _restRot = modelRoot.localRotation;
        }

        // LateUpdate so the pose lands after animation; ordering against PlayerAvatar is
        // irrelevant — that writes the Animator's bones, never modelRoot's own transform.
        private void LateUpdate()
        {
            ShipController ship = _rider.CurrentShip;
            if (ship != _lastShip)
            {
                _lastShip = ship;
                if (ship != null) _float = ship.GetComponent<ShipFloatView>();
            }

            float goal = ship != null && _float != null ? 1f : 0f;
            _weight = Mathf.Lerp(_weight, goal, 1f - Mathf.Exp(-blendRate * Time.deltaTime));

            if (_float == null || _weight < 0.001f)
            {
                _weight = 0f;
                modelRoot.localPosition = _restPos;
                modelRoot.localRotation = _restRot;
                return;
            }

            // The rock at the player's feet; modelRoot pivots at the feet, so composing the
            // rotation there tilts the body about its deck contact, and the offset settles
            // the feet onto the visually rocked planks.
            _float.GetDeckMotion(transform.position, out Vector3 offset, out Quaternion rot);
            if (_weight < 0.999f)
            {
                offset *= _weight;
                rot = Quaternion.Slerp(Quaternion.identity, rot, _weight);
            }
            modelRoot.rotation = rot * transform.rotation * _restRot;
            modelRoot.position = transform.TransformPoint(_restPos) + offset;
        }
    }
}
