using UnityEngine;

namespace NextBots.Runtime
{
    /// <summary>
    /// Movement without a NavMesh: head for the destination, and when something is in the
    /// way, fan out to either side until a clear heading is found ("whisker" steering).
    ///
    /// This is the tier-3 fallback for maps where no NavMesh exists and a runtime bake
    /// produced nothing. It is genuinely dumber than pathfinding - it will happily walk into
    /// a dead-end U-shaped wall and sit there - but the brain's stuck escalation is what
    /// covers that: a couple of seconds of no progress and it charges, a couple more and it
    /// teleports. Between them the bot never just stands in a corner, which is the failure
    /// that actually ruins the effect.
    ///
    /// No Rigidbody and no colliders, deliberately: a solid body would shove players around,
    /// and in a game where being shoved moves your whole body that feels awful.
    /// </summary>
    public class SteeringLocomotor : MonoBehaviour
    {
        /// <summary>
        /// Which layers count as solid ground and obstacles.
        ///
        /// Deliberately the same broad mask the NavMesh bake uses, NOT the narrow sight mask.
        /// Sight only needs Default + Gorilla Object, but map collision lives on more layers
        /// than that - observed live: a bot steered correctly to the player's exact X/Z and
        /// then fell to y=-32, because the floor it was walking on was on a layer the ground
        /// raycast could not see.
        /// </summary>
        private static int GroundMask => NavMeshProvider.BakeMask;

        public Vector3 Destination;
        public float Speed = 6f;
        public bool Stopped;

        /// <summary>How far ahead to test for obstructions.</summary>
        public float ProbeDistance = 1.6f;

        /// <summary>Clearance either side of the path.</summary>
        public float BodyRadius = 0.45f;

        /// <summary>How high a lip the bot can walk straight over.</summary>
        public float StepHeight = 0.7f;

        /// <summary>Distance to search downward for the ground each frame.</summary>
        public float GroundProbe = 12f;

        public Vector3 Velocity { get; private set; }
        public bool Grounded { get; private set; }

        // Fanned headings, in degrees, tried in order. Alternating sides so the bot does not
        // develop a permanent bias to one direction, which looks like a limp.
        private static readonly float[] Whiskers = { 0f, 22f, -22f, 45f, -45f, 70f, -70f, 100f, -100f, 140f, -140f };

        private float _verticalVelocity;

        private void Update()
        {
            var dt = Time.deltaTime;
            if (dt <= 0f) return;

            var pos = transform.position;

            if (Stopped)
            {
                Velocity = Vector3.zero;
                ApplyGravityAndGround(ref pos, dt);
                transform.position = pos;
                return;
            }

            var toDest = Destination - pos;
            toDest.y = 0f;
            var planarDist = toDest.magnitude;

            if (planarDist < 0.05f)
            {
                Velocity = Vector3.zero;
                ApplyGravityAndGround(ref pos, dt);
                transform.position = pos;
                return;
            }

            var desired = toDest / planarDist;
            var heading = ChooseHeading(pos, desired, planarDist);

            var step = heading * Speed * dt;
            pos += step;
            Velocity = step / dt;

            ApplyGravityAndGround(ref pos, dt);
            transform.position = pos;

            if (heading.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(heading, Vector3.up);
        }

        /// <summary>
        /// The straight line if it is clear, otherwise the closest heading to it that is.
        /// Falls back to the straight line so a fully enclosed bot still pushes, and the
        /// brain's stuck timer notices and escalates.
        /// </summary>
        private Vector3 ChooseHeading(Vector3 pos, Vector3 desired, float distToDest)
        {
            var probe = Mathf.Min(ProbeDistance, distToDest);
            // Probe from mid-body height so a kerb does not read as a wall.
            var origin = pos + Vector3.up * (StepHeight + 0.15f);

            for (int i = 0; i < Whiskers.Length; i++)
            {
                var dir = Quaternion.AngleAxis(Whiskers[i], Vector3.up) * desired;
                if (!Physics.SphereCast(origin, BodyRadius, dir, out _, probe,
                                        GroundMask, QueryTriggerInteraction.Ignore))
                {
                    return dir;
                }
            }

            return desired;
        }

        private void ApplyGravityAndGround(ref Vector3 pos, float dt)
        {
            var origin = pos + Vector3.up * (StepHeight + 0.2f);

            RaycastHit hit;
            if (Physics.Raycast(origin, Vector3.down, out hit, GroundProbe,
                                GroundMask, QueryTriggerInteraction.Ignore))
            {
                var groundY = hit.point.y;

                // Snap up onto small steps, and fall to the floor otherwise.
                if (pos.y <= groundY + StepHeight)
                {
                    pos.y = groundY;
                    _verticalVelocity = 0f;
                    Grounded = true;
                    return;
                }

                _verticalVelocity += Physics.gravity.y * dt;
                pos.y = Mathf.Max(groundY, pos.y + _verticalVelocity * dt);
                Grounded = Mathf.Approximately(pos.y, groundY);
                if (Grounded) _verticalVelocity = 0f;
                return;
            }

            // Nothing underneath at all - keep falling, and let the brain's hard reset
            // recover us rather than dropping out of the world forever.
            _verticalVelocity += Physics.gravity.y * dt;
            pos.y += _verticalVelocity * dt;
            Grounded = false;
        }

        public void Warp(Vector3 position)
        {
            transform.position = position;
            _verticalVelocity = 0f;
            Velocity = Vector3.zero;
        }
    }
}
