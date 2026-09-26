using System.Collections.Generic;
using System.IO;
using NextBots.Config;
using Photon.Pun;
using UnityEngine;

namespace NextBots.Runtime
{
    /// <summary>
    /// Lives: every real bot catch costs the player one, and a player with none left can be made
    /// a ghost the bots ignore.
    ///
    /// <para><b>The host keeps the count.</b> It is the one that decides catches, so it is the only
    /// one that takes a life, and it sends the whole table to the lobby whenever it changes (and
    /// every few seconds, so a dropped packet or a late joiner catches up). Everyone else only
    /// shows what the host says. Offline, this client is the host.</para>
    ///
    /// <para><b>Only entries that differ from full are kept.</b> A player missing from the table has
    /// every life, which is also what a new round is: the table emptied. So nobody has to be
    /// registered on joining, and a late joiner simply starts full.</para>
    ///
    /// <para><b>A new round starts</b> when the host switches lives on, or changes the life count
    /// while they are on.</para>
    /// </summary>
    public sealed class Lives : MonoBehaviour
    {
        /// <summary>Two bots reaching someone at once is one death, not two.</summary>
        private const float LossGap = 2.5f;

        /// <summary>How often the host repeats the table while lives are on.</summary>
        private const float Resend = 3f;

        private static readonly Dictionary<int, int> Left = new Dictionary<int, int>();
        private static readonly Dictionary<int, float> LostAt = new Dictionary<int, float>();

        /// <summary>Whether lives are in play, as the host last said (or, as the host, as set).</summary>
        public static bool Active { get; private set; }

        /// <summary>Lives each player starts a round with.</summary>
        public static int Max { get; private set; } = 3;

        /// <summary>Whether a player with no lives left is ignored by the bots.</summary>
        public static bool GhostsOn { get; private set; } = true;

        public Net.BotNetwork Net;

        private bool _lastEnabled;
        private int _lastCount = -1;
        private float _resendAt;
        private bool _dirty;
        private bool _wasHost;

        /// <summary>Lives this player has left.</summary>
        public static int Of(int actor)
        {
            int n;
            return Left.TryGetValue(actor, out n) ? n : Max;
        }

        /// <summary>No lives left, lives are on, and ghosts are on: the bots leave them alone.</summary>
        public static bool IsGhost(int actor) => Active && GhostsOn && Of(actor) <= 0;

        private static Lives _instance;

        private void Awake() => _instance = this;

        /// <summary>
        /// A bot caught this player for real. Only the host's call counts; everyone else hears
        /// the result.
        /// </summary>
        public static void OnKilled(int actor)
        {
            if (_instance == null || !Active || !IsHost) return;

            float last;
            if (LostAt.TryGetValue(actor, out last) && Time.time - last < LossGap) return;
            LostAt[actor] = Time.time;

            int now = Mathf.Max(0, Of(actor) - 1);
            if (now == Of(actor)) return;
            Left[actor] = now;
            _instance._dirty = true;

            Plugin.Log.LogInfo("[Lives] " + PlayerNames.Of(actor) + " lost a life: " + now + "/" + Max +
                               (now == 0 ? (GhostsOn ? " - out, now a ghost" : " - out") : ""));
        }

        private static bool IsHost => !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient;

        private void Update()
        {
            // Taking over as host mid-game carries on with the count the old host sent, rather than
            // reading it as lives having just been switched on and starting everyone over.
            bool host = IsHost;
            if (host && !_wasHost && PhotonNetwork.InRoom)
            {
                _lastEnabled = Active;
                _lastCount = Max;
            }
            _wasHost = host;
            if (!host) return;

            var cfg = NextBotSettings.Active;
            int count = Mathf.Clamp(cfg.LifeCount, 1, 10);

            // A new round: lives switched on, or the count changed while they were on.
            if (cfg.LivesEnabled && (!_lastEnabled || count != _lastCount))
            {
                Left.Clear();
                LostAt.Clear();
                Plugin.Log.LogInfo("[Lives] new round: everyone has " + count + (count == 1 ? " life" : " lives") +
                                   (cfg.Ghosts ? ", ghosts on" : ""));
                _dirty = true;
            }
            if (cfg.LivesEnabled != _lastEnabled || cfg.Ghosts != GhostsOn) _dirty = true;

            _lastEnabled = cfg.LivesEnabled;
            _lastCount = count;
            Active = cfg.LivesEnabled;
            Max = count;
            GhostsOn = cfg.Ghosts;

            if (!PhotonNetwork.InRoom || Net == null) { _dirty = false; return; }
            if (_dirty || (Active && Time.time >= _resendAt))
            {
                _dirty = false;
                _resendAt = Time.time + Resend;
                Net.SendLives(WriteTo);
            }
        }

        /// <summary>The whole table, for the wire.</summary>
        public static void WriteTo(BinaryWriter w)
        {
            w.Write(Active);
            w.Write((byte)Max);
            w.Write(GhostsOn);
            w.Write((ushort)Left.Count);
            foreach (var kv in Left)
            {
                w.Write(kv.Key);
                w.Write((byte)Mathf.Clamp(kv.Value, 0, 255));
            }
        }

        /// <summary>The host's table, replacing ours.</summary>
        public static void ReadFrom(BinaryReader r)
        {
            bool active = r.ReadBoolean();
            int max = Mathf.Clamp(r.ReadByte(), 1, 10);
            bool ghosts = r.ReadBoolean();
            int n = r.ReadUInt16();
            if (n > 256) return;

            var incoming = new Dictionary<int, int>(n);
            for (int i = 0; i < n; i++)
            {
                int actor = r.ReadInt32();
                incoming[actor] = Mathf.Clamp(r.ReadByte(), 0, max);
            }

            Active = active;
            Max = max;
            GhostsOn = ghosts;
            Left.Clear();
            foreach (var kv in incoming) Left[kv.Key] = kv.Value;
        }

        /// <summary>Left the room: the host's count no longer applies. Our own settings take over.</summary>
        public static void Forget()
        {
            Left.Clear();
            LostAt.Clear();
            if (_instance != null) _instance._lastEnabled = false;
            Active = false;
        }

        /// <summary>Send the table now, for someone who has just asked for everything.</summary>
        public static void ResendSoon()
        {
            if (_instance != null) _instance._dirty = true;
        }
    }
}
