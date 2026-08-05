using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// Purely visual: rides the anchor prop up and down the hull side as the ship's anchored
    /// state changes, and simulates the rope it hangs from. The prefab carries a plain
    /// stretched cylinder for the rope (so the stowed pose reads in the editor); at runtime
    /// it is swapped for a verlet-simulated LineRenderer pinned to the cathead's tip and the
    /// anchor's ring, so the line sags and sways with the ship's motion instead of staying
    /// rigid. Driven off the synced <see cref="ShipController"/> state, so it needs no
    /// network traffic of its own and stays correct on every client.
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
        [Tooltip("The anchor ring's height above the prop's pivot (m) — where the rope ties on.")]
        [SerializeField] private float ringOffset = 0.75f;
        [Tooltip("Simulated rope points; more = smoother curve.")]
        [SerializeField] private int ropeSegments = 12;
        [Tooltip("Rope rest length as a multiple of the straight pin-to-pin distance (>1 sags).")]
        [SerializeField] private float ropeSlack = 1.06f;
        [SerializeField] private float ropeWidth = 0.08f;
        [Tooltip("Fraction of point velocity kept per step; lower settles the swing faster.")]
        [SerializeField] private float ropeDamping = 0.96f;

        // While the just-spawned state settles (scene load, auto-moor's first scan, a late
        // join's SyncVars), state changes are initial state — the anchor snaps to its pose
        // instead of animating, so a moored ship starts with the anchor already down.
        private const float SpawnSnapSeconds = 1.5f;

        private Vector3 _stowed;
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
            if (anchor != null) _stowed = anchor.localPosition;
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
                if ((anchor.localPosition - target).sqrMagnitude > 1e-4f) SnapTo(target);
            }
            else
            {
                float speed = down ? dropSpeed : drop / Mathf.Max(0.1f, hoistSeconds);
                anchor.localPosition =
                    Vector3.MoveTowards(anchor.localPosition, target, speed * Time.deltaTime);
            }

            // Clamp the step so a hitch doesn't explode the verlet integration.
            if (_line != null) SimulateRope(Mathf.Min(Time.deltaTime, 1f / 30f));
        }

        // Jump straight to a pose (initial state, not an event): place the anchor, re-seed
        // the rope between the pins and run the sim ahead so it starts already sagging at
        // rest instead of visibly falling into shape.
        private void SnapTo(Vector3 targetLocal)
        {
            anchor.localPosition = targetLocal;
            if (_line == null) return;
            Vector3 top = ropeTop.position, ring = RingWorld();
            for (int i = 0; i < _points.Length; i++)
                _points[i] = _prev[i] = Vector3.Lerp(top, ring, i / (_points.Length - 1f));
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

        private Vector3 RingWorld() => anchor.position + Vector3.up * ringOffset;

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
            Vector3 top = ropeTop.position, ring = RingWorld();
            for (int i = 0; i < ropeSegments; i++)
                _points[i] = _prev[i] = Vector3.Lerp(top, ring, i / (ropeSegments - 1f));
        }

        // Classic verlet rope: integrate the free points, pin the ends to the cathead and
        // the ring, then relax segment lengths a few rounds. The rest length follows the
        // pin distance, so the rope pays out with the anchor and winds back on the haul.
        private void SimulateRope(float dt)
        {
            int n = _points.Length;
            Vector3 top = ropeTop.position, ring = RingWorld();
            float rest = Vector3.Distance(top, ring) * ropeSlack / (n - 1);

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
