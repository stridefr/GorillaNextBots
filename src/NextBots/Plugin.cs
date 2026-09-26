using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using NextBots.Recon;
using NextBots.Runtime;
using NextBots.UI;
using UnityEngine;

namespace NextBots
{
    /// <summary>
    /// Entry point.
    ///
    /// Venue policy lives in <see cref="Venue"/>, not here: the mod runs in a private
    /// (code-locked) room where you are the master client, and never in a public lobby.
    /// Utilla is an optional extra - its modded-lobby routing broke with a game update, so
    /// nothing here depends on it working. Nothing here even refers to it by type: a reference
    /// compiled into this class made the whole mod fail to start on a PC without Utilla, so
    /// its one event is hooked by name, at run time, only when Utilla is actually there.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("org.legoandmars.gorillatag.utilla", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.stridefr.nextbots";
        public const string Name = "NextBots";
        public const string Version = "0.36.1";

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        public static ConfigEntry<bool> CfgEnableRecon;
        public static ConfigEntry<string> CfgMenuButton;
        public static ConfigEntry<KeyCode> CfgMenuKey;
        public static ConfigEntry<KeyCode> CfgReloadKey;
        public static ConfigEntry<bool> CfgLogInput;
        public static ConfigEntry<bool> CfgDesktopConsole;
        public static ConfigEntry<bool> CfgUseGesture;
        public static ConfigEntry<string> CfgFont;

        private GameObject _host;
        private BotManager _bots;
        private NextBotMenu _menu;
        private SpawnAimer _aimer;
        private NavMeshProvider _nav;
        private ModInput _input;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            GorillaNextBots.Shared.AssetBundle.Unpack(typeof(Plugin).Assembly,
                System.IO.Path.Combine(Paths.PluginPath, "NextBots"), m => Log.LogInfo(m));
            if (GorillaNextBots.Shared.AssetBundle.InstallDefaults(typeof(Plugin).Assembly, Paths.ConfigPath,
                    m => Log.LogInfo(m)) > 0)
                Config.Reload();
            NextBots.Config.NextBotSettings.Active.Load();

            CfgMenuButton = Config.Bind("Input", "MenuButton", "LeftSecondary",
                "Controller button that toggles the panel: LeftPrimary (X), LeftSecondary (Y), " +
                "RightPrimary (A), RightSecondary (B), AnyFace, or None.");
            CfgMenuKey = Config.Bind("Input", "MenuKey", KeyCode.F7,
                "Keyboard fallback for toggling the panel, handy when testing without a headset.");
            CfgReloadKey = Config.Bind("Input", "ReloadLayoutKey", KeyCode.F10,
                "Re-read panel.json and rebuild the panel, for the browser design tool loop.");
            CfgUseGesture = Config.Bind("Input", "UseGripTriggerGesture", false,
                "Also open the panel by holding grip + trigger. Off by default: grip is how " +
                "you climb in this game, so the gesture fires during ordinary play.");
            CfgLogInput = Config.Bind("Debug", "LogInput", false,
                "Log what the controllers are actually reporting, once per second and only on " +
                "change. Turn off once input is working.");
            CfgFont = Config.Bind("UI", "Font", "",
                "Font for the mod's text in the headset - the wrist panel and the kill cam screen. The name " +
                "of any font installed on this PC, e.g. Verdana, Impact or Coolvetica. Empty = the game's own " +
                "(Liberation Sans). Needs a restart. The death log has its own font, in killfeed.json.");
            CfgDesktopConsole = Config.Bind("Debug", "DesktopConsole", true,
                "On-monitor control panel and bot telemetry (F1). Lets the mod be driven and " +
                "debugged without a headset.");
            CfgEnableRecon = Config.Bind("Debug", "EnableRecon", false,
                "Write read-only diagnostic dumps (F8), and allow the NavMesh bake test (F9).");

            var built = System.IO.File.GetLastWriteTime(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            Log.LogInfo("================================================");
            Log.LogInfo(" " + Name + " v" + Version + "   built " + built.ToString("yyyy-MM-dd HH:mm:ss"));
            Log.LogInfo(" Unity " + Application.unityVersion);
            Log.LogInfo("================================================");
        }

        private void OnEnable()
        {
            // Utilla is optional, and "optional" has to mean more than "does not throw".
            // Observed on this install: Utilla loads fine but never raises GameInitialized,
            // so a mod that waits for that event silently never starts. Subscribe for the
            // case where it works, but ALWAYS run our own startup as well and let whichever
            // arrives first win - BuildRuntime is idempotent.
            StartCoroutine(SelfStartup());
            HookUtilla(true);
        }

