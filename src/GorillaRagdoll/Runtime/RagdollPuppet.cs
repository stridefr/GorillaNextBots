using System.Collections.Generic;
using System.Text;
using GorillaLocomotion;
using GorillaRagdoll.Config;
using UnityEngine;

namespace GorillaRagdoll.Runtime
{
    /// <summary>
    /// The invisible physics skeleton that actually falls over.
    ///
    /// <para>Nothing here is ever rendered. That is the whole idea, and it is what earlier
    /// attempts got wrong: the puppet does not have to look like a gorilla, satisfy GT's IK, or
    /// keep a mesh intact. It only has to <i>fall like a body</i>. The visible gorilla is GT's
    /// own rig, posed from this puppet's head and hands by <see cref="RigDriver"/> - so the
    /// avatar is drawn by the game's own IK and is therefore always anatomically valid, no
    /// matter what the physics does.</para>
    ///
    /// <para>Freed from having to look right, the puppet can be a plain jointed chain with
    /// generous colliders, which is the shape physics engines are actually good at.</para>
    /// </summary>
    public sealed class RagdollPuppet : IPoseSource
    {
        public GameObject Root { get; private set; }

        public Transform Body { get; private set; }
        public Transform Head { get; private set; }
        public Transform HandL { get; private set; }
        public Transform HandR { get; private set; }

        /// <summary>The rig bone the puppet's torso was built from; the driver aligns to it.</summary>
        public Transform SourceBody { get; private set; }

        /// <summary>
        /// Rotation from the head bone's own frame to the direction you actually look.
        ///
        /// <para>Measured at collapse as <c>inverse(headBone) * camera</c>, because a bone's
        /// forward axis is a modelling choice, not a convention - GT's head bone happens to
        /// face backwards relative to the view, which is why locked first person looked out of
        /// the back of the skull. Calibrating it against the live camera is exact and needs no
        /// magic 180.</para>
        /// </summary>
        public Quaternion HeadToView { get; private set; } = Quaternion.identity;

        private readonly Dictionary<string, Transform> _nodes = new Dictionary<string, Transform>(10);
        private readonly Dictionary<string, Rigidbody> _bodies = new Dictionary<string, Rigidbody>(10);
        private readonly Dictionary<string, BonePart> _parts = new Dictionary<string, BonePart>(10);
        private readonly List<Collider> _colliders = new List<Collider>(10);
        private readonly List<ConfigurableJoint> _motors = new List<ConfigurableJoint>(10);

        public static RagdollPuppet Build(VRRig rig)
        {
            if (rig == null) return null;

            // Source poses come from the live rig, so the puppet starts in exactly the pose you
            // collapsed in. Hands read from the IK targets rather than the bones - that is where
            // GT considers your hands to actually be.
            var source = new Dictionary<string, Transform>(10);
            foreach (var link in RagdollPlan.Chain)
            {
                var t = RigBones.Find(rig.transform, link.Bone);
                if (t != null) source[link.Bone] = t;
            }
            OverrideFromTarget(source, RagdollPlan.HandLeft, rig.leftHand);
            OverrideFromTarget(source, RagdollPlan.HandRight, rig.rightHand);

            Transform body;
            if (!source.TryGetValue(RagdollPlan.Torso, out body))
            {
                Plugin.Log.LogError("[Puppet] rig has no '" + RagdollPlan.Torso +
                                    "' bone - press DumpSkeletonKey and check the rig");
                return null;
            }

            var puppet = new RagdollPuppet();
            puppet.SourceBody = body;
            puppet.Root = new GameObject("GorillaRagdoll.Puppet");
            Object.DontDestroyOnLoad(puppet.Root);

            int layer = RagdollLayer.Resolve();

            foreach (var link in RagdollPlan.Chain)
            {
                Transform src;
                if (!source.TryGetValue(link.Bone, out src)) continue;

                Transform parent = puppet.Root.transform;
                if (link.Parent != null && !puppet._nodes.TryGetValue(link.Parent, out parent))
                    continue;   // parent missing means the whole limb is missing

                var go = new GameObject(link.Bone) { layer = layer };
                go.transform.SetParent(parent, true);
                go.transform.SetPositionAndRotation(src.position, src.rotation);

                puppet._nodes[link.Bone] = go.transform;
                puppet._parts[link.Bone] = link.Part;
            }

            puppet.Body = puppet.Get(RagdollPlan.Torso);
            puppet.Head = puppet.Get(RagdollPlan.Head) ?? puppet.Body;
            puppet.HandL = puppet.Get(RagdollPlan.HandLeft);
            puppet.HandR = puppet.Get(RagdollPlan.HandRight);

            Transform sourceHead;
            if (source.TryGetValue(RagdollPlan.Head, out sourceHead) && sourceHead != null)
            {
                var cam = GTPlayer.Instance != null ? GTPlayer.Instance.mainCamera : null;
                if (cam != null)
                    puppet.HeadToView = Quaternion.Inverse(sourceHead.rotation) * cam.transform.rotation;
            }

            puppet.AddPhysics();
            puppet.WireCollisions();

            Plugin.Log.LogInfo("[Puppet] built " + puppet._nodes.Count + " nodes from the live rig");
            return puppet;
        }

