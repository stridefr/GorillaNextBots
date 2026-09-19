using GorillaRagdoll.Config;
using GorillaRagdoll.Runtime;
using UnityEngine;

namespace GorillaRagdoll.UI
{
    /// <summary>
    /// Reads the toggle.
    ///
    /// <para>Keyboard is the primary binding because this is a PC-first feature, with the
    /// caveat NextBots already established the hard way: <b>keyboard input only reaches the
    /// game while the desktop window has focus</b>, which is never while you are actually
    /// wearing the headset. So an optional controller binding exists too, defaulted to None
    /// so it cannot collide with NextBots' wrist panel on the same install.</para>
    /// </summary>
    public sealed class RagdollInput : MonoBehaviour
    {
        public RagdollController Controller;
        public RagdollMenu Menu;

        private float _gestureSince = -1f;
        private bool _vrWasDown;
        private float _nextToggleAllowed;
        private bool _faulted;

        private void Update()
        {
            try { Tick(); }
            catch (System.Exception ex)
            {
                if (_faulted) return;
                _faulted = true;
                Plugin.Log.LogError("[Input] Update threw (reported once): " + ex);
            }
        }

        private void Tick()
        {
            if (Controller == null) return;

            // Panic first, so a bad camera setting can always be escaped.
            if (SafeInput.KeyDown(RagdollConfig.PanicKey.Value))
            {
                Controller.Panic();
                return;
            }

            if (SafeInput.KeyDown(RagdollConfig.MenuKey.Value) && Menu != null)
                Menu.Visible = !Menu.Visible;

            if (SafeInput.KeyDown(RagdollConfig.DumpSkeletonKey.Value))
                Controller.DumpSkeleton();

            bool toggle = SafeInput.KeyDown(RagdollConfig.ToggleKey.Value);

            bool vrDown = ReadVrBinding();
            if (vrDown && !_vrWasDown) toggle = true;
            _vrWasDown = vrDown;

            if (toggle && Time.time >= _nextToggleAllowed)
            {
                _nextToggleAllowed = Time.time + 0.25f;
                Controller.Toggle();
            }
        }

        /// <summary>
        /// In-headset toggle. Reads GT's own poller rather than Unity XR: NextBots found
        /// <c>InputDevices</c> reports face buttons as permanently unpressed on a Quest 3 over
        /// OpenVR, while the poller reads them fine.
        /// </summary>
        private bool ReadVrBinding()
        {
            var which = RagdollConfig.VrBinding.Value;
            if (string.IsNullOrEmpty(which) || which == "None") return false;

            var p = ControllerInputPoller.instance;
            if (p == null) { _gestureSince = -1f; return false; }

            bool raw;
            switch (which)
            {
                case "LeftPrimary": raw = p.leftControllerPrimaryButton; break;
                case "LeftSecondary": raw = p.leftControllerSecondaryButton; break;
                case "RightPrimary": raw = p.rightControllerPrimaryButton; break;
                case "RightSecondary": raw = p.rightControllerSecondaryButton; break;
                case "BothGrips":
                    raw = p.leftControllerGripFloat > 0.75f && p.rightControllerGripFloat > 0.75f;
                    break;
                default: raw = false; break;
            }

            if (!raw) { _gestureSince = -1f; return false; }

            // Hold applies to every binding, not just the grip gesture. A face button that
            // ragdolls the instant it is brushed will fire while you are climbing or tagging,
            // and the first you know about it is lying on the floor mid-game.
            float hold = RagdollConfig.VrGestureHold.Value;
            if (hold <= 0.01f) return true;

            if (_gestureSince < 0f) _gestureSince = Time.time;
            return Time.time - _gestureSince >= hold;
        }
    }
}
