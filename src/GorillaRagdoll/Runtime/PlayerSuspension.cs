using GorillaLocomotion;
using UnityEngine;

namespace GorillaRagdoll.Runtime
{
    /// <summary>
    /// Puts GT's normal locomotion on hold while the ragdoll simulates, and puts it back
    /// afterwards.
    ///
    /// <para>Deliberately does <i>not</i> disable the <c>GTPlayer</c> component. Plenty of
    /// other systems read its state every frame (VRRig reads grounded flags, cosmetics read
    /// scale), and switching the whole thing off invites a class of bug that only shows up
    /// three systems away. Instead it uses the two levers GT already provides for exactly
    /// this: <c>disableMovement</c>, which the locomotion path checks, and a kinematic
    /// rigidbody, which GT itself sets during teleports.</para>
    ///
    /// <para>Both are re-asserted every frame, because the game is free to write them too.</para>
    /// </summary>
    public sealed class PlayerSuspension
    {
        private bool _active;
        private bool _prevDisableMovement;
        private bool _prevKinematic;
        private Vector3 _collapsePosition;
        private Vector3 _collapseVelocity;
        private Quaternion _collapseRotation = Quaternion.identity;

        public bool Active => _active;

        /// <summary>Where the player stood when they collapsed. Used by StayPut get-up.</summary>
        public Vector3 CollapsePosition => _collapsePosition;

        /// <summary>
        /// How fast the player was moving at the instant of collapse.
        ///
        /// <para>Captured here rather than read back by the caller, because <b>this method is
        /// what destroys it</b>: zeroing the rigidbody is the first thing the hold does. The
        /// controller used to call <c>Begin()</c> and then read
        /// <c>GTPlayer.RigidbodyVelocity</c>, which is just <c>playerRigidBody.linearVelocity</c>
        /// - so it always read exactly zero, and the ragdoll always dropped from a dead stop no
        /// matter what <c>InheritPlayerVelocity</c> was set to. Owning the capture here means
        /// the ordering cannot come apart again.</para>
        /// </summary>
        public Vector3 CollapseVelocity => _collapseVelocity;

        public void Begin()
        {
            var p = GTPlayer.Instance;
            if (p == null || _active) return;

            _prevDisableMovement = p.disableMovement;
            _prevKinematic = p.playerRigidBody != null && p.playerRigidBody.isKinematic;
            _collapsePosition = p.transform.position;
            _collapseVelocity = ReadVelocity(p);
            _collapseRotation = p.transform.rotation;

            p.disableMovement = true;
            if (p.playerRigidBody != null)
            {
                p.playerRigidBody.linearVelocity = Vector3.zero;
                p.playerRigidBody.angularVelocity = Vector3.zero;
                p.playerRigidBody.isKinematic = true;
            }
            _active = true;
        }

        /// <summary>
        /// The player's world velocity, before any of this interferes with it.
        ///
        /// <para>The rigidbody is the truth while you are running, jumping or falling. It reads
        /// zero in the states where GT moves you some other way - mid-climb, on a handhold - so
        /// the body velocity tracker is the fallback, because it measures actual displacement
        /// and does not care how the movement was produced.</para>
        /// </summary>
        private static Vector3 ReadVelocity(GTPlayer p)
        {
            Vector3 v = Vector3.zero;
            try { if (p.playerRigidBody != null) v = p.playerRigidBody.linearVelocity; } catch { }
            if (v.sqrMagnitude > 0.01f) return v;

            try
            {
                if (p.bodyVelocityTracker != null)
                    v = p.bodyVelocityTracker.GetAverageVelocity(worldSpace: true);
            }
            catch { /* the tracker is a bonus, not a requirement */ }
            return v;
        }

        /// <summary>Keep the hold in force. GT can and does write these fields itself.</summary>
        public void Assert()
        {
            if (!_active) return;
            var p = GTPlayer.Instance;
            if (p == null) return;
            p.disableMovement = true;
            if (p.playerRigidBody != null && !p.playerRigidBody.isKinematic)
                p.playerRigidBody.isKinematic = true;
        }

        /// <summary>
        /// Hand control back and stand the player up at <paramref name="standPoint"/>.
        ///
        /// Uses GT's own <c>TeleportTo</c> rather than writing the transform, because that is
        /// what clears hand holds, resyncs the body collider and breaks hand links. Skipping it
        /// leaves you stuck to whatever you were climbing when you collapsed.
        /// </summary>
        public void End(Vector3 standPoint, Vector3 exitVelocity)
        {
            if (!_active) return;
            var p = GTPlayer.Instance;
            _active = false;
            if (p == null) return;

            if (p.playerRigidBody != null) p.playerRigidBody.isKinematic = _prevKinematic;
            p.disableMovement = _prevDisableMovement;

            // Worked out before the teleport so the same value is used whether or not it
            // succeeds. The old code derived it inline and only applied it through TeleportTo,
            // so any throw in there - ClearHandHolds on a rig that collapsed mid-climb, say -
            // left the player standing at the right place still wearing the corpse's tilt.
            var upright = UprightFacing(p);

            try { p.TeleportTo(standPoint, upright); }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[Suspend] TeleportTo failed, placing directly: " + ex.Message);
                p.transform.SetPositionAndRotation(standPoint, upright);
            }

