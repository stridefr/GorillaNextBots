using NextBots.Config;
using UnityEngine;
using UnityEngine.AI;

namespace NextBots.Runtime
{
    /// <summary>
    /// Climbing on arbitrary geometry, the way a Gorilla Tag player moves.
    ///
    /// This exists because NavMesh cannot express how this game is actually played. A navmesh
    /// describes walkable floor - a surface with a modest slope and a step height. Players
    /// here go up tree trunks, along branches, over rock faces and onto ledges that no bake
    /// will ever mark as walkable at any setting. A walking agent therefore has whole
    /// categories of the map permanently closed to it, and no amount of pathfinding tuning
    /// changes that.
    ///
    /// So when the mesh cannot get the bot there, it stops pretending to walk: it grabs the
    /// nearest surface and hauls itself along it toward the target, hugging the geometry and
    /// mantling over edges. Slower and less clever than pathfinding, but it can follow a
    /// player up a tree, which is the point.
    ///
    /// Detaches the moment ordinary walking becomes viable again, so this is a fallback and
    /// not the primary way the bot gets around.
    /// </summary>
    public class BotClimber : MonoBehaviour
    {
        /// <summary>How far off the surface the body sits while climbing.</summary>
        public float SurfaceOffset = 0.35f;

        /// <summary>How far it will reach to find a surface to grab.</summary>
        public float GrabRange = 2.0f;

        /// <summary>Distance searched each step to stay attached.</summary>
        public float StickRange = 1.4f;

        public bool Attached { get; private set; }
        public Vector3 SurfaceNormal { get; private set; } = Vector3.up;

        private NextBot _bot;
        private NextBotSettings _cfg;
        private float _noProgress;
        private Vector3 _lastPos;
        private float _heldFor;

        /// <summary>
        /// What counts as climbable.
        ///
        /// Explicitly not players: their body, head, hand and tag colliders are excluded, so
        /// a bot can never grab a gorilla - its own target, another player, or one of us
        /// walking past. It also excludes the throwable and cosmetic layers, which are things
        /// people carry rather than parts of the map.
        ///
        /// Bots themselves have no colliders at all, so they cannot grab each other either.
        /// </summary>
        public static int ClimbMask => NavMeshProvider.BakeMask & ~PlayerLayers;

        private static readonly int PlayerLayers =
            (1 << 7)  |   // GorillaEquipment
            (1 << 8)  |   // GorillaBodyCollider
            (1 << 10) |   // GorillaHand
            (1 << 11) |   // GorillaTrigger
            (1 << 13) |   // GorillaHead
            (1 << 14) |   // GorillaTagCollider
            (1 << 16) |   // GorillaEquipmentContainer
            (1 << 21) |   // GorillaCosmetics
            (1 << 23) |   // GorillaThrowable
            (1 << 24) |   // GorillaHandSocket
            (1 << 25);    // GorillaCosmeticParticle

        public void Init(NextBot bot, NextBotSettings cfg)
        {
            _bot = bot;
            _cfg = cfg ?? NextBotSettings.Active;
        }

        // ==================================================================== attach

        /// <summary>
        /// Grab whatever is between us and the target. Prefers a surface in the target's
        /// direction so the bot commits to the face it actually needs to go up, rather than
        /// whichever happens to be nearest.
        /// </summary>
        public bool TryAttach(Vector3 targetPos)
        {
            if (Attached || _cfg == null || !_cfg.ClimbEnabled) return false;

            var origin = transform.position + Vector3.up * 0.6f;
            var toTarget = targetPos - origin;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.01f) return false;
            toTarget.Normalize();

            // Straight at it first, then fan out - a tree trunk is rarely square-on.
            float[] fan = { 0f, 25f, -25f, 50f, -50f, 80f, -80f };
            for (int i = 0; i < fan.Length; i++)
            {
                var dir = Quaternion.AngleAxis(fan[i], Vector3.up) * toTarget;

                RaycastHit hit;
                if (!Physics.Raycast(origin, dir, out hit, GrabRange,
                        ClimbMask, QueryTriggerInteraction.Ignore))
                    continue;

                // A near-flat surface is floor, not something to climb - walking handles it.
                if (hit.normal.y > 0.7f) continue;

                Attach(hit.point, hit.normal);
                return true;
            }

