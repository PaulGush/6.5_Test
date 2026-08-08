using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// Client-side ambient bed: sea and wind loops above water, a muffled rumble below.
    /// Self-bootstraps like <see cref="UnderwaterView"/> and needs no scene wiring —
    /// clips load from Resources/Audio, the sources are 2D, and the mix crossfades on
    /// whether the active camera is under the animated sea surface (same HeightAt test
    /// the underwater fog uses).
    /// </summary>
    public class AmbientSoundView : MonoBehaviour
    {
        [SerializeField] private float seaVolume = 0.4f;
        [SerializeField] private float windVolume = 0.28f;
        [SerializeField] private float underwaterVolume = 0.9f;
        [Tooltip("Crossfade rate between the above/below-water mixes (1/s).")]
        [SerializeField] private float fadeRate = 4f;

        private AudioSource _sea, _wind, _under;
        private WaterSurface _water;
        private float _nextWaterScan;
        private float _submerged; // 0 above water .. 1 below

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<AmbientSoundView>() != null) return;
            var go = new GameObject("AmbientSoundView");
            go.AddComponent<AmbientSoundView>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            _sea = MakeLoop("Audio/amb_sea_loop");
            _wind = MakeLoop("Audio/amb_wind_loop");
            _under = MakeLoop("Audio/amb_underwater_loop");
        }

        private AudioSource MakeLoop(string path)
        {
            var clip = Resources.Load<AudioClip>(path);
            if (clip == null)
            {
                Debug.LogWarning($"AmbientSoundView: missing clip {path}");
                return null;
            }
            var src = gameObject.AddComponent<AudioSource>();
            src.clip = clip;
            src.loop = true;
            src.spatialBlend = 0f; // ambient bed, not positional
            src.volume = 0f;
            src.Play();
            return src;
        }

        private void Update()
        {
            if (_water == null && Time.time >= _nextWaterScan)
            {
                _nextWaterScan = Time.time + 1f;
                _water = FindAnyObjectByType<WaterSurface>();
            }

            Camera cam = Camera.main;
            bool under = false;
            if (_water != null && cam != null)
            {
                Vector3 p = cam.transform.position;
                under = p.y < _water.HeightAt(p.x, p.z);
            }
            _submerged = Mathf.MoveTowards(_submerged, under ? 1f : 0f, fadeRate * Time.deltaTime);

            if (_sea != null) _sea.volume = seaVolume * (1f - _submerged);
            if (_wind != null) _wind.volume = windVolume * (1f - _submerged);
            if (_under != null) _under.volume = underwaterVolume * _submerged;
        }
    }
}
