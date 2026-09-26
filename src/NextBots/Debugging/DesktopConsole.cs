using System.Text;
using NextBots.Config;
using NextBots.Runtime;
using UnityEngine;
using UnityEngine.AI;

namespace NextBots.Debugging
{
    /// <summary>
    /// An on-monitor control panel, for working on the mod without a headset on.
    ///
    /// Everything the wrist panel does, driven by mouse and keyboard - plus a live telemetry
    /// table for each bot (state, target, distance, path status, speed). That table is the
    /// point: without VR you cannot watch a bot hunt, but you can read whether its brain is
    /// choosing a target, whether it has a path, and whether it is actually closing distance.
    ///
    /// Works with the headset on too, as a second screen for whoever is at the desk.
    /// </summary>
    public class DesktopConsole : MonoBehaviour
    {
        public BotManager Bots;
        public NavMeshProvider Nav;
        public NextBotSettings Settings = NextBotSettings.Active;

        public KeyCode ToggleKey = KeyCode.F1;

        /// <summary>Hidden until the toggle key brings it up.</summary>
        public bool Visible = false;
        private Font _font;
        private bool _fontTried;

        private Rect _window = new Rect(16, 16, 460, 620);
        private Vector2 _scrollSettings;
        private Vector2 _scrollBots;
        private int _skinIndex;
        private string _message = "";
        private float _messageUntil;
        private GUIStyle _mono;

        private void Update()
        {
            if (UI.Keys.Down(ToggleKey)) Visible = !Visible;
        }


        private void OnGUI()
        {
            if (!Visible) return;

            // The mod's own font (Plugin's UI Font, Coolvetica by default), for this window only.
            if (!_fontTried)
            {
                _fontTried = true;
                _font = GorillaNextBots.Shared.FileFonts.Get(Plugin.CfgFont != null ? Plugin.CfgFont.Value : "",
                                                             m => Plugin.Log.LogInfo(m));
            }
            var saved = GUI.skin.font;
            if (_font != null) GUI.skin.font = _font;
            try
            {
                if (_mono == null)
                {
                    _mono = new GUIStyle(GUI.skin.label)
                    {
                        font = GUI.skin.font,
                        fontSize = 12,
                        wordWrap = false
                    };
                }

                _window = GUILayout.Window(GetInstanceID(), _window, DrawWindow,
                    "NextBots  " + Plugin.Version + "   (" + ToggleKey + " to hide)");
            }
            finally { GUI.skin.font = saved; }
        }

        private void DrawWindow(int id)
        {
            DrawStatus();
            GUILayout.Space(6);
            DrawSpawnControls();
            GUILayout.Space(6);
            DrawBotTable();
            GUILayout.Space(6);
            DrawSettings();

            if (Time.time < _messageUntil)
            {
                GUILayout.Space(4);
                GUILayout.Label(_message, _mono);
            }

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        // ------------------------------------------------------------------ status

        private void DrawStatus()
        {
            GUILayout.Label("VENUE   " + Venue.Describe(), _mono);

            var allowed = Venue.CanSpawn;
            GUILayout.Label("SPAWN   " + (allowed ? "allowed" : "BLOCKED - " + Venue.BlockedReason), _mono);

            GUILayout.Label("NAV     " + (Nav != null ? Nav.Describe() : "<no provider>"), _mono);

            var pos = LocalPos();
            GUILayout.Label("PLAYER  " + (pos.HasValue ? pos.Value.ToString("0.0") : "<no rig>") +
                            "   onNav=" + (pos.HasValue && NavMeshProvider.HasNavMeshNear(pos.Value, 2f)), _mono);
        }

        // ------------------------------------------------------------------ spawning

        private void DrawSpawnControls()
        {
            var types = Bots != null ? Bots.SpawnTypes : null;
            var count = types != null ? types.Count : 0;
            if (count > 0) _skinIndex = ((_skinIndex % count) + count) % count;

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(26)) && count > 0) _skinIndex--;
            GUILayout.Label(count > 0 ? types[_skinIndex] + "   (" + (_skinIndex + 1) + "/" + count + ")" : "<no skins>",
                            _mono, GUILayout.Width(200));
            if (GUILayout.Button(">", GUILayout.Width(26)) && count > 0) _skinIndex++;
            GUILayout.FlexibleSpace();
            GUILayout.Label("live " + (Bots != null ? Bots.LiveCount : 0), _mono);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Spawn where I look")) SpawnAtLook();
            if (GUILayout.Button("Spawn 8m ahead")) SpawnAhead(8f);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Undo"))
            {
                string msg;
                if (Bots != null) { Bots.UndoLast(out msg); Say(msg); }
            }
            if (GUILayout.Button("Clear all"))
            {
                if (Bots != null) Say("cleared " + Bots.ClearAll());
            }
            if (GUILayout.Button("Reload skins"))
            {
                BotSkins.Reload();
                Say("skins reloaded: " + BotSkins.All.Count);
            }
            GUILayout.EndHorizontal();
        }

