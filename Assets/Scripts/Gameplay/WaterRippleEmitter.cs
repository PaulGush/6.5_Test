using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// Spawns water ripple rings (via <see cref="WaterRippleSystem"/>) from a set of
    /// local-space emit points, but only while a point is actually at the waterline —
    /// measured against the animated <see cref="WaterSurface"/>, so a swimmer ripples,
    /// the same player on deck or on the dock does not, and a crate starts rippling the
    /// moment it splashes down. Ring cadence, size and brightness all scale with how
    /// fast the emitter is moving: idle floaters lap gently, a sprinting swimmer or a
    /// ship under sail leaves a churned trail.
    ///
    /// View-only and unsynced: it watches the transform (which Mirror already syncs),
    /// so every peer grows near-identical rings on the shared wave clock.
    /// </summary>
    public class WaterRippleEmitter : MonoBehaviour
    {
        [Tooltip("Local-space points that touch the waterline (hull stations, the swimmer's chest).")]
        [SerializeField] private Vector3[] points = { Vector3.zero };
        [Tooltip("Seconds between rings when holding still.")]
        [SerializeField] private float idleInterval = 2.4f;
        [Tooltip("Seconds between rings at full speed.")]
        [SerializeField] private float movingInterval = 0.5f;
        [Tooltip("Planar speed (m/s) that counts as full speed.")]
        [SerializeField] private float fullRateSpeed = 4f;
        [Tooltip("Ring full-grown radius when idle (m).")]
        [SerializeField] private float idleRadius = 1f;
        [Tooltip("Ring full-grown radius at full speed (m).")]
        [SerializeField] private float movingRadius = 2.2f;
        [Tooltip("Foam brightness when idle (0..1).")]
        [SerializeField] private float idleStrength = 0.35f;
        [Tooltip("Foam brightness at full speed (0..1).")]
        [SerializeField] private float movingStrength = 0.8f;
        [Tooltip("Seconds a ring takes to expand and fade.")]
        [SerializeField] private float ringLife = 2f;
        [Tooltip("A point deeper under the surface than this is submerged, not at the waterline (m).")]
        [SerializeField] private float maxDepth = 1.5f;
        [Tooltip("A point higher above the surface than this is out of the water (m).")]
        [SerializeField] private float maxAbove = 0.5f;

        private WaterSurface _water;
        private float _nextWaterScan;
        private float[] _nextRing;
        private Vector3 _lastPos;
        private bool _hasLastPos;
        private float _speed; // smoothed planar speed (m/s)

        private void Update()
        {
            if (_water == null)
            {
                if (Time.time < _nextWaterScan) return;
                _nextWaterScan = Time.time + 2f;
                _water = FindAnyObjectByType<WaterSurface>();
                if (_water == null) return;
            }

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector3 pos = transform.position;
            if (_hasLastPos)
            {
                Vector3 d = pos - _lastPos;
                d.y = 0f;
                _speed = Mathf.Lerp(_speed, d.magnitude / dt, 1f - Mathf.Exp(-8f * dt));
            }
            _lastPos = pos;
            _hasLastPos = true;

            if (_nextRing == null || _nextRing.Length != points.Length)
            {
                // Timers start desynced, or a hull's four stations would pulse in lockstep.
                _nextRing = new float[points.Length];
                for (int i = 0; i < _nextRing.Length; i++)
                    _nextRing[i] = Time.time + Random.value * idleInterval;
            }

            float speed01 = fullRateSpeed > 0f ? Mathf.Clamp01(_speed / fullRateSpeed) : 0f;
            for (int i = 0; i < points.Length; i++)
            {
                if (Time.time < _nextRing[i]) continue;
                float interval = Mathf.Lerp(idleInterval, movingInterval, speed01);
                _nextRing[i] = Time.time + interval * Random.Range(0.8f, 1.25f);

                Vector3 p = transform.TransformPoint(points[i]);
                float depth = _water.HeightAt(p.x, p.z) - p.y;
                if (depth > maxDepth || depth < -maxAbove) continue;

                Vector2 jitter = Random.insideUnitCircle * 0.25f;
                WaterRippleSystem.Spawn(
                    new Vector3(p.x + jitter.x, p.y, p.z + jitter.y),
                    Mathf.Lerp(idleRadius, movingRadius, speed01) * Random.Range(0.85f, 1.15f),
                    Mathf.Lerp(idleStrength, movingStrength, speed01),
                    ringLife * Random.Range(0.9f, 1.1f));
            }
        }

        // ------------------------------------------------------------------ presets
        // Called by the editor builder when it wires emitters up; values are then baked
        // into the prefab/scene, so tuning in the inspector still sticks afterwards.

        /// <summary>A swimming (or wading) player. The point rides mid-body: underwater
        /// enough while swimming, but clear of the band on a dock or deck.</summary>
        public void ConfigureSwimmer()
        {
            points = new[] { new Vector3(0f, -0.5f, 0f) };
            idleInterval = 1.7f; movingInterval = 0.32f; fullRateSpeed = 4f;
            idleRadius = 0.9f; movingRadius = 2f;
            idleStrength = 0.5f; movingStrength = 0.9f;
            ringLife = 1.8f;
            maxDepth = 2f; maxAbove = 0.3f;
        }

        /// <summary>A ship hull: waterline stations at bow, stern and both beams
        /// (ship local y=0 is the buoyancy waterline plane).</summary>
        public void ConfigureShip(float halfLength, float halfBeam)
        {
            points = new[]
            {
                new Vector3(0f, 0f, halfLength),
                new Vector3(0f, 0f, -halfLength * 0.9f),
                new Vector3(halfBeam, 0f, halfLength * 0.3f),
                new Vector3(-halfBeam, 0f, halfLength * 0.3f),
            };
            idleInterval = 2.6f; movingInterval = 0.5f; fullRateSpeed = 6f;
            idleRadius = 1.6f; movingRadius = 4.5f;
            idleStrength = 0.3f; movingStrength = 0.7f;
            ringLife = 2.6f;
            maxDepth = 2f; maxAbove = 1.5f; // waves + tilt swing the stations; stay generous
        }

        /// <summary>Loose floating cargo: small, gentle, and silent the moment it is
        /// hauled onto a dock or deck.</summary>
        public void ConfigureFloater()
        {
            points = new[] { Vector3.zero };
            idleInterval = 2.8f; movingInterval = 0.9f; fullRateSpeed = 2f;
            idleRadius = 0.8f; movingRadius = 1.4f;
            idleStrength = 0.3f; movingStrength = 0.5f;
            ringLife = 2f;
            maxDepth = 0.9f; maxAbove = 0.5f;
        }

        /// <summary>A shark: quiet while cruising deep, a churned wake when its back
        /// breaks the surface (and a fast one when it chases).</summary>
        public void ConfigureShark()
        {
            points = new[] { Vector3.zero };
            idleInterval = 1.2f; movingInterval = 0.5f; fullRateSpeed = 4f;
            idleRadius = 1.2f; movingRadius = 2.4f;
            idleStrength = 0.4f; movingStrength = 0.7f;
            ringLife = 1.6f;
            maxDepth = 0.6f; maxAbove = 0.3f;
        }
    }
}
