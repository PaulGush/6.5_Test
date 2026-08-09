using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// RETIRED as a motion layer: the hull's heave, pitch and roll are fully physical now
    /// (<see cref="ShipController"/>.DriveHeave/DriveTilt move the rigidbody itself), so
    /// nothing rocks the visual subtrees separately — what you see IS the physics, on
    /// every peer via the ship's synced transform. This component survives only as the
    /// ship's wave-sampling footprint (the hull extents the physical tilt fits the wave
    /// plane over), kept on the prefab so the builder wiring and hand-tuned extents stay.
    /// </summary>
    public class ShipFloatView : MonoBehaviour
    {
        [Tooltip("Retired rock targets (kept for prefab compatibility; never moved).")]
        [SerializeField] private Transform[] targets = { };
        [Tooltip("Fore/aft distance (m) of the wave sample points from the pivot.")]
        [SerializeField] private float sampleHalfLength = 12f;
        [Tooltip("Port/starboard distance (m) of the wave sample points.")]
        [SerializeField] private float sampleHalfBeam = 2.8f;

        /// <summary>Wave-sample extents, consumed by ShipController's physical tilt.</summary>
        public float SampleHalfLength => sampleHalfLength;
        public float SampleHalfBeam => sampleHalfBeam;

        /// <summary>Editor-tooling hooks for wiring at build time.</summary>
        public void SetTargets(Transform[] value) => targets = value;
        public void SetExtents(float halfLength, float halfBeam)
        {
            sampleHalfLength = halfLength;
            sampleHalfBeam = halfBeam;
        }
    }
}
