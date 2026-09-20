using System.Collections.Generic;
using ExitGames.Client.Photon;
using GorillaLocomotion;
using GorillaRagdoll.Net;
using GorillaRagdoll.Runtime;
using NextBots.Config;
using NextBots.Runtime;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace NextBotsRagdoll
{
    /// <summary>
    /// Turns a nextbot catch into a knockdown: ragdoll, thrown the way the bot hit you, then back
    /// on your feet - or back at spawn, if the host has <c>DEATH ON CATCH</c> on.
    ///
    /// <para><b>Where the hit comes from.</b> NextBots' catch message carries it: which bot,
    /// where it was and how fast it was moving when it connected, measured by the host. Every
    /// client gets the same numbers, so a guest is thrown exactly as the host saw the hit. The
    /// nearest-bot guess and this class's own velocity tracking remain only as fallbacks - for
    /// the F5 test, and for a message that somehow arrives without a position.</para>
    ///
    /// <para><b>Why the knockdown waits a frame.</b> A guest's catch arrives inside Photon's
    /// dispatch, not in Update. <c>RagdollController.Collapse</c> has only ever been exercised
    /// from Update (the toggle key), and it builds a physics skeleton and takes over the rig -
    /// not something to start from an unfamiliar point in the frame. So the catch is queued and
    /// the collapse runs from this component's Update, same as a key press.</para>
    ///
    /// <para><b>Keeping bots off a downed player.</b> Only the host runs brains, so only the
    /// host's <c>WorldView.Excluded</c> matters. The host knows who it caught, and hears each
    /// ragdoll's pose stream (code 150) and get-up (code 151) from GorillaRagdoll's own traffic -
    /// so it can tell when a caught guest is down and when they are back up without any new
    /// messages at all.</para>
    /// </summary>
    public sealed class KnockdownBridge : MonoBehaviour, IOnEventCallback
    {
        public static KnockdownBridge Instance { get; private set; }

        /// <summary>A guest whose catch never produced a ragdoll - no bridge installed, say - is
        /// treated as up again after this long.</summary>
        private const float CatchWindow = 2f;

        /// <summary>Matches GorillaRagdoll's own silence timeout for a remote ragdoll.</summary>
        private const float PoseSilence = 1.5f;

        // ------------------------------------------------------------------ local knockdown

        private struct Hit
        {
            public NextBot Bot;
            public Vector3 From;
            public Vector3 Velocity;

            /// <summary>The bot's image name, for the kill cam and the log.</summary>
            public string By;

            /// <summary>The bot's skin key, for its weight file.</summary>
            public string Skin;

            /// <summary>DEATH ON CATCH as the host had it for this catch.</summary>
            public bool Death;
        }

        /// <summary>The puppet that already has impact listeners on it.</summary>
        private RagdollPuppet _listened;

        private Hit? _pending;

        /// <summary>A knockdown this bridge started and still owns. A ragdoll you started
        /// yourself is not ours, and is never timed out or respawned.</summary>
        private bool _down;
        private float _downUntil;
        private bool _respawnAfter;
        private float _graceUntil;

        // ------------------------------------------------------------------ bot velocity

        private struct Track
        {
            public Vector3 Pos;
            public float Time;
            public Vector3 Vel;
        }

        private readonly Dictionary<NextBot, Track> _tracks = new Dictionary<NextBot, Track>(8);
        private readonly List<NextBot> _gone = new List<NextBot>(4);

        // ------------------------------------------------------------------ who bots leave alone

        private sealed class RemoteDown
        {
            public bool Down;
            public float CaughtAt;
            public float LastPoseAt = -999f;
            public float UpAt = -999f;
        }

        private readonly Dictionary<int, RemoteDown> _remote = new Dictionary<int, RemoteDown>(8);
        private readonly HashSet<int> _excludedByUs = new HashSet<int>();
        private readonly HashSet<int> _want = new HashSet<int>();
        private bool _wasInRoom;

        private BotManager _bots;
        private float _nextBotLookup;

        // ================================================================== lifecycle

        private void Awake() => Instance = this;

        private void OnEnable()
        {
            try { PhotonNetwork.AddCallbackTarget(this); }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[Bridge] no Photon callbacks: " + ex.Message); }

            CatchEffects.Caught += OnAnyCatch;
            CatchEffects.LocalCatchHandler = OnLocalCatch;
        }

        private void OnDisable()
        {
            try { PhotonNetwork.RemoveCallbackTarget(this); } catch { /* shutting down */ }
            ReleaseExclusions();

            CatchEffects.Caught -= OnAnyCatch;
            if (CatchEffects.LocalCatchHandler == (System.Func<CatchInfo, bool>)OnLocalCatch)
                CatchEffects.LocalCatchHandler = null;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ================================================================== the catch

        /// <summary>
        /// Every catch in the lobby, on every client.
        ///
        /// <para>Someone else's catch is heard where their body is: the bot connecting, and -
        /// depending on <c>DeathSound</c> - Garry's Mod's death sound. The host also notes who
        /// went down, so its bots leave them alone. Our own catch is handled by
        /// <see cref="OnLocalCatch"/>, which plays the same sounds from the ragdoll.</para>
        /// </summary>
        private void OnAnyCatch(CatchInfo info)
        {
            if (!BridgeConfig.Enabled.Value || info == null || info.VictimIsLocal) return;

            var down = Remote(info.VictimActor);
            down.Down = true;
            down.CaughtAt = Time.time;

            var sounds = ImpactSounds.Instance;
            if (sounds == null || !BridgeConfig.HearOthers.Value) return;

            Vector3 at;
            if (!PlayerNames.TryGetPosition(info.VictimActor, out at)) at = info.VictimPosition;
            var profile = BotProfiles.Get(info.BotSkin);
            sounds.OnBotHit(at, profile);
            if (WantsDeathSound(info.Death)) sounds.PlayDeath(at);
        }

        private static bool WantsDeathSound(bool killed)
        {
            switch (BridgeConfig.DeathSound.Value)
            {
                case DeathSoundMode.EveryCatch: return true;
                case DeathSoundMode.KilledOnly: return killed;
                default: return false;
            }
        }

        /// <summary>
        /// Replaces NextBots' own effect when the victim is us. Returns true if the bridge has
        /// taken the catch; false leaves it to NextBots (haptics, and spawn with DEATH ON CATCH).
        ///
        /// <para>Everything about the hit comes from the host's message: which bot, where it was
        /// and how fast it was going when it connected. A guest no longer has to guess the
        /// nearest bot, and the throw matches what the host saw.</para>
        /// </summary>
        private bool OnLocalCatch(CatchInfo info)
        {
            if (!BridgeConfig.Enabled.Value || info == null) return false;

            var ctrl = GorillaRagdoll.Plugin.Controller;
            if (ctrl == null)
            {
                Plugin.Log.LogWarning("[Bridge] caught before GorillaRagdoll finished booting - using NextBots' catch");
                return false;
            }

            // Collapse would refuse here (public lobby). Leave the catch to NextBots rather than
            // swallowing it and doing nothing.
            if (!ctrl.IsRagdolled && !GorillaRagdoll.Runtime.Venue.Allowed) return false;

            var bot = info.Bot;
            Vector3 from = info.BotPosition;
            Vector3 vel = info.BotVelocity;

            // A message that somehow carried no position: fall back to our own copy of the bot,
            // then to "it came from where you were looking".
            if (from == Vector3.zero)
            {
                var me = PlayerPosition(ctrl);
                if (bot == null) bot = NearestBot(me);
                from = bot != null ? bot.Center : me + Flat(ViewForward());
                if (bot != null && vel == Vector3.zero) vel = VelocityOf(bot);
            }

            _pending = new Hit
            {
                Bot = bot,
                From = from,
                Velocity = vel,
                By = string.IsNullOrEmpty(info.BotName) ? "?" : info.BotName,
                Skin = info.BotSkin,
                Death = info.Death
            };
            return true;
        }

        /// <summary>
        /// The test key: the nearest bot catches you, through exactly the same path as a real
        /// catch. A bot standing still is given <c>TestBotSpeed</c> towards you, since a test
        /// that throws you nowhere tests nothing. With no bots out you are hit from the front.
        ///
        /// <para>The rest of the lobby is told as well, so the others see it in their death log
        /// and hear it, exactly as they would a real catch. That is the point of the key: one
        /// person can press it and everyone can check that deaths show up on their screen.</para>
        /// </summary>
        private void TestCatch()
        {
            var ctrl = GorillaRagdoll.Plugin.Controller;
            if (ctrl == null) { Plugin.Log.LogWarning("[Bridge] test catch: GorillaRagdoll not ready"); return; }
            if (!ctrl.IsRagdolled && !GorillaRagdoll.Runtime.Venue.Allowed)
            {
                Plugin.Log.LogWarning("[Bridge] test catch refused: " + GorillaRagdoll.Runtime.Venue.BlockedReason);
                return;
            }

            var me = PlayerPosition(ctrl);
            var bot = NearestBot(me);
            float speed = BridgeConfig.TestBotSpeed.Value;

            Vector3 from = bot != null ? bot.Center : me + Flat(ViewForward()) * 1.2f;
            Vector3 vel = bot != null ? VelocityOf(bot) : Vector3.zero;
            if (Flat(vel).magnitude < 0.5f)
            {
                var towards = Flat(me - from);
                vel = towards.sqrMagnitude > 1e-4f ? towards.normalized * speed : -Flat(ViewForward()) * speed;
            }

            // Through NextBots' real catch path: the death log, the sounds and the knockdown all
            // see exactly what a real catch would give them.
            int actor = LocalActor();
            var info = new CatchInfo
            {
                VictimActor = actor,
                VictimName = PlayerNames.Of(actor),
                BotNetId = bot != null ? bot.NetId : -1,
                BotSkin = bot != null ? bot.SkinName : "",
                BotName = bot != null ? bot.DisplayName : "TEST",
                BotPosition = from,
                BotVelocity = vel,
                VictimPosition = me,
                Death = NextBotSettings.Active.DeathEnabled,
                Time = CatchEffects.Now(),
                Bot = bot,
                Test = true
            };

            var manager = Bots();
            bool sent = false;
            if (manager != null && manager.Net != null && PhotonNetwork.InRoom)
            {
                manager.Net.SendCaught(info);
                sent = true;
            }
            Plugin.Log.LogInfo("[Bridge] test catch (" + BridgeConfig.TestCatchKey.Value + ") by '" + info.BotName + "'" +
                               (sent ? ", sent to the lobby" : ""));
            CatchEffects.Dispatch(info);
        }

        // ================================================================== frame

        private void Update()
        {
            TrackRoom();
            TrackBots();

            if (BridgeConfig.Enabled.Value && SafeInput.KeyDown(BridgeConfig.TestCatchKey.Value))
                TestCatch();

            if (_pending.HasValue)
            {
                var hit = _pending.Value;
                _pending = null;
                KnockDown(hit);
            }

            ListenForImpacts();
            TickKnockdown();
            SyncExclusions();
        }

        /// <summary>
        /// Puts an impact listener on every part of a freshly built ragdoll - including one you
        /// started yourself, because a body hitting the floor should sound the same either way.
        /// </summary>
        private void ListenForImpacts()
        {
            var ctrl = GorillaRagdoll.Plugin.Controller;
            var puppet = ctrl != null ? ctrl.Pose as RagdollPuppet : null;
            if (puppet == _listened) return;

            _listened = puppet;
            if (puppet == null || puppet.Root == null) return;

            foreach (var rb in puppet.Root.GetComponentsInChildren<Rigidbody>())
                if (rb.GetComponent<ImpactListener>() == null) rb.gameObject.AddComponent<ImpactListener>();
        }

        private void KnockDown(Hit hit)
        {
            var ctrl = GorillaRagdoll.Plugin.Controller;
            bool already = ctrl != null && ctrl.IsRagdolled;
            if (ctrl != null && !already) ctrl.Collapse();

            var puppet = ctrl != null ? ctrl.Pose as RagdollPuppet : null;
            if (puppet == null || puppet.Root == null || puppet.Body == null)
            {
                // The ragdoll refused; its Status says why. Do what NextBots would have done.
                Plugin.Log.LogWarning("[Bridge] could not ragdoll (" + (ctrl != null ? ctrl.Status : "no controller") +
                                      ") - falling back to NextBots' catch");
                if (hit.Death) CatchEffects.SendToSpawn();
                return;
            }

            var profile = BotProfiles.Get(string.IsNullOrEmpty(hit.Skin) ? hit.By : hit.Skin);
            Vector3 torso = puppet.Body.position;

            // Before anything else: the face, while the hit is still the thing you are reacting to.
            if (Jumpscare.Instance != null) Jumpscare.Instance.Play(hit.Skin, hit.By);
            if (DeathVignette.Instance != null) DeathVignette.Instance.Begin();

            Vector3 dir;
            var launch = Launch(hit, torso, profile, out dir);
            Throw(puppet, launch, dir, profile.tumble >= 0f ? profile.tumble : BridgeConfig.Tumble.Value * profile.Scale);
            Haptics(profile);
            if (ImpactSounds.Instance != null)
            {
                ImpactSounds.Instance.OnBotHit(torso, profile);
                if (WantsDeathSound(hit.Death)) ImpactSounds.Instance.PlayDeath(torso);
            }

            _down = true;
            float hold = profile.downSeconds >= 0f ? profile.downSeconds : BridgeConfig.DownSeconds.Value;
            _downUntil = hold > 0f ? Time.time + hold : 0f;

            // The host's setting at the moment of the catch, carried in the catch itself, so the
            // whole lobby plays by one rule.
            _respawnAfter = hit.Death;

            float botSpeed = Flat(hit.Velocity).magnitude;
            if (KillCam.Instance != null)
                KillCam.Instance.Begin(hit.Bot, hit.From, dir, torso,
                    (_respawnAfter ? "KILLED BY " : "KNOCKED DOWN BY ") + hit.By,
                    profile.weight.ToString("0") + " KG  |  HIT AT " + botSpeed.ToString("0.0") +
                    " M/S  |  THROWN " + launch.magnitude.ToString("0.0") + " M/S",
                    _downUntil);

            Plugin.Log.LogInfo("[Bridge] " + (already ? "hit again" : "knocked down") + " by '" + hit.By + "'" +
                               " | " + profile.weight.ToString("0") + " kg (x" + profile.Scale.ToString("0.00") + ")" +
                               " | bot " + botSpeed.ToString("0.0") + " m/s" +
                               " | launch " + launch.magnitude.ToString("0.0") + " m/s" +
                               (_respawnAfter ? " | respawn after" : ""));
        }

        /// <summary>
        /// The velocity to throw the body with.
        ///
        /// <para>Two directions are blended. <i>Away</i> - from the bot's centre through yours - is
        /// what a shove does. <i>Travel</i> - the way the bot was running - is what being run over
        /// does. A bot that clips you from the side at full sprint should carry you along with it,
        /// not bounce you out sideways, so the default leans towards travel. Horizontal only: the
        /// upward part is a separate, fixed lift, so a bot dropping on you from a ledge does not
        /// pile-drive you into the floor.</para>
        ///
        /// <para>The bot's weight scales both, through <see cref="BotProfile.Scale"/>. Only the cap
        /// is left alone, so a very heavy bot hits as hard as the config allows and no harder.</para>
        /// </summary>
        private static Vector3 Launch(Hit hit, Vector3 torso, BotProfile profile, out Vector3 dir)
        {
            Vector3 away = Flat(torso - hit.From);
            Vector3 travel = Flat(hit.Velocity);
            float botSpeed = travel.magnitude;

            // Below walking pace the bot's heading is noise.
            bool moving = botSpeed > 0.5f;

            Vector3 a = away.sqrMagnitude > 1e-4f ? away.normalized : moving ? travel / botSpeed : Vector3.zero;
            Vector3 t = moving ? travel / botSpeed : a;

            float bias = BridgeConfig.TravelBias.Value;
            dir = a * (1f - bias) + t * bias;
            if (dir.sqrMagnitude < 1e-4f) dir = a;                          // the two cancelled out
            if (dir.sqrMagnitude < 1e-4f) dir = -Flat(ViewForward());       // no information at all
            if (dir.sqrMagnitude < 1e-4f) dir = Vector3.back;
            dir.Normalize();

            float scale = profile.Scale;
            float speed = (BridgeConfig.BaseSpeed.Value + botSpeed * BridgeConfig.SpeedTransfer.Value) * scale;
            float lift = profile.lift >= 0f ? profile.lift : BridgeConfig.Lift.Value * scale;
            var v = dir * speed + Vector3.up * lift;
            return Vector3.ClampMagnitude(v, BridgeConfig.MaxLaunch.Value);
        }

        /// <summary>
        /// Adds the launch on top of whatever the ragdoll was seeded with, so running away from
        /// the bot and being hit from behind stacks the way it should.
        ///
        /// <para>Every body gets the same velocity change. Launching only the torso makes the
        /// joints do the work of dragging the head and hands after it, and at these speeds they
        /// stretch - the limbs trail on elastic. The tumble goes on the torso alone, about the
        /// axis that tips its top in the direction of the hit: hit from the front, you go over
        /// backwards.</para>
        /// </summary>
        private static void Throw(RagdollPuppet puppet, Vector3 launch, Vector3 dir, float tumble)
        {
            foreach (var rb in puppet.Root.GetComponentsInChildren<Rigidbody>())
            {
                rb.WakeUp();
                rb.AddForce(launch, ForceMode.VelocityChange);
            }

            var torso = puppet.Body.GetComponent<Rigidbody>();
            if (torso != null && tumble > 0f)
                torso.AddTorque(Vector3.Cross(Vector3.up, dir) * tumble, ForceMode.VelocityChange);

            // A body that had already settled is asleep and flagged as such; without this it
            // would take the hit but stop running gravity and the settle check.
            puppet.Disturb();
        }

        private void TickKnockdown()
        {
            if (!_down) return;

            var ctrl = GorillaRagdoll.Plugin.Controller;

            // Up by some other route: the toggle, panic, AutoGetUpSeconds, or falling out of the
            // world. Still ours to finish - the respawn and the grace period apply regardless.
            if (ctrl == null || !ctrl.IsRagdolled)
            {
                EndKnockdown();
                return;
            }

            if (_downUntil > 0f && Time.time >= _downUntil)
            {
                ctrl.GetUp();
                EndKnockdown();
            }
        }

        private void EndKnockdown()
        {
            _down = false;
            _downUntil = 0f;
            _graceUntil = Time.time + BridgeConfig.GetUpGrace.Value;
            if (KillCam.Instance != null) KillCam.Instance.End();
            if (DeathVignette.Instance != null) DeathVignette.Instance.Release();

            if (_respawnAfter)
            {
                _respawnAfter = false;
                CatchEffects.SendToSpawn();
            }
        }

        // ================================================================== bots

        private BotManager Bots()
        {
            if (_bots != null) return _bots;
            if (Time.time < _nextBotLookup) return null;
            _nextBotLookup = Time.time + 1f;
            _bots = FindFirstObjectByType<BotManager>();

            // NextBots is up, so its skins can be listed: give each one a weight file to edit.
            if (_bots != null) BotProfiles.EnsureTemplates(BotSkins.Names);
            return _bots;
        }

        /// <summary>
        /// Every bot's velocity, from its own motion. A guest's bots are puppets with no agent,
        /// so <c>NextBot.Velocity</c> reads zero there; differencing works on both sides, and
        /// catches jumps and climbs that an agent's velocity would not.
        /// </summary>
        private void TrackBots()
        {
            var mgr = Bots();
            if (mgr == null) return;

            float now = Time.time;
            var list = mgr.Bots;
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                if (b == null) continue;

                var p = b.Center;
                Track t;
                if (_tracks.TryGetValue(b, out t))
                {
                    float dt = now - t.Time;
                    if (dt > 1e-4f)
                    {
                        var instant = (p - t.Pos) / dt;
                        // A warp (stuck reset, fall recovery) is not a velocity.
                        if (instant.sqrMagnitude > 60f * 60f) instant = t.Vel;
                        t.Vel = Vector3.Lerp(t.Vel, instant, Mathf.Clamp01(dt * 10f));
                    }
                }
                else t.Vel = Vector3.zero;

                t.Pos = p;
                t.Time = now;
                _tracks[b] = t;
            }

            if (_tracks.Count > list.Count)
            {
                _gone.Clear();
                foreach (var k in _tracks.Keys) if (k == null) _gone.Add(k);
                for (int i = 0; i < _gone.Count; i++) _tracks.Remove(_gone[i]);
            }
        }

        private Vector3 VelocityOf(NextBot bot)
        {
            Track t;
            if (_tracks.TryGetValue(bot, out t) && t.Vel.sqrMagnitude > 0.01f) return t.Vel;
            return bot.Velocity;
        }

        private NextBot NearestBot(Vector3 near)
        {
            var mgr = Bots();
            if (mgr == null) return null;

            NextBot best = null;
            float bestSq = float.MaxValue;
            var list = mgr.Bots;
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                if (b == null || !b.Alive) continue;
                float d = (b.Center - near).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = b; }
            }
            return best;
        }

        // ================================================================== exclusion

        /// <summary>
        /// Mirrors who should be left alone into the host's <c>WorldView.Excluded</c>. Only ever
        /// touches entries this bridge added, so anything else using that set is left intact.
        /// </summary>
        private void SyncExclusions()
        {
            if (!BridgeConfig.Enabled.Value) { ReleaseExclusions(); return; }

            var mgr = Bots();
            var world = mgr != null ? mgr.World : null;
            if (world == null) return;

            float now = Time.time;
            float grace = BridgeConfig.GetUpGrace.Value;
            bool safe = BridgeConfig.DownedPlayersAreSafe.Value;
            _want.Clear();

            // Grace after getting up always applies - a bot standing over your body would
            // otherwise catch you the instant you stood. Being untargeted while down is the
            // optional part.
            int local = LocalActor();
            if (local >= 0 && ((safe && (_down || _pending.HasValue)) || now < _graceUntil))
                _want.Add(local);

            foreach (var kv in _remote)
            {
                var s = kv.Value;
                if (s.Down && now - s.CaughtAt > CatchWindow && now - s.LastPoseAt > PoseSilence)
                {
                    s.Down = false;   // never ragdolled, or stopped sending: treat as up
                    s.UpAt = now;
                }
                if ((safe && s.Down) || now - s.UpAt < grace) _want.Add(kv.Key);
            }

            foreach (var a in _excludedByUs)
                if (!_want.Contains(a)) world.Excluded.Remove(a);
            foreach (var a in _want) world.Excluded.Add(a);

            _excludedByUs.Clear();
            _excludedByUs.UnionWith(_want);
        }

        private void ReleaseExclusions()
        {
            var world = _bots != null ? _bots.World : null;
            if (world != null)
                foreach (var a in _excludedByUs) world.Excluded.Remove(a);
            _excludedByUs.Clear();
        }

        /// <summary>Actor numbers are per room; carrying them into the next one would protect
        /// or expose the wrong people.</summary>
        private void TrackRoom()
        {
            bool inRoom = PhotonNetwork.InRoom;
            if (inRoom != _wasInRoom)
            {
                _remote.Clear();
                ReleaseExclusions();
            }
            _wasInRoom = inRoom;
        }

        /// <summary>GorillaRagdoll's own traffic, overheard: 150 while someone is down, 151 when
        /// they get up. Read as a presence signal only - GorillaRagdoll does the decoding.</summary>
        public void OnEvent(EventData e)
        {
            if (e.Code != RagdollNet.EvPose && e.Code != RagdollNet.EvEnd) return;

            var s = Remote(e.Sender);
            if (e.Code == RagdollNet.EvPose) s.LastPoseAt = Time.time;
            else if (s.Down)
            {
                s.Down = false;
                s.UpAt = Time.time;
            }
        }

        private RemoteDown Remote(int actor)
        {
            RemoteDown s;
            if (!_remote.TryGetValue(actor, out s))
            {
                s = new RemoteDown();
                _remote[actor] = s;
            }
            return s;
        }

        // ================================================================== plumbing

        private static int LocalActor()
        {
            try
            {
                var net = NetworkSystem.Instance;
                if (net == null || net.LocalPlayer == null) return -1;
                return net.LocalPlayer.ActorNumber;
            }
            catch { return -1; }
        }

        private static Vector3 PlayerPosition(RagdollController ctrl)
        {
            var puppet = ctrl.Pose as RagdollPuppet;
            if (puppet != null && puppet.Body != null) return puppet.Body.position;

            var p = GTPlayer.Instance;
            if (p == null) return Vector3.zero;
            if (p.bodyCollider != null) return p.bodyCollider.transform.position;
            return p.transform.position;
        }

        private static Vector3 ViewForward()
        {
            var p = GTPlayer.Instance;
            return p != null && p.mainCamera != null ? p.mainCamera.transform.forward : Vector3.forward;
        }

        private static Vector3 Flat(Vector3 v) => Vector3.ProjectOnPlane(v, Vector3.up);

        /// <summary>Heavier bots shake harder and longer.</summary>
        private static void Haptics(BotProfile profile)
        {
            var tagger = GorillaTagger.Instance;
            if (tagger == null) return;

            float amp = Mathf.Clamp(0.4f + 0.5f * profile.Scale, 0.3f, 1f);
            float dur = Mathf.Clamp(0.3f * profile.Scale, 0.15f, 0.7f);
            try
            {
                tagger.StartVibration(true, amp, dur);
                tagger.StartVibration(false, amp, dur);
            }
            catch { /* a nicety */ }
        }
    }
}
