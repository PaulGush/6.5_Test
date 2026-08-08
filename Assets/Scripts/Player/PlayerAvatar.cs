using Mirror;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Game.Player
{
    /// <summary>
    /// Drives the visible character model with real animation clips (Mesh2Motion's CC0
    /// human library, humanoid-retargeted onto the Synty rig) blended through a
    /// hand-rolled Playables mixer — no AnimatorController asset.
    ///
    /// Locomotion is inferred on every client from parent-space position deltas (parent
    /// space so riding a moving ship reads as standing still); clip playback rates are
    /// scaled by actual speed to avoid foot sliding. State that cannot be inferred
    /// remotely (crouch blend, grounded, climbing, swimming) is quantized into two synced bytes by
    /// the server, which owns the simulation. Carrying a prop swaps the moving-clip to a
    /// carry walk (the held Grabbable is already synced by PlayerGrabber).
    ///
    /// For the local player the model is switched to shadows-only while the camera is
    /// first-person, so the head doesn't block the view but the shadow remains.
    /// </summary>
    [RequireComponent(typeof(PlayerController))]
    [DefaultExecutionOrder(40)] // after PlayerRockView (0) rewrites modelRoot, before PlayerHelmPose (60)
    public class PlayerAvatar : NetworkBehaviour
    {
        [Tooltip("Root of the nested character model (must carry the humanoid Animator).")]
        [SerializeField] private Transform modelRoot;

        [Header("Clips")]
        [SerializeField] private AnimationClip idle;
        [SerializeField] private AnimationClip walk;
        [SerializeField] private AnimationClip jog;
        [SerializeField] private AnimationClip sprint;
        [SerializeField] private AnimationClip walkBackwards;
        [SerializeField] private AnimationClip walkCarry;
        [SerializeField] private AnimationClip crouchIdle;
        [SerializeField] private AnimationClip crouchWalk;
        [SerializeField] private AnimationClip jumpStart;
        [SerializeField] private AnimationClip jumpAir;
        [SerializeField] private AnimationClip jumpLand;
        [SerializeField] private AnimationClip climb;
        [SerializeField] private AnimationClip climbIdle;
        [SerializeField] private AnimationClip swim;
        [SerializeField] private AnimationClip swimIdle;
        [SerializeField] private AnimationClip death;

        [Header("Clip nominal speeds (m/s the clip was authored at; playback is scaled by actual speed)")]
        [SerializeField] private float walkClipSpeed = 1.6f;
        [SerializeField] private float jogClipSpeed = 3.6f;
        [SerializeField] private float sprintClipSpeed = 6.5f;
        [SerializeField] private float crouchClipSpeed = 1.2f;
        [SerializeField] private float climbClipSpeed = 1.5f;
        [SerializeField] private float swimClipSpeed = 2f;

        [Tooltip("Visual-only lift of the model while stroking forward (m): the prone swim pose reads mostly submerged from third person without it. The capsule and camera never move.")]
        [SerializeField] private float swimForwardLift = 0.4f;
        [Tooltip("Visual-only lift of a dead body in water (m): the death pose lies at feet level, this floats it up to the surface.")]
        [SerializeField] private float deadFloatLift = 1.25f;

        // Mixer slots. Order is arbitrary but fixed; weights are recomputed every frame.
        private const int SlotIdle = 0, SlotWalk = 1, SlotJog = 2, SlotSprint = 3,
            SlotBack = 4, SlotCarry = 5, SlotCrouchIdle = 6, SlotCrouchWalk = 7,
            SlotJumpStart = 8, SlotJumpAir = 9, SlotJumpLand = 10,
            SlotClimb = 11, SlotClimbIdle = 12, SlotSwim = 13, SlotSwimIdle = 14,
            SlotDeath = 15, SlotCount = 16;

        // Server -> clients: visual state that cannot be inferred from the replicated
        // transform. bit0 = grounded, bit1 = climbing, bit2 = swimming, bit3 = dead;
        // crouch is the 0..1 blend in a byte.
        [SyncVar] private byte poseFlags = 1;
        [SyncVar] private byte crouchByte;

        private PlayerController _controller;
        private NetworkPlayer _networkPlayer;
        private Game.Gameplay.PlayerGrabber _grabber;
        private Renderer[] _renderers;

        private PlayableGraph _graph;
        private AnimationMixerPlayable _mixer;
        private readonly AnimationClipPlayable[] _playables = new AnimationClipPlayable[SlotCount];
        private readonly bool[] _hasClip = new bool[SlotCount];
        private readonly float[] _weights = new float[SlotCount];
        private readonly float[] _targets = new float[SlotCount];

        // Locomotion inferred from parent-space movement (all clients).
        private Vector3 _lastLocalPos;
        private bool _hasLastPos;
        private float _planarSpeed, _forwardSpeed, _verticalSpeed;
        private float _crouch, _airT, _climbT, _swimT, _deadT;
        private bool _wasDead;

        // Airborne sub-state: launch clip -> falling loop -> landing overlay.
        private enum AirPhase { None, Start, Air }
        private AirPhase _airPhase;
        private float _airTime, _landPulse;

        private void Awake()
        {
            _controller = GetComponent<PlayerController>();
            _networkPlayer = GetComponent<NetworkPlayer>();
            _grabber = GetComponent<Game.Gameplay.PlayerGrabber>();
            if (modelRoot == null)
            {
                Animator found = GetComponentInChildren<Animator>();
                if (found != null) modelRoot = found.transform;
            }

            Animator animator = modelRoot != null ? modelRoot.GetComponent<Animator>() : null;
            if (animator == null || idle == null)
            {
                Debug.LogWarning($"{name}: PlayerAvatar needs a model Animator and clips; disabling.", this);
                enabled = false;
                return;
            }

            _renderers = modelRoot.GetComponentsInChildren<Renderer>(true);
            BuildGraph(animator);
        }

        private void BuildGraph(Animator animator)
        {
            _graph = PlayableGraph.Create("PlayerAvatar");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            _mixer = AnimationMixerPlayable.Create(_graph, SlotCount);

            AnimationClip[] clips =
            {
                idle, walk, jog, sprint, walkBackwards, walkCarry, crouchIdle, crouchWalk,
                jumpStart, jumpAir, jumpLand, climb, climbIdle, swim, swimIdle, death,
            };
            for (int i = 0; i < SlotCount; i++)
            {
                if (clips[i] == null) continue; // slot simply never gets weight
                _hasClip[i] = true;
                _playables[i] = AnimationClipPlayable.Create(_graph, clips[i]);
                // Foot IK grounds the feet on grounded poses; airborne/climb/swim/death clips don't want it.
                bool grounded = i != SlotJumpStart && i != SlotJumpAir && i != SlotClimb && i != SlotClimbIdle
                    && i != SlotSwim && i != SlotSwimIdle && i != SlotDeath;
                _playables[i].SetApplyFootIK(grounded);
                _mixer.ConnectInput(i, _playables[i], 0, 0f);
            }

            var output = AnimationPlayableOutput.Create(_graph, "PlayerAvatar", animator);
            output.SetSourcePlayable(_mixer);
            _graph.Play();

            _weights[SlotIdle] = 1f;
            _mixer.SetInputWeight(SlotIdle, 1f);
        }

        public override void OnStartLocalPlayer()
        {
            ApplyFirstPerson(CameraRig.IsFirstPerson);
            CameraRig.FirstPersonChanged += ApplyFirstPerson;
        }

        private void OnDestroy()
        {
            CameraRig.FirstPersonChanged -= ApplyFirstPerson;
            if (_graph.IsValid()) _graph.Destroy();
        }

        // The local player's own model would sit inside the first-person camera, so keep
        // only its shadow in that mode. Remote players are never touched.
        private void ApplyFirstPerson(bool firstPerson)
        {
            foreach (Renderer r in _renderers)
                r.shadowCastingMode = firstPerson
                    ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly
                    : UnityEngine.Rendering.ShadowCastingMode.On;
        }

        // The server owns the simulation, so it publishes the pose state each frame.
        // SyncVars only ship when the value actually changes.
        [ServerCallback]
        private void Update()
        {
            poseFlags = (byte)((_controller.IsGrounded ? 1 : 0) | (_controller.IsClimbing ? 2 : 0)
                | (_controller.IsSwimming ? 4 : 0)
                | (_networkPlayer != null && _networkPlayer.IsDead ? 8 : 0));
            crouchByte = (byte)Mathf.RoundToInt(Mathf.Clamp01(_controller.CrouchBlend) * 255f);
        }

        private void LateUpdate()
        {
            if (isServerOnly) return; // headless: nobody sees the pose
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            MeasureMotion(dt);
            BlendState(dt);
            ComputeTargets();
            ApplyWeights(dt);

            // Visual-only: raise the model while stroking forward so the swimmer stays
            // visible from third person, and float a dead body up to the surface (the
            // death pose lies at feet level, 1.25m under the waterline). Both are gated
            // by the swim blend, so a land or deck death lies where it fell. Safe as a
            // += because PlayerRockView (execution order 0) rewrites modelRoot's
            // transform every frame before this runs.
            float lift = _swimT * Mathf.Clamp01(_planarSpeed / 0.7f) * swimForwardLift * (1f - _deadT)
                + _swimT * _deadT * deadFloatLift;
            if (lift > 0.0005f && modelRoot != null)
                modelRoot.position += Vector3.up * lift;
        }

        // Velocity from parent-space position deltas: works on every client off the
        // replicated transform, and standing on a moving (parented) ship reads as idle.
        private void MeasureMotion(float dt)
        {
            Vector3 local = transform.parent != null
                ? transform.parent.InverseTransformPoint(transform.position)
                : transform.position;
            Vector3 delta = local - _lastLocalPos;
            _lastLocalPos = local;
            if (!_hasLastPos) { _hasLastPos = true; return; }
            if (delta.magnitude > 2f) return; // teleport (respawn/boarding): keep previous speeds

            Vector3 world = transform.parent != null ? transform.parent.TransformDirection(delta / dt) : delta / dt;
            Vector3 vel = transform.InverseTransformDirection(world);
            float smooth = 1f - Mathf.Exp(-10f * dt);
            _planarSpeed = Mathf.Lerp(_planarSpeed, new Vector2(vel.x, vel.z).magnitude, smooth);
            _forwardSpeed = Mathf.Lerp(_forwardSpeed, vel.z, smooth);
            _verticalSpeed = Mathf.Lerp(_verticalSpeed, vel.y, smooth);
        }

        private void BlendState(float dt)
        {
            bool grounded = (poseFlags & 1) != 0;
            bool climbing = (poseFlags & 2) != 0;
            bool swimming = (poseFlags & 4) != 0;
            bool isDead = (poseFlags & 8) != 0;
            bool airborne = !grounded && !climbing && !swimming && !isDead;

            float smooth = 1f - Mathf.Exp(-8f * dt);
            _crouch = Mathf.Lerp(_crouch, crouchByte / 255f, smooth);
            _airT = Mathf.Lerp(_airT, airborne ? 1f : 0f, smooth);
            _climbT = Mathf.Lerp(_climbT, climbing ? 1f : 0f, smooth);
            _swimT = Mathf.Lerp(_swimT, swimming ? 1f : 0f, smooth);
            _deadT = Mathf.Lerp(_deadT, isDead ? 1f : 0f, smooth);

            // Death is a one-shot: restart the collapse the moment the flag arrives, in
            // any environment — ashore it crumples where it stood, aboard it rides the
            // rocking deck (PlayerRockView keeps composing), in water it floats up.
            if (isDead && !_wasDead && _hasClip[SlotDeath])
                _playables[SlotDeath].SetTime(0);
            _wasDead = isDead;

            // Airborne phases: a rising launch plays the jump clip once, then the falling
            // loop; touching down after real airtime fires a decaying landing overlay.
            if (airborne)
            {
                if (_airPhase == AirPhase.None)
                {
                    _airPhase = _verticalSpeed > 1f ? AirPhase.Start : AirPhase.Air;
                    _airTime = 0f;
                    if (_airPhase == AirPhase.Start && _hasClip[SlotJumpStart])
                        _playables[SlotJumpStart].SetTime(0);
                }
                _airTime += dt;
                if (_airPhase == AirPhase.Start && (_airTime > 0.5f || _verticalSpeed < 0f))
                    _airPhase = AirPhase.Air;
            }
            else
            {
                if (_airPhase != AirPhase.None && _airTime > 0.35f)
                {
                    // Landing softens with speed: sprinting through a landing barely shows it.
                    _landPulse = 1f - Mathf.Clamp01(_planarSpeed / 4f);
                    if (_hasClip[SlotJumpLand]) _playables[SlotJumpLand].SetTime(0);
                }
                _airPhase = AirPhase.None;
                _airTime = 0f;
            }
            _landPulse = Mathf.MoveTowards(_landPulse, 0f, dt / 0.7f);
        }

        private void ComputeTargets()
        {
            for (int i = 0; i < SlotCount; i++) _targets[i] = 0f;

            float deadW = _deadT;
            float swimW = _swimT;
            float climbW = _climbT * (1f - swimW);
            float airW = _airT * (1f - swimW - climbW);
            float groundW = Mathf.Clamp01(1f - swimW - climbW - airW);
            float crouchW = groundW * _crouch;
            float locoW = groundW - crouchW;

            // Swim: moving -> forward stroke rate-scaled by speed; still -> treading water.
            if (swimW > 0.001f)
            {
                float swim01 = Mathf.Clamp01(_planarSpeed / 0.7f);
                _targets[SlotSwim] = swimW * swim01;
                _targets[SlotSwimIdle] = swimW * (1f - swim01);
                SetSpeed(SlotSwim, Mathf.Clamp(_planarSpeed / swimClipSpeed, 0.7f, 1.5f));
            }

            // Climb: moving -> ladder cycle scrubbed by vertical speed (negative descends);
            // hanging still -> ladder idle.
            if (climbW > 0.001f)
            {
                float climbing01 = Mathf.Clamp01(Mathf.Abs(_verticalSpeed) / 0.4f);
                _targets[SlotClimb] = climbW * climbing01;
                _targets[SlotClimbIdle] = climbW * (1f - climbing01);
                SetSpeed(SlotClimb, Mathf.Clamp(_verticalSpeed / climbClipSpeed, -1.6f, 1.6f));
            }

            // Airborne: launch clip fades into the falling loop.
            if (airW > 0.001f)
            {
                bool starting = _airPhase == AirPhase.Start && _hasClip[SlotJumpStart];
                _targets[SlotJumpStart] = starting ? airW : 0f;
                _targets[SlotJumpAir] = starting ? 0f : airW;
                SetSpeed(SlotJumpStart, 1.5f); // authored launch is leisurely; tighten it
            }

            float moveT = Mathf.Clamp01(_planarSpeed / 1f); // 0 = standing, 1 = clearly moving

            // Crouched: idle <-> crouch-walk by speed.
            if (crouchW > 0.001f)
            {
                _targets[SlotCrouchIdle] = crouchW * (1f - moveT);
                _targets[SlotCrouchWalk] = crouchW * moveT;
                SetSpeed(SlotCrouchWalk, Mathf.Clamp(_planarSpeed / crouchClipSpeed, 0.6f, 1.6f));
            }

            // Grounded locomotion: idle plus a walk/jog/sprint mix picked by speed.
            // Moving backwards swaps in the backwards walk; carrying a prop swaps in the
            // carry walk. A recent landing overlays on top of whatever loco is doing.
            if (locoW > 0.001f)
            {
                float land = Mathf.Min(_landPulse, 0.85f) * locoW;
                float loco = locoW - land;
                _targets[SlotJumpLand] = land;
                _targets[SlotIdle] = loco * (1f - moveT);

                float moving = loco * moveT;
                bool backwards = _forwardSpeed < -0.2f && -_forwardSpeed > _planarSpeed * 0.6f;
                bool carrying = _grabber != null && _grabber.IsHolding;
                if (backwards && _hasClip[SlotBack])
                {
                    _targets[SlotBack] = moving;
                    SetSpeed(SlotBack, Mathf.Clamp(_planarSpeed / walkClipSpeed, 0.6f, 1.6f));
                }
                else if (carrying && _hasClip[SlotCarry])
                {
                    _targets[SlotCarry] = moving;
                    SetSpeed(SlotCarry, Mathf.Clamp(_planarSpeed / walkClipSpeed, 0.6f, 1.6f));
                }
                else
                {
                    float s = _planarSpeed;
                    float wWalk, wJog, wSprint;
                    if (s <= walkClipSpeed) { wWalk = 1f; wJog = 0f; wSprint = 0f; }
                    else if (s <= jogClipSpeed)
                    {
                        float f = (s - walkClipSpeed) / (jogClipSpeed - walkClipSpeed);
                        wWalk = 1f - f; wJog = f; wSprint = 0f;
                    }
                    else
                    {
                        float f = Mathf.Clamp01((s - jogClipSpeed) / (sprintClipSpeed - jogClipSpeed));
                        wWalk = 0f; wJog = 1f - f; wSprint = f;
                    }
                    _targets[SlotWalk] = moving * wWalk;
                    _targets[SlotJog] = moving * wJog;
                    _targets[SlotSprint] = moving * wSprint;
                    // Scale each cycle so the feet keep up with the actual velocity.
                    SetSpeed(SlotWalk, Mathf.Clamp(s / walkClipSpeed, 0.6f, 1.7f));
                    SetSpeed(SlotJog, Mathf.Clamp(s / jogClipSpeed, 0.7f, 1.5f));
                    SetSpeed(SlotSprint, Mathf.Clamp(s / sprintClipSpeed, 0.7f, 1.4f));
                }
            }

            // Death overrides everything: the one-shot collapse plays and clamps on its
            // final face-down pose (loopTime 0) until respawn fades it back out.
            if (deadW > 0.001f && _hasClip[SlotDeath])
            {
                for (int i = 0; i < SlotCount; i++) _targets[i] *= 1f - deadW;
                _targets[SlotDeath] = deadW;
            }
        }

        // Ease the live weights toward the targets (a universal ~0.1s crossfade), then
        // normalize so the mixer always sums to 1.
        private void ApplyWeights(float dt)
        {
            float smooth = 1f - Mathf.Exp(-12f * dt);
            float sum = 0f;
            for (int i = 0; i < SlotCount; i++)
            {
                if (!_hasClip[i]) { _weights[i] = 0f; continue; }
                _weights[i] = Mathf.Lerp(_weights[i], _targets[i], smooth);
                if (_weights[i] < 0.0005f) _weights[i] = 0f;
                sum += _weights[i];
            }
            if (sum <= 0f) { _weights[SlotIdle] = 1f; sum = 1f; }
            for (int i = 0; i < SlotCount; i++)
                if (_hasClip[i]) _mixer.SetInputWeight(i, _weights[i] / sum);
        }

        private void SetSpeed(int slot, float speed)
        {
            if (_hasClip[slot]) _playables[slot].SetSpeed(speed);
        }
    }
}
