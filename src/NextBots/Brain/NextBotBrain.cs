using System;
using System.Collections.Generic;
using NextBots.Config;
using UnityEngine;

namespace NextBots.Brain
{
    /// <summary>
    /// The nextbot decision ladder. Pure logic over <see cref="IBotBody"/> and
    /// <see cref="IWorldView"/>, so it is identical whether the body is a real GT
    /// <c>GameAgent</c> (Option A) or a locally simulated one (Option B), and so it can be
    /// exercised without a running game.
    ///
    /// Runs on the authority client only. Everyone else watches a replicated transform.
    ///
    /// Priority order, highest first:
    ///   1. Strike   - committed, fixed duration, nothing below may interrupt it
    ///   2. Reset    - stuck escalation 2
    ///   3. Charge   - stuck escalation 1
    ///   4. Ambush   - hold at cover, or creep in behind their back
    ///   5. Chase    - default
    /// </summary>
    public class NextBotBrain
    {
        private readonly NextBotSettings _cfg;

        public BotState State { get; private set; } = BotState.Idle;
        public int TargetActor { get; private set; } = -1;
        public Vector3 Destination { get; private set; }

        /// <summary>Last reason the state changed, for the debug overlay.</summary>
        public string LastTransition { get; private set; } = "";

        /// <summary>
        /// How many times this bot has been teleported by the stuck escalation. Every one of
        /// these is a visible snap, so it is the direct measure of whether movement is
        /// actually working - zero means it is pathing the whole way.
        /// </summary>
        public int HardResets { get; internal set; }

        private float _stateTime;
        private float _ambushReadyAt;

        // Where the target was last actually visible. Source nextbots keep this
        // (GetLastKnownArea) and it is what stops a bot giving up the instant you break
        // line of sight - it commits to where you went instead of to where you are.
        private Vector3 _lastSeenAt;
        private bool _hasLastSeen;
        private float _lastSeenTime;
        private float _lastCatchAt = float.NegativeInfinity;

        // Stuck tracking measures *actual* progress, not whether the agent thinks it is
        // moving. An agent wedged on a corner reports a healthy velocity while going nowhere.
        private bool _arrived;
        private Vector3 _lastProgressPos;
        private bool _hasProgressPos;
        private float _stuckTime;

        public NextBotBrain(NextBotSettings cfg)
        {
            _cfg = cfg ?? NextBotSettings.Active;
        }

        public float StuckTime => _stuckTime;
        public float TimeInState => _stateTime;

        // ======================================================================
        //  Tick
        // ======================================================================

