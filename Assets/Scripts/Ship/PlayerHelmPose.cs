using UnityEngine;

namespace Game.Ship
{
    /// <summary>
    /// Visual helm stance: while this player steers, the model turns to face the wheel and
    /// the hands reach out and grip its rim (analytic two-bone arm IK on the humanoid
    /// bones, applied after animation). Purely cosmetic and computed on every client from
    /// the synced helm state — the body transform, camera and simulation are untouched, so
    /// look stays free while the model visibly works the wheel.
    /// </summary>
    [DefaultExecutionOrder(60)] // after PlayerAvatar (40) rebuilds modelRoot's base pose
    public class PlayerHelmPose : MonoBehaviour
    {
        [Tooltip("Where the hands grip, as an angle either side of the wheel's top (deg).")]
        [SerializeField] private float gripAngle = 35f;
        [Tooltip("Grip radius from the wheel hub (m); 0 = derive it from the wheel's meshes.")]
        [SerializeField] private float gripRadius = 0f;
        [Tooltip("How quickly the stance blends in/out (1/s).")]
        [SerializeField] private float blendRate = 5f;

        private PlayerHelmUser _helmUser;
        private Transform _model;
        private Transform _upperL, _lowerL, _handL, _upperR, _lowerR, _handR;
        private ShipHelm _helm; // held through the fade-out after letting go
        private float _autoRadius;
        private float _weight;

        private void Awake()
        {
            _helmUser = GetComponent<PlayerHelmUser>();
            Animator animator = GetComponentInChildren<Animator>();
            if (_helmUser == null || animator == null || !animator.isHuman)
            {
                enabled = false;
                return;
            }
            _model = animator.transform;
            _upperL = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            _lowerL = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            _handL = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            _upperR = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            _lowerR = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            _handR = animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (_upperL == null || _lowerL == null || _handL == null
                || _upperR == null || _lowerR == null || _handR == null)
                enabled = false;
        }

        private void LateUpdate()
        {
            ShipHelm current = _helmUser.CurrentHelm;
            if (current != null && current != _helm)
            {
                _helm = current;
                _autoRadius = MeasureWheel(_helm.WheelVisual);
            }

            float goal = current != null ? 1f : 0f;
            _weight = Mathf.Lerp(_weight, goal, 1f - Mathf.Exp(-blendRate * Time.deltaTime));
            if (_helm == null || _helm.WheelVisual == null || _weight < 0.001f)
            {
                _weight = 0f;
                return;
            }
            Transform wheel = _helm.WheelVisual;

            // Face the wheel: yaw the whole model about its feet, on top of the base pose
            // PlayerAvatar rebuilt this frame. The body transform (and with it the camera
            // pivot) stays free, so looking around while steering never twists the stance.
            Vector3 toWheel = wheel.position - transform.position;
            toWheel.y = 0f;
            if (toWheel.sqrMagnitude > 1e-4f)
            {
                float delta = Mathf.DeltaAngle(
                    transform.eulerAngles.y,
                    Mathf.Atan2(toWheel.x, toWheel.z) * Mathf.Rad2Deg);
                _model.rotation = Quaternion.AngleAxis(delta * _weight, Vector3.up) * _model.rotation;
            }

            // Grip points on the rim, either side of the top of the wheel. They deliberately
            // do not spin with it — a helmsman's hands ride the rim, they don't cartwheel.
            Vector3 axis = wheel.TransformDirection(_helm.WheelAxis).normalized;
            Vector3 rimUp = Vector3.ProjectOnPlane(Vector3.up, axis);
            if (rimUp.sqrMagnitude < 1e-4f) rimUp = Vector3.ProjectOnPlane(_model.forward, axis);
            rimUp.Normalize();
            float r = gripRadius > 0f ? gripRadius : _autoRadius;
            Vector3 gripA = wheel.position + Quaternion.AngleAxis(-gripAngle, axis) * (rimUp * r);
            Vector3 gripB = wheel.position + Quaternion.AngleAxis(gripAngle, axis) * (rimUp * r);

            // Hand the model's-left grip to the left hand, whichever side we stand on.
            bool swap = Vector3.Dot(gripA - gripB, _model.right) > 0f;
            SolveArm(_upperL, _lowerL, _handL, swap ? gripB : gripA, -1f);
            SolveArm(_upperR, _lowerR, _handR, swap ? gripA : gripB, 1f);
        }

        // Wheel grip radius from its meshes: a wheel's renderer bounds are dominated by its
        // diameter (handles included), so grip a little inside the outer edge.
        private static float MeasureWheel(Transform wheel)
        {
            if (wheel == null) return 0.45f;
            Renderer[] rs = wheel.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return 0.45f;
            Bounds b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            float outer = Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z));
            return Mathf.Clamp(outer * 0.8f, 0.25f, 0.8f);
        }

        // Analytic two-bone IK (the Animation Rigging solver's math): bend the elbow to fit
        // the reach, swing the shoulder so the hand lands on the grip, then swivel the elbow
        // down and outward so the raised arms read as a stance, not a zombie reach. Out-of-
        // reach grips degrade gracefully: the arm simply extends toward them.
        private void SolveArm(Transform upper, Transform lower, Transform hand, Vector3 grip, float side)
        {
            Vector3 target = Vector3.Lerp(hand.position, grip, _weight);

            Vector3 a = upper.position, b = lower.position, c = hand.position;
            float lab = Vector3.Distance(a, b);
            float lbc = Vector3.Distance(b, c);
            float lat = Mathf.Clamp(Vector3.Distance(a, target), 0.02f, lab + lbc - 0.01f);

            float oldElbow = TriangleAngle(Vector3.Distance(a, c), lab, lbc);
            float newElbow = TriangleAngle(lat, lab, lbc);
            Vector3 bendAxis = Vector3.Cross(b - a, c - b);
            if (bendAxis.sqrMagnitude < 1e-8f) bendAxis = Vector3.Cross(target - a, c - b);
            if (bendAxis.sqrMagnitude < 1e-8f) bendAxis = _model.right * side;
            lower.rotation = Quaternion.AngleAxis(oldElbow - newElbow, bendAxis.normalized) * lower.rotation;

            upper.rotation = Quaternion.FromToRotation(hand.position - a, target - a) * upper.rotation;

            // Elbow swivel about the shoulder→hand axis toward a natural hint (out and
            // down); the hand stays planted because it lies on that axis.
            Vector3 at = (target - a).normalized;
            Vector3 elbowDir = Vector3.ProjectOnPlane(lower.position - a, at);
            Vector3 hintDir = Vector3.ProjectOnPlane(_model.rotation * new Vector3(side * 0.6f, -0.8f, 0.1f), at);
            if (elbowDir.sqrMagnitude > 1e-6f && hintDir.sqrMagnitude > 1e-6f)
            {
                float swivel = Vector3.SignedAngle(elbowDir, hintDir, at);
                upper.rotation = Quaternion.AngleAxis(swivel * _weight, at) * upper.rotation;
            }
        }

        private static float TriangleAngle(float opposite, float l1, float l2) =>
            Mathf.Acos(Mathf.Clamp((l1 * l1 + l2 * l2 - opposite * opposite) / (2f * l1 * l2), -1f, 1f))
            * Mathf.Rad2Deg;
    }
}