        /// <summary>
        /// The torso's world velocity right now, so getting up can hand it back to the player.
        ///
        /// <para>Reads zero once <see cref="Settle"/> has slept the bodies, which is correct:
        /// a corpse that has come to rest should stand up at rest.</para>
        /// </summary>
        public Vector3 TorsoVelocity
        {
            get
            {
                Rigidbody rb;
                if (_bodies.TryGetValue(RagdollPlan.Torso, out rb) && rb != null)
                    return rb.linearVelocity;
                return Vector3.zero;
            }
        }

        /// <summary>A puppet node by bone name, for the driver to copy onto the real rig.</summary>
        public Transform Node(string name)
        {
            Transform t;
            return _nodes.TryGetValue(name, out t) ? t : null;
        }

        private Transform Get(string name)
        {
            Transform t;
            return _nodes.TryGetValue(name, out t) ? t : null;
        }

        private static void OverrideFromTarget(Dictionary<string, Transform> source, string key, VRMap map)
        {
            if (map != null && map.rigTarget != null) source[key] = map.rigTarget;
        }

        // ------------------------------------------------------------------ physics

        private void AddPhysics()
        {
            float weightSum = 0f;
            foreach (var kv in _parts) weightSum += BoneProfile.MassWeight(kv.Value);
            if (weightSum <= 0f) weightSum = _parts.Count;
            float massScale = RagdollConfig.TotalMass.Value / weightSum;

            foreach (var link in RagdollPlan.Chain)
            {
                Transform t;
                if (!_nodes.TryGetValue(link.Bone, out t)) continue;

                var rb = t.gameObject.AddComponent<Rigidbody>();
                rb.mass = Mathf.Max(0.1f, BoneProfile.MassWeight(link.Part) * massScale);
                rb.linearDamping = RagdollConfig.LinearDamping.Value;
                rb.angularDamping = RagdollConfig.AngularDamping.Value;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = RagdollConfig.ContinuousCollision.Value
                    ? CollisionDetectionMode.ContinuousDynamic
                    : CollisionDetectionMode.Discrete;
                rb.solverIterations = RagdollConfig.SolverIterations.Value;
                rb.solverVelocityIterations = Mathf.Max(2, RagdollConfig.SolverIterations.Value / 2);
                rb.maxAngularVelocity = 20f;
                rb.useGravity = true;
                rb.isKinematic = false;
                _bodies[link.Bone] = rb;

                var col = BuildCollider(link, t);
                if (col != null) _colliders.Add(col);
            }

            // Joints second, so every parent body exists before anything connects to it.
            foreach (var link in RagdollPlan.Chain)
            {
                if (link.Parent == null) continue;

                Transform t;
                Rigidbody parentBody;
                if (!_nodes.TryGetValue(link.Bone, out t)) continue;
                if (!_bodies.TryGetValue(link.Parent, out parentBody)) continue;

                BuildJoint(t, parentBody, link.Part);
            }
        }

