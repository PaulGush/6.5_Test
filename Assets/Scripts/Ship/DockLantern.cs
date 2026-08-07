using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// A jetty's signal lantern, there so sailors can pick the dock out at a distance.
    /// In Auto mode it lights itself when its spot lies in shade (a ray from the lamp
    /// toward the main directional light, decided once at load) — and every Auto lantern
    /// lights at nightfall and snuffs at dawn, following the scene's
    /// <see cref="Game.Gameplay.DayNightCycle"/>. Lit/Unlit force the state for
    /// hand-authored placements. Purely visual and client-local: no networking — the
    /// day/night clock itself is already synced.
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

        private bool _autoShade; // decided once at load: does this spot sit in shade by day?

        private void Start()
        {
            _autoShade = mode == LanternMode.Auto && InShade();
            Refresh();
            // Nightfall is slow; a gentle poll keeps every Auto lantern in step with dusk.
            InvokeRepeating(nameof(Refresh), 4f, 4f);
        }

        private void Refresh()
        {
            var cycle = Game.Gameplay.DayNightCycle.Active;
            bool night = cycle != null && cycle.IsNight;
            Apply(mode == LanternMode.Lit
                  || (mode == LanternMode.Auto && (_autoShade || night)));
        }

        private bool InShade()
        {
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
