using System.Collections.Generic;
using NextBots.Brain;
using NextBots.Config;
using NextBots.Debugging;
using UnityEngine;
using UnityEngine.AI;

namespace NextBots.Runtime
{
    /// <summary>
    /// One bot. Owns a NavMeshAgent, a billboard, a brain and (optionally) a debug overlay.
    ///
    /// The body has two modes, chosen per-bot at spawn:
    ///
    /// - **Rides a GT <see cref="GameAgent"/>** (Option A). Destination and speed go through
    ///   the agent, which replicates them itself; every other client watches a replicated
    ///   transform and runs its own NavMeshAgent toward the same destination.
    /// - **Standalone** (Option B). We own the NavMeshAgent outright and something else is
    ///   responsible for broadcasting our transform.
    ///
    /// The brain does not know which - it only ever sees <see cref="IBotBody"/>.
    /// </summary>
    public class NextBot : MonoBehaviour, IBotBody
    {
        public NavMeshAgent Agent;
        public GameAgent HostAgent;          // non-null only under Option A
        public SteeringLocomotor Steering;   // used only when there is no NavMesh at all
        public BotVisual Visual;
        public BotAudio Audio;
        public BotClimber Climber;
        public NextBotBrain Brain;

        public int SkinIndex;
        public string SkinName = "";

        /// <summary>The skin's file name without extension, case kept - what the death log shows.</summary>
        public string DisplayName = "";

        /// <summary>
        /// How fast the bot is really moving, measured from its own motion every frame.
        ///
        /// <para>Not <see cref="Velocity"/>: that asks the agent, which knows nothing about jumps,
        /// climbs or falls, and a guest's copy has no agent at all. Measuring the transform works
        /// the same on the host and on every guest, whatever is moving the bot.</para>
        /// </summary>
        public Vector3 MeasuredVelocity { get; private set; }
        private Vector3 _lastPos;
        private bool _hasLastPos;

        /// <summary>Where this bot was originally placed. Used to recover if it falls out of the world.</summary>
        public Vector3 SpawnPosition;

        /// <summary>Only the authority runs the brain. Everyone else just watches.</summary>
        public bool IsAuthority = true;

        /// <summary>Identity across the lobby. Assigned by the host and never reused.</summary>
        public int NetId = -1;

        // Where the host last said this bot is. Remote bots ease toward it rather than
        // snapping, because state arrives at 10Hz and the eye notices 10Hz.
        private Vector3 _netPos;
        private float _netYaw;
        private bool _hasNetTarget;
        private Vector3 _netVel;
        private float _netAt;

        private BotDebugVisual _debug;
        private NextBotSettings _cfg;
        private readonly NavMeshPath _pathScratch = new NavMeshPath();
        private int _offMeshTicks;
        private Vector3 _lastIssuedDest = new Vector3(99999f, 99999f, 99999f);
        private int _noPathTicks;
        private BotAirborne _air;

        /// <summary>The ballistic integrator, so the climber can launch off a wall through it.</summary>
        public BotAirborne Airborne => _air;

        /// <summary>Called by the integrator on touchdown, so we can require ground time.</summary>
        public void NotifyLanded() => _lastLandedAt = Time.time;
        private float _facingTarget;
        private Vector3 _faceAt;
        private bool _hasFaceAt;
        private Vector3 _climbTarget;
        private float _lastLandedAt;

        public bool Alive { get; private set; } = true;

        // ================================================================== setup

        public void Initialise(NextBotSettings cfg, BotSkins.Skin skin, int skinIndex)
        {
            _cfg = cfg ?? NextBotSettings.Active;
            _air = new BotAirborne(this);

            if (Climber == null) Climber = gameObject.AddComponent<BotClimber>();
            Climber.Init(this, _cfg);
            Brain = new NextBotBrain(_cfg);
            Brain.Reset();

            SkinIndex = skinIndex;
            SkinName = skin != null ? skin.Name : "?";
            DisplayName = skin != null && !string.IsNullOrEmpty(skin.DisplayName) ? skin.DisplayName : SkinName;

            if (Visual == null) Visual = gameObject.AddComponent<BotVisual>();
            Visual.Apply(skin);

            if (skin != null && skin.Clip != null)
            {
                if (Audio == null) Audio = gameObject.AddComponent<BotAudio>();
                Audio.Setup(skin.Clip, _cfg);
            }

            if (Agent != null)
            {
                Agent.speed = _cfg.Speed;
                Agent.acceleration = _cfg.Acceleration;
                Agent.angularSpeed = _cfg.TurnSpeed;
                Agent.stoppingDistance = 0f;
                Agent.autoBraking = false;   // we control approach via the overshoot instead
            }
        }

        // ================================================================== tick

        /// <summary>
        /// Advance the brain. Called by the manager at a fixed rate rather than per frame -
        /// the raycasts are the expensive part and 10 Hz is plenty for decisions.
        /// </summary>
        public void BrainTick(WorldView world, float dt)
        {
            if (!Alive || !IsAuthority || Brain == null) return;

            // Mid-flight the body belongs to the arc. This check has to come FIRST: running
            // fall-recovery on an airborne bot teleports it back to its spawn point in the
            // middle of a jump, which reads as the bot flying about and glitching.
            if (_air != null && _air.Active) return;

            if (RecoverIfFallen()) return;

            // Likewise while climbing: the climber owns movement until it lets go.
            if (Climber != null && Climber.Attached)
            {
                // Refresh the goal so a climbing bot tracks a player who keeps moving.
                NextBots.Brain.PlayerSnapshot climbing;
                if (world != null && Brain.TargetActor >= 0 &&
                    world.TryGetPlayer(Brain.TargetActor, out climbing))
                    _climbTarget = climbing.Center;
                return;
            }

            ApplyTuning();
            EnsureCanMove(world);

            if (!_cfg.BotsMove)
            {
                SetStopped(true);
                TickAudio(world, dt);
                return;
            }

            if (TryJumpIfUseful(world)) return;

            Brain.Tick(this, world, dt);
            LogIfStuck();
            TickAudio(world, dt);

            int caught;
            if (Brain.TryCatch(this, world, out caught)) OnCaught?.Invoke(this, caught);

            if (_cfg != null && _cfg.DebugOverlay)
            {
                EnsureDebug();
                _debug.SetVisible(true);
                _debug.Render(this, Brain, world);
            }
            else if (_debug != null) _debug.SetVisible(false);
        }