        private void SpawnAtLook()
        {
            Vector3 origin, dir;
            if (!UI.SpawnAimer.TryGetViewerRay(out origin, out dir)) { Say("no viewer transform"); return; }

            RaycastHit hit;
            if (!Physics.Raycast(origin, dir, out hit, 40f, WorldView.SightMask, QueryTriggerInteraction.Ignore))
            {
                Say("nothing under the crosshair");
                return;
            }
            Place(hit.point);
        }

        private void SpawnAhead(float metres)
        {
            var viewer = UI.UiResources.Viewer;
            var pos = LocalPos();
            if (viewer == null || !pos.HasValue) { Say("no rig"); return; }

            var fwd = viewer.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
            Place(pos.Value + fwd.normalized * metres);
        }

        private void Place(Vector3 where)
        {
            if (Bots == null) { Say("no bot manager"); return; }

            // Snap onto the mesh so a desktop spawn behaves like an aimed one.
            NavMeshHit hit;
            if (NavMesh.SamplePosition(where, out hit, 6f, NavMesh.AllAreas)) where = hit.position;

            string msg;
            Bots.TrySpawn(_skinIndex, where, Quaternion.identity, out msg);
            Say(msg);
        }

        // ------------------------------------------------------------------ telemetry

        /// <summary>
        /// The bit that makes headless testing possible: what each brain is actually doing.
        /// </summary>
        private void DrawBotTable()
        {
            GUILayout.Label("BOTS", _mono);
            _scrollBots = GUILayout.BeginScrollView(_scrollBots, GUILayout.Height(130));

            if (Bots == null || Bots.LiveCount == 0)
            {
                GUILayout.Label("  (none)", _mono);
            }
            else
            {
                var player = LocalPos();
                var sb = new StringBuilder(128);

                for (int i = 0; i < Bots.Bots.Count; i++)
                {
                    var bot = Bots.Bots[i];
                    if (bot == null || bot.Brain == null) continue;

                    sb.Length = 0;
                    sb.Append(i).Append(' ').Append(bot.SkinName).Append("  ");
                    sb.Append(bot.Brain.State.ToString().ToUpperInvariant());
                    sb.Append(" tgt=").Append(bot.Brain.TargetActor);

                    if (player.HasValue)
                        sb.Append(" dist=").Append(Vector3.Distance(bot.Position, player.Value).ToString("0.0"));

                    sb.Append(" spd=").Append(bot.Velocity.magnitude.ToString("0.0"));
                    sb.Append("/").Append(bot.Speed.ToString("0.0"));
                    sb.Append(" stuck=").Append(bot.Brain.StuckTime.ToString("0.0"));
                    sb.Append(" tp=").Append(bot.Brain.HardResets);

                    if (bot.Agent != null)
                    {
                        sb.Append(" path=").Append(bot.Agent.hasPath ? bot.Agent.pathStatus.ToString() : "NONE");
                        if (bot.Agent.pathPending) sb.Append(" PENDING");
                        sb.Append(" rem=").Append(bot.Agent.hasPath ? bot.Agent.remainingDistance.ToString("0.0") : "-");
                        if (!bot.Agent.isOnNavMesh) sb.Append(" OFFMESH");
                    }
                    else if (bot.Steering != null) sb.Append(" steering");

                    sb.Append("  ").Append(bot.Brain.LastTransition);

                    GUILayout.Label(sb.ToString(), _mono);
                }
            }

            GUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ settings

        private void DrawSettings()
        {
            GUILayout.Label("SETTINGS", _mono);
            _scrollSettings = GUILayout.BeginScrollView(_scrollSettings, GUILayout.Height(190));

            // A guest shows the host's values but cannot change them.
            var locked = Net.BotNetwork.SettingsLocked;
            if (locked) GUILayout.Label("HOST CONTROLS SETTINGS (you are a guest)", _mono);

            var tunables = Settings.Tunables;
            string group = null;
            for (int i = 0; i < tunables.Count; i++)
            {
                var t = tunables[i];
                if (t.Group != group)
                {
                    group = t.Group;
                    GUILayout.Label("— " + group + " —", _mono);
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label(t.Label, _mono, GUILayout.Width(150));
                GUILayout.Label(t.Display, _mono, GUILayout.Width(80));
                GUI.enabled = !locked;
                int step = 0; bool coarse = false;
                if (GUILayout.Button("-", GUILayout.Width(24))) step = -1;
                if (GUILayout.Button("+", GUILayout.Width(24))) step = +1;
                if (GUILayout.Button("--", GUILayout.Width(28))) { step = -1; coarse = true; }
                if (GUILayout.Button("++", GUILayout.Width(28))) { step = +1; coarse = true; }
                if (step != 0)
                {
                    var r = t.Adjust(step, coarse);
                    Say(r.Message);
                    if (r.Changed) Settings.Save();
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ helpers

        private void Say(string message)
        {
            _message = message ?? "";
            _messageUntil = Time.time + 4f;
            if (!string.IsNullOrEmpty(_message)) Plugin.Log.LogInfo("[Console] " + _message);
        }

        private static Vector3? LocalPos()
        {
            var tagger = GorillaTagger.Instance;
            if (tagger == null || tagger.offlineVRRig == null) return null;
            return tagger.offlineVRRig.transform.position;
        }
    }
}