        private void OnDisable()
        {
            HookUtilla(false);
            Teardown();
        }

        /// <summary>
        /// Wait for the local rig rather than a fixed delay: it is the thing everything else
        /// needs, and on a slow machine a fixed delay is a race. This does not depend on any
        /// other mod being alive.
        /// </summary>
        private System.Collections.IEnumerator SelfStartup()
        {
            var waited = 0f;
            while (GorillaTagger.Instance == null || GorillaTagger.Instance.offlineVRRig == null)
            {
                yield return new WaitForSeconds(0.5f);
                waited += 0.5f;

                // Say something rather than hanging silently forever, so a future failure
                // here is visible in the log instead of looking like the mod never loaded.
                if (waited % 15f < 0.01f)
                    Log.LogWarning("Still waiting for the local rig after " + waited + "s...");
            }

            yield return new WaitForSeconds(1f);

            if (_host == null) Log.LogInfo("Local rig found after " + waited + "s; starting up.");
            BuildRuntime();
        }

        private Delegate _utillaHook;

        /// <summary>Utilla's GameInitialized, by name, when Utilla is loaded. Never required.</summary>
        private void HookUtilla(bool on)
        {
            try
            {
                Type events = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "Utilla") continue;
                    events = asm.GetType("Utilla.Events");
                    break;
                }
                var ev = events != null ? events.GetEvent("GameInitialized") : null;
                if (ev == null) return;

                if (on)
                {
                    if (_utillaHook != null) return;
                    _utillaHook = Delegate.CreateDelegate(ev.EventHandlerType, this,
                        typeof(Plugin).GetMethod(nameof(OnGameInitialized),
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
                    ev.AddEventHandler(null, _utillaHook);
                }
                else if (_utillaHook != null)
                {
                    ev.RemoveEventHandler(null, _utillaHook);
                    _utillaHook = null;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("Utilla hook unavailable (" + ex.GetType().Name + ").");
            }
        }

        private void OnGameInitialized(object sender, EventArgs e)
        {
            Log.LogInfo("Utilla GameInitialized fired.");
            BuildRuntime();
        }

        private void BuildRuntime()
        {
            if (_host != null) return;

            _host = new GameObject("NextBotsRuntime");
            DontDestroyOnLoad(_host);

            _nav = _host.AddComponent<NavMeshProvider>();
            _nav.Links = _host.AddComponent<NavLinkBuilder>();

            _bots = _host.AddComponent<BotManager>();
            _bots.Nav = _nav;

            var net = _host.AddComponent<Net.BotNetwork>();
            net.Bots = _bots;
            _bots.Net = net;

            _aimer = _host.AddComponent<SpawnAimer>();

            // The panel lives on its own object because it is positioned onto the wrist
            // every frame; keeping it off the runtime root avoids dragging everything else
            // around with it.
            var menuGo = new GameObject("NextBotsMenu");
            menuGo.transform.SetParent(_host.transform, false);
            _menu = menuGo.AddComponent<NextBotMenu>();
            _menu.Director = _bots;
            _menu.Aimer = _aimer;
            _menu.Layout = PanelLayout.Load();
            _menu.AnchorToLeftHand = _menu.Layout.anchorLeftHand;

            // Aim with whichever hand is not wearing the panel.
            _aimer.UseRightHand = _menu.AnchorToLeftHand;

            _input = _host.AddComponent<ModInput>();
            _input.Menu = _menu;

            // The death log: every catch in the lobby, in the headset and on the monitor.
            _host.AddComponent<KillFeed>();
            var lives = _host.AddComponent<Lives>();
            lives.Net = net;
            _host.AddComponent<LivesDisplay>();
            _host.AddComponent<LivesNotice>();

            if (CfgDesktopConsole.Value)
            {
                var console = _host.AddComponent<Debugging.DesktopConsole>();
                console.Bots = _bots;
                console.Nav = _nav;
            }

            if (CfgEnableRecon.Value) _host.AddComponent<ReconDump>();

            Log.LogInfo("Runtime built. Venue: " + Venue.Describe());
            Log.LogInfo("Press " + CfgMenuButton.Value + " (or " + CfgMenuKey.Value + " on the keyboard) to open the panel.");
        }

        private void Teardown()
        {
            if (_host == null) return;
            Destroy(_host);
            _host = null;
        }
    }
}
