using System;
using TMPro;
using UnityEngine;

namespace NextBots.UI
{
    /// <summary>
    /// Shaders and fonts get stripped from a built player, so nothing here may assume a
    /// particular asset resolves. Everything is resolved by trying a priority list against
    /// what the running game actually has loaded, and what got picked is logged once.
    /// </summary>
    public static class UiResources
    {
        private static Shader _unlit;
        private static TMP_FontAsset _tmpFont;
        private static Font _legacyFont;
        private static Mesh _quad;
        private static bool _reported;

        private static readonly string[] UnlitCandidates =
        {
            "Universal Render Pipeline/Unlit",
            "Unlit/Color",
            "Unlit/Texture",
            "Sprites/Default",
            "UI/Default",
            "GUI/Text Shader",
            "Legacy Shaders/Diffuse",
            "Standard"
        };

        public static Shader Unlit
        {
            get
            {
                if (_unlit != null) return _unlit;
                foreach (var name in UnlitCandidates)
                {
                    try
                    {
                        var s = Shader.Find(name);
                        if (s != null) { _unlit = s; break; }
                    }
                    catch { /* keep trying */ }
                }

                // Last resort: borrow a shader off any material already loaded in the game.
                if (_unlit == null)
                {
                    var mats = Resources.FindObjectsOfTypeAll<Material>();
                    for (int i = 0; i < mats.Length; i++)
                        if (mats[i] != null && mats[i].shader != null) { _unlit = mats[i].shader; break; }
                }

                Report();
                return _unlit;
            }
        }

        private static TMP_FontAsset _gameTmpFont;
        private static bool _tmpFontResolved;

        /// <summary>The game's own text font (Liberation Sans). Always there; everything else
        /// falls back to it.</summary>
        public static TMP_FontAsset GameTmpFont
        {
            get
            {
                if (_gameTmpFont != null) return _gameTmpFont;
                try
                {
                    if (TMP_Settings.defaultFontAsset != null) _gameTmpFont = TMP_Settings.defaultFontAsset;
                }
                catch { /* TMP_Settings may not be initialised */ }

                if (_gameTmpFont == null)
                {
                    var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                    if (fonts != null && fonts.Length > 0) _gameTmpFont = fonts[0];
                }
                return _gameTmpFont;
            }
        }

        /// <summary>
        /// The font the mod's own headset text uses - the wrist panel, the kill cam screen. The
        /// one named in the config (<c>UI.Font</c>) when it is installed and can be built,
        /// otherwise the game's own. Chosen once, so changing it needs a restart.
        /// </summary>
        public static TMP_FontAsset TmpFont
        {
            get
            {
                if (_tmpFontResolved && _tmpFont != null) return _tmpFont;
                _tmpFontResolved = true;

                var game = GameTmpFont;
                string wanted = Plugin.CfgFont != null ? Plugin.CfgFont.Value : "";
                if (!string.IsNullOrWhiteSpace(wanted))
                {
                    bool trueBold;
                    var custom = ModFonts.Headset(wanted, false, out trueBold);
                    if (custom != null && custom != game) _tmpFont = custom;
                }
                if (_tmpFont == null) _tmpFont = game;
                Report();
                return _tmpFont;
            }
        }

