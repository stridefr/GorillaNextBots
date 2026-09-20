using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GorillaNextBots.Installer
{
    /// <summary>
    /// One window: where the game is, what you have, what GitHub has, and a button that puts the
    /// newest one in. Everything slow runs off the UI thread so the window never locks up.
    /// </summary>
    public sealed class MainForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(14, 16, 22);
        private static readonly Color Panel = Color.FromArgb(22, 26, 36);
        private static readonly Color Line = Color.FromArgb(38, 44, 58);
        private static readonly Color Ink = Color.FromArgb(232, 236, 248);
        private static readonly Color Dim = Color.FromArgb(139, 148, 173);
        private static readonly Color Accent = Color.FromArgb(255, 90, 74);
        private static readonly Color Ok = Color.FromArgb(74, 222, 128);

        private readonly TextBox _path = new TextBox();
        private readonly Label _status = new Label();
        private readonly CheckedListBox _mods = new CheckedListBox();
        private readonly CheckBox _bepinex = new CheckBox();
        private readonly Button _install = new Button();
        private readonly Button _browse = new Button();
        private readonly Button _folder = new Button();
        private readonly TextBox _log = new TextBox();
        private readonly ProgressBar _bar = new ProgressBar();

        private string _game;
        private Work.Release _latest;

        public MainForm()
        {
            Text = "GorillaNextBots Installer";
            BackColor = Bg;
            ForeColor = Ink;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(560, 460);
            MinimumSize = new Size(480, 420);
            StartPosition = FormStartPosition.CenterScreen;

            var title = new Label
            {
                Text = "GorillaNextBots",
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                ForeColor = Ink, AutoSize = true, Location = new Point(16, 12)
            };
            var subtitle = new Label
            {
                Text = "Nextbots and ragdolls for Gorilla Tag, by stridefr",
                ForeColor = Dim, AutoSize = true, Location = new Point(18, 42)
            };

            var pathLabel = new Label { Text = "Gorilla Tag folder", ForeColor = Dim, AutoSize = true, Location = new Point(16, 74) };
            _path.SetBounds(16, 94, 430, 24);
            _path.ReadOnly = true;
            _path.BackColor = Panel;
            _path.ForeColor = Ink;
            _path.BorderStyle = BorderStyle.FixedSingle;
            _path.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            Style(_browse, "Browse", 452, 93, 92, 26);
            _browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browse.Click += (s, e) => Browse();

            _status.SetBounds(16, 126, 528, 22);
            _status.ForeColor = Dim;
            _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            var modsLabel = new Label { Text = "Install", ForeColor = Dim, AutoSize = true, Location = new Point(16, 152) };
            _mods.SetBounds(16, 172, 270, 70);
            _mods.BackColor = Panel;
            _mods.ForeColor = Ink;
            _mods.BorderStyle = BorderStyle.FixedSingle;
            _mods.CheckOnClick = true;
            foreach (var mod in Work.Mods) _mods.Items.Add(mod, true);

            _bepinex.SetBounds(296, 172, 250, 24);
            _bepinex.Text = "Install BepInEx if missing";
            _bepinex.Checked = true;
            _bepinex.ForeColor = Ink;
            _bepinex.FlatStyle = FlatStyle.Flat;

            Style(_install, "Install / Update", 296, 204, 160, 38, accent: true);
            _install.Click += async (s, e) => await Run();

            Style(_folder, "Open folder", 462, 204, 82, 38);
            _folder.Click += (s, e) =>
            {
                if (Work.IsGameFolder(_game)) System.Diagnostics.Process.Start("explorer.exe", "\"" + _game + "\"");
            };

            _bar.SetBounds(16, 252, 528, 6);
            _bar.Style = ProgressBarStyle.Marquee;
            _bar.MarqueeAnimationSpeed = 0;
            _bar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _log.SetBounds(16, 268, 528, 176);
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = Panel;
            _log.ForeColor = Dim;
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.Font = new Font("Consolas", 8.5f);
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            Controls.AddRange(new Control[] { title, subtitle, pathLabel, _path, _browse, _status,
                                              modsLabel, _mods, _bepinex, _install, _folder, _bar, _log });

            Load += async (s, e) => await Start();
        }

        private void Style(Button b, string text, int x, int y, int w, int h, bool accent = false)
        {
            b.Text = text;
            b.SetBounds(x, y, w, h);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = accent ? Accent : Line;
            b.BackColor = accent ? Accent : Panel;
            b.ForeColor = accent ? Color.White : Ink;
            if (accent) b.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            b.Cursor = Cursors.Hand;
        }

        // ------------------------------------------------------------------ actions

        private async Task Start()
        {
            _game = Work.FindGame();
            _path.Text = _game ?? "not found - click Browse";
            if (_game == null)
                Log("Could not find Gorilla Tag. Click Browse and pick the folder with Gorilla Tag.exe.");
            await Check();
        }

        private void Browse()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Pick your Gorilla Tag folder" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (!Work.IsGameFolder(dlg.SelectedPath))
                {
                    MessageBox.Show(this, "That folder has no Gorilla Tag.exe in it.", "Not the game folder",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _game = dlg.SelectedPath;
                _path.Text = _game;
                Work.SaveSetting("gamedir", _game);
                _ = Check();
            }
        }

        private async Task Check()
        {
            Busy(true);
            string installed = _game != null ? Work.Installed(_game) : null;
            try
            {
                _latest = await Task.Run(() => Work.Latest(Work.Repo));
                string have = installed ?? "nothing yet";
                if (_latest.Tag == installed)
                {
                    SetStatus("Up to date: " + installed, Ok);
                }
                else
                {
                    SetStatus("Installed: " + have + "    Latest: " + _latest.Tag, Ink);
                    if (!string.IsNullOrEmpty(_latest.Notes)) Log(FirstLines(_latest.Notes, 3));
                }
            }
            catch (Exception ex)
            {
                SetStatus("Could not reach GitHub", Accent);
                Log(ex.Message);
                if (ex.Message.Contains("404"))
                    Log("If the repo is private, put a GitHub token in " +
                        System.IO.Path.Combine(Work.SettingsDir, "token.txt") + " and try again.");
            }
            finally { Busy(false); }
        }

        private async Task Run()
        {
            if (!Work.IsGameFolder(_game)) { Browse(); return; }
            if (Work.GameRunning())
            {
                MessageBox.Show(this, "Close Gorilla Tag first - Windows will not let the mods be replaced while it is running.",
                                "Game is running", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var mods = new List<string>();
            foreach (var item in _mods.CheckedItems) mods.Add(item.ToString());
            if (mods.Count == 0) { Log("Nothing ticked."); return; }
            if (mods.Contains("NextBotsRagdoll") && !(mods.Contains("NextBots") && mods.Contains("GorillaRagdoll")))
                Log("Note: NextBotsRagdoll only does anything with both of the other two installed.");

            Busy(true);
            try
            {
                bool bepinex = _bepinex.Checked && !Work.HasBepInEx(_game);
                var release = _latest ?? await Task.Run(() => Work.Latest(Work.Repo));
                await Task.Run(() =>
                {
                    if (bepinex) Work.InstallBepInEx(_game, Log);
                    Work.Install(_game, release, mods, Log);
                });
                _latest = release;
                SetStatus("Installed " + release.Tag + " - start Gorilla Tag", Ok);
                Log("Done. Bot images go in BepInEx\\plugins\\NextBots\\skins.");
            }
            catch (Exception ex)
            {
                SetStatus("Install failed", Accent);
                Log(ex.Message);
            }
            finally { Busy(false); }
        }

        // ------------------------------------------------------------------ plumbing

        private void Busy(bool on)
        {
            _install.Enabled = !on;
            _browse.Enabled = !on;
            _bar.MarqueeAnimationSpeed = on ? 30 : 0;
        }

        private void SetStatus(string text, Color colour)
        {
            _status.Text = text;
            _status.ForeColor = colour;
        }

        private void Log(string line)
        {
            if (InvokeRequired) { BeginInvoke((Action<string>)Log, line); return; }
            _log.AppendText(line + Environment.NewLine);
        }

        private static string FirstLines(string text, int count)
        {
            var lines = text.Replace("\r", "").Split('\n');
            var take = Math.Min(count, lines.Length);
            return string.Join(Environment.NewLine, lines, 0, take).Trim();
        }
    }
}
