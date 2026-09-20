using BepInEx;
using BepInEx.Logging;
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
    /// <para>It plugs into NextBots through <c>CatchEffects</c>: <c>Caught</c> tells it about
    /// every catch in the lobby, and <c>LocalCatchHandler</c> lets it replace what happens to
    /// the victim. On the ragdoll side it uses <c>RagdollController.Collapse</c> and the
    /// puppet's rigidbodies, all public.</para>
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInProcess("Gorilla Tag.exe")]
    [BepInDependency(NextBots.Plugin.Guid)]
    [BepInDependency(GorillaRagdoll.Plugin.Guid)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.stridefr.nextbotsragdoll";
        public const string Name = "NextBotsRagdoll";
        public const string Version = "0.7.0";

        public static ManualLogSource Log { get; private set; }

        private GameObject _host;

        private void Awake()
        {
            Log = Logger;
            BridgeConfig.Bind(Config);

            _host = new GameObject("NextBotsRagdoll.Host");
            DontDestroyOnLoad(_host);
            _host.AddComponent<ImpactSounds>();
            _host.AddComponent<KnockdownBridge>();
            _host.AddComponent<Jumpscare>();
            _host.AddComponent<DeathVignette>();
            _host.AddComponent<Dust>();
            _host.AddComponent<HardLanding>();

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
            if (_host != null) Destroy(_host);
        }
    }
}
