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
    /// themselves. Amplitudes are kept small so the visual deck never drifts far from the
    /// flat colliders players actually stand on.
    /// </summary>
    public class ShipFloatView : MonoBehaviour
    {
        [Tooltip("Visual subtrees to rock (wired by the builder: hull meshes, anchor station).")]
        [SerializeField] private Transform[] targets = { };
        [Tooltip("Fore/aft distance (m) of the wave sample points from the pivot.")]
        [SerializeField] private float sampleHalfLength = 12f;
        [Tooltip("Port/starboard distance (m) of the wave sample points.")]
        [SerializeField] private float sampleHalfBeam = 2.8f;
        [Tooltip("Visual pitch limit (deg). Keep small: masts amplify it aloft.")]
        [SerializeField] private float maxPitch = 1.0f;
        [Tooltip("Visual roll limit (deg). Keep small: masts amplify it aloft.")]
        [SerializeField] private float maxRoll = 1.5f;
        [Tooltip("Visual heave limit (m).")]
        [SerializeField] private float maxHeave = 0.25f;
        [Tooltip("How quickly the hull settles onto the sampled water plane (bigger = stiffer).")]
        [SerializeField] private float stiffness = 1.5f;

        private struct Rest
        {
            public Transform T;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        private Rest[] _rest = { };
        private WaterSurface _water;
        private float _nextWaterScan;
        private float _heave, _pitch, _roll;
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

            // Fit a plane to four wave samples around the hull; ease the visual pose onto
            // it so short chop reads as gentle motion, not jitter.
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
                heaveT = Mathf.Clamp((hBow + hStern + hPort + hStar) * 0.25f - mean,
                    -maxHeave, maxHeave);
                pitchT = Mathf.Clamp(
                    Mathf.Atan2(hStern - hBow, sampleHalfLength * 2f) * Mathf.Rad2Deg,
                    -maxPitch, maxPitch);
                rollT = Mathf.Clamp(
                    Mathf.Atan2(hStar - hPort, sampleHalfBeam * 2f) * Mathf.Rad2Deg,
                    -maxRoll, maxRoll);
            }

            float k = 1f - Mathf.Exp(-stiffness * Time.deltaTime);
            _heave = Mathf.Lerp(_heave, heaveT, k);
            _pitch = Mathf.Lerp(_pitch, pitchT, k);
            _roll = Mathf.Lerp(_roll, rollT, k);

            // Rock everything about the waterline, so the hull pivots where it floats
            // rather than around the prefab origin.
            float pivotY = _water != null ? _water.SurfaceY - transform.position.y : 0f;
            _rockPivot = new Vector3(0f, pivotY, 0f);
            _rockRot = Quaternion.Euler(_pitch, 0f, _roll);
            foreach (Rest rest in _rest)
            {
                if (rest.T == null) continue;
                rest.T.localRotation = _rockRot * rest.Rot;
                rest.T.localPosition = _rockPivot + _rockRot * (rest.Pos - _rockPivot) + Vector3.up * _heave;
            }
        }

        /// <summary>
        /// World-space motion the current rock applies to a point resting on the flat deck:
        /// a position offset plus a rotation delta. Lets things standing on the ship — a
        /// player's model, loose props — ride exactly the same motion as the hull visuals.
        /// </summary>
        public void GetDeckMotion(Vector3 worldPos, out Vector3 offset, out Quaternion rotation)
        {
            Vector3 local = transform.InverseTransformPoint(worldPos);
            Vector3 rocked = _rockPivot + _rockRot * (local - _rockPivot) + Vector3.up * _heave;
            offset = transform.TransformPoint(rocked) - worldPos;
            rotation = transform.rotation * _rockRot * Quaternion.Inverse(transform.rotation);
        }
    }
}
