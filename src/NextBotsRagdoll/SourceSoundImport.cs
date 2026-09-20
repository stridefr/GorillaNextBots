using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace NextBotsRagdoll
{
    /// <summary>
    /// Fills empty sound folders from the player's own Garry's Mod or Half-Life 2 install, with
    /// the exact files those games play for a body.
    ///
    /// <para><b>Which files, exactly.</b> Read out of Garry's Mod's own sound scripts, not
    /// guessed: a ragdoll is surface property <c>flesh</c>, whose impacts are
    /// <c>Flesh.ImpactSoft</c> (body_medium_impact_soft 1, 2, 5, 6, 7), <c>Flesh.ImpactHard</c>
    /// (hard 1-6) and <c>Flesh.Break</c> (break 2-4); and a player dying is
    /// <c>Player.Death</c> (player/pl_pain 5-7). The bot connecting uses flesh_impact_hard 1-6.</para>
    ///
    /// <para><b>Where they are.</b> Inside <c>hl2_sound_misc_dir.vpk</c>, which Garry's Mod keeps
    /// in <c>sourceengine</c> and Half-Life 2 in <c>hl2</c>. Steam libraries are found from the
    /// one Gorilla Tag itself runs from, Steam's <c>libraryfolders.vdf</c>, and the usual folder
    /// names on each drive.</para>
    ///
    /// <para>Nothing is downloaded or shipped, a folder that already has any WAV in it is never
    /// touched, and any failure just leaves the folder empty - it is a convenience, not
    /// something the mod depends on.</para>
    /// </summary>
    public static class SourceSoundImport
    {
        private static readonly Dictionary<string, string[]> Sets = new Dictionary<string, string[]>
        {
            { ImpactSounds.Soft, Files("physics/body/body_medium_impact_soft{0}.wav", 1, 2, 5, 6, 7) },
            { ImpactSounds.Hard, Files("physics/body/body_medium_impact_hard{0}.wav", 1, 2, 3, 4, 5, 6) },
            { ImpactSounds.Break, Files("physics/body/body_medium_break{0}.wav", 2, 3, 4) },
            { ImpactSounds.Hit, Files("physics/flesh/flesh_impact_hard{0}.wav", 1, 2, 3, 4, 5, 6) },
            { ImpactSounds.Death, Files("player/pl_pain{0}.wav", 5, 6, 7) },

            // Counter-Strike's flashbang, which Garry's Mod keeps in content_cstrike_dir.vpk: the
            // bang that comes just before the ringing. The ringing itself is not a file in any Source game.
            { ImpactSounds.Concussion, Files("weapons/flashbang/flashbang_explode{0}.wav", 1, 2) },
        };

        private static string[] Files(string pattern, params int[] n)
        {
            var list = new string[n.Length];
            for (int i = 0; i < n.Length; i++) list[i] = "sound/" + string.Format(pattern, n[i]);
            return list;
        }

        public static void FillEmptySets(string soundsDir)
        {
            try
            {
                var empty = new List<string>();
                foreach (var set in Sets.Keys)
                {
                    var dir = Path.Combine(soundsDir, set);
                    if (!Directory.Exists(dir) || Directory.GetFiles(dir, "*.wav").Length == 0) empty.Add(set);
                }
                if (empty.Count == 0) return;

                string vpk, game;
                if (!FindArchive(out vpk, out game))
                {
                    Plugin.Log.LogInfo("[Sound] no Garry's Mod or Half-Life 2 install found to import sounds from");
                    return;
                }

                var archive = Vpk.Open(vpk);

                // Some sets live in another archive beside the first one.
                Vpk strike = null;
                try
                {
                    var strikePath = Path.Combine(Path.GetDirectoryName(vpk), "content_cstrike_dir.vpk");
                    if (File.Exists(strikePath)) strike = Vpk.Open(strikePath);
                }
                catch { /* it is optional */ }

                int copied = 0;
                foreach (var set in empty)
                {
                    var dir = Path.Combine(soundsDir, set);
                    Directory.CreateDirectory(dir);
                    foreach (var file in Sets[set])
                    {
                        var data = archive.Read(file);
                        if (data == null && strike != null) data = strike.Read(file);
                        if (data == null) continue;
                        File.WriteAllBytes(Path.Combine(dir, Path.GetFileName(file)), data);
                        copied++;
                    }
                }
                Plugin.Log.LogInfo("[Sound] imported " + copied + " sounds into " + string.Join(", ", empty) +
                                   " from " + game + " (" + vpk + ")");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Sound] sound import skipped: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ finding the games

        private static bool FindArchive(out string vpk, out string game)
        {
            foreach (var lib in SteamLibraries())
            {
                var common = Path.Combine(lib, Path.Combine("steamapps", "common"));
                var gmod = Path.Combine(common, Path.Combine("GarrysMod", Path.Combine("sourceengine", "hl2_sound_misc_dir.vpk")));
                if (File.Exists(gmod)) { vpk = gmod; game = "Garry's Mod"; return true; }
                var hl2 = Path.Combine(common, Path.Combine("Half-Life 2", Path.Combine("hl2", "hl2_sound_misc_dir.vpk")));
                if (File.Exists(hl2)) { vpk = hl2; game = "Half-Life 2"; return true; }
            }
            vpk = game = null;
            return false;
        }

        private static IEnumerable<string> SteamLibraries()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new List<string>();

            // The library Gorilla Tag itself is in: <lib>/steamapps/common/Gorilla Tag/Gorilla Tag_Data
            try
            {
                var data = new DirectoryInfo(Application.dataPath);
                var steamapps = data.Parent != null && data.Parent.Parent != null ? data.Parent.Parent.Parent : null;
                if (steamapps != null && steamapps.Parent != null) roots.Add(steamapps.Parent.FullName);
            }
            catch { /* odd install layout */ }

            roots.Add(@"C:\Program Files (x86)\Steam");
            roots.Add(@"C:\Program Files\Steam");
            for (char d = 'C'; d <= 'H'; d++)
            {
                roots.Add(d + @":\SteamLibrary");
                roots.Add(d + @":\Steam");
                roots.Add(d + @":\Program Files (x86)\Steam");
                roots.Add(d + @":\Games\Steam");
            }

            // Every library Steam itself knows about, from any root that has the list.
            var fromVdf = new List<string>();
            foreach (var root in roots)
            {
                try
                {
                    var vdf = Path.Combine(root, Path.Combine("steamapps", "libraryfolders.vdf"));
                    if (!File.Exists(vdf)) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        fromVdf.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
                catch { /* unreadable, skip */ }
            }
            roots.AddRange(fromVdf);

            foreach (var r in roots)
            {
                bool exists;
                try { exists = Directory.Exists(r); } catch { exists = false; }
                if (exists && seen.Add(r)) yield return r;
            }
        }

        // ------------------------------------------------------------------ VPK

        /// <summary>
        /// Just enough of Valve's VPK format (versions 1 and 2) to pull a few files out: the
        /// directory tree is extension, then path, then file name, each list ended by an empty
        /// string, and each file's entry says which numbered archive holds its bytes.
        /// </summary>
        private sealed class Vpk
        {
            private struct Entry
            {
                public ushort Archive;
                public uint Offset, Length;
                public byte[] Preload;
            }

            private readonly string _dirPath;
            private readonly long _dataStart;
            private readonly Dictionary<string, Entry> _entries =
                new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

            private Vpk(string dirPath, long dataStart)
            {
                _dirPath = dirPath;
                _dataStart = dataStart;
            }

            public static Vpk Open(string dirPath)
            {
                using (var r = new BinaryReader(File.OpenRead(dirPath)))
                {
                    if (r.ReadUInt32() != 0x55AA1234) throw new Exception("not a VPK: " + dirPath);
                    uint version = r.ReadUInt32();
                    uint treeSize = r.ReadUInt32();
                    int header = 12;
                    if (version == 2) { r.ReadBytes(16); header = 28; }

                    var vpk = new Vpk(dirPath, header + treeSize);
                    while (true)
                    {
                        string ext = CString(r);
                        if (ext.Length == 0) break;
                        while (true)
                        {
                            string path = CString(r);
                            if (path.Length == 0) break;
                            while (true)
                            {
                                string name = CString(r);
                                if (name.Length == 0) break;

                                r.ReadUInt32();                       // CRC
                                ushort preload = r.ReadUInt16();
                                var e = new Entry
                                {
                                    Archive = r.ReadUInt16(),
                                    Offset = r.ReadUInt32(),
                                    Length = r.ReadUInt32()
                                };
                                r.ReadUInt16();                       // terminator
                                e.Preload = preload > 0 ? r.ReadBytes(preload) : null;

                                string dir = path.Trim();
                                string full = (dir.Length == 0 ? "" : dir + "/") + name + "." + ext;
                                vpk._entries[full] = e;
                            }
                        }
                    }
                    return vpk;
                }
            }

            public byte[] Read(string file)
            {
                Entry e;
                if (!_entries.TryGetValue(file, out e)) return null;

                string source = e.Archive == 0x7FFF
                    ? _dirPath
                    : _dirPath.Substring(0, _dirPath.Length - "_dir.vpk".Length) + "_" + e.Archive.ToString("000") + ".vpk";
                long at = e.Archive == 0x7FFF ? _dataStart + e.Offset : e.Offset;

                int pre = e.Preload != null ? e.Preload.Length : 0;
                var result = new byte[pre + e.Length];
                if (pre > 0) Buffer.BlockCopy(e.Preload, 0, result, 0, pre);
                if (e.Length > 0)
                {
                    using (var f = File.OpenRead(source))
                    {
                        f.Seek(at, SeekOrigin.Begin);
                        int got = 0;
                        while (got < e.Length)
                        {
                            int n = f.Read(result, pre + got, (int)e.Length - got);
                            if (n <= 0) throw new EndOfStreamException(file);
                            got += n;
                        }
                    }
                }
                return result;
            }

            private static string CString(BinaryReader r)
            {
                var sb = new StringBuilder(32);
                while (true)
                {
                    byte b = r.ReadByte();
                    if (b == 0) return sb.ToString();
                    sb.Append((char)b);
                }
            }
        }
    }
}