        /// <summary>Raised on the authority when this bot reaches its target.</summary>
        public System.Action<NextBot, int> OnCaught;

        /// <summary>
        /// The part of a tick a guest's puppet still needs: its chase sound, which depends on
        /// where *our* ears are and so cannot be sent by the host.
        /// </summary>
        public void RemoteTick(WorldView world, float dt)
        {
            if (!Alive || IsAuthority) return;
            TickAudio(world, dt);
        }

        /// <summary>
        /// Feed the chase loop. Occlusion reuses the sightline the brain already needs, so
        /// hearing a bot go quiet behind a wall costs no extra raycasts.
        /// </summary>
        private void TickAudio(WorldView world, float dt)
        {
            if (Audio == null || world == null) return;

            var listener = UI.UiResources.Viewer;
            if (listener == null) { Audio.Tick(0f, 0f, dt); return; }

            var ear = listener.position;
            var distance = Vector3.Distance(Center, ear);
            Audio.Tick(HiddenFraction(world, ear), distance, dt);
        }

        /// <summary>
        /// How much of the bot a wall hides from <paramref name="ear"/>: five lines across its
        /// body, counted. One line flipped by any post, branch or railing it grazed, which made
        /// the muffle jump on and off at the slightest thing.
        /// </summary>
        private float HiddenFraction(WorldView world, Vector3 ear)
        {
            var toEar = ear - Center;
            toEar.y = 0f;
            var side = toEar.sqrMagnitude > 0.01f ? Vector3.Cross(Vector3.up, toEar.normalized) : Vector3.right;
            float r = 0.4f;
            int hidden = 0;
            if (world.LineBlocked(Center, ear)) hidden++;
            if (world.LineBlocked(Center + side * r, ear)) hidden++;
            if (world.LineBlocked(Center - side * r, ear)) hidden++;
            if (world.LineBlocked(Center + Vector3.up * r, ear)) hidden++;
            if (world.LineBlocked(Center - Vector3.up * r * 0.6f, ear)) hidden++;
            return hidden <= 1 ? 0f : (hidden - 1) / 4f;
        }

        /// <summary>
        /// Push the tunables onto the live body every tick, so changes made in the panel take
        /// effect immediately instead of only on newly spawned bots. Acceleration especially:
        /// it is the main lever on how jukeable the bot feels, and tuning it is useless if it
        /// only applies at spawn.
        /// </summary>
        private void ApplyTuning()
        {
            if (_cfg == null) return;

            if (Agent != null)
            {
                Agent.acceleration = _cfg.Acceleration;
                Agent.angularSpeed = _cfg.TurnSpeed;
            }
            else if (Steering != null)
            {
                Steering.Speed = _cfg.Speed;
            }
        }

        private float _nextStuckLog;

        /// <summary>
        /// Dump the agent's full state while a bot is stuck. Every "it just stands there"
        /// report so far has had a different cause, and each round of guessing costs a test
        /// cycle - so make the bot say exactly which field is wrong.
        /// </summary>
        private void LogIfStuck()
        {
            if (Brain == null || Brain.StuckTime < 1f) return;
            if (Time.time < _nextStuckLog) return;
            _nextStuckLog = Time.time + 1f;

            if (Agent == null)
            {
                if (Steering == null)
                {
                    Plugin.Log.LogWarning("[BotDiag] '" + SkinName + "' stuck " +
                        Brain.StuckTime.ToString("0.0") + "s | NO BODY AT ALL (no agent, no steering)");
                    return;
                }

                var toDest = Steering.Destination - transform.position;
                toDest.y = 0f;

                Plugin.Log.LogWarning("[BotDiag] '" + SkinName + "' stuck " + Brain.StuckTime.ToString("0.0") + "s" +
                    " STEERING state=" + Brain.State +
                    " pos=" + transform.position.ToString("0.0") +
                    " dest=" + Steering.Destination.ToString("0.0") +
                    " planarDist=" + toDest.magnitude.ToString("0.0") +
                    " stopped=" + Steering.Stopped +
                    " speed=" + Steering.Speed.ToString("0.0") +
                    " vel=" + Steering.Velocity.magnitude.ToString("0.00") +
                    " grounded=" + Steering.Grounded +
                    " enabled=" + Steering.enabled +
                    " activeSelf=" + gameObject.activeInHierarchy +
                    " timeScale=" + Time.timeScale.ToString("0.00"));
                return;
            }

            Plugin.Log.LogWarning("[BotDiag] '" + SkinName + "' stuck " + Brain.StuckTime.ToString("0.0") + "s" +
                " state=" + Brain.State +
                " pos=" + transform.position.ToString("0.0") +
                " onMesh=" + Agent.isOnNavMesh +
                " enabled=" + Agent.enabled +
                " stopped=" + (Agent.isOnNavMesh ? Agent.isStopped.ToString() : "?") +
                " hasPath=" + Agent.hasPath +
                " pending=" + Agent.pathPending +
                " status=" + (Agent.hasPath ? Agent.pathStatus.ToString() : "-") +
                " dest=" + (Agent.hasPath ? Agent.destination.ToString("0.0") : "-") +
                " rem=" + (Agent.hasPath ? Agent.remainingDistance.ToString("0.0") : "-") +
                " vel=" + Agent.velocity.magnitude.ToString("0.00") +
                " desired=" + Agent.desiredVelocity.magnitude.ToString("0.00") +
                " speed=" + Agent.speed.ToString("0.0") +
                " radius=" + Agent.radius.ToString("0.00") +
                " height=" + Agent.height.ToString("0.00") +
                " lastIssued=" + _lastIssuedDest.ToString("0.0") +
                " reachable=" + Reachable(_lastIssuedDest));
        }

