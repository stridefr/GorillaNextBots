using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace NextBots.Runtime
{
    /// <summary>
    /// Spawnable bot types, loaded from PNGs on disk rather than from an asset bundle - so
    /// adding a nextbot is dropping an image in a folder, which is how Garry's Mod nextbots
    /// have always worked, and it needs no Unity project to rebuild.
    ///
    /// Falls back to a generated placeholder so the mod is usable with an empty folder.
    /// </summary>
    public static class BotSkins
    {
        public class Skin
        {
            /// <summary>Upper-cased key: what the lobby matches skins by.</summary>
            public string Name;

            /// <summary>
            /// The image's file name without its extension, as the person who named it wrote it
            /// ("Obunga", not "OBUNGA"). What the death log shows.
            /// </summary>
            public string DisplayName;
            public Texture2D Texture;

            /// <summary>Billboard height in metres. Width follows the image aspect.</summary>
            public float Height = 2.0f;

            public float AspectWidthOverHeight = 1f;

            /// <summary>
            /// Chase loop for this bot, from a WAV of the same name beside the image.
            /// Null is fine - the bot is simply silent.
            /// </summary>
            public AudioClip Clip;

            /// <summary>Animation frames, when the source was a GIF. Null for still images.</summary>
            public Texture2D[] Frames;

            /// <summary>Per-frame hold time, matching <see cref="Frames"/>.</summary>
            public float[] FrameDelays;

            public bool Animated => Frames != null && Frames.Length > 1;
        }

        private static readonly List<Skin> _skins = new List<Skin>();
        private static bool _loaded;

        public static string SkinDirectory =>
            Path.Combine(Paths.PluginPath, Path.Combine("NextBots", "skins"));

        public static IReadOnlyList<Skin> All
        {
            get { EnsureLoaded(); return _skins; }
        }

        private static List<string> _names;

        /// <summary>
        /// Cached deliberately: the panel reads this once per frame plus once per visible
        /// row, and rebuilding the list each time was allocating several times a frame -
        /// which in VR shows up as GC hitches, not as a profiler curiosity.
        /// Invalidated by <see cref="Reload"/>.
        /// </summary>
        public static IReadOnlyList<string> Names
        {
            get
            {
                EnsureLoaded();
                if (_names != null) return _names;

                _names = new List<string>(_skins.Count);
                for (int i = 0; i < _skins.Count; i++) _names.Add(_skins[i].Name);
                return _names;
            }
        }

        public static Skin Get(int index)
        {
            EnsureLoaded();
            if (_skins.Count == 0) return null;
            if (index < 0) index = 0;
            if (index >= _skins.Count) index = _skins.Count - 1;
            return _skins[index];
        }

        /// <summary>Slot of the skin with this name, ignoring case; -1 if there is none.</summary>
        public static int IndexOf(string name)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(name)) return -1;
            for (int i = 0; i < _skins.Count; i++)
                if (string.Equals(_skins[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        /// <summary>The skin with this name, ignoring case; null if there is none.</summary>
        public static Skin Find(string name)
        {
            int i = IndexOf(name);
            return i >= 0 ? _skins[i] : null;
        }

        public static void Reload()
        {
            _loaded = false;
            _skins.Clear();
            _names = null;
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                Directory.CreateDirectory(SkinDirectory);

                // Unity's Texture2D.LoadImage decodes PNG and JPG only.
                var files = new List<string>();
                files.AddRange(Directory.GetFiles(SkinDirectory, "*.png"));
                files.AddRange(Directory.GetFiles(SkinDirectory, "*.jpg"));
                files.AddRange(Directory.GetFiles(SkinDirectory, "*.jpeg"));
                files.AddRange(Directory.GetFiles(SkinDirectory, "*.gif"));
                files.Sort(StringComparer.OrdinalIgnoreCase);

                // Anything else in the folder was almost certainly meant to be a skin, so say
                // so instead of ignoring it. Silently skipping a file the user deliberately
                // put there is the worst possible behaviour.
                foreach (var other in Directory.GetFiles(SkinDirectory))
                {
                    var ext = Path.GetExtension(other).ToLowerInvariant();
                    if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif") continue;
                    if (ext == ".wav") continue;      // per-skin chase loop, loaded alongside

                    Plugin.Log.LogWarning("Skin '" + Path.GetFileName(other) + "' ignored: " +
                                          ext + " is not supported. Convert it to PNG or JPG " +
                                          "(for an animated GIF, export a single frame).");
                }

                foreach (var file in files)
                {
                    try
                    {
                        // GIF needs our own decoder - Unity cannot read the format at all.
                        if (Path.GetExtension(file).ToLowerInvariant() == ".gif")
                        {
                            if (LoadGif(file)) continue;
                            Plugin.Log.LogWarning("Skin '" + Path.GetFileName(file) +
                                                  "' could not be decoded; skipped.");
                            continue;
                        }

                        var bytes = File.ReadAllBytes(file);
                        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                        {
                            name = Path.GetFileNameWithoutExtension(file),
                            filterMode = FilterMode.Bilinear,
                            wrapMode = TextureWrapMode.Clamp,
                            hideFlags = HideFlags.HideAndDontSave
                        };

                        if (!tex.LoadImage(bytes))
                        {
                            UnityEngine.Object.Destroy(tex);
                            Plugin.Log.LogWarning("Skin '" + file + "' is not a readable PNG; skipped.");
                            continue;
                        }

                        _skins.Add(new Skin
                        {
                            Name = tex.name.ToUpperInvariant(),
                            DisplayName = tex.name,
                            Texture = tex,
                            AspectWidthOverHeight = tex.height > 0 ? (float)tex.width / tex.height : 1f,
                            Clip = LoadClipBeside(file, tex.name)
                        });
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning("Failed to load skin '" + file + "': " + ex.Message);
                    }
                }

                Plugin.Log.LogInfo("Loaded " + _skins.Count + " skin(s) from " + SkinDirectory);
                for (int i = 0; i < _skins.Count; i++)
                    Plugin.Log.LogInfo("    " + (i + 1) + ". " + _skins[i].Name);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Skin load failed: " + ex);
            }

            if (_skins.Count == 0)
            {
                _skins.Add(BuildPlaceholder());
                Plugin.Log.LogInfo("No skins found - using the generated placeholder. " +
                                   "Drop PNGs in " + SkinDirectory + " and they become spawnable types.");
            }
        }

        /// <summary>
        /// Decode an animated GIF into a skin. The first frame is the still texture, so
        /// everything that does not care about animation keeps working unchanged.
        /// </summary>
        private static bool LoadGif(string file)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var gif = GifDecoder.Load(file);
            if (gif == null || gif.Frames.Count == 0) return false;

            var frames = new Texture2D[gif.Frames.Count];
            var delays = new float[gif.Frames.Count];
            for (int i = 0; i < gif.Frames.Count; i++)
            {
                frames[i] = gif.Frames[i].Texture;
                frames[i].name = name + "_" + i;
                delays[i] = gif.Frames[i].Delay;
            }

            _skins.Add(new Skin
            {
                Name = name.ToUpperInvariant(),
                DisplayName = name,
                Texture = frames[0],
                Frames = frames,
                FrameDelays = delays,
                AspectWidthOverHeight = gif.Height > 0 ? (float)gif.Width / gif.Height : 1f,
                Clip = LoadClipBeside(file, name)
            });

            Plugin.Log.LogInfo("    (animated: " + frames.Length + " frames)");
            return true;
        }

        /// <summary>
        /// A WAV sitting next to the image, same name. Only WAV: this install ships no
        /// UnityWebRequest modules, so OGG and MP3 cannot be decoded at runtime at all.
        /// </summary>
        private static AudioClip LoadClipBeside(string imagePath, string name)
        {
            var dir = Path.GetDirectoryName(imagePath);
            if (dir == null) return null;

            var wav = Path.Combine(dir, name + ".wav");
            if (!File.Exists(wav)) return null;

            var clip = WavLoader.Load(wav, name);
            if (clip != null) Plugin.Log.LogInfo("    (sound: " + Path.GetFileName(wav) + ")");
            return clip;
        }

        /// <summary>
        /// A generated stand-in so an empty skins folder still produces something visible and
        /// obviously a placeholder rather than an invisible bot that looks like a bug.
        /// </summary>
        private static Skin BuildPlaceholder()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "PLACEHOLDER",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var a = new Color32(20, 20, 24, 255);
            var b = new Color32(235, 60, 60, 255);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    pixels[y * size + x] = ((x / 8 + y / 8) % 2 == 0) ? a : b;

            tex.SetPixels32(pixels);
            tex.Apply(false, false);

            return new Skin { Name = "PLACEHOLDER", DisplayName = "Placeholder", Texture = tex, AspectWidthOverHeight = 1f };
        }
    }
}
