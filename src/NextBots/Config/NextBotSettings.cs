using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace NextBots.Config
{
    /// <summary>
    /// Every number the brain reads, live-tunable in VR and syncable across the lobby so
    /// everyone's bots behave identically on camera.
    ///
    /// Defaults are the starting values from the design brief, which came out of the
    /// prototype. They are starting points, not gospel - tune them in the simulator.
    /// </summary>
    public class NextBotSettings
    {
        public static NextBotSettings Active = new NextBotSettings();

        // ---- population -------------------------------------------------------
        public int MaxBots = 4;

        // ---- movement ---------------------------------------------------------
        public float Speed = 6.0f;

        /// <summary>
        /// How fast it can change velocity. This is the single biggest lever on whether the
        /// bot can be juked.
        ///
        /// At 24 m/s^2 a NavMeshAgent changes direction almost instantly, so sidestepping it
        /// does nothing - it simply re-aims and is on you again, which reads as the bot being
        /// unfairly accurate. Real nextbots carry momentum: they commit to a direction, and
        /// cutting sharply across them makes them swing wide. Lower value = more overshoot,
        /// more juking room.
        /// </summary>
        /// Garry's Mod CLuaLocomotion:SetAcceleration defaults to 400 source units/s^2.
        /// One source unit is 0.75in = 0.01905m, so 52.49 units per metre -> 7.6 m/s^2.
        public float Acceleration = 7.6f;

        /// <summary>
        /// How fast it sheds speed. GMod's SetDeceleration also defaults to 400 u/s^2.
        /// Kept separate from acceleration because a bot that brakes as slowly as it
        /// accelerates overshoots corners, which is a large part of being jukeable.
        /// </summary>
        public float Deceleration = 7.6f;

        /// <summary>
        /// Degrees per second the body turns. Mostly cosmetic for a billboard, but the
        /// steering fallback uses it to actually steer.
        /// </summary>
        /// GMod SetMaxYawRate defaults to 250 deg/s. Mine was nearly double that, which is
        /// part of why the bot could match a sidestep instantly.
        public float TurnSpeed = 250f;

        /// <summary>
        /// Seconds of lag between the player moving and the bot reacting to it.
        ///
        /// The bot aims at where you *were* this long ago, not where you are now. Without
        /// this the pursuit is frame-perfect and no amount of dodging helps, because it
        /// re-solves for your exact position ten times a second. A quarter of a second is
        /// roughly human reaction time and is what makes a feint work.
        /// </summary>
        public float ReactionTime = 0.25f;

        // ---- pursuit ----------------------------------------------------------
        /// <summary>Seconds of target velocity to lead by, so the bot cuts them off.</summary>
        public float LeadTime = 0.40f;

        /// <summary>
        /// Metres to aim *past* the target.
        ///
        /// This exists because NavMesh agents brake as they approach their destination, which
        /// reads as the bot giving up a metre short. But we also set
        /// <c>NavMeshAgent.autoBraking = false</c>, which solves the same problem directly -
        /// the big overshoot was inherited from the Luau prototype, where no such setting
        /// existed. Aiming 6m past the player routes the path *around* them and produces the
        /// wide, silly-looking approaches; kept small, the bot runs straight at you.
        /// </summary>
        public float Overshoot = 2.0f;

        /// <summary>
        /// Below this range the overshoot is fully faded out. Applied point-blank it puts
        /// the destination *behind* the target, and "behind" flips as the bot crosses over,
        /// so it orbits instead of arriving. This caused a very visible bug in the prototype.
        /// </summary>
        public float OvershootFadeNear = 2.5f;

        /// <summary>Above this range the overshoot is applied in full.</summary>
        public float OvershootFadeFar = 8.0f;

        // ---- target selection -------------------------------------------------
        /// <summary>
        /// Weight candidates by reachability rather than straight-line distance. A player
        /// 5 m away through a wall is a 40 m walk; someone 8 m down an open corridor is the
        /// better target. Without this the bot grinds against walls chasing the wrong person.
        /// </summary>
        public bool SightlineWeighting = true;

        /// <summary>Multiplier applied to the distance of a target we cannot see.</summary>
        public float BlockedPenalty = 2.5f;

        /// <summary>
        /// A new target must beat the current one by this many effective metres before the
        /// bot commits. Without it two players at similar range make it flip-flop several
        /// times a second and look broken.
        /// </summary>
        public float SwitchHysteresis = 2.0f;

        // ---- ambush -----------------------------------------------------------
        public float AmbushRange = 9.0f;
        public float AmbushHold = 4.0f;
        public float AmbushCooldown = 3.0f;
        public float StrikeDuration = 1.3f;

        /// <summary>Speed multiplier during the committed strike. GameAgent.SetSpeed exists.</summary>
        public float StrikeSpeedMultiplier = 1.6f;

        /// <summary>
        /// Approximates a headset's horizontal field of view. The whole ambush feature is
        /// this one test: rotate forward by head rotation, dot against the direction to the
        /// bot, compare to cos(fov/2).
        /// </summary>
        public float FieldOfView = 110f;

        /// <summary>
        /// Speed multiplier while creeping in on a target whose back is turned. This is the
        /// one that gets screams - you turn around and it is right there.
        /// </summary>
        public float CreepSpeedMultiplier = 0.55f;

        /// <summary>
        /// Give up the ambush if the target wanders beyond AmbushRange * this. Camping an
        /// empty corner is the difference between an ambush and a bot that looks broken.
        /// </summary>
        public float AmbushGiveUpScale = 1.8f;

        // ---- terrain ----------------------------------------------------------
        /// <summary>
        /// Steepest slope, in degrees, the bake will treat as walkable. Changing this forces
        /// a re-bake, because it changes the mesh itself rather than how the bot uses it.
        /// Higher lets bots up ramps and rubble; too high and they walk up walls.
        /// </summary>
        public float MaxSlope = 60f;

        // ---- jumping ----------------------------------------------------------
        /// <summary>Whether the bot may leave the ground to cross gaps and drops.</summary>
        public bool JumpEnabled = true;

        /// <summary>
        /// Peak of the arc above the higher endpoint. GMod SetJumpHeight defaults to 58
        /// source units = 1.10m, which is about a player's own jump.
        /// </summary>
        public float JumpHeight = 1.1f;

        /// <summary>
        /// Whether the bot can haul itself up onto ledges above it - Source's
        /// SetClimbAllowed / ClimbUpToLedge. This is a separate capability from jumping
        /// gaps, and it is what lets a bot reach someone standing above it rather than
        /// milling about underneath.
        /// </summary>
        public bool ClimbEnabled = true;

        /// <summary>
        /// Tallest ledge it can climb. Higher than JumpHeight on purpose: Source nextbots
        /// climb ledges well above what they can jump, by hauling up rather than leaping.
        /// </summary>
        public float ClimbMaxHeight = 3.5f;

        /// <summary>
        /// Surface climbing: hauling up trunks, rock faces and ledges that no NavMesh bake
        /// marks as walkable. This is what lets a bot follow a player somewhere a walking
        /// agent simply cannot go, which in this game is most of the interesting places.
        /// </summary>
        public bool SurfaceClimbEnabled = false;

        /// <summary>Speed multiplier while climbing. Slower than running, as hauling should be.</summary>
        public float ClimbSpeedScale = 0.55f;

        /// <summary>
        /// Longest a bot may stay stuck to a surface before it has to let go.
        ///
        /// Grip is what makes climbing look wrong: a bot welded to a wall crawling steadily
        /// upward reads as an insect, not as something chasing you. Forcing it off after a
        /// short hold turns climbing into a brief transition between running and jumping,
        /// which is what it should be.
        /// </summary>
        public float MaxClimbTime = 1.6f;

        /// <summary>
        /// Push off a wall mid-climb toward the target, instead of crawling all the way up.
        /// This is the wall-jump: the surface is a launch point rather than a ladder.
        /// </summary>
        public bool WallHopEnabled = true;

        /// <summary>
        /// How far above the bot the target has to be before it gives up walking and starts
        /// climbing. Too low and it climbs over every kerb.
        /// </summary>
        public float ClimbTriggerHeight = 2.5f;

        /// <summary>Seconds spent airborne. Scaled a little by distance.</summary>
        public float JumpDuration = 0.65f;

        /// <summary>Furthest gap it will attempt to clear.</summary>
        public float JumpMaxDistance = 7f;

        /// <summary>Seconds between jumps, so it cannot hop continuously.</summary>
        public float JumpCooldown = 1.5f;

        /// <summary>
        /// Jump between surfaces during ordinary pursuit, not only when stuck.
        ///
        /// This is what lets a bot cross a gap, hop onto a rock and carry on, or cut a corner
        /// the walking route goes the long way around - the parkour a player does constantly
        /// in this game. Without it the bot is limited to whatever the walkable surface
        /// happens to connect.
        /// </summary>
        public bool ParkourEnabled = true;

        /// <summary>
        /// How much further a running jump carries than a standing one. Reach is scaled
        /// between half and this multiple of JUMP REACH depending on current speed, so a bot
        /// at a sprint clears gaps a stationary one would not attempt.
        /// </summary>
        public float RunningJumpBonus = 1.6f;

        /// <summary>
        /// How much shorter the jump has to make the remaining trip before it is worth doing.
        /// 2.5 means the walking route must be two and a half times as long; short of that it
        /// walks. A jump is for a gap or a height, not for shaving a corner.
        /// </summary>
        public float ParkourShortcutRatio = 2.5f;

        /// <summary>Whether the bot drops off ledges instead of stopping at the edge.</summary>
        public bool FallEnabled = true;

        /// <summary>
        /// Gravity multiplier. GMod SetGravity defaults to 1000 source units/s^2 = 19.05
        /// m/s^2, which is 1.94x Earth. Games use heavier-than-real gravity because real
        /// gravity feels floaty on a character this size.
        /// </summary>
        public float GravityScale = 1.94f;

        /// <summary>
        /// A drop of at least this much, with ground beneath, counts as a ledge worth
        /// stepping off rather than a bump to walk down.
        /// </summary>
        public float LedgeMinDrop = 1.0f;

        /// <summary>
        /// The drop height the bot is unwilling to fall from - Source's SetDeathDropHeight,
        /// which defaults to 200 source units = 3.81m. Mine was 14m, so bots would fling
        /// themselves off anything.
        /// </summary>
        public float LedgeMaxDrop = 3.8f;

        /// <summary>
        /// Step height: the lip it walks straight over rather than jumping. GMod defaults to
        /// 18 source units = 0.34m. This feeds the NavMesh bake, so changing it re-bakes.
        /// Raise it if bots cannot get up a particular set of stairs.
        /// </summary>
        public float StepHeight = 0.4f;

        // ---- stuck recovery ---------------------------------------------------
        /// <summary>Metres of real progress per second below which we count as stuck.</summary>
        public float StuckProgressRate = 0.35f;

        /// <summary>Seconds stuck before we stop being clever and just charge.</summary>
        public float StuckChargeTime = 2.0f;

        /// <summary>Further seconds stuck before we teleport rather than leave it in a corner.</summary>
        public float StuckResetTime = 4.0f;

        /// <summary>
        /// Allow the stuck-recovery teleport to happen while the target can actually see the
        /// bot. Off by default: a nextbot that snaps across the room in front of you stops
        /// being a nextbot. With this off it keeps charging while watched and only relocates
        /// once you look away - which reads as it having closed the gap while unseen.
        /// </summary>
        public bool TeleportWhenSeen = false;

        // ---- contact ----------------------------------------------------------
        public float CatchRange = 1.2f;
        public float CatchCooldown = 3.0f;

        /// <summary>
        /// Whether being caught actually does anything to you.
        ///
        /// Off by default. The respawn teleport can drop you through the world if the
        /// recorded spawn point is not solid ground, and repeated catches chain it - which
        /// crashed the game. With this off a catch is still reported and still resolves the
        /// bot's ambush cycle; it just does not move you.
        /// </summary>
        public bool DeathEnabled = false;

        /// <summary>Off: every bot stands where it is and cannot catch anyone, until switched back on.</summary>
        public bool BotsMove = true;

        // ---- sound ------------------------------------------------------------
        /// <summary>Play the per-skin chase loop.</summary>
        public bool SoundEnabled = true;

        public float SoundVolume = 0.85f;

        /// <summary>Beyond this the loop is inaudible. Also sets the falloff curve.</summary>
        public float SoundMaxDistance = 45f;

        /// <summary>
        /// How much quieter a bot is when something solid is between you. 1 = silent through
        /// walls, 0 = no occlusion at all. Combined with a low-pass so it sounds muffled
        /// rather than merely distant.
        /// </summary>
        public float OcclusionMuffle = 0.75f;

        /// <summary>Pitch multiplier at point-blank range, so a closing bot winds up.</summary>
        public float SoundCloseUpPitch = 1.12f;

        // ---- debug ------------------------------------------------------------
        public bool DebugOverlay = false;

        // ----------------------------------------------------------------------
        // Tunable descriptors. Order is the browse order in the settings panel.
        // ----------------------------------------------------------------------

        private List<Tunable> _tunables;

        public IReadOnlyList<Tunable> Tunables => _tunables ?? (_tunables = BuildTunables());

        private List<Tunable> BuildTunables()
        {
            return new List<Tunable>
            {
                new TunableInt("bots", "BOT COUNT", "POPULATION",
                    () => MaxBots, v => MaxBots = v, 0, 8),

                new TunableFloat("speed", "SPEED", "MOVEMENT",
                    () => Speed, v => Speed = v, 1f, 16f, 0.5f, " M/S", "0.0"),

                new TunableFloat("accel", "ACCELERATION", "MOVEMENT",
                    () => Acceleration, v => Acceleration = v, 2f, 40f, 1f, " M/S2", "0"),

                new TunableFloat("turn", "TURN SPEED", "MOVEMENT",
                    () => TurnSpeed, v => TurnSpeed = v, 60f, 3600f, 30f, " D/S", "0"),

                new TunableFloat("reaction", "REACTION TIME", "PURSUIT",
                    () => ReactionTime, v => ReactionTime = v, 0f, 1.5f, 0.05f, " S", "0.00"),

                new TunableFloat("lead", "LEAD TIME", "PURSUIT",
                    () => LeadTime, v => LeadTime = v, 0f, 1.5f, 0.05f, " S", "0.00"),

                new TunableFloat("overshoot", "OVERSHOOT", "PURSUIT",
                    () => Overshoot, v => Overshoot = v, 0f, 20f, 0.5f, " M", "0.0"),

                new TunableBool("sightline", "SIGHTLINE WEIGHT", "TARGETING",
                    () => SightlineWeighting, v => SightlineWeighting = v),

                new TunableFloat("blockedpen", "BLOCKED PENALTY", "TARGETING",
                    () => BlockedPenalty, v => BlockedPenalty = v, 1f, 8f, 0.25f, "x", "0.00"),

                new TunableFloat("hysteresis", "SWITCH MARGIN", "TARGETING",
                    () => SwitchHysteresis, v => SwitchHysteresis = v, 0f, 12f, 0.5f, " M", "0.0"),

                new TunableFloat("ambushrange", "AMBUSH RANGE", "AMBUSH",
                    () => AmbushRange, v => AmbushRange = v, 0f, 40f, 0.5f, " M", "0.0"),

                new TunableFloat("ambushhold", "AMBUSH HOLD", "AMBUSH",
                    () => AmbushHold, v => AmbushHold = v, 0.5f, 20f, 0.25f, " S", "0.00"),

                new TunableFloat("strikedur", "STRIKE TIME", "AMBUSH",
                    () => StrikeDuration, v => StrikeDuration = v, 0.2f, 6f, 0.1f, " S", "0.0"),

                new TunableFloat("strikespeed", "STRIKE SPEED", "AMBUSH",
                    () => StrikeSpeedMultiplier, v => StrikeSpeedMultiplier = v, 1f, 3f, 0.1f, "x", "0.0"),

                new TunableFloat("ambushcd", "AMBUSH COOLDOWN", "AMBUSH",
                    () => AmbushCooldown, v => AmbushCooldown = v, 0f, 20f, 0.25f, " S", "0.00"),

                new TunableFloat("fov", "PLAYER FOV", "AMBUSH",
                    () => FieldOfView, v => FieldOfView = v, 30f, 360f, 5f, " DEG", "0"),

                new TunableFloat("creep", "CREEP SPEED", "AMBUSH",
                    () => CreepSpeedMultiplier, v => CreepSpeedMultiplier = v, 0.1f, 1f, 0.05f, "x", "0.00"),

                new TunableFloat("slope", "MAX SLOPE", "TERRAIN",
                    () => MaxSlope, v => MaxSlope = v, 20f, 85f, 5f, " DEG", "0"),

                new TunableBool("jump", "JUMPING", "JUMP",
                    () => JumpEnabled, v => JumpEnabled = v),

                new TunableFloat("jumpheight", "JUMP HEIGHT", "JUMP",
                    () => JumpHeight, v => JumpHeight = v, 0.4f, 6f, 0.2f, " M", "0.0"),

                new TunableFloat("jumpdur", "JUMP TIME", "JUMP",
                    () => JumpDuration, v => JumpDuration = v, 0.2f, 2f, 0.05f, " S", "0.00"),

                new TunableFloat("jumpdist", "JUMP REACH", "JUMP",
                    () => JumpMaxDistance, v => JumpMaxDistance = v, 1f, 20f, 0.5f, " M", "0.0"),

                new TunableBool("fall", "FALL OFF LEDGES", "JUMP",
                    () => FallEnabled, v => FallEnabled = v),

                new TunableFloat("gravity", "GRAVITY", "JUMP",
                    () => GravityScale, v => GravityScale = v, 0.3f, 4f, 0.1f, "x", "0.0"),

                new TunableFloat("ledgedrop", "MAX FALL", "JUMP",
                    () => LedgeMaxDrop, v => LedgeMaxDrop = v, 1f, 40f, 0.5f, " M", "0.0"),

                new TunableBool("climb", "CLIMB LEDGES", "JUMP",
                    () => ClimbEnabled, v => ClimbEnabled = v),

                new TunableBool("parkour", "PARKOUR JUMPS", "JUMP",
                    () => ParkourEnabled, v => ParkourEnabled = v),

                new TunableFloat("runjump", "RUNNING JUMP", "JUMP",
                    () => RunningJumpBonus, v => RunningJumpBonus = v, 1f, 3f, 0.1f, "x", "0.0"),

                new TunableFloat("shortcut", "SHORTCUT RATIO", "JUMP",
                    () => ParkourShortcutRatio, v => ParkourShortcutRatio = v, 1f, 4f, 0.1f, "x", "0.0"),

                new TunableBool("surfclimb", "SURFACE CLIMBING", "JUMP",
                    () => SurfaceClimbEnabled, v => SurfaceClimbEnabled = v),

                new TunableBool("wallhop", "WALL HOPS", "JUMP",
                    () => WallHopEnabled, v => WallHopEnabled = v),

                new TunableFloat("maxclimb", "MAX GRIP TIME", "JUMP",
                    () => MaxClimbTime, v => MaxClimbTime = v, 0.2f, 8f, 0.2f, " S", "0.0"),

                new TunableFloat("climbspeed", "CLIMB SPEED", "JUMP",
                    () => ClimbSpeedScale, v => ClimbSpeedScale = v, 0.1f, 1.5f, 0.05f, "x", "0.00"),

                new TunableFloat("climbtrig", "CLIMB ABOVE", "JUMP",
                    () => ClimbTriggerHeight, v => ClimbTriggerHeight = v, 0.5f, 12f, 0.25f, " M", "0.0"),

                new TunableFloat("climbh", "CLIMB HEIGHT", "JUMP",
                    () => ClimbMaxHeight, v => ClimbMaxHeight = v, 0.5f, 12f, 0.25f, " M", "0.0"),

                new TunableFloat("decel", "DECELERATION", "MOVEMENT",
                    () => Deceleration, v => Deceleration = v, 2f, 40f, 0.5f, " M/S2", "0.0"),

                new TunableFloat("step", "STEP HEIGHT", "TERRAIN",
                    () => StepHeight, v => StepHeight = v, 0.15f, 1.5f, 0.05f, " M", "0.00"),

                new TunableFloat("stuckrate", "STUCK RATE", "RECOVERY",
                    () => StuckProgressRate, v => StuckProgressRate = v, 0.05f, 3f, 0.05f, " M/S", "0.00"),

                new TunableFloat("stuckcharge", "STUCK -> CHARGE", "RECOVERY",
                    () => StuckChargeTime, v => StuckChargeTime = v, 0.5f, 10f, 0.25f, " S", "0.00"),

                new TunableFloat("stuckreset", "STUCK -> RESET", "RECOVERY",
                    () => StuckResetTime, v => StuckResetTime = v, 1f, 20f, 0.5f, " S", "0.0"),

                new TunableBool("tpseen", "TELEPORT IF SEEN", "RECOVERY",
                    () => TeleportWhenSeen, v => TeleportWhenSeen = v),

                new TunableFloat("catchrange", "CATCH RANGE", "CONTACT",
                    () => CatchRange, v => CatchRange = v, 0.4f, 5f, 0.1f, " M", "0.0"),

                new TunableBool("death", "DEATH ON CATCH", "CONTACT",
                    () => DeathEnabled, v => DeathEnabled = v),

                new TunableBool("sound", "SOUND", "AUDIO",
                    () => SoundEnabled, v => SoundEnabled = v),

                new TunableFloat("vol", "VOLUME", "AUDIO",
                    () => SoundVolume, v => SoundVolume = v, 0f, 1f, 0.05f, "", "0.00"),

                new TunableFloat("sounddist", "HEARING RANGE", "AUDIO",
                    () => SoundMaxDistance, v => SoundMaxDistance = v, 5f, 120f, 5f, " M", "0"),

                new TunableFloat("muffle", "WALL MUFFLE", "AUDIO",
                    () => OcclusionMuffle, v => OcclusionMuffle = v, 0f, 1f, 0.05f, "", "0.00"),

                new TunableBool("debug", "DEBUG OVERLAY", "DEBUG",
                    () => DebugOverlay, v => DebugOverlay = v),

                // Last, so the lobby sync's index order is unchanged for older builds; the list
                // wraps, so it is one press of UP from the top.
                new TunableBool("move", "BOTS MOVE", "CONTROL",
                    () => BotsMove, v => BotsMove = v),
            };
        }

        /// <summary>Cosine of half the FOV, precomputed per read for the facing-cone test.</summary>
        public float HalfFovCos => Mathf.Cos(FieldOfView * 0.5f * Mathf.Deg2Rad);

        // ---- saved between sessions --------------------------------------------

        /// <summary>Your own settings, one <c>key=value</c> per line. By key rather than position, so
        /// adding a setting later never shifts the others.</summary>
        public static string SavePath =>
            Path.Combine(BepInEx.Paths.ConfigPath, "com.stridefr.nextbots.settings.txt");

        /// <summary>Not saved: a switch for right now, and coming back to frozen bots next launch
        /// would look broken.</summary>
        private static readonly HashSet<string> SessionOnly = new HashSet<string> { "move" };

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var t in Tunables)
                    if (!SessionOnly.Contains(t.Key))
                        sb.Append(t.Key).Append('=').Append(t.Raw.ToString("R", CultureInfo.InvariantCulture)).AppendLine();
                File.WriteAllText(SavePath, sb.ToString());
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Settings] could not save: " + ex.Message); }
        }

        public void Load()
        {
            var path = SavePath;
            if (!File.Exists(path)) return;
            int applied = 0;
            try
            {
                var byKey = new Dictionary<string, Tunable>();
                foreach (var t in Tunables) byKey[t.Key] = t;

                foreach (var line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    Tunable t;
                    float v;
                    if (!byKey.TryGetValue(line.Substring(0, eq).Trim(), out t) || SessionOnly.Contains(t.Key)) continue;
                    if (!float.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                    t.Raw = v;
                    applied++;
                }
                Plugin.Log.LogInfo("[Settings] loaded " + applied + " saved setting(s) from " + path);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Settings] could not load " + path + ": " + ex.Message); }
        }

        // ---- sync -------------------------------------------------------------

        /// <summary>Flatten to a float array for the settings-sync network message.</summary>
        public float[] Serialize()
        {
            var t = Tunables;
            var arr = new float[t.Count];
            for (int i = 0; i < t.Count; i++) arr[i] = t[i].Raw;
            return arr;
        }

        /// <summary>
        /// Apply a synced settings payload. Length-tolerant on purpose: a lobby where one
        /// client runs a slightly different build should degrade to "apply what matches"
        /// rather than throw.
        /// </summary>
        public void Deserialize(float[] values)
        {
            if (values == null) return;
            var t = Tunables;
            var n = Mathf.Min(values.Length, t.Count);
            for (int i = 0; i < n; i++)
            {
                try { t[i].Raw = values[i]; }
                catch (Exception ex) { Plugin.Log.LogWarning("Settings sync: " + t[i].Key + ": " + ex.Message); }
            }
        }
    }
}