        /// <summary>
        /// Does a complete route exist from here to there right now, and if not, why not?
        ///
        /// Reports how far the nearest navmesh is from both ends, because "no route" and
        /// "there is no mesh under the target at all" look identical from the agent's API
        /// and need completely different fixes.
        /// </summary>
        private string Reachable(Vector3 to)
        {
            if (Agent == null || !Agent.isOnNavMesh) return "?";

            NavMeshHit hit;
            var mapped = NavMesh.SamplePosition(to, out hit, 25f, NavMesh.AllAreas);
            var dstInfo = mapped
                ? "dstMesh=" + Vector3.Distance(to, hit.position).ToString("0.0") + "m"
                : "dstMesh=MISS@25m";

            NavMeshHit selfHit;
            var selfMapped = NavMesh.SamplePosition(transform.position, out selfHit, 25f, NavMesh.AllAreas);
            var selfInfo = selfMapped
                ? " selfMesh=" + Vector3.Distance(transform.position, selfHit.position).ToString("0.0") + "m"
                : " selfMesh=MISS@25m";

            var typeInfo = " agentType=" + Agent.agentTypeID + " areaMask=" + Agent.areaMask;

            if (!mapped) return "NO-MESH-AT-TARGET " + dstInfo + selfInfo + typeInfo;

            if (!Agent.CalculatePath(hit.position, _pathScratch))
                return "NO-CALC " + dstInfo + selfInfo + typeInfo;

            return _pathScratch.status + " corners=" + _pathScratch.corners.Length + " " +
                   dstInfo + selfInfo;
        }

        /// <summary>
        /// A NavMeshAgent that falls off the mesh is inert: no path, no movement, and
        /// SetDestination silently does nothing. The brain then reads it as "stuck", charges,
        /// and finally teleports - which is what "it keeps teleporting to me" actually was.
        ///
        /// Bots get stranded routinely because the baked volume re-centres on the player, so
        /// ground a bot is standing on can stop being covered. Recover by warping back onto
        /// the nearest mesh; if that keeps failing, give up on pathfinding for this bot and
        /// switch it to steering permanently, because a dumb bot that moves beats a clever
        /// one that cannot.
        /// </summary>
        private void EnsureCanMove(WorldView world)
        {
            if (Agent == null || !Agent.enabled) return;

            if (Agent.isOnNavMesh)
            {
                _offMeshTicks = 0;
                return;
            }

            _offMeshTicks++;

            // Warping back onto the mesh is itself a visible snap. If the target is watching,
            // hand the bot to steering instead - it keeps moving smoothly, just less cleverly.
            // The warp is saved for when nobody can see it happen.
            if (!_cfg.TeleportWhenSeen && _offMeshTicks >= 3 && IsWatched(world))
            {
                Plugin.Log.LogInfo("[Bots] '" + SkinName + "' off-mesh while watched; " +
                                   "steering rather than snapping.");
                SwitchToSteering();
                return;
            }

            // Widen the search as attempts fail - a small radius will not find the mesh if
            // the bot has been left well outside it.
            var radius = Mathf.Min(4f + _offMeshTicks * 4f, 40f);
            NavMeshHit hit;
            if (NavMesh.SamplePosition(transform.position, out hit, radius, NavMesh.AllAreas) &&
                Agent.Warp(hit.position))
            {
                Plugin.Log.LogInfo("[Bots] '" + SkinName + "' was off-mesh; warped back on after " +
                                   _offMeshTicks + " tick(s).");
                _offMeshTicks = 0;
                Brain.Reset();
                return;
            }

            if (_offMeshTicks < 20) return;   // ~2s at 10Hz before giving up

            Plugin.Log.LogWarning("[Bots] '" + SkinName + "' could not rejoin the NavMesh; " +
                                  "switching to steering permanently.");
            SwitchToSteering();
        }

        /// <summary>
        /// Bots occasionally fall out of the world - through a seam, off the end of a
        /// runtime-baked surface, or by being spawned over a hole. Rather than let one plummet
        /// forever while the brain reports a confident CHASE, put it back where it started.
        /// </summary>
        private bool RecoverIfFallen()
        {
            var pos = transform.position;
            var fallen = pos.y < SpawnPosition.y - 30f || pos.y < -500f;
            if (!fallen) return false;

            Plugin.Log.LogWarning("[Bots] '" + SkinName + "' fell out of the world at " + pos +
                                  "; returning it to its spawn point.");

            var target = SpawnPosition;
            NavMeshHit hit;
            if (NavMesh.SamplePosition(target, out hit, 10f, NavMesh.AllAreas)) target = hit.position;

            Warp(target);
            _lastIssuedDest = new Vector3(99999f, 99999f, 99999f);
            _offMeshTicks = 0;
            if (Brain != null) Brain.Reset();
            return true;
        }

