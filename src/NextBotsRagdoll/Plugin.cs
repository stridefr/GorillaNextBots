using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NextBotsRagdoll
{
    /// <summary>
    /// Glue between NextBots and GorillaRagdoll: a catch knocks you down instead of (or before)
    /// sending you to spawn, and throws the body the way the bot was running.
    ///
    /// <para>A third plugin rather than an edit to either mod, for the same reason those two are
    /// already separate: each can load, break or be removed without taking the other with it.
    /// Delete this DLL and NextBots goes back to its own catch behaviour, unchanged.</para>
    ///
    /// <para>Nothing in either mod was modified to make this work. The seams it uses were all
    /// already public: <c>CatchEffects.Apply</c> (patched), <c>RagdollController.Collapse</c>,
    /// the puppet's rigidbodies, and <c>WorldView.Excluded</c>.</para>
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInProcess("Gorilla Tag.exe")]
    [BepInDependency(NextBots.Plugin.Guid)]
    [BepInDependency(GorillaRagdoll.Plugin.Guid)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.stridefr.nextbotsragdoll";
        public const string Name = "NextBotsRagdoll";
        public const string Version = "0.2.0";

        public static ManualLogSource Log { get; private set; }

        private Harmony _harmony;
        private GameObject _host;

        private void Awake()
        {
            Log = Logger;
            BridgeConfig.Bind(Config);

            try
            {
                var target = AccessTools.Method(typeof(NextBots.Runtime.CatchEffects),
                                                nameof(NextBots.Runtime.CatchEffects.Apply));
                if (target == null)
                {
                    Log.LogError("[Bridge] CatchEffects.Apply not found - NextBots changed shape. " +
                                 "Catches will behave as plain NextBots.");
                    return;
                }

                _harmony = new Harmony(Guid);
                _harmony.Patch(target, prefix: new HarmonyMethod(typeof(CatchPatch), nameof(CatchPatch.Prefix)));
            }
            catch (Exception ex)
            {
                Log.LogError("[Bridge] could not patch CatchEffects.Apply: " + ex);
                return;
            }

            _host = new GameObject("NextBotsRagdoll.Host");
            DontDestroyOnLoad(_host);
            _host.AddComponent<ImpactSounds>();
            _host.AddComponent<KnockdownBridge>();

            // Its own object: it has a camera, a model and a screen hanging off it, placed in
            // world space every frame, and none of that belongs under the host.
            var cam = new GameObject("NextBotsRagdoll.KillCam");
            cam.transform.SetParent(_host.transform, false);
            cam.AddComponent<KillCam>();

            Log.LogInfo(Name + " " + Version + " | catches now ragdoll you (" +
                        (BridgeConfig.Enabled.Value ? "enabled" : "DISABLED in config") + ")");
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { /* shutting down */ }
            if (_host != null) Destroy(_host);
        }
    }

    /// <summary>
    /// Runs in front of <c>CatchEffects.Apply</c> on every client, which is the one place both
    /// kinds of catch arrive: the host calls it directly from the bot, and a guest calls it
    /// from the network message with <c>bot == null</c>.
    /// </summary>
    internal static class CatchPatch
    {
        // Parameter names must match the original's for Harmony to bind them.
        public static bool Prefix(int actorNumber, NextBots.Runtime.NextBot bot)
        {
            try
            {
                var bridge = KnockdownBridge.Instance;
                if (bridge == null) return true;

                // True means the bridge took the catch, so the original's respawn teleport must
                // not also run - it would yank you out of your own ragdoll mid-flight.
                return !bridge.OnCatch(actorNumber, bot);
            }
            catch (Exception ex)
            {
                // Whatever went wrong, a catch still has to do something: fall back to NextBots.
                Plugin.Log.LogError("[Bridge] catch handler threw, using NextBots' own: " + ex);
                return true;
            }
        }
    }
}