        private Collider BuildCollider(PuppetLink link, Transform t)
        {
            // Where the next link down sits, so limbs get a capsule along their own length.
            Transform child = null;
            foreach (var other in RagdollPlan.Chain)
                if (other.Parent == link.Bone && _nodes.ContainsKey(other.Bone))
                {
                    child = _nodes[other.Bone];
                    break;
                }

            if (link.Part == BonePart.Torso)
            {
                var cap = t.gameObject.AddComponent<CapsuleCollider>();
                cap.direction = 1;
                cap.center = new Vector3(0f, 0.05f, 0f);
                cap.radius = 0.22f;
                cap.height = 0.7f;
                cap.sharedMaterial = Surface();
                return cap;
            }

            if (child != null)
            {
                Vector3 local = t.InverseTransformPoint(child.position);
                float len = local.magnitude;
                if (len > 0.02f)
                {
                    float radius = Mathf.Clamp(len * 0.4f, 0.04f, 0.2f);
                    var holder = new GameObject("col") { layer = t.gameObject.layer };
                    holder.transform.SetParent(t, false);
                    holder.transform.localPosition = local * 0.5f;
                    holder.transform.localRotation = Quaternion.FromToRotation(Vector3.up, local.normalized);

                    var cap = holder.AddComponent<CapsuleCollider>();
                    cap.direction = 1;
                    cap.height = len + radius;
                    cap.radius = radius;
                    cap.sharedMaterial = Surface();
                    return cap;
                }
            }

            var sphere = t.gameObject.AddComponent<SphereCollider>();
            sphere.radius = link.Part == BonePart.Head ? 0.16f : 0.07f;
            sphere.sharedMaterial = Surface();
            return sphere;
        }

        private void BuildJoint(Transform t, Rigidbody parent, BonePart part)
        {
            Vector3 twist = t.InverseTransformDirection(t.position - parent.transform.position);
            if (twist.sqrMagnitude < 1e-6f) twist = Vector3.up;
            twist.Normalize();

            Vector3 swing = Vector3.Cross(twist, Vector3.up);
            if (swing.sqrMagnitude < 1e-4f) swing = Vector3.Cross(twist, Vector3.right);
            swing.Normalize();

            float s1, s2, tw;
            BoneProfile.Limits(part, out s1, out s2, out tw);
            float loose = RagdollConfig.JointLooseness.Value;
            s1 = Mathf.Clamp(s1 * loose, 1f, 177f);
            s2 = Mathf.Clamp(s2 * loose, 1f, 177f);
            tw = Mathf.Clamp(tw * loose, 1f, 177f);

            if (RagdollConfig.ActiveMode.Value != ActiveRagdollMode.Off)
            {
                BuildMotorJoint(t, parent, twist, swing, s1, s2, tw);
                return;
            }

            var j = t.gameObject.AddComponent<CharacterJoint>();
            j.connectedBody = parent;
            j.anchor = Vector3.zero;
            j.axis = twist;
            j.swingAxis = swing;
            j.autoConfigureConnectedAnchor = true;

            var low = j.lowTwistLimit; low.limit = -tw; j.lowTwistLimit = low;
            var high = j.highTwistLimit; high.limit = tw; j.highTwistLimit = high;
            var sw1 = j.swing1Limit; sw1.limit = s1; j.swing1Limit = sw1;
            var sw2 = j.swing2Limit; sw2.limit = s2; j.swing2Limit = sw2;

            // Projection is what stops a fast ragdoll pulling its own joints apart on a hard
            // hit; without it a limb can separate and the driven rig follows it off the body.
            j.enableProjection = true;
            j.projectionDistance = 0.05f;
            j.projectionAngle = 15f;
            j.enablePreprocessing = false;
        }

