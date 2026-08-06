using Unity.Cinemachine;
using UnityEngine;
using Game.Ship;

namespace Game.Player
{
    /// <summary>
    /// Subtle first-person deck sway. Post-processes the first-person camera with a
    /// scaled-down copy of the ship's visual rock (<see cref="ShipFloatView"/>), so the
    /// horizon tilts and heaves gently while you stand on a live deck. Applied as a
    /// Cinemachine correction on the CAMERA only — the aim pivot transform is never
    /// touched, so aim raycasts, helm targeting and the replicated look direction all
    /// keep reading the flat, physical deck.
    /// </summary>
    public class CameraDeckSway : CinemachineExtension
    {
        [Tooltip("Fraction of the deck's visual heave/offset applied to the camera.")]
        [SerializeField] private float positionWeight = 0.6f;
        [Tooltip("Fraction of the deck's visual tilt applied to the camera. Keep low: the whole horizon amplifies it.")]
        [SerializeField] private float rotationWeight = 0.35f;
        [Tooltip("How quickly the sway fades in/out when boarding or going ashore (1/s).")]
        [SerializeField] private float blendRate = 3f;

        private Transform _follow;
        private ShipRider _rider;
        private ShipController _lastShip;
        private ShipFloatView _float; // held through the fade-out after going ashore
        private float _weight;

        protected override void PostPipelineStageCallback(CinemachineVirtualCameraBase vcam,
            CinemachineCore.Stage stage, ref CameraState state, float deltaTime)
        {
            if (stage != CinemachineCore.Stage.Finalize) return;

            // The follow target is the local player's camera pivot; the rider lives on
            // the player root above it. Re-resolve only when the target changes (respawn).
            Transform follow = vcam.Follow;
            if (follow != _follow)
            {
                _follow = follow;
                _rider = follow != null ? follow.GetComponentInParent<ShipRider>() : null;
                _lastShip = null;
                _float = null;
                _weight = 0f;
            }

            ShipController ship = _rider != null ? _rider.CurrentShip : null;
            if (ship != _lastShip)
            {
                _lastShip = ship;
                if (ship != null) _float = ship.GetComponent<ShipFloatView>();
            }

            float goal = ship != null && _float != null ? 1f : 0f;
            _weight = deltaTime < 0f // negative on cuts: snap instead of easing across one
                ? goal
                : Mathf.Lerp(_weight, goal, 1f - Mathf.Exp(-blendRate * deltaTime));
            if (_float == null || _weight < 0.001f)
            {
                _weight = 0f;
                return;
            }

            _float.GetDeckMotion(_rider.transform.position, out Vector3 offset, out Quaternion rot);
            state.PositionCorrection += offset * (positionWeight * _weight);

            // GetDeckMotion's rotation is a world-space delta; corrections compose on the
            // right of RawOrientation, so conjugate it into the camera's local frame.
            Quaternion sway = Quaternion.Slerp(Quaternion.identity, rot, rotationWeight * _weight);
            Quaternion raw = state.RawOrientation;
            state.OrientationCorrection =
                Quaternion.Inverse(raw) * sway * raw * state.OrientationCorrection;
        }
    }
}
