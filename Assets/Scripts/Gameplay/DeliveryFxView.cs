using System.Collections.Generic;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// The skull cave's payoff moment. Client-local and unsynced on purpose: it polls
    /// the already-synced <see cref="CargoManager"/> ledger (the CargoHudView pattern)
    /// and celebrates every increase — a coin chime from the hoard, an ember burst
    /// from the skull's eye flames (they flash even by day), a warm flare over the
    /// treasure — while the treasure piles grow with the lifetime ledger. Lives on
    /// the CargoDeliveryZone inside the hand-authored loot cave.
    /// </summary>
    public class DeliveryFxView : MonoBehaviour
    {
        private const float FlareTime = 1.6f;
        private const float GrowFullAt = 400f; // delivered gold at which the hoard is full-size

        private CargoManager _manager;
        private AudioSource _source;
        private AudioClip[] _chimes;
        private Light _flare;
        private ParticleSystem[] _eyeFlames;
        private bool[] _eyeWasVisible;
        private Transform[] _piles;
        private Vector3[] _pileBase;
        private int _seen = -1;
        private float _flareLeft;
        private float _nextScan;

        private void Awake()
        {
            _source = gameObject.AddComponent<AudioSource>();
            _source.spatialBlend = 1f;
            _source.minDistance = 5f;
            _source.maxDistance = 45f;
            _source.playOnAwake = false;

            var chimes = new List<AudioClip>();
            foreach (AudioClip clip in Resources.LoadAll<AudioClip>("Audio"))
                if (clip.name.StartsWith("coin_chime")) chimes.Add(clip);
            _chimes = chimes.ToArray();

            var flareGo = new GameObject("DeliveryFlare");
            flareGo.transform.SetParent(transform, false);
            flareGo.transform.localPosition = Vector3.up * 0.8f;
            _flare = flareGo.AddComponent<Light>();
            _flare.type = LightType.Point;
            _flare.color = new Color(1f, 0.82f, 0.35f);
            _flare.range = 14f;
            _flare.shadows = LightShadows.None;
            _flare.enabled = false;

            // The cave around us: eye flames up in the skull, treasure piles below.
            var flames = new List<ParticleSystem>();
            var piles = new List<Transform>();
            if (transform.parent != null)
                foreach (Transform t in transform.parent.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "EyeFlame")
                    {
                        var ps = t.GetComponentInChildren<ParticleSystem>(true);
                        if (ps != null) flames.Add(ps);
                    }
                    else if (t.name.Contains("TreasurePile")) piles.Add(t);
                }
            _eyeFlames = flames.ToArray();
            _eyeWasVisible = new bool[_eyeFlames.Length];
            _piles = piles.ToArray();
            _pileBase = new Vector3[_piles.Length];
            for (int i = 0; i < _piles.Length; i++) _pileBase[i] = _piles[i].localScale;
        }

        private void Update()
        {
            if (_manager == null)
            {
                if (Time.time < _nextScan) return;
                _nextScan = Time.time + 1f;
                _manager = FindAnyObjectByType<CargoManager>();
                if (_manager == null) return;
            }

            int delivered = _manager.Delivered;
            if (_seen < 0) _seen = delivered; // joining mid-voyage adopts the ledger quietly
            else if (delivered > _seen)
            {
                Celebrate();
                _seen = delivered;
            }

            // The hoard grows toward the ledger's size; eased so it swells, not pops.
            float target = 1f + 0.45f * Mathf.Clamp01(delivered / GrowFullAt);
            for (int i = 0; i < _piles.Length; i++)
                if (_piles[i] != null)
                    _piles[i].localScale = Vector3.Lerp(
                        _piles[i].localScale, _pileBase[i] * target, 2f * Time.deltaTime);

            if (_flareLeft > 0f)
            {
                _flareLeft -= Time.deltaTime;
                float k = Mathf.Clamp01(_flareLeft / FlareTime);
                _flare.intensity = 7f * k * k;
                if (_flareLeft <= 0f)
                {
                    _flare.enabled = false;
                    // Daytime eyes go dark again (their DockLantern would fix this
                    // within a poll anyway; this just avoids the 4 s of afterglow).
                    for (int i = 0; i < _eyeFlames.Length; i++)
                    {
                        var r = _eyeFlames[i] != null ? _eyeFlames[i].GetComponent<Renderer>() : null;
                        if (r != null && !_eyeWasVisible[i]) r.enabled = false;
                    }
                }
            }
        }

        private void Celebrate()
        {
            if (_source != null && _chimes.Length > 0)
            {
                _source.pitch = Random.Range(0.94f, 1.06f);
                _source.PlayOneShot(_chimes[Random.Range(0, _chimes.Length)], 0.9f);
            }

            bool fresh = _flareLeft <= 0f;
            _flareLeft = FlareTime;
            _flare.enabled = true;

            for (int i = 0; i < _eyeFlames.Length; i++)
            {
                ParticleSystem ps = _eyeFlames[i];
                if (ps == null) continue;
                var r = ps.GetComponent<Renderer>();
                if (r != null)
                {
                    if (fresh) _eyeWasVisible[i] = r.enabled; // remember the day-state once
                    r.enabled = true;                         // the skull tastes gold, even at noon
                }
                ps.Emit(22);
            }
        }
    }
}
