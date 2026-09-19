using GorillaLocomotion;
using GorillaRagdoll.Cameras;
using GorillaRagdoll.Config;
using UnityEngine;

namespace GorillaRagdoll.Runtime
{
    /// <summary>
    /// The state machine: collapse, simulate, get up.
    ///
    /// <para>Two objects do the work. <see cref="RagdollPuppet"/> is an invisible jointed
    /// skeleton that falls over; <see cref="RigDriver"/> switches GT's <c>VRRig</c> off and
    /// poses the real avatar from the puppet's head and hands, letting the game's own IK draw
    /// the gorilla. Nothing is cloned and no renderer is hidden - the thing you see is your
    /// actual avatar the whole time.</para>
    ///
    /// <para>Ordering matters: the rig is driven in LateUpdate, after physics has settled the
    /// frame and after GT has finished whatever it does in Update.</para>
    /// </summary>
    public sealed class RagdollController : MonoBehaviour
    {
        public bool IsRagdolled { get; private set; }

        /// <summary>The live ragdoll's poses, for sharing with the room; null when standing.</summary>
        public IPoseSource Pose => IsRagdolled ? _puppet : null;

        /// <summary>The multiplayer side, for the overlay to report on.</summary>
        public GorillaRagdoll.Net.RagdollNet Network { get; set; }

        /// <summary>Last thing that went wrong or was refused, for the overlay to show.</summary>
        public string Status { get; private set; } = "ready";

        public int BoneCount => _puppet != null ? _puppet.NodeCount : 0;
        public MonitorCamera Monitor => _monitor;
        public float Tension => _puppet != null ? _puppet.ActiveTension : 0f;
        public bool MenuOpen { get; set; }

        private RagdollPuppet _puppet;
        private readonly RigDriver _driver = new RigDriver();
        private readonly PlayerSuspension _suspend = new PlayerSuspension();
        private readonly MonitorCamera _monitor = new MonitorCamera();
        private readonly VrView _vr = new VrView();

        /// <summary>How far down to look for a floor before calling the body airborne.</summary>
        private const float AirborneCheckReach = 4f;

        private float _startedAt;
        private Vector3 _rootAtCollapse;
        private bool _diagPending;

        /// <summary>
        /// Where the headset was when the game last posed the avatar from it.
        ///
        /// <para><c>VRRig.PostTick</c> runs in LateUpdate, so for the whole of the next Update -
        /// which is when a collapse is triggered - the avatar corresponds to this pose rather
        /// than to where the headset is right now. Anything that has to relate a camera-parented
        /// object to a rig-parented one needs this, not the live camera.</para>
        /// </summary>
        private Pose? _camAtLastRigSync;

        private float _nextWatchdog;
        private bool _reportedTilt;

        /// <summary>The skeleton root's resting local position, learned the first time the
        /// watchdog sees a healthy idle avatar.</summary>
        private Vector3? _skeletonRest;

        /// <summary>When to take the settled-state probe after getting up, or 0 for none. The
        /// interesting snapshot is not the one taken the instant the driver lets go - it is the
        /// one a second later, once VRRig and the IK have both had frames to put things back.
        /// Whatever still differs from the baseline then is genuinely left broken.</summary>
        private float _probeAt;

        // ------------------------------------------------------------------ public API

        public void Toggle()
        {
            if (IsRagdolled) GetUp();
            else Collapse();
        }

