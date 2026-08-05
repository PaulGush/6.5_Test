using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// Marks the scene's water plane so gameplay visuals (the ship's anchor, for one) can
    /// ask where the surface is at runtime instead of baking heights in. Lives on the
    /// water plane object; the surface is this transform's world Y.
    /// </summary>
    public class WaterSurface : MonoBehaviour
    {
        public float SurfaceY => transform.position.y;
    }
}
