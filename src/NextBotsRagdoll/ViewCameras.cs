using GorillaLocomotion;
using UnityEngine;

namespace NextBotsRagdoll
{
    /// <summary>
    /// Which cameras are the player's own view of the world.
    ///
    /// <para>There are two. In the headset it is the eye camera. On the monitor it is the eye camera
    /// too - until the ragdoll mod's third-person orbit takes over, and then it is that camera, which
    /// is a separate one entirely. Anything that lives in front of the player's view - the death
    /// vignette - has to draw for both, or it silently vanishes from the monitor the
    /// moment you go third person, which is exactly when you have just been knocked down.</para>
    ///
    /// <para>Every other camera - the kill cam's lens, anything another mod adds - is not the
    /// player's view, and must not see these: to it, a picture hung in front of your eyes is just a
    /// floating sign next to your head.</para>
    /// </summary>
    internal static class ViewCameras
    {
        public static Camera Eye
        {
            get
            {
                var p = GTPlayer.Instance;
                return p != null ? p.mainCamera : null;
            }
        }

        public static Camera Monitor
        {
            get
            {
                var ctrl = GorillaRagdoll.Plugin.Controller;
                var m = ctrl != null ? ctrl.Monitor : null;
                return m != null && m.Alive ? m.Cam : null;
            }
        }

        public static bool IsPlayerView(Camera cam)
        {
            if (cam == null) return false;
            return cam == Eye || cam == Monitor;
        }
    }
}
