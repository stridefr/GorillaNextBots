using GorillaLocomotion;
using NextBots.Runtime;
using NextBots.UI;
using UnityEngine;
using UnityEngine.Rendering;

namespace NextBotsRagdoll
{
    /// <summary>
    /// The bot's face, in yours, at the moment it gets you.
    ///
    /// <para><b>A quad in front of the eye camera</b>, not a screen effect: it is the bot's own
    /// image - the animated frames too, if it is a GIF - grown from small to filling your view,
    /// held, then faded out as the kill cam takes over. Nothing else about the view changes, so
    /// you can still look around and watch your body being thrown.</para>
    ///
    /// <para><b>Comfort comes first.</b> It is capped at well under a second, never strobes and
    /// can be switched off. In VR a full-face flash has to be short and predictable, and it is
    /// deliberately not a blackout: you are never blind.</para>
    ///
    /// <para><b>Only you see it.</b> Anything hung in front of your eyes is, to every other
    /// camera, a picture floating next to your head - the kill cam and the ragdoll mod's
    /// monitor camera would both film it - so the renderer is switched off for the duration of
    /// any camera that is not your eye.</para>
    /// </summary>
    public sealed class Jumpscare : MonoBehaviour
    {
        public static Jumpscare Instance { get; private set; }

        private const float RiseTime = 0.10f;
        private const float FadeTime = 0.14f;

        private Transform _root;
        private MeshRenderer _renderer;
        private Material _material;
        private Camera _eye;

        private BotSkins.Skin _skin;
        private float _started = -1f;
        private float _length;
        private int _frame;
        private float _frameAt;

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
            Hide();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_root != null) Destroy(_root.gameObject);
        }

        // ================================================================== playing

        /// <summary>
        /// Show the bot that just caught you. A bot with no image of its own - the test key with
        /// nothing spawned - shows nothing, rather than a blank rectangle across your view.
        /// </summary>
        public void Play(string skinKey, string displayName)
        {
            if (!BridgeConfig.JumpscareEnabled.Value) return;

            var skin = BotSkins.Find(string.IsNullOrEmpty(skinKey) ? displayName : skinKey);
            if (skin == null || skin.Texture == null) return;
            if (!Build()) return;

            _skin = skin;
            _frame = 0;
            _frameAt = Time.unscaledTime;
            _length = BridgeConfig.JumpscareSeconds.Value;
            _started = Time.unscaledTime;

            SetTexture(skin.Texture);
            _root.gameObject.SetActive(true);
        }

        private void LateUpdate()
        {
            if (_started < 0f) return;

            float age = Time.unscaledTime - _started;
            if (age >= _length + FadeTime) { Hide(); return; }

            Place();
            Animate();

            // Up fast with a little overshoot, hold, then fade. The overshoot is what makes it
            // read as something lunging at you rather than a picture appearing.
            float grow = Mathf.Clamp01(age / RiseTime);
            float scale = Mathf.Lerp(0.45f, 1.06f, Mathf.SmoothStep(0f, 1f, grow));
            if (age > RiseTime) scale = Mathf.Lerp(1.06f, 1f, Mathf.Clamp01((age - RiseTime) / 0.12f));

            float alpha = BridgeConfig.JumpscareOpacity.Value;
            if (age > _length) alpha *= 1f - Mathf.Clamp01((age - _length) / FadeTime);

            float height = BridgeConfig.JumpscareDistance.Value * 2.4f * scale;
            float aspect = _skin != null ? Mathf.Clamp(_skin.AspectWidthOverHeight, 0.3f, 3f) : 1f;
            _root.localScale = new Vector3(height * aspect, height, 1f);

            if (_material != null) UiResources.TrySetColor(_material, new Color(1f, 1f, 1f, alpha));
        }

        /// <summary>
        /// Straight in front of the eye, at arm's length, facing you. Placed every frame from
        /// the camera rather than parented to it, so it survives the rig being rebuilt under us
        /// when the ragdoll takes over the avatar.
        /// </summary>
        private void Place()
        {
            var eye = Eye();
            if (eye == null) return;
            var t = eye.transform;
            _root.position = t.position + t.forward * BridgeConfig.JumpscareDistance.Value;
            _root.rotation = Quaternion.LookRotation(t.forward, t.up);
        }

        /// <summary>A GIF bot scares with its animation running, at its own frame rate.</summary>
        private void Animate()
        {
            if (_skin == null || !_skin.Animated) return;

            float delay = _skin.FrameDelays != null && _frame < _skin.FrameDelays.Length
                ? Mathf.Max(0.02f, _skin.FrameDelays[_frame])
                : 0.08f;
            if (Time.unscaledTime - _frameAt < delay) return;

            _frameAt = Time.unscaledTime;
            _frame = (_frame + 1) % _skin.Frames.Length;
            SetTexture(_skin.Frames[_frame]);
        }

        private void Hide()
        {
            _started = -1f;
            _skin = null;
            if (_root != null) _root.gameObject.SetActive(false);
        }

        // ================================================================== the quad

        private Camera Eye()
        {
            if (_eye != null) return _eye;
            var p = GTPlayer.Instance;
            _eye = p != null ? p.mainCamera : null;
            return _eye;
        }

        private bool Build()
        {
            if (_root != null) return true;
            if (Eye() == null) return false;

            var go = new GameObject("NextBotsRagdoll.Jumpscare");
            go.transform.SetParent(transform, false);
            _root = go.transform;

            go.AddComponent<MeshFilter>().sharedMesh = UiResources.Quad;
            _renderer = go.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            _material = SeeThrough();
            _renderer.sharedMaterial = _material;
            go.SetActive(false);
            return true;
        }

        private void SetTexture(Texture tex)
        {
            if (_material == null || tex == null) return;
            _material.mainTexture = tex;
            if (_material.HasProperty("_BaseMap")) _material.SetTexture("_BaseMap", tex);
        }

        /// <summary>
        /// A material that honours alpha, so the picture can fade rather than vanish. Same order
        /// of preference as the death log's rows: the sprite shader blends correctly under URP
        /// and in single-pass VR, with URP's own unlit set up by hand as the fallback.
        /// </summary>
        private static Material SeeThrough()
        {
            Shader shader = null;
            foreach (var name in new[] { "Sprites/Default", "UI/Default", "Universal Render Pipeline/Unlit" })
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
            // In front of everything else we draw: the death log's rows and the kill cam screen.
            m.renderQueue = (int)RenderQueue.Overlay - 50;
            UiResources.TrySetColor(m, new Color(1f, 1f, 1f, 0f));
            return m;
        }

        // ================================================================== eye camera only

        private void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (_renderer == null || cam == null || cam == _eye) return;
            _renderer.forceRenderingOff = true;
        }

        private void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (_renderer == null || cam == null || cam == _eye) return;
            _renderer.forceRenderingOff = false;
        }
    }
}
