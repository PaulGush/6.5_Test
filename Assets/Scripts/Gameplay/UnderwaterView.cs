using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// Client-side underwater look: when the active camera dips below the animated sea
    /// surface (same <see cref="WaterSurface.HeightAt"/> the swimming and buoyancy use),
    /// dense teal fog rolls in and the camera clears to the same color instead of the
    /// skybox, so the horizon doesn't show through the water. Everything is restored the
    /// moment the camera surfaces. All the scene's shaders are fog-aware (URP Lit, the
    /// sea, the island terrain), so the fog alone sells the murk.
    ///
    /// Purely visual and per-client, so it self-bootstraps at runtime instead of living
    /// in the scene; a headless server has no camera and the component idles.
    /// </summary>
    [DefaultExecutionOrder(300)] // after Cinemachine has placed the camera for this frame
    public class UnderwaterView : MonoBehaviour
    {
        [Tooltip("Fog color and camera background while submerged.")]
        [SerializeField] private Color waterColor = new Color(0.05f, 0.22f, 0.33f);
        [Tooltip("Exponential-squared fog density while submerged (~0.06 = 20 m visibility).")]
        [SerializeField] private float fogDensity = 0.06f;

        private WaterSurface _water;
        private float _nextWaterScan;
        private bool _submerged;

        // Settings to put back when surfacing.
        private bool _hadFog;
        private FogMode _fogMode;
        private Color _fogColor;
        private float _fogDensity, _fogStart, _fogEnd;
        private CameraClearFlags _clearFlags;
        private Color _background;
        private Camera _restoreCam;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<UnderwaterView>() != null) return;
            var go = new GameObject("UnderwaterView");
            go.AddComponent<UnderwaterView>();
            DontDestroyOnLoad(go);
        }

        private void LateUpdate()
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

            if (under == _submerged) return;
            _submerged = under;
            if (under) Apply(cam);
            else Restore();
        }

        private void Apply(Camera cam)
        {
            _hadFog = RenderSettings.fog;
            _fogMode = RenderSettings.fogMode;
            _fogColor = RenderSettings.fogColor;
            _fogDensity = RenderSettings.fogDensity;
            _fogStart = RenderSettings.fogStartDistance;
            _fogEnd = RenderSettings.fogEndDistance;
            _restoreCam = cam;
            _clearFlags = cam.clearFlags;
            _background = cam.backgroundColor;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = waterColor;
            RenderSettings.fogDensity = fogDensity;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = waterColor;
        }

        private void Restore()
        {
            RenderSettings.fog = _hadFog;
            RenderSettings.fogMode = _fogMode;
            RenderSettings.fogColor = _fogColor;
            RenderSettings.fogDensity = _fogDensity;
            RenderSettings.fogStartDistance = _fogStart;
            RenderSettings.fogEndDistance = _fogEnd;
            if (_restoreCam != null)
            {
                _restoreCam.clearFlags = _clearFlags;
                _restoreCam.backgroundColor = _background;
            }
            _restoreCam = null;
        }

        private void OnDisable()
        {
            if (_submerged) { _submerged = false; Restore(); }
        }
    }
}