            return false;
        }

        private void Attach(Vector3 point, Vector3 normal)
        {
            Attached = true;
            SurfaceNormal = normal;
            _noProgress = 0f;
            _heldFor = 0f;
            _lastPos = transform.position;

            if (_bot.Agent != null && _bot.Agent.enabled) _bot.Agent.enabled = false;
            if (_bot.Steering != null) _bot.Steering.enabled = false;

            transform.position = point + normal * SurfaceOffset;
            Plugin.Log.LogInfo("[Climb] " + _bot.SkinName + " grabbed a surface at " +
                               point.ToString("0.0"));
        }

        public void Detach(bool reseat)
        {
            if (!Attached) return;
            Attached = false;
            SurfaceNormal = Vector3.up;

            if (_bot.Agent != null)
            {
                _bot.Agent.enabled = true;
                if (reseat)
                {
                    NavMeshHit hit;
                    if (NavMesh.SamplePosition(transform.position, out hit, 4f, NavMesh.AllAreas))
                        _bot.Agent.Warp(hit.position);
                }
            }
            if (_bot.Steering != null) _bot.Steering.enabled = true;
        }

        // ==================================================================== climb

        /// <summary>
        /// Haul along the surface toward the target. Returns false when it has let go - either
        /// because it topped out, lost the surface, or walking became possible again.
        /// </summary>
        public bool Tick(Vector3 targetPos, float dt)
        {
            if (!Attached || dt <= 0f) return false;

            _heldFor += dt;
            var pos = transform.position;

            // Grip has a time limit. Rather than crawling the whole way up, push off toward
            // the target and let the arc do the rest - a wall is a launch point.
            if (_heldFor >= _cfg.MaxClimbTime)
            {
                if (_cfg.WallHopEnabled && TryWallHop(targetPos)) return false;

                Plugin.Log.LogInfo("[Climb] " + _bot.SkinName + " let go after " +
                                   _heldFor.ToString("0.0") + "s.");
                Detach(reseat: true);
                return false;
            }

            // Once level with the target and standing on something walkable, go back to
            // pathfinding - climbing is the fallback, not the preferred gait.
            if (pos.y >= targetPos.y - 0.5f)
            {
                NavMeshHit navHit;
                if (NavMesh.SamplePosition(pos, out navHit, 2.5f, NavMesh.AllAreas))
                {
                    Plugin.Log.LogInfo("[Climb] " + _bot.SkinName + " topped out and let go.");
                    Detach(reseat: true);
                    return false;
                }
            }

            // Move along the surface, toward the target as far as the surface allows.
            var toTarget = targetPos - pos;
            var along = Vector3.ProjectOnPlane(toTarget, SurfaceNormal);

            // Nearly parallel to the surface normal means the target is straight through the
            // wall; climb upward instead of grinding into it.
            if (along.sqrMagnitude < 0.01f)
                along = Vector3.ProjectOnPlane(Vector3.up, SurfaceNormal);
            if (along.sqrMagnitude < 0.01f) { Detach(reseat: true); return false; }

            along.Normalize();

            var speed = _cfg.Speed * _cfg.ClimbSpeedScale;
            var next = pos + along * speed * dt;

            // Re-anchor: feel for the surface from just off it. This is what keeps the bot
            // following a curved trunk instead of walking off into the air.
            RaycastHit stick;
            var probeFrom = next + SurfaceNormal * SurfaceOffset;
            if (Physics.Raycast(probeFrom, -SurfaceNormal, out stick, StickRange,
                    ClimbMask, QueryTriggerInteraction.Ignore))
            {
                SurfaceNormal = stick.normal;
                transform.position = stick.point + stick.normal * SurfaceOffset;
            }
            else
            {
                // Surface ran out - we are at an edge. Try to mantle over the top of it,
                // which is how a climb up a trunk becomes standing on a branch.
                if (!TryMantle(next, along))
                {
                    Plugin.Log.LogInfo("[Climb] " + _bot.SkinName + " lost the surface.");
                    Detach(reseat: true);
                    return false;
                }
            }

            // Face into the surface, so the billboard leans against what it is climbing.
            var face = Vector3.ProjectOnPlane(-SurfaceNormal, Vector3.up);
            if (face.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(face.normalized, Vector3.up);

            // Give up if the climb is going nowhere, rather than scrabbling forever.
            if ((transform.position - _lastPos).sqrMagnitude < 0.0004f) _noProgress += dt;
            else _noProgress = 0f;
            _lastPos = transform.position;

            if (_noProgress > 2f)
            {
                Plugin.Log.LogInfo("[Climb] " + _bot.SkinName + " made no progress; letting go.");
                Detach(reseat: true);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Kick off the wall toward the target.
        ///
        /// Aims for a landing if there is a sensible one, and otherwise just launches up and
        /// away from the surface - a bot springing off a wall and arcing toward you looks far
        /// more like the thing chasing you than one steadily crawling.
        /// </summary>
        private bool TryWallHop(Vector3 targetPos)
        {
            var air = _bot.Airborne;
            if (air == null) return false;

            var pos = transform.position;

            // Somewhere to actually land, in the target's direction.
            var toTarget = targetPos - pos;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.04f)
            {
                toTarget.Normalize();

                for (var d = _cfg.JumpMaxDistance; d >= 1.5f; d -= 0.8f)
                {
                    var probe = pos + toTarget * d;
                    RaycastHit ground;
                    if (!Physics.Raycast(probe + Vector3.up * (_cfg.ClimbMaxHeight + 0.5f),
                            Vector3.down, out ground,
                            _cfg.ClimbMaxHeight + _cfg.LedgeMaxDrop + 1f,
                            ClimbMask, QueryTriggerInteraction.Ignore))
                        continue;

                    if (ground.normal.y < 0.6f) continue;

                    var landing = ground.point + Vector3.up * 0.05f;
                    if (Physics.Linecast(pos + Vector3.up * 0.3f, landing + Vector3.up * 0.6f,
                            ClimbMask, QueryTriggerInteraction.Ignore))
                        continue;

                    // Let go before launching, or the agent is re-enabled mid-flight.
                    Detach(reseat: false);
                    if (air.TryJump(landing, _cfg, Time.time))
                    {
                        Plugin.Log.LogInfo("[Climb] " + _bot.SkinName + " hopped off the wall " +
                                           d.ToString("0.0") + "m toward the target.");
                        return true;
                    }
                    return false;
                }
            }

            // Nothing to aim at - just spring off the surface so it does not simply cling.
            var away = SurfaceNormal * 3.5f + Vector3.up * 3f;
            Detach(reseat: false);
            if (air.TryLaunch(away, _cfg))
            {
                Plugin.Log.LogInfo("[Climb] " + _bot.SkinName + " pushed off the wall.");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Pull up over the lip of the surface we have been climbing. Looks for ground just
        /// beyond and above the edge and steps onto it.
        /// </summary>
        private bool TryMantle(Vector3 edgePos, Vector3 along)
        {
            var over = edgePos + along * 0.5f + Vector3.up * 0.3f;

            RaycastHit top;
            if (!Physics.Raycast(over + Vector3.up * 0.6f, Vector3.down, out top, 1.6f,
                    ClimbMask, QueryTriggerInteraction.Ignore))
                return false;

            if (top.normal.y < 0.5f) return false;      // not something to stand on

            transform.position = top.point + Vector3.up * 0.05f;
            SurfaceNormal = Vector3.up;

            // Standing on top now; if it is walkable ground, hand back to the agent.
            NavMeshHit navHit;
            if (NavMesh.SamplePosition(transform.position, out navHit, 2f, NavMesh.AllAreas))
            {
                Plugin.Log.LogInfo("[Climb] " + _bot.SkinName + " mantled onto a ledge.");
                Detach(reseat: true);
                return false;      // no longer climbing, but this is success
            }

            return true;           // on top of something unbaked; keep climbing from here
        }
    }
}
