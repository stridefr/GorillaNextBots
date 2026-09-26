using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace GorillaNextBots.Shared
{
    /// <summary>
    /// Files carried inside a mod's DLL (embedded resources named <c>bundle/relative/path</c>) and
    /// unpacked into its plugin folder at start-up - sounds, pictures, fonts, styles. Carrying them in
    /// the DLL means anything that installs the DLL installs them too, including updaters that only
    /// ever copy DLLs.
    ///
    /// <para>Unpacked when the set carried differs from the set last unpacked (a stamp file records
    /// which), so a new build brings its files once, and the player's own edits stay until the next
    /// build that ships different ones.</para>
    /// </summary>
    internal static class AssetBundle
    {
        private const string Prefix = "bundle/";

        /// <summary>
        /// Starting settings for a new install: each <c>defaults/NAME</c> resource is written to
        /// <paramref name="folder"/> only when no file of that name is there yet, so an existing
        /// player's own settings are never touched. Returns how many were written.
        /// </summary>
        public static int InstallDefaults(Assembly asm, string folder, Action<string> log)
        {
            int written = 0;
            try
            {
                foreach (var n in asm.GetManifestResourceNames())
                {
                    if (!n.StartsWith("defaults/", StringComparison.Ordinal)) continue;
                    string name = Path.GetFileName(n.Substring("defaults/".Length));
                    if (name.Length == 0) continue;
                    string path = Path.Combine(folder, name);
                    if (File.Exists(path)) continue;
                    Directory.CreateDirectory(folder);
                    using (var s = asm.GetManifestResourceStream(n))
                    using (var f = File.Create(path))
                        s.CopyTo(f);
                    written++;
                    log?.Invoke("[Bundle] new install: starting settings written to " + name);
                }
            }
            catch (Exception ex) { log?.Invoke("[Bundle] could not write starting settings: " + ex.Message); }
            return written;
        }

        /// <summary>Unpacks this DLL's bundled files under <paramref name="folder"/>. Returns how many
        /// were written; 0 when already up to date or when the DLL carries none.</summary>
        public static int Unpack(Assembly asm, string folder, Action<string> log)
        {
            try
            {
                var names = Array.FindAll(asm.GetManifestResourceNames(),
                                          n => n.StartsWith(Prefix, StringComparison.Ordinal));
                if (names.Length == 0) return 0;
                Array.Sort(names, StringComparer.Ordinal);

                var sig = new StringBuilder();
                foreach (var n in names)
                    using (var s = asm.GetManifestResourceStream(n))
                        sig.Append(n).Append(':').Append(s != null ? s.Length : -1).Append(';');
                string stamp = Path.Combine(folder, "bundle.stamp");
                string want = sig.ToString().GetHashCode().ToString("X8") + " " + names.Length;
                if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == want) return 0;

                int written = 0;
                foreach (var n in names)
                {
                    string rel = n.Substring(Prefix.Length).Replace('\\', '/');
                    if (rel.Length == 0 || rel.Contains("..")) continue;
                    string path = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var s = asm.GetManifestResourceStream(n))
                    using (var f = File.Create(path))
                        s.CopyTo(f);
                    written++;
                }
                Directory.CreateDirectory(folder);
                File.WriteAllText(stamp, want);
                log?.Invoke("[Bundle] unpacked " + written + " file(s) into " + folder);
                return written;
            }
            catch (Exception ex)
            {
                log?.Invoke("[Bundle] could not unpack: " + ex.Message);
                return 0;
            }
        }
    }
}
