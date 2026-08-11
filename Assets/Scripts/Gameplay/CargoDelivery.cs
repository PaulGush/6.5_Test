using Mirror;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// The loot cave's delivery zone: loose cargo set down inside converts to delivered
    /// gold (via <see cref="CargoManager"/>) and vanishes. Server-only sensing on a slow
    /// tick, like PressurePlate — items must be out of hand and at rest, so hurling a
    /// chest into the cave mouth doesn't count until it lands.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public class CargoDelivery : MonoBehaviour
    {
        private BoxCollider _box;
        private CargoManager _manager;
        private float _nextTick;

        private void Awake() => _box = GetComponent<BoxCollider>();

        private void Update()
        {
            if (!NetworkServer.active || Time.time < _nextTick) return;
            _nextTick = Time.time + 0.5f;
            if (_manager == null)
            {
                _manager = FindAnyObjectByType<CargoManager>();
                if (_manager == null) return;
            }

            foreach (var item in FindObjectsByType<CargoItem>(FindObjectsSortMode.None))
            {
                if (item.Grabbable.IsHeld || item.IsStowed) continue;
                Vector3 local = _box.transform.InverseTransformPoint(item.transform.position) - _box.center;
                Vector3 half = _box.size * 0.5f;
                if (Mathf.Abs(local.x) > half.x || Mathf.Abs(local.y) > half.y || Mathf.Abs(local.z) > half.z)
                    continue;
                var body = item.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic && body.linearVelocity.sqrMagnitude > 0.36f)
                    continue;

                _manager.ServerDeliver(item.Value);
                NetworkServer.Destroy(item.gameObject);
            }
        }
    }
}
