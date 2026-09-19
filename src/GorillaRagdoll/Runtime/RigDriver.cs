using System;
using System.Reflection;
using UnityEngine;

namespace GorillaRagdoll.Runtime
{
    /// <summary>
    /// Suspends VRRig's per-frame update without disabling the component.
    ///
    /// <para>This exists because <c>VRRig.enabled = false</c> is a trap. Its <c>OnDisable</c> is
    /// not a pause, it is a <b>teardown</b>: it nulls <c>netView</c>, <c>voiceAudio</c> and
    /// <c>creator</c>, clears <c>initialized</c> and <c>initializedCosmetics</c>, resets the skin
    /// and material, resets scale to one, and resets the render transform. <c>OnEnable</c>
    /// restores almost none of it - so every ragdoll permanently destroyed a little more rig
    /// state, which is why the avatar looked progressively worse with each cycle instead of
    /// failing outright.</para>
    ///
    /// <para>What actually needs stopping is far smaller: <c>VRRig.PostTick()</c>, which writes
    /// the rig transform and maps the head and hand IK targets from tracking. It runs from
    /// <c>TickSystem&lt;object&gt;</c>, so removing that one callback stops exactly the thing
    /// that fights us and touches nothing else. Both the type and its interface are internal,
    /// hence reflection.</para>
    /// </summary>
    internal static class TickBridge
    {
        private static MethodInfo _add, _remove;
        private static bool _probed, _usable;

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;

            try
            {
                var open = typeof(VRRig).Assembly.GetType("TickSystem`1", false);
                if (open != null)
                {
                    var closed = open.MakeGenericType(typeof(object));
                    _add = closed.GetMethod("AddPostTickCallback", BindingFlags.Public | BindingFlags.Static);
                    _remove = closed.GetMethod("RemovePostTickCallback", BindingFlags.Public | BindingFlags.Static);
                    _usable = _add != null && _remove != null;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Tick] could not reach TickSystem: " + ex.Message);
            }

            if (_usable) Plugin.Log.LogInfo("[Tick] suspending VRRig via TickSystem post-tick callback");
            else Plugin.Log.LogWarning("[Tick] TickSystem unavailable - falling back to disabling VRRig, " +
                                       "which resets some rig state each time. Repeated ragdolls may " +
                                       "look progressively wrong.");
        }

        public static bool Suspend(VRRig rig)
        {
            Probe();
            if (!_usable || rig == null) return false;
            try { _remove.Invoke(null, new object[] { rig }); return true; }
            catch (Exception ex) { Plugin.Log.LogWarning("[Tick] suspend failed: " + ex.Message); return false; }
        }