        public static Font LegacyFont
        {
            get
            {
                if (_legacyFont != null) return _legacyFont;
                try { _legacyFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch { }
                if (_legacyFont == null)
                {
                    try { _legacyFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
                }
                if (_legacyFont == null)
                {
                    var fonts = Resources.FindObjectsOfTypeAll<Font>();
                    if (fonts != null && fonts.Length > 0) _legacyFont = fonts[0];
                }
                Report();
                return _legacyFont;
            }
        }

        private static void Report()
        {
            if (_reported) return;
            _reported = true;
            Plugin.Log.LogInfo("[UI] shader=" + (_unlit != null ? _unlit.name : "<none>") +
                               " tmpFont=" + (_tmpFont != null ? _tmpFont.name : "<none>") +
                               " legacyFont=" + (_legacyFont != null ? _legacyFont.name : "<none>"));
        }

        /// <summary>
        /// Where the local player is looking from. Camera.main only works if a camera is
        /// tagged "MainCamera", which is not something a mod may assume, so fall back to the
        /// rig's head and then to any enabled camera. Billboards and the aim ghost need this
        /// every frame; returning null would make them silently misbehave.
        /// </summary>
        private static Camera _freeCam;
        private static float _freeCamRecheck;

        /// <summary>
        /// QuickFreeCam's detached camera, if that mod is running it right now.
        /// It names its GameObject "QuickFreeCam.Camera" and destroys it when switched off,
        /// so presence of an enabled one is a reliable "the user is looking through the free
        /// cam" signal. Re-checked periodically rather than cached forever.
        /// </summary>
        public static Camera FreeCam
        {
            get
            {
                if (_freeCam != null && _freeCam.isActiveAndEnabled) return _freeCam;
                if (Time.unscaledTime < _freeCamRecheck) return null;
                _freeCamRecheck = Time.unscaledTime + 0.5f;

                _freeCam = null;
                var cams = Camera.allCameras;
                for (int i = 0; i < cams.Length; i++)
                {
                    var c = cams[i];
                    if (c == null || !c.isActiveAndEnabled) continue;
                    if (c.name.StartsWith("QuickFreeCam", StringComparison.OrdinalIgnoreCase))
                    {
                        _freeCam = c;
                        break;
                    }
                }
                return _freeCam;
            }
        }

        /// <summary>True while the free cam is what the player is actually looking through.</summary>
        public static bool UsingFreeCam => FreeCam != null;

        /// <summary>
        /// Where the player is looking from.
        ///
        /// Order matters: the free cam wins, because when it is on that is the only view the
        /// person can actually see - aiming or placing UI relative to the (possibly untracked)
        /// headset would put things off screen. Camera.main only works if something is tagged
        /// "MainCamera", which a mod may not assume, so the rig head and then any camera at
        /// all are the fallbacks.
        /// </summary>
        public static Transform Viewer
        {
            get
            {
                var free = FreeCam;
                if (free != null) return free.transform;

                var cam = Camera.main;
                if (cam != null) return cam.transform;

                try
                {
                    var player = GorillaLocomotion.GTPlayer.Instance;
                    if (player != null && player.headCollider != null) return player.headCollider.transform;
                }
                catch { /* fall through */ }

                try
                {
                    var tagger = GorillaTagger.Instance;
                    if (tagger != null && tagger.offlineVRRig != null && tagger.offlineVRRig.headConstraint != null)
                        return tagger.offlineVRRig.headConstraint;
                }
                catch { /* fall through */ }

                var cams = Camera.allCameras;
                if (cams != null && cams.Length > 0) return cams[0].transform;

                return null;
            }
        }

        private static float _tmpLineHeight = -1f;

        /// <summary>
        /// World height of one line at fontSize 1, scale 1, measured from the font asset the
        /// game actually loaded rather than assumed. Everything sizes off this.
        /// </summary>
        public static float TmpLineHeightAtSizeOne
        {
            get
            {
                if (_tmpLineHeight > 0f) return _tmpLineHeight;

                _tmpLineHeight = 0.1f;   // sane fallback if measurement is impossible
                var font = TmpFont;
                if (font == null) return _tmpLineHeight;

                GameObject probe = null;
                try
                {
                    probe = new GameObject("NextBotsTextProbe") { hideFlags = HideFlags.HideAndDontSave };
                    probe.SetActive(false);
                    var tmp = probe.AddComponent<TextMeshPro>();
                    tmp.font = font;
                    tmp.fontSize = 1f;
                    tmp.textWrappingMode = TextWrappingModes.NoWrap;
                    tmp.text = "Hg";
                    probe.SetActive(true);
                    tmp.ForceMeshUpdate(true, true);

                    var h = tmp.preferredHeight;
                    if (h > 0.0001f && h < 100f) _tmpLineHeight = h;
                    Plugin.Log.LogInfo("[UI] TMP line height at size 1 = " +
                                       _tmpLineHeight.ToString("0.0000") + " (font " + font.name + ")");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[UI] text calibration failed (" + ex.Message +
                                          "); using fallback " + _tmpLineHeight);
                }
                finally
                {
                    if (probe != null) UnityEngine.Object.Destroy(probe);
                }

                return _tmpLineHeight;
            }
        }

        public static Material NewColorMaterial(Color color)
        {
            var mat = new Material(Unlit) { hideFlags = HideFlags.HideAndDontSave };
            TrySetColor(mat, color);
            return mat;
        }

        public static void TrySetColor(Material mat, Color color)
        {
            if (mat == null) return;
            // URP/Lit uses _BaseColor; the legacy pipeline uses _Color. Set whichever exists.
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", color);
            mat.color = color;
        }


        /// <summary>
        /// A unit quad in the XY plane facing -Z, built by hand. Deliberately not
        /// GameObject.CreatePrimitive, which attaches a MeshCollider - and a collider on
        /// anything hand-mounted shoves the player around.
        /// </summary>
        public static Mesh Quad
        {
            get
            {
                if (_quad != null) return _quad;
                _quad = new Mesh { name = "NextBotsQuad", hideFlags = HideFlags.HideAndDontSave };
                _quad.SetVertices(new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f),
                    new Vector3( 0.5f, -0.5f, 0f),
                    new Vector3( 0.5f,  0.5f, 0f),
                    new Vector3(-0.5f,  0.5f, 0f)
                });
                _quad.SetUVs(0, new[]
                {
                    new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
                });
                _quad.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
                _quad.RecalculateNormals();
                _quad.RecalculateBounds();
                return _quad;
            }
        }

