using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ExitGames.Client.Photon;
using GorillaRagdoll.Config;
using GorillaRagdoll.Runtime;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace GorillaRagdoll.Net
{
    /// <summary>
    /// Ragdolls for everyone in the room who has the mod.
    ///
    /// <para><b>Transport.</b> Photon <c>RaiseEvent</c> with a <c>byte[]</c> payload - the same
    /// channel NextBots already runs on this install. Codes 150-151 are clear of Gorilla Tag's
    /// own traffic (1-3, 8, 50, 51, 100-103, 176, 177, 180, 186, 199, 202), clear of NextBots'
    /// 140-146, and below Photon's reserved 200+.</para>
    ///
    /// <para><b>Players without the mod</b> receive an event code they do not know and drop it.
    /// That was checked in the decompile rather than assumed: every handler in the game -
    /// <c>RoomSystem</c>, <c>PlayerCosmeticsSystem</c> and the four raw Photon listeners -
    /// filters on its own code and returns silently on anything else. None of them reports an
    /// unknown code to the anti-cheat, and <c>MonkeAgent</c>'s RPC counter only runs inside
    /// those handlers, so this traffic never touches it.</para>
    ///
    /// <para><b>Model.</b> The owner's physics is authoritative. Poses go out unreliably at
    /// <c>NetSendRate</c> while ragdolled; getting up goes out reliably. Receivers start a
    /// ragdoll on the first pose they see from someone, so there is no separate "start" packet
    /// to lose, and a late joiner picks up a ragdoll already in progress. Silence for a second
    /// and a half ends it, which covers crashes, disconnects and the owner turning sharing off.</para>
    /// </summary>
    public sealed class RagdollNet : MonoBehaviour, IOnEventCallback
    {
        public const byte EvPose = 150;
        public const byte EvEnd = 151;

        /// <summary>Wire format version. A different build is ignored rather than misread.</summary>
        public const byte Protocol = 1;

        /// <summary>Four poses at 7 floats each, plus the version byte and a timestamp.</summary>
        private const int PoseBytes = 1 + 8 + 4 * 7 * 4;

        private const float SilenceTimeout = 1.5f;

        /// <summary>
        /// How far a received torso may be from where the game already has that player.
        /// Generous on purpose: a ragdolling player's own networked position freezes where they
        /// collapsed, so a long fall off a map edge legitimately carries the body a long way from
        /// it. The bound exists only so a malformed or hostile packet cannot fling an avatar to
        /// the far side of the world.
        /// </summary>
        private const float MaxRemoteReach = 500f;

        public RagdollController Controller;

        /// <summary>Remote ragdolls by Photon actor number.</summary>
        private static readonly Dictionary<int, RemoteRagdoll> Remotes = new Dictionary<int, RemoteRagdoll>();
        private static readonly List<int> Scratch = new List<int>(8);

        /// <summary>Whether the pre-IK hook is live. If Harmony could not patch, poses are
        /// applied from this component's LateUpdate instead - usually fine, but one frame out
        /// of phase with the IK whenever Unity happens to run that first.</summary>
        public static bool HookInstalled;

        public static int RemoteCount => Remotes.Count;

        /// <summary>Other players' ragdolls as this client is showing them right now.</summary>
        public static IEnumerable<RemoteRagdoll> All => Remotes.Values;
        public bool Sending { get; private set; }

        private float _sendAccum;
        private bool _wasInRoom;
        private readonly MemoryStream _buffer = new MemoryStream(PoseBytes);

        private static readonly RaiseEventOptions ToOthers = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
        private static readonly SendOptions Unreliable = new SendOptions { Reliability = false };
        private static readonly SendOptions Reliable = new SendOptions { Reliability = true };

        // ==================================================================== lifecycle

        private void OnEnable()
        {
            try { PhotonNetwork.AddCallbackTarget(this); }
            catch (Exception ex) { Plugin.Log.LogWarning("[Net] could not register for events: " + ex.Message); }
        }

        private void OnDisable()
        {
            if (Sending) SendEnd();
            Sending = false;
            try { PhotonNetwork.RemoveCallbackTarget(this); } catch { /* shutting down */ }
            EndAll("mod shutting down");
        }

        private void Update()
        {
            bool inRoom = PhotonNetwork.InRoom;
            if (!inRoom && _wasInRoom)
            {
                // Everyone we were drawing belonged to that room.
                EndAll("left the room");
                Sending = false;
            }
            _wasInRoom = inRoom;

            UpdateSending(inRoom);
            ExpireRemotes();
        }

        private void LateUpdate()
        {
            if (!HookInstalled) ApplyRemotes();
        }

        // ==================================================================== sending

        private void UpdateSending(bool inRoom)
        {
            var pose = Controller != null ? Controller.Pose : null;
            bool shouldSend = inRoom && pose != null && RagdollConfig.ShareMyRagdoll.Value && Venue.Allowed;

            if (!shouldSend)
            {
                // The one reliable packet: "I am standing again". Without it everyone else
                // would keep drawing the last pose until the silence timeout, which reads as
                // the body hanging in the air for a second and a half after you get up.
                if (Sending && inRoom) SendEnd();
                Sending = false;
                _sendAccum = 0f;
                return;
            }

            if (!Sending)
            {
                Sending = true;
                _sendAccum = float.MaxValue;   // first pose goes out this frame
                float flush = FlushRate();
                Plugin.Log.LogInfo("[Net] sharing ragdoll with the room at " +
                                   SendRate().ToString("0") + "Hz | Photon flushes " + flush.ToString("0") +
                                   "/s" + (RagdollConfig.NetFlushSends.Value
                                       ? ", each pose pushed out as it is sent"
                                       : RagdollConfig.NetSendRate.Value > flush
                                           ? ", so the asked-for " + RagdollConfig.NetSendRate.Value.ToString("0") +
                                             "Hz was capped - turn on NetFlushSends for more"
                                           : ""));
            }

            float interval = 1f / Mathf.Max(1f, SendRate());
            _sendAccum += Time.unscaledDeltaTime;
            if (_sendAccum < interval) return;

            // Carry the remainder rather than zeroing: zeroing rounds every send up to the next
            // frame, so the spacing wobbles by a frame each time and the receiver has to smooth
            // out unevenness this end invented.
            _sendAccum = Mathf.Min(_sendAccum - interval, interval);

            SendPose(pose);
        }

        /// <summary>
        /// Photon does not send when <c>RaiseEvent</c> is called: it queues, and flushes on its
        /// own timer. Asking for more poses a second than that timer allows does not get them
        /// there sooner - it gets them there in pairs, which looks worse at the far end than
        /// fewer, evenly spaced. So the rate is capped at the flush rate.
        /// </summary>
        private static float SendRate()
        {
            // With the immediate flush on, each pose leaves the moment it is queued and the
            // flush timer holds nothing back, so the asked-for rate stands.
            return RagdollConfig.NetFlushSends.Value
                ? RagdollConfig.NetSendRate.Value
                : Mathf.Min(RagdollConfig.NetSendRate.Value, FlushRate());
        }

        private static float FlushRate()
        {
            try { return Mathf.Max(1, PhotonNetwork.SendRate); }
            catch { return 30f; }
        }

        private void SendPose(IPoseSource pose)
        {
            if (pose.Body == null || pose.Head == null || pose.HandL == null || pose.HandR == null) return;

            _buffer.SetLength(0);
            using (var w = new BinaryWriter(_buffer, System.Text.Encoding.UTF8, true))
            {
                w.Write(Protocol);
                w.Write(PhotonNetwork.Time);
                Write(w, pose.Body);
                Write(w, pose.Head);
                Write(w, pose.HandL);
                Write(w, pose.HandR);
            }

            // Unreliable on purpose: a dropped pose is superseded one send interval later, and
            // reliable delivery would stall behind a lost packet and then deliver a burst.
            Raise(EvPose, _buffer.ToArray(), Unreliable);

            // And out of the queue now, so it is not held back to Photon's next flush and then
            // delivered alongside the following one.
            if (RagdollConfig.NetFlushSends.Value)
            {
                try { PhotonNetwork.SendAllOutgoingCommands(); }
                catch { /* not fatal: the pose goes out on the next flush instead */ }
            }
        }

        private void SendEnd()
        {
            _buffer.SetLength(0);
            using (var w = new BinaryWriter(_buffer, System.Text.Encoding.UTF8, true))
            {
                w.Write(Protocol);
                w.Write(PhotonNetwork.Time);
            }
            Raise(EvEnd, _buffer.ToArray(), Reliable);
            Plugin.Log.LogInfo("[Net] stopped sharing ragdoll");
        }

        private static void Write(BinaryWriter w, Transform t)
        {
            t.GetPositionAndRotation(out var p, out var q);
            w.Write(p.x); w.Write(p.y); w.Write(p.z);
            w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
        }

        private static void Raise(byte code, byte[] payload, SendOptions how)
        {
            if (!PhotonNetwork.InRoom) return;
            try { PhotonNetwork.RaiseEvent(code, payload, ToOthers, how); }
            catch (Exception ex) { Plugin.Log.LogWarning("[Net] send " + code + " failed: " + ex.Message); }
        }

        // ==================================================================== receiving

        public void OnEvent(EventData e)
        {
            if (e.Code != EvPose && e.Code != EvEnd) return;   // not ours

            var payload = e.CustomData as byte[];
            if (payload == null || payload.Length < 9) return;

            try
            {
                using (var r = new BinaryReader(new MemoryStream(payload)))
                {
                    if (r.ReadByte() != Protocol) return;   // another build of the mod; ignore quietly
                    double time = r.ReadDouble();

                    if (e.Code == EvEnd) { End(e.Sender, "they got up"); return; }

                    if (!RagdollConfig.ShowOtherRagdolls.Value || !Venue.Allowed) return;
                    if (payload.Length < PoseBytes) return;

                    var s = new PoseSnapshot { Time = time };
                    if (!Read(r, out s.BodyPos, out s.BodyRot)) return;
                    if (!Read(r, out s.HeadPos, out s.HeadRot)) return;
                    if (!Read(r, out s.HandLPos, out s.HandLRot)) return;
                    if (!Read(r, out s.HandRPos, out s.HandRRot)) return;

                    OnPose(e.Sender, s);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Net] malformed ragdoll event " + e.Code + " from " + e.Sender + ": " + ex.Message);
            }
        }

        /// <summary>Reads one pose, rejecting anything that would poison a transform. A single
        /// NaN written into the rig spams errors every frame until the scene reloads.</summary>
        private static bool Read(BinaryReader r, out Vector3 p, out Quaternion q)
        {
            p = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

            if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z)) return false;
            if (!Finite(q.x) || !Finite(q.y) || !Finite(q.z) || !Finite(q.w)) return false;

            float len = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (len < 0.5f || len > 1.5f) return false;
            q = new Quaternion(q.x / len, q.y / len, q.z / len, q.w / len);
            return true;
        }

        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        private static void OnPose(int actor, PoseSnapshot s)
        {
            if (PhotonNetwork.LocalPlayer != null && actor == PhotonNetwork.LocalPlayer.ActorNumber) return;

            RemoteRagdoll rd;
            Remotes.TryGetValue(actor, out rd);
            var rig = rd != null ? rd.Rig : FindRig(actor);
            if (rig == null) return;   // not spawned on this client yet; the next pose retries

            // Checked before anything is created, so a stream of out-of-range packets costs
            // nothing rather than building and tearing down a remote every packet.
            if (Vector3.Distance(s.BodyPos, rig.transform.position) > MaxRemoteReach) return;

            if (rd == null)
            {
                rd = new RemoteRagdoll(actor, rig);
                Remotes[actor] = rd;
            }
            rd.Push(s);
        }

        private static void End(int actor, string why)
        {
            RemoteRagdoll rd;
            if (!Remotes.TryGetValue(actor, out rd)) return;
            Remotes.Remove(actor);
            rd.End(why);
        }

        private static void EndAll(string why)
        {
            Scratch.Clear();
            Scratch.AddRange(Remotes.Keys);
            foreach (var a in Scratch) End(a, why);
        }

        private static void ExpireRemotes()
        {
            if (Remotes.Count == 0) return;

            bool showing = RagdollConfig.ShowOtherRagdolls.Value && Venue.Allowed;

            Scratch.Clear();
            foreach (var kv in Remotes)
            {
                var rd = kv.Value;
                if (!showing) Scratch.Add(kv.Key);
                else if (Time.unscaledTime - rd.LastHeard > SilenceTimeout) Scratch.Add(kv.Key);
                else if (!StillOwnedBy(rd.Rig, kv.Key)) Scratch.Add(kv.Key);
            }

            foreach (var a in Scratch)
            {
                RemoteRagdoll rd;
                if (!Remotes.TryGetValue(a, out rd)) continue;
                string why = !showing ? "hidden" :
                             Time.unscaledTime - rd.LastHeard > SilenceTimeout ? "no packets for " + SilenceTimeout + "s" :
                             "player left";
                End(a, why);
            }
        }

        /// <summary>
        /// VRRigs are pooled. When a player leaves, their rig goes back to <c>VRRigCache</c> and
        /// can be handed straight to someone who just joined - so a remote ragdoll that outlives
        /// its owner would start posing a stranger. Checked every frame, not left to the
        /// timeout.
        /// </summary>
        private static bool StillOwnedBy(VRRig rig, int actor)
        {
            try { return rig != null && rig.Creator != null && rig.Creator.ActorNumber == actor; }
            catch { return false; }
        }

        // ==================================================================== drawing

        /// <summary>
        /// Poses every remote ragdoll. Called immediately before <c>GorillaIKMgr</c> reads its
        /// targets - see <see cref="IkHook"/> for why that exact moment.
        /// </summary>
        private static int _appliedFrame = -1;
        private static float _appliedAt;

        internal static void ApplyRemotes()
        {
            if (Remotes.Count == 0) return;

            // Once per frame. The IK hook fires once, but if the patch ever failed and the
            // LateUpdate fallback took over mid-session, both could run in the same frame and
            // advance every playback clock twice.
            if (_appliedFrame == Time.frameCount) return;
            _appliedFrame = Time.frameCount;

            float now = Time.unscaledTime;
            float dt = _appliedAt > 0f ? Mathf.Clamp(now - _appliedAt, 0f, 0.25f) : Time.unscaledDeltaTime;
            _appliedAt = now;

            foreach (var kv in Remotes)
            {
                // Each ragdoll runs its own playback clock, a measured distance behind the
                // sender, and curves between the snapshots either side of it.
                try { kv.Value.Apply(dt); }
                catch (Exception ex)
                {
                    // One broken remote must never take the IK down with it: this runs inside
                    // the game's own IK update, for every avatar in the room.
                    Plugin.Log.LogWarning("[Net] drawing actor " + kv.Key + " failed: " + ex.Message);
                }
            }
        }

        // ==================================================================== rig lookup

        private static MethodInfo _tryGetVrrig;
        private static bool _lookupProbed;

        /// <summary>
        /// The avatar belonging to a Photon actor.
        ///
        /// <para><c>VRRigCache.TryGetVrrig</c> is the game's own answer but it is
        /// <c>internal</c>, so it is reached by reflection; if that ever moves, the fallback
        /// scans live rigs for the matching public <c>Creator</c>. The scan only runs on a
        /// cache miss - once per remote ragdoll, not per packet.</para>
        /// </summary>
        private static VRRig FindRig(int actor)
        {
            try
            {
                if (!_lookupProbed)
                {
                    _lookupProbed = true;
                    _tryGetVrrig = typeof(VRRigCache).GetMethod("TryGetVrrig",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new[] { typeof(int), typeof(RigContainer).MakeByRefType() }, null);
                    if (_tryGetVrrig == null)
                        Plugin.Log.LogWarning("[Net] VRRigCache.TryGetVrrig(int) not found - falling back to scanning rigs");
                }

                if (_tryGetVrrig != null && VRRigCache.Instance != null)
                {
                    var args = new object[] { actor, null };
                    if ((bool)_tryGetVrrig.Invoke(VRRigCache.Instance, args))
                    {
                        var container = args[1] as RigContainer;
                        if (container != null && container.Rig != null) return container.Rig;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Net] rig lookup failed, scanning instead: " + ex.Message);
                _tryGetVrrig = null;
            }

            foreach (var rig in UnityEngine.Object.FindObjectsByType<VRRig>(FindObjectsSortMode.None))
                if (StillOwnedBy(rig, actor) && rig != VRRig.LocalRig) return rig;
            return null;
        }
    }
}
