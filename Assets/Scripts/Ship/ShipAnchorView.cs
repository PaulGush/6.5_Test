using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// Purely visual: the anchor hangs from the cathead as a pendulum on a taut rope. The
    /// ship's anchored state only pays rope out and winds it back in; where the anchor IS
    /// is a verlet bob swinging from the cathead's tip on that much rope — so it sways with
    /// the deck rock, trails the ship's acceleration, and the prop tilts along the rope,
    /// visibly attached to its end. The sea damps the swing hard once the anchor submerges.
    /// The rope itself is a verlet-simulated LineRenderer pinned to the cathead and the
    /// swinging ring (the prefab's stretched cylinder only serves the editor pose). Driven
    /// off the synced <see cref="ShipController"/> state, so it needs no network traffic of
    /// its own and stays correct on every client.
    /// </summary>
    public class ShipAnchorView : MonoBehaviour
    {
        [Tooltip("The ship whose anchored state drives the prop (found in parents if unset).")]
        [SerializeField] private ShipController ship;
        [Tooltip("The anchor prop to move; its authored pose is the stowed position.")]
        [SerializeField] private Transform anchor;
        [Tooltip("Fallback drop below the stowed pose (m), used only when the scene has no " +
                 "WaterSurface to measure against. Normally the anchor keeps running out " +
                 "until it is submergeDepth under the live water surface.")]
        [SerializeField] private float dropDistance = 6f;
        [Tooltip("How far below the water surface the dropped anchor hangs (m).")]
        [SerializeField] private float submergeDepth = 2f;
        [Tooltip("How fast the anchor runs out when let go (m/s) — near-instant, the brake.")]
        [SerializeField] private float dropSpeed = 6f;
        [Tooltip("How long hauling the anchor back up takes (s) — keep just under the " +
                 "ship's weighSeconds so the anchor is stowed as the haul completes.")]
        [SerializeField] private float hoistSeconds = 4.8f;

        [Header("Rope")]
        [Tooltip("Rope visual between the rope point and the anchor's ring. A stretched " +
                 "cylinder in the prefab; replaced by a simulated line at runtime.")]
        [SerializeField] private Transform rope;
        [Tooltip("Fixed point the rope pays out from (the cathead's tip, a sibling of the anchor).")]
        [SerializeField] private Transform ropeTop;
        [Tooltip("Fallback ring height above the prop's pivot (m). Normally the tie-on point " +
                 "is measured from the prop's meshes: the top of the anchor.")]
        [SerializeField] private float ringOffset = 0.75f;
        [Tooltip("Simulated rope points; more = smoother curve.")]
        [SerializeField] private int ropeSegments = 12;
        [Tooltip("Rope rest length as a multiple of the straight pin-to-pin distance (>1 sags).")]
        [SerializeField] private float ropeSlack = 1.06f;
        [SerializeField] private float ropeWidth = 0.08f;
        [Tooltip("Fraction of point velocity kept per step; lower settles the swing faster.")]
        [SerializeField] private float ropeDamping = 0.96f;
        [Tooltip("Fraction of swing velocity the hanging anchor keeps per step (in air). " +
                 "Keep high: air drag on an anchor is nothing.")]
        [SerializeField] private float swingDamping = 0.995f;
        [Tooltip("Swing damping once the anchor is underwater — the splash brakes it hard.")]
        [SerializeField] private float swingDampingWet = 0.9f;
        [Tooltip("Gravity multiplier on the falling anchor. Above 1 reads as real mass at game scale.")]
        [SerializeField] private float fallGravityScale = 1.8f;

        // While the just-spawned state settles (scene load, auto-moor's first scan, a late
        // join's SyncVars), state changes are initial state — the anchor snaps to its pose
        // instead of animating, so a moored ship starts with the anchor already down.
        private const float SpawnSnapSeconds = 1.5f;

        private Vector3 _stowed;
        private Vector3 _animLocal;        // rigid payout animation: where the rig carries the anchor
        private Quaternion _stowedRot;     // anchor's authored local rotation
        private Vector3 _ringLocal;        // ring offset in the anchor's own frame
        private Vector3 _bob, _bobPrev;    // world-space pendulum bob (the anchor's ring)
        private bool _ropeLoaded = true;   // is the anchor's weight on the rope right now?
        private float _slackNow;           // current rope sag multiplier, eased toward load state
        private LineRenderer _line;
        private Vector3[] _points, _prev; // world-space verlet points
        private Game.Gameplay.WaterSurface _water;
        private float _nextWaterScan;
        private float _snapUntil;

        /// <summary>Editor-tooling hooks for wiring at build time.</summary>
        public void SetShip(ShipController value) => ship = value;
        public void SetAnchor(Transform value) => anchor = value;
        public void SetRope(Transform value, Transform top) { rope = value; ropeTop = top; }
        public void SetDropDistance(float value) => dropDistance = value;

        private void Awake()
        {
            if (ship == null) ship = GetComponentInParent<ShipController>();
            if (anchor != null)
            {
                _stowed = _animLocal = anchor.localPosition;
                _stowedRot = anchor.localRotation;
                _ringLocal = MeasureRingLocal();
                _bob = _bobPrev = anchor.TransformPoint(_ringLocal);
            }
            _snapUntil = Time.time + SpawnSnapSeconds;
            BuildRopeLine();
        }

        private void Update()
        {
            if (ship == null || anchor == null) return;

            // How far down "under the surface" is from here, measured live — the ship's
            // freeboard or the map's water height are never assumed.
            float surfaceY = WaterSurfaceY();
            float stowedWorldY = transform.TransformPoint(_stowed).y;
            float drop = float.IsNaN(surfaceY) ? dropDistance
                : Mathf.Max(1f, stowedWorldY - surfaceY + submergeDepth);

            bool down = ship.Anchored && !ship.WeighingAnchor;
            Vector3 target = down ? _stowed + Vector3.down * drop : _stowed;
            if (Time.time < _snapUntil)
            {
                if ((_animLocal - target).sqrMagnitude > 1e-4f) SnapTo(target);
            }
            else
            {
                float speed = down ? dropSpeed : drop / Mathf.Max(0.1f, hoistSeconds);
                _animLocal = Vector3.MoveTowards(_animLocal, target, speed * Time.deltaTime);
            }

            // How much rope the anchor may take: on a drop the windlass free-runs — the
            // whole scope is available at once, so the anchor genuinely FALLS and the rope
            // streams out after it until the scope arrests it. Stowed and hoisting, the
            // rig animation meters the length (wind-in drags the bob back up).
            // Clamp the step so a hitch doesn't explode the verlet integration.
            float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
            Vector3 top = ropeTop.position;
            float ropeLen = down && Time.time >= _snapUntil
                ? Vector3.Distance(top,
                    transform.TransformPoint(target) + Vector3.up * _ringLocal.magnitude)
                : Vector3.Distance(top, CarriedRingWorld());
            bool wet = !float.IsNaN(surfaceY) && _bob.y < surfaceY;
            SimulateSwing(dt, top, ropeLen, wet);

            // Hang the prop off the bob: ring at the rope's end, body tilted along the rope.
            Vector3 ropeDir = top - _bob;
            Quaternion tilt = ropeDir.sqrMagnitude > 1e-6f
                ? Quaternion.FromToRotation(Vector3.up, ropeDir.normalized)
                : Quaternion.identity;
            anchor.rotation = tilt * transform.rotation * _stowedRot;
            anchor.position = _bob - anchor.rotation * _ringLocal;

            if (_line != null) SimulateRope(dt);
        }

        // Where the rigid payout animation carries the ring — defines the rope length only.
        private Vector3 CarriedRingWorld() =>
            transform.TransformPoint(_animLocal) + Vector3.up * _ringLocal.magnitude;

        // Where the rope ties on: the TOP of the anchor prop, measured from its meshes in
        // the anchor's own frame (the authored pose is upright at Awake, so world bounds
        // convert exactly). The serialized ringOffset only covers a prop with no renderers.
        private Vector3 MeasureRingLocal()
        {
            Renderer[] rs = anchor.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return Vector3.up * ringOffset;
            bool has = false;
            Bounds local = default;
            foreach (Renderer r in rs)
            {
                Bounds w = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = new Vector3(
                        (i & 1) == 0 ? w.min.x : w.max.x,
                        (i & 2) == 0 ? w.min.y : w.max.y,
                        (i & 4) == 0 ? w.min.z : w.max.z);
                    Vector3 p = anchor.InverseTransformPoint(corner);
                    if (!has) { local = new Bounds(p, Vector3.zero); has = true; }
                    else local.Encapsulate(p);
                }
            }
            return new Vector3(local.center.x, local.max.y, local.center.z);
        }

        // Pendulum on a real rope: integrate the bob under (heavy) gravity; the rope only
        // acts when it loads up — inside the scope the anchor is in free fall and the line
        // is slack, at the scope's edge it arrests the fall and carries the weight. The
        // pivot riding the rocking deck (and the ship's own motion) feeds the swing.
        private void SimulateSwing(float dt, Vector3 top, float len, bool wet)
        {
            float damping = wet ? swingDampingWet : swingDamping;
            Vector3 p = _bob;
            _bob += (_bob - _bobPrev) * damping + Physics.gravity * (fallGravityScale * dt * dt);
            _bobPrev = p;

            Vector3 d = _bob - top;
            float dist = d.magnitude;
            _ropeLoaded = dist >= len - 0.05f;
            if (dist > len) _bob = top + d * (len / dist);
        }

        // Jump straight to a pose (initial state, not an event): park the payout, hang the
        // bob at rest directly below the cathead, re-seed the rope between the pins and run
        // the sim ahead so everything starts settled instead of visibly falling into shape.
        private void SnapTo(Vector3 targetLocal)
        {
            _animLocal = targetLocal;
            Vector3 top = ropeTop.position;
            _bob = _bobPrev = top + Vector3.down * Vector3.Distance(top, CarriedRingWorld());
            if (_line == null) return;
            for (int i = 0; i < _points.Length; i++)
                _points[i] = _prev[i] = Vector3.Lerp(top, _bob, i / (_points.Length - 1f));
            for (int k = 0; k < 90; k++) SimulateRope(1f / 60f);
        }

        // The scene's water plane, found lazily (it may spawn after us on clients).
        private float WaterSurfaceY()
        {
            if (_water == null && Time.time >= _nextWaterScan)
            {
                _nextWaterScan = Time.time + 2f;
                _water = FindAnyObjectByType<Game.Gameplay.WaterSurface>();
            }
            return _water != null ? _water.SurfaceY : float.NaN;
        }

        // Swap the editor's rigid cylinder for a world-space line the sim can bend.
        private void BuildRopeLine()
        {
            if (rope == null || ropeTop == null || anchor == null) return;
            var meshRenderer = rope.GetComponent<MeshRenderer>();
            Material mat = meshRenderer != null ? meshRenderer.sharedMaterial : null;
            Destroy(rope.GetComponent<MeshFilter>());
            if (meshRenderer != null) Destroy(meshRenderer);

            _line = rope.gameObject.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.positionCount = ropeSegments;
            _line.widthMultiplier = ropeWidth;
            _line.numCapVertices = 2;
            _line.numCornerVertices = 2;
            if (mat != null) _line.sharedMaterial = mat;

            _points = new Vector3[ropeSegments];
            _prev = new Vector3[ropeSegments];
            Vector3 top = ropeTop.position;
            for (int i = 0; i < ropeSegments; i++)
                _points[i] = _prev[i] = Vector3.Lerp(top, _bob, i / (ropeSegments - 1f));
        }

        // Classic verlet rope: integrate the free points, pin the ends to the cathead and
        // the swinging ring, then relax segment lengths a few rounds. The rest length
        // follows the pin distance, so the rope pays out and winds back with the bob.
        private void SimulateRope(float dt)
        {
            int n = _points.Length;
            Vector3 top = ropeTop.position, ring = _bob;
            // Sag follows tension: loaded (hanging, hauling, the arrest) the line is bar-
            // taut; slack (free fall, a heave of the deck) it bellies out and streams.
            _slackNow = Mathf.Lerp(_slackNow <= 0f ? ropeSlack : _slackNow,
                _ropeLoaded ? 1.0f : ropeSlack, 1f - Mathf.Exp(-8f * dt));
            float rest = Vector3.Distance(top, ring) * _slackNow / (n - 1);

            for (int i = 1; i < n - 1; i++)
            {
                Vector3 p = _points[i];
                _points[i] += (p - _prev[i]) * ropeDamping + Physics.gravity * (dt * dt);
                _prev[i] = p;
            }
            _points[0] = top;
            _points[n - 1] = ring;

            for (int k = 0; k < 4; k++)
            {
                for (int i = 0; i < n - 1; i++)
                {
                    Vector3 d = _points[i + 1] - _points[i];
                    float len = d.magnitude;
                    if (len < 1e-5f) continue;
                    Vector3 half = d * ((len - rest) / len * 0.5f);
                    bool pinA = i == 0, pinB = i + 1 == n - 1;
                    if (!pinA) _points[i] += pinB ? half * 2f : half;
                    if (!pinB) _points[i + 1] -= pinA ? half * 2f : half;
                }
                _points[0] = top;
                _points[n - 1] = ring;
            }
            _line.SetPositions(_points);
        }
    }
}
