using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// A jetty's signal lantern, there so sailors can pick the dock out at a distance.
    /// In Auto mode it decides once at load whether this spot lies in shade — a ray from
    /// the lamp toward the main directional light — so lanterns light themselves in the
    /// dark parts of the map and stay unlit in open sun. Lit/Unlit force the state for
    /// hand-authored placements. Purely visual and client-local: no networking.
    /// </summary>
    public class DockLantern : MonoBehaviour
    {
        public enum LanternMode { Auto, Lit, Unlit }

        [Tooltip("Auto: lit only when the lamp sits in shadow of the main directional light.")]
        [SerializeField] private LanternMode mode = LanternMode.Auto;
        [Tooltip("The lamp's point light, enabled while lit.")]
        [SerializeField] private Light lamp;
        [Tooltip("Optional emissive glow mesh toggled with the lamp (the visible flame).")]
        [SerializeField] private Renderer glow;

        /// <summary>Editor-tooling hook for wiring at build time.</summary>
        public void SetRefs(Light lampRef, Renderer glowRef)
        {
            lamp = lampRef;
            glow = glowRef;
        }

        private void Start() => Apply(ShouldBeLit());

        private bool ShouldBeLit()
        {
            if (mode == LanternMode.Lit) return true;
            if (mode == LanternMode.Unlit) return false;

            Light sun = FindSun();
            if (sun == null) return true; // no sun at all: a dark map, light up

            // In shade if anything solid blocks the path from just above the lamp to the sun.
            Vector3 from = (lamp != null ? lamp.transform.position : transform.position)
                           + Vector3.up * 0.3f;
            return Physics.Raycast(from, -sun.transform.forward, 500f, ~0,
                QueryTriggerInteraction.Ignore);
        }

        private static Light FindSun()
        {
            foreach (Light l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && l.enabled) return l;
            return null;
        }

        private void Apply(bool lit)
        {
            if (lamp != null) lamp.enabled = lit;
            if (glow != null) glow.enabled = lit;
        }
    }
}
