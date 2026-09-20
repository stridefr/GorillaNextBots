using BepInEx.Configuration;
using GorillaRagdoll.Config;
using GorillaRagdoll.Runtime;
using UnityEngine;

namespace GorillaRagdoll.UI
{
    /// <summary>
    /// The desktop settings overlay.
    ///
    /// <para>IMGUI on purpose. It needs no assets, no canvas, no font resolution and no
    /// shader - all four of which are things NextBots had to solve to draw a panel in VR, and
    /// none of which are worth solving twice for a window that only ever appears on a monitor.
    /// Sliders read their own bounds out of the BepInEx config descriptions, so a knob added
    /// in <see cref="RagdollConfig"/> shows up here with the right range automatically.</para>
    /// </summary>
    public sealed class RagdollMenu : MonoBehaviour
    {
        public RagdollController Controller;

        private const int WindowId = 0x7A9D01;
        private static readonly string[] BaseTabs = { "Ragdoll", "Camera", "Physics", "Active", "Launch" };

        // BaseTabs plus whatever other mods have added through MenuTabs. Rebuilt only when that changes.
        private string[] _allTabs;
        private int _allTabsVersion = -1;

        private string[] AllTabs()
        {
            if (_allTabs != null && _allTabsVersion == MenuTabs.Version) return _allTabs;

            var list = new System.Collections.Generic.List<string>(BaseTabs);
            for (int i = 0; i < MenuTabs.Count; i++) list.Add(MenuTabs.Name(i));
            _allTabs = list.ToArray();
            _allTabsVersion = MenuTabs.Version;
            return _allTabs;
        }

        private Rect _rect = new Rect(24f, 24f, 420f, 0f);
        private bool _visible;
        private int _tab;
        private Vector2 _scroll;

        private bool _savedCursorVisible;
        private CursorLockMode _savedLockState;

        public bool Visible
        {
            get { return _visible; }
            set
            {
                if (_visible == value) return;
                _visible = value;
                if (Controller != null) Controller.MenuOpen = value;

                // A window you cannot click is not a settings screen.
                if (value)
                {
                    _savedCursorVisible = Cursor.visible;
                    _savedLockState = Cursor.lockState;
                    Cursor.visible = true;
                    Cursor.lockState = CursorLockMode.None;
                }
                else
                {
                    Cursor.visible = _savedCursorVisible;
                    Cursor.lockState = _savedLockState;
                }
            }
        }

        private void OnGUI()
        {
            if (!_visible || Controller == null) return;

            // The title bar is drawn here; everything inside the window is drawn later, in
            // Draw, so the font is set in both places. The skin is shared with every other mod's
            // IMGUI, so it is always put back.
            var skin = GUI.skin;
            var saved = skin.font;
            var font = MenuFont();
            if (font != null) skin.font = font;
            try
            {
                GUI.depth = -100;
                _rect = GUILayout.Window(WindowId, _rect, Draw, "Gorilla Ragdoll",
                    GUILayout.Width(420f), GUILayout.MinHeight(200f));
            }
            finally { skin.font = saved; }
        }

        private void Draw(int id)
        {
            var skin = GUI.skin;
            var saved = skin.font;
            var font = MenuFont();
            if (font != null) skin.font = font;
            try { DrawContents(); }
            finally { skin.font = saved; }
        }

        // ------------------------------------------------------------------ font

        private Font _font;
        private string _fontName;

