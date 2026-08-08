using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// Client-side water-death ragdoll. A death on land or deck keeps the authored
    /// collapse animation, but a corpse in the sea looks stiff riding a clamped pose —
    /// so when this player is dead with its pivot under the animated surface, the local
    /// view disables the Animator and converts the humanoid skeleton into a physics
    /// ragdoll (built from the Animator's humanoid bone map, no bone names hardcoded).
    /// Every part gets buoyancy against <see cref="Game.Gameplay.WaterSurface.HeightAt"/>
    /// plus water drag, so the limbs sprawl and settle at the surface with the swell.
    ///
    /// Purely cosmetic and per-client: each client simulates its own ragdoll while the
    /// networked root keeps everyone's corpse in the same place. Torn down on respawn —
    /// re-enabling the Animator lets the humanoid pose overwrite every bone again.
    /// </summary>
    public class PlayerRagdollView : MonoBehaviour
    {
        [Tooltip("Pivot submersion (m) that switches a death to the ragdoll instead of the collapse animation.")]
        [SerializeField] private float waterDepthThreshold = 0.05f;
        [Tooltip("Upward acceleration at full submersion (m/s²); above gravity so parts float.")]
        [SerializeField] private float buoyancyAccel = 16f;
        [Tooltip("Linear damping while ragdolled (water drag).")]
        [SerializeField] private float waterLinearDamping = 2.8f;
        [Tooltip("Angular damping while ragdolled.")]
        [SerializeField] private float waterAngularDamping = 2f;

        private NetworkPlayer _player;
        private Animator _animator;
        private Game.Gameplay.WaterSurface _water;
        private float _nextWaterScan;

        private readonly List<Rigidbody> _parts = new List<Rigidbody>();
        private readonly List<Component> _cleanup = new List<Component>(); // destroy order: joints, bodies, colliders
        private bool _active;

        private void Awake()
        {
            _player = GetComponent<NetworkPlayer>();
            _animator = GetComponentInChildren<Animator>();
        }

        private void Update()
        {
            if (NetworkServer.active && !NetworkClient.active) return; // headless server: nobody sees it

            if (_water == null && Time.time >= _nextWaterScan)
            {
                _nextWaterScan = Time.time + 1f;
                _water = FindAnyObjectByType<Game.Gameplay.WaterSurface>();
            }

            bool dead = _player != null && _player.IsDead;
            if (!_active && dead && Submerged())
                Build();
            else if (_active && !dead)
                Teardown();
            // Once ragdolled, stay ragdolled for the whole death even if depth drifts.
        }

        private bool Submerged()
        {
            if (_water == null) return false;
            Vector3 p = transform.position;
            return _water.HeightAt(p.x, p.z) - p.y > waterDepthThreshold;
        }

        private void FixedUpdate()
        {
            if (!_active || _water == null) return;
            foreach (Rigidbody rb in _parts)
            {
                if (rb == null) continue;
                Vector3 p = rb.worldCenterOfMass;
                float depth = _water.HeightAt(p.x, p.z) - p.y;
                if (depth > 0f)
                {
                    // Ramp buoyancy in over the first ~35cm of submersion, per limb, so
                    // deep parts rise hard and surfaced parts just kiss the waterline.
                    float f = Mathf.Clamp01(depth / 0.35f);
                    rb.AddForce(Vector3.up * (buoyancyAccel * f * rb.mass), ForceMode.Force);
                }
            }
        }

        // ---------- build / teardown ----------

        private void Build()
        {
            if (_animator == null || !_animator.isHuman) return;
            Transform hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null) return;

            // Freeze the playables output where it is; physics takes the pose from here.
            _animator.enabled = false;

            // Existing colliders (CharacterController etc.) must never fight the ragdoll.
            Collider[] existing = GetComponentsInChildren<Collider>(true);

            Transform chest = Bone(HumanBodyBones.Chest) ?? Bone(HumanBodyBones.Spine);
            Transform head = Bone(HumanBodyBones.Head);

            var made = new List<Collider>();
            Rigidbody hipsRb = AddPart(hips, null, chest != null ? chest.position : hips.position + Vector3.up * 0.3f, 0.15f, 12f, made);
            Rigidbody chestRb = chest != null && head != null
                ? AddPart(chest, hipsRb, head.position, 0.14f, 10f, made) : hipsRb;
            if (head != null)
                AddPart(head, chestRb, head.position + head.up * 0.2f, 0.11f, 5f, made);

            AddLimb(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, chestRb, 0.055f, 2.5f, 2f, made);
            AddLimb(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, chestRb, 0.055f, 2.5f, 2f, made);
            AddLimb(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, hipsRb, 0.07f, 6f, 4f, made);
            AddLimb(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, hipsRb, 0.07f, 6f, 4f, made);

            foreach (Collider rag in made)
                foreach (Collider col in existing)
                    if (col != null) Physics.IgnoreCollision(rag, col);

            _active = true;
        }

        private Transform Bone(HumanBodyBones bone) => _animator.GetBoneTransform(bone);

        // Upper + lower segment chained onto the torso (upper->lower->end give the capsule spans).
        private void AddLimb(HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones end,
            Rigidbody parent, float radius, float upperMass, float lowerMass, List<Collider> made)
        {
            Transform u = Bone(upper), l = Bone(lower), e = Bone(end);
            if (u == null || l == null) return;
            Rigidbody upperRb = AddPart(u, parent, l.position, radius, upperMass, made);
            if (e != null)
                AddPart(l, upperRb, e.position, radius * 0.85f, lowerMass, made);
        }

        // One ragdoll part: capsule along bone->end, rigidbody with water drag, and a
        // CharacterJoint onto the parent part (default limits are fine for a corpse).
        private Rigidbody AddPart(Transform bone, Rigidbody parent, Vector3 endWorld,
            float radius, float mass, List<Collider> made)
        {
            Vector3 localEnd = bone.InverseTransformPoint(endWorld);
            var capsule = bone.gameObject.AddComponent<CapsuleCollider>();
            int axis = 0;
            if (Mathf.Abs(localEnd.y) > Mathf.Abs(localEnd.x)) axis = 1;
            if (Mathf.Abs(localEnd.z) > Mathf.Abs(localEnd[axis])) axis = 2;
            capsule.direction = axis;
            capsule.center = localEnd * 0.5f;
            capsule.radius = radius;
            capsule.height = localEnd.magnitude + radius;
            made.Add(capsule);

            var rb = bone.gameObject.AddComponent<Rigidbody>();
            rb.mass = mass;
            rb.linearDamping = waterLinearDamping;
            rb.angularDamping = waterAngularDamping;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            if (parent != null)
            {
                var joint = bone.gameObject.AddComponent<CharacterJoint>();
                joint.connectedBody = parent;
                joint.enableProjection = true;
                _cleanup.Insert(0, joint); // joints destroyed before the bodies they connect
            }

            _parts.Add(rb);
            _cleanup.Add(rb);
            _cleanup.Add(capsule);
            return rb;
        }

        private void Teardown()
        {
            foreach (Component c in _cleanup)
                if (c != null) Destroy(c);
            _cleanup.Clear();
            _parts.Clear();
            if (_animator != null) _animator.enabled = true; // the humanoid pose reclaims every bone
            _active = false;
        }
    }
}
