using System.Text;
using NextBots.Runtime;
using UnityEngine;
using UnityEngine.XR;

namespace NextBots.UI
{
    /// <summary>
    /// Opens the panel, and is deliberately paranoid about how.
    ///
    /// Three independent input paths, because each has a way of silently not working:
    ///   - GT's <c>ControllerInputPoller</c> face-button flags.
    ///   - Unity XR <c>InputDevices</c> directly, in case the poller's flags are not populated.
    ///   - A grip+trigger gesture, using the axes GT definitely maintains because they drive
    ///     climbing and tagging.
    ///   - Keyboard, which only works when the desktop window has focus - i.e. never while
    ///     you are actually wearing the headset. That is why it is the fallback and not the
    ///     plan.
    ///
    /// <see cref="LogInputDiagnostics"/> prints whatever the hardware is reporting, so a
    /// "nothing happens" report can be diagnosed from the log instead of by guesswork.
    /// </summary>
    public class ModInput : MonoBehaviour
    {
        public NextBotMenu Menu;

        /// <summary>Seconds grip+trigger must be held together to count as the gesture.</summary>
        public float GestureHold = 0.4f;

        private bool _wasDown;
        private bool _spawnRecorded;
        private float _nextToggleAllowed;
        private float _gestureSince = -1f;
        private float _nextDiagnostic;
        private string _lastDiagnostic = "";

        private bool _legacyInputDead;
        private bool _loggedFault;

        private void Awake()
        {
            Plugin.Log.LogInfo("[Input] ModInput alive on '" + gameObject.name +
                               "' | menuButton=" + (Plugin.CfgMenuButton != null ? Plugin.CfgMenuButton.Value : "<null cfg>") +
                               " | logInput=" + (Plugin.CfgLogInput != null ? Plugin.CfgLogInput.Value.ToString() : "<null cfg>"));
        }

        private void Update()
        {
            // Anything that throws in here would otherwise kill input silently, which is
            // exactly the failure that wasted a test cycle. Report once, then carry on.
            try { UpdateInner(); }
            catch (System.Exception ex)
            {
                if (!_loggedFault)
                {
                    _loggedFault = true;
                    Plugin.Log.LogError("[Input] Update threw (reported once): " + ex);
                }
            }
        }

        private void UpdateInner()
        {
            RecordSpawnOnce();

            if (Plugin.CfgReloadKey != null && Menu != null && SafeKeyDown(Plugin.CfgReloadKey.Value))
                Menu.ReloadLayout();

            var down = ReadAnyOpenInput();

            if (down && !_wasDown && Time.time >= _nextToggleAllowed)
            {
                _nextToggleAllowed = Time.time + 0.3f;
                if (Menu != null)
                {
                    Menu.Toggle();
                    Plugin.Log.LogInfo("[Input] panel " + (Menu.IsOpen ? "opened" : "closed") +
                                       " | " + Venue.Describe() +
                                       " | spawning: " + (Venue.CanSpawn ? "allowed" : Venue.BlockedReason));
                }
            }
            _wasDown = down;

            if (Plugin.CfgLogInput != null && Plugin.CfgLogInput.Value) LogInputDiagnostics();
        }

        // ==================================================================== input

        private bool ReadAnyOpenInput()
        {
            if (ReadPollerButton()) return true;
            if (ReadXrButton()) return true;

            // Opt-in only. Grip is the climbing button in this game, so grip+trigger comes
            // up constantly during normal play and would open the panel by accident.
            if (Plugin.CfgUseGesture != null && Plugin.CfgUseGesture.Value && ReadGesture()) return true;

            // Keyboard: only reaches us when the game window has focus, i.e. never while
            // the headset is actually on. Last resort by design.
            if (Plugin.CfgMenuKey != null && SafeKey(Plugin.CfgMenuKey.Value)) return true;

            return false;
        }

        /// <summary>
        /// Legacy UnityEngine.Input throws outright when a project has switched to the new
        /// Input System. Fail once, say so, and never touch it again.
        /// </summary>
        private bool SafeKey(KeyCode key)
        {
            if (_legacyInputDead) return false;
            try { return Input.GetKey(key); }
            catch (System.Exception ex)
            {
                _legacyInputDead = true;
                Plugin.Log.LogWarning("[Input] legacy keyboard input unavailable (" +
                                      ex.GetType().Name + "); controller only from here.");
                return false;
            }
        }

        private bool SafeKeyDown(KeyCode key)
        {
            if (_legacyInputDead) return false;
            try { return Input.GetKeyDown(key); }
            catch { _legacyInputDead = true; return false; }
        }

        private static bool ReadPollerButton()
        {
            var p = ControllerInputPoller.instance;
            if (p == null) return false;

            switch (Plugin.CfgMenuButton != null ? Plugin.CfgMenuButton.Value : "LeftSecondary")
            {
                case "LeftPrimary":    return p.leftControllerPrimaryButton;
                case "LeftSecondary":  return p.leftControllerSecondaryButton;
                case "RightPrimary":   return p.rightControllerPrimaryButton;
                case "RightSecondary": return p.rightControllerSecondaryButton;
                case "AnyFace":
                    return p.leftControllerPrimaryButton || p.leftControllerSecondaryButton ||
                           p.rightControllerPrimaryButton || p.rightControllerSecondaryButton;
                case "None":           return false;
                default:               return p.leftControllerSecondaryButton;
            }
        }

