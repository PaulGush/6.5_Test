using Mirror;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// A door that slides between a closed and an open pose. The open/closed state is a server-set
    /// <see cref="SyncVar"/>; every peer lerps its local transform toward the matching pose, so it
    /// moves smoothly everywhere and late joiners settle into the right state. Driven by one or more
    /// <see cref="PressurePlate"/>s via <see cref="SetOpen"/>.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    public class Gate : NetworkBehaviour
    {
        [Tooltip("Local-space offset added to the closed pose when open (e.g. (0,4,0) to rise into a ceiling).")]
        [SerializeField] private Vector3 openOffset = new Vector3(0f, 4f, 0f);
        [Tooltip("Open/close speed (m/s).")]
        [SerializeField] private float speed = 6f;

        [SyncVar] private bool _open;
        private Vector3 _closedPos;

        private void Awake()
        {
            _closedPos = transform.localPosition;
            var rb = GetComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        [Server]
        public void SetOpen(bool open) => _open = open;

        private void Update()
        {
            Vector3 target = _closedPos + (_open ? openOffset : Vector3.zero);
            transform.localPosition = Vector3.MoveTowards(transform.localPosition, target, speed * Time.deltaTime);
        }
    }
}
