using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// Marks the scene's water plane so gameplay code can ask where the surface is at
    /// runtime instead of baking heights in. The flat mean level is this transform's world
    /// Y; <see cref="HeightAt"/> additionally re-evaluates the Sea/Waves shader's animated
    /// wave field on the CPU (reading the wave parameters straight off the water material),
    /// so visuals like the ship's buoyancy can ride the same waves the player sees. The
    /// shader's camera-distance detail fade is deliberately not applied — it is a rendering
    /// optimisation, not part of the sea state.
    /// </summary>
    public class WaterSurface : MonoBehaviour
    {
        private Material _mat;
        private bool _cached;
        private float _amp, _len, _speed, _noiseAmp, _noiseScale;

        /// <summary>The mean surface height (the flat plane the waves oscillate around).</summary>
        public float SurfaceY => transform.position.y;

        /// <summary>World-space height of the animated surface at (x, z).</summary>
        public float HeightAt(float x, float z)
        {
            if (!CacheParams()) return SurfaceY;
            float t = Time.timeSinceLevelLoad; // matches the shader's _Time.y
            float ts = t * _speed;

            // Mirror of the Sea/Waves vertex stage (height terms only; horizontal chop
            // does not matter for sampling). Keep in step with SeaWaves.shader.
            float y = 0f;
            y += Gerstner(x, z, 0.3f, -1.0f, _len * 1.9f, _amp * 0.35f, t);
            y += Gerstner(x, z, 1.0f, 0.35f, _len, _amp * 0.5f, t);
            y += Gerstner(x, z, -0.4f, 1.0f, _len * 0.53f, _amp * 0.25f, t);
            y += Gerstner(x, z, 0.7f, -0.8f, _len * 0.31f, _amp * 0.15f, t);

            float groups = Mathf.Lerp(0.45f, 1.4f, VNoise(x * 0.024f + 0.013f * ts, z * 0.024f + 0.008f * ts))
                         * Mathf.Lerp(0.7f, 1.3f, VNoise(x * 0.011f - 0.006f * ts, z * 0.011f - 0.011f * ts));
            y *= groups;

            float ripple = VNoise(x * _noiseScale + 0.07f * ts, z * _noiseScale + 0.045f * ts)
                         + 0.5f * VNoise(x * _noiseScale * 2.3f - 0.052f * ts, z * _noiseScale * 2.3f - 0.09f * ts)
                         + 0.25f * VNoise(x * _noiseScale * 5.1f + 0.11f * ts, z * _noiseScale * 5.1f - 0.06f * ts);
            y += (ripple * 0.5714f - 0.5f) * _noiseAmp * 2f;

            return SurfaceY + y;
        }

        private float Gerstner(float x, float z, float dx, float dz, float wavelength, float amp, float t)
        {
            float inv = 1f / Mathf.Sqrt(dx * dx + dz * dz);
            float k = Mathf.PI * 2f / wavelength;
            float c = Mathf.Sqrt(9.8f / k); // dispersion, as in the shader
            float phase = k * ((dx * x + dz * z) * inv - c * t * _speed);
            return amp * Mathf.Sin(phase);
        }

        // Same hash/value-noise as the shader, so CPU and GPU agree on the sea state.
        private static float Hash(float x, float y)
        {
            float h = Mathf.Sin(x * 127.1f + y * 311.7f) * 43758.5453f;
            return h - Mathf.Floor(h);
        }

        private static float VNoise(float x, float y)
        {
            float ix = Mathf.Floor(x), iy = Mathf.Floor(y);
            float fx = x - ix, fy = y - iy;
            float ux = fx * fx * (3f - 2f * fx), uy = fy * fy * (3f - 2f * fy);
            return Mathf.Lerp(
                Mathf.Lerp(Hash(ix, iy), Hash(ix + 1f, iy), ux),
                Mathf.Lerp(Hash(ix, iy + 1f), Hash(ix + 1f, iy + 1f), ux), uy);
        }

        private bool CacheParams()
        {
            if (_cached && _mat != null) return true;
            var renderer = GetComponent<MeshRenderer>();
            _mat = renderer != null ? renderer.sharedMaterial : null;
            if (_mat == null || !_mat.HasProperty("_WaveAmp")) { _mat = null; return false; }
            _amp = _mat.GetFloat("_WaveAmp");
            _len = _mat.GetFloat("_WaveLength");
            _speed = _mat.GetFloat("_WaveSpeed");
            _noiseAmp = _mat.HasProperty("_NoiseAmp") ? _mat.GetFloat("_NoiseAmp") : 0f;
            _noiseScale = _mat.HasProperty("_NoiseScale") ? _mat.GetFloat("_NoiseScale") : 0.13f;
            _cached = true;
            return true;
        }
    }
}
