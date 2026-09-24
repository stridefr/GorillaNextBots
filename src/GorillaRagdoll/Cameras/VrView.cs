using GorillaLocomotion;
using GorillaRagdoll.Config;
using GorillaRagdoll.Runtime;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

namespace GorillaRagdoll.Cameras
{
    /// <summary>
    /// Drives what you see in the headset while ragdolled.
    ///
    /// <para>There is no camera object to move here. The HMD's local pose belongs to XR
    /// tracking, so the only way to relocate the in-headset view is to move the rig the camera
    /// hangs off - which is exactly what GT's own locomotion does. Everything below therefore
    /// works by translating and rotating the player rig <i>about the current eye position</i>,
    /// via <see cref="PlayerSuspension"/>.</para>
    ///
    /// <para>One consequence worth being explicit about: the player rig is also your networked
    /// position. In the first-person modes that is exactly right - your body is where your
    /// ragdoll is. In VR third person it is not: other players see you where the camera is,
    /// floating behind your own ragdoll. That is the honest cost of putting the headset
    /// somewhere the body is not, and it is why the monitor gets its own camera instead.</para>
    ///
    /// <para>Comfort is treated as a real constraint, not a footnote. Roll is off by default
    /// and pitch is clamped, because a tumbling ragdoll driving a VR horizon is genuinely
    /// unpleasant rather than merely disorienting.</para>
    /// </summary>
    public sealed class VrView
    {
        private bool _armed;

        // Orbit state, driven by the thumbsticks.
        private float _orbitYaw;
        private float _orbitPitch;
        private float _orbitDistance;

        /// <summary>The distance actually used, after walls. Pulled in quickly and let back out slowly,
        /// both damped: a cast grazing a doorway flips between hit and clear from one frame to the
        /// next, and applied raw that is the camera snapping in and out.</summary>
        private float _easedDist = -1f;
        private float _distVel;

        /// <summary>The point the orbit circles, damped towards the body's anchor. Only this and the
        /// distance are smoothed; the stick's yaw and pitch are applied exactly, so turning can never
        /// fight a lagging position.</summary>
        private Vector3 _pivot;
        private Vector3 _pivotVel;

        /// <summary>Wall probe for the headset. Not the monitor's <c>OcclusionRadius</c>: that one is
        /// tuned for a flat screen and, set large, has the headset bumping into everything nearby.</summary>
        private const float HeadsetProbe = 0.12f;

        /// <summary>Where the orbit is centred: the body, but only once it has moved further than
        /// <c>VrFollowDeadzone</c> from here.</summary>
        private Vector3 _anchor;
        private bool _hasAnchor;

        /// <summary>Snap orbit: the stick has to come back to centre before it clicks again, the
        /// same rule GT's own snap turn uses.</summary>
        private bool _snapReset = true;
        private float _snapAt;

        private readonly TurnPause _turnPause = new TurnPause();

        // Rotation watch: the view's rotation as this class last left it. Anything that differs
        // at the start of the next frame was turned by someone else.
        private Quaternion _lastPlayerRot, _lastTurnRot;
        private bool _watching;
        private bool _reportedForeignTurn;

        /// <summary>Called on collapse: starts the orbit behind you, looking at the body.</summary>
        public void Begin()
        {
            _armed = true;
            PlayerSuspension.CaptureNeutralHead();
            _orbitPitch = 15f;
            _orbitDistance = RagdollConfig.ThirdPersonDistance.Value;
            _easedDist = -1f;
            _distVel = 0f;
            _pivotVel = Vector3.zero;
            _hasAnchor = false;
            _snapReset = true;
            _watching = false;
            _reportedForeignTurn = false;

            // Start from where you were already facing, so the camera does not swing round the
            // moment you collapse.
            var p = GTPlayer.Instance;
            _orbitYaw = 0f;
            if (p != null && p.mainCamera != null)
            {
                var f = p.mainCamera.transform.forward;
                f.y = 0f;
                if (f.sqrMagnitude > 0.01f)
                    _orbitYaw = Quaternion.LookRotation(f.normalized, Vector3.up).eulerAngles.y;
            }
        }

        public void End()
        {
            _armed = false;
            _watching = false;
            _turnPause.Release();
        }