        /// <summary>
        /// Jump when it is the only way forward.
        ///
        /// Two triggers. A NavMesh off-mesh link is the clean case - the mesh itself says
        /// "there is a gap here, cross it". The other is being wedged: a bot stuck on scenery
        /// (a tree root, a ledge, the lip of a planter) has no route out on the mesh, and a
        /// hop toward the target clears exactly that. Jumping before the stuck escalation
        /// teleports is much nicer to watch than the teleport.
        /// </summary>
        private bool TryJumpIfUseful(WorldView world)
        {
            if (_air == null || _cfg == null) return false;

            var target = default(NextBots.Brain.PlayerSnapshot);
            var haveTarget = false;
            if (world != null && Brain != null && Brain.TargetActor >= 0)
                haveTarget = world.TryGetPlayer(Brain.TargetActor, out target);

            // 1. The mesh says there is a gap to cross.
            if ((_cfg.JumpEnabled || _cfg.FallEnabled) && Agent != null && Agent.isOnNavMesh && Agent.isOnOffMeshLink)
            {
                var link = Agent.currentOffMeshLinkData;
                if (link.valid && _air.TryJump(link.endPos, _cfg, Time.time)) return true;
            }

            // 2. A ledge between us and the target, worth dropping off. Checked before the
            //    stuck timer, because running off a balcony to reach someone below is normal
            //    pursuit rather than a recovery - waiting to be "stuck" first looks hesitant.
            if (haveTarget && _cfg.FallEnabled && TryStepOffLedge(target.Position)) return true;

            // At the end of the route with the target still out of reach - usually right below
            // someone on a ledge. Standing there until the jump cooldown or the stuck timer
            // runs out is the pause before the leap; go straight for it instead.
            bool deadEnd = haveTarget && AtRouteEnd(target);
            if (deadEnd) _air.AllowJumpBy(_lastLandedAt + 0.25f);

            // 3. The target is above us. Source calls this ClimbUpToLedge and treats it as a
            //    separate capability from jumping gaps - without it a bot just mills about
            //    underneath someone standing on a crate, which is exactly what ours did.
            if (haveTarget && _cfg.ClimbEnabled && TryClimbToward(target.Position)) return true;

            // 4. Parkour: jump between surfaces as part of ordinary pursuit. This is the
            //    movement players actually use - clearing a gap, hopping onto a rock, cutting
            //    a corner the walking route goes the long way around. It runs before the
            //    stuck check on purpose, because a bot that only jumps once it has failed to
            //    move looks like it is recovering rather than chasing.
            if (haveTarget && _cfg.ParkourEnabled && TryParkourJump(target)) return true;

            // 5. Surface climbing, off by default - it is a different kind of movement and
            //    reads as spidering up walls rather than running and jumping.
            if (haveTarget && _cfg.SurfaceClimbEnabled && ShouldSurfaceClimb(target))
            {
                _climbTarget = target.Center;
                if (Climber != null && Climber.TryAttach(target.Center)) return true;
            }

            // 6. Wedged. Only once the bot has genuinely failed to move for a while, so a
            //    normal chase never turns into hopping - or at once at a dead end, above.
            if (!_cfg.JumpEnabled || Brain == null || !haveTarget) return false;
            if (!deadEnd && Brain.StuckTime < _cfg.StuckChargeTime) return false;

            var landing = FindLandingToward(target.Position);
            if (!landing.HasValue) return false;

            return _air.TryJump(landing.Value, _cfg, Time.time);
        }

        /// <summary>
        /// Arrived as far as walking goes, target close but not caught, and no complete walking
        /// route to them.
        /// </summary>
        private bool AtRouteEnd(NextBots.Brain.PlayerSnapshot target)
        {
            if (Agent == null || !Agent.isOnNavMesh || Agent.pathPending) return false;
            if (Agent.hasPath && Agent.remainingDistance > 0.4f) return false;

            var flat = target.Position - transform.position;
            flat.y = 0f;
            if (flat.magnitude > 8f) return false;

            if (Agent.CalculatePath(target.Position, _pathScratch) &&
                _pathScratch.status == NavMeshPathStatus.PathComplete)
                return false;
            return true;
        }

        /// <summary>
        /// A running jump onto another surface, taken as part of the chase.
        ///
        /// Two situations make it worth leaving the ground: the walking route is much longer
        /// than the straight line (so jumping cuts a real corner), or there is no walking
        /// route at all. In both cases we look for somewhere to land that genuinely gets us
        /// closer, and let the ballistic solver work out the arc.
        ///
        /// Reach scales with current speed, so this is a *running* jump - a bot at a sprint
        /// clears gaps a stationary one will not attempt, exactly as a player does.
        /// </summary>
        private bool TryParkourJump(NextBots.Brain.PlayerSnapshot target)
        {
            if (_air == null || Agent == null || !Agent.isOnNavMesh) return false;
            if (!_cfg.JumpEnabled || _air.JumpOnCooldown(Time.time)) return false;

            // Must have both feet under it for a moment first. Without this a bot lands and
            // immediately launches again the instant the cooldown lapses, which turns a chase
            // into continuous hopping across the map.
            if (Time.time - _lastLandedAt < 0.6f) return false;

            // A running jump needs a run. A near-stationary bot springing off the spot looks
            // wrong, and it is usually a sign the jump is being used to escape a problem
            // rather than to travel.
            if (Agent.velocity.magnitude < _cfg.Speed * 0.35f) return false;

            var from = transform.position;
            var directDistance = Vector3.Distance(from, target.Position);

            // Close enough to just walk in.
            if (directDistance < 2.5f) return false;

            // Is walking actually worse than jumping here?
            //
            // Defaulting this to "yes" was a mistake: the navmesh frequently cannot reach the
            // target at all, so it was true nearly always and bots jumped on every cooldown.
            // No route is a reason to jump, but only occasionally, not constantly.
            var walkingIsPoor = false;
            if (Agent.CalculatePath(target.Position, _pathScratch))
            {
                if (_pathScratch.status == NavMeshPathStatus.PathComplete)
                {
                    var routeLength = PathLength(_pathScratch);
                    walkingIsPoor = routeLength > directDistance * _cfg.ParkourShortcutRatio;
                }
                else
                {
                    // Partial or invalid: jumping may genuinely be the only way on, but hold
                    // off until the walking attempt has visibly stalled.
                    walkingIsPoor = Brain != null && Brain.StuckTime > 0.6f;
                }
            }
            if (!walkingIsPoor) return false;

            // Running jumps go further than standing ones.
            var speedFraction = Mathf.Clamp01(Agent.velocity.magnitude / Mathf.Max(1f, _cfg.Speed));
            var reach = _cfg.JumpMaxDistance *
                        Mathf.Lerp(0.5f, _cfg.RunningJumpBonus, speedFraction);

            var landing = FindJumpLanding(target.Position, reach, directDistance);
            if (!landing.HasValue) return false;

            if (!_air.TryJump(landing.Value, _cfg, Time.time)) return false;

            Plugin.Log.LogInfo("[Air] " + SkinName + " parkour jump " +
                               Vector3.Distance(from, landing.Value).ToString("0.0") +
                               "m (speed " + Agent.velocity.magnitude.ToString("0.0") + ")");
            return true;
        }

