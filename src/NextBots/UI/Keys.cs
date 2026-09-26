using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace NextBots.UI
{
    /// <summary>
    /// A key press on the keyboard, whichever input system the game runs. Gorilla Tag is set to
    /// the new Input System, where the old <c>Input.GetKeyDown</c> throws; this tries the old one
    /// once and, if it is not there, reads the same key through the new one.
    /// </summary>
    public static class Keys
    {
        private static bool _probed, _legacy;

        public static bool Down(KeyCode key) => Read(key, true);

        /// <summary>Held down right now.</summary>
        public static bool Held(KeyCode key) => Read(key, false);

        private static bool Read(KeyCode key, bool pressedThisFrame)
        {
            if (!_probed)
            {
                _probed = true;
                try { Input.GetKey(KeyCode.None); _legacy = true; }
                catch { _legacy = false; }
            }

            if (_legacy)
            {
                try { return pressedThisFrame ? Input.GetKeyDown(key) : Input.GetKey(key); }
                catch { _legacy = false; }
            }

            try
            {
                var kb = Keyboard.current;
                if (kb == null) return false;
                Key k;
                if (!Enum.TryParse(Translate(key), true, out k) || k == Key.None) return false;
                ButtonControl c = kb[k];
                return c != null && (pressedThisFrame ? c.wasPressedThisFrame : c.isPressed);
            }
            catch { return false; }
        }

        /// <summary>The names the two enums disagree on; the rest match.</summary>
        private static string Translate(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.Return: return "Enter";
                case KeyCode.Alpha0: return "Digit0";
                case KeyCode.Alpha1: return "Digit1";
                case KeyCode.Alpha2: return "Digit2";
                case KeyCode.Alpha3: return "Digit3";
                case KeyCode.Alpha4: return "Digit4";
                case KeyCode.Alpha5: return "Digit5";
                case KeyCode.Alpha6: return "Digit6";
                case KeyCode.Alpha7: return "Digit7";
                case KeyCode.Alpha8: return "Digit8";
                case KeyCode.Alpha9: return "Digit9";
                case KeyCode.LeftControl: return "LeftCtrl";
                case KeyCode.RightControl: return "RightCtrl";
                case KeyCode.BackQuote: return "Backquote";
                default: return key.ToString();
            }
        }
    }
}
