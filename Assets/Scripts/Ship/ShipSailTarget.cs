using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// Marker on the interaction collider at a mast's base so the look-ray can tell "this mast's
    /// sail station" apart from the rest of the ship. Points back at the
    /// <see cref="ShipSailStation"/> on the ship root. Mirrors <see cref="ShipHelmTarget"/>.
    /// </summary>
    public class ShipSailTarget : MonoBehaviour
    {
        [SerializeField] private ShipSailStation station;

        public ShipSailStation Station => station;

        /// <summary>Editor-tooling hook for wiring at build time.</summary>
        public void SetStation(ShipSailStation value) => station = value;
    }
}
