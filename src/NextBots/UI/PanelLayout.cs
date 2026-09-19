using System;
using System.IO;
using BepInEx;
using UnityEngine;

namespace NextBots.UI
{
    /// <summary>
    /// Everything about how the panel looks and where it sits, loaded from
    /// <c>BepInEx/plugins/NextBots/panel.json</c> so it can be retuned without a rebuild.
    ///
    /// Edit the file (by hand or with a layout tool) and press the reload key in game to pick
    /// up changes. That loop is why these are data and not consts.
    ///
    /// Uses Unity's JsonUtility rather than Newtonsoft: no extra assembly reference, and it
    /// cannot fail on a version mismatch with whatever the game ships.
    /// </summary>
    [Serializable]
    public class PanelLayout
    {
        // ---- placement -------------------------------------------------------
        /// <summary>"aboveHand" (default) or "wrist".</summary>
        public string anchorMode = "aboveHand";

        public bool anchorLeftHand = true;

        /// <summary>Metres above the hand, along world up.</summary>
        public float anchorHeight = 0.17f;

        /// <summary>Metres toward the player from the hand, so it is not edge-on.</summary>
        public float anchorForward = 0.0f;

        /// <summary>
        /// Face the player's head rather than inheriting hand rotation. Keeps the panel
        /// readable no matter how the hand is turned, which is the whole point of putting it
        /// above the hand rather than on it.
        /// </summary>
        public bool faceHead = true;

        /// <summary>Degrees to tilt the top away from the player.</summary>
        public float tiltDegrees = 12f;

        /// <summary>Smoothing on the follow, 0 = rigid. Reduces jitter from hand tracking.</summary>
        public float followSmoothing = 12f;

        // ---- size ------------------------------------------------------------
        public float width = 0.26f;
        public float height = 0.20f;

        // ---- list ------------------------------------------------------------
        /// <summary>Rows visible in the browser list at once.</summary>
        public int visibleRows = 5;

        public float rowHeight = 0.022f;
        public float rowGap = 0.004f;

        // ---- buttons ---------------------------------------------------------
        public float buttonHeight = 0.028f;
        public float buttonGap = 0.006f;

        /// <summary>
        /// Press-volume depth either side of the panel plane. Generous on purpose: a hand at
        /// 2 m/s covers ~22 mm per frame at 90 fps.
        /// </summary>
        public float buttonDepth = 0.035f;

        // ---- text ------------------------------------------------------------
        public float textTitle = 0.012f;
        public float textRow = 0.013f;
        public float textValue = 0.016f;
        public float textButton = 0.011f;
        public float textFooter = 0.010f;

        // ---- colours (hex, "#rrggbb") ---------------------------------------
        public string colBackground = "#0B0D12";
        public string colHeader = "#151926";
        public string colRow = "#171B26";
        public string colRowSelected = "#2E6BE6";
        public string colButton = "#232838";
        public string colButtonHot = "#4C8DFF";
        public string colButtonOff = "#14161D";
        public string colText = "#EDF1FF";
        public string colTextDim = "#8A93AC";
        public string colAccent = "#5AC8FA";
        public string colGood = "#5BE58C";
        public string colWarn = "#FFB84D";

        // ======================================================================

        public static string FilePath =>
            Path.Combine(Paths.PluginPath, Path.Combine("NextBots", "panel.json"));

        public static PanelLayout Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var loaded = JsonUtility.FromJson<PanelLayout>(json);
                    if (loaded != null)
                    {
                        loaded.Sanitise();
                        Plugin.Log.LogInfo("[Panel] layout loaded from " + FilePath);
                        return loaded;
                    }
                    Plugin.Log.LogWarning("[Panel] panel.json parsed to null; using defaults.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Panel] could not read panel.json (" + ex.Message + "); using defaults.");
            }

            var def = new PanelLayout();
            def.TrySave();      // so there is something to edit next time
            return def;
        }

        public void TrySave()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonUtility.ToJson(this, true));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Panel] could not write panel.json: " + ex.Message);
            }
        }

        /// <summary>
        /// Clamp anything that would produce an unusable panel. A design tool that can brick
        /// the menu with a bad number is worse than no design tool.
        /// </summary>
        private void Sanitise()
        {
            width = Mathf.Clamp(width, 0.05f, 1.5f);
            height = Mathf.Clamp(height, 0.05f, 1.5f);
            anchorHeight = Mathf.Clamp(anchorHeight, -0.5f, 1.0f);
            anchorForward = Mathf.Clamp(anchorForward, -0.5f, 0.5f);
            tiltDegrees = Mathf.Clamp(tiltDegrees, -80f, 80f);
            followSmoothing = Mathf.Clamp(followSmoothing, 0f, 60f);

            visibleRows = Mathf.Clamp(visibleRows, 1, 12);
            rowHeight = Mathf.Clamp(rowHeight, 0.005f, 0.2f);
            rowGap = Mathf.Clamp(rowGap, 0f, 0.05f);

            buttonHeight = Mathf.Clamp(buttonHeight, 0.008f, 0.2f);
            buttonGap = Mathf.Clamp(buttonGap, 0f, 0.05f);
            buttonDepth = Mathf.Clamp(buttonDepth, 0.005f, 0.15f);

            textTitle = Mathf.Clamp(textTitle, 0.002f, 0.1f);
            textRow = Mathf.Clamp(textRow, 0.002f, 0.1f);
            textValue = Mathf.Clamp(textValue, 0.002f, 0.1f);
            textButton = Mathf.Clamp(textButton, 0.002f, 0.1f);
            textFooter = Mathf.Clamp(textFooter, 0.002f, 0.1f);
        }

        // ---- colour helpers --------------------------------------------------

        public Color Background => Hex(colBackground, new Color(0.04f, 0.05f, 0.07f));
        public Color Header => Hex(colHeader, new Color(0.08f, 0.10f, 0.15f));
        public Color Row => Hex(colRow, new Color(0.09f, 0.11f, 0.15f));
        public Color RowSelected => Hex(colRowSelected, new Color(0.18f, 0.42f, 0.90f));
        public Color Button => Hex(colButton, new Color(0.14f, 0.16f, 0.22f));
        public Color ButtonHot => Hex(colButtonHot, new Color(0.30f, 0.55f, 1.00f));
        public Color ButtonOff => Hex(colButtonOff, new Color(0.08f, 0.09f, 0.11f));
        public Color Text => Hex(colText, Color.white);
        public Color TextDim => Hex(colTextDim, new Color(0.54f, 0.58f, 0.67f));
        public Color Accent => Hex(colAccent, new Color(0.35f, 0.78f, 0.98f));
        public Color Good => Hex(colGood, new Color(0.36f, 0.90f, 0.55f));
        public Color Warn => Hex(colWarn, new Color(1.00f, 0.72f, 0.30f));

        public static Color Hex(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            Color parsed;
            if (ColorUtility.TryParseHtmlString(hex.StartsWith("#") ? hex : "#" + hex, out parsed))
                return parsed;
            return fallback;
        }
    }
}
