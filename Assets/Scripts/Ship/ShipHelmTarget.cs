using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// Marker on the wheel prop's collider so an interaction ray can tell "the wheel" apart from
    /// every other collider belonging to the ship. Points back at the <see cref="ShipHelm"/> on
    /// the ship root.
    /// </summary>
    public class ShipHelmTarget : MonoBehaviour
    {
        [SerializeField] private ShipHelm helm;

        public ShipHelm Helm => helm;

        /// <summary>Editor-tooling hook for wiring at build time.</summary>
        public void SetHelm(ShipHelm value) => helm = value;
    }
}
