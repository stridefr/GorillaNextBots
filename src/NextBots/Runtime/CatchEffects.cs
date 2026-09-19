using System;
using Photon.Pun;
using UnityEngine;

namespace NextBots.Runtime
{
    /// <summary>
    /// Everything about one catch: who, by what, from where and how fast. Built once by the
    /// host, sent to every client, and handed to anything listening on
    /// <see cref="CatchEffects.Caught"/>.
    /// </summary>
    public sealed class CatchInfo
    {
        public int VictimActor = -1;

        /// <summary>The name on the victim's nametag, as this client sees it.</summary>
        public string VictimName = "";

        public int BotNetId = -1;

        /// <summary>The bot's skin key (upper case) - how every client finds its own copy of the image.</summary>
        public string BotSkin = "";

        /// <summary>The bot's image file name without extension, case kept - what the death log shows.</summary>
        public string BotName = "";

        public Vector3 BotPosition;
        public Vector3 BotVelocity;
        public Vector3 VictimPosition;

        /// <summary>DEATH ON CATCH, as the host had it. The host's setting is the lobby's.</summary>
        public bool Death;

        /// <summary>When it happened: <c>PhotonNetwork.Time</c> in a room, <c>Time.time</c> offline.</summary>
        public double Time;

        /// <summary>This client's copy of the bot, if it has one. May be null on a guest.</summary>
        public NextBot Bot;

        /// <summary>True on the caught player's own client.</summary>
        public bool VictimIsLocal;
    }

    /// <summary>
    /// What happens when a bot reaches someone.
    ///
    /// <para><b>Every client hears about every catch.</b> The host builds a
    /// <see cref="CatchInfo"/> and sends it to the whole lobby, not just the caught player,
    /// and each client raises <see cref="Caught"/> with it. That is what lets a death log show
    /// everyone's deaths and lets other players hear you get hit - and because the message
    /// carries the bot and its velocity, a guest knows exactly what hit them instead of
    /// guessing.</para>
    ///
    /// <para><b>The effect on the victim is replaceable.</b> By default the caught player gets
    /// haptics and, with DEATH ON CATCH on, is sent back to spawn. Another mod can take that
    /// over by setting <see cref="LocalCatchHandler"/> - the ragdoll bridge does, to knock you
    /// down instead. Only the victim's own client runs it: GT owns each player's locomotion
    /// locally, so nobody else could move them anyway.</para>
    /// </summary>
    public static class CatchEffects
    {
        /// <summary>Where "back to spawn" is. Captured on room join.</summary>
        public static Vector3? SpawnPoint;

        /// <summary>Raised on every client for every catch, the victim's included.</summary>
        public static event Action<CatchInfo> Caught;

        /// <summary>
        /// Runs on the victim's client instead of the default effect. Return true to say it was
        /// handled; false (or throwing) falls back to the default, so a catch always does
        /// something.
        /// </summary>
        public static Func<CatchInfo, bool> LocalCatchHandler;

        public static void RememberSpawn(Vector3 position)
        {
            SpawnPoint = position;
            Plugin.Log.LogInfo("[Catch] spawn point recorded at " + position);
        }

        /// <summary>The host's description of a catch it just detected.</summary>
        public static CatchInfo Describe(NextBot bot, int actorNumber)
        {
            var info = new CatchInfo
            {
                VictimActor = actorNumber,
                VictimName = PlayerNames.Of(actorNumber),
                Death = Config.NextBotSettings.Active.DeathEnabled,
                Time = Now(),
                Bot = bot
            };

            if (bot != null)
            {
                info.BotNetId = bot.NetId;
                info.BotSkin = bot.SkinName ?? "";
                info.BotName = bot.DisplayName ?? bot.SkinName ?? "";
                info.BotPosition = bot.Center;
                info.BotVelocity = bot.MeasuredVelocity.sqrMagnitude > 0.01f ? bot.MeasuredVelocity : bot.Velocity;
            }

            Vector3 victim;
            info.VictimPosition = PlayerNames.TryGetPosition(actorNumber, out victim) ? victim : info.BotPosition;
            return info;
        }

