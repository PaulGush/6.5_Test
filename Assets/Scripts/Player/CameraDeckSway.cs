using Unity.Cinemachine;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// RETIRED: deck sway is fully physical now — the ship rigidbody itself heaves and
    /// tilts (<see cref="Game.Ship.ShipController"/>), the player is parented to it, and
    /// the camera pivot rides the real deck, so the horizon motion comes from physics
    /// with nothing to add. The extension remains only so camera rigs still carrying it
    /// keep loading; it applies no correction.
    /// </summary>
    public class CameraDeckSway : CinemachineExtension
    {
        protected override void PostPipelineStageCallback(CinemachineVirtualCameraBase vcam,
            CinemachineCore.Stage stage, ref CameraState state, float deltaTime)
        {
        }
    }
}
