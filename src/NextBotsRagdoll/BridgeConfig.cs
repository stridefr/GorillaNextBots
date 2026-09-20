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

        public static ConfigEntry<bool> HardLandingEnabled;
        public static ConfigEntry<float> HardLandingMinSpeed;

        public static ConfigEntry<bool> DazeEnabled;
        public static ConfigEntry<bool> DazeOnHardImpacts;
        public static ConfigEntry<float> DazeRingVolume;
        public static ConfigEntry<float> DazeMuffle;
        public static ConfigEntry<float> DazeSeconds;

        public static ConfigEntry<bool> DustEnabled;
        public static ConfigEntry<bool> DustForOthers;
        public static ConfigEntry<float> DustMinSpeed;
        public static ConfigEntry<int> DustAmount;
        public static ConfigEntry<float> DustOpacity;

        public static ConfigEntry<bool> VignetteEnabled;
        public static ConfigEntry<float> VignetteStrength;
        public static ConfigEntry<float> VignetteWash;
        public static ConfigEntry<float> VignetteGrey;
        public static ConfigEntry<float> VignetteRedSeconds;
        public static ConfigEntry<float> VignetteRecoverSeconds;

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
            KillCamEnabled = cfg.Bind(C, "Enabled", false,
                "While you are down, a camera films your body flying and then turns to the bot that got you. " +
                "Off by default: it is a second render of the whole scene, and the knockdown reads better " +
                "without a picture in the corner competing with it.");
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

            const string U = "9. Landing dust";
            DustEnabled = cfg.Bind(U, "Enabled", true,
                "A body landing throws up a ring of dust with grit, and leaves a scuff on the floor. " +
                "The look is in plugins/NextBotsRagdoll/dust.json, which the design page writes.");
            DustForOthers = cfg.Bind(U, "ShowForOthers", true,
                "Also show the dust from other players' landings.");
            DustMinSpeed = cfg.Bind(U, "MinSpeed", 4f,
                new ConfigDescription("Impacts slower than this (m/s) throw no dust. Below the hard-impact " +
                                      "speed, so a heavy thud puffs and a gentle bump does not.",
                    new AcceptableValueRange<float>(1f, 20f)));
            DustAmount = cfg.Bind(U, "Amount", 110,
                new ConfigDescription("Puffs in the biggest burst. The cost is mostly overdraw, so this is " +
                                      "the number to lower if a landing dips your frame rate in VR.",
                    new AcceptableValueRange<int>(20, 300)));
            HardLandingEnabled = cfg.Bind(U, "HardLandings", true,
                "Landing hard as a normal gorilla - dropping off a roof - throws the same dust, and other " +
                "players see and hear it. A ragdoll's own landings are always covered.");
            HardLandingMinSpeed = cfg.Bind(U, "HardLandingSpeed", 8f,
                new ConfigDescription("How fast, in m/s, you have to be falling when you land for it to count. " +
                                      "Around 8 is a drop off a roof; 5 catches a jump off a wall.",
                    new AcceptableValueRange<float>(3f, 30f)));
            DustOpacity = cfg.Bind(U, "Opacity", 1f,
                new ConfigDescription("Multiplies how solid the dust is, on top of the look's own thickness.",
                    new AcceptableValueRange<float>(0.1f, 2f)));

            const string Z = "10. Impact daze";
            DazeEnabled = cfg.Bind(Z, "Enabled", true,
                "A heavy hit muffles everything you hear, as if underwater, and leaves a thin high whine on top; " +
                "both fade as you come round. Heavier bots hit harder.");
            DazeOnHardImpacts = cfg.Bind(Z, "OnHardImpacts", true,
                "Also for very hard landings and slamming into things as a ragdoll, not only being caught. " +
                "Only the very hardest count.");
            DazeRingVolume = cfg.Bind(Z, "RingVolume", 0.25f,
                new ConfigDescription("How loud the whine is at its worst. Quiet by default and hard-capped; " +
                                      "0 turns the whine off and keeps the muffling. It is added after the " +
                                      "muffling, so the muffling never dulls it.",
                    new AcceptableValueRange<float>(0f, 1f)));
            DazeMuffle = cfg.Bind(Z, "Muffle", 0.55f,
                new ConfigDescription("How dull everything goes at its worst. Around 0.5 is like the next room; " +
                                      "1 is like hearing through a wall, though never silent; " +
                                      "0 leaves the sound alone and keeps the whine.",
                    new AcceptableValueRange<float>(0f, 1f)));
            DazeSeconds = cfg.Bind(Z, "Seconds", 4.5f,
                new ConfigDescription("Roughly how long it takes to clear. The whine lingers a little after the " +
                                      "muffling has gone.",
                    new AcceptableValueRange<float>(1f, 15f)));

            const string V = "8. Death vignette";
            VignetteEnabled = cfg.Bind(V, "Enabled", true,
                "Being caught flashes the edges of your view red, drains the colour out of the world while " +
                "you are down, and lets it come back as you get up.");
            VignetteStrength = cfg.Bind(V, "Strength", 0.8f,
                new ConfigDescription("How heavy it gets. The middle of your view is never blacked out, " +
                                      "whatever this is set to.",
                    new AcceptableValueRange<float>(0.1f, 1f)));
            VignetteWash = cfg.Bind(V, "Wash", 0.5f,
                new ConfigDescription("How strongly the grey wash takes the colour out of the whole view. " +
                                      "0 = edges only. This is what fades slowly on the way back, which is " +
                                      "the colour returning.",
                    new AcceptableValueRange<float>(0f, 1f)));
            VignetteGrey = cfg.Bind(V, "Grey", 0.38f,
                new ConfigDescription("How light the wash is. 0 is nearly black, which dims the world as it drains " +
                                      "the colour; higher looks like a white haze. The real desaturation a " +
                                      "post-process would give is not available in this game, and a see-through " +
                                      "grey is the nearest thing.",
                    new AcceptableValueRange<float>(0f, 0.8f)));
            VignetteRedSeconds = cfg.Bind(V, "RedSeconds", 0.3f,
                new ConfigDescription("How long the red flash holds before it turns grey.",
                    new AcceptableValueRange<float>(0f, 2f)));
            VignetteRecoverSeconds = cfg.Bind(V, "RecoverSeconds", 2.5f,
                new ConfigDescription("Seconds for the colour to come back once you are up.",
                    new AcceptableValueRange<float>(0.2f, 10f)));
        }
    }
}
