using Mirror;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// Shark: ambient patroller that turns predator. On its circuit it swims a wobbly
    /// circle just under the animated surface, fin breaking the water now and then. A
    /// server-side brain watches for players SWIMMING near it: linger too long inside its
    /// detection ring and it peels off its circuit, runs the swimmer down, and bites —
    /// each bite wounds (NetworkPlayer health), and the shark circles back for another
    /// pass until the swimmer dies or escapes. It gives up if the prey gets out of the
    /// water or far enough away, then swims back to its circuit.
    ///
    /// The server owns all movement and the NetworkTransform on the same object
    /// replicates it; patrol math is still deterministic off the synced wave clock, so
    /// the server does no pathfinding — the circuit IS the schedule. Purely visual on
    /// clients (Animator swim loop speeds up during a chase via a synced mode byte).
    /// </summary>
    public class SharkView : NetworkBehaviour
    {
        [Header("Circuit")]
        [Tooltip("Centre of the patrol circuit (world XZ).")]
        [SerializeField] private Vector2 center;
        [Tooltip("Mean circuit radius (m); the actual radius breathes around it.")]
        [SerializeField] private float radius = 18f;
        [Tooltip("Cruise speed (m/s); sets how fast the shark rounds its circuit.")]
        [SerializeField] private float speed = 2.2f;
        [Tooltip("Start angle on the circuit (radians), so sharks sharing one don't stack.")]
        [SerializeField] private float phase;
        [Tooltip("Per-shark variation seed.")]
        [SerializeField] private float seed;
        [Tooltip("Mean depth of the body under the animated surface (m).")]
        [SerializeField] private float depthMean = 0.75f;
        [Tooltip("Slow rise/sink amplitude (m); the top of the cycle puts the fin out.")]
        [SerializeField] private float depthWave = 0.55f;
        [Tooltip("Roll into the turn (deg).")]
        [SerializeField] private float bank = 9f;

        [Header("Hunting")]
        [Tooltip("A swimmer inside this ring feeds the shark's interest (m).")]
        [SerializeField] private float detectRadius = 22f;
        [Tooltip("How long a swimmer can linger inside the ring before the chase starts (s).")]
        [SerializeField] private float lingerSeconds = 5f;
        [Tooltip("Chase speed (m/s). Faster than a sprint-swim — reach shore or a ladder.")]
        [SerializeField] private float chaseSpeed = 6.5f;
        [Tooltip("Turn rate while hunting (deg/s).")]
        [SerializeField] private float turnRate = 110f;
        [Tooltip("Bite distance (m).")]
        [SerializeField] private float biteRange = 1.7f;
        [Tooltip("Damage per bite (players spawn with 100 health).")]
        [SerializeField] private float biteDamage = 35f;
        [Tooltip("Pause before the next hunt after landing a bite (s) — shorter than the give-up cooldown, a wounded swimmer is worth circling back for.")]
        [SerializeField] private float biteCooldown = 4f;
        [Tooltip("Give up when the prey gets this far away (m).")]
        [SerializeField] private float loseDistance = 50f;
        [Tooltip("Give up when the prey has been out of the water this long (s).")]
        [SerializeField] private float outOfWaterGrace = 2.5f;
        [Tooltip("Hard cap on one chase (s).")]
        [SerializeField] private float maxChaseSeconds = 25f;
        [Tooltip("Disinterest after a bite or an abandoned chase (s).")]
        [SerializeField] private float cooldownSeconds = 12f;

        private enum Mode : byte { Patrol, Chase, Return }

        // Server -> clients, cosmetic only (Animator pace); movement itself replicates
        // through the NetworkTransform alongside this component.
        [SyncVar] private byte modeByte;

        private WaterSurface _water;
        private float _nextWaterScan;
        private Animator _animator;

        // Server-side hunting state.
        private Mode _mode = Mode.Patrol;
        private float _linger, _chaseTime, _preyDryTime, _cooldown;
        private Game.Player.PlayerController _prey;
        private Game.Player.NetworkPlayer _preyPlayer;
        private Game.Player.PlayerController[] _players = System.Array.Empty<Game.Player.PlayerController>();
        private float _nextPlayerScan;

        private void Awake()
        {
            _animator = GetComponentInChildren<Animator>();
        }

        private void Update()
        {
            if (_animator != null)
                _animator.speed = modeByte == (byte)Mode.Chase ? 1.8f : 1f;

            if (!NetworkServer.active) return; // clients render what the server replicates

            if (_water == null && Time.time >= _nextWaterScan)
            {
                _nextWaterScan = Time.time + 1f;
                _water = FindAnyObjectByType<WaterSurface>();
            }

            float dt = Time.deltaTime;
            _cooldown = Mathf.Max(0f, _cooldown - dt);

            switch (_mode)
            {
                case Mode.Patrol: ServerPatrol(dt); break;
                case Mode.Chase: ServerChase(dt); break;
                case Mode.Return: ServerReturn(dt); break;
            }
            modeByte = (byte)_mode;
        }

        // ---------- patrol ----------

        private void ServerPatrol(float dt)
        {
            float t = WaterSurface.WaveTime;
            Vector3 now = Sample(t);
            Vector3 ahead = Sample(t + 0.35f);
            Vector3 dir = ahead - now;
            if (dir.sqrMagnitude > 1e-6f)
                transform.SetPositionAndRotation(now,
                    Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(0f, 0f, bank));

            if (_cooldown > 0f) { _linger = 0f; return; }

            // Interest builds while any swimmer sits inside the ring, drains when empty.
            Game.Player.PlayerController swimmer = NearestSwimmer(out float dist);
            if (swimmer != null && dist <= detectRadius)
            {
                _linger += dt;
                if (_linger >= lingerSeconds)
                {
                    _prey = swimmer;
                    _preyPlayer = swimmer.GetComponent<Game.Player.NetworkPlayer>();
                    _chaseTime = 0f;
                    _preyDryTime = 0f;
                    _mode = Mode.Chase;
                }
            }
            else
            {
                _linger = Mathf.Max(0f, _linger - dt);
            }
        }

        // ---------- chase ----------

        private void ServerChase(float dt)
        {
            _chaseTime += dt;
            bool lost = _prey == null || _preyPlayer == null || _preyPlayer.IsDead
                || _chaseTime > maxChaseSeconds;
            if (!lost)
            {
                _preyDryTime = _prey.IsSwimming ? 0f : _preyDryTime + dt;
                float dist = Vector3.Distance(transform.position, _prey.transform.position);
                lost = _preyDryTime > outOfWaterGrace || dist > loseDistance;

                if (!lost && dist <= biteRange && _prey.IsSwimming)
                {
                    // A bite wounds; the shark peels off and circles back for another pass
                    // unless that was the killing blow.
                    _preyPlayer.ServerDamage(biteDamage, Game.Player.NetworkPlayer.CauseOfDeath.Shark);
                    Disengage(_preyPlayer.IsDead ? cooldownSeconds : biteCooldown);
                    return;
                }
            }
            if (lost)
            {
                Disengage(cooldownSeconds);
                return;
            }

            // Run the swimmer down from just below, but never breach.
            Vector3 target = _prey.transform.position + Vector3.down * 0.35f;
            Steer(target, chaseSpeed, dt);
        }

        private void Disengage(float cooldown)
        {
            _prey = null;
            _preyPlayer = null;
            _cooldown = cooldown;
            _mode = Mode.Return;
        }

        // ---------- back to the circuit ----------

        private void ServerReturn(float dt)
        {
            Vector3 slot = Sample(WaterSurface.WaveTime);
            if (Vector3.Distance(transform.position, slot) < 2.5f)
            {
                _mode = Mode.Patrol;
                _linger = 0f;
                return;
            }
            Steer(slot, speed * 1.5f, dt);
        }

        // Turn-rate-limited swim toward a point, capped below the animated surface.
        private void Steer(Vector3 target, float moveSpeed, float dt)
        {
            if (_water != null)
                target.y = Mathf.Min(target.y, _water.HeightAt(target.x, target.z) - 0.3f);

            Vector3 dir = target - transform.position;
            if (dir.sqrMagnitude < 1e-6f) return;
            Quaternion look = Quaternion.LookRotation(dir.normalized, Vector3.up);
            Quaternion rot = Quaternion.RotateTowards(transform.rotation, look, turnRate * dt);
            transform.SetPositionAndRotation(transform.position + rot * Vector3.forward * moveSpeed * dt, rot);
        }

        private Game.Player.PlayerController NearestSwimmer(out float bestDist)
        {
            if (Time.time >= _nextPlayerScan)
            {
                _nextPlayerScan = Time.time + 1f;
                _players = FindObjectsByType<Game.Player.PlayerController>(FindObjectsSortMode.None);
            }

            Game.Player.PlayerController best = null;
            bestDist = float.MaxValue;
            foreach (var p in _players)
            {
                if (p == null || !p.IsSwimming) continue;
                var np = p.GetComponent<Game.Player.NetworkPlayer>();
                if (np != null && np.IsDead) continue; // corpses aren't prey
                float d = Vector3.Distance(transform.position, p.transform.position);
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        // Deterministic pose on the circuit at time t: breathing radius, drifting depth.
        private Vector3 Sample(float t)
        {
            float ang = phase + t * speed / Mathf.Max(4f, radius);
            float r = radius * (0.78f + 0.22f * Mathf.Sin(t * 0.13f + seed * 7.31f));
            float x = center.x + Mathf.Cos(ang) * r;
            float z = center.y + Mathf.Sin(ang) * r;
            float surface = _water != null ? _water.HeightAt(x, z) : 0f;
            float depth = depthMean + depthWave * Mathf.Sin(t * 0.19f + seed * 3.77f);
            return new Vector3(x, surface - depth, z);
        }
    }
}