        public void Tick(IBotBody body, IWorldView world, float dt)
        {
            if (body == null || world == null) return;

            _stateTime += dt;
            UpdateStuckTracking(body, dt);

            // ---- 1. Strike: committed. Re-evaluating it every frame is what made the
            //         prototype's lunge read as a flinch. Fixed duration, then cooldown.
            if (State == BotState.Strike)
            {
                var st = FindTarget(world, TargetActor);
                if (_stateTime < _cfg.StrikeDuration && st.IsValid)
                {
                    DriveStrike(body, st);
                    return;
                }

                // Strike over (or the target vanished). Cool down before another ambush,
                // or a bot that loses you re-hides instantly and the pacing collapses.
                _ambushReadyAt = world.Now + _cfg.AmbushCooldown;
                body.Speed = _cfg.Speed;
                Transition(BotState.Chase, "strike ended");
            }

            var target = SelectTarget(body, world);
            if (!target.IsValid)
            {
                if (State != BotState.Idle)
                {
                    body.Speed = _cfg.Speed;
                    body.SetStopped(true);
                    Transition(BotState.Idle, "no target");
                }
                TargetActor = -1;
                return;
            }

            TargetActor = target.ActorNumber;
            body.SetStopped(false);

            // Remember the last place we could actually see them.
            if (!world.LineBlocked(body.Center, target.Center))
            {
                _lastSeenAt = target.Center;
                _hasLastSeen = true;
                _lastSeenTime = world.Now;
            }

            // "Close enough to be touching" - used to suppress false stuck detection below.
            _arrived = Vector3.Distance(body.Center, target.Center) <= _cfg.CatchRange * 1.5f;

            // ---- 2. Hard reset: still stuck after charging. Teleport rather than leave it
            //         standing in a corner, which ruins the whole effect.
            if (_stuckTime >= _cfg.StuckChargeTime + _cfg.StuckResetTime)
            {
                // Never snap in front of the person watching. Keep charging while seen; the
                // relocation happens the moment they look away, which reads as the bot having
                // closed the gap unseen rather than as a glitch.
                if (_cfg.TeleportWhenSeen || !VisibleToPlayer(body, world, target))
                {
                    DoHardReset(body, world, target);
                    return;
                }

                if (State != BotState.Charge) Transition(BotState.Charge, "stuck, but watched");
                body.Speed = _cfg.Speed;
                SetDestination(body, target.Center);
                return;
            }

            // ---- 3. Charge: stop being clever about geometry and just run at them.
            //         (The prototype had a sidestep step here. It was removed - it looked
            //          like shuffling and rarely helped.)
            if (_stuckTime >= _cfg.StuckChargeTime)
            {
                if (State != BotState.Charge) Transition(BotState.Charge, "stuck " + _stuckTime.ToString("0.0") + "s");
                body.Speed = _cfg.Speed;
                SetDestination(body, target.Center);
                return;
            }

            // ---- 4. Ambush.
            if (State == BotState.AmbushHold || State == BotState.AmbushCreep)
            {
                UpdateAmbush(body, world, target);
                return;
            }

            if (CanEnterAmbush(body, world, target))
            {
                EnterAmbush(body, world, target);
                return;
            }

            // ---- 5. Chase.
            if (State != BotState.Chase) Transition(BotState.Chase, "chasing");
            body.Speed = _cfg.Speed;
            DriveChase(body, target, world);
        }

        // ======================================================================
        //  Target selection
        // ======================================================================

        /// <summary>
        /// Closest player, re-evaluated constantly, weighted by reachability rather than
        /// straight-line distance, with hysteresis so it does not flip-flop.
        /// </summary>
        private PlayerSnapshot SelectTarget(IBotBody body, IWorldView world)
        {
            var players = world.Players;
            if (players == null || players.Count == 0) return default;

            var eye = body.Center;
            var best = default(PlayerSnapshot);
            var bestScore = float.MaxValue;
            var currentScore = float.MaxValue;
            var current = default(PlayerSnapshot);

            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (!p.IsValid) continue;

                var score = EffectiveDistance(world, eye, p);

                if (p.ActorNumber == TargetActor)
                {
                    current = p;
                    currentScore = score;
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }

            if (!best.IsValid) return default;

            // Switch hysteresis: require the newcomer to be meaningfully closer. Two players
            // at similar range would otherwise make the bot flip several times a second.
            if (current.IsValid && best.ActorNumber != current.ActorNumber &&
                bestScore > currentScore - _cfg.SwitchHysteresis)
            {
                return current;
            }

            return best;
        }

        /// <summary>
        /// A player 5 m away through a wall is a 40 m walk; someone 8 m down an open corridor
        /// is the better target. We cannot afford a real path query per player per tick, so
        /// inflate the distance of anyone we cannot see.
        /// </summary>
        private float EffectiveDistance(IWorldView world, Vector3 eye, PlayerSnapshot p)
        {
            var d = Vector3.Distance(eye, p.Center);
            if (!_cfg.SightlineWeighting) return d;
            return world.LineBlocked(eye, p.Center) ? d * _cfg.BlockedPenalty : d;
        }

        private static PlayerSnapshot FindTarget(IWorldView world, int actor)
        {
            if (actor < 0) return default;
            var players = world.Players;
            if (players == null) return default;
            for (int i = 0; i < players.Count; i++)
                if (players[i].IsValid && players[i].ActorNumber == actor)
                    return players[i];
            return default;
        }

