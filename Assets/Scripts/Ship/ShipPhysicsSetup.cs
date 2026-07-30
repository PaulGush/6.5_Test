using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// Player bodies must never impart forces to ships: a CharacterController is a kinematic
    /// capsule, and any kinematic-vs-dynamic contact resolves by shoving the dynamic body — a
    /// player leaning on the rail would push an 8-ton warship. The ship's walkable colliders sit
    /// on kinematic child bodies (immune to that), and the one dynamic collider left (the hull's
    /// world-collision box, layer ShipHull) is excluded from player contacts here.
    /// </summary>
    public static class ShipPhysicsSetup
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Init()
        {
            int player = LayerMask.NameToLayer("PlayerBody");
            int hull = LayerMask.NameToLayer("ShipHull");
            if (player >= 0 && hull >= 0)
                Physics.IgnoreLayerCollision(player, hull, true);
        }
    }
}