        /// <summary>Create a flat coloured quad. No collider, ever.</summary>
        public static GameObject CreateQuad(Transform parent, string name, Vector3 localPos,
                                            Vector2 size, Color color, out Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = Quad;

            var mr = go.AddComponent<MeshRenderer>();
            material = NewColorMaterial(color);
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            return go;
        }
    }

    /// <summary>Text that works whether TMP or only the legacy renderer is available.</summary>
    /// <summary>
    /// A line of text sized in real metres.
    ///
    /// TMP's fontSize is not world units - at fontSize 1 the rendered line is roughly a tenth
    /// of the transform scale, which is how the first version ended up with 1.3 mm text where
    /// 13 mm was asked for. So the ratio is measured once from the actual font asset and used
    /// to derive the scale, which makes <c>worldHeight</c> mean what it says regardless of
    /// which font the game happens to have loaded.
    ///
    /// The text also occupies a real rect of <c>areaWidth</c> centred on its position, so
    /// Left/Right alignment lands on the edges you asked for instead of on the edges of an
    /// arbitrary default rect.
    /// </summary>
    public class PanelText
    {
        private readonly TextMeshPro _tmp;
        private readonly TextMesh _legacy;

        public readonly GameObject GameObject;

        public PanelText(Transform parent, string name, Vector3 localPos, float areaWidth,
                         float worldHeight, Color color, TextAlignmentOptions align)
        {
            GameObject = new GameObject(name);
            GameObject.transform.SetParent(parent, false);
            GameObject.transform.localPosition = localPos;
            GameObject.transform.localRotation = Quaternion.identity;

            var font = UiResources.TmpFont;
            if (font != null)
            {
                _tmp = GameObject.AddComponent<TextMeshPro>();
                _tmp.font = font;
                _tmp.fontSize = 1f;
                _tmp.color = color;
                _tmp.alignment = align;
                _tmp.textWrappingMode = TextWrappingModes.NoWrap;
                _tmp.overflowMode = TextOverflowModes.Overflow;
                _tmp.raycastTarget = false;

                var scale = worldHeight / Mathf.Max(0.0001f, UiResources.TmpLineHeightAtSizeOne);
                GameObject.transform.localScale = Vector3.one * scale;

                // sizeDelta is in local units, so divide the world width back out.
                var rt = _tmp.rectTransform;
                rt.sizeDelta = new Vector2(Mathf.Max(0.001f, areaWidth) / scale, 1.4f);
                return;
            }

            // Legacy fallback, for a build with no TMP font asset loaded at all.
            _legacy = GameObject.AddComponent<TextMesh>();
            _legacy.font = UiResources.LegacyFont;
            _legacy.fontSize = 64;
            _legacy.characterSize = 1f;
            _legacy.color = color;
            _legacy.anchor = align == TextAlignmentOptions.Left ? TextAnchor.MiddleLeft
                           : align == TextAlignmentOptions.Right ? TextAnchor.MiddleRight
                           : TextAnchor.MiddleCenter;

            var mr = GameObject.GetComponent<MeshRenderer>();
            if (mr != null && _legacy.font != null) mr.sharedMaterial = _legacy.font.material;

            // TextMesh at characterSize 1 / fontSize 64 renders ~64 units per line.
            GameObject.transform.localScale = Vector3.one * (worldHeight / 64f);
        }

        public string Text
        {
            get { return _tmp != null ? _tmp.text : (_legacy != null ? _legacy.text : ""); }
            set
            {
                if (_tmp != null) _tmp.text = value;
                else if (_legacy != null) _legacy.text = value;
            }
        }

        public Color Color
        {
            set
            {
                if (_tmp != null) _tmp.color = value;
                else if (_legacy != null) _legacy.color = value;
            }
        }

        public void SetActive(bool active)
        {
            if (GameObject != null) GameObject.SetActive(active);
        }
    }
}
