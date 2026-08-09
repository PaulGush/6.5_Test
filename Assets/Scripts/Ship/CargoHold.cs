using Mirror;
using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// The ship's stowage zone: a coamed patch of deck where the crew piles cargo.
    /// Nothing is lashed — every crate stays a LIVE dynamic body that rides the deck
    /// (<see cref="Game.Gameplay.CargoItem"/>'s deck-carry) and genuinely slides when
    /// the ship heels, so keeping the pile aboard through weather IS the cargo-safety
    /// job; the low coaming (builder patch) retains it in mild seas and spills it in
    /// hard rolls. This behaviour just COUNTS what's riding inside the volume, synced
    /// for the HUD. Lives on the ship's root (Mirror only syncs behaviours on the
    /// identity object); the volume is a child trigger box wired by the builder patch.
    /// (CargoItem's lash machinery survives for a future manual lash-down job.)
    /// </summary>
    public class CargoHold : NetworkBehaviour
    {
        [Tooltip("Trigger box marking the stowage area on deck (wired by the builder).")]
        [SerializeField] private BoxCollider volume;
        [Tooltip("Cargo must be moving slower than this to lash down (m/s).")]
        [SerializeField] private float restSpeed = 0.6f;
        [Tooltip("The glowing stow-zone frame shows only while the local player carries cargo within this range of the hold (m).")]
        [SerializeField] private float frameVisibleRange = 30f;

        // "Stowed" = riding loose inside the hold volume; nothing is physically lashed.
        [SyncVar] private int stowedCount;
        [SyncVar] private int stowedValue;

        public int StowedCount => stowedCount;
        public int StowedValue => stowedValue;

        /// <summary>Editor-tooling hook for wiring at patch time.</summary>
        public void SetVolume(BoxCollider value) => volume = value;

        private float _nextTick;
        private Transform _frame;
        private bool _frameSearched;

        private void Update()
        {
            UpdateFrameVisibility();
            if (NetworkServer.active) ServerTick();
        }

        // The stow-zone frame is guidance, not decoration: visible only to a player who
        // is actually carrying cargo near the hold, so the deck stays clean otherwise.
        private void UpdateFrameVisibility()
        {
            if (!_frameSearched)
            {
                _frameSearched = true;
                _frame = FindDeepChild(transform, "CargoHoldFrame_v2")
                      ?? FindDeepChild(transform, "CargoHoldFrame");
            }
            if (_frame == null || volume == null) return;

            bool show = false;
            var local = NetworkClient.localPlayer;
            if (local != null)
            {
                var grabber = local.GetComponent<Game.Gameplay.PlayerGrabber>();
                var held = grabber != null ? grabber.Held : null;
                if (held != null && held.GetComponent<Game.Gameplay.CargoItem>() != null)
                {
                    Vector3 holdCenter = volume.transform.TransformPoint(volume.center);
                    show = (local.transform.position - holdCenter).sqrMagnitude
                        < frameVisibleRange * frameVisibleRange;
                }
            }
            if (_frame.gameObject.activeSelf != show)
                _frame.gameObject.SetActive(show);
        }

        private static Transform FindDeepChild(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        private void ServerTick()
        {
            if (volume == null || Time.time < _nextTick) return;
            _nextTick = Time.time + 0.4f;

            int count = 0, total = 0;
            foreach (var item in FindObjectsByType<Game.Gameplay.CargoItem>(FindObjectsSortMode.None))
            {
                // Migration from the auto-lash era: everything rides loose now.
                if (item.IsStowed) item.ServerSetStowed(null);

                if (item.Grabbable.IsHeld) continue;
                if (!InsideVolume(item.transform.position)) continue;
                if (!Supported(item)) continue; // sailing over the zone in a throw doesn't count
                count++; total += item.Value;
            }
            stowedCount = count;
            stowedValue = total;
        }

        private bool InsideVolume(Vector3 worldPos)
        {
            Vector3 local = volume.transform.InverseTransformPoint(worldPos) - volume.center;
            Vector3 half = volume.size * 0.5f;
            // Slack so cargo resting ON the volume's floor edge still counts — extra on Y,
            // so a crate bouncing over a wave crest or sitting atop the pile doesn't
            // flicker out of the count.
            return Mathf.Abs(local.x) <= half.x + 0.35f
                && Mathf.Abs(local.y) <= half.y + 0.6f
                && Mathf.Abs(local.z) <= half.z + 0.35f;
        }

        private static bool Supported(Game.Gameplay.CargoItem item)
        {
            Collider col = item.Grabbable.Col;
            Bounds b = col != null ? col.bounds
                : new Bounds(item.transform.position, Vector3.one * 0.5f);
            // Reach generously below: a lashed item rocked upward with the visual deck
            // still counts as sitting on it.
            return Physics.Raycast(b.center, Vector3.down, b.extents.y + 0.6f,
                ~0, QueryTriggerInteraction.Ignore);
        }
    }
}
