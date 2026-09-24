using System.Collections;
using BepInEx;
using BepInEx.Logging;
using GorillaLocomotion;
using GorillaRagdoll.Config;
using GorillaRagdoll.Runtime;
using GorillaRagdoll.UI;
using UnityEngine;

namespace GorillaRagdoll
{
    /// <summary>
    /// BepInEx entry point.
    ///
    /// <para>Starts itself rather than waiting on Utilla. NextBots found on this exact install
    /// that Utilla loads but never raises <c>GameInitialized</c>, so anything gated on that
    /// event silently never runs. This mod has no need for Utilla at all, so it simply polls
    /// for the local player to exist and then builds its host object.</para>
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInProcess("Gorilla Tag.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.stridefr.gorillaragdoll";
        public const string Name = "GorillaRagdoll";
        public const string Version = "0.6.1";

        public static ManualLogSource Log { get; private set; }
        public static RagdollController Controller { get; private set; }

        private GameObject _host;

        private void Awake()
        {
            Log = Logger;
            RagdollConfig.Bind(base.Config);
            // BepInEx loads plugin DLLs once, at process start. Copying a new build over the
            // top of a running game changes nothing until it is restarted - so stamp the build
            // time into the log, and "am I even testing the new code" stops being a guess.
            string built = "unknown";
            try
            {
                var path = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(path))
                    built = System.IO.File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch { /* not worth failing startup over */ }

            Log.LogInfo(Name + " " + Version + " | BUILD " + built + " | toggle=" + RagdollConfig.ToggleKey.Value +
                        " menu=" + RagdollConfig.MenuKey.Value + " panic=" + RagdollConfig.PanicKey.Value);
            StartCoroutine(Boot());
        }

        private IEnumerator Boot()
        {
            // The rig and the locomotion player appear at different times during load, and
            // touching either early gets you a null reference in someone else's Awake.
            while (GorillaTagger.Instance == null ||
                   GorillaTagger.Instance.offlineVRRig == null ||
                   GTPlayer.Instance == null)
            {
                yield return new WaitForSeconds(0.5f);
            }

            // Let the avatar finish assembling: cosmetics and the body renderer swap in after
            // the rig object itself exists, and cloning mid-swap gives a half-dressed ragdoll.
            yield return new WaitForSeconds(2f);

            _host = new GameObject("GorillaRagdoll.Host");
            DontDestroyOnLoad(_host);

            Controller = _host.AddComponent<RagdollController>();
            var menu = _host.AddComponent<RagdollMenu>();
            menu.Controller = Controller;

            var input = _host.AddComponent<RagdollInput>();
            input.Controller = Controller;
            input.Menu = menu;

            // Multiplayer: share our ragdoll with the room, draw everyone else's. The IK hook is
            // what lets a remote ragdoll win against the game's own network pose every frame.
            Net.IkHook.Install(Guid);
            var net = _host.AddComponent<Net.RagdollNet>();
            net.Controller = Controller;
            Controller.Network = net;

            Log.LogInfo("[Boot] ready | " + Venue.Describe() +
                        " | ragdoll " + (Venue.Allowed ? "allowed" : "blocked: " + Venue.BlockedReason));

            // The known-good state, before anything here has touched the avatar. Every later
            // probe is reported as a diff against this, so "what did the mod fail to put back"
            // is a question the log answers directly instead of one we reason about.
            RigProbe.Capture("boot", full: true, baseline: false);
        }

        private void OnDestroy()
        {
            if (Controller != null) Controller.Panic();
            if (_host != null) Destroy(_host);
            Net.IkHook.Uninstall();
        }
    }
}
