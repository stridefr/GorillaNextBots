using System;
using System.IO;
using ExitGames.Client.Photon;
using NextBots.Config;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace NextBots.Net
{
    /// <summary>
    /// Replication for the bots, over Photon's RaiseEvent.
    ///
    /// This is "Option B" from the original brief. Option A - riding the game's own entity
    /// replication - is closed: of Gorilla Tag's 37 zones only three (customMaps,
    /// ghostReactor, ghostReactorDrill) have a GameAgentManager, so a GameAgent cannot be
    /// driven in any stock map. That was measured, not assumed.
    ///
    /// Model: the master client owns the simulation. It runs the brains and broadcasts
    /// transforms; everyone else creates a visual-only bot and interpolates toward what it is
    /// told. Nobody simulates independently, because two clients running the same brain over
    /// slightly different rig positions will diverge within seconds.
    ///
    /// Event codes 140-146 are unused by Gorilla Tag - the game's own traffic sits on
    /// 1,2,3,8,51,100-103,176,177,180,186,199,202 - and Photon reserves 200+. Clients without
    /// the mod receive an unknown code and discard it, which is the graceful degradation
    /// path.
    /// </summary>
    public class BotNetwork : MonoBehaviour, IOnEventCallback
    {
        public const byte EvSpawn = 140;
        public const byte EvState = 141;
        public const byte EvDespawn = 142;
        public const byte EvSettings = 143;
        public const byte EvCaught = 144;
        public const byte EvSyncRequest = 145;

        /// <summary>
        /// Wire format version. Bumped if the packet layout changes.
        /// 2: spawn carries the skin's name, so it no longer depends on both clients having
        ///    the same files in the same order.
        /// 3: a catch goes to the whole lobby and carries the bot, its image name, where it
        ///    was and how fast it was moving - the exact hit, for knockback and the death log.
        /// </summary>
        public const byte Protocol = 3;

        public Runtime.BotManager Bots;

        /// <summary>Transform broadcasts per second. Ten is plenty for interpolation.</summary>
        public float StateHz = 10f;

        /// <summary>How often the host checks whether its settings have changed.</summary>
        public float SettingsCheckInterval = 0.5f;

        private float _stateAccum;
        private bool _wasInRoom;
        private float _syncAt = -1f;
        private readonly MemoryStream _buffer = new MemoryStream(512);

        private int _masterActor = -1;
        private bool _wasMaster;

        private float _settingsCheckAt;
        private float[] _lastSentSettings;

        /// <summary>
        /// A guest's own settings, saved the first time the host's overwrite them and put
        /// back on leaving - otherwise visiting someone's lobby permanently changes yours.
        /// </summary>
        private float[] _localSettingsBackup;

        public bool IsAuthority => !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;

        /// <summary>
        /// True for a guest in someone else's room. The host's settings are the lobby's
        /// settings; a guest editing their own copy would only desync themselves, so the
        /// panel and console refuse instead.
        /// </summary>
        public static bool SettingsLocked => PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient;

        // ==================================================================== lifecycle

        private void OnEnable()
        {
            try { PhotonNetwork.AddCallbackTarget(this); }
            catch (Exception ex) { Plugin.Log.LogWarning("[Net] could not register callbacks: " + ex.Message); }
        }

        private void OnDisable()
        {
            try { PhotonNetwork.RemoveCallbackTarget(this); } catch { /* shutting down */ }
        }

        private void Update()
        {
            TrackRoomChanges();

            if (!PhotonNetwork.InRoom || Bots == null) return;
            if (!IsAuthority) return;

            SendSettingsIfChanged();

            var interval = 1f / Mathf.Max(1f, StateHz);
            _stateAccum += Time.deltaTime;
            if (_stateAccum < interval) return;
            _stateAccum = 0f;

            BroadcastState();
        }

        /// <summary>
        /// Ask the host what already exists when we walk into a room mid-session, otherwise
        /// a late joiner sees an empty map while everyone else is being chased.
        ///
        /// Delayed a moment: asking during the join handshake tends to be answered before we
        /// are ready to receive it.
        /// </summary>
        private void TrackRoomChanges()
        {
            var inRoom = PhotonNetwork.InRoom;

            if (inRoom && !_wasInRoom)
            {
                _syncAt = Time.time + 1.5f;
                _lastSentSettings = null;
                _masterActor = CurrentMasterActor();
                _wasMaster = PhotonNetwork.IsMasterClient;
            }
            else if (!inRoom && _wasInRoom)
            {
                // Left the room: the host's bots are not ours to keep.
                if (Bots != null) Bots.ClearRemote();
                _syncAt = -1f;
                _masterActor = -1;
                RestoreLocalSettings();
            }
            else if (inRoom)
            {
                var master = CurrentMasterActor();
                if (master != _masterActor && master != -1)
                {
                    OnHostChanged();
                    _masterActor = master;
                }
                _wasMaster = PhotonNetwork.IsMasterClient;
            }

            _wasInRoom = inRoom;

            if (_syncAt > 0f && Time.time >= _syncAt)
            {
                _syncAt = -1f;
                if (IsAuthority)
                {
                    // Walking into a room with bots already out (spawned offline, say) - tell
                    // anyone who got there first.
                    SendSettings();
                    if (Bots != null) Bots.ResendAll();
                }
                else RequestSync();
            }
        }

        private static int CurrentMasterActor()
        {
            var m = PhotonNetwork.MasterClient;
            return m != null ? m.ActorNumber : -1;
        }

        /// <summary>
        /// The master client changed - usually because the host left. Their bots went with
        /// them: the copies everyone else holds would otherwise stand frozen forever, since
        /// nobody is sending their positions any more.
        ///
        /// Not migrated to the new host on purpose. A guest's copy is a puppet with no brain
        /// and no agent, so "taking over" means spawning fresh ones anyway.
        /// </summary>
        private void OnHostChanged()
        {
            if (Bots == null) return;

            var dropped = Bots.ClearRemote();

            // If it was us who lost it, our bots can no longer be shown to anyone.
            if (_wasMaster && !PhotonNetwork.IsMasterClient)
                dropped += Bots.ClearAll();

            Plugin.Log.LogInfo("[Net] host changed to actor " + CurrentMasterActor() +
                               (PhotonNetwork.IsMasterClient ? " (us)" : "") +
                               "; dropped " + dropped + " bot(s).");

            if (PhotonNetwork.IsMasterClient)
            {
                // We are the lobby's settings now; keep what the old host had rather than
                // snapping back to ours mid-game, and forget the backup so leaving later does
                // not revert something we have since been in charge of.
                _localSettingsBackup = null;
                _lastSentSettings = null;
            }
        }

        private void RestoreLocalSettings()
        {
            if (_localSettingsBackup == null) return;
            NextBotSettings.Active.Deserialize(_localSettingsBackup);
            _localSettingsBackup = null;
            Plugin.Log.LogInfo("[Net] left the room; restored your own settings.");
        }

        // ==================================================================== sending

        private static RaiseEventOptions ToOthers =>
            new RaiseEventOptions { Receivers = ReceiverGroup.Others };

        private static readonly SendOptions Unreliable = new SendOptions { Reliability = false };
        private static readonly SendOptions Reliable = new SendOptions { Reliability = true };

        private static void Send(byte code, byte[] payload, bool reliable)
        {
            if (!PhotonNetwork.InRoom) return;
            try
            {
                PhotonNetwork.RaiseEvent(code, payload, ToOthers, reliable ? Reliable : Unreliable);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Net] send " + code + " failed: " + ex.Message);
            }
        }

        public void SendSpawn(int id, int skinIndex, string skinName, Vector3 pos, float yaw)
        {
            if (!PhotonNetwork.InRoom || !IsAuthority) return;

            // Clamp the name so a silly filename cannot bloat every spawn packet.
            skinName = skinName ?? "";
            if (skinName.Length > 64) skinName = skinName.Substring(0, 64);

            _buffer.SetLength(0);
            using (var w = new BinaryWriter(_buffer, System.Text.Encoding.UTF8, true))
            {
                w.Write(Protocol);
                w.Write(id);
                w.Write((byte)Mathf.Clamp(skinIndex, 0, 255));
                w.Write(skinName);
                w.Write(pos.x); w.Write(pos.y); w.Write(pos.z);
                w.Write(yaw);
            }
            Send(EvSpawn, _buffer.ToArray(), reliable: true);
        }

        public void SendDespawn(int id)
        {
            if (!PhotonNetwork.InRoom || !IsAuthority) return;

            _buffer.SetLength(0);
            using (var w = new BinaryWriter(_buffer, System.Text.Encoding.UTF8, true))
            {
                w.Write(Protocol);
                w.Write(id);
            }
            Send(EvDespawn, _buffer.ToArray(), reliable: true);
        }

        /// <summary>
        /// One packet for every bot rather than one per bot: the rate stays at StateHz no
        /// matter how many are alive, which keeps us well clear of the anti-cheat's RPC
        /// budget (MonkeAgent rate-limits at 50).
        /// </summary>
        private void BroadcastState()
        {
            var bots = Bots.Bots;
            if (bots.Count == 0) return;

            _buffer.SetLength(0);
            using (var w = new BinaryWriter(_buffer, System.Text.Encoding.UTF8, true))
            {
                w.Write(Protocol);
                w.Write((ushort)bots.Count);

                for (int i = 0; i < bots.Count; i++)
                {
                    var b = bots[i];
                    if (b == null) continue;

                    var p = b.transform.position;
                    w.Write(b.NetId);
                    w.Write(p.x); w.Write(p.y); w.Write(p.z);
                    w.Write(b.transform.eulerAngles.y);
                    w.Write((byte)(b.Brain != null ? (int)b.Brain.State : 0));
                }
            }

            // Unreliable on purpose: a dropped position packet is corrected 100ms later, and
            // reliable delivery would queue them up and produce rubber-banding instead.
            Send(EvState, _buffer.ToArray(), reliable: false);
        }

        /// <summary>
        /// A catch, to everyone. Reliable: a death log with holes in it, or a knockback that never
        /// arrives, is worse than a packet that turns up a moment late.
        /// </summary>
        public void SendCaught(Runtime.CatchInfo info)
        {
            if (!PhotonNetwork.InRoom || !IsAuthority || info == null) return;

            _buffer.SetLength(0);
            using (var w = new BinaryWriter(_buffer, System.Text.Encoding.UTF8, true))
            {
                w.Write(Protocol);
                w.Write(info.VictimActor);
                w.Write(info.BotNetId);
                w.Write(Clip(info.BotSkin, 64));
                w.Write(Clip(info.BotName, 64));
                w.Write(Clip(info.VictimName, 32));
                WriteVec(w, info.BotPosition);
                WriteVec(w, info.BotVelocity);
                WriteVec(w, info.VictimPosition);
                w.Write(info.Death);
                w.Write(info.Time);
            }
            Send(EvCaught, _buffer.ToArray(), reliable: true);
        }

        private static string Clip(string s, int max)
        {
            s = s ?? "";
            return s.Length > max ? s.Substring(0, max) : s;
        }

        private static void WriteVec(BinaryWriter w, Vector3 v)
        {
            w.Write(v.x); w.Write(v.y); w.Write(v.z);
        }

        private static Vector3 ReadVec(BinaryReader r)
        {
            var v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            // One NaN in a rigidbody velocity is a ragdoll that vanishes from the world.
            return float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
                   float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)
                ? Vector3.zero : v;
        }

        public void SendSettings()
        {
            if (!PhotonNetwork.InRoom || !IsAuthority) return;

            var values = NextBotSettings.Active.Serialize();
            _buffer.SetLength(0);
            using (var w = new BinaryWriter(_buffer, System.Text.Encoding.UTF8, true))
            {
                w.Write(Protocol);
                w.Write((ushort)values.Length);
                for (int i = 0; i < values.Length; i++) w.Write(values[i]);
            }
            Send(EvSettings, _buffer.ToArray(), reliable: true);
            _lastSentSettings = values;
        }

        /// <summary>
        /// Push a settings change to the lobby shortly after it happens.
        ///
        /// Polled rather than hooked into every setter: settings change from the wrist panel,
        /// the desktop console and hold-to-repeat, and a comparison catches all of them. The
        /// interval doubles as a debounce - holding "+" produces one packet per half second,
        /// not one per step.
        /// </summary>
        private void SendSettingsIfChanged()
        {
            if (Time.time < _settingsCheckAt) return;
            _settingsCheckAt = Time.time + Mathf.Max(0.1f, SettingsCheckInterval);

            // Nothing sent yet means the join handshake has not happened; it sends them.
            if (_lastSentSettings == null) return;

            var now = NextBotSettings.Active.Serialize();
            if (SameValues(now, _lastSentSettings)) return;

            SendSettings();
            Plugin.Log.LogInfo("[Net] settings changed; sent to the lobby.");
        }

        private static bool SameValues(float[] a, float[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // ==================================================================== receiving

        public void OnEvent(EventData e)
        {
            if (e.Code < EvSpawn || e.Code > EvSyncRequest) return;      // not ours

            var payload = e.CustomData as byte[];
            if (payload == null || payload.Length < 1) return;

            try
            {
                using (var r = new BinaryReader(new MemoryStream(payload)))
                {
                    var version = r.ReadByte();
                    if (version != Protocol) return;   // different mod build; ignore quietly

                    switch (e.Code)
                    {
                        case EvSpawn:     OnSpawn(r, e.Sender); break;
                        case EvState:     OnState(r, e.Sender); break;
                        case EvDespawn:   OnDespawn(r, e.Sender); break;
                        case EvSettings:  OnSettings(r, e.Sender); break;
                        case EvCaught:    OnCaught(r, e.Sender); break;
                        case EvSyncRequest: OnSyncRequest(e.Sender); break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Net] malformed event " + e.Code + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Only the master client may drive bots. Without this any client could spawn
        /// entities in everyone else's game.
        /// </summary>
        private static bool SenderIsAuthority(int sender)
        {
            var master = PhotonNetwork.MasterClient;
            return master != null && master.ActorNumber == sender;
        }

        private void OnSpawn(BinaryReader r, int sender)
        {
            if (!SenderIsAuthority(sender) || Bots == null) return;

            var id = r.ReadInt32();
            var skin = r.ReadByte();
            var skinName = r.ReadString();
            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var yaw = r.ReadSingle();

            Bots.SpawnRemote(id, skin, skinName, pos, yaw);
        }

        private void OnState(BinaryReader r, int sender)
        {
            if (!SenderIsAuthority(sender) || Bots == null) return;

            var count = r.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                var id = r.ReadInt32();
                var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var yaw = r.ReadSingle();
                var state = r.ReadByte();
                Bots.ApplyRemoteState(id, pos, yaw, state);
            }
        }

        private void OnDespawn(BinaryReader r, int sender)
        {
            if (!SenderIsAuthority(sender) || Bots == null) return;
            Bots.DespawnRemote(r.ReadInt32());
        }

        private void OnSettings(BinaryReader r, int sender)
        {
            if (!SenderIsAuthority(sender) || IsAuthority) return;

            var count = r.ReadUInt16();
            var values = new float[count];
            for (int i = 0; i < count; i++) values[i] = r.ReadSingle();

            if (_localSettingsBackup == null) _localSettingsBackup = NextBotSettings.Active.Serialize();
            NextBotSettings.Active.Deserialize(values);
            Plugin.Log.LogInfo("[Net] applied " + count + " synced settings from the host.");
        }

        private void OnCaught(BinaryReader r, int sender)
        {
            if (!SenderIsAuthority(sender)) return;

            var info = new Runtime.CatchInfo
            {
                VictimActor = r.ReadInt32(),
                BotNetId = r.ReadInt32(),
                BotSkin = r.ReadString(),
                BotName = r.ReadString(),
                VictimName = r.ReadString(),
                BotPosition = ReadVec(r),
                BotVelocity = Vector3.ClampMagnitude(ReadVec(r), 60f),
                VictimPosition = ReadVec(r),
                Death = r.ReadBoolean(),
                Time = r.ReadDouble()
            };

            // Our own copies where we have them: the bot object, the image name as *our* file
            // spells it, and the victim's name as *our* nametag shows it. The host's strings
            // are only the fallback.
            info.Bot = Bots != null ? Bots.FindByNetId(info.BotNetId) : null;
            var skin = Runtime.BotSkins.Find(info.BotSkin);
            if (skin != null && !string.IsNullOrEmpty(skin.DisplayName)) info.BotName = skin.DisplayName;
            string name;
            if (Runtime.PlayerNames.TryOf(info.VictimActor, out name)) info.VictimName = name;

            Runtime.CatchEffects.Dispatch(info);
        }

        /// <summary>A late joiner asking what already exists.</summary>
        private void OnSyncRequest(int sender)
        {
            if (!IsAuthority || Bots == null) return;
            Bots.ResendAll();
            SendSettings();
            Plugin.Log.LogInfo("[Net] re-sent world state for actor " + sender + ".");
        }

        /// <summary>Ask the host for everything, having just walked in on it.</summary>
        public void RequestSync()
        {
            if (!PhotonNetwork.InRoom || IsAuthority) return;

            _buffer.SetLength(0);
            using (var w = new BinaryWriter(_buffer, System.Text.Encoding.UTF8, true))
                w.Write(Protocol);

            Send(EvSyncRequest, _buffer.ToArray(), reliable: true);
        }
    }
}