        public void Collapse()
        {
            if (IsRagdolled) return;

            var blocked = Venue.BlockedReason;
            if (blocked != null)
            {
                Status = blocked;
                Plugin.Log.LogWarning("[Ragdoll] refused: " + blocked);
                return;
            }

            var rig = VRRig.LocalRig;
            if (rig == null || GTPlayer.Instance == null)
            {
                Status = "player not ready";
                return;
            }

            RigProbe.Capture("pre-collapse");

            _puppet = RagdollPuppet.Build(rig);
            if (_puppet == null) { Status = "could not build puppet - see log"; return; }

            _suspend.Begin();

            // Taken from the hold rather than read back off the player: the hold zeroes the
            // rigidbody, so anything asking GTPlayer for its velocity after this point gets
            // zero. That ordering is why ragdolling at speed used to stop you dead.
            Vector3 forward = GTPlayer.Instance.mainCamera != null
                ? GTPlayer.Instance.mainCamera.transform.forward
                : Vector3.forward;
            _puppet.Seed(_suspend.CollapseVelocity, forward);

            _driver.Begin(rig, _camAtLastRigSync);
            _vr.Begin();
            if (RagdollConfig.MonitorMode.Value != CameraMode.Off) _monitor.Ensure();

            _startedAt = Time.time;
            _rootAtCollapse = _puppet.Body != null ? _puppet.Body.position : Vector3.zero;
            _diagPending = true;
            IsRagdolled = true;
            Status = "ragdolled (" + _puppet.NodeCount + " nodes)";
            Plugin.Log.LogInfo("[Ragdoll] collapse | " + Status + " | " + Venue.Describe());
        }

        public void GetUp()
        {
            if (!IsRagdolled) return;

            var torso = _puppet != null && _puppet.Body != null
                ? _puppet.Body.position
                : _suspend.CollapsePosition;

            // Read before the puppet is destroyed a few lines down.
            Vector3 torsoVelocity = _puppet != null ? _puppet.TorsoVelocity : Vector3.zero;

            // Is there actually a floor under the body, or is it still in the air? Everything
            // below turns on this. Placing you "just above the ground" when the search found no
            // ground is what stood you up hanging in mid-air, stopped dead, half way through a
            // fall. The reach is generous because a body in flight can be a long way up.
            Vector3 ground;
            bool grounded = PlayerSuspension.TryGroundUnder(torso, out ground, AirborneCheckReach);

            // Finding no ground is not the same as being in the air, and conflating the two was
            // a mistake: the search only looks for surfaces GT lets you walk on, so a body at
            // rest on a prop, a table or any non-walkable layer also comes back empty. Treating
            // that as a fall skipped the clearance test and stood you up floating. A body is
            // only genuinely airborne if it is also still moving.
            bool falling = torsoVelocity.sqrMagnitude > 1f;   // ~1 m/s

            Vector3 stand;
            Vector3 exitVelocity = Vector3.zero;

            if (RagdollConfig.BodyFollow.Value == BodyFollowMode.StayPut)
                stand = _suspend.CollapsePosition;
            else if (!grounded && falling)
            {
                // Airborne: stand up exactly where the body is and carry its velocity, so the
                // fall simply continues as if you had never gone limp.
                stand = torso;
                exitVelocity = torsoVelocity;
            }
            else if (RagdollConfig.GetUp.Value == GetUpMode.Respawn)
                stand = PlayerSuspension.GetUpPoint(torso, RagdollConfig.GetUpHeight.Value,
                                                    _suspend.CollapsePosition);
            else
                stand = ground;

            // Everything except the driver goes now; the driver stays to ease the avatar back
            // to live tracking over the next fraction of a second.
            _vr.End();
            _monitor.Destroy();
            if (_puppet != null) { _puppet.Destroy(); _puppet = null; }

            _suspend.End(stand, exitVelocity);

            // Respawn is deliberately a hard reset: no blend, nothing left half-restored. The
            // easing only exists for InPlace, where you are meant to come round where you fell.
            if (RagdollConfig.GetUp.Value == GetUpMode.Respawn) _driver.End();
            else _driver.BeginBlendOut(RagdollConfig.GetUpBlend.Value);

            IsRagdolled = false;
            Status = "ready";
            Plugin.Log.LogInfo("[Ragdoll] get up at " + stand.ToString("0.00") +
                               (exitVelocity.sqrMagnitude > 0.01f
                                   ? " (airborne, carrying " + exitVelocity.magnitude.ToString("0.0") + " m/s)"
                                   : grounded ? " (on ground)" : " (no walkable ground, placed by clearance)"));

            RigProbe.Capture("post-getup");
            _probeAt = Time.time + 1.5f;
        }

