using UnityEngine;

namespace NextBots.Runtime
{
    /// <summary>
    /// What happens when a bot reaches someone. Deliberately a single swappable hook, so
    /// ragdoll / sound / a jumpscare can replace "send them back to spawn" without touching
    /// the brain.
    ///
    /// Only ever applies to the *local* player. Teleporting someone else's rig from our
    /// client would not move them - GT owns each player's locomotion locally - so once bots
    /// are networked, catching a remote player becomes a message telling their client to run
    /// this same hook on themselves.
    /// </summary>
    public static class CatchEffects
    {
        /// <summary>Where "back to spawn" is. Captured on room join.</summary>
        public static Vector3? SpawnPoint;

        public static void RememberSpawn(Vector3 position)
        {
            SpawnPoint = position;
            Plugin.Log.LogInfo("[Catch] spawn point recorded at " + position);
        }

        public static void Apply(int actorNumber, NextBot bot)
        {
            if (!IsLocalActor(actorNumber))
            {
                // Remote catch. Nothing to do locally yet; this is where the network message
                // will go once the transport exists.
                Plugin.Log.LogInfo("[Catch] bot '" + (bot != null ? bot.SkinName : "?") +
                                   "' caught remote actor " + actorNumber + " (no transport yet)");
                return;
            }

            Plugin.Log.LogInfo("[Catch] caught by '" + (bot != null ? bot.SkinName : "?") + "'" +
                               (Config.NextBotSettings.Active.DeathEnabled ? "" : " (death disabled)"));

            Haptics();
            if (Config.NextBotSettings.Active.DeathEnabled) SendToSpawn();
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
            catch (System.Exception ex)
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
