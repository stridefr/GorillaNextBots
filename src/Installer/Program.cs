using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GorillaNextBots.Installer
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int pid);

        [STAThread]
        private static int Main(string[] args)
        {
            foreach (var a in args)
            {
                var flag = a.TrimStart('-', '/').ToLowerInvariant();
                if (flag == "silent" || flag == "s" || flag == "update") return Silent(args);
                if (flag == "help" || flag == "?" || flag == "h") return Help();
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        private static int Help()
        {
            AttachConsole(-1);
            Console.WriteLine();
            Console.WriteLine("GorillaNextBotsInstaller");
            Console.WriteLine("  (no arguments)            open the window");
            Console.WriteLine("  --silent [\"game dir\"]     update and exit, printing what it did");
            Console.WriteLine();
            return 0;
        }

        /// <summary>
        /// Update with no window: for a shortcut, a scheduled task, or anyone who would rather
        /// type. Exit code 0 means updated or already current, 1 means it could not.
        /// </summary>
        private static int Silent(string[] args)
        {
            AttachConsole(-1);
            Console.WriteLine();
            try
            {
                string game = null;
                foreach (var a in args)
                    if (!a.StartsWith("-") && !a.StartsWith("/") && Work.IsGameFolder(a)) game = a;
                game = game ?? Work.FindGame();

                if (!Work.IsGameFolder(game))
                {
                    Console.WriteLine("Could not find Gorilla Tag. Pass the folder: --silent \"D:\\Games\\Gorilla Tag\"");
                    return 1;
                }
                if (Work.GameRunning())
                {
                    Console.WriteLine("Gorilla Tag is running - close it first.");
                    return 1;
                }

                var installed = Work.Installed(game);
                var release = Work.Latest(Work.Repo);
                Console.WriteLine("game     " + game);
                Console.WriteLine("latest   " + release.Tag + (installed != null ? "   (you have " + installed + ")" : ""));

                if (release.Tag == installed)
                {
                    Console.WriteLine("Already up to date.");
                    return 0;
                }

                if (!Work.HasBepInEx(game)) Work.InstallBepInEx(game, Console.WriteLine);
                Work.Install(game, release, new List<string>(Work.Mods), Console.WriteLine);
                Console.WriteLine("Updated to " + release.Tag + ".");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed: " + ex.Message);
                return 1;
            }
        }
    }
}