        // ======================================================================
        //  Pursuit
        // ======================================================================

        private void DriveChase(IBotBody body, PlayerSnapshot target, IWorldView world)
        {
            // With a clear line, chase them directly.
            if (world == null || !world.LineBlocked(body.Center, target.Center))
            {
                SetDestination(body, PursuitPoint(body, target, world));
                return;
            }

            // Out of sight. Head for where they were last seen rather than pressing toward
            // their current position through a wall - that is what produces a bot grinding
            // against the outside of a room instead of coming round to the door. Once we
            // reach that spot, or it goes stale, resume chasing their live position.
            if (_hasLastSeen &&
                world.Now - _lastSeenTime < 6f &&
                Vector3.Distance(body.Center, _lastSeenAt) > 1.5f)
            {
                SetDestination(body, _lastSeenAt);
                return;
            }

            SetDestination(body, PursuitPoint(body, target, world));
        }

        private void DriveStrike(IBotBody body, PlayerSnapshot target)
        {
            body.Speed = _cfg.Speed * _cfg.StrikeSpeedMultiplier;

            // Aim far past the target for the whole strike. Raising speed and aiming through
            // are complementary: the speed makes it fast, aiming through stops the agent
            // braking on approach.
            var lead = target.Center + target.Velocity * _cfg.LeadTime;
            var dir = lead - body.Center;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.0001f) dir.Normalize(); else dir = Vector3.forward;

            SetDestination(body, lead + dir * Mathf.Max(_cfg.Overshoot, 4f));
        }

        /// <summary>
        /// Lead the target by their velocity, then aim past them so the NavMesh agent does
        /// not brake on approach - but fade that overshoot out at close range.
        ///
        /// The fade is load-bearing. Applied point-blank the overshoot puts the destination
        /// *behind* the target, and "behind" flips as the bot crosses over, so it orbits
        /// instead of arriving. That was a very visible bug in the prototype.
        /// </summary>
        private Vector3 PursuitPoint(IBotBody body, PlayerSnapshot target)
        {
            return PursuitPoint(body, target, null);
        }

        private Vector3 PursuitPoint(IBotBody body, PlayerSnapshot target, IWorldView world)
        {
            // Aim from where the target was a reaction-time ago, not where they are now.
            var seen = world != null
                ? world.DelayedCenter(target.ActorNumber, _cfg.ReactionTime, target.Center)
                : target.Center;

            var lead = seen + target.Velocity * _cfg.LeadTime;

            var toLead = lead - body.Center;
            toLead.y = 0f;
            var dist = toLead.magnitude;
            if (dist < 0.0001f) return lead;

            var near = _cfg.OvershootFadeNear;
            var far = Mathf.Max(near + 0.01f, _cfg.OvershootFadeFar);
            var fade = Mathf.Clamp01((dist - near) / (far - near));

            return lead + (toLead / dist) * (_cfg.Overshoot * fade);
        }

        // ======================================================================
        //  Ambush
        // ======================================================================

        /// <summary>
        /// Entering requires all of: within ambush range, not visible to the target, and off
        /// cooldown. Range-gating matters - a bot ambushing from across the map looks idle.
        /// </summary>
        private bool CanEnterAmbush(IBotBody body, IWorldView world, PlayerSnapshot target)
        {
            if (world.Now < _ambushReadyAt) return false;
            if (Vector3.Distance(body.Center, target.Center) > _cfg.AmbushRange) return false;

            // Never settle in to wait while the target is well above us. Holding position
            // under someone standing on a branch reads as the bot having given up, when what
            // it should be doing is finding a way up to them.
            if (target.Center.y - body.Center.y > 1.5f) return false;

            return !VisibleToPlayer(body, world, target);
        }