        public void Tick(CameraMode mode, Transform head, Quaternion headToView, Vector3 centroid)
        {
            // The right stick belongs to the orbit while it is on screen, so GT's own turn is held
            // off for exactly that long and no longer.
            if (_armed && mode == CameraMode.ThirdPersonFree) _turnPause.Hold();
            else _turnPause.Release();

            if (!_armed || mode == CameraMode.Off) return;

            var p = GTPlayer.Instance;
            if (p == null || p.mainCamera == null || head == null) return;

            // The locked mode turns the rig itself, so a comparison spanning a switch out of the
            // orbit and back would blame someone else for it.
            if (mode == CameraMode.ThirdPersonFree) CheckForeignTurn(p);
            else _watching = false;

            float smooth = RagdollConfig.CameraSmoothing.Value;
            float k = smooth <= 0.001f ? 1f : 1f - Mathf.Exp(-Time.deltaTime / smooth);

            switch (mode)
            {
                // No position lag in either first-person mode: this is meant to feel like your own
                // eyes, and CameraSmoothing exists for a camera chasing a body from outside, not for
                // where your own view sits. A lagging position is also how a reanchored face cosmetic
                // ends up "in the way" - it sits exactly where the ragdoll's head bone is *this*
                // frame, and a smoothed eye a moment behind that point is looking at it edge-on or
                // from inside it rather than past it. Rotation smoothing (k, locked mode only) is
                // untouched - that one is comfort, not lag.
                case CameraMode.FirstPersonUnlocked:
                    PlayerSuspension.PlaceRigForEye(head.TransformPoint(RagdollConfig.EyeOffset.Value), 0f);
                    break;

                case CameraMode.FirstPersonLocked:
                    PlayerSuspension.PlaceRigForEye(head.TransformPoint(RagdollConfig.EyeOffset.Value), 0f);
                    ApplyLockedRotation(head, headToView, k);
                    break;

                case CameraMode.ThirdPersonFree:
                    Orbit(centroid, smooth, k);
                    break;
            }

            if (mode == CameraMode.ThirdPersonFree) RememberRotation(p);
        }

        /// <summary>
        /// Thumbstick orbit camera: fly around your own body and watch it fall.
        ///
        /// <para><b>Your head is never touched.</b> This only ever decides where you <i>stand</i>
        /// - the rig's position - and turns you only when you push the stick. Where you look is
        /// entirely your head, exactly as in normal play: look up and you see the sky. Nothing
        /// here aims you at the body.</para>
        ///
        /// <para>Right stick swings you around the body and raises or lowers the view; left
        /// stick pushes in and out. The yaw is applied as a rotation <b>about the body</b>
        /// rather than about your eye, so a stick orbit moves you around it and keeps it in
        /// front of you as one motion. GT's own snap/smooth turn reads the same stick axis and
        /// would otherwise fire at the same time - a second, unrelated rotation about your head
        /// on every push - so it is paused for the duration via its <c>ISnapTurnOverride</c>
        /// hook.</para>
        ///
        /// <para><b>It follows the body lazily.</b> A ragdoll never quite stops: it rocks,
        /// settles, slides a few centimetres. Chasing every one of those moves your whole world
        /// in the headset, which is precisely what seasickness is. So the orbit centre only moves
        /// once the body is more than <c>VrFollowDeadzone</c> away from it, and then only far
        /// enough to bring it back to that edge. A real flight is followed; a wobble is not.</para>
        ///
        /// <para>Input comes from GT's own poller rather than Unity XR. That matters here:
        /// <c>ControllerInputPoller</c> fills these axes from <c>SteamVR_Actions</c> on OpenVR
        /// and from <c>InputDevices</c> on Oculus, so reading it works on both, whereas going
        /// to Unity XR directly is unreliable on a Quest over SteamVR.</para>
        /// </summary>
        private void Orbit(Vector3 centroid, float smooth, float k)
        {
            float dt = Time.deltaTime;
            Vector2 look = Stick(right: true);
            Vector2 move = Stick(right: false);

            if (!_hasAnchor)
            {
                _anchor = centroid;
                _pivot = centroid + Vector3.up * RagdollConfig.ThirdPersonHeight.Value;
                _hasAnchor = true;
            }
            Vector3 drift = centroid - _anchor;
            float driftLen = drift.magnitude;
            float dead = RagdollConfig.VrFollowDeadzone.Value;
            if (driftLen > dead) _anchor += drift * ((driftLen - dead) / driftLen);

            Vector3 goal = _anchor + Vector3.up * RagdollConfig.ThirdPersonHeight.Value;
            _pivot = smooth > 0.001f
                ? Vector3.SmoothDamp(_pivot, goal, ref _pivotVel, smooth, Mathf.Infinity, dt)
                : goal;
            Vector3 target = _pivot;

            // Yaw is turned about the eye itself, so turning never slides the view sideways. Turning
            // about the rig's origin did: in room-scale the origin can be a metre or more from your
            // head, so each turn swung the eye across and the placement below then pulled it back.
            float yawDelta = OrbitYawInput(look.x, dt);
            if (Mathf.Abs(yawDelta) > 0.0001f)
            {
                _orbitYaw += yawDelta;
                PlayerSuspension.YawAboutNeutralEye(yawDelta);
            }

            float pitchInput = RagdollConfig.VrOrbitInvertPitch.Value ? -look.y : look.y;
            _orbitPitch = Mathf.Clamp(_orbitPitch + pitchInput * RagdollConfig.VrOrbitPitchSpeed.Value * dt,
                                      -25f, 80f);

            _orbitDistance = Mathf.Clamp(_orbitDistance - move.y * RagdollConfig.VrOrbitZoomSpeed.Value * dt,
                                         RagdollConfig.ThirdPersonMinDistance.Value, 30f);

            Vector3 dir = Quaternion.Euler(_orbitPitch, _orbitYaw, 0f) * Vector3.back;

            // Never let scenery end up between you and the body, and keep the headset out of walls.
            float dist = _orbitDistance;
            int mask;
            try { mask = GTPlayer.LocomotionEnabledLayers; }
            catch { mask = ~0; }

            RaycastHit hit;
            if (Physics.SphereCast(target, HeadsetProbe, dir, out hit, dist, mask, QueryTriggerInteraction.Ignore))
                dist = Mathf.Max(0.3f, hit.distance);

            if (_easedDist < 0f) _easedDist = dist;
            float ease = dist < _easedDist ? 0.06f : 0.4f;
            _easedDist = Mathf.SmoothDamp(_easedDist, dist, ref _distVel, ease, Mathf.Infinity, dt);

            PlayerSuspension.PlaceRigForEye(target + dir * _easedDist, 0f);
        }

