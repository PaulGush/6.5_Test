using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// Feeds the Sea/Waves shader its dynamic ripple rings — the expanding foam circles
    /// left by swimmers, hulls, bobbing cargo and surfacing sharks. Rings live in a fixed
    /// ring buffer of shader globals (position, birth time, size, strength); the sea's
    /// fragment stage draws them, which is what keeps every ring glued to the animated
    /// Gerstner surface instead of clipping through crests the way a particle quad would.
    ///
    /// Purely visual and purely local: each peer spawns rings by watching the same synced
    /// transforms, and birth times are stamped with the shared wave clock, so peers agree
    /// closely enough without any networking. Expired slots are simply overwritten; the
    /// shader skips them by age, so nothing needs compacting.
    /// </summary>
    public class WaterRippleSystem : MonoBehaviour
    {
        /// <summary>Must match MAX_RIPPLES in SeaWaves.shader.</summary>
        public const int MaxRipples = 96;

        // Rings farther than this from the camera are sub-pixel — don't spend a slot.
        private const float MaxSpawnDistance = 150f;

        private static readonly Vector4[] Data = new Vector4[MaxRipples]; // x, z, birth, full radius
        private static readonly Vector4[] Aux = new Vector4[MaxRipples];  // strength, lifetime
        private static int _next;
        private static int _used;

        private static readonly int DataId = Shader.PropertyToID("_RippleData");
        private static readonly int AuxId = Shader.PropertyToID("_RippleAux");
        private static readonly int CountId = Shader.PropertyToID("_RippleCount");

        private void OnEnable()
        {
            // Kill stale slots from a previous session (statics survive when domain
            // reload is off) — a birth in the far past reads as long expired.
            for (int i = 0; i < MaxRipples; i++)
            {
                Data[i] = new Vector4(0f, 0f, -1e6f, 1f);
                Aux[i] = new Vector4(0f, 1f, 0f, 0f);
            }
            _next = 0;
            _used = 0;
            Push();
        }

        private void Update() => Push();

        private void OnDisable() => Shader.SetGlobalInt(CountId, 0);

        private void Push()
        {
            // Re-upload every frame: SetGlobalVectorArray fixes the array size on first
            // use, and unconditional pushes keep the globals correct across scene loads.
            Shader.SetGlobalVectorArray(DataId, Data);
            Shader.SetGlobalVectorArray(AuxId, Aux);
            Shader.SetGlobalInt(CountId, _used);
        }

        /// <summary>
        /// Spawn one expanding ring at a world position (only its XZ matters — the sea
        /// shader draws the ring on the surface, wherever the waves put it).
        /// </summary>
        public static void Spawn(Vector3 worldPos, float fullRadius, float strength, float life)
        {
            Camera cam = Camera.main;
            if (cam != null &&
                (cam.transform.position - worldPos).sqrMagnitude > MaxSpawnDistance * MaxSpawnDistance)
                return;

            Data[_next] = new Vector4(worldPos.x, worldPos.z, WaterSurface.WaveTime, Mathf.Max(0.1f, fullRadius));
            Aux[_next] = new Vector4(strength, Mathf.Max(0.1f, life), 0f, 0f);
            _next = (_next + 1) % MaxRipples;
            _used = Mathf.Max(_used, _next == 0 ? MaxRipples : _next);
        }
    }
}