        /// <summary>
        /// The active-ragdoll joint: same limits, but with a motor.
        ///
        /// <para>A <c>ConfigurableJoint</c> is used instead of a <c>CharacterJoint</c> purely
        /// because only the former has angular drives. Its <c>targetRotation</c> is left at
        /// identity, which means "hold the configuration you were created in" - and since the
        /// joints are created at the instant you collapse, the body's target pose <i>is</i> the
        /// pose you were standing in. No pose authoring, no animation data.</para>
        ///
        /// <para>The spring is driven from zero at collapse and ramped up over
        /// <c>ActiveRecover</c>, so the body crumples first and gathers itself afterwards
        /// instead of snapping rigid on frame one.</para>
        /// </summary>
        private void BuildMotorJoint(Transform t, Rigidbody parent, Vector3 twist, Vector3 swing,
                                     float s1, float s2, float tw)
        {
            var j = t.gameObject.AddComponent<ConfigurableJoint>();
            j.connectedBody = parent;
            j.anchor = Vector3.zero;
            j.autoConfigureConnectedAnchor = true;
            j.axis = twist;
            j.secondaryAxis = swing;

            j.xMotion = ConfigurableJointMotion.Locked;
            j.yMotion = ConfigurableJointMotion.Locked;
            j.zMotion = ConfigurableJointMotion.Locked;
            j.angularXMotion = ConfigurableJointMotion.Limited;
            j.angularYMotion = ConfigurableJointMotion.Limited;
            j.angularZMotion = ConfigurableJointMotion.Limited;

            j.lowAngularXLimit = new SoftJointLimit { limit = -tw };
            j.highAngularXLimit = new SoftJointLimit { limit = tw };
            j.angularYLimit = new SoftJointLimit { limit = s1 };
            j.angularZLimit = new SoftJointLimit { limit = s2 };

            j.rotationDriveMode = RotationDriveMode.Slerp;
            j.slerpDrive = new JointDrive
            {
                positionSpring = 0f,     // ramped up by UpdateActive
                positionDamper = RagdollConfig.ActiveDamping.Value,
                maximumForce = float.MaxValue
            };
            j.targetRotation = Quaternion.identity;

            j.enablePreprocessing = false;
            j.projectionMode = JointProjectionMode.PositionAndRotation;
            j.projectionDistance = 0.05f;
            j.projectionAngle = 15f;

            _motors.Add(j);
        }

        private float _tensedSince = -1f;
        private Vector3 _lastTorsoVelocity;