        /// <summary>
        /// The font named in <c>MenuFont</c>, built from the fonts installed on this PC; null for
        /// Unity's own. IMGUI can only use fonts through the operating system's font list - not
        /// from a file, as the headset text does - so whether Unity can see the font is checked
        /// and said in the log, rather than left to fall back silently.
        /// </summary>
        private Font MenuFont()
        {
            string name = (RagdollConfig.MenuFont.Value ?? "").Trim();
            if (name == _fontName) return _font;
            _fontName = name;
            _font = null;
            if (name.Length == 0) return null;

            string match = null;
            try
            {
                foreach (var n in Font.GetOSInstalledFontNames())
                {
                    if (string.Equals(n, name, System.StringComparison.OrdinalIgnoreCase)) { match = n; break; }
                    if (match == null && n.StartsWith(name, System.StringComparison.OrdinalIgnoreCase)) match = n;
                }
            }
            catch { /* the list is only a diagnostic */ }

            // A font Unity cannot see does not fail: it hands back a font with no letters in it,
            // which draws whatever else is in the shared glyph texture - another mod's on-screen
            // text, in the middle of the menu. So use the default font instead, and say why.
            if (match == null)
            {
                Plugin.Log.LogWarning("[Menu] Windows has no font called '" + name + "' installed for all users, so the " +
                                      "menu uses the default font. Right-click the font file and pick " +
                                      "\"Install for all users\" to use it here.");
                return null;
            }

            try
            {
                _font = Font.CreateDynamicFontFromOSFont(match, 15);
                // Nothing Unity tracks holds this, and a level load may unload its glyph texture.
                if (_font != null) _font.hideFlags = HideFlags.DontUnloadUnusedAsset;
                Plugin.Log.LogInfo("[Menu] font '" + match + "'");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[Menu] font '" + name + "': " + ex.Message); }

            return _font;
        }

        private void DrawContents()
        {
            Header();
            var tabs = AllTabs();
            if (_tab >= tabs.Length) _tab = 0;
            _tab = GUILayout.Toolbar(_tab, tabs);
            GUILayout.Space(4f);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(360f));
            switch (_tab)
            {
                case 0: TabRagdoll(); break;
                case 1: TabCamera(); break;
                case 2: TabPhysics(); break;
                case 3: TabActive(); break;
                case 4: TabLaunch(); break;
                default: MenuTabs.Draw(_tab - BaseTabs.Length); break;
            }
            GUILayout.EndScrollView();

            GUILayout.Space(2f);
            GUILayout.Label("<b>" + RagdollConfig.ToggleKey.Value + "</b> ragdoll   " +
                            "<b>" + RagdollConfig.MenuKey.Value + "</b> menu   " +
                            "<b>" + RagdollConfig.PanicKey.Value + "</b> panic   " +
                            "<b>" + RagdollConfig.DumpSkeletonKey.Value + "</b> dump bones",
                            RichLabel());

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        private void Header()
        {
            GUILayout.BeginHorizontal(GUI.skin.box);
            GUILayout.Label(Controller.IsRagdolled ? "<b>RAGDOLLED</b>" : "upright", RichLabel());
            GUILayout.FlexibleSpace();
            GUILayout.Label(Venue.Describe());
            GUILayout.EndHorizontal();

            var blocked = Venue.BlockedReason;
            if (blocked != null) GUILayout.Label("blocked: " + blocked);
            else GUILayout.Label("status: " + Controller.Status);
        }

        // ------------------------------------------------------------------ tabs

        private void TabRagdoll()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Controller.IsRagdolled ? "GET UP" : "GO RAGDOLL", GUILayout.Height(32f)))
                Controller.Toggle();
            if (GUILayout.Button("Rebuild", GUILayout.Height(32f), GUILayout.Width(90f)))
                Controller.Rebuild();
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            GUILayout.Label("bodies simulating: " + Controller.BoneCount);

            GUILayout.Space(6f);
            Bool(RagdollConfig.HideRealRig, "Hide my normal avatar");
            Bool(RagdollConfig.CloneFace, "Copy face onto ragdoll");
            Bool(RagdollConfig.RestrictToPrivateRooms, "Private/offline rooms only");
            Slider(RagdollConfig.AutoGetUpSeconds, "Auto get-up (0 = never)", "0.#");

            GUILayout.Space(6f);
            EnumRow("Getting up", RagdollConfig.GetUp);
            if (RagdollConfig.GetUp.Value == GetUpMode.Respawn)
            {
                Slider(RagdollConfig.GetUpHeight, "Respawn height");
                GUILayout.Label("Drops you in just above where the body landed, checking the spot is " +
                                "actually clear first. Hard reset - nothing left half-restored.", Wrapped());
            }
            else
            {
                Slider(RagdollConfig.GetUpBlend, "Get-up blend (0 = snap)", "0.00");
            }

            GUILayout.Space(6f);
            EnumRow("Body while ragdolled", RagdollConfig.BodyFollow);
            GUILayout.Label("Only applies when the VR camera mode is Off - otherwise the " +
                            "headset mode decides where your body goes.", Wrapped());

            GUILayout.Space(6f);
            GUILayout.Label("<b>Multiplayer</b>", RichLabel());
            Bool(RagdollConfig.ShareMyRagdoll, "Share my ragdoll with the room");
            Bool(RagdollConfig.ShowOtherRagdolls, "Show other players' ragdolls");
            Slider(RagdollConfig.NetSendRate, "Poses per second", "0");
            Slider(RagdollConfig.NetSmoothness, "Smoothing of other ragdolls");
            Slider(RagdollConfig.NetExtrapolate, "Keep moving when late (ms)", "0");
            Bool(RagdollConfig.NetFlushSends, "Send each pose straight away");
            GUILayout.Label((Controller.Network != null && Controller.Network.Sending ? "sending | " : "") +
                            "other ragdolls on screen: " + Net.RagdollNet.RemoteCount +
                            (Net.RagdollNet.HookInstalled ? "" : " | IK hook missing - may flicker"),
                            Wrapped());
            GUILayout.Label("Only players who also have this mod see your ragdoll, or can show you " +
                            "theirs. Everyone else sees normal avatars.", Wrapped());

