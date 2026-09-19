using BepInEx.Configuration;
using UnityEngine;

namespace NextBotsRagdoll
{
    /// <summary>
    /// Every knob, persisted to <c>BepInEx/config/com.stridefr.nextbotsragdoll.cfg</c>.
    ///
    /// Same trap as the other two mods: BepInEx prefers the file over the code default, so
    /// editing a default here does nothing once the .cfg exists. Delete it to re-default.
    ///
    /// Whether a catch ends at spawn is deliberately <i>not</i> a setting here. NextBots' own
    /// <c>DEATH ON CATCH</c> toggle already means exactly that, is already on the wrist panel, and
    /// is already synced from the host - so it decides: on, you ragdoll and then respawn; off, you
    /// ragdoll and get up where you land.
    /// </summary>
    /// <summary>When the Garry's Mod death sound plays for a catch.</summary>
    public enum DeathSoundMode
    {
        /// <summary>Every catch - the death log treats every catch as a kill, so does this.</summary>
        EveryCatch,

        /// <summary>Only when DEATH ON CATCH is on and the catch really sends you to spawn.</summary>
        KilledOnly,

        Off
    }

    public static class BridgeConfig
    {
        public static ConfigEntry<bool> Enabled;

        public static ConfigEntry<float> BaseSpeed;
        public static ConfigEntry<float> SpeedTransfer;
        public static ConfigEntry<float> Lift;
        public static ConfigEntry<float> TravelBias;
        public static ConfigEntry<float> Tumble;
        public static ConfigEntry<float> MaxLaunch;

        public static ConfigEntry<float> DownSeconds;
        public static ConfigEntry<float> GetUpGrace;
        public static ConfigEntry<bool> DownedPlayersAreSafe;

        public static ConfigEntry<bool> KillCamEnabled;
        public static ConfigEntry<bool> KillCamModel;
        public static ConfigEntry<bool> KillCamHeadsetScreen;
        public static ConfigEntry<bool> KillCamMonitorPip;
        public static ConfigEntry<float> KillCamFocusDelay;
        public static ConfigEntry<float> KillCamFov;
        public static ConfigEntry<float> KillCamZoomFov;
        public static ConfigEntry<int> KillCamResolution;

        public static ConfigEntry<bool> SoundsEnabled;
        public static ConfigEntry<float> SoundVolume;
        public static ConfigEntry<float> SoftSpeed;
        public static ConfigEntry<float> HardSpeed;
        public static ConfigEntry<float> BreakSpeed;
        public static ConfigEntry<bool> ShareSounds;
        public static ConfigEntry<bool> HearOthers;
        public static ConfigEntry<DeathSoundMode> DeathSound;
        public static ConfigEntry<bool> ImportFromSourceGames;

        public static ConfigEntry<KeyCode> TestCatchKey;
        public static ConfigEntry<float> TestBotSpeed;