        /// <summary>
        /// Straight from the XR subsystem, bypassing GT entirely. If the game's poller is not
        /// populating its flags, this still works.
        /// </summary>
        private static bool ReadXrButton()
        {
            var which = Plugin.CfgMenuButton != null ? Plugin.CfgMenuButton.Value : "LeftSecondary";
            if (which == "None") return false;

            var wantLeft = !which.StartsWith("Right");
            var wantSecondary = which.EndsWith("Secondary") || which == "AnyFace";

            var node = wantLeft ? XRNode.LeftHand : XRNode.RightHand;
            var device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid) return false;

            bool pressed;
            if (which == "AnyFace")
            {
                if (device.TryGetFeatureValue(CommonUsages.primaryButton, out pressed) && pressed) return true;
                if (device.TryGetFeatureValue(CommonUsages.secondaryButton, out pressed) && pressed) return true;

                var other = InputDevices.GetDeviceAtXRNode(wantLeft ? XRNode.RightHand : XRNode.LeftHand);
                if (other.isValid)
                {
                    if (other.TryGetFeatureValue(CommonUsages.primaryButton, out pressed) && pressed) return true;
                    if (other.TryGetFeatureValue(CommonUsages.secondaryButton, out pressed) && pressed) return true;
                }
                return false;
            }

            var usage = wantSecondary ? CommonUsages.secondaryButton : CommonUsages.primaryButton;
            return device.TryGetFeatureValue(usage, out pressed) && pressed;
        }

        /// <summary>
        /// Hold grip + trigger on the panel hand. Uses the axes GT definitely keeps live, so
        /// this works even if every button path above is dead. Held rather than tapped so it
        /// cannot fire while you are climbing.
        /// </summary>
        private bool ReadGesture()
        {
            var p = ControllerInputPoller.instance;
            if (p == null) { _gestureSince = -1f; return false; }

            var left = Menu == null || Menu.AnchorToLeftHand;
            var grip = left ? p.leftControllerGripFloat : p.rightControllerGripFloat;
            var trig = left ? p.leftControllerIndexFloat : p.rightControllerIndexFloat;

            if (grip > 0.75f && trig > 0.75f)
            {
                if (_gestureSince < 0f) _gestureSince = Time.time;
                return Time.time - _gestureSince >= GestureHold;
            }

            _gestureSince = -1f;
            return false;
        }

        // ==================================================================== diagnostics

        /// <summary>
        /// Once a second, log what the hardware reports - but only when it changes, so it is
        /// useful without flooding the log.
        /// </summary>
        private void LogInputDiagnostics()
        {
            if (Time.time < _nextDiagnostic) return;
            _nextDiagnostic = Time.time + 1f;

            var sb = new StringBuilder(256);
            var p = ControllerInputPoller.instance;
            if (p == null) sb.Append("poller=<null> ");
            else
            {
                sb.Append("poller[");
                sb.Append("Lvalid=").Append(p.leftControllerIsValid ? 1 : 0);
                sb.Append(" Rvalid=").Append(p.rightControllerIsValid ? 1 : 0);
                sb.Append(" LP=").Append(p.leftControllerPrimaryButton ? 1 : 0);
                sb.Append(" LS=").Append(p.leftControllerSecondaryButton ? 1 : 0);
                sb.Append(" RP=").Append(p.rightControllerPrimaryButton ? 1 : 0);
                sb.Append(" RS=").Append(p.rightControllerSecondaryButton ? 1 : 0);
                sb.Append(" Lgrip=").Append(p.leftControllerGripFloat.ToString("0.0"));
                sb.Append(" Ltrig=").Append(p.leftControllerIndexFloat.ToString("0.0"));
                sb.Append(" handTrack=").Append(p.handTrackingActive ? 1 : 0);
                sb.Append("] ");
            }

            AppendXr(sb, "L", XRNode.LeftHand);
            AppendXr(sb, "R", XRNode.RightHand);

            var line = sb.ToString();
            if (line == _lastDiagnostic) return;    // only on change
            _lastDiagnostic = line;
            Plugin.Log.LogInfo("[InputDiag] " + line);
        }

        private static void AppendXr(StringBuilder sb, string tag, XRNode node)
        {
            var d = InputDevices.GetDeviceAtXRNode(node);
            if (!d.isValid) { sb.Append("xr").Append(tag).Append("=<invalid> "); return; }

            bool prim, sec;
            d.TryGetFeatureValue(CommonUsages.primaryButton, out prim);
            d.TryGetFeatureValue(CommonUsages.secondaryButton, out sec);
            sb.Append("xr").Append(tag).Append('[').Append(d.name)
              .Append(" P=").Append(prim ? 1 : 0)
              .Append(" S=").Append(sec ? 1 : 0).Append("] ");
        }

        // ==================================================================== spawn point

        private void RecordSpawnOnce()
        {
            if (_spawnRecorded) return;
            if (!Photon.Pun.PhotonNetwork.InRoom) return;

            var tagger = GorillaTagger.Instance;
            if (tagger == null || tagger.offlineVRRig == null) return;

            var pos = tagger.offlineVRRig.transform.position;
            if (pos.sqrMagnitude < 0.01f) return;

            CatchEffects.RememberSpawn(pos);
            _spawnRecorded = true;
        }

        private void OnEnable()
        {
            try
            {
                var client = Photon.Pun.PhotonNetwork.NetworkingClient;
                if (client != null) client.EventReceived += OnAnyEvent;
            }
            catch { /* convenience only */ }
        }

        private void OnDisable()
        {
            try
            {
                var client = Photon.Pun.PhotonNetwork.NetworkingClient;
                if (client != null) client.EventReceived -= OnAnyEvent;
            }
            catch { /* shutting down */ }
        }

        private void OnAnyEvent(ExitGames.Client.Photon.EventData e)
        {
            if (e.Code == 255 || e.Code == 254) _spawnRecorded = false;
        }
    }
}
