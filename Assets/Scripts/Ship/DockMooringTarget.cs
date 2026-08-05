using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// Marker on a jetty bollard's collider so the interaction ray can tell "the mooring
    /// point" apart from the rest of the dock. Points back at the <see cref="DockMooring"/>
    /// on the jetty root.
    /// </summary>
    public class DockMooringTarget : MonoBehaviour
    {
        [SerializeField] private DockMooring mooring;

        public DockMooring Mooring => mooring;

        /// <summary>Editor-tooling hook for wiring at build time.</summary>
        public void SetMooring(DockMooring value) => mooring = value;
    }
}
