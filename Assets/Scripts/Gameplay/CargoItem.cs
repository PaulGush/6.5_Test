using Mirror;
using UnityEngine;

namespace Game.Gameplay
{
    /// <summary>
    /// A piece of loot: a networked <see cref="Grabbable"/> with a gold value. Aboard a
    /// ship it stays a LIVE dynamic body: the deck-carry (below) moves it with the hull
    /// while gravity integrates on top, and its colliders swap to a slick material while
    /// riding a deck — so on the ship's REAL tilt cargo genuinely slides in a heel, and
    /// keeping the pile inside the hold's coaming is the cargo-safety game. (On land the
    /// normal grippy material returns, so island loot doesn't toboggan down hillsides.)
    /// Dropped in the sea it floats, so a spilled crate is a swim, not a loss. The lash
    /// machinery (ServerSetStowed: parent + kinematic + NT off, mirroring the held-state
    /// pattern) is kept for a future manual lash-down job; nothing auto-lashes.
    /// The server owns all state; <see cref="Game.Ship.CargoHold"/> counts what rides
    /// the deck, the loot cave's <see cref="CargoDelivery"/> converts it to score.
    /// </summary>
    [RequireComponent(typeof(Grabbable))]
    public class CargoItem : NetworkBehaviour
    {
        [Tooltip("Delivery value (gold).")]
        [SerializeField] private int value = 10;
        [Tooltip("Buoyancy: fraction above neutral when submerged (>0 floats).")]
        [SerializeField] private float buoyancy = 1.25f;
        [Tooltip("Linear damping while in the water (drag makes the bob read right).")]
        [SerializeField] private float wetDamping = 2.5f;
        [Tooltip("How hard the deck can drag this cargo along (m/s²) — an unlashed crate's " +
                 "effective grip. Deck tilt or ship acceleration beyond this leaves the " +
                 "crate behind: it slides. tan(slide angle) ≈ this / 9.8.")]
        [SerializeField] private float carryTraction = 1.2f;

        [SyncVar(hook = nameof(OnStowedChanged))]
        private NetworkIdentity _stowedShip;

        private Grabbable _grabbable;
        private Rigidbody _body;
        private NetworkTransformBase _netTransform;
        private float _dryDamping;
        private bool _wet;
        private WaterSurface _water;
        private float _nextWaterScan;
        private Game.Ship.ShipController _deckShip;
        private Rigidbody _deckShipBody;

        // Deck slickness: ship tilt tops out under ~10°, where ordinary friction (~0.6,
        // holds to ~30°) would keep cargo glued forever. One shared low-friction material,
        // swapped in only while riding a deck, lets a few degrees of heel start a slide.
        private static PhysicsMaterial _slickShared;
        private Collider[] _cols;
        private PhysicsMaterial[] _dryMats;
        private bool _slick;

        /// <summary>The ship this loose item is resting on, if any (server).</summary>
        public Game.Ship.ShipController DeckShip => _deckShip;

        public int Value => value;
        public bool IsStowed => _stowedShip != null;
        public Grabbable Grabbable => _grabbable;
        /// <summary>Server: seconds since this item was last in someone's hands.</summary>
        public float LooseSeconds => Time.time - _heldUntil;

        private float _heldUntil;

        private void Awake()
        {
            _grabbable = GetComponent<Grabbable>();
            _body = GetComponent<Rigidbody>();
            _netTransform = GetComponent<NetworkTransformBase>();
            _dryDamping = _body.linearDamping;
            _cols = GetComponentsInChildren<Collider>(true);
            _dryMats = new PhysicsMaterial[_cols.Length];
            for (int i = 0; i < _cols.Length; i++)
                _dryMats[i] = _cols[i].sharedMaterial;
        }

        public override void OnStartClient() => ApplyStowState(_stowedShip);

        /// <summary>Server: lash to a ship (parents + freezes on every client) or cast loose.</summary>
        [Server]
        public void ServerSetStowed(NetworkIdentity ship)
        {
            if (_stowedShip == ship) return;
            _stowedShip = ship;
            ApplyStowState(ship); // hooks don't run on the server
        }

        private void OnStowedChanged(NetworkIdentity oldShip, NetworkIdentity newShip)
            => ApplyStowState(newShip);

        private void ApplyStowState(NetworkIdentity ship)
        {
            bool stowed = ship != null;
            // Parenting alone rides perfectly now: the deck's motion is fully physical,
            // so a lashed (kinematic) child inherits heave and tilt from the hierarchy.
            transform.SetParent(stowed ? ship.transform : null, true);
            // While held, Grabbable owns the physics/NT switches; don't fight it.
            if (!_grabbable.IsHeld)
            {
                _body.isKinematic = stowed;
                _body.useGravity = !stowed;
                if (_netTransform != null) _netTransform.enabled = !stowed;
            }
        }

