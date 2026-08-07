using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// Keeps the sea mesh centred on the viewer. The wave shader evaluates world-space
    /// positions, so sliding the grid under the camera is invisible — and it means the
    /// finely tessellated inner disc is always where the player is looking, letting the
    /// playable ocean be kilometres wide without a kilometre-wide dense mesh. Snapped to
    /// the grid step so the triangulation doesn't crawl as the camera pans. Purely
    /// visual: <see cref="WaterSurface"/> queries are analytic and ignore the mesh.
    /// </summary>
    public class SeaFollowView : MonoBehaviour
    {
        [Tooltip("Snap step (m) — keep equal to the sea grid's dense ring spacing.")]
        [SerializeField] private float snapStep = 2.2f;

        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 c = cam.transform.position;
            transform.position = new Vector3(
                Mathf.Round(c.x / snapStep) * snapStep,
                transform.position.y,
                Mathf.Round(c.z / snapStep) * snapStep);
        }
    }
}
