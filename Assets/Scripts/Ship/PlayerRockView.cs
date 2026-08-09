using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// RETIRED: the deck's motion is fully physical now (<see cref="ShipController"/>
    /// drives the hull rigidbody's heave and tilt), and players parented to the ship ride
    /// the real deck — there is no separate visual rock left to copy onto the avatar.
    /// The class remains only so prefabs still carrying it keep loading; it disables
    /// itself on wake.
    /// </summary>
    public class PlayerRockView : MonoBehaviour
    {
        private void Awake() => enabled = false;
    }
}
