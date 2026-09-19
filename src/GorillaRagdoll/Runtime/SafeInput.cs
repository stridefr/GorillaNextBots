using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
// The class exposes a Key(KeyCode) method, which shadows the enum name inside it.
using ISKey = UnityEngine.InputSystem.Key;
using ISMouse = UnityEngine.InputSystem.Mouse;

namespace GorillaRagdoll.Runtime
{
    /// <summary>
    /// Keyboard and mouse, over whichever input backend this build of the game actually has.
    ///
    /// <para><b>Gorilla Tag has switched to the new Input System.</b> That means every call
    /// into legacy <c>UnityEngine.Input</c> throws <c>InvalidOperationException</c> on first
    /// use - not returns false, <i>throws</i>. A mod that reads keys the old way does not
    /// degrade, it dies silently on frame one, which is exactly what happened here: the plugin
    /// loaded, booted, and then reported "PC controls are dead from here" before a single key
    /// could register.</para>
    ///
    /// <para>So the backend is probed once and then used for the life of the process. Legacy
    /// is still tried first, because it costs one exception to find out and a build with the
    /// old handler (or "Both") should keep working.</para>
    /// </summary>
    public static class SafeInput
    {
        private enum Backend { Unprobed, Legacy, Modern, Dead }

        private static Backend _backend = Backend.Unprobed;

        /// <summary>
        /// Legacy is written off permanently on its first throw. Without this the probe
        /// retries it every frame while waiting for an input device to appear, which means an
        /// exception and a log line per frame.
        /// </summary>
        private static bool _legacyRuledOut;

        private static bool _devicesLogged;

        /// <summary>Legacy delta is pre-scaled by Unity; the new system hands over raw pixels.</summary>
        private const float PixelsToLegacyAxis = 0.1f;

        public static bool Dead => Probe() == Backend.Dead;

        /// <summary>Which backend is live, for the overlay and the log.</summary>
        public static string BackendName => Probe().ToString();

        private static Backend Probe()
        {
            if (_backend != Backend.Unprobed) return _backend;

            if (!_legacyRuledOut)
            {
                try
                {
                    // Cheapest possible legacy read. Throws outright when the project is set
                    // to the new Input System.
                    Input.GetKey(KeyCode.None);
                    _backend = Backend.Legacy;
                    Plugin.Log.LogInfo("[Input] using legacy UnityEngine.Input");
                    return _backend;
                }
                catch (Exception ex)
                {
                    _legacyRuledOut = true;
                    Plugin.Log.LogInfo("[Input] legacy input unavailable (" + ex.GetType().Name +
                                       "), switching to the new Input System");
                }
            }

            try
            {
                if (Keyboard.current != null || ISMouse.current != null)
                {
                    _backend = Backend.Modern;
                    Plugin.Log.LogInfo("[Input] using UnityEngine.InputSystem | keyboard=" +
                                       (Keyboard.current != null) + " mouse=" + (ISMouse.current != null));
                    return _backend;
                }
                // Devices can arrive late; stay unprobed so a later frame can succeed. Say
                // what the Input System can actually see, once, so "still nothing happens"
                // is diagnosable from the log instead of by guesswork.
                if (!_devicesLogged)
                {
                    _devicesLogged = true;
                    var sb = new System.Text.StringBuilder("[Input] no keyboard/mouse yet. Devices: ");
                    foreach (var d in InputSystem.devices)
                        sb.Append(d.GetType().Name).Append('(').Append(d.name).Append(") ");
                    Plugin.Log.LogWarning(sb.ToString());
                }
                return Backend.Unprobed;
            }
            catch (Exception ex)
            {
                _backend = Backend.Dead;
                Plugin.Log.LogError("[Input] no usable input backend: " + ex);
                return _backend;
            }
        }

        // ------------------------------------------------------------------ keyboard

