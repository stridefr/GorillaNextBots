using NextBots.Config;
using UnityEngine;
using UnityEngine.AI;

namespace NextBots.Runtime
{
    /// <summary>
    /// Everything the bot does while its feet are off the ground: jumping, falling off
    /// ledges, and being knocked clear of the mesh.
    ///
    /// This is a real ballistic integrator, not a scripted arc. Velocity accumulates gravity
    /// each frame and position integrates velocity, so a jump and a fall are the same
    /// physics with different launch conditions - a jump starts with upward speed, a fall
    /// starts with whatever the bot was already running at. That is what makes a drop off a
    /// balcony look like the bot ran off it rather than like it was lowered.
    ///
    /// The NavMeshAgent is disabled for the whole flight. An agent is glued to the surface
    /// and silently cancels any height you give it, so a bot "jumping" with the agent still
    /// enabled just slides along the floor.
    /// </summary>
    public class BotAirborne
    {
        private readonly NextBot _bot;

        private bool _active;
        private Vector3 _velocity;
        private float _elapsed;
        private float _nextJumpAllowedAt;
        private bool _wasJump;

        /// <summary>Radius used when sweeping for the ground, so it lands on edges not through them.</summary>
        private const float SweepRadius = 0.28f;

        /// <summary>Give up and re-seat if a flight somehow never lands.</summary>
        private const float MaxFlightTime = 6f;

        public BotAirborne(NextBot bot) { _bot = bot; }

        public bool Active => _active;
        public Vector3 Velocity => _velocity;
        public bool JumpOnCooldown(float now) => now < _nextJumpAllowedAt;

        /// <summary>Brings the next allowed jump forward to <paramref name="at"/>, never later.</summary>
        public void AllowJumpBy(float at)
        {
            if (_nextJumpAllowedAt > at) _nextJumpAllowedAt = at;
        }

        private static float Gravity(NextBotSettings cfg)
        {
            var g = Mathf.Abs(Physics.gravity.y);
            if (g < 0.1f) g = 9.81f;
            return g * (cfg != null ? Mathf.Max(0.1f, cfg.GravityScale) : 1f);
        }

        // ==================================================================== launches

        /// <summary>
        /// Jump so the arc passes through <paramref name="landing"/>, peaking
        /// <c>JumpHeight</c> above whichever end is higher.
        ///
        /// Solved properly rather than tweened: from the apex height we get the launch
        /// velocity, from that the flight time, and from the time the horizontal speed
        /// needed to cover the gap. The result obeys gravity, so it reads as a real jump and
        /// the same integrator can carry it.
        /// </summary>
        public bool TryJump(Vector3 landing, NextBotSettings cfg, float now)
        {
            if (_active || cfg == null || !cfg.JumpEnabled || JumpOnCooldown(now)) return false;

            var from = _bot.transform.position;
            var flat = landing - from;
            var rise = flat.y;
            flat.y = 0f;
            var distance = flat.magnitude;

            if (distance > cfg.JumpMaxDistance) return false;
            if (distance < 0.4f && Mathf.Abs(rise) < 0.4f) return false;

            var g = Gravity(cfg);
            var apex = Mathf.Max(0.25f, cfg.JumpHeight) + Mathf.Max(0f, rise);

            // Up-speed to reach the apex, then time to rise and then fall to the landing.
            var vy = Mathf.Sqrt(2f * g * apex);
            var tUp = vy / g;
            var dropFromApex = apex - rise;
            if (dropFromApex < 0.01f) dropFromApex = 0.01f;
            var tDown = Mathf.Sqrt(2f * dropFromApex / g);
            var flight = tUp + tDown;
            if (flight < 0.05f) return false;

            _velocity = flat / flight;
            _velocity.y = vy;

            Begin(cfg, isJump: true);
            Plugin.Log.LogInfo("[Air] '" + _bot.SkinName + "' jump " + distance.ToString("0.0") +
                               "m rise " + rise.ToString("0.0") + "m, flight " + flight.ToString("0.00") + "s");
            return true;
        }

