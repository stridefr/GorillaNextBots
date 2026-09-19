using System.Reflection;
using GorillaLocomotion;
using GorillaRagdoll.Config;
using GorillaRagdoll.Runtime;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GorillaRagdoll.Cameras
{
    /// <summary>
    /// A mod-owned camera that renders the ragdoll view to the monitor, leaving the headset
    /// alone entirely.
    ///
    /// <para>The pattern is the one QuickFreeCam already proves on this install: clone the
    /// highest-depth non-stereo camera, force <c>stereoTargetEye = None</c> so it never
    /// reaches the HMD, sit above the source in depth, and carry the URP renderer data across
    /// so the image is not untextured. Building a camera from scratch instead gets you a grey
    /// screen under URP.</para>
    ///
    /// <para>Look controls are hold-right-mouse rather than a cursor lock. That is a
    /// deliberate simplification: capturing the cursor means restoring it correctly on every
    /// exit path, including alt-tab and a mid-ragdoll crash, and the failure mode is a player
    /// who cannot click anything.</para>
    /// </summary>
    public sealed class MonitorCamera
    {
        private Camera _cam;
        private Transform _tf;
        private Camera _source;

        private float _yaw, _pitch;

        /// <summary>Where the Free orbit is looking - separate from where it sits.</summary>
        private float _lookYaw, _lookPitch;
        private Vector3 _pos;
        private Vector3 _posVelocity;
        private float _distance;
        private Vector3 _lookAt;
        private Vector3 _lookVelocity;
        private readonly OccluderFade _fade = new OccluderFade();
        private bool _placed;
        private float _speed;

        public bool Alive => _cam != null;

        /// <summary>Live speed, so the overlay can show what scroll has done to it.</summary>
        public float Speed => _speed;

        public void Ensure()
        {
            if (_cam != null) return;

            var src = FindSource();
            if (src == null) { Plugin.Log.LogWarning("[Cam] no source camera to clone"); return; }
            _source = src;

            var go = new GameObject("GorillaRagdoll.Camera");
            go.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(go);

            _cam = go.AddComponent<Camera>();
            _tf = go.transform;
            _cam.CopyFrom(src);
            _cam.targetTexture = null;
            _cam.stereoTargetEye = StereoTargetEyeMask.None;
            _cam.targetDisplay = 0;
            _cam.rect = new Rect(0f, 0f, 1f, 1f);
            _cam.depth = src.depth + RagdollConfig.CameraDepthOffset.Value;
            _cam.ResetProjectionMatrix();
            _cam.ResetAspect();
            _cam.usePhysicalProperties = false;
            _cam.fieldOfView = RagdollConfig.FreeCamFov.Value;
            if (_cam.clearFlags == CameraClearFlags.Depth || _cam.clearFlags == CameraClearFlags.Nothing)
                _cam.clearFlags = CameraClearFlags.Skybox;

            // First-person-only props are staged for the headset and look like floating junk
            // from any other angle.
            int fpOnly = LayerMask.NameToLayer("FirstPersonOnly");
            if (fpOnly >= 0) _cam.cullingMask &= ~(1 << fpOnly);

            CopyUrpData(_cam, src);

            _speed = RagdollConfig.FreeCamSpeed.Value;
            _placed = false;
            Plugin.Log.LogInfo("[Cam] monitor camera created from '" + src.name +
                               "' at depth " + _cam.depth.ToString("0"));
        }

        public void Destroy()
        {
            _fade.RestoreAll();
            if (_cam != null) Object.Destroy(_cam.gameObject);
            _cam = null;
            _tf = null;
            _source = null;
            _placed = false;
        }

        /// <summary>Highest-depth camera that is not ours and not rendering to a texture.</summary>
        private Camera FindSource()
        {
            Camera best = null;
            float bestScore = float.NegativeInfinity;

            foreach (var c in Camera.allCameras)
            {
                if (c == null || c == _cam || c.targetTexture != null) continue;

                float score = c.depth;
                if (c.stereoTargetEye != StereoTargetEyeMask.None) score += 1000f;
                if (c.CompareTag("MainCamera")) score += 500f;
                if (score > bestScore) { bestScore = score; best = c; }
            }
            return best != null ? best : Camera.main;
        }

        /// <summary>
        /// URP keeps per-camera renderer settings in a sidecar component, and the renderer
        /// index behind a private field. Without both, the clone renders with the wrong
        /// renderer or none at all.
        /// </summary>
        private static void CopyUrpData(Camera dst, Camera src)
        {
            try
            {
                var dstData = dst.GetComponent<UniversalAdditionalCameraData>()
                              ?? dst.gameObject.AddComponent<UniversalAdditionalCameraData>();
                var srcData = src != null ? src.GetComponent<UniversalAdditionalCameraData>() : null;
                if (srcData == null) return;

                dstData.renderShadows = srcData.renderShadows;
                dstData.requiresColorOption = srcData.requiresColorOption;
                dstData.requiresDepthOption = srcData.requiresDepthOption;
                dstData.renderPostProcessing = srcData.renderPostProcessing;
                dstData.antialiasing = srcData.antialiasing;
                dstData.antialiasingQuality = srcData.antialiasingQuality;
                dstData.volumeLayerMask = srcData.volumeLayerMask;
                dstData.renderType = CameraRenderType.Base;

                var field = typeof(UniversalAdditionalCameraData)
                    .GetField("m_RendererIndex", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null) field.SetValue(dstData, field.GetValue(srcData));
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[Cam] URP data copy failed, image may look wrong: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ per-frame

        public void Tick(CameraMode mode, Transform head, Quaternion headToView, bool menuOpen)
        {
            if (_cam == null || _tf == null) return;

            _cam.fieldOfView = RagdollConfig.FreeCamFov.Value;

            switch (mode)
            {
                case CameraMode.FirstPersonLocked: FirstPerson(head, headToView, true); break;
                case CameraMode.FirstPersonUnlocked: FirstPerson(head, headToView, false); break;
                case CameraMode.ThirdPersonFree: Orbit(head, menuOpen); break;
                case CameraMode.ThirdPersonFly: FreeCam(head, menuOpen); break;
            }
        }

        /// <summary>
        /// Orbit camera: circles the body, keeps it framed, and never lets scenery get between
        /// you and it.
        ///
        /// <para>Three things make it feel right rather than merely work.</para>
        ///
        /// <para><b>It glides in.</b> The camera is seeded at the game camera's current pose and
        /// eased to the orbit position, so collapsing is a move rather than a cut.</para>
        ///
        /// <para><b>It is damped, not snapped.</b> Position uses SmoothDamp and rotation an
        /// exponential ease, both frame-rate independent, so a tumbling body does not make the
        /// camera judder.</para>
        ///
        /// <para><b>It refuses to be blocked.</b> A spherecast runs from the body outwards along
        /// the view direction; anything in the way pulls the camera in to just in front of it.
        /// This is how third-person games solve occlusion, and it is strictly better here than
        /// fading the obstruction: GT's world geometry shares materials across the whole map, so
        /// making a wall transparent would make <i>every</i> wall using that material
        /// transparent. Pulling in is local, instant and cannot damage the scene.</para>
        ///
        /// <para><b>Where it looks is yours</b> (<c>OrbitAim = Free</c>, the default). Where the
        /// camera <i>sits</i> and where it <i>looks</i> are two separate things: it stays
        /// tethered to the body as before, but the mouse aims it, the way your head aims the
        /// headset. Circling the body moved to A/D (and W/S for height), and that turns the view
        /// by the same amount - the same as the VR stick orbit - so orbiting keeps the body where
        /// it was on screen. C re-aims at the body. The old behaviour, where the aim was welded
        /// to the body and the mouse could only swing the camera round it, is
        /// <c>OrbitAim = LockOnBody</c>.</para>
        /// </summary>
        private void Orbit(Transform head, bool menuOpen)
        {
            bool free = RagdollConfig.MonitorOrbitAim.Value == OrbitAim.Free;
            Vector3 target = (head != null ? head.position : _pos) +
                             Vector3.up * RagdollConfig.ThirdPersonHeight.Value;

            if (!_placed)
            {
                // Start where the player is already looking from, so the move reads as a glide.
                var from = _source != null ? _source.transform : null;
                _pos = from != null ? from.position : target;
                if (_tf != null && from != null) _tf.rotation = from.rotation;

                Vector3 back = target - _pos;
                back.y = 0f;
                _yaw = back.sqrMagnitude > 0.01f
                    ? Quaternion.LookRotation(back.normalized, Vector3.up).eulerAngles.y
                    : 0f;
                _pitch = 15f;
                _distance = RagdollConfig.ThirdPersonDistance.Value;
                _posVelocity = Vector3.zero;
                _lookAt = target;
                _lookVelocity = Vector3.zero;
                // Starts aimed at the body - you collapsed, you want to see it - and is yours
                // from the first mouse movement.
                _lookYaw = _yaw;
                _lookPitch = _pitch;
                _fade.SetExclusion(VRRig.LocalRig != null ? VRRig.LocalRig.transform : null);
                _placed = true;
            }

            if (!menuOpen)
            {
                if (free)
                {
                    MouseLook(ref _lookYaw, ref _lookPitch);
                    OrbitKeys();
                    if (SafeInput.KeyDown(KeyCode.C) || SafeInput.Mouse(2)) AimAt(target);
                }
                else MouseLook(ref _yaw, ref _pitch);

                float scroll = SafeInput.Scroll();
                if (Mathf.Abs(scroll) > 0.01f)
                    _distance = Mathf.Clamp(_distance * (scroll > 0f ? 1f / 1.15f : 1.15f),
                                            RagdollConfig.ThirdPersonMinDistance.Value, 30f);
            }

            // The tether never swings below the body: that is under the floor.
            if (free) _pitch = Mathf.Clamp(_pitch, -10f, 80f);

            Vector3 dir = Quaternion.Euler(_pitch, _yaw, 0f) * Vector3.back;
            float dist = _distance;

            // Keep the body in sight: if scenery blocks the line, come closer than it.
            int mask;
            try { mask = GTPlayer.LocomotionEnabledLayers; }
            catch { mask = ~0; }

            float probe = RagdollConfig.OcclusionRadius.Value;
            RaycastHit hit;
            if (Physics.SphereCast(target, probe, dir, out hit, dist, mask, QueryTriggerInteraction.Ignore))
                dist = Mathf.Max(RagdollConfig.ThirdPersonMinDistance.Value, hit.distance);

            Vector3 desired = target + dir * dist;

            float smooth = Mathf.Max(0.02f, RagdollConfig.CameraSmoothing.Value);
            _pos = Vector3.SmoothDamp(_pos, desired, ref _posVelocity, smooth);
            _tf.position = _pos;

            if (free)
            {
                // Straight from the mouse, no easing: an eased look is a laggy look.
                _tf.rotation = Quaternion.Euler(_lookPitch, _lookYaw, 0f);
            }
            else
            {
                // Aim is exact, never eased. Smoothing the *rotation* lets a moving body drift
                // out of frame; smoothing the point we aim at instead keeps the gorilla dead
                // centre while still absorbing the jitter of a physics-driven target.
                _lookAt = Vector3.SmoothDamp(_lookAt, target, ref _lookVelocity, smooth);
                Vector3 look = _lookAt - _pos;
                if (look.sqrMagnitude > 1e-4f)
                    _tf.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
            }

            // Even at the closest allowed distance a wall can still be in the way, which is
            // when pulling in stops helping and fading has to take over.
            if (RagdollConfig.SeeThroughWalls.Value)
                _fade.Update(target, _pos, probe * 0.5f, mask, RagdollConfig.OccluderAlpha.Value);
            else
                _fade.RestoreAll();
        }

        private void FirstPerson(Transform head, Quaternion headToView, bool locked)
        {
            if (head == null) return;

            Vector3 eye = head.TransformPoint(RagdollConfig.EyeOffset.Value);
            float smooth = RagdollConfig.CameraSmoothing.Value;

            _pos = (!_placed || smooth <= 0.001f)
                ? eye
                : Vector3.Lerp(_pos, eye, 1f - Mathf.Exp(-Time.deltaTime / smooth));
            _placed = true;
            _tf.position = _pos;

            if (locked)
            {
                // headToView is measured live at collapse, so this looks where the ragdoll's
                // head looks regardless of which way the bone's own axes happen to point.
                _tf.rotation = head.rotation * headToView *
                               Quaternion.Euler(RagdollConfig.HeadRotationOffset.Value);
                // Keep the free-look angles in step so unlocking does not snap the view.
                var e = _tf.rotation.eulerAngles;
                _yaw = e.y;
                _pitch = Normalize(e.x);
            }
            else
            {
                MouseLook();
                _tf.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }
        }

        private void FreeCam(Transform head, bool menuOpen)
        {
            if (!_placed)
            {
                // Start behind and above the ragdoll, looking at it.
                Vector3 anchor = head != null ? head.position : Vector3.zero;
                Vector3 back = _source != null ? -_source.transform.forward : Vector3.back;
                back.y = 0f;
                if (back.sqrMagnitude < 0.01f) back = Vector3.back;
                back.Normalize();

                _pos = anchor + back * RagdollConfig.ThirdPersonDistance.Value +
                       Vector3.up * RagdollConfig.ThirdPersonHeight.Value;
                var look = Quaternion.LookRotation((anchor - _pos).normalized, Vector3.up).eulerAngles;
                _yaw = look.y;
                _pitch = Normalize(look.x);
                _placed = true;
            }

            if (!menuOpen)
            {
                MouseLook();

                float scroll = SafeInput.Scroll();
                if (Mathf.Abs(scroll) > 0.01f)
                    _speed = Mathf.Clamp(_speed * (scroll > 0f ? 1.15f : 1f / 1.15f), 0.1f, 200f);

                Vector3 move = Vector3.zero;
                if (SafeInput.Key(KeyCode.W)) move += Vector3.forward;
                if (SafeInput.Key(KeyCode.S)) move += Vector3.back;
                if (SafeInput.Key(KeyCode.A)) move += Vector3.left;
                if (SafeInput.Key(KeyCode.D)) move += Vector3.right;
                if (SafeInput.Key(KeyCode.E)) move += Vector3.up;
                if (SafeInput.Key(KeyCode.Q)) move += Vector3.down;

                if (move.sqrMagnitude > 0f)
                {
                    float mul = 1f;
                    if (SafeInput.Key(KeyCode.LeftShift) || SafeInput.Key(KeyCode.RightShift)) mul = 4f;
                    else if (SafeInput.Key(KeyCode.LeftControl) || SafeInput.Key(KeyCode.RightControl)) mul = 0.25f;

                    _pos += Quaternion.Euler(_pitch, _yaw, 0f) * move.normalized * (_speed * mul * Time.deltaTime);
                }
            }

            _tf.position = _pos;
            _tf.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        private void MouseLook() => MouseLook(ref _yaw, ref _pitch);

        private static void MouseLook(ref float yaw, ref float pitch)
        {
            if (!SafeInput.Mouse(1)) return;

            float s = RagdollConfig.FreeCamSensitivity.Value;
            yaw += SafeInput.Axis("Mouse X") * s;
            float dy = SafeInput.Axis("Mouse Y") * s;
            pitch += RagdollConfig.InvertY.Value ? dy : -dy;
            pitch = Mathf.Clamp(pitch, -89f, 89f);
        }

        /// <summary>A/D circle the body and W/S raise or lower the camera, for the Free orbit.
        /// Circling turns the view with it, so the body stays where it was on screen.</summary>
        private void OrbitKeys()
        {
            float step = RagdollConfig.MonitorOrbitKeySpeed.Value * Time.deltaTime;

            float yaw = 0f;
            if (SafeInput.Key(KeyCode.A)) yaw += step;
            if (SafeInput.Key(KeyCode.D)) yaw -= step;
            if (yaw != 0f)
            {
                _yaw += yaw;
                _lookYaw += yaw;
            }

            if (SafeInput.Key(KeyCode.W)) _pitch += step;
            if (SafeInput.Key(KeyCode.S)) _pitch -= step;
        }

        /// <summary>Points the Free orbit's view at the body again, from wherever it now sits.</summary>
        private void AimAt(Vector3 target)
        {
            if (_tf == null) return;
            Vector3 d = target - _tf.position;
            if (d.sqrMagnitude < 1e-4f) return;
            var e = Quaternion.LookRotation(d.normalized, Vector3.up).eulerAngles;
            _lookYaw = e.y;
            _lookPitch = Normalize(e.x);
        }

        private static float Normalize(float angle)
        {
            angle %= 360f;
            if (angle > 180f) angle -= 360f;
            return angle;
        }
    }
}
