using System;
using System.IO;
using BepInEx;
using UnityEngine;

namespace NextBotsRagdoll
{
    /// <summary>
    /// How the landing dust looks: the numbers from the local design page, in the same file it
    /// writes. Loaded from <c>BepInEx/plugins/NextBotsRagdoll/dust.json</c> and re-read within a
    /// second of changing, so tuning on the page shows up in the game while it runs.
    ///
    /// <para>The defaults are the look that was settled on. Any field left out keeps its
    /// default; colours are <c>"#RRGGBB"</c>.</para>
    /// </summary>
    [Serializable]
    public class DustLook
    {
        // ---- the burst
        public float speed = 8f;
        public float drag = 4f;
        public float growth = 2.35f;
        public float column = 0.62f;
        public float rise = 0.25f;
        public float wind = 1.45f;

        // ---- the cloud
        public float life = 2.4f;
        public float density = 0.3f;
        public float size = 0.51f;
        public float curl = 2.1f;
        public float erode = 0.92f;
        public float churn = 0.95f;
        public float hug = 0.34f;
        public float ring = 0.85f;

        // ---- light
        public string lit = "#bca085";
        public string shadow = "#4a4437";
        public string sky = "#8fa0bb";
        public string bounce = "#7d6a4e";
        public float lightAngle = 282f;
        public float ambient = 0.32f;
        public float core = 1f;

        // ---- on the ground
        public float grit = 184f;
        public float gritBounce = 0.4f;
        public float flash = 1f;
        public float scuff = 0.66f;
        public float scuffLife = 12f;

        public static string FilePath =>
            Path.Combine(Paths.PluginPath, Path.Combine("NextBotsRagdoll", "dust.json"));

        public Color Lit => Parse(lit, new Color(0.74f, 0.63f, 0.52f));
        public Color Shadow => Parse(shadow, new Color(0.29f, 0.27f, 0.22f));
        public Color Sky => Parse(sky, new Color(0.56f, 0.63f, 0.73f));
        public Color Bounce => Parse(bounce, new Color(0.49f, 0.42f, 0.31f));

        private static Color Parse(string hex, Color fallback)
        {
            Color c;
            return !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex.Trim(), out c) ? c : fallback;
        }

        /// <summary>Keeps a hand-edited file from producing something unusable.</summary>
        public void Sanitise()
        {
            speed = Mathf.Clamp(speed, 0.5f, 20f);
            drag = Mathf.Clamp(drag, 0.2f, 10f);
            growth = Mathf.Clamp(growth, 0.3f, 4f);
            column = Mathf.Clamp01(column);
            rise = Mathf.Clamp(rise, 0f, 4f);
            wind = Mathf.Clamp(wind, 0f, 4f);
            life = Mathf.Clamp(life, 0.4f, 10f);
            density = Mathf.Clamp(density, 0.05f, 6f);
            size = Mathf.Clamp(size, 0.1f, 2f);
            curl = Mathf.Clamp(curl, 0f, 4f);
            erode = Mathf.Clamp01(erode);
            churn = Mathf.Clamp(churn, 0f, 3f);
            hug = Mathf.Clamp01(hug);
            ring = Mathf.Clamp(ring, 0f, 2f);
            ambient = Mathf.Clamp01(ambient);
            core = Mathf.Clamp01(core);
            grit = Mathf.Clamp(grit, 0f, 300f);
            gritBounce = Mathf.Clamp(gritBounce, 0f, 0.9f);
            flash = Mathf.Clamp01(flash);
            scuff = Mathf.Clamp01(scuff);
            scuffLife = Mathf.Clamp(scuffLife, 1f, 60f);
        }
    }
}
