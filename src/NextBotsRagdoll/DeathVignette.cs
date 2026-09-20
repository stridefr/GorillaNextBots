using NextBots.UI;
using UnityEngine;
using UnityEngine.Rendering;

namespace NextBotsRagdoll
{
    /// <summary>
    /// What being caught does to the view: red at the edges for a moment, then the colour draining
    /// out of everything and the edges going dark, held while you are down, and then the colour
    /// slowly coming back as you get up.
    ///
    /// <para><b>What this can and cannot be.</b> Real desaturation - taking each pixel towards its own
    /// grey - is a shader, and the game's build has none of URP's post-processing shaders in it, so
    /// there is nothing to switch on. What a plain see-through layer can do is blend the picture
    /// towards a flat grey, and that does drain the colour (the chroma is scaled by exactly what is
    /// left) but also flattens the contrast, and a <i>light</i> grey reads as a white haze. So the
    /// layer is a dark grey: it takes the colour out and dims the world, the way a low-health screen
    /// does, instead of fogging it.</para>
    ///
    /// <para><b>Two layers.</b> A grey wash across the whole view, which is what drains the colour, and a
    /// separate edge layer that is red at first and goes dark. Keeping the red on its own layer is what
    /// lets the middle of the view stay clear for the flash instead of the whole picture going red.</para>
    ///
    /// <para><b>Sized from the camera.</b> Each layer is stretched to the camera's own field of view and
    /// aspect ratio, with a margin, so it reaches the corners of a wide monitor as well as a square
    /// headset eye. It is set per camera, just before that camera renders, which is also what lets one
    /// pair of quads serve the headset and the monitor's third-person view.</para>
    ///
    /// <para><b>Comfort.</b> The red is brief and does not pulse, the middle of the view is never
    /// blacked out, and it is one line to switch off.</para>
    /// </summary>
    public sealed class DeathVignette : MonoBehaviour
    {
        public static DeathVignette Instance { get; private set; }

        /// <summary>How long the red takes to turn dark.</summary>
        private const float Drain = 0.45f;

        /// <summary>Nothing stays on the view for ever, whatever happens to the knockdown.</summary>
        private const float MaxHold = 20f;

        private static readonly Color Blood = new Color(0.66f, 0.03f, 0.03f);
        private static readonly Color DarkEdge = new Color(0.02f, 0.02f, 0.03f);

        private Transform _root;
        private MeshRenderer _washRenderer, _edgeRenderer;
        private Material _washMat, _edgeMat;
        private Texture2D _washTex, _edgeTex;

