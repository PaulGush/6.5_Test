using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Gameplay
{
    /// <summary>
    /// Network-synced day/night. Time of day derives from the same shared clock the waves
    /// ride (<see cref="WaterSurface.WaveTime"/>, Mirror's network time), so every peer
    /// sees the same sun — and the cycle needs no traffic of its own.
    ///
    /// One directional light plays both bodies: it sweeps the sky as the sun by day and,
    /// once the sun has fully faded below the horizon, swaps to the opposite arc as a dim
    /// blue moon — one shadow caster, always above the horizon. Ambient tri-light follows
    /// with a lag into dusk; the built-in procedural skybox reads the light's direction by
    /// itself, so sunsets and a bright moon disc come for free. Lanterns and other
    /// nightfall reactions poll <see cref="IsNight"/> via <see cref="Active"/>.
    /// </summary>
    public class DayNightCycle : MonoBehaviour
    {
        [Tooltip("Real seconds for one full in-game day.")]
        [SerializeField] private float dayLengthSeconds = 600f;
        [Tooltip("Time of day at network-time zero (0 = midnight, 0.5 = noon).")]
        [SerializeField, Range(0f, 1f)] private float startTimeOfDay = 0.4f;
        [Tooltip("Yaw of the orbit plane — sets the character of the shadows.")]
        [SerializeField] private float orbitYaw = 35f;

        [Header("Sun")]
        [SerializeField] private float sunIntensity = 2f;
        [SerializeField] private Color sunColor = new Color(1f, 0.96f, 0.9f);
        [SerializeField] private Color sunHorizonColor = new Color(1f, 0.55f, 0.3f);

        [Header("Moon")]
        [SerializeField] private float moonIntensity = 0.35f;
        [SerializeField] private Color moonColor = new Color(0.56f, 0.66f, 0.9f);

        [Header("Ambient (tri-light)")]
        [SerializeField] private Color dayAmbientSky = new Color(0.55f, 0.64f, 0.75f);
        [SerializeField] private Color dayAmbientEquator = new Color(0.46f, 0.50f, 0.52f);
        [SerializeField] private Color dayAmbientGround = new Color(0.28f, 0.26f, 0.22f);
        [SerializeField] private Color nightAmbientSky = new Color(0.07f, 0.09f, 0.15f);
        [SerializeField] private Color nightAmbientEquator = new Color(0.05f, 0.06f, 0.10f);
        [SerializeField] private Color nightAmbientGround = new Color(0.02f, 0.03f, 0.05f);

        private Light _light;
        private Material _sky; // runtime instance, so driving it never dirties the asset
        private static readonly int NightId = Shader.PropertyToID("_Night");
        private static readonly int SunDirId = Shader.PropertyToID("_SunDir");
        private static readonly int MoonDirId = Shader.PropertyToID("_MoonDir");

        /// <summary>The scene's active cycle, for anything that reacts to nightfall.</summary>
        public static DayNightCycle Active { get; private set; }

        /// <summary>0 = midnight, 0.25 = sunrise, 0.5 = noon, 0.75 = sunset. Same on every peer.</summary>
        public float TimeOfDay =>
            Mathf.Repeat(startTimeOfDay + WaterSurface.WaveTime / Mathf.Max(60f, dayLengthSeconds), 1f);

        /// <summary>True from just before sunset glow dies to just after dawn breaks.</summary>
        public bool IsNight => SunElevation(TimeOfDay) <= 0.02f;

        // Sine elevation of the sun: 1 at noon, 0 at sunrise/sunset, -1 at midnight.
        private static float SunElevation(float tod) => Mathf.Sin((tod - 0.25f) * Mathf.PI * 2f);

        private void OnEnable()
        {
            _light = GetComponent<Light>();
            Active = this;
            RenderSettings.ambientMode = AmbientMode.Trilight;

            // The stylized sky is driven per-frame; clone it so the asset stays clean.
            if (RenderSettings.skybox != null && RenderSettings.skybox.HasProperty(NightId))
            {
                _sky = new Material(RenderSettings.skybox);
                RenderSettings.skybox = _sky;
            }
        }

        private void OnDisable()
        {
            if (Active == this) Active = null;
        }

        private void LateUpdate()
        {
            if (_light == null) return;
            float tod = TimeOfDay;
            float sunEl = SunElevation(tod);

            // The sun's sweep: 0 at sunrise, 90 overhead at noon, 180 at sunset. The moon
            // rides the same arc offset half a day, so whichever body is up, the light
            // pitches through 0..180 and never shines from below the horizon.
            float sweep = (tod - 0.25f) * 360f;
            bool sunUp = sunEl > -0.03f; // the swap happens after the sun fully fades
            transform.rotation = Quaternion.Euler(sunUp ? sweep : sweep - 180f, orbitYaw, 0f);

            if (sunUp)
            {
                float day = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.03f, 0.12f, sunEl));
                _light.color = Color.Lerp(sunHorizonColor, sunColor,
                    Mathf.InverseLerp(0.02f, 0.35f, sunEl));
                _light.intensity = sunIntensity * day;
            }
            else
            {
                float moon = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.25f, -sunEl));
                _light.color = moonColor;
                _light.intensity = moonIntensity * moon;
            }

            // Ambient lags a little into dusk, so twilight lingers after the sun is gone.
            float amb = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.12f, 0.25f, sunEl));
            RenderSettings.ambientSkyColor = Color.Lerp(nightAmbientSky, dayAmbientSky, amb);
            RenderSettings.ambientEquatorColor = Color.Lerp(nightAmbientEquator, dayAmbientEquator, amb);
            RenderSettings.ambientGroundColor = Color.Lerp(nightAmbientGround, dayAmbientGround, amb);

            // The sky blends to its night face (stars, moon) as the ambient dies. Both
            // body directions go over so the discs track the light exactly.
            if (_sky != null)
            {
                _sky.SetFloat(NightId, 1f - amb);
                _sky.SetVector(SunDirId,
                    -(Quaternion.Euler(sweep, orbitYaw, 0f) * Vector3.forward));
                _sky.SetVector(MoonDirId,
                    -(Quaternion.Euler(sweep - 180f, orbitYaw, 0f) * Vector3.forward));
            }
        }
    }
}
