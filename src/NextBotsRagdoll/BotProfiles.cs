using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace NextBotsRagdoll
{
    /// <summary>
    /// How one bot hits. Read from <c>plugins/NextBotsRagdoll/bots/&lt;NAME&gt;.json</c>; any field
    /// left out keeps its default, so a file holding only <c>{"weight": 300}</c> is valid.
    /// </summary>
    [Serializable]
    public class BotProfile
    {
        /// <summary>Kilograms. 80 is an average person and hits exactly as hard as the config says.</summary>
        public float weight = BotProfiles.ReferenceWeight;

        /// <summary>Extra multiplier on top of whatever the weight gives, for bots that should
        /// hit harder or softer than they weigh.</summary>
        public float knockback = 1f;

        /// <summary>Upward launch, m/s. Negative = the config's Lift, scaled by weight.</summary>
        public float lift = -1f;

        /// <summary>Tumble, rad/s. Negative = the config's Tumble, scaled by weight.</summary>
        public float tumble = -1f;

        /// <summary>Seconds you stay down. Negative = the config's DownSeconds.</summary>
        public float downSeconds = -1f;

        /// <summary>Folder under <c>sounds/</c> played when this bot hits you. Empty = <c>hit</c>.</summary>
        public string hitSound = "";

        [NonSerialized] public string Name = "";

        /// <summary>
        /// How much harder than an 80 kg bot this one hits.
        ///
        /// <para>Square root of the weight ratio, because that is how the energy works out: a bot
        /// twice as heavy delivers twice the energy, and launch speed goes with the square root of
        /// energy. It also means weights stay sane to tune - 320 kg hits twice as hard, 20 kg
        /// half as hard - instead of a 1000 kg bot firing you across the map.</para>
        /// </summary>
        public float Scale =>
            Mathf.Clamp(Mathf.Sqrt(Mathf.Max(1f, weight) / BotProfiles.ReferenceWeight), 0.25f, 4f) *
            Mathf.Max(0f, knockback);

        /// <summary>Heavier bots sound deeper. Gentle on purpose: past this range a thud stops
        /// sounding like a thud and starts sounding like a tape machine.</summary>
        public float Pitch =>
            Mathf.Clamp(Mathf.Pow(BotProfiles.ReferenceWeight / Mathf.Max(1f, weight), 0.15f), 0.75f, 1.3f);
    }

    /// <summary>
    /// Per-bot weight files. Kept in the bridge's own folder rather than beside the skin PNGs:
    /// NextBots warns about every file in its skins folder that is not an image or a WAV, and
    /// a .json there would read as a broken skin in the log.
    ///
    /// <para>Files are re-read whenever they change on disk, so weights can be tuned with the
    /// game running - edit, save, get caught again.</para>
    /// </summary>
    public static class BotProfiles
    {
        public const float ReferenceWeight = 80f;

        public static string Directory =>
            Path.Combine(Paths.PluginPath, Path.Combine("NextBotsRagdoll", "bots"));

        private struct Entry
        {
            public BotProfile Profile;
            public DateTime Stamp;
        }

        private static readonly Dictionary<string, Entry> Cache =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static bool _templatesWritten;

        /// <summary>The profile for a bot by skin name, or the default one if it has no file.</summary>
        public static BotProfile Get(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "?") return Default(name);

            string path = Path.Combine(Directory, name + ".json");
            DateTime stamp;
            try
            {
                if (!File.Exists(path)) return Default(name);
                stamp = File.GetLastWriteTimeUtc(path);
            }
            catch { return Default(name); }

            Entry e;
            if (Cache.TryGetValue(name, out e) && e.Stamp == stamp) return e.Profile;

            var profile = new BotProfile { Name = name };
            try
            {
                JsonUtility.FromJsonOverwrite(File.ReadAllText(path), profile);
                profile.Name = name;
                Plugin.Log.LogInfo("[Bots] profile '" + name + "': " + profile.weight.ToString("0") + " kg" +
                                   " -> hits x" + profile.Scale.ToString("0.00"));
            }
            catch (Exception ex)
            {
                // A typo in one file must not take the catch down with it.
                Plugin.Log.LogWarning("[Bots] '" + path + "' is not valid JSON (" + ex.Message + "); using defaults");
                profile = Default(name);
            }

            Cache[name] = new Entry { Profile = profile, Stamp = stamp };
            return profile;
        }

        private static BotProfile Default(string name) => new BotProfile { Name = name ?? "" };

        /// <summary>
        /// Writes a starter file for every skin that has none, so the folder shows what can be
        /// tuned instead of starting empty. Never overwrites.
        /// </summary>
        public static void EnsureTemplates(IReadOnlyList<string> skinNames)
        {
            if (_templatesWritten || skinNames == null) return;
            _templatesWritten = true;

            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                int made = 0;
                for (int i = 0; i < skinNames.Count; i++)
                {
                    var name = skinNames[i];
                    if (string.IsNullOrEmpty(name)) continue;
                    var path = Path.Combine(Directory, name + ".json");
                    if (File.Exists(path)) continue;
                    File.WriteAllText(path, JsonUtility.ToJson(new BotProfile(), true));
                    made++;
                }

                var readme = Path.Combine(Directory, "README.txt");
                if (!File.Exists(readme)) File.WriteAllText(readme, Readme);

                if (made > 0)
                    Plugin.Log.LogInfo("[Bots] wrote " + made + " starter profile(s) to " + Directory);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Bots] could not write profile templates: " + ex.Message);
            }
        }

        private const string Readme =
@"Per-bot weights for NextBotsRagdoll
====================================

One file per bot, named after the skin (the PNG's name, any case): OBUNGA.json
Every field is optional - leave one out and it keeps its default.

  weight       kg. 80 = normal. Knockback scales with the square root:
               20 kg hits x0.5, 80 kg x1, 320 kg x2, 720 kg x3 (capped at x4).
               Heavier bots also sound deeper and shake the controllers harder.
  knockback    extra multiplier on top of the weight. 1 = none.
  lift         upward launch in m/s. -1 = the config's Lift, scaled by weight.
  tumble       spin in rad/s. -1 = the config's Tumble, scaled by weight.
  downSeconds  how long this bot keeps you down. -1 = the config's DownSeconds.
  hitSound     folder under ../sounds/ to play when this bot hits you. """" = hit.

Example - a heavy bot with its own hit sound:

  { ""weight"": 300, ""hitSound"": ""hit_heavy"", ""downSeconds"": 6 }

Files are re-read when they change, so you can tune with the game running.
Everyone in the lobby should have the same files: the knockback is worked out on the
caught player's own machine, using their copy.
";
    }
}