        private void EnterAmbush(IBotBody body, IWorldView world, PlayerSnapshot target)
        {
            var blocked = world.LineBlocked(body.Center, target.HeadPosition);
            Transition(blocked ? BotState.AmbushHold : BotState.AmbushCreep,
                       blocked ? "hidden by cover" : "hidden by facing");
        }

        private void UpdateAmbush(IBotBody body, IWorldView world, PlayerSnapshot target)
        {
            var dist = Vector3.Distance(body.Center, target.Center);

            // Give up if they wander well outside range. Camping an empty corner is the
            // difference between an ambush and a bot that looks broken.
            if (dist > _cfg.AmbushRange * _cfg.AmbushGiveUpScale)
            {
                body.Speed = _cfg.Speed;
                Transition(BotState.Chase, "target left ambush range");
                DriveChase(body, target, world);
                return;
            }

            // Leaving an ambush ALWAYS means attacking. Both triggers end in a committed
            // charge. Dropping back to a normal chase on timeout reads as the bot losing
            // interest and wandering off, and it killed the tension every time.
            if (VisibleToPlayer(body, world, target))
            {
                EnterStrike(body, "spotted");
                DriveStrike(body, target);
                return;
            }

            if (_stateTime >= _cfg.AmbushHold)
            {
                EnterStrike(body, "hold expired");
                DriveStrike(body, target);
                return;
            }

            // Two ways to be hidden, and they want opposite behaviour. Re-evaluated every
            // tick, because they move: cover can become a clear line and vice versa.
            var blockedByGeometry = world.LineBlocked(body.Center, target.HeadPosition);

            if (blockedByGeometry)
            {
                // Hidden by geometry: hold position at the cover. Do not route around noisily.
                if (State != BotState.AmbushHold) Transition(BotState.AmbushHold, "took cover", keepTime: true);
                body.SetStopped(true);
                body.FaceTowards(target.Center);
            }
            else
            {
                // Hidden only by facing: creep in while their back is turned. This is the one
                // that gets screams - you turn around and it is right there.
                if (State != BotState.AmbushCreep) Transition(BotState.AmbushCreep, "closing unseen", keepTime: true);
                body.SetStopped(false);
                body.Speed = _cfg.Speed * _cfg.CreepSpeedMultiplier;
                SetDestination(body, target.Center);
            }
        }

        private void EnterStrike(IBotBody body, string why)
        {
            body.SetStopped(false);
            body.Speed = _cfg.Speed * _cfg.StrikeSpeedMultiplier;
            Transition(BotState.Strike, why);
        }

        /// <summary>
        /// Can the *player* see the bot? A raycast is symmetric; a facing cone is not.
        /// Clear line AND inside their view cone. This one test is the whole ambush feature.
        /// </summary>
        public bool VisibleToPlayer(IBotBody body, IWorldView world, PlayerSnapshot target)
        {
            var toBot = body.Center - target.HeadPosition;
            var dist = toBot.magnitude;
            if (dist < 0.0001f) return true;
            toBot /= dist;

            if (Vector3.Dot(target.HeadForward, toBot) < _cfg.HalfFovCos) return false;
            return !world.LineBlocked(target.HeadPosition, body.Center);
        }

        // ======================================================================
        //  Stuck recovery
        // ======================================================================

        /// <summary>
        /// Measure actual progress, not whether the agent thinks it is moving. A bot wedged
        /// on a corner reports a healthy desiredVelocity while going nowhere.
        /// </summary>
        private void UpdateStuckTracking(IBotBody body, float dt)
        {
            var pos = body.Position;

            if (!_hasProgressPos)
            {
                _lastProgressPos = pos;
                _hasProgressPos = true;
                return;
            }

            // Holding position in cover is deliberate, not stuck.
            if (State == BotState.AmbushHold || State == BotState.Idle)
            {
                _lastProgressPos = pos;
                _stuckTime = 0f;
                return;
            }

            // Neither is standing on top of the target. A bot that has arrived has nowhere
            // left to go, so counting that as "no progress" fires the escalation - and
            // teleports a bot that is already exactly where it wanted to be.
            if (_arrived)
            {
                _lastProgressPos = pos;
                _stuckTime = 0f;
                return;
            }

            var moved = Vector3.Distance(pos, _lastProgressPos);
            _lastProgressPos = pos;

            if (dt <= 0f) return;
            var rate = moved / dt;

            if (rate < _cfg.StuckProgressRate) _stuckTime += dt;
            else _stuckTime = 0f;
        }