        // Server-side buoyancy for loose cargo in the sea: spring toward the animated
        // surface plus water drag. Held/stowed cargo is kinematic and skips out early.
        private void FixedUpdate()
        {
            if (!NetworkServer.active) return;
            if (_grabbable.IsHeld) _heldUntil = Time.time;
            if (_body.isKinematic) return;

            CarryWithDeck();
            SetDeckSlick(_deckShip != null);

            if (_water == null && Time.time >= _nextWaterScan)
            {
                _nextWaterScan = Time.time + 1f;
                _water = FindAnyObjectByType<WaterSurface>();
            }
            if (_water == null) return;

            Vector3 p = _body.worldCenterOfMass;
            float depth = _water.HeightAt(p.x, p.z) - p.y;
            bool wet = depth > 0f;
            if (wet)
            {
                float f = Mathf.Clamp01(depth / 0.35f);
                _body.AddForce(Vector3.up * (-Physics.gravity.y * buoyancy * f * _body.mass));
            }
            if (wet != _wet)
            {
                _wet = wet;
                _body.linearDamping = wet ? wetDamping : _dryDamping;
            }
        }

        // Moving-platform carry, velocity-level. The deck colliders are kinematic
        // children (players and cargo must never shove the hull), so PhysX sees a
        // STATIC surface: real friction can neither drag cargo along nor release it.
        // Instead the deck's true surface velocity (the hull rigidbody genuinely moves
        // now) is transmitted through a traction cap, and that cap IS the friction
        // model: within it cargo rides the deck perfectly; deck tilt or ship
        // acceleration beyond it leaves the crate behind — it slides. Everything is
        // velocity-space — no position/rotation teleports — so the contact solver is
        // never fought (the old per-step pose writes read as violent rattling) and
        // rigidbody interpolation stays smooth. The slick physics material keeps
        // PhysX's own wrong-direction static friction out of the way.
        private void CarryWithDeck()
        {
            Game.Ship.ShipController ship = null;
            Collider col = _grabbable.Col;
            Bounds b = col != null ? col.bounds : new Bounds(transform.position, Vector3.one * 0.5f);
            if (Physics.Raycast(b.center, Vector3.down, out RaycastHit hit, b.extents.y + 0.3f,
                    ~0, QueryTriggerInteraction.Ignore))
            {
                ship = hit.collider.GetComponentInParent<Game.Ship.ShipController>();
                // Stacked cargo: ride whatever the crate under us is riding.
                if (ship == null)
                {
                    var below = hit.collider.GetComponentInParent<CargoItem>();
                    if (below != null) ship = below.DeckShip;
                }
            }

            if (ship != _deckShip)
            {
                _deckShip = ship;
                _deckShipBody = ship != null ? ship.GetComponent<Rigidbody>() : null;
            }
            if (_deckShipBody == null) return;

            float dt = Time.fixedDeltaTime;
            Vector3 dv = _deckShipBody.GetPointVelocity(_body.worldCenterOfMass)
                       - _body.linearVelocity;

            // Horizontal: friction-limited transmission (the slide threshold).
            Vector3 dvH = Vector3.ClampMagnitude(new Vector3(dv.x, 0f, dv.z), carryTraction * dt);
            // Vertical: follow the heave generously so the rising deck never has to
            // depenetration-shove the crate — but capped, so a crate dropped from
            // above still falls onto the deck instead of freezing mid-air.
            float dvY = Mathf.Clamp(dv.y, -10f * dt, 10f * dt);
            _body.linearVelocity += new Vector3(dvH.x, dvY, dvH.z);

            // Turn with the ship (yaw only): a carving hull shouldn't spin the crate
            // relative to the planks it rests on.
            Vector3 av = _body.angularVelocity;
            av.y = Mathf.MoveTowards(av.y, _deckShipBody.angularVelocity.y, 3f * dt);
            _body.angularVelocity = av;
        }

        // Server: swap every collider between its authored material and the shared slick
        // one as the item boards/leaves a deck. Clients don't simulate these contacts
        // (their cargo bodies are kinematic under the network sync), so server-only is enough.
        private void SetDeckSlick(bool slick)
        {
            if (slick == _slick) return;
            _slick = slick;
            if (_slickShared == null)
                _slickShared = new PhysicsMaterial("Cargo_DeckSlick")
                {
                    staticFriction = 0.06f,
                    dynamicFriction = 0.05f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                    bounciness = 0f,
                    bounceCombine = PhysicsMaterialCombine.Minimum,
                };
            for (int i = 0; i < _cols.Length; i++)
                if (_cols[i] != null)
                    _cols[i].sharedMaterial = slick ? _slickShared : _dryMats[i];
        }
    }
}