        /// <summary>
        /// Degrees to swing round the body this frame. Snap mode clicks a fixed angle per push
        /// and needs the stick to return to centre (or be held for half a second) before the
        /// next; smooth turns continuously. <c>MatchGame</c> reads the player's own GT setting.
        /// </summary>
        private float OrbitYawInput(float x, float dt)
        {
            if (!UseSnap())
                return x * RagdollConfig.VrOrbitYawSpeed.Value * dt;

            const float press = 0.6f, release = 0.3f, repeat = 0.5f;
            if (Mathf.Abs(x) < release) { _snapReset = true; return 0f; }
            if (Mathf.Abs(x) < press) return 0f;
            if (!_snapReset && Time.time - _snapAt < repeat) return 0f;

            _snapReset = false;
            _snapAt = Time.time;
            return Mathf.Sign(x) * RagdollConfig.VrOrbitSnapAngle.Value;
        }

        private static bool UseSnap()
        {
            switch (RagdollConfig.VrOrbitTurning.Value)
            {
                case OrbitTurning.Snap: return true;
                case OrbitTurning.Smooth: return false;
                default:
                    try
                    {
                        var snap = GorillaSnapTurn.CachedSnapTurnRef;
                        return snap != null && snap.turnType == "SNAP";
                    }
                    catch { return false; }
            }
        }

        // ------------------------------------------------------------------ rotation watch

        /// <summary>
        /// Reports, once per ragdoll, any rotation of the view that this class did not make.
        ///
        /// <para>The orbit never turns you except on the stick, so if the view turns anyway,
        /// something else did it - and "the camera keeps swinging me round" is exactly the kind
        /// of report that costs a debugging cycle per guess. Both levers GT turns you with are
        /// watched: the player root, and <c>turnParent</c>, which is what snap turn rotates.</para>
        /// </summary>
        private void CheckForeignTurn(GTPlayer p)
        {
            var turn = p.turnParent != null ? p.turnParent.transform : null;
            if (!_watching || _reportedForeignTurn || turn == null) return;

            float rootDelta = Quaternion.Angle(_lastPlayerRot, p.transform.rotation);
            float turnDelta = Quaternion.Angle(_lastTurnRot, turn.rotation) - rootDelta;
            if (rootDelta < 1f && Mathf.Abs(turnDelta) < 1f) return;

            _reportedForeignTurn = true;
            Plugin.Log.LogWarning("[VrView] something other than the ragdoll turned your view while orbiting: " +
                                  "player root " + rootDelta.ToString("0.0") + " deg, turnParent " +
                                  turnDelta.ToString("0.0") + " deg. Snap turn is paused, so this is " +
                                  "another mod or a game system.");
        }