        private void DoHardReset(IBotBody body, IWorldView world, PlayerSnapshot target)
        {
            HardResets++;
            Transition(BotState.Reset, "hard reset");

            // Drop in near the target rather than on top of them, so it reads as arriving
            // rather than as a glitch.
            var offset = UnityEngine.Random.insideUnitSphere;
            offset.y = 0f;
            if (offset.sqrMagnitude < 0.01f) offset = Vector3.forward;
            offset = offset.normalized * Mathf.Max(2f, _cfg.CatchRange * 2f);

            var want = target.Position + offset;
            var landed = world.SampleNavigable(want, 6f) ?? world.SampleNavigable(target.Position, 6f);

            if (landed.HasValue) body.Warp(landed.Value);

            _stuckTime = 0f;
            _hasProgressPos = false;
            body.Speed = _cfg.Speed;
            Transition(BotState.Chase, "reset -> chase");
        }

        // ======================================================================
        //  Contact
        // ======================================================================

        /// <summary>
        /// True on the frame the bot catches its target. Kept separate from the effect so
        /// ragdoll / sound / jumpscare can be swapped in for "send them back to spawn".
        /// </summary>
        public bool TryCatch(IBotBody body, IWorldView world, out int caughtActor)
        {
            caughtActor = -1;
            if (world.Now - _lastCatchAt < _cfg.CatchCooldown) return false;

            var target = FindTarget(world, TargetActor);
            if (!target.IsValid) return false;

            // Contact is judged horizontally, with a generous vertical tolerance, rather than
            // as a single 3D distance. A bot standing on the floor with its centre ~1m up,
            // against a player whose centre is at chest height on a slightly different floor
            // level, can be "1.3m apart" while visually standing inside them - and a straight
            // 3D test then never fires.
            var delta = target.Center - body.Center;
            var vertical = Mathf.Abs(delta.y);
            delta.y = 0f;

            if (delta.magnitude > _cfg.CatchRange) return false;
            if (vertical > _cfg.CatchRange * 2f + 1f) return false;

            _lastCatchAt = world.Now;
            caughtActor = target.ActorNumber;

            // A catch resolves the ambush cycle too, otherwise the bot re-hides instantly.
            _ambushReadyAt = world.Now + _cfg.AmbushCooldown;
            return true;
        }

        // ======================================================================
        //  Plumbing
        // ======================================================================

        private void SetDestination(IBotBody body, Vector3 dest)
        {
            Destination = dest;
            body.SetDestination(dest);
        }

        private void Transition(BotState next, string why, bool keepTime = false)
        {
            if (State == next) return;
            State = next;
            LastTransition = why;
            if (!keepTime) _stateTime = 0f;
        }

        /// <summary>Called when the bot is (re)spawned, so stale state does not leak in.</summary>
        /// <summary>
        /// Adopt the state the host reports, so remote clients colour the debug overlay and
        /// pick sounds correctly without simulating anything themselves.
        /// </summary>
        public void SetReplicatedState(byte state)
        {
            if (Enum.IsDefined(typeof(BotState), (int)state)) State = (BotState)state;
        }

        public void Reset()
        {
            State = BotState.Idle;
            TargetActor = -1;
            _stateTime = 0f;
            _stuckTime = 0f;
            _hasProgressPos = false;
            _ambushReadyAt = 0f;
            _hasLastSeen = false;
            LastTransition = "spawned";
            // HardResets deliberately not cleared - it counts the bot's whole life.
        }
    }
}
