using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NextBotsRagdoll
{
    /// <summary>
    /// A real full-screen colour effect - true desaturation and a vignette - run over the finished
    /// picture by the game's own URP, using a shader this mod ships.
    ///
    /// <para><b>Why it is not just an overlay.</b> Desaturating means pulling each pixel towards its
    /// <i>own</i> grey. A see-through layer can only pull every pixel towards the <i>same</i> grey,
    /// which reads as the scene getting darker or hazier and never as the colour draining out. A
    /// shader that reads the finished image can do the real thing, and this game's build has none of
    /// URP's post-processing shaders to borrow, so the mod carries its own.</para>
    ///
    /// <para><b>How it is wired in.</b> The shader is compiled into an AssetBundle (built with the game's
    /// own Unity version) and embedded in the mod's DLL. At start-up a
    /// <see cref="FullScreenPassRendererFeature"/> holding a material of that shader is added to every
    /// renderer the URP asset has, switched off, and it is switched on only while there is something
    /// to show. The feature runs after everything else has drawn, so it sees transparents, particles
    /// and the mod's own effects as the player does.</para>
    ///
    /// <para><b>If any of it fails</b> - no bundle, no URP asset, a shader the graphics API cannot
    /// run - <see cref="Available"/> stays false and the caller falls back to something simpler. Nothing
    /// here throws.</para>
    /// </summary>
    public static class ScreenSaturation
    {
        public const string ShaderName = "NextBots/ScreenSaturation";

        private static readonly int Desaturate = Shader.PropertyToID("_Desaturate");
        private static readonly int Dim = Shader.PropertyToID("_Dim");
        private static readonly int VigColor = Shader.PropertyToID("_VigColor");
        private static readonly int VigAmount = Shader.PropertyToID("_VigAmount");
        private static readonly int VigStart = Shader.PropertyToID("_VigStart");

        private static readonly List<FullScreenPassRendererFeature> Features = new List<FullScreenPassRendererFeature>(2);
        private static Material _material;
        private static AssetBundle _bundle;
        private static bool _tried;
        private static bool _on;

        /// <summary>True once the effect is installed and can be used.</summary>
        public static bool Available { get; private set; }

        /// <summary>Why it is not available, for the log.</summary>
        public static string Failure { get; private set; }

        /// <summary>
        /// Installs the effect. Safe to call more than once; only the first call does anything. Call it
        /// after the render pipeline exists (any time after the first scene has loaded).
        /// </summary>
        public static bool Init(byte[] bundle, bool singlePassVr, Action<string> log)
        {
            if (_tried) return Available;
            _tried = true;
            if (log == null) log = _ => { };

            try
            {
                if (bundle == null || bundle.Length < 1024) return Fail("no shader bundle", log);

                var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (asset == null) return Fail("the game is not using a URP asset (" +
                                               (GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.GetType().Name : "none") + ")", log);

                _bundle = AssetBundle.LoadFromMemory(bundle);
                if (_bundle == null) return Fail("the shader bundle would not load (built for a different Unity version?)", log);

                Shader shader = null;
                foreach (var s in _bundle.LoadAllAssets<Shader>())
                    if (s != null && s.name == ShaderName) { shader = s; break; }
                if (shader == null) return Fail("the bundle has no '" + ShaderName + "' shader", log);
                if (!shader.isSupported) return Fail("the shader is not supported on this graphics API (" + SystemInfo.graphicsDeviceType + ")", log);

                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = "NextBots.ScreenSaturation" };

                // Single-pass instanced VR needs its own variant of the shader: see Shaders/README.md.
                if (singlePassVr) _material.EnableKeyword("NB_STEREO");

                // Every renderer the asset has, not just the default one: which a camera uses is the
                // game's business, and it should not matter.
                int added = 0;
                foreach (var data in asset.rendererDataList)
                {
                    if (data == null) continue;

                    var feature = ScriptableObject.CreateInstance<FullScreenPassRendererFeature>();
                    feature.name = "NextBots.ScreenSaturation";
                    feature.passMaterial = _material;
                    feature.passIndex = 0;
                    feature.injectionPoint = FullScreenPassRendererFeature.InjectionPoint.AfterRenderingPostProcessing;
                    feature.requirements = ScriptableRenderPassInput.Color;
                    feature.fetchColorBuffer = true;
                    feature.bindDepthStencilAttachment = false;
                    feature.SetActive(false);

                    data.rendererFeatures.Add(feature);
                    data.SetDirty();          // rebuilds the renderer with the feature in it
                    Features.Add(feature);
                    added++;
                }
                if (added == 0) return Fail("the URP asset has no renderer data", log);

                Available = true;
                log("[Saturation] installed on " + added + " renderer(s) | " + SystemInfo.graphicsDeviceType +
                    (singlePassVr ? " | single-pass VR variant" : ""));
                return true;
            }
            catch (Exception ex)
            {
                return Fail(ex.GetType().Name + ": " + ex.Message, log);
            }
        }

        /// <summary>
        /// Shows the effect. <paramref name="desaturate"/> 0 is normal and 1 is black and white;
        /// <paramref name="dim"/> darkens; the vignette is a colour, how much of it, and where it starts
        /// (0 = the centre, 1 = the corner). All zero is the same as <see cref="Off"/>.
        /// </summary>
        public static void Apply(float desaturate, float dim, Color vignetteColour, float vignetteAmount, float vignetteStart)
        {
            if (!Available || _material == null) return;

            if (desaturate <= 0.002f && dim <= 0.002f && vignetteAmount <= 0.002f) { Off(); return; }

            _material.SetFloat(Desaturate, Mathf.Clamp01(desaturate));
            _material.SetFloat(Dim, Mathf.Clamp01(dim));
            _material.SetColor(VigColor, vignetteColour);
            _material.SetFloat(VigAmount, Mathf.Clamp01(vignetteAmount));
            _material.SetFloat(VigStart, Mathf.Clamp01(vignetteStart));

            if (!_on) SetActive(true);
        }

        /// <summary>Switches it off entirely: no pass is added to the frame, so it costs nothing.</summary>
        public static void Off()
        {
            if (_on) SetActive(false);
        }

        private static void SetActive(bool on)
        {
            _on = on;
            foreach (var f in Features)
                if (f != null) f.SetActive(on);
        }

        private static bool Fail(string why, Action<string> log)
        {
            Available = false;
            Failure = why;
            log("[Saturation] not available: " + why);
            return false;
        }
    }
}