        private float _started = -1f;
        private float _released = -1f;
        private bool _loggedView;

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
            Clear();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_root != null) Destroy(_root.gameObject);
            foreach (var o in new UnityEngine.Object[] { _washTex, _edgeTex, _washMat, _edgeMat })
                if (o != null) Destroy(o);
        }

        // ================================================================== playing

        /// <summary>Caught. Flash, drain, and hold until <see cref="Release"/>.</summary>
        public void Begin()
        {
            if (!BridgeConfig.VignetteEnabled.Value) return;
            if (!Build()) return;

            _started = Time.unscaledTime;
            _released = -1f;
            _loggedView = false;
            _root.gameObject.SetActive(true);

            var eye = ViewCameras.Eye;
            var mon = ViewCameras.Monitor;
            Plugin.Log.LogInfo("[Vignette] on | strength " + BridgeConfig.VignetteStrength.Value.ToString("0.00") +
                               " wash " + BridgeConfig.VignetteWash.Value.ToString("0.00") +
                               " grey " + BridgeConfig.VignetteGrey.Value.ToString("0.00") +
                               " over " + BridgeConfig.VignetteSeconds.Value.ToString("0.0") + "s" +
                               " | colour space " + QualitySettings.activeColorSpace +
                               " | eye camera " + (eye != null ? "'" + eye.name + "'" : "none") +
                               " | monitor camera " + (mon != null ? "'" + mon.name + "'" : "none"));
        }

        /// <summary>On your feet again: the colour comes back over <c>RecoverSeconds</c>.</summary>
        public void Release()
        {
            if (_started >= 0f && _released < 0f) _released = Time.unscaledTime;
        }

        private void LateUpdate()
        {
            if (_started < 0f) return;

            float age = Time.unscaledTime - _started;
            float recover = BridgeConfig.VignetteRecoverSeconds.Value;

            // Never held for ever: a knockdown that ends in some way this never hears about still
            // lets go of the view.
            if (_released < 0f && age > MaxHold) _released = Time.unscaledTime;

            float fade = 1f;
            if (_released >= 0f)
            {
                float since = Time.unscaledTime - _released;
                if (since >= recover) { Clear(); return; }
                // Eased, so the last of the colour returns gently rather than snapping back.
                fade = Mathf.SmoothStep(1f, 0f, since / Mathf.Max(0.01f, recover));
            }

            float strength = BridgeConfig.VignetteStrength.Value;
            float red = BridgeConfig.VignetteRedSeconds.Value;
            float toDark = Mathf.Clamp01((age - red) / Drain);

            // The whole effect lessens by itself: full for the flash and the moments after it, then
            // easing away over Seconds. By the time you are back on your feet most of it is already
            // gone, so getting up only has a little left to hand back - which is what makes the return
            // smooth instead of a step. Releasing fades whatever remains on top of that.
            float hold = red + Drain;
            float seconds = Mathf.Max(1f, BridgeConfig.VignetteSeconds.Value);
            float level = 1f - Mathf.SmoothStep(0f, 1f, (age - hold) / seconds);
            if (level <= 0.002f) { Clear(); return; }
            level *= fade;

            // The edge: blood red for the hit, then dark.
            var edge = Space(Color.Lerp(Blood, DarkEdge, toDark));
            float punch = age < red ? 1f : Mathf.Lerp(1f, 0.68f, toDark);
            edge.a = strength * punch * level;

            // The wash: the colour going out of the world. It builds through the red - the hit lands
            // first, then the world drains - and then it is what lessens over time.
            float grey = Mathf.Clamp01(BridgeConfig.VignetteGrey.Value);
            var wash = Space(new Color(grey, grey, grey * 1.04f, 0f));
            wash.a = BridgeConfig.VignetteWash.Value * strength * Mathf.SmoothStep(0f, 1f, age / hold) * level;

            if (_edgeMat != null) UiResources.TrySetColor(_edgeMat, edge);
            if (_washMat != null) UiResources.TrySetColor(_washMat, wash);
        }

        /// <summary>
        /// The colours here are written the way the eye reads them - 0.2 is a dark grey. A project
        /// rendering in linear colour space wants them converted, or the same 0.2 lands as a light
        /// grey, and a light grey over the picture is exactly the white haze this is meant not to be.
        /// </summary>
        private static Color Space(Color c) =>
            QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;

        private void Clear()
        {
            _started = -1f;
            _released = -1f;
            if (_root != null) _root.gameObject.SetActive(false);
        }

        /// <summary>
        /// In front of whichever camera is about to render, stretched to fill exactly what it sees.
        /// The margin covers the ways a projection is not a clean rectangle: a headset eye's frustum is
        /// lopsided, and a monitor can be any shape.
        /// </summary>
        private void PlaceFor(Camera cam)
        {
            var t = cam.transform;

            // As close as it can be. Anything nearer the camera than this layer pokes through it -
            // a wall you stand next to cuts a hole in the wash - and the one place nothing can be
            // nearer is closer than the camera's own near plane, which clips it away. So on a monitor
            // it sits just beyond that. A headset has two eyes a few centimetres either side of the
            // camera's centre, and at that range the layer would not cover both, so it stays back.
            float distance = cam.stereoEnabled
                ? 0.12f
                : Mathf.Max(0.02f, cam.nearClipPlane * 1.5f + 0.002f);

            float vfov = cam.fieldOfView;
            if (vfov < 20f || vfov > 170f || cam.stereoEnabled) vfov = Mathf.Max(vfov, 100f);
            float h = 2f * distance * Mathf.Tan(vfov * 0.5f * Mathf.Deg2Rad);
            float aspect = cam.aspect > 0.2f ? cam.aspect : 1.6f;
            float w = h * aspect;
            float margin = cam.stereoEnabled ? 1.9f : 1.2f;

            _root.position = t.position + t.forward * distance;
            _root.rotation = Quaternion.LookRotation(t.forward, t.up);
            _root.localScale = new Vector3(w * margin, h * margin, 1f);
        }

        // ================================================================== the layers

        private bool Build()
        {
            if (_root != null) return true;
            if (ViewCameras.Eye == null) return false;

            var go = new GameObject("NextBotsRagdoll.DeathVignette");
            go.transform.SetParent(transform, false);
            _root = go.transform;

            _washTex = BuildWashTexture();
            _edgeTex = BuildEdgeTexture();
            _washMat = SeeThrough(_washTex, (int)RenderQueue.Overlay - 70);
            _edgeMat = SeeThrough(_edgeTex, (int)RenderQueue.Overlay - 69);

            _washRenderer = Layer("wash", _washMat);
            _edgeRenderer = Layer("edge", _edgeMat);

            go.SetActive(false);
            return true;
        }

        private MeshRenderer Layer(string name, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root, false);
            go.AddComponent<MeshFilter>().sharedMesh = UiResources.Quad;
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.sharedMaterial = mat;
            return r;
        }

        /// <summary>The wash: even across the view, a little heavier towards the edges so it reads as a
        /// vignette and not a flat sheet.</summary>
        private static Texture2D BuildWashTexture()
        {
            const int size = 64;
            var tex = NewTexture(size, "NextBotsRagdoll.Wash");
            var pix = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float r = Radius(x, y, size);
                    float a = Mathf.Lerp(0.78f, 1f, Mathf.SmoothStep(0.2f, 1f, r));
                    pix[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(pix);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>The edge layer: clear across the middle of the view, coming in from about a third of
        /// the way out and solid at the very edge and corners.</summary>
        private static Texture2D BuildEdgeTexture()
        {
            const int size = 128;
            var tex = NewTexture(size, "NextBotsRagdoll.Edge");
            var pix = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float r = Radius(x, y, size);
                    float a = Mathf.SmoothStep(0.33f, 0.98f, r);
                    pix[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(pix);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>0 in the middle, 1 at the corners: distance from the centre over the distance to a
        /// corner, so the edge layer reaches the corners of whatever shape the quad is stretched to.</summary>
        private static float Radius(int x, int y, int size)
        {
            float dx = (x + 0.5f) / size * 2f - 1f;
            float dy = (y + 0.5f) / size * 2f - 1f;
            return Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) / 1.4142f);
        }

        private static Texture2D NewTexture(int size, string name)
        {
            return new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        /// <summary>Alpha-blended, and drawn after the world.</summary>
        private static Material SeeThrough(Texture tex, int queue)
        {
            Shader shader = null;
            foreach (var name in new[] { "Sprites/Default", "UI/Default", "Universal Render Pipeline/Particles/Unlit" })
            {
                try { shader = Shader.Find(name); } catch { /* try the next one */ }
                if (shader != null) break;
            }
            if (shader == null) shader = UiResources.Unlit;

            var m = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (shader != null && shader.name == "Universal Render Pipeline/Unlit")
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            m.mainTexture = tex;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            m.renderQueue = queue;
            UiResources.TrySetColor(m, new Color(1f, 1f, 1f, 0f));
            return m;
        }

        // ================================================================== which cameras see it

        // The layers are on for the player's own views - the eye camera and the ragdoll mod's
        // third-person monitor camera - and off for every other camera, each time one is about to
        // render. Placing them here, per camera, is what lets one pair of quads serve both views.
        private void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (_washRenderer == null) return;
            if (_started < 0f || !ViewCameras.IsPlayerView(cam)) { SetShown(false); return; }
            SetShown(true);
            PlaceFor(cam);

            // Said once per hit, so the log shows which camera actually drew it.
            if (!_loggedView)
            {
                _loggedView = true;
                Plugin.Log.LogInfo("[Vignette] drawing for camera '" + cam.name + "' | fov " +
                                   cam.fieldOfView.ToString("0") + " aspect " + cam.aspect.ToString("0.00") +
                                   " near " + cam.nearClipPlane.ToString("0.000") + (cam.stereoEnabled ? " stereo" : ""));
            }
        }

        private void OnEndCamera(ScriptableRenderContext ctx, Camera cam) => SetShown(false);

        private void SetShown(bool on)
        {
            if (_washRenderer != null) _washRenderer.forceRenderingOff = !on;
            if (_edgeRenderer != null) _edgeRenderer.forceRenderingOff = !on;
        }
    }
}
