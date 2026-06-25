using Mirror;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// A platform that shuttles smoothly back and forth on a fixed path. Its position is a pure
    /// function of the synced <see cref="NetworkTime"/>, so every peer (host + clients) computes the
    /// exact same pose with no per-object network sync needed. The server additionally carries any
    /// players standing on top by moving their CharacterControllers with the platform each step
    /// (CharacterControllers don't ride moving colliders on their own); that motion replicates to
    /// clients via the player's own NetworkTransform.
    ///
    /// Plain MonoBehaviour (no NetworkIdentity): it runs identically everywhere off NetworkTime.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    public class MovingPlatform : MonoBehaviour
    {
        [Tooltip("Travel offset from the start position to the far end (world space).")]
        [SerializeField] private Vector3 travel = new Vector3(0f, 0f, 6f);
        [Tooltip("Seconds for one full there-and-back cycle.")]
        [SerializeField] private float period = 4f;
        [Tooltip("Phase offset 0..1 to stagger multiple platforms on the same clock.")]
        [SerializeField] private float phase = 0f;
        [Tooltip("Height of the slab above the platform used to detect riders.")]
        [SerializeField] private float riderProbe = 0.4f;

        private Rigidbody _rb;
        private Collider _col;
        private Vector3 _start;
        private Vector3 _prev;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _col = GetComponent<Collider>();
            _rb.isKinematic = true;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _start = transform.position;
            _prev = _start;
        }

        private void FixedUpdate()
        {
            // Smooth ease-in/out ping-pong in [0,1] from the shared network clock.
            double cycles = (NetworkTime.time / Mathf.Max(0.01f, period)) + phase;
            float u = 0.5f - 0.5f * Mathf.Cos((float)(cycles * System.Math.PI * 2.0));
            Vector3 pos = _start + travel * u;
            Vector3 delta = pos - _prev;

            _rb.MovePosition(pos);

            if (NetworkServer.active && delta.sqrMagnitude > 1e-10f)
                CarryRiders(delta);

            _prev = pos;
        }

        // Server only: move every CharacterController resting on top by the platform's frame delta.
        private void CarryRiders(Vector3 delta)
        {
            Bounds b = _col.bounds;
            Vector3 center = new Vector3(b.center.x, b.max.y + riderProbe * 0.5f, b.center.z);
            Vector3 half = new Vector3(b.extents.x, riderProbe * 0.5f, b.extents.z);

            Collider[] hits = Physics.OverlapBox(center, half, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            foreach (Collider h in hits)
            {
                CharacterController cc = h.GetComponent<CharacterController>();
                if (cc != null) cc.Move(delta);
            }
        }
    }
}
