using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GorillaNextBots.Shared
{
    /// <summary>
    /// Fonts for the monitor menus (IMGUI), loaded from a TrueType file the mod carries in
    /// <c>plugins/NextBots/fonts</c> rather than from Windows' installed list - so a font works on
    /// every player's PC without being installed. IMGUI can only draw TrueType outlines; a
    /// CFF-based <c>.otf</c> loads with no letters, which is why the mod ships a <c>.ttf</c> copy.
    /// </summary>
    internal static class FileFonts
    {
        private static readonly Dictionary<string, Font> Cache = new Dictionary<string, Font>(StringComparer.OrdinalIgnoreCase);

        public static string Folder =>
            Path.Combine(BepInEx.Paths.PluginPath, Path.Combine("NextBots", "fonts"));

        /// <summary>A font whose .ttf file name starts with <paramref name="family"/>; null if there is
        /// none or it would not draw.</summary>
        public static Font Get(string family, Action<string> log)
        {
            family = (family ?? "").Trim();
            if (family.Length == 0) return null;
            Font font;
            if (Cache.TryGetValue(family, out font)) return font;
            Cache[family] = null;

            try
            {
                if (!Directory.Exists(Folder)) return null;
                foreach (var path in Directory.GetFiles(Folder, "*.ttf"))
                {
                    if (!Path.GetFileNameWithoutExtension(path).StartsWith(family, StringComparison.OrdinalIgnoreCase)) continue;
                    font = new Font(path) { hideFlags = HideFlags.DontUnloadUnusedAsset };
                    font.RequestCharactersInTexture("Ag", 16);
                    CharacterInfo ci;
                    if (!font.GetCharacterInfo('A', out ci, 16))
                    {
                        log?.Invoke("[Fonts] '" + Path.GetFileName(path) + "' has no letters the menu can draw");
                        return null;
                    }
                    log?.Invoke("[Fonts] menu font '" + family + "' from " + Path.GetFileName(path));
                    Cache[family] = font;
                    return font;
                }
            }
            catch (Exception ex) { log?.Invoke("[Fonts] menu font '" + family + "': " + ex.Message); }
            return null;
        }
    }
}
