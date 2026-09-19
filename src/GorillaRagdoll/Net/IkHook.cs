using System;
using HarmonyLib;

namespace GorillaRagdoll.Net
{
    /// <summary>
    /// Runs remote ragdoll posing at the one moment it can win: immediately before
    /// <c>GorillaIKMgr</c> reads its targets.
    ///
    /// <para><b>Why a patch rather than an ordinary LateUpdate.</b> A remote avatar is written
    /// to twice a frame by the game. <c>VRRigJobManager.Update</c> calls
    /// <c>RemoteRigUpdate</c> on every remote rig, whose <c>MapOther</c> lerps the head and hand
    /// IK targets towards the network values; then <c>GorillaIKMgr.LateUpdate</c> reads those
    /// targets and poses the bones. A mod LateUpdate at the default order runs before or after
    /// the IK at Unity's whim. After, and the IK has already consumed the network pose - the
    /// ragdoll flickers against the real one. So the pose is written from a prefix on the IK's
    /// own LateUpdate, which is after the network write and before the read, every frame, by
    /// construction.</para>
    ///
    /// <para>The prefix is <c>void</c>, so the original always runs, and it cannot throw: an
    /// exception here would stop the IK for every avatar in the room.</para>
    /// </summary>
    internal static class IkHook
    {
        private static Harmony _harmony;

        public static void Install(string id)
        {
            try
            {
                var target = AccessTools.Method(typeof(GorillaIKMgr), "LateUpdate");
                if (target == null)
                {
                    Plugin.Log.LogWarning("[Net] GorillaIKMgr.LateUpdate not found - remote ragdolls will " +
                                          "be posed from LateUpdate instead, which can flicker");
                    return;
                }

                _harmony = new Harmony(id);
                _harmony.Patch(target, prefix: new HarmonyMethod(typeof(IkHook), nameof(Prefix)));
                RagdollNet.HookInstalled = true;
                Plugin.Log.LogInfo("[Net] remote ragdolls posed from a GorillaIKMgr.LateUpdate prefix");
            }
            catch (Exception ex)
            {
                RagdollNet.HookInstalled = false;
                Plugin.Log.LogWarning("[Net] could not patch GorillaIKMgr (" + ex.Message +
                                      ") - remote ragdolls will be posed from LateUpdate instead");
            }
        }

        public static void Uninstall()
        {
            try { _harmony?.UnpatchSelf(); } catch { /* shutting down */ }
            _harmony = null;
            RagdollNet.HookInstalled = false;
        }

        private static void Prefix()
        {
            try { RagdollNet.ApplyRemotes(); }
            catch { /* never let this reach the IK */ }
        }
    }
}
