using System.Collections.Generic;
using System.Reflection;
using GorillaLocomotion;
using GorillaRagdoll.Runtime;
using NextBots.Runtime;
using NextBots.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NextBotsRagdoll
{
    /// <summary>
    /// The kill cam, as a real object in the world.
    ///
    /// <para><b>What you see.</b> A little film camera - body, two reels, a lens and a blinking
    /// REC light - flying around your body with its view cone drawn in front of it. What it
    /// films goes to a floating screen in front of you in VR and to the corner of the monitor.
    /// The model is there so the shot can be <i>seen</i> being taken: where the camera is, what
    /// it is pointed at, when it swings. Turn it off with <c>ShowCameraModel</c>.</para>
    ///
    /// <para><b>The shot, in two beats.</b> First it hangs off to the side of your flight and
    /// follows the body, so the whole launch crosses the frame. After <c>FocusDelay</c> it swings
    /// round behind the body and zooms onto the bot that did it - Team Fortress 2's freeze cam,
    /// over your own corpse's shoulder.</para>
    ///
    /// <para><b>Why a screen and not the headset.</b> The headset view already belongs to the
    /// ragdoll mod's VR modes, and a camera that swoops and zooms in someone's face is the
    /// fastest way to make them ill. A screen at arm's length gives the same shot with none of
    /// that. It is fixed to your body, not your head, so where you look is always your own.</para>
    ///
    /// <para><b>How the lens is made.</b> The monitor camera's recipe, which is proven on this
    /// install: clone the headset camera, force it mono, and copy URP's per-camera data across.
    /// A camera built from scratch under URP renders grey. It renders into a small texture, and
    /// only while you are down - it is a whole extra render of the scene.</para>
    /// </summary>
    public sealed class KillCam : MonoBehaviour
    {
        public static KillCam Instance { get; private set; }

        public bool Active { get; private set; }

        private const float ScreenWidth = 0.48f;
        private const float ScreenDistance = 0.75f;
        private const float ScreenDrop = 0.18f;
        private const float ConeLength = 0.6f;

        // ---- the object
        private Transform _cameraRig;
        private Camera _lens;
        private RenderTexture _texture;
        private GameObject _model;
        private GameObject _recLight;
        private LineRenderer _cone;
        private float _coneFov = -1f;

        private Transform _screen;
        private Material _screenMat;
        private PanelText _screenTitle;
        private PanelText _screenInfo;

        /// <summary>The model and the screen, hidden from the lens while it renders so the camera
        /// does not film the inside of itself or its own screen.</summary>
        private readonly List<Renderer> _hideFromLens = new List<Renderer>(16);

        // ---- the shot
        private NextBot _bot;
        private Vector3 _botLast;
        private bool _haveBot;
        private Vector3 _throwDir;
        private Vector3 _side;
        private float _startedAt;
        private float _endsAt;
        private string _title = "";
        private string _info = "";

        private Vector3 _pos, _posVel, _look, _lookVel;

        /// <summary>The screen's offset from your eye, in your body's frame.</summary>
        private Vector3 _screenLocal;
        private bool _screenPlaced;
        private float _nextText;

        // ================================================================== lifecycle

        private void Awake() => Instance = this;

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            RenderPipelineManager.endCameraRendering -= OnEndCamera;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_texture != null) { _texture.Release(); Destroy(_texture); }
        }

        // ================================================================== control

        /// <summary>
        /// Start (or restart) the shot. Called again on a second hit while down, which simply
        /// re-aims it at the new bot.
        /// </summary>
        /// <param name="endsAt">Time.time when you get up, or 0 for "until you get up yourself".</param>
        public void Begin(NextBot bot, Vector3 hitFrom, Vector3 throwDir, Vector3 body,
                          string title, string info, float endsAt)
        {
            if (!BridgeConfig.KillCamEnabled.Value) return;
            if (!Build()) return;

            _bot = bot;
            _haveBot = bot != null;
            _botLast = bot != null ? bot.Center : hitFrom;
            _throwDir = throwDir.sqrMagnitude > 1e-4f ? throwDir.normalized : Vector3.forward;
            _side = PickSide(body, _throwDir);
            _title = title;
            _info = info;
            _endsAt = endsAt;
            _startedAt = Time.time;
            _nextText = 0f;

            if (!Active)
            {
                // Start over the bot's shoulder, looking at you: the first frames read as the
                // camera being knocked loose by the same hit.
                _pos = hitFrom + Vector3.up * 1.6f;
                _look = body;
                _posVel = _lookVel = Vector3.zero;
                _screenPlaced = false;
            }

            _cameraRig.gameObject.SetActive(true);
            _model.SetActive(BridgeConfig.KillCamModel.Value);
            _cone.gameObject.SetActive(BridgeConfig.KillCamModel.Value);
            _screen.gameObject.SetActive(BridgeConfig.KillCamHeadsetScreen.Value);
            if (_lens != null) _lens.enabled = true;
            Active = true;
        }

        public void End()
        {
            if (!Active) return;
            Active = false;
            _bot = null;
            if (_lens != null) _lens.enabled = false;
            if (_screen != null) _screen.gameObject.SetActive(false);
            if (_cameraRig != null) _cameraRig.gameObject.SetActive(false);
        }

        // ================================================================== the shot

        private void LateUpdate()
        {
            if (!Active) return;

            var ctrl = GorillaRagdoll.Plugin.Controller;
            var pose = ctrl != null ? ctrl.Pose : null;
            if (pose == null || pose.Body == null) { End(); return; }

            Vector3 body = pose.Body.position;
            if (_bot != null && _bot.Alive) _botLast = _bot.Center;
            else _bot = null;   // despawned mid-shot: keep aiming at where it was

            float t = Time.time - _startedAt;
            float focusAt = BridgeConfig.KillCamFocusDelay.Value;
            bool onKiller = _haveBot && t >= focusAt;

            Vector3 wantPos, wantLook;
            float fov = BridgeConfig.KillCamFov.Value;

            if (!onKiller)
            {
                // Beat one: side-on to the throw and a little behind where it started, so the
                // body flies across the frame rather than away from the lens.
                wantLook = body;
                wantPos = body + _side * 3f + Vector3.up * 1.2f - _throwDir * 1f;
            }
            else
            {
                // Beat two: behind the body, looking past it at the bot, zooming in.
                Vector3 fromBot = body - _botLast;
                fromBot.y = 0f;
                fromBot = fromBot.sqrMagnitude > 1e-4f ? fromBot.normalized : -_throwDir;

                wantPos = body + fromBot * 1.2f + Vector3.up * 0.9f;
                wantLook = _botLast;
                float zoom = Mathf.SmoothStep(0f, 1f, (t - focusAt) / 0.6f);
                fov = Mathf.Lerp(fov, BridgeConfig.KillCamZoomFov.Value, zoom);
            }

            wantPos = KeepInSight(body + Vector3.up * 0.4f, wantPos);

            // Position is damped; the aim point is damped separately rather than the rotation,
            // for the same reason as the ragdoll's orbit camera: easing a rotation lets a moving
            // target drift out of frame, easing what it looks at does not.
            _pos = Vector3.SmoothDamp(_pos, wantPos, ref _posVel, 0.18f);
            _look = Vector3.SmoothDamp(_look, wantLook, ref _lookVel, 0.08f);

            _cameraRig.position = _pos;
            Vector3 aim = _look - _pos;
            if (aim.sqrMagnitude > 1e-4f) _cameraRig.rotation = Quaternion.LookRotation(aim, Vector3.up);

            if (_lens != null) _lens.fieldOfView = fov;
            UpdateCone(fov);

            _recLight.SetActive(Mathf.Repeat(Time.time, 1f) < 0.6f);

            PlaceScreen();
            UpdateText();
        }

        /// <summary>Pulls the camera in front of anything between it and the body. Without this,
        /// half of all shots in GT's forest are the inside of a tree.</summary>
        private static Vector3 KeepInSight(Vector3 anchor, Vector3 want)
        {
            int mask;
            try { mask = GTPlayer.LocomotionEnabledLayers; }
            catch { mask = ~0; }

            Vector3 d = want - anchor;
            float dist = d.magnitude;
            if (dist < 0.05f) return want;

            RaycastHit hit;
            if (Physics.SphereCast(anchor, 0.15f, d / dist, out hit, dist, mask, QueryTriggerInteraction.Ignore))
                return anchor + d / dist * Mathf.Max(0.3f, hit.distance - 0.05f);
            return want;
        }

        /// <summary>Of the two sides of the flight, the one with more open space.</summary>
        private static Vector3 PickSide(Vector3 body, Vector3 throwDir)
        {
            Vector3 side = Vector3.Cross(Vector3.up, throwDir);
            if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
            side.Normalize();

            int mask;
            try { mask = GTPlayer.LocomotionEnabledLayers; }
            catch { mask = ~0; }

            Vector3 from = body + Vector3.up * 0.5f;
            float right = Physics.Raycast(from, side, out var hr, 4f, mask, QueryTriggerInteraction.Ignore) ? hr.distance : 4f;
            float left = Physics.Raycast(from, -side, out var hl, 4f, mask, QueryTriggerInteraction.Ignore) ? hl.distance : 4f;
            return left > right ? -side : side;
        }

        // ================================================================== the screen

        /// <summary>
        /// Body-locked, not head-locked: the screen is placed once, in front of where you were
        /// facing, and after that it rides your <i>body</i> - GT's <c>turnParent</c> - never your
        /// head. Look up, look away, look behind you: it stays put. Turn with the stick (or orbit
        /// round your ragdoll) and it comes along, because that turns the body it is fixed to.
        ///
        /// <para>An earlier version followed your gaze once you looked 30° away. Anything that
        /// chases where you look reads as the view being dragged, which in VR is the thing to
        /// avoid above all.</para>
        /// </summary>
        private void PlaceScreen()
        {
            if (_screen == null || !_screen.gameObject.activeSelf) return;

            var p = GTPlayer.Instance;
            if (p == null || p.mainCamera == null) return;
            var eye = p.mainCamera.transform;
            var body = p.turnParent != null ? p.turnParent.transform : p.transform;

            if (!_screenPlaced)
            {
                Vector3 offset = Heading(eye) * ScreenDistance + Vector3.down * ScreenDrop;
                _screenLocal = Quaternion.Inverse(body.rotation) * offset;
                _screenPlaced = true;
            }

            Vector3 world = body.rotation * _screenLocal;
            _screen.position = eye.position + world;
            _screen.rotation = Quaternion.LookRotation(world, body.up);
        }

        /// <summary>
        /// The way you are facing, level. Flattening <c>forward</c> alone falls apart when you
        /// look straight up or down - what is left is a few millimetres of noise pointing
        /// anywhere - so near vertical the head's up vector stands in: tip your head back and it
        /// points behind you, forward at your feet and it points ahead.
        /// </summary>
        private static Vector3 Heading(Transform eye)
        {
            Vector3 f = eye.forward;
            f.y = 0f;
            if (f.sqrMagnitude > 0.09f) return f.normalized;

            Vector3 u = eye.up * -Mathf.Sign(eye.forward.y);
            u.y = 0f;
            return u.sqrMagnitude > 1e-4f ? u.normalized : Vector3.forward;
        }

        private void UpdateText()
        {
            if (Time.time < _nextText) return;
            _nextText = Time.time + 0.1f;

            string info = _info;
            if (_endsAt > 0f)
                info += "  |  UP IN " + Mathf.Max(0f, _endsAt - Time.time).ToString("0.0");

            if (_screenTitle != null) _screenTitle.Text = _title;
            if (_screenInfo != null) _screenInfo.Text = info;
            _pipInfo = info;
        }

        // ================================================================== monitor

        private string _pipInfo = "";
        private GUIStyle _pipTitle, _pipText;

        private void OnGUI()
        {
            if (!Active || _texture == null || !BridgeConfig.KillCamMonitorPip.Value) return;

            if (_pipTitle == null)
            {
                _pipTitle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
                _pipTitle.normal.textColor = new Color(1f, 0.3f, 0.25f);
                _pipText = new GUIStyle(GUI.skin.label) { fontSize = 14 };
                _pipText.normal.textColor = Color.white;
            }

            float w = Mathf.Round(Screen.width * 0.3f);
            float h = Mathf.Round(w * _texture.height / _texture.width);

            // Top-right, unless the death log is there - it gets a row at exactly the moment
            // this appears, and the two would sit on top of each other.
            var feed = NextBots.UI.KillFeed.Current;
            bool feedThere = feed != null && feed.enabled && feed.showOnMonitor && feed.Top && feed.Right;
            var img = new Rect(feedThere ? 16f : Screen.width - w - 16f, 44f, w, h);

            GUI.Box(new Rect(img.x - 6f, img.y - 38f, w + 12f, h + 66f), GUIContent.none);
            GUI.Label(new Rect(img.x, img.y - 34f, w, 30f), _title, _pipTitle);
            GUI.DrawTexture(img, _texture, ScaleMode.ScaleToFit, false);
            GUI.Label(new Rect(img.x, img.yMax + 4f, w, 22f), _pipInfo, _pipText);
        }

        // ================================================================== hide from the lens

        // Wrapped like every other camera hook in the mod suite now is: these events give no
        // subscriber isolation, so one throwing silently stops every later one sharing it - see
        // DeathVignette.cs for the case that found it.
        private void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            try
            {
                if (cam == null || cam != _lens) return;
                for (int i = 0; i < _hideFromLens.Count; i++)
                    if (_hideFromLens[i] != null) _hideFromLens[i].forceRenderingOff = true;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[KillCam] camera hook: " + ex.Message); }
        }

        private void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
        {
            try
            {
                if (cam == null || cam != _lens) return;
                for (int i = 0; i < _hideFromLens.Count; i++)
                    if (_hideFromLens[i] != null) _hideFromLens[i].forceRenderingOff = false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[KillCam] camera hook: " + ex.Message); }
        }

        // ================================================================== building it

        private bool Build()
        {
            if (_cameraRig != null) return true;

            try
            {
                _cameraRig = new GameObject("KillCam.Camera").transform;
                _cameraRig.SetParent(transform, false);

                BuildLens();
                BuildModel();
                BuildCone();
                BuildScreen();

                foreach (var r in _cameraRig.GetComponentsInChildren<Renderer>(true)) _hideFromLens.Add(r);
                foreach (var r in _screen.GetComponentsInChildren<Renderer>(true)) _hideFromLens.Add(r);

                Plugin.Log.LogInfo("[KillCam] built | lens " + (_lens != null
                    ? _texture.width + "x" + _texture.height + " from '" + _sourceName + "'"
                    : "NONE - no source camera, the model will show but the screen will be blank"));
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[KillCam] could not build: " + ex);
                if (_cameraRig != null) Destroy(_cameraRig.gameObject);
                if (_screen != null) Destroy(_screen.gameObject);
                _cameraRig = null;
                _screen = null;
                return false;
            }
        }

        private string _sourceName = "?";

        private void BuildLens()
        {
            int w = Mathf.Clamp(BridgeConfig.KillCamResolution.Value, 128, 1920);
            int h = Mathf.Max(72, w * 9 / 16);
            _texture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
            {
                name = "NextBotsRagdoll.KillCam",
                antiAliasing = 2,
                hideFlags = HideFlags.HideAndDontSave
            };
            _texture.Create();

            var src = FindSource();
            if (src == null) return;
            _sourceName = src.name;

            _lens = _cameraRig.gameObject.AddComponent<Camera>();
            _lens.CopyFrom(src);
            _lens.stereoTargetEye = StereoTargetEyeMask.None;
            _lens.targetTexture = _texture;
            _lens.depth = src.depth - 1f;
            _lens.usePhysicalProperties = false;
            _lens.ResetProjectionMatrix();
            _lens.ResetAspect();
            _lens.nearClipPlane = 0.05f;
            _lens.fieldOfView = BridgeConfig.KillCamFov.Value;
            if (_lens.clearFlags == CameraClearFlags.Depth || _lens.clearFlags == CameraClearFlags.Nothing)
                _lens.clearFlags = CameraClearFlags.Skybox;

            int fpOnly = LayerMask.NameToLayer("FirstPersonOnly");
            if (fpOnly >= 0) _lens.cullingMask &= ~(1 << fpOnly);

            CopyUrpData(_lens, src);
            _lens.enabled = false;
        }

        /// <summary>A film camera out of primitives: body, two reels on top, lens barrel, glass,
        /// and a REC light. It faces +Z, which is where the lens looks.</summary>
        private void BuildModel()
        {
            _model = new GameObject("KillCam.Model");
            _model.transform.SetParent(_cameraRig, false);

            var dark = new Color(0.13f, 0.13f, 0.15f);
            var reel = new Color(0.22f, 0.22f, 0.25f);

            Part(PrimitiveType.Cube, "body", new Vector3(0f, 0f, -0.02f), Vector3.zero,
                 new Vector3(0.12f, 0.13f, 0.22f), dark);
            Part(PrimitiveType.Cylinder, "reel.front", new Vector3(0f, 0.125f, 0.045f), new Vector3(0f, 0f, 90f),
                 new Vector3(0.12f, 0.015f, 0.12f), reel);
            Part(PrimitiveType.Cylinder, "reel.back", new Vector3(0f, 0.125f, -0.085f), new Vector3(0f, 0f, 90f),
                 new Vector3(0.12f, 0.015f, 0.12f), reel);
            Part(PrimitiveType.Cylinder, "lens", new Vector3(0f, -0.005f, 0.13f), new Vector3(90f, 0f, 0f),
                 new Vector3(0.07f, 0.04f, 0.07f), Color.black);
            Part(PrimitiveType.Sphere, "glass", new Vector3(0f, -0.005f, 0.17f), Vector3.zero,
                 new Vector3(0.055f, 0.055f, 0.02f), new Color(0.15f, 0.3f, 0.55f));
            _recLight = Part(PrimitiveType.Sphere, "rec", new Vector3(0.045f, 0.05f, 0.092f), Vector3.zero,
                             Vector3.one * 0.022f, new Color(1f, 0.1f, 0.1f));
        }

        /// <summary>
        /// One primitive, with its collider removed at once. <c>CreatePrimitive</c> always adds
        /// one, and a collider on something flying round your ragdoll would hit it. The default
        /// material is swapped too: its shader is not guaranteed to exist in a built game.
        /// </summary>
        private GameObject Part(PrimitiveType type, string name, Vector3 pos, Vector3 euler, Vector3 scale, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            var col = go.GetComponent<Collider>();
            if (col != null) DestroyImmediate(col);

            go.name = name;
            go.transform.SetParent(_model.transform, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = scale;

            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = UiResources.NewColorMaterial(color);
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        /// <summary>The view cone: four edges from the lens to a rectangle, matching the live
        /// field of view, so the zoom is visible from outside as the cone narrowing.</summary>
        private void BuildCone()
        {
            var go = new GameObject("KillCam.Cone");
            go.transform.SetParent(_cameraRig, false);
            _cone = go.AddComponent<LineRenderer>();
            _cone.useWorldSpace = false;
            _cone.positionCount = 11;
            _cone.widthMultiplier = 0.004f;
            _cone.sharedMaterial = UiResources.NewColorMaterial(new Color(1f, 0.85f, 0.2f));
            _cone.shadowCastingMode = ShadowCastingMode.Off;
            _cone.receiveShadows = false;
            UpdateCone(BridgeConfig.KillCamFov.Value);
        }

        private void UpdateCone(float fov)
        {
            if (_cone == null || Mathf.Abs(fov - _coneFov) < 0.05f) return;
            _coneFov = fov;

            float aspect = _texture != null ? (float)_texture.width / _texture.height : 16f / 9f;
            float hh = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) * ConeLength;
            float hw = hh * aspect;
            var apex = new Vector3(0f, 0f, 0.17f);
            var c0 = new Vector3(-hw, -hh, ConeLength);
            var c1 = new Vector3(hw, -hh, ConeLength);
            var c2 = new Vector3(hw, hh, ConeLength);
            var c3 = new Vector3(-hw, hh, ConeLength);

            // One polyline covering all eight edges; the doubled-back segments are invisible.
            _cone.SetPositions(new[] { apex, c0, c1, c2, c3, c0, apex, c1, c2, apex, c3 });
        }

        private void BuildScreen()
        {
            float h = ScreenWidth * 9f / 16f;

            _screen = new GameObject("KillCam.Screen").transform;
            _screen.SetParent(transform, false);

            Material frame;
            UiResources.CreateQuad(_screen, "frame", new Vector3(0f, 0.01f, 0.004f),
                                   new Vector2(ScreenWidth + 0.03f, h + 0.1f), new Color(0.05f, 0.05f, 0.06f), out frame);

            UiResources.CreateQuad(_screen, "image", Vector3.zero, new Vector2(ScreenWidth, h), Color.white, out _screenMat);
            if (_screenMat.HasProperty("_BaseMap")) _screenMat.SetTexture("_BaseMap", _texture);
            if (_screenMat.HasProperty("_MainTex")) _screenMat.SetTexture("_MainTex", _texture);
            _screenMat.mainTexture = _texture;

            _screenTitle = new PanelText(_screen, "title", new Vector3(0f, h * 0.5f + 0.028f, -0.002f),
                                         ScreenWidth, 0.03f, new Color(1f, 0.3f, 0.25f), TextAlignmentOptions.Center);
            _screenInfo = new PanelText(_screen, "info", new Vector3(0f, -h * 0.5f - 0.022f, -0.002f),
                                        ScreenWidth, 0.018f, new Color(0.85f, 0.85f, 0.85f), TextAlignmentOptions.Center);

            _screen.gameObject.SetActive(false);
        }

        // ---- the monitor camera's recipe, reproduced: it is private to GorillaRagdoll

        private static Camera FindSource()
        {
            Camera best = null;
            float bestScore = float.NegativeInfinity;
            foreach (var c in Camera.allCameras)
            {
                if (c == null || c.targetTexture != null) continue;
                if (c.name.StartsWith("GorillaRagdoll") || c.name.StartsWith("QuickFreeCam")) continue;

                float score = c.depth;
                if (c.stereoTargetEye != StereoTargetEyeMask.None) score += 1000f;
                if (c.CompareTag("MainCamera")) score += 500f;
                if (score > bestScore) { bestScore = score; best = c; }
            }
            return best != null ? best : Camera.main;
        }

        private static void CopyUrpData(Camera dst, Camera src)
        {
            try
            {
                var dstData = dst.GetComponent<UniversalAdditionalCameraData>() ??
                              dst.gameObject.AddComponent<UniversalAdditionalCameraData>();

                // Never the headset. It renders to a texture, which URP already keeps out of XR, but
                // that is one assignment away from not being true.
                dstData.allowXRRendering = false;
                var srcData = src.GetComponent<UniversalAdditionalCameraData>();
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
                Plugin.Log.LogWarning("[KillCam] URP data copy failed, image may look wrong: " + ex.Message);
            }
        }
    }
}