        /// <summary>Unconditional restore. The one path that must never leave you stuck.</summary>
        public void Panic()
        {
            if (!IsRagdolled)
            {
                Teardown();
                _suspend.End(GTPlayer.Instance != null ? GTPlayer.Instance.transform.position : Vector3.zero,
                             Vector3.zero);
                return;
            }
            Plugin.Log.LogWarning("[Ragdoll] panic restore");
            GetUp();
            _driver.End();   // no easing on the escape hatch; put everything back now
        }

        public void Rebuild()
        {
            if (!IsRagdolled) return;
            GetUp();
            Collapse();
        }

        public void DumpSkeleton()
        {
            var rig = VRRig.LocalRig;
            if (rig == null) { Plugin.Log.LogWarning("[Dump] no rig"); return; }
            Plugin.Log.LogInfo("[Dump] avatar rig:" + System.Environment.NewLine + SkeletonDump.Describe(rig));
            RigProbe.Capture("manual-dump", full: true);
        }

        public void ApplyLiveTuning()
        {
            if (_puppet != null) _puppet.ApplyLiveTuning();
        }

        /// <summary>
        /// Order matters on the way out: the driver has to hand VRRig back <i>before</i> the
        /// puppet is destroyed, or the rig spends a frame chasing a dead transform.
        /// </summary>
        private void Teardown()
        {
            _vr.End();
            _monitor.Destroy();
            _driver.End();
            if (_puppet != null) { _puppet.Destroy(); _puppet = null; }
        }

        // ------------------------------------------------------------------ unity loop

        private void Update()
        {
            if (_probeAt > 0f && Time.time >= _probeAt)
            {
                _probeAt = 0f;
                // Collapsing again inside the settle window would have this report the live
                // ragdoll as if it were the restored state - which it did, and the reading was
                // worthless. Only the genuinely idle sample is worth anything.
                if (!IsRagdolled) RigProbe.Capture("settled-after-getup");
            }

            if (!IsRagdolled) { Watchdog(); return; }

            float limit = RagdollConfig.AutoGetUpSeconds.Value;
            if (limit > 0f && Time.time - _startedAt >= limit)
            {
                Plugin.Log.LogInfo("[Ragdoll] auto get-up after " + limit.ToString("0.#") + "s");
                GetUp();
            }
        }

        private void FixedUpdate()
        {
            if (IsRagdolled && _puppet != null) _puppet.ApplyExtraGravity();
        }

        private void LateUpdate()
        {
            RecordRigSync();

            if (!IsRagdolled)
            {
                // Runs after VRRig has written this frame's live pose, so the blend eases
                // towards a genuinely current target.
                if (_driver.Blending) _driver.TickBlend();
                return;
            }

            if (_puppet == null || _puppet.Root == null)
            {
                Plugin.Log.LogWarning("[Ragdoll] lost the puppet (scene change?) - restoring");
                GetUp();
                return;
            }

            // A puppet that finds a hole in the map would otherwise fall forever, taking the
            // headset view with it in first person.
            if (_puppet.Body != null &&
                _puppet.Body.position.y < _suspend.CollapsePosition.y - 200f)
            {
                Plugin.Log.LogWarning("[Ragdoll] fell out of the world - restoring at collapse point");
                Teardown();
                _suspend.End(_suspend.CollapsePosition, Vector3.zero);
                IsRagdolled = false;
                Status = "fell out of world";
                return;
            }

            if (_diagPending && Time.time - _startedAt >= 1f)
            {
                _diagPending = false;
                Plugin.Log.LogInfo("physics state 1s after collapse:" + System.Environment.NewLine +
                                   _puppet.Describe(_rootAtCollapse, Time.time - _startedAt));
            }

            _suspend.Assert();

            // Active ragdoll first: it decides whether the body is limp or tensing, which
            // affects whether it is allowed to settle at all.
            _puppet.UpdateActive();

            // Stop the endless micro-drift once it has actually come to rest.
            _puppet.Settle();

            // Draw the avatar from the puppet, then point the cameras at where it ended up.
            _driver.Tick(_puppet);

            var head = _puppet.Head;
            var centroid = _puppet.Centroid();

            var headToView = _puppet.HeadToView;

            _vr.Tick(RagdollConfig.VrMode.Value, head, headToView, centroid);

            var monitorMode = RagdollConfig.MonitorMode.Value;
            if (monitorMode == CameraMode.Off)
            {
                if (_monitor.Alive) _monitor.Destroy();
            }
            else
            {
                _monitor.Ensure();
                _monitor.Tick(monitorMode, head, headToView, MenuOpen);
            }

            if (RagdollConfig.VrMode.Value == CameraMode.Off &&
                RagdollConfig.BodyFollow.Value == BodyFollowMode.FollowRagdoll &&
                _puppet.Body != null && GTPlayer.Instance != null)
            {
                GTPlayer.Instance.transform.position =
                    PlayerSuspension.GroundUnder(_puppet.Body.position);
            }
        }

