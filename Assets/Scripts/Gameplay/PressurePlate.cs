using Mirror;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// A weight-sensing plate for co-op puzzles: it counts as pressed while a player stands on it OR a
    /// (non-held) <see cref="Grabbable"/> prop rests on it. The server senses the weight and drives the
    /// linked <see cref="Gate"/>(s) open while pressed; the pressed state is synced so clients can show
    /// the indicator. Two-player friendly — one player can weigh it down (or park a crate on it) while
    /// the other passes through the gate.
    /// </summary>
    public class PressurePlate : NetworkBehaviour
    {
        [Tooltip("Gates opened while this plate is pressed.")]
        [SerializeField] private Gate[] gates;
        [Tooltip("World half-extents of the weight-sensor box above the plate.")]
        [SerializeField] private Vector3 sensorHalfExtents = new Vector3(1f, 0.6f, 1f);
        [Tooltip("Height above the plate's origin at which the sensor box is centred (m).")]
        [SerializeField] private float sensorHeight = 0.6f;
        [Tooltip("Optional indicator whose colour flips when pressed (green) / released (red).")]
        [SerializeField] private Renderer indicator;

        [SyncVar(hook = nameof(OnPressedChanged))] private bool _pressed;
        public bool IsPressed => _pressed;

        public override void OnStartClient() => ApplyIndicator(_pressed);

        private void FixedUpdate()
        {
            if (!isServer) return;

            bool pressed = Sense();
            if (pressed == _pressed) return;

            _pressed = pressed;
            if (gates != null)
                foreach (Gate g in gates)
                    if (g != null) g.SetOpen(pressed);
        }

        // Server: is anything heavy resting in the sensor box?
        private bool Sense()
        {
            Vector3 center = transform.position + Vector3.up * sensorHeight;
            Collider[] hits = Physics.OverlapBox(center, sensorHalfExtents, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);

            foreach (Collider h in hits)
            {
                if (h.transform.IsChildOf(transform)) continue;                 // ignore the plate itself
                if (h.GetComponentInParent<CharacterController>() != null) return true; // a player
                Grabbable g = h.GetComponentInParent<Grabbable>();
                if (g != null && !g.IsHeld) return true;                        // a dropped prop
            }
            return false;
        }

        private void OnPressedChanged(bool _, bool pressed) => ApplyIndicator(pressed);

        private void ApplyIndicator(bool pressed)
        {
            if (indicator != null) indicator.material.color = pressed ? Color.green : new Color(0.8f, 0.2f, 0.2f);
        }
    }
}
