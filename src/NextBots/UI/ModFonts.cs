using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace NextBots.UI
{
    /// <summary>
    /// Fonts for the mod's text - the death log, the wrist panel - built at runtime from fonts
    /// installed on the player's own PC.
    ///
    /// <para>The game ships exactly one text font (Liberation Sans), and a mod cannot ship font
    /// assets without a Unity project. But TextMesh Pro can build a font asset from a .ttf or
    /// .otf file while the game runs, so a font is just a name in the settings. Nothing is
    /// bundled: that also keeps fonts whose licence forbids embedding them in an app (Coolvetica,
    /// for one) usable, because each player's own installed copy is what gets read.</para>
    ///
    /// <para><b>Finding the file.</b> Windows' standard fonts come from a fixed table. Anything
    /// else is found by reading the real family and style names out of every font file in the
    /// player's own fonts folder (<c>%LOCALAPPDATA%\Microsoft\Windows\Fonts</c>, where
    /// installing a font for one user puts it) and then <c>C:\Windows\Fonts</c> - file names are
    /// no guide ("Coolvetica Rg.otf").</para>
    ///
    /// <para>The headset text needs a TextMesh Pro font asset; the monitor's IMGUI text needs an
    /// ordinary dynamic font. Both are built once per font and cached. Anything that fails -
    /// a font that is not installed, a shader the game stripped - falls back to the game's own
    /// font and says so in the log, so a bad name never blanks any text.</para>
    /// </summary>
    public static class ModFonts
    {
        /// <summary>
        /// Family name to its regular and bold files, for the fonts every Windows PC has (and the
        /// Office ones). A fast path: no scanning needed for these.
        /// </summary>
        private static readonly Dictionary<string, string[]> Files =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Arial", new[] { "arial.ttf", "arialbd.ttf" } },
            { "Arial Black", new[] { "ariblk.ttf", null } },
            { "Bahnschrift", new[] { "bahnschrift.ttf", null } },
            { "Calibri", new[] { "calibri.ttf", "calibrib.ttf" } },
            { "Cambria", new[] { "cambria.ttc", "cambriab.ttf" } },
            { "Candara", new[] { "Candara.ttf", "Candarab.ttf" } },
            { "Comic Sans MS", new[] { "comic.ttf", "comicbd.ttf" } },
            { "Consolas", new[] { "consola.ttf", "consolab.ttf" } },
            { "Corbel", new[] { "corbel.ttf", "corbelb.ttf" } },
            { "Courier New", new[] { "cour.ttf", "courbd.ttf" } },
            { "Franklin Gothic Medium", new[] { "framd.ttf", null } },
            { "Gabriola", new[] { "Gabriola.ttf", null } },
            { "Georgia", new[] { "georgia.ttf", "georgiab.ttf" } },
            { "Impact", new[] { "impact.ttf", null } },
            { "Ink Free", new[] { "Inkfree.ttf", null } },
            { "Lucida Console", new[] { "lucon.ttf", null } },
            { "Palatino Linotype", new[] { "pala.ttf", "palab.ttf" } },
            { "Segoe Print", new[] { "segoepr.ttf", "segoeprb.ttf" } },
            { "Segoe Script", new[] { "segoesc.ttf", "segoescb.ttf" } },
            { "Segoe UI", new[] { "segoeui.ttf", "segoeuib.ttf" } },
            { "Segoe UI Black", new[] { "seguibl.ttf", null } },
            { "Tahoma", new[] { "tahoma.ttf", "tahomabd.ttf" } },
            { "Times New Roman", new[] { "times.ttf", "timesbd.ttf" } },
            { "Trebuchet MS", new[] { "trebuc.ttf", "trebucbd.ttf" } },
            { "Verdana", new[] { "verdana.ttf", "verdanab.ttf" } },

            { "Bauhaus 93", new[] { "BAUHS93.TTF", null } },
            { "Century Gothic", new[] { "GOTHIC.TTF", "GOTHICB.TTF" } },
            { "Chiller", new[] { "CHILLER.TTF", null } },
            { "Copperplate Gothic Bold", new[] { "COPRGTB.TTF", null } },
            { "Harlow Solid Italic", new[] { "HARLOWSI.TTF", null } },
            { "Rockwell Extra Bold", new[] { "ROCKEB.TTF", null } },
            { "Stencil", new[] { "STENCIL.TTF", null } },
        };

        private struct Built
        {
            public TMP_FontAsset Font;
            public bool TrueBold;
        }

        private static readonly Dictionary<string, Built> Headsets = new Dictionary<string, Built>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Font> Monitors = new Dictionary<string, Font>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<TMP_FontAsset, float> LineHeights = new Dictionary<TMP_FontAsset, float>();

        private static string WindowsFonts
        {
            get
            {
                try
                {
                    var d = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                    if (!string.IsNullOrEmpty(d)) return d;
                }
                catch { /* fall through */ }
                return @"C:\Windows\Fonts";
            }
        }

        private static string UserFonts
        {
            get
            {
                try
                {
                    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (!string.IsNullOrEmpty(local)) return Path.Combine(local, Path.Combine("Microsoft", Path.Combine("Windows", "Fonts")));
                }
                catch { /* fall through */ }
                return null;
            }
        }

        /// <summary>
        /// The headset font for a family, or the game's own when the family is empty or cannot
        /// be built. <paramref name="trueBold"/> says whether a real bold face was used, so the
        /// caller only fakes bold when it was not.
        /// </summary>
        public static TMP_FontAsset Headset(string family, bool bold, out bool trueBold)
        {
            trueBold = false;
            var fallback = UiResources.GameTmpFont;
            family = (family ?? "").Trim();
            if (family.Length == 0) return fallback;

            string key = family + (bold ? "|bold" : "");
            Built built;
            if (Headsets.TryGetValue(key, out built))
            {
                trueBold = built.TrueBold;
                return built.Font != null ? built.Font : fallback;
            }

            TMP_FontAsset font = null;
            bool realBold = false;
            string source = null;

            string[] files;
            if (Files.TryGetValue(family, out files))
            {
                string file = files[0];
                if (bold && files[1] != null && File.Exists(Path.Combine(WindowsFonts, files[1])))
                {
                    file = files[1];
                    realBold = true;
                }
                source = Path.Combine(WindowsFonts, file);
                font = FromFile(source);
                if (font == null) realBold = false;
            }

            if (font == null)
            {
                var found = FindInstalled(family, bold, out realBold);
                if (found != null)
                {
                    source = found;
                    font = FromFile(found);
                    if (font == null) realBold = false;
                }
            }

            if (font == null)
            {
                font = FromFamily(family, bold ? "Bold" : "Regular");
                realBold = font != null && bold;
                if (font == null && bold) font = FromFamily(family, "Regular");
                if (font != null) source = "the system font list";
            }

            if (font != null)
            {
                font.name = family + (realBold ? " Bold" : "");
                // Letters this font lacks fall back to the game's own font rather than boxes.
                if (fallback != null)
                {
                    if (font.fallbackFontAssetTable == null) font.fallbackFontAssetTable = new List<TMP_FontAsset>();
                    font.fallbackFontAssetTable.Add(fallback);
                }
                // Draw it with the shader the game's own text already uses - the one TextMesh Pro
                // picks for a new font may not have survived the game's build.
                if (fallback != null && fallback.material != null && font.material != null)
                    font.material.shader = fallback.material.shader;
                Plugin.Log.LogInfo("[Fonts] '" + font.name + "' ready, from " + source);
            }
            else
            {
                Plugin.Log.LogWarning("[Fonts] '" + family + "' is not installed or could not be built; " +
                                      "using the game's default");
            }

            Headsets[key] = new Built { Font = font, TrueBold = realBold };
            trueBold = realBold;
            return font != null ? font : fallback;
        }

        /// <summary>
        /// The monitor font for a family; null means IMGUI's own.
        ///
        /// <para><b>Only fonts Windows has installed for every user.</b> IMGUI can only reach a
        /// font through the operating system's list, and asking it for one that is not on that
        /// list - a font installed "for me only", which is where a font installed from the
        /// preview window ends up - does not fail. It hands back a font with no letters in it,
        /// which then draws whatever else happens to be in the shared glyph texture: another
        /// mod's on-screen text, in the middle of our rows. So a family Unity cannot see is
        /// refused here and the monitor copy uses the default font, which always draws what it
        /// is asked to.</para>
        /// </summary>
        public static Font Monitor(string family)
        {
            family = (family ?? "").Trim();
            if (family.Length == 0) return null;

            Font font;
            if (Monitors.TryGetValue(family, out font)) return font;

            string match = null;
            try
            {
                foreach (var n in Font.GetOSInstalledFontNames())
                {
                    if (string.Equals(n, family, StringComparison.OrdinalIgnoreCase)) { match = n; break; }
                    if (match == null && n.StartsWith(family, StringComparison.OrdinalIgnoreCase)) match = n;
                }
            }
            catch { /* no list: treat it as one we cannot see */ }

            if (match == null)
            {
                Plugin.Log.LogWarning("[Fonts] Windows has no font called '" + family + "' installed for all users, so " +
                                      "the monitor copy uses the default font. Either install it for all users " +
                                      "(right-click the font file and pick \"Install for all users\"), or set " +
                                      "monitorFont in killfeed.json to one that is, like Verdana.");
                Monitors[family] = null;
                return null;
            }

            try
            {
                font = Font.CreateDynamicFontFromOSFont(match, 32);
                // Built at runtime and referenced by nothing Unity tracks: without this, a level
                // load can unload its glyph texture under us, which draws as garbage.
                if (font != null) font.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Fonts] monitor font '" + family + "': " + ex.Message); }

            Monitors[family] = font;
            return font;
        }

        /// <summary>
        /// World height of one line at font size 1, measured for this font. Fonts differ a lot -
        /// Impact is tall and tight, Segoe Script is loose - so text heights have to be measured
        /// per font to mean the same number of millimetres whichever you pick.
        /// </summary>
        public static float LineHeightAtSizeOne(TMP_FontAsset font)
        {
            if (font == null || font == UiResources.TmpFont) return UiResources.TmpLineHeightAtSizeOne;

            float h;
            if (LineHeights.TryGetValue(font, out h)) return h;

            h = UiResources.TmpLineHeightAtSizeOne;
            GameObject probe = null;
            try
            {
                probe = new GameObject("NextBots.FontProbe") { hideFlags = HideFlags.HideAndDontSave };
                probe.SetActive(false);
                var tmp = probe.AddComponent<TextMeshPro>();
                tmp.font = font;
                tmp.fontSize = 1f;
                tmp.textWrappingMode = TextWrappingModes.NoWrap;
                tmp.text = "Hg";
                probe.SetActive(true);
                tmp.ForceMeshUpdate(true, true);
                var measured = tmp.preferredHeight;
                if (measured > 0.0001f && measured < 100f) h = measured;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Fonts] could not measure '" + font.name + "': " + ex.Message);
            }
            finally
            {
                if (probe != null) UnityEngine.Object.Destroy(probe);
            }

            LineHeights[font] = h;
            return h;
        }

        private static TMP_FontAsset FromFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return TMP_FontAsset.CreateFontAsset(path, 0, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Fonts] could not build a font from " + path + ": " + ex.Message);
                return null;
            }
        }

        private static TMP_FontAsset FromFamily(string family, string style)
        {
            try { return TMP_FontAsset.CreateFontAsset(family, style); }
            catch { return null; }
        }

        // ------------------------------------------------------------------ installed fonts

        private struct Face
        {
            public string Path, Family, Style;
        }

        private static List<Face> _userFaces, _windowsFaces;

        /// <summary>
        /// A font file whose family matches, from the player's own fonts folder first and then
        /// Windows'. Among a family's faces the plain one is preferred - Regular, not Italic,
        /// Condensed or Compressed - or the boldest plain one when bold is asked for.
        /// </summary>
        private static string FindInstalled(string family, bool bold, out bool isBold)
        {
            isBold = false;
            if (_userFaces == null) _userFaces = Scan(UserFonts);
            var hit = Best(_userFaces, family, bold, out isBold);
            if (hit != null) return hit;

            if (_windowsFaces == null) _windowsFaces = Scan(WindowsFonts);
            return Best(_windowsFaces, family, bold, out isBold);
        }

        private static string Best(List<Face> faces, string family, bool bold, out bool isBold)
        {
            isBold = false;
            string best = null;
            int bestScore = int.MinValue;
            foreach (var f in faces)
            {
                if (!string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase)) continue;

                string style = (f.Style ?? "").ToLowerInvariant();
                bool faceBold = style.Contains("bold") || style.Contains("heavy") || style.Contains("black");
                int score = 0;
                if (style == "regular" || style == "normal" || style == "book") score += 50;
                foreach (var odd in new[] { "italic", "oblique", "cond", "comp", "cram", "narrow", "light", "thin" })
                    if (style.Contains(odd)) score -= 40;
                score += bold == faceBold ? 30 : -30;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = f.Path;
                    isBold = faceBold;
                }
            }
            return best;
        }

        private static List<Face> Scan(string dir)
        {
            var faces = new List<Face>();
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return faces;
                foreach (var path in Directory.GetFiles(dir))
                {
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext != ".ttf" && ext != ".otf" && ext != ".ttc") continue;
                    string family, style;
                    if (TryReadNames(path, out family, out style))
                        faces.Add(new Face { Path = path, Family = family, Style = style });
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Fonts] could not read " + dir + ": " + ex.Message);
            }
            return faces;
        }

        /// <summary>
        /// The family and style names from a font file's OpenType <c>name</c> table - the
        /// typographic ones (IDs 16 and 17) where present, else the legacy ones (1 and 2), in
        /// Windows' US English record. For a collection (.ttc), the first font.
        /// </summary>
        private static bool TryReadNames(string path, out string family, out string style)
        {
            family = style = null;
            try
            {
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    long start = 0;
                    if (U32(r) == 0x74746366)            // 'ttcf'
                    {
                        U32(r);                          // version
                        U32(r);                          // number of fonts
                        start = U32(r);
                    }
                    r.BaseStream.Seek(start + 4, SeekOrigin.Begin);
                    int tables = U16(r);
                    r.BaseStream.Seek(start + 12, SeekOrigin.Begin);

                    long nameAt = -1;
                    for (int i = 0; i < tables; i++)
                    {
                        uint tag = U32(r);
                        U32(r);                          // checksum
                        uint offset = U32(r);
                        U32(r);                          // length
                        if (tag == 0x6E616D65) nameAt = offset;   // 'name'
                    }
                    if (nameAt < 0) return false;

                    r.BaseStream.Seek(nameAt, SeekOrigin.Begin);
                    U16(r);                              // format
                    int count = U16(r);
                    int strings = U16(r);

                    string f1 = null, f16 = null, s2 = null, s17 = null;
                    for (int i = 0; i < count; i++)
                    {
                        int platform = U16(r), encoding = U16(r), language = U16(r), id = U16(r);
                        int length = U16(r), offset = U16(r);
                        if (platform != 3 || (language != 0x409 && language != 0)) continue;
                        if (id != 1 && id != 2 && id != 16 && id != 17) continue;

                        long back = r.BaseStream.Position;
                        r.BaseStream.Seek(nameAt + strings + offset, SeekOrigin.Begin);
                        string value = Encoding.BigEndianUnicode.GetString(r.ReadBytes(length));
                        r.BaseStream.Seek(back, SeekOrigin.Begin);

                        if (id == 1) f1 = value;
                        else if (id == 2) s2 = value;
                        else if (id == 16) f16 = value;
                        else s17 = value;
                    }

                    family = f16 ?? f1;
                    style = s17 ?? s2 ?? "Regular";
                    return !string.IsNullOrEmpty(family);
                }
            }
            catch { return false; }
        }

        private static uint U32(BinaryReader r)
        {
            var b = r.ReadBytes(4);
            return b.Length < 4 ? 0 : (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        }

        private static int U16(BinaryReader r)
        {
            var b = r.ReadBytes(2);
            return b.Length < 2 ? 0 : b[0] << 8 | b[1];
        }
    }
}