            GUILayout.Space(6f);
            GUILayout.Label("<b>VR button</b>", RichLabel());
            Choice(RagdollConfig.VrBinding);
            Slider(RagdollConfig.VrGestureHold, "Hold time", "0.00");
            GUILayout.Label("Left X is 'LeftPrimary'. Left Y is taken by NextBots' wrist panel. " +
                            "The hold time stops it firing while you climb or tag.", Wrapped());

            GUILayout.Space(6f);
            if (GUILayout.Button("Dump skeleton to log")) Controller.DumpSkeleton();
        }

        private void TabCamera()
        {
            // The headset and the monitor are genuinely independent outputs, but the common
            // cases only ever want a matched pair - so offer them as one click instead of
            // making people set the same intent twice.
            GUILayout.Label("<b>Presets</b>", RichLabel());
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Watch in VR"))
            {
                RagdollConfig.VrMode.Value = CameraMode.ThirdPersonFree;
                RagdollConfig.MonitorMode.Value = CameraMode.ThirdPersonFree;
            }
            if (GUILayout.Button("Ride it"))
            {
                RagdollConfig.VrMode.Value = CameraMode.FirstPersonUnlocked;
                RagdollConfig.MonitorMode.Value = CameraMode.ThirdPersonFree;
            }
            if (GUILayout.Button("Headset off"))
            {
                RagdollConfig.VrMode.Value = CameraMode.Off;
                RagdollConfig.MonitorMode.Value = CameraMode.ThirdPersonFree;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.Label("<b>Headset</b>", RichLabel());
            EnumRow("VR view", RagdollConfig.VrMode);
            if (RagdollConfig.VrMode.Value == CameraMode.FirstPersonUnlocked)
                GUILayout.Label("Your head is free - look around, lean, duck. Only where you are " +
                                "standing follows the ragdoll.", Wrapped());
            if (RagdollConfig.VrMode.Value == CameraMode.FirstPersonLocked)
                GUILayout.Label("Locked first person rides the ragdoll's head rotation. " +
                                "Comfort settings below exist for a reason.", Wrapped());
            if (RagdollConfig.VrMode.Value == CameraMode.ThirdPersonFree)
                GUILayout.Label("Joystick orbit camera - see VR orbit below. Your head is free: look " +
                                "wherever you like, only where you stand follows the body. Players " +
                                "with this mod see your ragdoll; players without it see you where " +
                                "the camera is.", Wrapped());

            GUILayout.Space(6f);
            GUILayout.Label("<b>Monitor</b>", RichLabel());
            EnumRow("Monitor view", RagdollConfig.MonitorMode);
            if (RagdollConfig.MonitorMode.Value == CameraMode.ThirdPersonFree)
            {
                EnumRow("Orbit aim", RagdollConfig.MonitorOrbitAim);
                if (RagdollConfig.MonitorOrbitAim.Value == OrbitAim.Free)
                {
                    EnumRow("Right mouse", RagdollConfig.MonitorOrbitDrag);
                    GUILayout.Label(RagdollConfig.MonitorOrbitDrag.Value == OrbitDrag.OrbitBody
                        ? "Hold RIGHT MOUSE and drag to circle your body and raise or lower the " +
                          "camera. A/D and W/S do the same, scroll zooms, C looks back at the body. " +
                          "It pulls in when scenery blocks it."
                        : "Hold RIGHT MOUSE to look anywhere without moving the camera. A/D circle " +
                          "the body, W/S raise/lower, scroll zooms, C (or middle mouse) looks back " +
                          "at the body.", Wrapped());
                    Slider(RagdollConfig.MonitorOrbitKeySpeed, "Circle speed (A/D, W/S)", "0");
                    GUILayout.Space(4f);
                    Bool(RagdollConfig.AutoOrbit, "Auto orbit for filming (" + RagdollConfig.AutoOrbitKey.Value + " toggles)");
                    if (RagdollConfig.AutoOrbit.Value)
                    {
                        Slider(RagdollConfig.AutoOrbitSpeed, "Auto orbit speed (deg/s)", "0");
                        Slider(RagdollConfig.AutoOrbitSway, "Drift and breathing", "0.00");
                        Slider(RagdollConfig.AutoOrbitResume, "Resumes after (s)", "0.0");
                    }
                }
                else
                    GUILayout.Label("Always aimed at the body: hold RIGHT MOUSE to swing around it, " +
                                    "scroll to zoom.", Wrapped());
            }
            if (RagdollConfig.MonitorMode.Value == CameraMode.ThirdPersonFly)
                GUILayout.Label("WASD + QE to fly, hold RIGHT MOUSE to look, shift/ctrl for " +
                                "speed, scroll to tune it. Speed now: " +
                                Controller.Monitor.Speed.ToString("0.0"), Wrapped());

            GUILayout.Space(6f);
            Slider(RagdollConfig.ThirdPersonDistance, "Third person distance");
            Slider(RagdollConfig.ThirdPersonHeight, "Third person height");
            Slider(RagdollConfig.CameraSmoothing, "Follow smoothing", "0.000");
            Slider(RagdollConfig.FreeCamSpeed, "Free cam speed");
            Slider(RagdollConfig.FreeCamSensitivity, "Mouse sensitivity");
            Slider(RagdollConfig.FreeCamFov, "Field of view", "0");
            Slider(RagdollConfig.ThirdPersonMinDistance, "Closest when blocked");
            Slider(RagdollConfig.OcclusionRadius, "View-block thickness", "0.00");
            Slider(RagdollConfig.CameraTransition, "Ease-in time", "0.00");
            Bool(RagdollConfig.SeeThroughWalls, "See through walls in the way");
            Slider(RagdollConfig.OccluderAlpha, "Blocked surface opacity");
            Bool(RagdollConfig.InvertY, "Invert mouse Y");

            GUILayout.Space(6f);
            GUILayout.Label("<b>VR orbit</b>", RichLabel());
            GUILayout.Label("With the headset on ThirdPersonFree: right stick swings you around " +
                            "the body and raises/lowers the view, left stick pushes in and out. " +
                            "The game's own snap/smooth turn is paused while you orbit, since it " +
                            "uses the same stick.", Wrapped());
            EnumRow("Stick turning", RagdollConfig.VrOrbitTurning);
            Slider(RagdollConfig.VrOrbitSnapAngle, "Snap angle", "0");
            Slider(RagdollConfig.VrFollowDeadzone, "Follow deadzone (m)");
            Slider(RagdollConfig.VrOrbitYawSpeed, "Orbit speed (smooth)", "0");
            Slider(RagdollConfig.VrOrbitPitchSpeed, "Raise/lower speed", "0");
            Slider(RagdollConfig.VrOrbitZoomSpeed, "Zoom speed");
            Slider(RagdollConfig.VrOrbitDeadzone, "Stick deadzone");
            Bool(RagdollConfig.VrOrbitInvertPitch, "Invert stick up/down");

            GUILayout.Space(6f);
            GUILayout.Label("<b>VR comfort</b>", RichLabel());
            Bool(RagdollConfig.VrClampPitch, "Clamp how far the ragdoll tips your view");
            Slider(RagdollConfig.VrMaxPitch, "Max tip angle", "0");
            Bool(RagdollConfig.VrAllowRoll, "Allow horizon roll (not recommended)");
        }

        private void TabPhysics()
        {
            GUILayout.Label("Mass and damping apply live. The rest need a Rebuild.", Wrapped());
            GUILayout.Space(4f);

            Slider(RagdollConfig.TotalMass, "Total mass", "0.#");
            Slider(RagdollConfig.LinearDamping, "Linear damping");
            Slider(RagdollConfig.AngularDamping, "Angular damping");
            Controller.ApplyLiveTuning();

            GUILayout.Space(6f);
            Slider(RagdollConfig.JointLooseness, "Joint looseness");
            Slider(RagdollConfig.ExtraGravity, "Extra gravity", "0.#");
            IntSlider(RagdollConfig.SolverIterations, "Solver iterations");
            IntSlider(RagdollConfig.MaxBoneDepth, "Max bone depth");
            IntSlider(RagdollConfig.MaxBodies, "Max bodies");
            Bool(RagdollConfig.SelfCollision, "Limbs collide with each other");
            Bool(RagdollConfig.ContinuousCollision, "Continuous collision (anti tunnelling)");
        }

        private void TabActive()
        {
            GUILayout.Label("<b>Experimental.</b> Off is a fully limp ragdoll. Assisted gives every " +
                            "joint a motor that pulls back towards the pose you collapsed in, so the " +
                            "body crumples on impact and gathers itself as it recovers.", Wrapped());
            GUILayout.Space(4f);

            EnumRow("Mode", RagdollConfig.ActiveMode);
            GUILayout.Label("Changing the mode swaps the joint type, so it needs a Rebuild.", Wrapped());

            GUILayout.Space(6f);
            Slider(RagdollConfig.ActiveStrength, "Strength", "0");
            Slider(RagdollConfig.ActiveDamping, "Damping", "0");
            Slider(RagdollConfig.ActiveRecover, "Recover time", "0.00");
            Slider(RagdollConfig.ActiveImpactThreshold, "Impact threshold", "0.0");

            GUILayout.Space(6f);
            GUILayout.Label("tension now: " + (Controller.Tension * 100f).ToString("0") + "%");
            GUILayout.Label("Low strength reads as a drunk stagger; high fights to hold its shape. " +
                            "If it vibrates instead of settling, raise damping.", Wrapped());
        }

        private void TabLaunch()
        {
            Bool(RagdollConfig.InheritPlayerVelocity, "Inherit body momentum");
            Bool(RagdollConfig.InheritLimbVelocity, "Inherit head and hand speed");
            Slider(RagdollConfig.LaunchImpulse, "Launch impulse along view");
            Slider(RagdollConfig.LaunchSpin, "Launch spin");
            GUILayout.Space(6f);
            GUILayout.Label("Jump, swing hard, then hit the ragdoll key - the momentum you " +
                            "had is the momentum the ragdoll gets.", Wrapped());
        }

        // ------------------------------------------------------------------ widgets

        private static GUIStyle _rich, _wrapped;

        public static GUIStyle RichLabel()
        {
            if (_rich == null) _rich = new GUIStyle(GUI.skin.label) { richText = true };
            return _rich;
        }

        public static GUIStyle Wrapped()
        {
            if (_wrapped == null)
                _wrapped = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
            return _wrapped;
        }

        public static void Bool(ConfigEntry<bool> entry, string label)
        {
            bool v = GUILayout.Toggle(entry.Value, " " + label);
            if (v != entry.Value) entry.Value = v;
        }

        /// <summary>
        /// Range comes from the config description, so a knob only has to declare its bounds
        /// once - in <see cref="RagdollConfig"/> - and both the .cfg and this slider honour it.
        /// </summary>
        public static void Slider(ConfigEntry<float> entry, string label, string format = "0.00")
        {
            float min = 0f, max = 1f;
            var range = entry.Description != null
                ? entry.Description.AcceptableValues as AcceptableValueRange<float>
                : null;
            if (range != null) { min = range.MinValue; max = range.MaxValue; }

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(210f));
            GUILayout.Label(entry.Value.ToString(format), GUILayout.Width(52f));
            float v = GUILayout.HorizontalSlider(entry.Value, min, max);
            GUILayout.EndHorizontal();
            if (!Mathf.Approximately(v, entry.Value)) entry.Value = v;
        }

        public static void IntSlider(ConfigEntry<int> entry, string label)
        {
            int min = 0, max = 10;
            var range = entry.Description != null
                ? entry.Description.AcceptableValues as AcceptableValueRange<int>
                : null;
            if (range != null) { min = range.MinValue; max = range.MaxValue; }

            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(210f));
            GUILayout.Label(entry.Value.ToString(), GUILayout.Width(52f));
            int v = Mathf.RoundToInt(GUILayout.HorizontalSlider(entry.Value, min, max));
            GUILayout.EndHorizontal();
            if (v != entry.Value) entry.Value = v;
        }

        /// <summary>
        /// Picker for a string setting, using the options the config itself declares - so the
        /// list can never drift out of step with what the setting will actually accept.
        /// </summary>
        private static void Choice(ConfigEntry<string> entry)
        {
            var allowed = entry.Description != null
                ? entry.Description.AcceptableValues as AcceptableValueList<string>
                : null;
            if (allowed == null) return;

            var options = allowed.AcceptableValues;
            int current = System.Array.IndexOf(options, entry.Value);
            if (current < 0) current = 0;

            int picked = GUILayout.SelectionGrid(current, options, 3);
            if (picked != current) entry.Value = options[picked];
        }

        public static void EnumRow<T>(string label, ConfigEntry<T> entry) where T : struct
        {
            GUILayout.Label(label);
            var names = System.Enum.GetNames(typeof(T));
            var values = (T[])System.Enum.GetValues(typeof(T));

            int current = 0;
            for (int i = 0; i < values.Length; i++)
                if (values[i].Equals(entry.Value)) { current = i; break; }

            int picked = GUILayout.SelectionGrid(current, names, System.Math.Min(names.Length, 2));
            if (picked != current) entry.Value = values[picked];
        }
    }
}
