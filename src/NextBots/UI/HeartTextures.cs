using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace NextBots.UI
{
    /// <summary>
    /// The heart pictures for the lives display, in this order of preference:
    ///
    /// <list type="number">
    /// <item><c>full.png</c> and <c>empty.png</c> the player put in <c>plugins/NextBots/hearts</c>.</item>
    /// <item>Minecraft's own hearts, read from the player's own Minecraft install
    ///       (<c>%APPDATA%/.minecraft/versions</c>, newest version with the 1.20.2+ sprite layout).
    ///       Nothing of Minecraft's ships with the mod: they are only read from a copy the player
    ///       already has, and saved into the hearts folder so the next launch skips the search.</item>
    /// <item>Hearts drawn in code, when there is neither.</item>
    /// </list>
    ///
    /// <para>Minecraft draws a full heart as <c>full.png</c> on top of <c>container.png</c>, and a lost
    /// one as the container alone, so that is how the two are put together here. They are 9x9 pixel
    /// art, so they are drawn with point filtering - crisp, never blurred.</para>
    /// </summary>
    public static class HeartTextures
    {
        private const string SpriteDir = "assets/minecraft/textures/gui/sprites/hud/heart/";

        private static bool _tried;
        private static Texture2D _full, _empty;

        public static string Folder => Path.Combine(Paths.PluginPath, Path.Combine("NextBots", "hearts"));

        /// <summary>The full and empty heart, or false to draw them in code.</summary>
        public static bool TryGet(out Texture2D full, out Texture2D empty)
        {
            if (!_tried)
            {
                _tried = true;
                try { Load(); }
                catch (Exception ex) { Plugin.Log.LogWarning("[Lives] heart pictures: " + ex.Message); }
            }
            full = _full;
            empty = _empty;
            return _full != null && _empty != null;
        }

        private static void Load()
        {
            string fullPath = Path.Combine(Folder, "full.png");
            string emptyPath = Path.Combine(Folder, "empty.png");

            if (!File.Exists(fullPath) || !File.Exists(emptyPath)) ImportFromMinecraft(fullPath, emptyPath);
            if (!File.Exists(fullPath) || !File.Exists(emptyPath)) return;

            _full = Read(fullPath, "NextBots.HeartFull");
            _empty = Read(emptyPath, "NextBots.HeartEmpty");
            if (_full != null && _empty != null)
                Plugin.Log.LogInfo("[Lives] heart pictures from " + Folder);
        }

        /// <summary>Finds the player's own Minecraft, composes the two hearts the way Minecraft draws
        /// them, and saves them into the hearts folder.</summary>
        private static void ImportFromMinecraft(string fullPath, string emptyPath)
        {
            string versions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                           Path.Combine(".minecraft", "versions"));
            if (!Directory.Exists(versions)) return;

            var jars = Directory.GetDirectories(versions)
                .Select(d => Path.Combine(d, Path.GetFileName(d) + ".jar"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc);

            foreach (var jar in jars)
            {
                byte[] container, heart;
                using (var zip = ZipFile.OpenRead(jar))
                {
                    container = Entry(zip, SpriteDir + "container.png");
                    heart = Entry(zip, SpriteDir + "full.png");
                }
                if (container == null || heart == null) continue;

                var c = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                var h = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    if (!c.LoadImage(container) || !h.LoadImage(heart) || c.width != h.width || c.height != h.height) continue;

                    // Full: the heart laid over its container, as the game's own HUD does.
                    var cp = c.GetPixels32();
                    var hp = h.GetPixels32();
                    var full = new Color32[cp.Length];
                    for (int i = 0; i < cp.Length; i++) full[i] = Over(hp[i], cp[i]);
                    var composed = new Texture2D(c.width, c.height, TextureFormat.RGBA32, false);
                    composed.SetPixels32(full);
                    composed.Apply();

                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                    File.WriteAllBytes(fullPath, composed.EncodeToPNG());
                    File.WriteAllBytes(emptyPath, container);
                    UnityEngine.Object.Destroy(composed);
                    Plugin.Log.LogInfo("[Lives] heart pictures taken from your Minecraft (" + Path.GetFileName(jar) + ")");
                    return;
                }
                finally
                {
                    UnityEngine.Object.Destroy(c);
                    UnityEngine.Object.Destroy(h);
                }
            }
        }

        private static byte[] Entry(ZipArchive zip, string name)
        {
            var e = zip.GetEntry(name);
            if (e == null) return null;
            using (var s = e.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static Color32 Over(Color32 top, Color32 under)
        {
            float a = top.a / 255f, b = under.a / 255f;
            float outA = a + b * (1f - a);
            if (outA <= 0f) return new Color32(0, 0, 0, 0);
            byte Mix(byte t, byte u) => (byte)Mathf.RoundToInt((t * a + u * b * (1f - a)) / outA);
            return new Color32(Mix(top.r, under.r), Mix(top.g, under.g), Mix(top.b, under.b),
                               (byte)Mathf.RoundToInt(outA * 255f));
        }

        private static Texture2D Read(string path, string name)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave
            };
            if (!tex.LoadImage(File.ReadAllBytes(path))) { UnityEngine.Object.Destroy(tex); return null; }
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }
    }
}