        /// <summary>
        /// Drives the active-ragdoll strength. Called every frame; a no-op when the mode is Off.
        ///
        /// <para>Impacts are detected from the torso's frame-to-frame change in velocity rather
        /// than from collision callbacks - it needs no extra components on the puppet, and it
        /// catches being launched or yanked by a joint just as well as being hit by scenery.
        /// Anything above the threshold resets the body to limp and it tenses again from
        /// there.</para>
        /// </summary>
        public void UpdateActive()
        {
            if (RagdollConfig.ActiveMode.Value == ActiveRagdollMode.Off || _motors.Count == 0) return;

            Rigidbody torso;
            if (!_bodies.TryGetValue(RagdollPlan.Torso, out torso) || torso == null) return;

            if (_tensedSince < 0f) _tensedSince = Time.time;

            Vector3 v = torso.linearVelocity;
            if ((v - _lastTorsoVelocity).magnitude > RagdollConfig.ActiveImpactThreshold.Value)
            {
                _tensedSince = Time.time;   // knocked about; go limp and start again
                Disturb();
            }
            _lastTorsoVelocity = v;

            float recover = Mathf.Max(0.05f, RagdollConfig.ActiveRecover.Value);
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.time - _tensedSince) / recover));
            float spring = RagdollConfig.ActiveStrength.Value * t;
            float damper = RagdollConfig.ActiveDamping.Value;

            foreach (var j in _motors)
            {
                if (j == null) continue;
                j.slerpDrive = new JointDrive
                {
                    positionSpring = spring,
                    positionDamper = damper,
                    maximumForce = float.MaxValue
                };
            }
        }

        /// <summary>How tense the body currently is, 0-1. For the overlay.</summary>
        public float ActiveTension
        {
            get
            {
                if (RagdollConfig.ActiveMode.Value == ActiveRagdollMode.Off || _tensedSince < 0f) return 0f;
                float recover = Mathf.Max(0.05f, RagdollConfig.ActiveRecover.Value);
                return Mathf.Clamp01((Time.time - _tensedSince) / recover);
            }
        }

        private static PhysicsMaterial _surface;

        private static PhysicsMaterial Surface()
        {
            if (_surface != null) return _surface;
            _surface = new PhysicsMaterial("RagdollSurface")
            {
                dynamicFriction = 0.4f,
                staticFriction = 0.4f,
                bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Multiply,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };
            return _surface;
        }

        private void WireCollisions()
        {
            // Jointed pairs already ignore each other (Joint.enableCollision is false), so this
            // is only about limbs that are not directly connected.
            if (!RagdollConfig.SelfCollision.Value)
            {
                for (int i = 0; i < _colliders.Count; i++)
                    for (int k = i + 1; k < _colliders.Count; k++)
                        if (_colliders[i] != null && _colliders[k] != null)
                            Physics.IgnoreCollision(_colliders[i], _colliders[k], true);
            }

            foreach (var mine in PlayerColliders())
                foreach (var c in _colliders)
                    if (mine != null && c != null)
                        Physics.IgnoreCollision(c, mine, true);
        }

        public static List<Collider> PlayerColliders()
        {
            var list = new List<Collider>(6);
            try
            {
                var p = GTPlayer.Instance;
                if (p != null) { Add(list, p.headCollider); Add(list, p.bodyCollider); }

                var tagger = GorillaTagger.Instance;
                if (tagger != null)
                {
                    Add(list, tagger.headCollider);
                    Add(list, tagger.bodyCollider);
                    if (tagger.leftHandTriggerCollider != null)
                        Add(list, tagger.leftHandTriggerCollider.GetComponent<Collider>());
                    if (tagger.rightHandTriggerCollider != null)
                        Add(list, tagger.rightHandTriggerCollider.GetComponent<Collider>());
                }
            }
            catch { /* best effort */ }
            return list;
        }

        private static void Add(List<Collider> list, Collider c)
        {
            if (c != null && !list.Contains(c)) list.Add(c);
        }

        // ------------------------------------------------------------------ runtime

        /// <summary>
        /// Hands the puppet the momentum you had, so jumping or swinging before you collapse
        /// actually throws the body. Hand speeds come from GT's own velocity trackers, which
        /// are smoothed and already in world space.
        /// </summary>
        public void Seed(Vector3 bodyVelocity, Vector3 viewForward)
        {
            bool inheritBody = RagdollConfig.InheritPlayerVelocity.Value;
            bool inheritLimb = RagdollConfig.InheritLimbVelocity.Value;
            float impulse = RagdollConfig.LaunchImpulse.Value;
            float spin = RagdollConfig.LaunchSpin.Value;

            Vector3 baseVel = inheritBody ? bodyVelocity : Vector3.zero;
            if (Mathf.Abs(impulse) > 0.001f) baseVel += viewForward.normalized * impulse;
            baseVel = Vector3.ClampMagnitude(baseVel, 40f);

            foreach (var kv in _bodies)
            {
                kv.Value.linearVelocity = baseVel;
                kv.Value.angularVelocity = Vector3.zero;
            }

            if (inheritLimb)
            {
                SeedHand(RagdollPlan.HandLeft, true, baseVel);
                SeedHand(RagdollPlan.HandRight, false, baseVel);
            }

            Rigidbody torso;
            if (spin > 0f && _bodies.TryGetValue(RagdollPlan.Torso, out torso))
                torso.angularVelocity = Random.insideUnitSphere * spin;
        }

        private void SeedHand(string bone, bool left, Vector3 baseVel)
        {
            Rigidbody rb;
            if (!_bodies.TryGetValue(bone, out rb)) return;
            try
            {
                var p = GTPlayer.Instance;
                if (p == null) return;
                var tracker = left ? p.LeftHand.velocityTracker : p.RightHand.velocityTracker;
                if (tracker == null) return;
                rb.linearVelocity = Vector3.ClampMagnitude(
                    baseVel + tracker.GetAverageVelocity(true, 0f, false), 40f);
            }
            catch { /* trackers are a bonus, not a requirement */ }
        }

        private float _stillFor;
        private bool _asleep;

        /// <summary>
        /// Puts the puppet to sleep once it has genuinely come to rest.
        ///
        /// <para>Without this, a limb at the end of a joint chain never quite stops. Damping
        /// decays angular velocity exponentially, so a hand that is barely touching anything
        /// keeps turning for tens of seconds - slowly enough to look like a bug rather than
        /// like physics, which is exactly how it reads. Contact friction cannot help a body
        /// that is dangling.</para>
        ///
        /// <para>So once every body is below a walking-pace threshold for a moment, they are
        /// slept explicitly. Unity wakes them again by itself on any real collision or force,
        /// so the ragdoll still reacts to being hit - it just stops fidgeting.</para>
        /// </summary>
        public void Settle()
        {
            if (_asleep) return;

            const float linear = 0.08f;     // m/s
            const float angular = 0.35f;    // rad/s
            const float hold = 0.5f;        // seconds below both before sleeping

            foreach (var kv in _bodies)
            {
                var rb = kv.Value;
                if (rb == null) continue;
                if (rb.linearVelocity.sqrMagnitude > linear * linear ||
                    rb.angularVelocity.sqrMagnitude > angular * angular)
                {
                    _stillFor = 0f;
                    return;
                }
            }

            _stillFor += Time.deltaTime;
            if (_stillFor < hold) return;

            foreach (var kv in _bodies) if (kv.Value != null) kv.Value.Sleep();
            _asleep = true;
        }

        /// <summary>Anything that disturbs the puppet has to clear the settled state.</summary>
        public void Disturb()
        {
            _asleep = false;
            _stillFor = 0f;
        }

        public void ApplyExtraGravity()
        {
            float g = RagdollConfig.ExtraGravity.Value;
            if (Mathf.Abs(g) < 0.001f || _asleep) return;
            foreach (var kv in _bodies) kv.Value.AddForce(Vector3.down * g, ForceMode.Acceleration);
        }

        public void ApplyLiveTuning()
        {
            float weightSum = 0f;
            foreach (var kv in _parts) weightSum += BoneProfile.MassWeight(kv.Value);
            if (weightSum <= 0f) weightSum = _parts.Count;
            float massScale = RagdollConfig.TotalMass.Value / weightSum;

            foreach (var kv in _bodies)
            {
                BonePart part;
                if (!_parts.TryGetValue(kv.Key, out part)) continue;
                kv.Value.mass = Mathf.Max(0.1f, BoneProfile.MassWeight(part) * massScale);
                kv.Value.linearDamping = RagdollConfig.LinearDamping.Value;
                kv.Value.angularDamping = RagdollConfig.AngularDamping.Value;
            }
        }

        public int NodeCount => _nodes.Count;

        public Vector3 Centroid()
        {
            if (_nodes.Count == 0) return Vector3.zero;
            var sum = Vector3.zero;
            foreach (var kv in _nodes) sum += kv.Value.position;
            return sum / _nodes.Count;
        }

        public void Destroy()
        {
            if (Root != null) Object.Destroy(Root);
            Root = null;
        }

        public string Describe(Vector3 rootAtCollapse, float elapsed)
        {
            var sb = new StringBuilder(512);
            sb.Append("[Diag] gravity=").Append(Physics.gravity.ToString("0.00"))
              .Append(" timeScale=").Append(Time.timeScale.ToString("0.00"))
              .Append(" simMode=").Append(Physics.simulationMode).AppendLine();

            Rigidbody torso;
            if (_bodies.TryGetValue(RagdollPlan.Torso, out torso))
                sb.Append("[Diag] torso moved ")
                  .Append(Vector3.Distance(torso.transform.position, rootAtCollapse).ToString("0.000"))
                  .Append("m in ").Append(elapsed.ToString("0.0")).Append("s | v=")
                  .Append(torso.linearVelocity.magnitude.ToString("0.00"))
                  .Append(" sleeping=").Append(torso.IsSleeping()).AppendLine();

            foreach (var kv in _bodies)
                sb.Append("[Diag]   ").Append(kv.Key.PadRight(12))
                  .Append("m=").Append(kv.Value.mass.ToString("0.00"))
                  .Append(" v=").Append(kv.Value.linearVelocity.magnitude.ToString("0.00"))
                  .Append(" w=").Append(kv.Value.angularVelocity.magnitude.ToString("0.00"))
                  .AppendLine();
            return sb.ToString();
        }
    }
}
