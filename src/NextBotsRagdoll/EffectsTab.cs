using GorillaRagdoll.UI;
using UnityEngine;

namespace NextBotsRagdoll
{
    /// <summary>
    /// The Effects tab of the F4 menu: everything a hit does to the senses, in one place, live.
    ///
    /// <para>These are all config entries, and every one of them is read where it is used rather than
    /// once at start-up, so a slider here changes what the next hit looks and sounds like. That is the
    /// point of having it in the menu: the values were being found by editing a file, restarting and
    /// getting hit, and the useful loop is drag, press the button, see.</para>
    ///
    /// <para>Widgets come from the ragdoll mod's menu, so this tab looks and behaves like the rest.</para>
    /// </summary>
    internal static class EffectsTab
    {
        public static void Register()
        {
            MenuTabs.Add("Effects", Draw);
        }

        private static void Section(string title)
        {
            GUILayout.Space(8f);
            GUILayout.Label("<b>" + title + "</b>", RagdollMenu.RichLabel());
        }

        private static void Note(string text) => GUILayout.Label(text, RagdollMenu.Wrapped());

        private static void Draw()
        {
            // ---- try it
            if (GUILayout.Button("Fire a test hit  (" + BridgeConfig.TestCatchKey.Value + ")"))
            {
                if (KnockdownBridge.Instance != null) KnockdownBridge.Instance.RunTest();
            }
            Note("Drag a slider, fire a hit, see and hear it. Changes apply to the next hit and are saved.");

            // ---- the death overlay
            Section("Death overlay");
            RagdollMenu.Bool(BridgeConfig.VignetteEnabled, "Enabled");
            Note(ColourStatus());
            RagdollMenu.Slider(BridgeConfig.VignetteDrain, "Colour drained (1 = grey)");
            RagdollMenu.Slider(BridgeConfig.VignetteDim, "Dim");
            RagdollMenu.Slider(BridgeConfig.VignetteStrength, "Edge strength");
            RagdollMenu.Slider(BridgeConfig.VignetteSeconds, "Fades over (s)", "0.0");
            RagdollMenu.Slider(BridgeConfig.VignetteRedSeconds, "Red flash holds (s)");
            RagdollMenu.Slider(BridgeConfig.VignetteRecoverSeconds, "Clears when you stand (s)");
            RagdollMenu.Bool(BridgeConfig.VignetteTrueInVr, "Real drain in the headset too (untested)");
            Note("Used instead of the real drain where it isn't available:");
            RagdollMenu.Slider(BridgeConfig.VignetteWash, "  Grey wash strength");
            RagdollMenu.Slider(BridgeConfig.VignetteGrey, "  Grey wash lightness");

            // ---- ears
            Section("Ears");
            RagdollMenu.Bool(BridgeConfig.DazeEnabled, "Muffled hearing and ringing");
            RagdollMenu.Bool(BridgeConfig.DazeOnHardImpacts, "Also for very hard impacts");
            RagdollMenu.Slider(BridgeConfig.DazeMuffle, "How dull");
            RagdollMenu.Slider(BridgeConfig.DazeRingVolume, "Ringing volume");
            RagdollMenu.Slider(BridgeConfig.DazeSeconds, "Lasts (s)", "0.0");

            // ---- dust
            Section("Dust");
            RagdollMenu.Bool(BridgeConfig.DustEnabled, "Landing dust");
            RagdollMenu.Bool(BridgeConfig.DustForOthers, "Other players' landings too");
            RagdollMenu.Bool(BridgeConfig.HardLandingEnabled, "Hard landings on your feet");
            RagdollMenu.Slider(BridgeConfig.HardLandingMinSpeed, "  Counts above (m/s)", "0.0");
            RagdollMenu.Slider(BridgeConfig.DustMinSpeed, "Smallest impact that puffs (m/s)", "0.0");
            RagdollMenu.IntSlider(BridgeConfig.DustAmount, "Puffs in the biggest burst");
            RagdollMenu.Slider(BridgeConfig.DustOpacity, "Opacity");
            Note("Lower the puff count if a landing dips your frame rate in the headset. How the dust " +
                 "looks - colour, speed, grit - is dust.json, which the local design page writes.");

            // ---- sounds
            Section("Sounds");
            RagdollMenu.Bool(BridgeConfig.SoundsEnabled, "Impact and hit sounds");
            RagdollMenu.Slider(BridgeConfig.SoundVolume, "Volume");
            RagdollMenu.EnumRow("Death cry", BridgeConfig.DeathSound);

            // ---- timing
            Section("Timing");
            RagdollMenu.Bool(BridgeConfig.BeatSyncToAudio, "Hold the picture back to match the sound");
            RagdollMenu.IntSlider(BridgeConfig.BeatExtraDelayMs, "Extra picture delay (ms)");
            RagdollMenu.IntSlider(BridgeConfig.BeatPainDelayMs, "Death cry follows the thud by (ms)");
            Note("The sound reaches your ear about " + (HitBeat.AudioLatency() * 1000f).ToString("0") +
                 " ms after the game asks for it, so the flash, the drain and the dust wait " +
                 (HitBeat.VisualDelay() * 1000f).ToString("0") + " ms to land with it. If a recording " +
                 "shows the picture early or late, nudge the extra delay - up for early, down for late. " +
                 "A Bluetooth headset usually needs a positive number.");
        }

        /// <summary>Says which drain is in use, and why if it is the fallback.</summary>
        private static string ColourStatus()
        {
            if (ScreenSaturation.Available) return "Colour drain: real desaturation (shader installed).";
            if (!string.IsNullOrEmpty(ScreenSaturation.Failure)) return "Colour drain: grey wash - " + ScreenSaturation.Failure + ".";
            return "Colour drain: grey wash (the shader installs a few seconds after start-up, and not in the headset unless enabled).";
        }
    }
}
