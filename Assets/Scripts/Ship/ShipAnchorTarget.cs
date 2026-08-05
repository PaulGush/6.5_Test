using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// Marker on the bow anchor station's collider so the interaction ray can tell "the
    /// anchor" apart from the rest of the ship — the same pattern as <see cref="ShipSailTarget"/>
    /// and <see cref="DockMooringTarget"/>. Points back at the <see cref="ShipController"/>,
    /// which owns the anchored state; there is no per-station network state.
    /// </summary>
    public class ShipAnchorTarget : MonoBehaviour
    {
        [SerializeField] private ShipController ship;

        public ShipController Ship => ship;

        /// <summary>Editor-tooling hook for wiring at build time.</summary>
        public void SetShip(ShipController value) => ship = value;
    }
}