        /// <summary>
        /// Notes the camera pose the avatar currently corresponds to.
        ///
        /// <para>Only while <c>PostTick</c> is actually running, because that is the thing that
        /// ties the two together - while the ragdoll is driving the rig they are unrelated, and
        /// recording then would poison the next collapse.</para>
        /// </summary>
        /// <summary>
        /// Catches the two ways an idle avatar can still be wrong, because both are silent.
        ///
        /// <para>A rig whose <c>PostTick</c> never got resumed is stranded wherever the ragdoll
        /// left it while the player walks away underneath - the mod suspended it, so the mod
        /// repairs it. A player left tilted relative to GT's own idea of up is only reported,
        /// not corrected: gravity zones rotate the player legitimately, and quietly levelling
        /// someone who is standing on a wall would be worse than the bug.</para>
        /// </summary>
        private void Watchdog()
        {
            if (_driver.Blending || Time.time < _nextWatchdog) return;
            _nextWatchdog = Time.time + 1f;

            VRRig rig = null;
            try { rig = VRRig.LocalRig; } catch { }
            if (rig == null) return;

            if (!rig.PostTickRunning)
            {
                Plugin.Log.LogError("[Watchdog] VRRig.PostTick was left suspended while idle - " +
                                    "the avatar would be stranded where the ragdoll ended. Resuming.");
                TickBridge.Resume(rig);
                RigProbe.Capture("watchdog-posttick");
                return;
            }

            // The skeleton root has no business moving while nothing is ragdolling, and if it
            // does the whole body is stranded somewhere you are not.
            var skeleton = RigBones.Find(rig.transform, "rig");
            if (skeleton != null && !_skeletonRest.HasValue)
                _skeletonRest = skeleton.localPosition;
            if (skeleton != null && _skeletonRest.HasValue &&
                (skeleton.localPosition - _skeletonRest.Value).magnitude > 0.02f)
            {
                Plugin.Log.LogError("[Watchdog] the skeleton root is " +
                                    (skeleton.localPosition - _skeletonRest.Value).magnitude.ToString("0.00") +
                                    "m off its rest pose while idle - the avatar is stranded. Putting it back.");
                skeleton.localPosition = _skeletonRest.Value;
                RigProbe.Capture("watchdog-skeleton");
                return;
            }

            var p = GTPlayer.Instance;
            if (p == null) return;

            Vector3 up;
            try { up = GTPlayerTransform.Up; }
            catch { up = Vector3.up; }

            float tilt = Vector3.Angle(p.transform.up, up);
            if (tilt <= 10f) { _reportedTilt = false; return; }

            if (_reportedTilt) return;
            _reportedTilt = true;
            Plugin.Log.LogWarning("[Watchdog] player is " + tilt.ToString("0.0") +
                                  " deg off GT's up axis while idle. Not correcting it - gravity " +
                                  "zones do this legitimately - but here is the state:");
            RigProbe.Capture("watchdog-tilt");
        }

        private void RecordRigSync()
        {
            var p = GTPlayer.Instance;
            if (p == null || p.mainCamera == null) return;

            VRRig rig = null;
            try { rig = VRRig.LocalRig; } catch { }
            if (rig == null || !rig.PostTickRunning) return;

            var cam = p.mainCamera.transform;
            _camAtLastRigSync = new Pose(cam.position, cam.rotation);
        }

        private void OnDestroy() => Teardown();
    }
}