            // TeleportTo always leaves you stopped dead: it zeroes the rigidbody unless asked to
            // keep velocity, and what it would keep is the zero this hold put there anyway. So
            // the ragdoll's velocity is handed over explicitly, after the teleport - get up
            // mid-fall and you carry on falling rather than hanging in the air.
            if (exitVelocity.sqrMagnitude > 0.01f)
            {
                try { p.SetPlayerVelocity(exitVelocity); }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning("[Suspend] could not hand velocity back: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// The rotation to stand up in: the heading you are facing now, levelled against the
        /// orientation you collapsed in.
        ///
        /// <para>Two traps here. <c>eulerAngles.y</c> is <b>not</b> the heading once a rotation
        /// carries roll - the decomposition is degenerate, and reading it back after
        /// <c>FirstPersonLocked</c> has been rolling the rig around gives an arbitrary answer.
        /// Projecting forward onto the up plane is well defined however tilted things got.</para>
        ///
        /// <para>And "level" cannot mean world up. GT rotates the player itself for gravity
        /// zones (<c>GTPlayerTransform</c> is a <c>MonkeGravityController</c>), so forcing
        /// world upright would stand you up sideways anywhere gravity is not down. The
        /// orientation captured at collapse is the right reference: it is by definition a legal
        /// one, and it is the one the ragdoll took you away from.</para>
        /// </summary>
        private Quaternion UprightFacing(GTPlayer p)
        {
            Vector3 up = _collapseRotation * Vector3.up;

            Vector3 forward = Vector3.ProjectOnPlane(p.transform.forward, up);
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.ProjectOnPlane(_collapseRotation * Vector3.forward, up);
            if (forward.sqrMagnitude < 1e-6f)
                return _collapseRotation;

            return Quaternion.LookRotation(forward.normalized, up);
        }

        /// <summary>
        /// Drops a point to the ground so getting up does not leave you inside the floor or
        /// hovering. Falls back to the point itself if nothing is under it.
        /// </summary>
        public static Vector3 GroundUnder(Vector3 point)
        {
            Vector3 ground;
            return TryGroundUnder(point, out ground) ? ground : point;
        }

        /// <summary>
        /// Ground beneath a point, and whether there was any.
        ///
        /// <para>The distinction is the whole point: a body still falling has no ground under it
        /// within any sensible reach, and standing you up "just above the floor" when the search
        /// never found a floor is exactly what left you hanging in mid-air. Callers that care
        /// read the bool rather than trusting the position.</para>
        /// </summary>
        public static bool TryGroundUnder(Vector3 point, out Vector3 ground, float maxDistance = 6f)
        {
            int mask;
            try { mask = GTPlayer.LocomotionEnabledLayers; }
            catch { mask = ~0; }

            RaycastHit hit;
            if (Physics.Raycast(point + Vector3.up * 0.5f, Vector3.down, out hit, maxDistance, mask,
                                QueryTriggerInteraction.Ignore))
            {
                ground = hit.point;
                return true;
            }
            ground = point;
            return false;
        }

        /// <summary>
        /// A clear spot to stand up in, just above where the body came to rest.
        ///
        /// <para>Standing up exactly where a ragdoll landed is asking for trouble: a corpse
        /// settles half-inside geometry all the time - wedged against a log, under a ledge, on a
        /// slope - and a player spawned there is spawned inside a wall. So the ground beneath the
        /// body is found first, then the spot above it is <b>tested for clearance</b> and raised
        /// until it is actually empty. If nothing works, it falls back to where you collapsed,
        /// which was by definition a legal place to stand.</para>
        /// </summary>
        public static Vector3 GetUpPoint(Vector3 bodyPosition, float height, Vector3 fallback)
        {
            int mask;
            try { mask = GTPlayer.LocomotionEnabledLayers; }
            catch { mask = ~0; }

            Vector3 ground = GroundUnder(bodyPosition);

            // Roughly a gorilla's torso; enough to catch a spot that is inside something.
            const float clearance = 0.28f;

            for (float extra = 0f; extra <= 2f; extra += 0.4f)
            {
                Vector3 candidate = ground + Vector3.up * (height + extra);
                if (!Physics.CheckSphere(candidate + Vector3.up * clearance, clearance, mask,
                                         QueryTriggerInteraction.Ignore))
                    return candidate;
            }

            Plugin.Log.LogWarning("[Suspend] no clear spot above the body - standing up where you collapsed");
            return fallback;
        }

        // ------------------------------------------------------------------ root placement

        /// <summary>
        /// Where the headset sat, in rig space, at the moment of collapse.
        ///
        /// <para>This is the difference between a camera you are sitting in and a camera you are
        /// strapped to. Pinning the live camera to a point cancels every head movement you make:
        /// lean, and the rig instantly compensates, so you get no parallax and the world feels
        /// welded to your face. Placing the rig for a <i>neutral</i> head instead means your
        /// actual head is free to move within it - look around, lean, duck - and the view
        /// responds the way your body expects.</para>
        /// </summary>
        private static Vector3 _neutralHeadLocal;
        private static bool _hasNeutralHead;

        /// <summary>Records the resting head offset. Call once, at collapse.</summary>
        public static void CaptureNeutralHead()
        {
            var p = GTPlayer.Instance;
            if (p == null || p.mainCamera == null) { _hasNeutralHead = false; return; }
            _neutralHeadLocal = p.transform.InverseTransformPoint(p.mainCamera.transform.position);
            _hasNeutralHead = true;
        }

        /// <summary>
        /// Places the rig so a neutral head would sit at <paramref name="worldEye"/>, leaving
        /// your real head free to move relative to it.
        /// </summary>
        public static void PlaceRigForEye(Vector3 worldEye, float smoothing)
        {
            var p = GTPlayer.Instance;
            if (p == null || p.mainCamera == null) return;
            if (!_hasNeutralHead) { PlaceEyeAt(worldEye, smoothing); return; }

            Vector3 desired = worldEye - p.transform.rotation * _neutralHeadLocal;

            if (smoothing > 0.001f)
                p.transform.position = Vector3.Lerp(p.transform.position, desired,
                                                    1f - Mathf.Exp(-Time.deltaTime / smoothing));
            else
                p.transform.position = desired;
        }

        /// <summary>
        /// Moves the player rig so the headset ends up at <paramref name="worldEye"/>.
        ///
        /// <para>The HMD's local pose is written by XR tracking every frame and cannot be
        /// overwritten - GT itself works around this by subtracting the camera's local
        /// position from the player root. So rather than compute offsets and fight the
        /// tracking, we measure where the camera actually is and translate the whole rig by
        /// the difference. That is correct regardless of how deep the camera sits in the
        /// hierarchy or how the playspace is rotated.</para>
        /// </summary>
        public static void PlaceEyeAt(Vector3 worldEye, float smoothing)
        {
            var p = GTPlayer.Instance;
            if (p == null || p.mainCamera == null) return;

            Vector3 delta = worldEye - p.mainCamera.transform.position;
            if (smoothing > 0.001f)
                delta *= 1f - Mathf.Exp(-Time.deltaTime / smoothing);

            p.transform.position += delta;
        }

        /// <summary>
        /// Yaws the rig about the headset, so the view turns without also sliding sideways.
        /// Roll and pitch are intentionally not offered here: rotating a VR player's horizon
        /// is the fastest way to make them ill, and is gated behind explicit comfort settings
        /// at the call site.
        /// </summary>
        public static void RotateAroundEye(float yawDelta)
        {
            var p = GTPlayer.Instance;
            if (p == null || p.mainCamera == null) return;
            if (Mathf.Abs(yawDelta) < 0.0001f) return;
            p.transform.RotateAround(p.mainCamera.transform.position, Vector3.up, yawDelta);
        }

        /// <summary>
        /// Turns the rig by <paramref name="yawDelta"/> degrees, in place.
        ///
        /// <para><b>Rotation only, deliberately.</b> The VR orbit's own next line always places the
        /// rig afresh with <see cref="PlaceRigForEye"/>, computed straight from the stick's yaw and
        /// distance - so this used to also move the rig around a pivot (<c>Transform.RotateAround</c>),
        /// and that position write was thrown away a moment later by <c>PlaceRigForEye</c>'s own
        /// smoothed one every single frame you turned. Two writers of the same frame's position, one
        /// instant and one lagging behind it with its own Lerp, is exactly what "goes back and forth
        /// while rotating" looks like - the camera would snap to the exact orbit spot this call put
        /// it at, then immediately get pulled part-way back towards wherever the smoothed placement
        /// still thought it belonged. Turning the rig without moving it leaves position to the one
        /// piece of code that actually owns it, and nothing is fighting the smoothing any more.</para>
        ///
        /// <para>This is still what makes a VR orbit camera comfortable: rotating <i>with</i> the
        /// orbit's yaw, not just translating, is what keeps the body in front of you as you swing
        /// round it rather than sliding sideways past your own view.</para>
        /// </summary>
        public static void Yaw(float yawDelta)
        {
            var p = GTPlayer.Instance;
            if (p == null || Mathf.Abs(yawDelta) < 0.0001f) return;
            p.transform.rotation = Quaternion.AngleAxis(yawDelta, Vector3.up) * p.transform.rotation;
        }

        /// <summary>Tilts the rig about the headset. Only reached when comfort settings allow.</summary>
        public static void TiltAroundEye(Vector3 axis, float degrees)
        {
            var p = GTPlayer.Instance;
            if (p == null || p.mainCamera == null) return;
            if (Mathf.Abs(degrees) < 0.0001f) return;
            p.transform.RotateAround(p.mainCamera.transform.position, axis, degrees);
        }
    }
}