        /// <summary>True when the navmesh runs unbroken in a straight line from here to there at
        /// about the same height - no gap, no ledge - so walking is the way.</summary>
        private static bool WalkableStraight(Vector3 from, Vector3 to)
        {
            NavMeshHit a, b, edge;
            if (!NavMesh.SamplePosition(from, out a, 0.6f, NavMesh.AllAreas)) return false;
            if (!NavMesh.SamplePosition(to, out b, 0.6f, NavMesh.AllAreas)) return false;
            if (Mathf.Abs(b.position.y - to.y) > 0.5f) return false;
            return !NavMesh.Raycast(a.position, b.position, out edge, NavMesh.AllAreas);
        }

        private static float PathLength(NavMeshPath path)
        {
            var corners = path.corners;
            if (corners == null || corners.Length < 2) return 0f;

            var total = 0f;
            for (int i = 1; i < corners.Length; i++)
                total += Vector3.Distance(corners[i - 1], corners[i]);
            return total;
        }

        /// <summary>
        /// Somewhere to land that actually helps: nearer the target than we are, standable,
        /// with a clear arc. Searched furthest-first so the bot takes the biggest useful
        /// jump rather than a timid hop, and geometrically rather than via the navmesh so it
        /// can land on rocks, crates and ledges the bake never covered.
        /// </summary>
        private Vector3? FindJumpLanding(Vector3 targetPos, float reach, float currentDistance)
        {
            var from = transform.position;
            var toTarget = targetPos - from;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.04f) return null;
            toTarget.Normalize();

            var eye = from + Vector3.up * 0.6f;

            // Mostly forward, with a little spread so it can take an angled stepping stone.
            float[] fan = { 0f, 18f, -18f, 35f, -35f };

            for (var d = reach; d >= 2f; d -= 0.75f)
            {
                for (int f = 0; f < fan.Length; f++)
                {
                    var dir = Quaternion.AngleAxis(fan[f], Vector3.up) * toTarget;
                    var probe = from + dir * d;

                    // Look for a surface anywhere in the band we could plausibly reach:
                    // above us (hop up onto) through below us (drop across to).
                    RaycastHit ground;
                    var castFrom = probe + Vector3.up * (_cfg.ClimbMaxHeight + 0.5f);
                    var castLength = _cfg.ClimbMaxHeight + _cfg.LedgeMaxDrop + 1f;

                    if (!Physics.Raycast(castFrom, Vector3.down, out ground, castLength,
                            NavMeshProvider.BakeMask, QueryTriggerInteraction.Ignore))
                        continue;

                    if (ground.normal.y < 0.6f) continue;            // not standable

                    var landing = ground.point + Vector3.up * 0.05f;
                    var rise = landing.y - from.y;
                    if (rise > _cfg.ClimbMaxHeight) continue;        // too high to reach
                    if (rise < -_cfg.LedgeMaxDrop) continue;         // too far to fall

                    // Must actually make progress toward the target.
                    if (Vector3.Distance(landing, targetPos) > currentDistance - 1f) continue;

                    // Nothing to jump: a straight, unbroken walk gets there.
                    if (WalkableStraight(from, landing)) continue;

                    // Headroom to occupy, and a clear arc to get there.
                    if (Physics.Raycast(landing + Vector3.up * 0.1f, Vector3.up, 1.2f,
                            NavMeshProvider.BakeMask, QueryTriggerInteraction.Ignore))
                        continue;

                    if (Physics.Linecast(eye, landing + Vector3.up * 0.8f,
                            NavMeshProvider.BakeMask, QueryTriggerInteraction.Ignore))
                        continue;

                    return landing;
                }
            }

            return null;
        }

        /// <summary>
        /// Worth abandoning the navmesh for? Only when the target is meaningfully above and
        /// there is no walkable route to them - otherwise walking is always the better gait.
        /// </summary>
        private bool ShouldSurfaceClimb(NextBots.Brain.PlayerSnapshot target)
        {
            if (Climber == null || Climber.Attached) return false;

            var rise = target.Center.y - Center.y;
            if (rise < _cfg.ClimbTriggerHeight) return false;

            // A complete walking route beats climbing every time.
            if (Agent != null && Agent.isOnNavMesh &&
                Agent.CalculatePath(target.Position, _pathScratch) &&
                _pathScratch.status == NavMeshPathStatus.PathComplete)
                return false;

            return true;
        }

        /// <summary>
        /// Haul up onto a ledge when the target is above us and we cannot walk to them.
        ///
        /// Modelled on Source's ClimbUpToLedge: search upward in steps for a spot that is on
        /// the mesh, above us, and forward of us, then launch a ballistic arc onto it. Only
        /// runs when there is no ordinary route, so a bot that can simply take the stairs
        /// still does.
        /// </summary>
        private bool TryClimbToward(Vector3 targetPos)
        {
            if (_air == null) return false;

            var rise = targetPos.y - transform.position.y;
            if (rise < _cfg.StepHeight * 1.5f) return false;      // not meaningfully above

            // If we can already walk the whole way, walking beats climbing.
            if (Agent != null && Agent.isOnNavMesh &&
                Agent.CalculatePath(targetPos, _pathScratch) &&
                _pathScratch.status == NavMeshPathStatus.PathComplete)
                return false;

            var dir = targetPos - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.04f) dir = transform.forward;
            dir.Normalize();

            var ledge = FindClimbLedge(dir);
            if (!ledge.HasValue) return false;

            if (!_air.TryJump(ledge.Value, _cfg, Time.time)) return false;

