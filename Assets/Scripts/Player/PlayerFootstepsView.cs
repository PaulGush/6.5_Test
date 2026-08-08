using System.Collections.Generic;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// Client-side footsteps for this player (local and remote alike): distance-based
    /// steps driven by <see cref="PlayerAvatar"/>'s measured motion, so cadence scales
    /// with actual speed and remote players step off their replicated movement. The
    /// surface is picked by a short downward raycast — anything on a ship, the dock
    /// family, or wood-named objects/materials knocks like planks; island sand crunches;
    /// everything else falls back to stone. Clips load from Resources/Audio.
    /// </summary>
    public class PlayerFootstepsView : MonoBehaviour
    {
        [Tooltip("Metres travelled per step at walking pace; stride lengthens with speed.")]
        [SerializeField] private float strideBase = 0.55f;
        [SerializeField] private float stridePerSpeed = 0.16f;
        [Tooltip("Below this planar speed (m/s) no steps play.")]
        [SerializeField] private float minSpeed = 0.5f;
        [SerializeField] private float volumeAtWalk = 0.5f;
        [SerializeField] private float volumeAtSprint = 0.85f;
        [Tooltip("Step volume multiplier at full crouch (sneaking is quiet).")]
        [SerializeField] private float crouchVolumeScale = 0.4f;

        private PlayerAvatar _avatar;
        private AudioSource _source;
        private float _distance;
        private int _lastIndex = -1;

        private static AudioClip[] _wood, _sand, _stone;

        private void Awake()
        {
            _avatar = GetComponent<PlayerAvatar>();

            _source = gameObject.AddComponent<AudioSource>();
            _source.spatialBlend = 1f;
            _source.minDistance = 2f;
            _source.maxDistance = 22f;
            _source.playOnAwake = false;

            if (_wood == null)
            {
                _wood = LoadSet("step_wood");
                _sand = LoadSet("step_sand");
                _stone = LoadSet("step_stone");
            }
        }

        private static AudioClip[] LoadSet(string prefix)
        {
            var list = new List<AudioClip>();
            foreach (AudioClip clip in Resources.LoadAll<AudioClip>("Audio"))
                if (clip.name.StartsWith(prefix)) list.Add(clip);
            return list.ToArray();
        }

        private void Update()
        {
            if (_avatar == null) return;

            float speed = _avatar.PlanarSpeed;
            bool stepping = _avatar.GroundedFlag && !_avatar.SwimmingFlag && !_avatar.DeadFlag
                && speed > minSpeed;
            if (!stepping)
            {
                _distance = 0f;
                return;
            }

            _distance += speed * Time.deltaTime;
            float stride = strideBase + stridePerSpeed * speed;
            if (_distance < stride) return;
            _distance -= stride;

            AudioClip[] set = PickSurface();
            if (set == null || set.Length == 0) return;

            int i = set.Length == 1 ? 0 : Random.Range(0, set.Length - 1);
            if (i >= _lastIndex && set.Length > 1) i++; // never repeat the previous clip
            _lastIndex = i;

            float vol = Mathf.Lerp(volumeAtWalk, volumeAtSprint, Mathf.InverseLerp(1.6f, 6.5f, speed));
            vol *= Mathf.Lerp(1f, crouchVolumeScale, _avatar.CrouchBlend01);
            _source.pitch = Random.Range(0.92f, 1.08f);
            _source.PlayOneShot(set[i], vol);
        }

        private AudioClip[] PickSurface()
        {
            if (!Physics.Raycast(transform.position, Vector3.down, out RaycastHit hit, 2.2f,
                    ~0, QueryTriggerInteraction.Ignore))
                return _stone;

            if (hit.collider.GetComponentInParent<Game.Ship.ShipController>() != null)
                return _wood; // any deck knocks like planks

            string name = hit.collider.name.ToLowerInvariant();
            var renderer = hit.collider.GetComponent<MeshRenderer>();
            string mat = renderer != null && renderer.sharedMaterial != null
                ? renderer.sharedMaterial.name.ToLowerInvariant() : "";

            if (name.Contains("sand") || mat.Contains("sand") || mat.Contains("terrain"))
                return _sand;
            if (name.Contains("dock") || name.Contains("gangway") || name.Contains("jetty")
                || name.Contains("plank") || name.Contains("stairs") || name.Contains("pile")
                || name.Contains("step") || mat.Contains("wood"))
                return _wood;
            return _stone;
        }
    }
}