        public static void Bind(ConfigFile cfg)
        {
            const string G = "1. General";
            Enabled = cfg.Bind(G, "Enabled", true,
                "Ragdoll the player when a nextbot catches them. Off = NextBots' own catch behaviour.");

            const string K = "2. Knockback";
            BaseSpeed = cfg.Bind(K, "BaseSpeed", 5f,
                new ConfigDescription("Launch speed from any catch, even by a bot standing still, m/s.",
                    new AcceptableValueRange<float>(0f, 30f)));
            SpeedTransfer = cfg.Bind(K, "SpeedTransfer", 1f,
                new ConfigDescription("How much of the bot's own speed is added to the launch. 1 = all of it.",
                    new AcceptableValueRange<float>(0f, 4f)));
            Lift = cfg.Bind(K, "Lift", 3.5f,
                new ConfigDescription("Upward speed added so the body leaves the floor instead of skidding, m/s.",
                    new AcceptableValueRange<float>(0f, 20f)));
            TravelBias = cfg.Bind(K, "TravelBias", 0.6f,
                new ConfigDescription("0 = thrown straight away from the bot. 1 = thrown the way the bot was " +
                                      "running, like being hit by a car. In between blends the two.",
                    new AcceptableValueRange<float>(0f, 1f)));
            Tumble = cfg.Bind(K, "Tumble", 6f,
                new ConfigDescription("Spin that tips the torso over in the direction of the hit, rad/s.",
                    new AcceptableValueRange<float>(0f, 20f)));
            MaxLaunch = cfg.Bind(K, "MaxLaunch", 30f,
                new ConfigDescription("Hard cap on the launch speed, m/s. Stops a striking bot firing you into orbit.",
                    new AcceptableValueRange<float>(1f, 60f)));

            const string D = "3. Knockdown";
            DownSeconds = cfg.Bind(D, "DownSeconds", 4f,
                new ConfigDescription("How long you stay down before getting up automatically. " +
                                      "0 = stay down until you get up yourself.",
                    new AcceptableValueRange<float>(0f, 60f)));
            GetUpGrace = cfg.Bind(D, "GetUpGrace", 2f,
                new ConfigDescription("Seconds after getting up during which bots ignore you, so one standing " +
                                      "over your body cannot catch you again the instant you stand.",
                    new AcceptableValueRange<float>(0f, 10f)));
            DownedPlayersAreSafe = cfg.Bind(D, "DownedPlayersAreSafe", true,
                "Bots stop targeting a player they have knocked down and go after someone else. " +
                "Off = they keep hitting the body (juggling). Only the host's setting matters, " +
                "because only the host runs the bots.");

            const string C = "4. Kill cam";
            KillCamEnabled = cfg.Bind(C, "Enabled", true,
                "While you are down, a camera films your body flying and then turns to the bot that got you.");
            KillCamModel = cfg.Bind(C, "ShowCameraModel", true,
                "Draw the kill cam itself in the world - a little film camera with a blinking REC light " +
                "and its view cone - so you can see where the shot is taken from. Only you see it.");
            KillCamHeadsetScreen = cfg.Bind(C, "HeadsetScreen", true,
                "Show the kill cam on a floating screen in front of you in VR. The headset view itself is " +
                "never taken over; that is the ragdoll mod's job and a moving shot there makes people ill.");
            KillCamMonitorPip = cfg.Bind(C, "MonitorPictureInPicture", true,
                "Show the kill cam in the top-right corner of the monitor.");
            KillCamFocusDelay = cfg.Bind(C, "FocusDelay", 1.2f,
                new ConfigDescription("Seconds spent watching your body fly before swinging round to the bot.",
                    new AcceptableValueRange<float>(0f, 10f)));
            KillCamFov = cfg.Bind(C, "Fov", 70f,
                new ConfigDescription("Field of view while following the body.",
                    new AcceptableValueRange<float>(20f, 120f)));
            KillCamZoomFov = cfg.Bind(C, "ZoomFov", 32f,
                new ConfigDescription("Field of view it zooms to on the bot. Lower = tighter on its face.",
                    new AcceptableValueRange<float>(10f, 120f)));
            KillCamResolution = cfg.Bind(C, "Resolution", 512,
                new ConfigDescription("Width of the kill cam image in pixels (16:9). It is a whole extra render " +
                                      "of the scene while you are down, so keep it modest in VR.",
                    new AcceptableValueRange<int>(128, 1920)));

            const string S = "5. Impact sounds";
            SoundsEnabled = cfg.Bind(S, "Enabled", true,
                "Play the WAVs in plugins/NextBotsRagdoll/sounds when your ragdoll hits things and when a bot hits you.");
            SoundVolume = cfg.Bind(S, "Volume", 0.9f,
                new ConfigDescription("Master volume for impact sounds.", new AcceptableValueRange<float>(0f, 1f)));
            SoftSpeed = cfg.Bind(S, "SoftSpeed", 1.5f,
                new ConfigDescription("Impact speed, m/s, below which nothing plays.",
                    new AcceptableValueRange<float>(0.1f, 20f)));
            HardSpeed = cfg.Bind(S, "HardSpeed", 5f,
                new ConfigDescription("Impact speed, m/s, from which the hard set plays instead of the soft one.",
                    new AcceptableValueRange<float>(0.5f, 40f)));
            BreakSpeed = cfg.Bind(S, "BreakSpeed", 11f,
                new ConfigDescription("Impact speed, m/s, from which the bone-crunch 'break' set plays.",
                    new AcceptableValueRange<float>(1f, 60f)));
            ShareSounds = cfg.Bind(S, "ShareWithOthers", true,
                "Send your ragdoll's impacts to other players who have this mod, so they hear your body land. " +
                "Follows GorillaRagdoll's ShareMyRagdoll too: not sharing the ragdoll means not sharing its sounds.");
            HearOthers = cfg.Bind(S, "HearOthers", true,
                "Play other players' ragdoll impacts, and the hit and death sounds when a bot catches them.");
            DeathSound = cfg.Bind(S, "DeathSound", DeathSoundMode.EveryCatch,
                "When the death set (Garry's Mod's Player.Death) plays: EveryCatch, KilledOnly (DEATH ON CATCH), or Off.");
            ImportFromSourceGames = cfg.Bind(S, "ImportFromSourceGames", true,
                "On startup, fill any EMPTY sound folder from your own Garry's Mod or Half-Life 2 install, using the " +
                "exact files those games play for a body. Nothing is downloaded and folders you filled yourself are never touched.");

            const string T = "6. Testing";
            TestCatchKey = cfg.Bind(T, "TestCatchKey", KeyCode.F5,
                "Pretend the nearest bot just caught you - knockback, kill cam and sounds, end to end. " +
                "Works with no bots spawned too (you are hit from the front). Desktop window needs focus.");
            TestBotSpeed = cfg.Bind(T, "TestBotSpeed", 8f,
                new ConfigDescription("How fast the pretend bot is moving when it hits you, m/s.",
                    new AcceptableValueRange<float>(0f, 30f)));
        }
    }
}