        /// <summary>
        /// Step off a ledge. Keeps whatever horizontal speed the bot already had, so it
        /// carries its run off the edge instead of dropping straight down like a lift.
        /// </summary>
        public bool TryFall(Vector3 horizontalVelocity, NextBotSettings cfg)
        {
            if (_active || cfg == null || !cfg.FallEnabled) return false;

            _velocity = horizontalVelocity;
            _velocity.y = 0f;          // a step off an edge has no upward push

            Begin(cfg, isJump: false);
            return true;
        }

        /// <summary>
        /// Launch with an explicit velocity, upward component and all.
        ///
        /// Separate from TryFall because that deliberately flattens Y - which silently threw
        /// away the upward part of a wall push and made hops look like the bot slid off the
        /// surface rather than springing from it.
        /// </summary>
        public bool TryLaunch(Vector3 velocity, NextBotSettings cfg)
        {
            if (_active || cfg == null) return false;

            _velocity = velocity;
            Begin(cfg, isJump: true);
            return true;
        }

        private void Begin(NextBotSettings cfg, bool isJump)
        {
            _active = true;
            _elapsed = 0f;
            _wasJump = isJump;

            var agent = _bot.Agent;
            if (agent != null && agent.enabled) agent.enabled = false;

            var steering = _bot.Steering;
            if (steering != null) steering.enabled = false;
        }

        // ==================================================================== flight

        /// <summary>Integrate one frame. Returns true while still airborne.</summary>
        public bool Tick(float dt, NextBotSettings cfg)
        {
            if (!_active) return false;
            if (dt <= 0f) return true;

            _elapsed += dt;

            var from = _bot.transform.position;
            _velocity.y -= Gravity(cfg) * dt;
            var to = from + _velocity * dt;

            // Sweep rather than teleport-and-test, or a fast fall tunnels through the floor.
            var delta = to - from;
            var distance = delta.magnitude;
            if (distance > 0.0001f)
            {
                RaycastHit hit;
                if (Physics.SphereCast(from, SweepRadius, delta / distance, out hit, distance,
                        NavMeshProvider.BakeMask, QueryTriggerInteraction.Ignore))
                {
                    // Only treat downward-ish contact as landing; clipping a wall on the way
                    // should scrub speed, not end the flight in mid-air.
                    if (_velocity.y <= 0f && hit.normal.y > 0.4f)
                    {
                        Land(hit.point, cfg);
                        return false;
                    }

                    // Glance off: remove the component into the surface and carry on.
                    _velocity = Vector3.ProjectOnPlane(_velocity, hit.normal);
                    to = from + _velocity * dt;
                }
            }

            _bot.transform.position = to;

            // Face the direction of travel so the billboard does not spin mid-flight.
            var travel = _velocity;
            travel.y = 0f;
            if (travel.sqrMagnitude > 0.01f)
                _bot.transform.rotation = Quaternion.LookRotation(travel.normalized, Vector3.up);

            if (_elapsed > MaxFlightTime)
            {
                Plugin.Log.LogWarning("[Air] '" + _bot.SkinName + "' never landed after " +
                                      MaxFlightTime + "s; re-seating.");
                Land(_bot.transform.position, cfg);
                return false;
            }

            return true;
        }

        private void Land(Vector3 where, NextBotSettings cfg)
        {
            _active = false;
            _velocity = Vector3.zero;
            if (_wasJump) _nextJumpAllowedAt = Time.time + (cfg != null ? cfg.JumpCooldown : 1.5f);

            var landing = where;

            var agent = _bot.Agent;
            if (agent != null)
            {
                agent.enabled = true;

                NavMeshHit hit;
                if (NavMesh.SamplePosition(landing, out hit, 5f, NavMesh.AllAreas))
                    landing = hit.position;

                if (!agent.Warp(landing)) _bot.transform.position = landing;
            }
            else
            {
                _bot.transform.position = landing;
            }

            var steering = _bot.Steering;
            if (steering != null)
            {
                steering.enabled = true;
                steering.Warp(landing);
            }

            _bot.NotifyLanded();
        }

        /// <summary>End the flight immediately without leaving the body detached.</summary>
        public void Cancel(NextBotSettings cfg)
        {
            if (_active) Land(_bot.transform.position, cfg);
        }
    }
}