        public static void Resume(VRRig rig)
        {
            Probe();
            if (!_usable || rig == null) return;
            try { _add.Invoke(null, new object[] { rig }); }
            catch (Exception ex) { Plugin.Log.LogWarning("[Tick] resume failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// Poses Gorilla Tag's own avatar from the physics puppet.
    ///
    /// <para>GT's arm bones are IK <i>output</i> - <c>GorillaIK</c> solves
    /// <c>upper_arm</c>/<c>forearm</c> to span shoulder-to-hand from a hand target. Simulating
    /// those bones overwrites the solver and the arm stops connecting to anything. The reverse
    /// works beautifully: stop VRRig writing tracking data, hand the IK targets our physics
    /// poses, and <b>the game draws the ragdoll for us</b>. Every pose is anatomically valid by
    /// construction, because it is the same code that draws you normally.</para>
    ///
    /// <para>Restoring is <i>mostly</i> self-correcting: <c>PostTick</c> writes the rig's
    /// position and rotation from live tracking every frame
    /// (<c>SetPositionAndRotation(syncPos, GTPlayerTransform.BodyRotation)</c>) and re-maps the
    /// IK targets, so letting it run again puts most of this right on its own.</para>
    ///
    /// <para><b>The head target is the exception, and it has to be restored by hand.</b>
    /// <c>VRMap.MapMine</c> only writes its <c>rigTarget</c> if <c>overrideTarget</c> is set, or
    /// failing that if the XR device reports <c>devicePosition</c> and <c>deviceRotation</c>.
    /// The hands have an <c>overrideTarget</c> (<c>LeftHandGorilla</c> / <c>RightHandGorilla</c>)
    /// so they are rewritten every frame and need nothing. The head's is <c>null</c>, and the
    /// device read does not come back on this headset - so <c>VR Constraints/Head Constraint</c>
    /// is <b>never written at all</b>. Measured over a whole session its local pose never moves
    /// off <c>lp=(0, 1.6582, -0.0114) lr=(7.61, 0, 0)</c>: it is a fixed rest offset, and the
    /// head tracks because its <i>parent</i> follows the headset while the IK job copies its
    /// world rotation onto the head bone.</para>
    ///
    /// <para>So <c>Tick</c> writing <c>head.rigTarget.rotation</c> is a permanent, destructive
    /// edit. Nothing puts it back, every collapse bakes in the corpse's final head angle, and
    /// the next collapse builds its puppet from the already-wrong bone - which is why the head
    /// hung off the torso with the hat and glasses still attached to it, and why it got worse
    /// with every cycle. The earlier diagnostic missed it by one field: it recorded the head
    /// target's local <i>position</i>, which this never damages, and not its rotation.</para>
    /// </summary>
    public sealed class RigDriver
    {
        /// <summary>
        /// Drawing someone else's ragdoll rather than your own.
        ///
        /// <para>The posing is identical; three things are not, and each would do real damage on
        /// a rig that is not yours. Cosmetic re-anchoring reads <i>your</i>
        /// <c>GTPlayer.CosmeticsHeadTarget</c>, so on a remote rig it would lift your own hat off
        /// your head and weld it to theirs. Suspending <c>PostTick</c> would stop the game's
        /// appearance and hand-link upkeep for a player whose network state is still live. And
        /// the face layer swap exists only because you cannot normally see your own face; other
        /// people's faces are already visible.</para>
        /// </summary>
        private readonly bool _remote;

        public RigDriver(bool remote = false)
        {
            _remote = remote;
        }

        private VRRig _rig;
        private bool _suspended;
        private bool _disabledFallback;
        private readonly CosmeticReanchor _cosmetics = new CosmeticReanchor();
        private int _faceLayer = -1;
        private Transform _face;

        // Recorded purely to prove whether anything drifts between cycles.
        private Vector3 _beginScale;

        /// <summary>
        /// The head target's rest pose, which nothing in the game restores.
        ///
        /// <para>This is not a snapshot of live tracking - it is a constant. See the class
        /// remarks: <c>Head Constraint</c> is never written by <c>VRMap.MapMine</c> on this
        /// rig, so putting these two values back is exact rather than approximate. Both are
        /// kept even though only rotation is damaged today, because the get-up blend writes
        /// position through <c>SetPositionAndRotation</c> as well.</para>
        /// </summary>
        private Vector3 _headRestLocalPos;
        private Quaternion _headRestLocalRot;
        private bool _headRestValid;

        // The pose root's pose relative to the body bone, captured once. Placing it from this
        // each frame is a rigid assignment rather than an accumulating correction.
        private Vector3 _rigOffsetPos;
        private Quaternion _rigOffsetRot;

        /// <summary>
        /// The transform we actually move: the skeleton root, not the VRRig root.
        ///
        /// <para>The main camera is a <b>child of the VRRig object</b> - the same fact that made
        /// head cosmetics look like they were "already on the rig". So driving the VRRig
        /// transform drags the headset with it, welding your view to the corpse's orientation:
        /// you can turn your head all you like and the world turns with you. Posing the skeleton
        /// root instead moves the avatar and leaves the camera, a sibling, completely free.</para>
        ///
        /// <para>It is the same lever GT itself pulls to show a rig somewhere it is not (VRRig's
        /// <c>renderTransform</c>, used by displacement zones) - and that is the catch. VRRig
        /// only puts it back inside <c>if (!isLocal ...)</c> in <c>PostTick</c>, so on your own
        /// avatar <b>nothing restores it</b>. Moving it is fine; forgetting to move it back
        /// strands the entire visible body wherever the ragdoll ended while you walk away from
        /// it. That is what the probe caught at 3.7m.</para>
        /// </summary>
        private Transform _poseRoot;

        /// <summary>
        /// Where <see cref="_poseRoot"/> rests, captured at collapse.
        ///
        /// <para>Position <i>and</i> rotation. An earlier version kept only the rotation, on the
        /// assumption - true for the VRRig root, false for this - that <c>PostTick</c> would put
        /// the position back. For the skeleton root the rest pose is a constant
        /// (<c>cachedRenderTransformPos</c>, <c>(0, -1.65, 0)</c> here), so restoring it is
        /// exact.</para>
        /// </summary>
        private Vector3 _poseRootRestPos;
        private Quaternion _poseRootRestRot;

        /// <summary>True when the pose root is the skeleton, which nothing else will restore;
        /// false when it fell back to the VRRig root, which <c>PostTick</c> drives.</summary>
        private bool _poseRootIsSkeleton;

        public bool Active => _rig != null;

        public void Begin(VRRig rig, Pose? cameraWhenRigLastPlaced)
        {
            if (rig == null) return;

            // Collapsing again during a get-up blend must not be a no-op, or the rig keeps its
            // ragdoll pose and the cosmetics stay stuck on the bones.
            if (_rig != null) End();

            _rig = rig;

            // Cosmetics first: this reads the avatar while it is still standing where the
            // camera is, before anything below moves it.
            if (!_remote) _cosmetics.Begin(rig, cameraWhenRigLastPlaced);

            _beginScale = rig.transform.localScale;

            // Captured before Tick has had a chance to overwrite it. Collapsing during a get-up
            // blend re-enters here, but End() above has already put the rest pose back, so what
            // is read is the real one rather than a corpse angle carried over.
            _headRestValid = rig.head != null && rig.head.rigTarget != null;
            if (_headRestValid)
                rig.head.rigTarget.GetLocalPositionAndRotation(out _headRestLocalPos, out _headRestLocalRot);

            // Skeleton root if we can find it, VRRig root only as a fallback. Its rest pose is
            // recorded before Tick moves it, because for the skeleton nobody else remembers it.
            var skeleton = RigBones.Find(rig.transform, "rig");
            _poseRootIsSkeleton = skeleton != null;
            _poseRoot = skeleton ?? rig.transform;
            _poseRoot.GetLocalPositionAndRotation(out _poseRootRestPos, out _poseRootRestRot);

            // Where the pose root sits relative to the body bone. Constant for the session, so
            // it can be planted rigidly from the puppet every frame.

            var body = RigBones.Find(rig.transform, RagdollPlan.Torso);
            if (body != null)
            {
                _rigOffsetPos = body.InverseTransformPoint(_poseRoot.position);
                _rigOffsetRot = Quaternion.Inverse(body.rotation) * _poseRoot.rotation;
            }
            else
            {
                _rigOffsetPos = Vector3.zero;
                _rigOffsetRot = Quaternion.identity;
            }

            // Remote rigs are posed after the game's own network write each frame instead (see
            // IkHook), so there is nothing to suspend - and nothing to swap on the face.
            if (_remote) return;

            _suspended = TickBridge.Suspend(rig);
            if (!_suspended)
            {
                // Last resort. Destructive, but better than the avatar fighting us every frame.
                _disabledFallback = true;
                rig.enabled = false;
            }

            // The face normally renders to MirrorOnly, because you are not meant to see your own
            // from outside. While ragdolled you are looking at yourself, so it has to be visible.
            try
            {
                _face = rig.transform.Find("rig/head/gorillaface");
                if (_face != null)
                {
                    _faceLayer = _face.gameObject.layer;
                    _face.gameObject.layer = LayerMask.NameToLayer("Default");
                }
            }
            catch { /* cosmetic only */ }
        }

        /// <summary>
        /// Moves the whole rig so its torso bone lands on the puppet's, then hands the head and
        /// hands to the IK.
        ///
        /// <para>The body is matched by <i>delta</i> rather than assignment: the rig's root and
        /// its <c>body</c> bone are 1.65m apart (VRRig's renderTransform offset), so writing the
        /// puppet position straight onto the root would bury the gorilla in the floor. Measuring
        /// the offset each frame is exact and needs no knowledge of the rig's internals.</para>
        /// </summary>
        public void Tick(IPoseSource puppet)
        {
            if (_rig == null || puppet == null || puppet.Body == null) return;

            // Rigid assignment, not an accumulating correction. The previous version nudged
            // the rig by a delta every frame, which converges but drifts and is sensitive to
            // ordering; planting it from a fixed offset is exact and repeatable.
            if (_poseRoot == null) return;
            _poseRoot.SetPositionAndRotation(
                puppet.Body.TransformPoint(_rigOffsetPos),
                puppet.Body.rotation * _rigOffsetRot);

            // Then just feed the IK. Its Burst job is the thing that actually poses the arms,
            // head and hands - it is the mechanism, not an obstacle, and fighting it by writing
            // bones directly only takes the arms away from the solver that knows how to bend
            // them.
            SetTarget(_rig.leftHand, puppet.HandL);
            SetTarget(_rig.rightHand, puppet.HandR);

            // Rotation only for the head: the job writes head rotation from this target but
            // never its position, so writing position here achieves nothing.
            if (_rig.head != null && _rig.head.rigTarget != null && puppet.Head != null)
                _rig.head.rigTarget.rotation = puppet.Head.rotation;
        }

        private static void SetTarget(VRMap map, Transform from)
        {
            if (map == null || map.rigTarget == null || from == null) return;
            map.rigTarget.SetPositionAndRotation(from.position, from.rotation);
        }

        // ------------------------------------------------------------------ get-up blend

        private bool _blending;
        private float _blendT, _blendSeconds;
        private Vector3 _snapPos, _snapLeftPos, _snapRightPos;
        private Quaternion _snapRot, _snapLeftRot, _snapRightRot;

        // The skeleton root blends in local space towards its rest pose - see TickBlend.
        private Vector3 _snapRootLocalPos;
        private Quaternion _snapRootLocalRot;

        // The head blends in local space towards its rest pose, not in world space towards a
        // live target, because it has no live target to converge on.
        private Vector3 _snapHeadLocalPos;
        private Quaternion _snapHeadLocalRot;

        public bool Blending => _blending;

        /// <summary>
        /// Hands control back gradually instead of snapping upright.
        ///
        /// <para>VRRig's update is resumed immediately, so from this moment it writes the live
        /// tracked pose every frame. <see cref="TickBlend"/> then runs <i>after</i> it and eases
        /// from the snapshot taken here towards whatever the game just wrote - so for the rig
        /// and the hands the target is always the real current pose, and tracking stays
        /// responsive throughout.</para>
        ///
        /// <para>The head cannot work that way, because nothing writes its target (see the class
        /// remarks): easing towards "what the game just wrote" would be easing towards the
        /// corpse angle this is trying to leave. It eases towards the recorded rest pose in
        /// local space instead, which is the pose the head genuinely has when upright.</para>
        /// </summary>
        public void BeginBlendOut(float seconds)
        {
            if (_rig == null) return;

            ResumeRig();

            if (seconds <= 0.02f) { End(); return; }

            var poseRoot = _poseRoot != null ? _poseRoot : _rig.transform;
            _snapPos = poseRoot.position;
            _snapRot = poseRoot.rotation;
            poseRoot.GetLocalPositionAndRotation(out _snapRootLocalPos, out _snapRootLocalRot);
            Snapshot(_rig.leftHand, ref _snapLeftPos, ref _snapLeftRot);
            Snapshot(_rig.rightHand, ref _snapRightPos, ref _snapRightRot);

            if (_rig.head != null && _rig.head.rigTarget != null)
                _rig.head.rigTarget.GetLocalPositionAndRotation(out _snapHeadLocalPos, out _snapHeadLocalRot);

            _blending = true;
            _blendT = 0f;
            _blendSeconds = seconds;
        }

        private static void Snapshot(VRMap map, ref Vector3 pos, ref Quaternion rot)
        {
            if (map == null || map.rigTarget == null) return;
            pos = map.rigTarget.position;
            rot = map.rigTarget.rotation;
        }

        /// <summary>Advances the blend. Cleans itself up and returns true when finished.</summary>
        public bool TickBlend()
        {
            if (!_blending) return true;
            if (_rig == null) { _blending = false; End(); return true; }

            _blendT += Time.deltaTime / _blendSeconds;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_blendT));

            var poseRoot = _poseRoot != null ? _poseRoot : _rig.transform;
            if (_poseRootIsSkeleton)
            {
                // PostTick never writes the skeleton root of a local rig, so "ease towards what
                // the game just wrote" would ease towards the corpse position forever. Its real
                // destination is the recorded rest pose, in its parent's space - which PostTick
                // IS moving back to you, so this converges on your live position too.
                poseRoot.SetLocalPositionAndRotation(
                    Vector3.Lerp(_snapRootLocalPos, _poseRootRestPos, t),
                    Quaternion.Slerp(_snapRootLocalRot, _poseRootRestRot, t));
            }
            else
            {
                // The VRRig root fallback: PostTick has already written this frame's live pose,
                // so the blend converges on tracking rather than on a recorded guess.
                poseRoot.position = Vector3.Lerp(_snapPos, poseRoot.position, t);
                poseRoot.rotation = Quaternion.Slerp(_snapRot, poseRoot.rotation, t);
            }

            BlendTarget(_rig.leftHand, _snapLeftPos, _snapLeftRot, t);
            BlendTarget(_rig.rightHand, _snapRightPos, _snapRightRot, t);

            if (_headRestValid && _rig.head != null && _rig.head.rigTarget != null)
                _rig.head.rigTarget.SetLocalPositionAndRotation(
                    Vector3.Lerp(_snapHeadLocalPos, _headRestLocalPos, t),
                    Quaternion.Slerp(_snapHeadLocalRot, _headRestLocalRot, t));

            if (_blendT < 1f) return false;

            _blending = false;
            End();
            return true;
        }

        private static void BlendTarget(VRMap map, Vector3 fromPos, Quaternion fromRot, float t)
        {
            if (map == null || map.rigTarget == null) return;
            map.rigTarget.SetPositionAndRotation(
                Vector3.Lerp(fromPos, map.rigTarget.position, t),
                Quaternion.Slerp(fromRot, map.rigTarget.rotation, t));
        }

        private void ResumeRig()
        {
            if (_rig == null) return;

            if (_suspended)
            {
                TickBridge.Resume(_rig);
                _suspended = false;
            }
            if (_disabledFallback)
            {
                _rig.enabled = true;
                _disabledFallback = false;
            }
        }

        public void End()
        {
            if (_rig == null) return;

            _blending = false;
            ResumeRig();
            _cosmetics.End();
            if (_face != null && _faceLayer >= 0) _face.gameObject.layer = _faceLayer;

            // The head target, back to the rest pose nothing else will restore. This is the
            // whole fix for the detached head and the cosmetics that followed it.
            string headWas = "<none>";
            if (_headRestValid && _rig.head != null && _rig.head.rigTarget != null)
            {
                headWas = _rig.head.rigTarget.localRotation.eulerAngles.ToString("0.0");
                _rig.head.rigTarget.SetLocalPositionAndRotation(_headRestLocalPos, _headRestLocalRot);
            }
            _headRestValid = false;

            // And the pose root, whole. Tick moved it every frame on purpose; PostTick will not
            // put the skeleton root back for a local rig, so this is the only thing that does.
            string rootWas = "<none>";
            if (_poseRoot != null)
            {
                float drift = (_poseRoot.localPosition - _poseRootRestPos).magnitude;
                rootWas = drift.ToString("0.00") + "m out";
                _poseRoot.SetLocalPositionAndRotation(_poseRootRestPos, _poseRootRestRot);
            }

            var endScale = _rig.transform.localScale;
            Plugin.Log.LogInfo((_remote ? "[Net] remote " : "") + "[Restore] scale " + _beginScale.ToString("0.000") + " -> " + endScale.ToString("0.000") +
                               " | head.rigTarget.localRot " + headWas + " -> " + _headRestLocalRot.eulerAngles.ToString("0.0") +
                               " | pose root '" + (_poseRoot != null ? _poseRoot.name : "<none>") +
                               "' restored from " + rootWas);

            _rig = null;
            _poseRoot = null;
            _face = null;
            _faceLayer = -1;
        }
    }
}