        /// <summary>
        /// Announces a catch on this client and, if the victim is us, runs the effect. Called by
        /// the host for its own catches and by the network code for everyone else's.
        /// </summary>
        public static void Dispatch(CatchInfo info)
        {
            if (info == null) return;
            info.VictimIsLocal = IsLocalActor(info.VictimActor);

            // One listener throwing must not stop the rest, or the victim's own effect.
            var listeners = Caught;
            if (listeners != null)
            {
                foreach (var d in listeners.GetInvocationList())
                {
                    try { ((Action<CatchInfo>)d)(info); }
                    catch (Exception ex) { Plugin.Log.LogError("[Catch] a catch listener threw: " + ex); }
                }
            }

            if (!info.VictimIsLocal)
            {
                Plugin.Log.LogInfo("[Catch] '" + info.BotName + "' caught " + info.VictimName +
                                   " (actor " + info.VictimActor + ")");
                return;
            }

            Plugin.Log.LogInfo("[Catch] caught by '" + info.BotName + "'" + (info.Death ? "" : " (death disabled)"));

            var handler = LocalCatchHandler;
            if (handler != null)
            {
                try { if (handler(info)) return; }
                catch (Exception ex) { Plugin.Log.LogError("[Catch] catch handler threw, using the default: " + ex); }
            }

            Haptics();
            if (info.Death) SendToSpawn();
        }

        public static double Now()
        {
            try { if (PhotonNetwork.InRoom) return PhotonNetwork.Time; }
            catch { /* offline */ }
            return UnityEngine.Time.timeAsDouble;
        }

        public static void SendToSpawn()
        {
            var player = GorillaLocomotion.GTPlayer.Instance;
            if (player == null) return;

            var target = SpawnPoint;
            if (!target.HasValue)
            {
                Plugin.Log.LogWarning("[Catch] no spawn point recorded; ignoring.");
                return;
            }

            // Validate the destination before moving anyone to it.
            //
            // The recorded spawn point is wherever the player happened to be standing when
            // they joined, which is not guaranteed to be solid ground - and teleporting onto
            // a bad point drops you through the world. Repeated catches then chain it, which
            // is what was crashing the game.
            var safe = MakeSafe(target.Value);
            if (!safe.HasValue)
            {
                Plugin.Log.LogWarning("[Catch] no safe respawn point found; not moving the player.");
                return;
            }

            try
            {
                player.TeleportTo(safe.Value, player.transform.rotation, keepVelocity: false, center: false);
                Plugin.Log.LogInfo("[Catch] respawned at " + safe.Value);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[Catch] teleport failed: " + ex);
            }
        }

        /// <summary>
        /// Turn a candidate point into somewhere it is safe to stand: on the navmesh if we
        /// have one, otherwise on whatever solid ground is under it, lifted clear of the
        /// floor. Returns null if we cannot establish either.
        /// </summary>
        private static Vector3? MakeSafe(Vector3 candidate)
        {
            UnityEngine.AI.NavMeshHit navHit;
            if (UnityEngine.AI.NavMesh.SamplePosition(candidate, out navHit, 8f,
                    UnityEngine.AI.NavMesh.AllAreas))
                return navHit.position + Vector3.up * 0.5f;

            // No navmesh - drop a ray and stand on whatever it finds.
            RaycastHit hit;
            if (Physics.Raycast(candidate + Vector3.up * 3f, Vector3.down, out hit, 30f,
                    NavMeshProvider.BakeMask, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * 0.5f;

            return null;
        }

        private static void Haptics()
        {
            var tagger = GorillaTagger.Instance;
            if (tagger == null) return;
            try
            {
                tagger.StartVibration(true, 1f, 0.25f);
                tagger.StartVibration(false, 1f, 0.25f);
            }
            catch { /* a nicety */ }
        }

        private static bool IsLocalActor(int actorNumber)
        {
            try
            {
                var net = NetworkSystem.Instance;
                if (net == null || net.LocalPlayer == null) return true;   // offline: it is us
                return net.LocalPlayer.ActorNumber == actorNumber;
            }
            catch { return false; }
        }
    }
}
