using System;
using System.IO;
using BepInEx;
using UnityEngine;

namespace NextBots.UI
{
    public enum KillFeedCorner { TopLeft, TopRight, BottomLeft, BottomRight }

    /// <summary>
    /// How the death log looks and where it sits. Loaded from
    /// <c>BepInEx/plugins/NextBots/killfeed.json</c> - edit it by hand or with a designer page
    /// that writes the same fields; any field left out keeps its default. The
    /// defaults are Garry's Mod's own death notices: no box, a drop shadow, NPC killers in
    /// red and players in yellow.
    ///
    /// <para>Field names are the file format, so they stay lower camel case. Colours are
    /// <c>"#RRGGBBAA"</c>; an alpha of 00 switches that part off.</para>
    /// </summary>
    [Serializable]
    public class KillFeedStyle
    {
        public bool enabled = true;
        public string preset = "gmod";
        public string corner = "TopRight";
        public bool showInVr = true;
        public bool showOnMonitor = true;

        public float lifetime = 6f;
        public float fadeTime = 0.5f;
        public int maxEntries = 5;
        public string animation = "Slide";

        public string background = "#00000000";
        public string border = "#00000000";
        public string localHighlight = "#00000000";
        public string killerColor = "#FA3232FF";
        public string victimColor = "#FFFF64FF";
        public bool textShadow = true;

        public string icon = "BotImage";
        public float iconSize = 1.4f;
        public string verb = "";
        public bool upperCase = false;

        /// <summary>A Windows font by family name ("Verdana", "Impact"); empty = the game's own
        /// (Liberation Sans). Garry's Mod's death notices are Verdana, bold, with a shadow.</summary>
        public string font = "";

        /// <summary>The monitor copy's font, when it should differ from <see cref="font"/>; empty
        /// = the same one. The monitor can only use fonts Windows has installed for every user,
        /// so a font installed only for you works in the headset but needs a stand-in here -
        /// Verdana, which Garry's Mod itself uses, is always there.</summary>
        public string monitorFont = "";

        public bool bold = false;
        public float padding = 0.35f;
        public float spacing = 0.25f;

        public float vrDistance = 1.0f;
        public float vrOffsetX = 0.34f;
        public float vrOffsetY = 0.22f;
        public float vrTextHeight = 0.02f;

        public int monitorTextSize = 20;

        /// <summary>Pixels from the left or right edge.</summary>
        public int monitorMargin = 18;

        /// <summary>Pixels from the top or bottom edge; -1 = the same as <see cref="monitorMargin"/>.</summary>
        public int monitorMarginY = -1;

        public int MarginY => monitorMarginY >= 0 ? monitorMarginY : monitorMargin;

        public static string FilePath =>
            Path.Combine(Paths.PluginPath, Path.Combine("NextBots", "killfeed.json"));

        // ------------------------------------------------------------------ parsed views

        public KillFeedCorner Corner
        {
            get
            {
                KillFeedCorner c;
                return Enum.TryParse(corner ?? "", true, out c) ? c : KillFeedCorner.TopRight;
            }
        }

        public bool Right => Corner == KillFeedCorner.TopRight || Corner == KillFeedCorner.BottomRight;
        public bool Top => Corner == KillFeedCorner.TopLeft || Corner == KillFeedCorner.TopRight;
        public bool ShowIcon => string.Equals(icon, "BotImage", StringComparison.OrdinalIgnoreCase);

        public Color Background => ParseColor(background, Color.clear);
        public Color Border => ParseColor(border, Color.clear);
        public Color LocalHighlight => ParseColor(localHighlight, Color.clear);
        public Color KillerColor => ParseColor(killerColor, new Color(0.98f, 0.2f, 0.2f));
        public Color VictimColor => ParseColor(victimColor, new Color(1f, 1f, 0.39f));

        public static Color ParseColor(string hex, Color fallback)
        {
            Color c;
            return !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex.Trim(), out c) ? c : fallback;
        }

        /// <summary>Keeps a hand-edited file from producing something unusable - a negative
        /// lifetime, a zero-height line of text, a thousand rows.</summary>
        public void Sanitise()
        {
            lifetime = Mathf.Clamp(lifetime, 0.5f, 60f);
            fadeTime = Mathf.Clamp(fadeTime, 0f, lifetime);
            maxEntries = Mathf.Clamp(maxEntries, 1, 12);
            iconSize = Mathf.Clamp(iconSize, 0.3f, 6f);
            padding = Mathf.Clamp(padding, 0f, 3f);
            spacing = Mathf.Clamp(spacing, 0f, 3f);
            vrDistance = Mathf.Clamp(vrDistance, 0.3f, 5f);
            vrOffsetX = Mathf.Clamp(vrOffsetX, 0f, 2f);
            vrOffsetY = Mathf.Clamp(vrOffsetY, 0f, 2f);
            vrTextHeight = Mathf.Clamp(vrTextHeight, 0.004f, 0.2f);
            monitorTextSize = Mathf.Clamp(monitorTextSize, 8, 96);
            monitorMargin = Mathf.Clamp(monitorMargin, 0, 1000);
            monitorMarginY = monitorMarginY < 0 ? -1 : Mathf.Clamp(monitorMarginY, 0, 1000);
        }
    }
}
