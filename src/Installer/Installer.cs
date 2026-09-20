using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace GorillaNextBots.Installer
{
    /// <summary>
    /// Everything the installer actually does: find the game, ask GitHub what the newest release
    /// is, and put the mods in place. No UI in here, so the window and the silent command line
    /// run exactly the same code.
    /// </summary>
    public static class Work
    {
        public const string Repo = "stridefr/GorillaNextBots";
        private const string BepInExRepo = "BepInEx/BepInEx";

        /// <summary>The mods this installs, and the plugin folder each one lives in.</summary>
        public static readonly string[] Mods = { "NextBots", "GorillaRagdoll", "NextBotsRagdoll" };

        public static string SettingsDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GorillaNextBots");

        // ------------------------------------------------------------------ the game

        /// <summary>
        /// Gorilla Tag's folder: the one remembered from last time, else every Steam library
        /// Steam knows about, else the usual folder names on each drive.
        /// </summary>
        public static string FindGame()
        {
            var saved = LoadSetting("gamedir");
            if (IsGameFolder(saved)) return saved;

            var roots = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Steam",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Steam",
            };
            for (char d = 'C'; d <= 'H'; d++)
                roots.AddRange(new[] { d + @":\SteamLibrary", d + @":\Steam", d + @":\Games\Steam" });

            // Steam's own list of libraries, wherever we can find it.
            var extra = new List<string>();
            foreach (var root in roots)
            {
                try
                {
                    var vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
                    if (!File.Exists(vdf)) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        extra.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
                catch { /* unreadable: skip */ }
            }
            roots.AddRange(extra);

            foreach (var root in roots)
            {
                var game = Path.Combine(root, @"steamapps\common\Gorilla Tag");
                if (IsGameFolder(game)) return game;
            }
            return null;
        }

        public static bool IsGameFolder(string dir) =>
            !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "Gorilla Tag.exe"));

        public static bool HasBepInEx(string game) =>
            File.Exists(Path.Combine(game, "winhttp.dll")) && Directory.Exists(Path.Combine(game, "BepInEx"));

        public static bool GameRunning()
        {
            try { return System.Diagnostics.Process.GetProcessesByName("Gorilla Tag").Length > 0; }
            catch { return false; }
        }

        /// <summary>The version this installer put there last time, or null.</summary>
        public static string Installed(string game)
        {
            try
            {
                var stamp = Path.Combine(game, @"BepInEx\plugins\GorillaNextBots.version");
                return File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : null;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ GitHub

        public class Release
        {
            public string Tag;
            public string Notes;
            public string AssetName;
            public string DownloadUrl;   // public link
            public string ApiUrl;        // works on a private repo, with a token
        }

        /// <summary>
        /// The newest release of a repo. A private repo needs a token; one saved next to the
        /// installer's settings is used if it is there.
        /// </summary>
        public static Release Latest(string repo)
        {
            var json = Get("https://api.github.com/repos/" + repo + "/releases/latest");
            var map = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);

            var release = new Release
            {
                Tag = map.ContainsKey("tag_name") ? map["tag_name"] as string : null,
                Notes = map.ContainsKey("body") ? map["body"] as string : null,
            };

            if (map.ContainsKey("assets") && map["assets"] is object[] assets)
            {
                foreach (var a in assets)
                {
                    var asset = a as Dictionary<string, object>;
                    var name = asset != null && asset.ContainsKey("name") ? asset["name"] as string : null;
                    if (name == null || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    release.AssetName = name;
                    release.DownloadUrl = asset.ContainsKey("browser_download_url") ? asset["browser_download_url"] as string : null;
                    release.ApiUrl = asset.ContainsKey("url") ? asset["url"] as string : null;
                    break;
                }
            }
            return release;
        }

        public static string Token
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                if (!string.IsNullOrEmpty(env)) return env.Trim();
                var saved = LoadSetting("token");
                return string.IsNullOrEmpty(saved) ? null : saved;
            }
        }

        private static string Get(string url)
        {
            try
            {
                using (var c = Client())
                    return c.DownloadString(url);
            }
            catch (WebException ex)
            {
                // A private repo answers 404 to anyone not signed in, exactly as a repo that does
                // not exist does - so say both, rather than leaving an HTTP code on screen.
                var http = ex.Response as HttpWebResponse;
                if (http != null && (int)http.StatusCode == 404)
                    throw new Exception("GitHub returned 'not found'. Either that release does not exist, " +
                                        "or the repo is private - in which case put a GitHub token in " +
                                        Path.Combine(SettingsDir, "token.txt") + " (or set GITHUB_TOKEN) and try again.");
                throw;
            }
        }

        private static WebClient Client()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var c = new WebClient();
            c.Headers.Add("User-Agent", "GorillaNextBots-Installer");
            c.Headers.Add("Accept", "application/vnd.github+json");
            var token = Token;
            if (!string.IsNullOrEmpty(token)) c.Headers.Add("Authorization", "Bearer " + token);
            return c;
        }

        // ------------------------------------------------------------------ installing

        /// <summary>
        /// Downloads a release and copies its DLLs into the game. Only the mod DLLs are written:
        /// settings, bot images, sounds and the death log's style are the player's own and are
        /// never touched.
        /// </summary>
        public static void Install(string game, Release release, IEnumerable<string> mods, Action<string> log)
        {
            if (release == null || release.AssetName == null) throw new Exception("that release has no zip attached");

            var wanted = new HashSet<string>(mods, StringComparer.OrdinalIgnoreCase);
            var temp = Path.Combine(Path.GetTempPath(), "GorillaNextBots-" + release.Tag);
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);

            var zip = Path.Combine(temp, release.AssetName);
            log("Downloading " + release.AssetName + " ...");
            Download(release, zip);

            ZipFile.ExtractToDirectory(zip, temp);

            var from = Path.Combine(temp, @"BepInEx\plugins");
            var to = Path.Combine(game, @"BepInEx\plugins");
            Directory.CreateDirectory(to);

            int copied = 0;
            foreach (var dll in Directory.GetFiles(from, "*.dll", SearchOption.AllDirectories))
            {
                var folder = Path.GetFileName(Path.GetDirectoryName(dll));
                if (!wanted.Contains(folder)) continue;
                var target = Path.Combine(to, folder);
                Directory.CreateDirectory(target);
                File.Copy(dll, Path.Combine(target, Path.GetFileName(dll)), true);
                log("  installed " + folder + @"\" + Path.GetFileName(dll));
                copied++;
            }
            if (copied == 0) throw new Exception("no mod DLLs found inside the zip");

            // The skins folder, so it is obvious where bot images go.
            Directory.CreateDirectory(Path.Combine(to, @"NextBots\skins"));

            File.WriteAllText(Path.Combine(to, "GorillaNextBots.version"), release.Tag);
            try { Directory.Delete(temp, true); } catch { /* the temp folder is not important */ }
        }

        private static void Download(Release release, string path)
        {
            var token = Token;
            using (var c = Client())
            {
                if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(release.ApiUrl))
                {
                    // On a private repo the public link is not public; the API one works.
                    c.Headers["Accept"] = "application/octet-stream";
                    c.DownloadFile(release.ApiUrl, path);
                }
                else c.DownloadFile(release.DownloadUrl, path);
            }
        }

        /// <summary>
        /// Installs BepInEx 5 itself, which everything else needs. Only ever called when the game
        /// has none: an existing install may be someone else's setup with other mods in it.
        /// </summary>
        public static void InstallBepInEx(string game, Action<string> log)
        {
            log("Fetching BepInEx ...");
            var json = Get("https://api.github.com/repos/" + BepInExRepo + "/releases/latest");
            var map = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);

            string url = null, name = null;
            if (map.ContainsKey("assets") && map["assets"] is object[] assets)
            {
                foreach (var a in assets)
                {
                    var asset = a as Dictionary<string, object>;
                    var n = asset != null && asset.ContainsKey("name") ? asset["name"] as string : null;
                    if (n == null) continue;
                    // The 64-bit Windows build; Gorilla Tag is x64 and BepInEx also ships Unix ones.
                    if (n.IndexOf("x64", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (n.IndexOf("unix", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (!n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    name = n;
                    url = asset.ContainsKey("browser_download_url") ? asset["browser_download_url"] as string : null;
                    break;
                }
            }
            if (url == null) throw new Exception("could not find a BepInEx download");

            var temp = Path.Combine(Path.GetTempPath(), "BepInEx-download");
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            Directory.CreateDirectory(temp);
            var zip = Path.Combine(temp, name);

            log("Downloading " + name + " ...");
            using (var c = Client()) c.DownloadFile(url, zip);
            ZipFile.ExtractToDirectory(zip, temp);

            foreach (var file in Directory.GetFiles(temp, "*", SearchOption.AllDirectories))
            {
                if (file == zip) continue;
                var rel = file.Substring(temp.Length + 1);
                var target = Path.Combine(game, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
            log("BepInEx installed. The first launch takes a little longer while it sets itself up.");
            try { Directory.Delete(temp, true); } catch { }
        }

        // ------------------------------------------------------------------ settings

        public static void SaveSetting(string key, string value)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(Path.Combine(SettingsDir, key + ".txt"), value ?? "");
            }
            catch { /* remembering is a convenience, not a requirement */ }
        }

        public static string LoadSetting(string key)
        {
            try
            {
                var path = Path.Combine(SettingsDir, key + ".txt");
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch { return null; }
        }
    }
}
