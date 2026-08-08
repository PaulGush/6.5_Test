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
        [Tooltip("Chain strip prefab (a straight run of links, pivot at one end). When set, " +
                 "rigid pieces of it replace the rope line, laid chord-to-chord along the " +
                 "simulated curve — an anchor hangs from chain, not rope.")]
        [SerializeField] private GameObject chainPrefab;
        [Tooltip("Cross-section scale on the chain pieces: the Generic strip's links are " +
                 "thin gauge and read as a dark line at distance; fatten to match the " +
                 "hull's chunky deco chain.")]
        [SerializeField] private float chainThickness = 1.8f;
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
        private bool _hideRig;            // anchor is home: baked hull chain covers the visuals
        private bool _wasDown;            // edge detect for the drop moment
        private Transform[] _chain;       // pooled rigid chain pieces (chain mode)
        private float _chainLen = 2.25f;  // one piece's natural length, measured from its mesh
        private Vector3 _chainHangAxis = Vector3.down; // local axis the strip runs along
        private const int MaxChainPieces = 6;
        private Game.Gameplay.WaterSurface _water;
        private float _nextWaterScan;
        private float _snapUntil;

        /// <summary>Editor-tooling hooks for wiring at build time.</summary>
        public void SetShip(ShipController value) => ship = value;
        public void SetAnchor(Transform value) => anchor = value;
        public void SetRope(Transform value, Transform top) { rope = value; ropeTop = top; }
        public void SetChain(GameObject value) => chainPrefab = value;
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
            // A drop begins at the anchor's authored resting spot, not wherever the sim
            // bob idled under the hawse while the rig was hidden.
            if (down && !_wasDown && _hideRig)
                _bob = _bobPrev = CarriedRingWorld();
            _wasDown = down;
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

            // Hang the prop off the bob: ring at the rope's end, body tilted along the
            // rope. The dangle uses the ship's plain frame (a canonical ring-up mesh
            // hangs upright); the authored pose — for the hull anchor, its draped lean —
            // is only blended back in as the haul brings it home.
            Vector3 ropeDir = top - _bob;
            Quaternion tilt = ropeDir.sqrMagnitude > 1e-6f
                ? Quaternion.FromToRotation(Vector3.up, ropeDir.normalized)
                : Quaternion.identity;
            anchor.rotation = tilt * transform.rotation;
            anchor.position = _bob - anchor.rotation * _ringLocal;

            // The stowed anchor rests in its AUTHORED pose (draped on the hull, where the
            // baked deco chain meets it), not dangling under the pay-out point — the
            // hawse is offset from the stowed spot. Blend home over the last stretch of
            // the haul so the hand-off is seamless, and hide the runtime chain once home:
            // at rest the hull's own baked chain is the only chain in sight.
            float stowedLen = Vector3.Distance(top,
                transform.TransformPoint(_stowed) + Vector3.up * _ringLocal.magnitude);
            float stowBlend = down ? 0f : 1f - Mathf.Clamp01((ropeLen - stowedLen) / 1.2f);
            if (stowBlend > 0f)
            {
                anchor.position = Vector3.Lerp(anchor.position,
                    transform.TransformPoint(_stowed), stowBlend);
                anchor.rotation = Quaternion.Slerp(anchor.rotation,
                    transform.rotation * _stowedRot, stowBlend);
            }
            _hideRig = stowBlend > 0.95f;

            if (_line != null || _chain != null) SimulateRope(dt);
        }

        // Where the rigid payout animation carries the ring — defines the rope length only.
        private Vector3 CarriedRingWorld() =>
            transform.TransformPoint(_animLocal) + Vector3.up * _ringLocal.magnitude;

        // Where the rope ties on: the TOP of the anchor prop, measured from its meshes in
        // the anchor's own frame (the authored pose is upright at Awake, so world bounds
        // convert exactly). The serialized ringOffset only covers a prop with no renderers.
        private Vector3 MeasureRingLocal()
        {
            // A single-mesh prop measures exactly from its local mesh bounds, whatever
            // its authored pose (the canonical hull anchor has its ring at the origin).
            // The world-bounds fallback below inflates for tilted props.
            MeshFilter mf = anchor.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                Bounds b = mf.sharedMesh.bounds;
                return new Vector3(b.center.x, b.max.y, b.center.z);
            }

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
            if (_points == null) return;
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

        // Swap the editor's rigid cylinder for a bendable visual: pooled rigid chain
        // pieces when a chain prefab is wired, a world-space line otherwise.
        private void BuildRopeLine()
        {
            if (rope == null || ropeTop == null || anchor == null) return;
            var meshRenderer = rope.GetComponent<MeshRenderer>();
            Material mat = meshRenderer != null ? meshRenderer.sharedMaterial : null;
            Destroy(rope.GetComponent<MeshFilter>());
            if (meshRenderer != null) Destroy(meshRenderer);

            if (chainPrefab != null)
            {
                BuildChain();
            }
            else
            {
                _line = rope.gameObject.AddComponent<LineRenderer>();
                _line.useWorldSpace = true;
                _line.positionCount = ropeSegments;
                _line.widthMultiplier = ropeWidth;
                _line.numCapVertices = 2;
                _line.numCornerVertices = 2;
                if (mat != null) _line.sharedMaterial = mat;
            }

            _points = new Vector3[ropeSegments];
            _prev = new Vector3[ropeSegments];
            Vector3 top = ropeTop.position;
            for (int i = 0; i < ropeSegments; i++)
                _points[i] = _prev[i] = Vector3.Lerp(top, _bob, i / (ropeSegments - 1f));
        }

        // Pool of rigid chain pieces, and the prefab's own geometry measured so the code
        // adapts to any strip: the longest mesh axis is the run direction, its sign taken
        // from where the volume sits relative to the pivot.
        private void BuildChain()
        {
            // Pieces parent to the station itself — NEVER the rope placeholder, whose
            // authored non-uniform scale (thin stretched cylinder) would crush them.
            var probe = Instantiate(chainPrefab, transform);
            foreach (Collider c in probe.GetComponentsInChildren<Collider>(true))
                Destroy(c);
            var mf = probe.GetComponentInChildren<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                Bounds b = mf.sharedMesh.bounds;
                int axis = b.size.y >= b.size.x && b.size.y >= b.size.z ? 1
                         : b.size.z >= b.size.x ? 2 : 0;
                _chainLen = b.size[axis] * Mathf.Abs(mf.transform.lossyScale[axis])
                    / Mathf.Max(1e-4f, Mathf.Abs(transform.lossyScale[axis]));
                Vector3 dir = Vector3.zero;
                dir[axis] = Mathf.Sign(b.center[axis]);
                _chainHangAxis = dir;
            }

            _chain = new Transform[MaxChainPieces];
            _chain[0] = probe.transform;
            for (int i = 1; i < MaxChainPieces; i++)
            {
                var piece = Instantiate(chainPrefab, transform);
                foreach (Collider c in piece.GetComponentsInChildren<Collider>(true))
                    Destroy(c);
                _chain[i] = piece.transform;
            }
            int lengthAxis = Mathf.Abs(_chainHangAxis.y) > 0.5f ? 1
                           : Mathf.Abs(_chainHangAxis.z) > 0.5f ? 2 : 0;
            foreach (Transform t in _chain)
            {
                Vector3 s = Vector3.one * chainThickness;
                s[lengthAxis] = 1f;
                t.localScale = Vector3.Scale(t.localScale, s);
                t.gameObject.SetActive(false);
            }
        }

        // Rigid pieces laid chord-to-chord along the simulated curve at their natural
        // length — chain never stretches; the last piece overruns into the anchor's stock
        // instead, which is exactly how the Synty deco chains meet geometry.
        private void LayChain()
        {
            int n = _points.Length;
            float total = 0f;
            for (int i = 0; i < n - 1; i++) total += Vector3.Distance(_points[i], _points[i + 1]);
            int pieces = Mathf.Clamp(Mathf.CeilToInt(total / Mathf.Max(0.05f, _chainLen)), 1, _chain.Length);

            for (int k = 0; k < _chain.Length; k++)
            {
                bool on = k < pieces;
                if (_chain[k].gameObject.activeSelf != on) _chain[k].gameObject.SetActive(on);
                if (!on) continue;

                Vector3 p0 = SampleArc(k * _chainLen, total);
                Vector3 toward = SampleArc(Mathf.Min((k + 1) * _chainLen, total), total) - p0;
                if (toward.sqrMagnitude < 1e-6f) toward = _bob - p0;
                if (toward.sqrMagnitude < 1e-6f) toward = Vector3.down;
                Quaternion swing = Quaternion.FromToRotation(_chainHangAxis, toward.normalized);
                _chain[k].SetPositionAndRotation(p0,
                    swing * Quaternion.AngleAxis(k * 90f, _chainHangAxis));
            }
        }

        // Point on the verlet polyline at the given arc length from the cathead.
        private Vector3 SampleArc(float arc, float total)
        {
            if (arc <= 0f) return _points[0];
            if (arc >= total) return _points[_points.Length - 1];
            for (int i = 0; i < _points.Length - 1; i++)
            {
                float seg = Vector3.Distance(_points[i], _points[i + 1]);
                if (arc <= seg) return Vector3.Lerp(_points[i], _points[i + 1], seg < 1e-5f ? 0f : arc / seg);
                arc -= seg;
            }
            return _points[_points.Length - 1];
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
            if (_line != null)
            {
                _line.enabled = !_hideRig;
                if (!_hideRig) _line.SetPositions(_points);
            }
            else if (_chain != null)
            {
                if (_hideRig)
                {
                    foreach (Transform t in _chain)
                        if (t.gameObject.activeSelf) t.gameObject.SetActive(false);
                }
                else
                {
                    LayChain();
                }
            }
        }
    }
}
