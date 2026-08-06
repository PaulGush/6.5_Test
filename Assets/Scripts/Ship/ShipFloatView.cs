using UnityEngine;
using Game.Gameplay;

namespace Game.Ship
{
    /// <summary>
    /// Purely visual buoyancy. The ship's Rigidbody is deliberately pinned to a flat plane
    /// (players stand on it; the network syncs it), so instead of real buoyancy physics
    /// this samples the sea's wave field at bow, stern, port and starboard and rocks the
    /// ship's VISUAL subtrees — hull meshes, anchor station — with the matching heave,
    /// pitch and roll around the waterline. Colliders, movement and sync are untouched;
    /// every peer computes the same motion from its own local sea, like the waves
    /// themselves.
    ///
    /// The hull is modelled as a damped oscillator FORCED by the sampled water plane, so
    /// each passing wave shoulders it and it swings through and rings down — rough patches
    /// of sea visibly work the ship harder than calm ones. Soft limits (not hard clamps)
    /// keep the motion approaching, never exceeding, amplitudes the flat colliders
    /// tolerate, without flattening rough and calm seas into the same rock.
    /// </summary>
    public class ShipFloatView : MonoBehaviour
    {
        [Tooltip("Visual subtrees to rock (wired by the builder: hull meshes, anchor station).")]
        [SerializeField] private Transform[] targets = { };
        [Tooltip("Fore/aft distance (m) of the wave sample points from the pivot.")]
        [SerializeField] private float sampleHalfLength = 12f;
        [Tooltip("Port/starboard distance (m) of the wave sample points.")]
        [SerializeField] private float sampleHalfBeam = 2.8f;
        [Tooltip("Visual pitch soft limit (deg). Keep small: masts amplify it aloft.")]
        [SerializeField] private float maxPitch = 1.0f;
        [Tooltip("Visual roll soft limit (deg). Keep small: masts amplify it aloft.")]
        [SerializeField] private float maxRoll = 1.5f;
        [Tooltip("Visual heave soft limit (m).")]
        [SerializeField] private float maxHeave = 0.25f;
        [Tooltip("Natural frequency (rad/s) of the hull's wave response; near the swell's own frequency the hull rides liveliest.")]
        [SerializeField] private float stiffness = 1.5f;
        [Tooltip("Damping ratio of the response: below 1 the hull keeps ringing briefly after a wave shoulders it.")]
        [SerializeField] private float damping = 0.5f;

        private struct Rest
        {
            public Transform T;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        private Rest[] _rest = { };
        private WaterSurface _water;
        private float _nextWaterScan;
        private float _heave, _pitch, _roll;    // oscillator state (unbounded)
        private float _heaveV, _pitchV, _rollV; // oscillator velocities
        private float _heaveApplied;            // soft-limited heave composed into the pose
        private Vector3 _rockPivot;
        private Quaternion _rockRot = Quaternion.identity;

        /// <summary>Editor-tooling hooks for wiring at build time.</summary>
        public void SetTargets(Transform[] value) => targets = value;
        public void SetExtents(float halfLength, float halfBeam)
        {
            sampleHalfLength = halfLength;
            sampleHalfBeam = halfBeam;
        }

        private void Awake()
        {
            _rest = new Rest[targets.Length];
            for (int i = 0; i < targets.Length; i++)
                if (targets[i] != null)
                    _rest[i] = new Rest
                    {
                        T = targets[i],
                        Pos = targets[i].localPosition,
                        Rot = targets[i].localRotation,
                    };
        }

        private void Update()
        {
            if (_water == null && Time.time >= _nextWaterScan)
            {
                _nextWaterScan = Time.time + 2f;
                _water = FindAnyObjectByType<WaterSurface>();
            }

            // Fit a plane to four wave samples around the hull. Targets are deliberately
            // UNCLAMPED: rough patches of sea must ask for more motion than calm ones, or
            // the sea state can never read through the hull.
            float heaveT = 0f, pitchT = 0f, rollT = 0f;
            if (_water != null)
            {
                Vector3 bow = transform.TransformPoint(0f, 0f, sampleHalfLength);
                Vector3 stern = transform.TransformPoint(0f, 0f, -sampleHalfLength);
                Vector3 port = transform.TransformPoint(-sampleHalfBeam, 0f, 0f);
                Vector3 star = transform.TransformPoint(sampleHalfBeam, 0f, 0f);
                float hBow = _water.HeightAt(bow.x, bow.z);
                float hStern = _water.HeightAt(stern.x, stern.z);
                float hPort = _water.HeightAt(port.x, port.z);
                float hStar = _water.HeightAt(star.x, star.z);

                float mean = _water.SurfaceY;
                heaveT = (hBow + hStern + hPort + hStar) * 0.25f - mean;
                pitchT = Mathf.Atan2(hStern - hBow, sampleHalfLength * 2f) * Mathf.Rad2Deg;
                rollT = Mathf.Atan2(hStar - hPort, sampleHalfBeam * 2f) * Mathf.Rad2Deg;
            }

            // Damped oscillator per axis, forced by the water plane: each wave shoulders
            // the hull, which swings through and rings down instead of gliding onto the
            // surface. Semi-implicit Euler; dt capped so an editor hitch can't blow it up.
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            float w2 = stiffness * stiffness;
            float d = 2f * damping * stiffness;
            _heaveV += ((heaveT - _heave) * w2 - d * _heaveV) * dt;
            _pitchV += ((pitchT - _pitch) * w2 - d * _pitchV) * dt;
            _rollV += ((rollT - _roll) * w2 - d * _rollV) * dt;
            _heave += _heaveV * dt;
            _pitch += _pitchV * dt;
            _roll += _rollV * dt;

            // Rock everything about the waterline, so the hull pivots where it floats
            // rather than around the prefab origin. Soft limits at the output: calm seas
            // use a fraction of the range, heavy seas approach — never exceed — what the
            // flat colliders tolerate.
            float pivotY = _water != null ? _water.SurfaceY - transform.position.y : 0f;
            _rockPivot = new Vector3(0f, pivotY, 0f);
            _rockRot = Quaternion.Euler(SoftLimit(_pitch, maxPitch), 0f, SoftLimit(_roll, maxRoll));
            _heaveApplied = SoftLimit(_heave, maxHeave);
            foreach (Rest rest in _rest)
            {
                if (rest.T == null) continue;
                rest.T.localRotation = _rockRot * rest.Rot;
                rest.T.localPosition = _rockPivot + _rockRot * (rest.Pos - _rockPivot) + Vector3.up * _heaveApplied;
            }
        }

        // Smooth saturation: near-linear well below the limit, asymptotic at it.
        private static float SoftLimit(float x, float max) =>
            max <= 0f ? 0f : max * (float)System.Math.Tanh(x / max);

        /// <summary>
        /// World-space motion the current rock applies to a point resting on the flat deck:
        /// a position offset plus a rotation delta. Lets things standing on the ship — a
        /// player's model, loose props — ride exactly the same motion as the hull visuals.
        /// </summary>
        public void GetDeckMotion(Vector3 worldPos, out Vector3 offset, out Quaternion rotation)
        {
            Vector3 local = transform.InverseTransformPoint(worldPos);
            Vector3 rocked = _rockPivot + _rockRot * (local - _rockPivot) + Vector3.up * _heaveApplied;
            offset = transform.TransformPoint(rocked) - worldPos;
            rotation = transform.rotation * _rockRot * Quaternion.Inverse(transform.rotation);
        }
    }
}