        public static bool Key(KeyCode key)
        {
            switch (Probe())
            {
                case Backend.Legacy:
                    try { return Input.GetKey(key); } catch { return false; }
                case Backend.Modern:
                    var c = Control(key);
                    return c != null && c.isPressed;
                default:
                    return false;
            }
        }

        public static bool KeyDown(KeyCode key)
        {
            switch (Probe())
            {
                case Backend.Legacy:
                    try { return Input.GetKeyDown(key); } catch { return false; }
                case Backend.Modern:
                    var c = Control(key);
                    return c != null && c.wasPressedThisFrame;
                default:
                    return false;
            }
        }

        private static ButtonControl Control(KeyCode code)
        {
            var kb = Keyboard.current;
            if (kb == null) return null;
            var key = Translate(code);
            if (key == ISKey.None) return null;
            return kb[key];
        }

        /// <summary>
        /// KeyCode -> Key. Most names match, so the fallback is a name parse; this switch only
        /// carries the ones the two enums genuinely disagree about.
        /// </summary>
        private static ISKey Translate(KeyCode code)
        {
            switch (code)
            {
                case KeyCode.LeftControl: return ISKey.LeftCtrl;
                case KeyCode.RightControl: return ISKey.RightCtrl;
                case KeyCode.Return: return ISKey.Enter;
                case KeyCode.KeypadEnter: return ISKey.NumpadEnter;
                case KeyCode.BackQuote: return ISKey.Backquote;
                case KeyCode.Print: return ISKey.PrintScreen;
                case KeyCode.LeftWindows: return ISKey.LeftWindows;
                case KeyCode.RightWindows: return ISKey.RightWindows;
                case KeyCode.LeftApple: return ISKey.LeftApple;
                case KeyCode.RightApple: return ISKey.RightApple;
            }

            string name = code.ToString();

            // Alpha0..9 -> Digit0..9, Keypad0..9 -> Numpad0..9
            if (name.StartsWith("Alpha", StringComparison.Ordinal)) name = "Digit" + name.Substring(5);
            else if (name.StartsWith("Keypad", StringComparison.Ordinal)) name = "Numpad" + name.Substring(6);

            ISKey parsed;
            return Enum.TryParse(name, out parsed) ? parsed : ISKey.None;
        }

        // ------------------------------------------------------------------ mouse

        /// <summary>0 = left, 1 = right, 2 = middle, matching the legacy convention.</summary>
        public static bool Mouse(int button)
        {
            switch (Probe())
            {
                case Backend.Legacy:
                    try { return Input.GetMouseButton(button); } catch { return false; }
                case Backend.Modern:
                    var m = ISMouse.current;
                    if (m == null) return false;
                    switch (button)
                    {
                        case 0: return m.leftButton.isPressed;
                        case 1: return m.rightButton.isPressed;
                        case 2: return m.middleButton.isPressed;
                        default: return false;
                    }
                default:
                    return false;
            }
        }

        /// <summary>Legacy-style "Mouse X" / "Mouse Y" look delta.</summary>
        public static float Axis(string axis)
        {
            switch (Probe())
            {
                case Backend.Legacy:
                    try { return Input.GetAxisRaw(axis); } catch { return 0f; }
                case Backend.Modern:
                    var m = ISMouse.current;
                    if (m == null) return 0f;
                    var d = m.delta.ReadValue();
                    return (axis == "Mouse Y" ? d.y : d.x) * PixelsToLegacyAxis;
                default:
                    return 0f;
            }
        }

        /// <summary>
        /// Wheel movement. Callers only look at the sign and a small threshold, so the raw
        /// scale difference between backends (notches vs. 120-per-notch) does not matter.
        /// </summary>
        public static float Scroll()
        {
            switch (Probe())
            {
                case Backend.Legacy:
                    try { return Input.mouseScrollDelta.y; } catch { return 0f; }
                case Backend.Modern:
                    var m = ISMouse.current;
                    return m == null ? 0f : m.scroll.ReadValue().y;
                default:
                    return 0f;
            }
        }
    }
}