            Plugin.Log.LogInfo("[Air] " + SkinName + " climbed " +
                               (ledge.Value.y - transform.position.y).ToString("0.0") +
                               "m onto a ledge toward the target.");
            return true;
        }

        /// <summary>
        /// Find something to climb onto, using geometry rather than the navmesh.
        ///
        /// The earlier version only accepted a landing that NavMesh.SamplePosition agreed
        /// with, which meant a bot could never climb anything the bake had not covered - and
        /// the bake does not cover narrow surfaces like tree branches, railings or crates.
        /// So it stood underneath them instead.
        ///
        /// Instead: sweep upward, cast forward to find a face, then feel along the top of it
        /// for somewhere flat enough to stand. Landing off-mesh is fine - the airborne
        /// integrator lands on real geometry, and the off-mesh recovery re-seats the bot or
        /// hands it to steering.
        /// </summary>
        private Vector3? FindClimbLedge(Vector3 dir)
        {
            var basePos = transform.position;
            var reach = Mathf.Min(2.6f, Mathf.Max(1.2f, _cfg.JumpMaxDistance));

            // Lowest first, so it takes one step up at a time rather than leaping the lot.
            for (var h = _cfg.StepHeight; h <= _cfg.ClimbMaxHeight; h += 0.25f)
            {
                var probeFrom = basePos + Vector3.up * (h + 0.15f);

                for (var forward = 0.5f; forward <= reach; forward += 0.4f)
                {
                    var over = probeFrom + dir * forward;

                    // Feel downward for a surface at about this height. A ledge top shows up
                    // as ground just below the probe rather than far below it.
                    RaycastHit top;
                    if (!Physics.Raycast(over + Vector3.up * 0.45f, Vector3.down, out top, 0.9f,
                            NavMeshProvider.BakeMask, QueryTriggerInteraction.Ignore))
                        continue;

                    // Has to be a genuine step up from where we are.
                    var gain = top.point.y - basePos.y;
                    if (gain < _cfg.StepHeight) continue;
                    if (gain > _cfg.ClimbMaxHeight) continue;

                    // Standable, not a wall face or a steep slope.
                    if (top.normal.y < 0.6f) continue;

                    // Enough headroom above it to actually occupy.
                    if (Physics.Raycast(top.point + Vector3.up * 0.1f, Vector3.up, 1.0f,
                            NavMeshProvider.BakeMask, QueryTriggerInteraction.Ignore))
                        continue;

                    // The arc must not pass through anything on the way.
                    var landing = top.point + Vector3.up * 0.05f;
                    if (Physics.Linecast(basePos + Vector3.up * 0.4f, landing + Vector3.up * 0.4f,
                            NavMeshProvider.BakeMask, QueryTriggerInteraction.Ignore))
                        continue;

                    // Prefer a navmesh-snapped point when the bake does cover it, so the bot
                    // lands ready to path again; otherwise take the raw ledge.
                    NavMeshHit navHit;
                    if (NavMesh.SamplePosition(landing, out navHit, 1.0f, NavMesh.AllAreas) &&
                        Mathf.Abs(navHit.position.y - landing.y) < 0.8f)
                        return navHit.position;

                    return landing;
                }
            }

            return null;
        }

        /// <summary>
        /// Walk off a drop when the target is below and ahead.
        ///
        /// A NavMeshAgent will not do this on its own - it stops dead at the mesh edge, which
        /// is why bots pace along balconies instead of dropping down. Probe just past the
        /// edge; if there is no mesh there but there is real ground below, and the drop is
        /// survivable, hand over to the ballistic integrator carrying the current run speed
        /// so it runs off rather than steps off.
        /// </summary>
        private bool TryStepOffLedge(Vector3 targetPos)
        {
            if (Agent == null || !Agent.isOnNavMesh) return false;

            if (transform.position.y - targetPos.y < _cfg.LedgeMinDrop) return false;

            var dir = targetPos - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.04f) return false;
            dir.Normalize();

            var ahead = transform.position + dir * (Agent.radius + 0.8f);

            // Mesh continues out there, so it is a slope and the agent can just walk it.
            NavMeshHit navHit;
            if (NavMesh.SamplePosition(ahead, out navHit, 0.6f, NavMesh.AllAreas)) return false;

            RaycastHit ground;
            if (!Physics.Raycast(ahead + Vector3.up * 0.5f, Vector3.down, out ground,
                    _cfg.LedgeMaxDrop + 2f, NavMeshProvider.BakeMask, QueryTriggerInteraction.Ignore))
                return false;

            var fallHeight = transform.position.y - ground.point.y;
            if (fallHeight < _cfg.LedgeMinDrop || fallHeight > _cfg.LedgeMaxDrop) return false;

            var carry = Agent.velocity;
            if (carry.sqrMagnitude < 1f) carry = dir * Mathf.Max(2f, _cfg.Speed * 0.5f);

            transform.position = ahead;
            if (_air.TryFall(carry, _cfg))
            {
                Plugin.Log.LogInfo("[Air] " + SkinName + " stepped off a " +
                                   fallHeight.ToString("0.0") + "m ledge toward the target.");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Somewhere reachable to land, in the direction of the target. Walks outward along
        /// the flat direction and takes the furthest point that is on the mesh and not
        /// through a wall, so the bot hops onto a ledge rather than into one.
        /// </summary>
        private Vector3? FindLandingToward(Vector3 targetPos)
        {
            var from = transform.position;
            var dir = targetPos - from;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return null;
            dir.Normalize();

            var eye = from + Vector3.up * 1.0f;
            Vector3? best = null;

            for (var d = _cfg.JumpMaxDistance; d >= 1.5f; d -= 1f)
            {
                var probe = from + dir * d;

                // Do not jump through geometry - only over it.
                if (Physics.Linecast(eye, probe + Vector3.up * 1.0f,
                        NavMeshProvider.BakeMask, QueryTriggerInteraction.Ignore))
                    continue;

                NavMeshHit hit;
                if (!NavMesh.SamplePosition(probe, out hit, 3f, NavMesh.AllAreas)) continue;

                // Must actually get us somewhere - not back where we started.
                if ((hit.position - from).sqrMagnitude < 1f) continue;

                best = hit.position;
                break;
            }

            return best;
        }

        /// <summary>Is the bot's current target actually looking at it right now?</summary>
        private bool IsWatched(WorldView world)
        {
            if (world == null || Brain == null || Brain.TargetActor < 0) return false;
            NextBots.Brain.PlayerSnapshot snap;
            if (!world.TryGetPlayer(Brain.TargetActor, out snap)) return false;
            return Brain.VisibleToPlayer(this, world, snap);
        }

        /// <summary>Drop the agent and fall back to whisker steering for the rest of this bot's life.</summary>
        private void SwitchToSteering()
        {
            if (Steering == null) Steering = gameObject.AddComponent<SteeringLocomotor>();
            Steering.Speed = _cfg != null ? _cfg.Speed : 6f;
            Steering.Destination = transform.position;

            if (Agent != null)
            {
                Agent.enabled = false;
                Agent = null;
            }

            _offMeshTicks = 0;
            if (Brain != null) Brain.Reset();
        }

        /// <summary>Host says this is where the bot is now. Remote clients only.</summary>
        public void ApplyNetworkState(Vector3 position, float yaw, byte state)
        {
            // Velocity from consecutive packets, for dead reckoning between them. Without it
            // a fast bot moves in visible 100ms steps, and at strike speed a single dropped
            // packet leaves a gap big enough to trip the teleport snap.
            var now = Time.time;
            if (_hasNetTarget)
            {
                var gap = now - _netAt;
                var vel = gap > 0.02f ? (position - _netPos) / gap : _netVel;
                // Teleports and respawns are not velocities.
                _netVel = vel.sqrMagnitude < 60f * 60f ? vel : Vector3.zero;
            }
            _netAt = now;

            _netPos = position;
            _netYaw = yaw;

            // First packet places it outright; a lerp from the origin would fly it across
            // the map on spawn.
            if (!_hasNetTarget)
            {
                transform.position = position;
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                _hasNetTarget = true;
            }

            if (Brain != null) Brain.SetReplicatedState(state);
        }

        private void UpdateRemote()
        {
            if (!_hasNetTarget) return;

            // Carry on along the last known velocity for a short while after each packet,
            // but not indefinitely - if packets stop, the bot should stop, not drift off.
            var ahead = Mathf.Min(Time.time - _netAt, 0.15f);
            var goal = _netPos + _netVel * ahead;

            // A big gap means a teleport or a dropped run of packets - snap rather than
            // sliding the bot through the scenery to catch up. Scaled with speed so a bot
            // legitimately covering ground fast is not mistaken for one that teleported.
            var snap = Mathf.Max(5f, _netVel.magnitude * 0.5f);
            if ((transform.position - goal).sqrMagnitude > snap * snap)
            {
                transform.position = goal;
            }
            else
            {
                var k = 1f - Mathf.Exp(-12f * Time.deltaTime);
                transform.position = Vector3.Lerp(transform.position, goal, k);
            }

            transform.rotation = Quaternion.Slerp(transform.rotation,
                Quaternion.Euler(0f, _netYaw, 0f), 1f - Mathf.Exp(-10f * Time.deltaTime));
        }

        private void Update()
        {
            // Remote bots are pure puppets: no agent, no brain, no physics of their own.
            if (!IsAuthority) { UpdateRemote(); return; }

            // Climbing runs per-frame so it is smooth between brain ticks, and owns the body
            // while attached.
            if (Climber != null && Climber.Attached)
            {
                Climber.Tick(_climbTarget, Time.deltaTime);
                return;
            }

            // The arc runs per-frame so it is smooth regardless of the 10Hz brain tick.
            if (_air != null && _air.Active)
            {
                _air.Tick(Time.deltaTime, _cfg);
                return;
            }

            // Facing is per-frame so it stays smooth between brain ticks. The steering
            // locomotor turns itself, so this only applies to NavMesh-driven bots.
            if (Agent == null) { _hasFaceAt = false; return; }

            if (_hasFaceAt)
            {
                GameAgent.UpdateFacingPos(transform, Agent, _faceAt, _cfg != null ? _cfg.TurnSpeed : 3600f);
                _hasFaceAt = false;
            }
            else
            {
                GameAgent.UpdateFacingForward(transform, Agent, _cfg != null ? _cfg.TurnSpeed : 3600f);
            }
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            var p = transform.position;
            if (_hasLastPos && dt > 1e-4f)
            {
                var instant = (p - _lastPos) / dt;
                // A warp - stuck reset, fall recovery, a snap to the host's position - is not a
                // velocity, and would fling anyone this bot catches next across the map.
                if (instant.sqrMagnitude > 60f * 60f) instant = MeasuredVelocity;
                MeasuredVelocity = Vector3.Lerp(MeasuredVelocity, instant, Mathf.Clamp01(dt * 10f));
            }
            _lastPos = p;
            _hasLastPos = true;
        }

        private void EnsureDebug()
        {
            if (_debug != null) return;
            var go = new GameObject("debug");
            go.transform.SetParent(transform, false);
            _debug = go.AddComponent<BotDebugVisual>();
        }

        public void Kill()
        {
            if (Audio != null) Audio.Stop();
            if (_air != null) _air.Cancel(_cfg);
            if (Climber != null) Climber.Detach(reseat: false);
            Alive = false;
            if (gameObject != null) Destroy(gameObject);
        }

        // ================================================================== IBotBody

        public Vector3 Position => transform.position;

        public Vector3 Center => transform.position + Vector3.up * (Visual != null ? Visual.Height * 0.5f : 1f);

        public Vector3 Velocity =>
            Agent != null ? Agent.velocity :
            Steering != null ? Steering.Velocity : Vector3.zero;

        public bool IsOnNavMesh => Agent != null && Agent.isOnNavMesh;

        public float Speed
        {
            get
            {
                if (Agent != null) return Agent.speed;
                return Steering != null ? Steering.Speed : 0f;
            }
            set
            {
                // Under Option A, go through GameAgent so the change is the one the game
                // knows about; it writes navAgent.speed itself.
                if (HostAgent != null) HostAgent.SetSpeed(value);
                else if (Agent != null) Agent.speed = value;
                else if (Steering != null) Steering.Speed = value;
            }
        }

        public void SetDestination(Vector3 destination)
        {
            if (HostAgent != null)
            {
                // Authority-gated and 0.5 m-deduped inside GameAgent, and replicated for us.
                HostAgent.RequestDestination(destination);
                return;
            }

            // An agent that is off the mesh cannot accept a destination at all. Rather than
            // drop the request on the floor, remember it for the steering fallback.
            if (Agent != null && !Agent.isOnNavMesh && Steering != null)
            {
                Steering.Destination = destination;
                return;
            }

            if (Agent != null && Agent.isOnNavMesh)
            {
                // Never interrupt a path that is still being computed.
                //
                // This is the subtle one. While Unity is working out a route, hasPath is
                // FALSE and pathPending is true. A guard written as "hasPath && !pathPending"
                // therefore does not apply during exactly the window it needs to, so the
                // brain's 100ms re-aim cancels the computation over and over and the agent
                // never gets a route at all - it just stands there reporting CHASE until the
                // stuck timer fires. It only ever started working after a warp reset the
                // timing enough for one path to slip through.
                if (Agent.pathPending) return;

                // Once it has a route, ignore small drifts in the aim point. The overshoot
                // target wanders continuously as the player moves, and repathing every tick
                // is both wasteful and visibly stuttery.
                if (Agent.hasPath && Vector3.Distance(destination, _lastIssuedDest) < 0.75f)
                    return;

                _lastIssuedDest = destination;

                // Snap to the mesh, but NEVER leave the agent without a destination.
                // See TryReachable below for what happens when the target is unreachable.
                //
                // The pursuit point is aimed several metres *past* the target so the agent
                // does not brake on approach - which regularly puts it inside a wall or
                // outside the baked volume. The first version gave up when that point could
                // not be sampled, so the agent stood still, the stuck timer ran, and the
                // hard reset teleported the bot onto the player. It read as "they don't
                // move, they just teleport and kill you", which is exactly what it was.
                NavMeshHit hit;
                var snapped = destination;
                if (NavMesh.SamplePosition(destination, out hit, 3f, NavMesh.AllAreas) ||
                    NavMesh.SamplePosition(destination, out hit, 12f, NavMesh.AllAreas))
                    snapped = hit.position;

                // Ask whether a route actually exists before committing to it.
                //
                // A runtime bake routinely produces disconnected islands - each floor of a
                // building can come out as its own patch with nothing joining them. Handing
                // an agent a destination on another island yields PathInvalid, which reports
                // as hasPath=false / pathPending=false, and the bot just stands there looking
                // broken. That is not a bug in the agent; there genuinely is no route.
                if (Agent.CalculatePath(snapped, _pathScratch))
                {
                    // A complete path is ideal, but a PARTIAL path is not a failure - it is
                    // a real A* route to the closest reachable point, which means it already
                    // goes around walls and through doorways. Rejecting it and beelining at
                    // the target instead is what made bots grind against the outside of a
                    // room rather than walking to its door.
                    var st = _pathScratch.status;
                    if (st == NavMeshPathStatus.PathComplete ||
                        (st == NavMeshPathStatus.PathPartial && _pathScratch.corners.Length >= 2))
                    {
                        _noPathTicks = 0;
                        Agent.SetPath(_pathScratch);
                        return;
                    }
                }

                // No usable route. Pathfinding is the nicer option, but a bot that stands
                // still is not a nextbot at all - so if this keeps failing, stop pretending
                // the NavMesh is going to help and hand the bot to steering, which at least
                // walks toward the player. Roughly 2s at 10Hz before giving up.
                if (++_noPathTicks >= 20)
                {
                    Plugin.Log.LogWarning("[Bots] '" + SkinName + "' has had no usable path for " +
                                          _noPathTicks + " ticks (target unreachable on this " +
                                          "NavMesh); switching to steering so it keeps moving.");
                    SwitchToSteering();
                    Steering.Destination = destination;
                    return;
                }

                // Genuinely no route at all (PathInvalid) - not even a partial one. Only now
                // is it worth heading for the edge of our own island in the target's
                // direction, because there is no graph route to follow.
                NavMeshHit edge;
                if (NavMesh.Raycast(transform.position, snapped, out edge, NavMesh.AllAreas) &&
                    edge.hit && (edge.position - transform.position).sqrMagnitude > 0.25f)
                {
                    Agent.SetDestination(edge.position);
                    return;
                }

                // Even the edge is no use - take the partial path and make what progress we can.
                Agent.SetDestination(snapped);
                return;
            }

            if (Steering != null) Steering.Destination = destination;
        }

        public void SetStopped(bool stopped)
        {
            if (HostAgent != null) { HostAgent.SetStopped(stopped); return; }
            if (Agent != null && Agent.isOnNavMesh) { Agent.isStopped = stopped; return; }
            if (Steering != null) Steering.Stopped = stopped;
        }

        public void Warp(Vector3 position)
        {
            if (Agent != null && Agent.enabled) Agent.Warp(position);
            else if (Steering != null) Steering.Warp(position);
            else transform.position = position;
        }

        public void FaceTowards(Vector3 worldPoint)
        {
            _faceAt = worldPoint;
            _hasFaceAt = true;
        }

        public bool TryGetPathCorners(List<Vector3> into)
        {
            if (Agent == null || !Agent.isOnNavMesh || Agent.path == null) return false;
            var corners = Agent.path.corners;
            if (corners == null || corners.Length < 2) return false;
            for (int i = 0; i < corners.Length; i++) into.Add(corners[i]);
            return true;
        }
    }
}