        private void RememberRotation(GTPlayer p)
        {
            var turn = p.turnParent != null ? p.turnParent.transform : null;
            if (turn == null) return;
            _lastPlayerRot = p.transform.rotation;
            _lastTurnRot = turn.rotation;
            _watching = true;
        }

        /// <summary>
        /// GT's own hook for pausing snap/smooth turn. Registered with <c>GorillaSnapTurn</c>
        /// while the orbit owns the right stick.
        ///
        /// <para>Always answers true while registered, and is removed explicitly rather than by
        /// answering false: GT prunes inactive overriders by removing from the set it is
        /// iterating over, which throws. An overrider that switches itself off would crash GT's
        /// turn tick.</para>
        /// </summary>
        private sealed class TurnPause : ISnapTurnOverride
        {
            private bool _held;

            public bool TurnOverrideActive() => true;

            public void Hold()
            {
                if (_held) return;
                try
                {
                    var snap = GorillaSnapTurn.CachedSnapTurnRef;
                    if (snap == null) return;
                    snap.SetTurningOverride(this);
                    _held = true;
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning("[VrView] could not pause snap turn: " + ex.Message);
                    _held = true;   // do not retry every frame
                }
            }

            public void Release()
            {
                if (!_held) return;
                _held = false;
                try
                {
                    var snap = GorillaSnapTurn.CachedSnapTurnRef;
                    if (snap != null) snap.UnsetTurningOverride(this);
                }
                catch { /* shutting down */ }
            }
        }

        /// <summary>
        /// One thumbstick, deadzoned and rescaled so the usable travel still reaches full speed.
        /// </summary>
        private static Vector2 Stick(bool right)
        {
            var poller = ControllerInputPoller.instance;
            if (poller == null) return Vector2.zero;

            Vector2 raw = right ? poller.rightControllerPrimary2DAxis
                                : poller.leftControllerPrimary2DAxis;

            float dead = RagdollConfig.VrOrbitDeadzone.Value;
            float mag = raw.magnitude;
            if (mag <= dead) return Vector2.zero;

            return raw.normalized * Mathf.Clamp01((mag - dead) / Mathf.Max(0.001f, 1f - dead));
        }

        /// <summary>
        /// Makes the view ride the ragdoll's head. Yaw is applied in full; tilt is filtered
        /// through the comfort settings before it ever reaches the rig.
        /// </summary>
        private static void ApplyLockedRotation(Transform head, Quaternion headToView, float k)
        {
            var p = GTPlayer.Instance;
            var cam = p.mainCamera.transform;
            var headRot = head.rotation * headToView *
                          Quaternion.Euler(RagdollConfig.HeadRotationOffset.Value);

            // ---- yaw: turn the rig until the view faces where the ragdoll's head faces
            Vector3 fwd = headRot * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 1e-4f)
            {
                float desired = Quaternion.LookRotation(fwd.normalized, Vector3.up).eulerAngles.y;
                float delta = Mathf.DeltaAngle(cam.eulerAngles.y, desired);
                PlayerSuspension.RotateAroundEye(delta * k);
            }

            // ---- tilt: how far the ragdoll's head is from upright, clamped for comfort
            Vector3 headUp = headRot * Vector3.up;
            float target = Vector3.Angle(Vector3.up, headUp);
            if (RagdollConfig.VrClampPitch.Value)
                target = Mathf.Min(target, RagdollConfig.VrMaxPitch.Value);

            Vector3 axis = Vector3.Cross(Vector3.up, headUp);
            if (axis.sqrMagnitude < 1e-4f) return;
            axis.Normalize();

            if (!RagdollConfig.VrAllowRoll.Value)
            {
                // Keep only the component that pitches the view; drop anything that would
                // roll the horizon.
                Vector3 right = p.transform.right;
                float along = Vector3.Dot(axis, right);
                if (Mathf.Abs(along) < 1e-3f) return;
                axis = right * Mathf.Sign(along);
                target *= Mathf.Abs(along);
            }

            float current = Vector3.Angle(Vector3.up, p.transform.up);
            PlayerSuspension.TiltAroundEye(axis, (target - current) * k);
        }
    }
}
