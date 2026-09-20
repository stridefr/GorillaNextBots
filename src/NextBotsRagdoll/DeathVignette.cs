using GorillaLocomotion;
using NextBots.UI;
using UnityEngine;
using UnityEngine.Rendering;

namespace NextBotsRagdoll
{
    /// <summary>
    /// What being caught does to the view: a red flash round the edges, the colour draining out
    /// of everything, and then - slowly, while you are down and for a moment after you get up -
    /// the world coming back.
    ///
    /// <para><b>A veil, not a post-process.</b> The game renders through URP with its own stack,
    /// and adding a full-screen effect to someone else's renderer is the sort of thing that
    /// breaks on the next game update. This is a quad a hand's width in front of the eye with a
    /// radial gradient on it: clear enough in the middle to play through, dark at the edges. A
    /// neutral grey veil across the whole view is what drains the colour - everything reads
    /// closer to grey the more of it there is - so "coming back" is simply the veil fading.</para>
    ///
    /// <para><b>Comfort.</b> The middle of your view is never blacked out, the red is brief and
    /// does not pulse, and the whole thing is one line to switch off. What it must never do is
    /// hide the floor while you are trying to stand up, which is why the strength is capped well
    /// short of opaque.</para>
    ///
    /// <para><b>Only you see it</b>, like the jumpscare and the death log: any camera that is
    /// not your eye - the ragdoll mod's monitor camera, the kill cam - skips it entirely.</para>
    /// </summary>
    public sealed class DeathVignette : MonoBehaviour
    {
        public static DeathVignette Instance { get; private set; }

        /// <summary>How long the red takes to turn to grey.</summary>
        private const float Drain = 0.45f;

        /// <summary>Nothing stays on the view for ever, whatever happens to the knockdown.</summary>
        private const float MaxHold = 20f;

        private static readonly Color Blood = new Color(0.62f, 0.03f, 0.03f);
        private static readonly Color Ash = new Color(0.46f, 0.47f, 0.52f);

        private Transform _root;
        private MeshRenderer _renderer;
        private Material _material;
        private Texture2D _gradient;
        private Camera _eye;

        private float _started = -1f;
        private float _released = -1f;

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
            if (_gradient != null) Destroy(_gradient);
        }

        // ================================================================== playing

        /// <summary>Caught. Flash, drain, and hold until <see cref="Release"/>.</summary>
        public void Begin()
        {
            if (!BridgeConfig.VignetteEnabled.Value) return;
            if (!Build()) return;

            _started = Time.unscaledTime;
            _released = -1f;
            _root.gameObject.SetActive(true);
        }

        /// <summary>On your feet again: the colour comes back over
        /// <c>RecoverSeconds</c>.</summary>
        public void Release()
        {
            if (_started >= 0f && _released < 0f) _released = Time.unscaledTime;
        }

        private void LateUpdate()
        {
            if (_started < 0f) return;

            float age = Time.unscaledTime - _started;
            float recover = BridgeConfig.VignetteRecoverSeconds.Value;

            // Never held for ever: a knockdown that ends in some way this never hears about
            // still lets go of the view.
            if (_released < 0f && age > MaxHold) _released = Time.unscaledTime;

            float fade = 1f;
            if (_released >= 0f)
            {
                float since = Time.unscaledTime - _released;
                if (since >= recover) { Clear(); return; }
                // Eased, so the last of the colour returns gently rather than snapping back.
                fade = Mathf.SmoothStep(1f, 0f, since / Mathf.Max(0.01f, recover));
            }

            float red = BridgeConfig.VignetteRedSeconds.Value;
            var colour = age < red ? Blood : Color.Lerp(Blood, Ash, Mathf.Clamp01((age - red) / Drain));

            // The red is the hit landing, so it is the strongest the effect ever gets.
            float punch = age < red ? 1f : Mathf.Lerp(1f, 0.78f, Mathf.Clamp01((age - red) / Drain));
            colour.a = BridgeConfig.VignetteStrength.Value * punch * fade;

            Place();
            if (_material != null) UiResources.TrySetColor(_material, colour);
        }

        private void Clear()
        {
            _started = -1f;
            _released = -1f;
            if (_root != null) _root.gameObject.SetActive(false);
        }

        /// <summary>
        /// Across the eye, every frame. Closer than anything else we draw and wide enough that
        /// the edges are off the side of your vision, which is what makes it read as the view
        /// going dark rather than as a picture hanging in front of you.
        /// </summary>
        private void Place()
        {
            var eye = Eye();
            if (eye == null) return;
            var t = eye.transform;
            const float distance = 0.22f;
            _root.position = t.position + t.forward * distance;
            _root.rotation = Quaternion.LookRotation(t.forward, t.up);
            _root.localScale = new Vector3(distance * 4.2f, distance * 4.2f, 1f);
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

            var go = new GameObject("NextBotsRagdoll.DeathVignette");
            go.transform.SetParent(transform, false);
            _root = go.transform;

            go.AddComponent<MeshFilter>().sharedMesh = UiResources.Quad;
            _renderer = go.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            _gradient = BuildGradient();
            _material = SeeThrough(_gradient);
            _renderer.sharedMaterial = _material;
            go.SetActive(false);
            return true;
        }

        /// <summary>
        /// The veil itself: a thin wash over the middle that thickens towards the edges. The
        /// wash is what takes the colour out of what you are looking at; the thick edge is the
        /// vignette. Built once, in code, so there is no image to ship.
        /// </summary>
        private Texture2D BuildGradient()
        {
            const int size = 128;
            float wash = BridgeConfig.VignetteWash.Value;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "NextBotsRagdoll.Vignette",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f;
                    float dy = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy) / 1.4142f;

                    float edge = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.32f, 0.95f, r));
                    float a = Mathf.Clamp01(wash + (1f - wash) * edge);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Alpha-blended, and drawn after the world but under the jumpscare.</summary>
        private static Material SeeThrough(Texture tex)
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
            m.mainTexture = tex;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            m.renderQueue = (int)RenderQueue.Overlay - 60;
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
