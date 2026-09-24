using System;
using System.Collections.Generic;
using System.IO;
using GorillaLocomotion;
using NextBots.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace NextBots.UI
{
    /// <summary>
    /// The death log: "<c>Obunga [face] stridefr</c>", Garry's Mod style, for every catch in
    /// the lobby.
    ///
    /// <para><b>Two outputs, drawn separately.</b> In the headset, each row is real geometry -
    /// a text mesh, the bot's own image on a quad, an optional box - hung off the eye camera
    /// in the corner you pick, so it stays in that corner of your view. On the monitor it is
    /// drawn with IMGUI in the same corner of the screen. Neither is a picture of the other,
    /// so each can be sized for its own screen.</para>
    ///
    /// <para><b>Only the headset sees the headset copy.</b> Anything hung in front of your
    /// eyes is, to every other camera, a small sign floating next to your head: the ragdoll
    /// mod's third-person monitor view and the kill cam would both film it. So every renderer
    /// here is switched off for any camera that is not the eye camera, for the duration of
    /// that camera's render - the same trick the kill cam uses to keep its own model out of
    /// shot.</para>
    ///
    /// <para><b>The look is data.</b> Everything comes from <c>killfeed.json</c>, which is
    /// re-read within a second of changing, so the designer page can restyle it live while
    /// you watch in the headset.</para>
    /// </summary>
    public sealed class KillFeed : MonoBehaviour
    {
        private const float ArriveTime = 0.25f;

        /// <summary>
        /// Where the headset rows sit in the draw order: after nearly everything, and in particular
        /// after the ragdoll bridge's death overlay (Overlay-70 and -69). That overlay is a see-through
        /// layer hung a hand's width in front of your eyes, darkest at the edges and corners - which is
        /// exactly where this sits - so drawn in the usual transparent range the log was dimmed out
        /// of view at precisely the moment you were being told who got you. The overlay writes no
        /// depth, so drawing after it is enough to be on top of it.
        /// </summary>
        private const int QueueBorder = 3940, QueueBox = 3941, QueueText = 3942, QueueIcon = 3943;

        private KillFeedStyle _style = new KillFeedStyle();

        /// <summary>The style in use, for anything else drawing in a screen corner to keep out
        /// of its way (the kill cam's monitor picture does).</summary>
        public static KillFeedStyle Current { get; private set; }
        private DateTime _styleStamp;
        private float _nextStyleCheck;

        private sealed class Entry
        {
            public string Killer, Verb, Victim;
            public Texture2D Icon;
            public float IconAspect = 1f;
            public Texture2D[] Frames;
            public float[] FrameDelays;
            public Material IconMat;
            public int Frame;
            public bool Mine;
            public float Born;

            /// <summary>When it was pushed past the row limit and started fading; -1 while it is still
            /// one of the rows on show.</summary>
            public float LeavingSince = -1f;
            public bool Leaving => LeavingSince >= 0f;

            // headset
            public Transform Row;
            public float Width, Height, Y;
            public bool Placed;

            /// <summary>Where the monitor copy of this row is drawn, in pixels down from the
            /// corner. Smoothed towards where it belongs, exactly as the headset rows are, so a
            /// new catch pushes the list along instead of teleporting it.</summary>
            public float MonY;
            public bool MonPlaced;
            public readonly List<TextMeshPro> Texts = new List<TextMeshPro>(8);
            public readonly List<Color> TextColors = new List<Color>(8);
            public readonly List<Material> Mats = new List<Material>(4);
            public readonly List<Color> MatColors = new List<Color>(4);
            public float AppliedAlpha = -1f;
        }

        /// <summary>Newest first.</summary>
        private readonly List<Entry> _entries = new List<Entry>(8);

        private Transform _vrRoot;
        private Camera _eye;

        // The style's font, resolved: headset font asset, its measured line height, whether bold
        // has to be faked, and the monitor's font (null = IMGUI's own).
        private TMP_FontAsset _font;
        private float _fontLine = 0.1f;

        /// <summary>The font's line height over its size (its em): how much taller a line of text is
        /// than the font size says.</summary>
        private float _lineOverEm = 1.2f;
        private FontStyles _fontStyle = FontStyles.Normal;
        private Font _guiFont;
        private readonly List<Renderer> _vrRenderers = new List<Renderer>(32);

        // ================================================================== lifecycle

        private void OnEnable()
        {
            CatchEffects.Caught += OnCaught;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
            LoadStyle(force: true);
        }

        private void OnDisable()
        {
            CatchEffects.Caught -= OnCaught;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            RenderPipelineManager.endCameraRendering -= OnEndCamera;
            ClearAll();
            if (_vrRoot != null) Destroy(_vrRoot.gameObject);
            _vrRoot = null;
        }

        // ================================================================== entries

        private void OnCaught(CatchInfo info)
        {
            if (!_style.enabled || info == null) return;

            var skin = BotSkins.Find(info.BotSkin);
            var e = new Entry
            {
                Killer = Case(string.IsNullOrEmpty(info.BotName) ? "?" : info.BotName),
                Verb = Case(_style.verb ?? ""),
                Victim = Case(string.IsNullOrEmpty(info.VictimName) ? "?" : info.VictimName),
                Icon = skin != null ? skin.Texture : null,
                IconAspect = skin != null ? Mathf.Clamp(skin.AspectWidthOverHeight, 0.3f, 3f) : 1f,
                Frames = skin != null && skin.Animated ? skin.Frames : null,
                FrameDelays = skin != null ? skin.FrameDelays : null,
                Mine = info.VictimIsLocal,
                Born = Time.time
            };

            _entries.Insert(0, e);

            // Past the limit, the oldest rows fade out as they are pushed down, rather than
            // vanishing the instant a new one arrives. They still take their place in the stack
            // while they go, so the log slides rather than jumps.
            int shown = 0;
            foreach (var x in _entries)
            {
                if (x.Leaving) continue;
                if (++shown > _style.maxEntries) x.LeavingSince = Time.time;
            }

            // A burst of catches can pile up fading rows; past a point, drop the oldest outright.
            while (_entries.Count > _style.maxEntries * 2)
            {
                DestroyRow(_entries[_entries.Count - 1]);
                _entries.RemoveAt(_entries.Count - 1);
            }

            if (_style.showInVr) BuildRow(e);
        }

        private string Case(string s) => _style.upperCase ? s.ToUpperInvariant() : s;

        private void Update()
        {
            LoadStyle(force: false);

            float now = Time.time;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                bool expired = now - e.Born >= _style.lifetime;
                bool faded = e.Leaving && now - e.LeavingSince >= LeaveTime;
                if (!expired && !faded) continue;
                DestroyRow(e);
                _entries.RemoveAt(i);
            }
        }

        private void LateUpdate()
        {
            if (_entries.Count == 0 || !_style.showInVr) return;
            if (!EnsureVrRoot()) return;

            float th = _style.vrTextHeight;
            float gap = _style.spacing * th;
            float target = 0f;
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.Row == null) BuildRow(e);
                if (e.Row == null) continue;

                // Older rows glide out of the way of a new one rather than jumping.
                e.Y = e.Placed ? Mathf.Lerp(e.Y, target, k) : target;
                e.Placed = true;
                target += e.Height + gap;

                float age = Time.time - e.Born;
                float arrive = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(age / ArriveTime));
                float alpha = Fade(age) * Leave(e);
                float slide = 0f, scale = 1f;

                switch ((_style.animation ?? "").ToLowerInvariant())
                {
                    case "slide": slide = (1f - arrive) * (e.Width + 0.04f); break;
                    case "pop": scale = Mathf.Lerp(1.35f, 1f, arrive); alpha *= arrive; break;
                    case "fade": alpha *= arrive; break;
                }

                // Rows hang from the corner: down from a top corner, up from a bottom one, and
                // slide in from off the corner's own edge.
                float y = _style.Top ? -e.Y : e.Y;
                float x = _style.Right ? slide : -slide;
                e.Row.localPosition = new Vector3(x, y, 0f);
                e.Row.localScale = Vector3.one * scale;
                ApplyAlpha(e, alpha);
                AnimateIcon(e);
            }
        }

        /// <summary>A GIF bot moves in the log as it does in the designer, not stuck on its first frame.</summary>
        private static void AnimateIcon(Entry e)
        {
            if (e.Frames == null || e.IconMat == null) return;
            float total = 0f;
            for (int i = 0; i < e.Frames.Length; i++) total += FrameDelay(e, i);
            if (total <= 0f) return;
            float t = (Time.time - e.Born) % total;
            int f = 0;
            while (f < e.Frames.Length - 1 && t >= FrameDelay(e, f)) { t -= FrameDelay(e, f); f++; }
            if (f == e.Frame || e.Frames[f] == null) return;
            e.Frame = f;
            e.IconMat.mainTexture = e.Frames[f];
            if (e.IconMat.HasProperty("_BaseMap")) e.IconMat.SetTexture("_BaseMap", e.Frames[f]);
        }

        private static float FrameDelay(Entry e, int i) =>
            e.FrameDelays != null && i < e.FrameDelays.Length && e.FrameDelays[i] > 0.01f ? e.FrameDelays[i] : 0.1f;

        /// <summary>How long a row pushed past the limit takes to fade: the style's fade-out,
        /// but never so short it reads as a pop.</summary>
        private float LeaveTime => Mathf.Max(0.2f, _style.fadeTime);

        private float Leave(Entry e) =>
            e.Leaving ? Mathf.Clamp01(1f - (Time.time - e.LeavingSince) / LeaveTime) : 1f;

        private float Fade(float age)
        {
            float left = _style.lifetime - age;
            return _style.fadeTime > 0.001f && left < _style.fadeTime ? Mathf.Clamp01(left / _style.fadeTime) : 1f;
        }

        // ================================================================== headset rows

        private bool EnsureVrRoot()
        {
            var p = GTPlayer.Instance;
            var cam = p != null ? p.mainCamera : Camera.main;
            if (cam == null) return false;

            if (_vrRoot == null)
            {
                var go = new GameObject("NextBots.KillFeed");
                DontDestroyOnLoad(go);
                _vrRoot = go.transform;
            }

            // A child of the eye camera, so it is exactly in the corner of your view every
            // frame with no lag to chase - and scales with you if the game resizes the player.
            if (_vrRoot.parent != cam.transform) _vrRoot.SetParent(cam.transform, false);
            _eye = cam;

            _vrRoot.localPosition = new Vector3(
                (_style.Right ? 1f : -1f) * _style.vrOffsetX,
                (_style.Top ? 1f : -1f) * _style.vrOffsetY,
                _style.vrDistance);
            _vrRoot.localRotation = Quaternion.identity;
            return true;
        }

        /// <summary>
        /// One row: <c>[killer] [icon] [verb] [victim]</c>, in an optional box. Laid out left to
        /// right from zero, then shifted so a right-corner row ends at the corner and a
        /// left-corner row starts there. Everything is centred vertically on the row.
        /// </summary>
        private void BuildRow(Entry e)
        {
            if (!EnsureVrRoot() || e.Row != null) return;

            float th = _style.vrTextHeight;
            float pad = _style.padding * th;
            float gap = th * 0.35f;
            bool showIcon = _style.ShowIcon && e.Icon != null;
            float iconH = showIcon ? th * _style.iconSize : 0f;
            float iconW = iconH * e.IconAspect;

            var row = new GameObject("row").transform;
            row.SetParent(_vrRoot, false);
            e.Row = row;

            var content = new GameObject("content").transform;
            content.SetParent(row, false);

            float x = pad;
            x = AddText(e, content, e.Killer, _style.KillerColor, th, x) + gap;
            if (showIcon)
            {
                var icon = Quad(content, "icon", new Vector3(x + iconW * 0.5f, 0f, 0f), new Vector2(iconW, iconH),
                                Color.white, e.Icon, QueueIcon);
                Track(e, icon, Color.white);
                e.IconMat = icon.sharedMaterial;
                e.Frame = 0;
                x += iconW + gap;
            }
            if (!string.IsNullOrEmpty(e.Verb))
                x = AddText(e, content, e.Verb, Color.white, th, x) + gap;
            x = AddText(e, content, e.Victim, _style.VictimColor, th, x) + pad;

            e.Width = x;
            e.Height = Mathf.Max(th * _lineOverEm, iconH) + 2f * pad;

            // Right corners: the row ends at the corner. Top corners: it hangs below it.
            content.localPosition = new Vector3(_style.Right ? -e.Width : 0f,
                                                (_style.Top ? -1f : 1f) * e.Height * 0.5f, 0f);

            Vector3 centre = new Vector3(e.Width * 0.5f, 0f, 0f);
            // Picked hex codes go in as they are: Unity already converts material colours for the
            // linear colour space, and TextMeshPro its vertex colours, so converting here as well
            // darkened every colour past what was picked.
            Color frame = e.Mine && _style.LocalHighlight.a > 0.001f ? _style.LocalHighlight : _style.Border;
            if (frame.a > 0.001f)
            {
                float b = th * 0.12f;
                Track(e, Quad(content, "border", centre + new Vector3(0f, 0f, 0.003f),
                              new Vector2(e.Width + 2f * b, e.Height + 2f * b), frame, null, QueueBorder), frame);
            }
            var bg = _style.Background;
            if (bg.a > 0.001f)
                Track(e, Quad(content, "box", centre + new Vector3(0f, 0f, 0.002f),
                              new Vector2(e.Width, e.Height), bg, null, QueueBox), bg);

            _vrRenderers.RemoveAll(r => r == null);
            _vrRenderers.AddRange(row.GetComponentsInChildren<Renderer>(true));
            e.AppliedAlpha = -1f;
        }

        /// <summary>Adds a line of text starting at <paramref name="x"/>; returns where it ends.</summary>
        private float AddText(Entry e, Transform parent, string text, Color color, float height, float x)
        {
            var font = _font != null ? _font : UiResources.GameTmpFont;
            if (font == null || string.IsNullOrEmpty(text)) return x;

            // The text height setting is the font size, as in the designer (CSS font-size), not the
            // full line: sizing the line to it made every font smaller than the preview by its own
            // line spacing - a quarter or so for most fonts.
            float line = _font != null ? _fontLine : UiResources.TmpLineHeightAtSizeOne;
            float scale = height * _lineOverEm / Mathf.Max(0.0001f, line);

            if (_style.textShadow)
            {
                var shadow = MakeText(parent, text, new Color(0f, 0f, 0f, 0.85f), scale, font, _fontStyle);
                shadow.transform.localPosition = new Vector3(x + height * 0.07f, -height * 0.07f, 0.001f);
                Track(e, shadow, shadow.color);
            }

            var tmp = MakeText(parent, text, color, scale, font, _fontStyle);
            tmp.transform.localPosition = new Vector3(x, 0f, 0f);
            Track(e, tmp, color);

            return x + tmp.GetPreferredValues(text).x * scale;
        }

        private static TextMeshPro MakeText(Transform parent, string text, Color color, float scale,
                                            TMP_FontAsset font, FontStyles style)
        {
            var go = new GameObject("text");
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * scale;

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.font = font;
            tmp.fontSize = 1f;
            tmp.richText = false;   // player names are plain text, never markup
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.fontStyle = style;
            tmp.raycastTarget = false;
            tmp.text = text;
            tmp.color = color;

            // Its own material instance, so moving it up the draw order does not move every other
            // piece of text in the game that shares this font.
            tmp.fontMaterial.renderQueue = QueueText;

            var size = tmp.GetPreferredValues(text);
            var rt = tmp.rectTransform;
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(size.x, Mathf.Max(size.y, 1f));
            return tmp;
        }

        private Renderer Quad(Transform parent, string name, Vector3 pos, Vector2 size, Color color,
                              Texture tex, int queue)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = UiResources.Quad;

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = SeeThrough(color, tex, queue);
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return mr;
        }

        private static void Track(Entry e, TextMeshPro t, Color c)
        {
            e.Texts.Add(t);
            e.TextColors.Add(c);
        }

        private static void Track(Entry e, Renderer r, Color c)
        {
            e.Mats.Add(r.sharedMaterial);
            e.MatColors.Add(c);
        }

        private static void ApplyAlpha(Entry e, float alpha)
        {
            if (Mathf.Abs(alpha - e.AppliedAlpha) < 0.004f) return;
            e.AppliedAlpha = alpha;

            for (int i = 0; i < e.Texts.Count; i++)
            {
                if (e.Texts[i] == null) continue;
                var c = e.TextColors[i];
                c.a *= alpha;
                e.Texts[i].color = c;
            }
            for (int i = 0; i < e.Mats.Count; i++)
            {
                if (e.Mats[i] == null) continue;
                var c = e.MatColors[i];
                c.a *= alpha;
                UiResources.TrySetColor(e.Mats[i], c);
            }
        }

        private void DestroyRow(Entry e)
        {
            foreach (var m in e.Mats) if (m != null) Destroy(m);
            e.Mats.Clear();
            e.MatColors.Clear();
            e.Texts.Clear();
            e.TextColors.Clear();
            if (e.Row != null) Destroy(e.Row.gameObject);
            e.Row = null;
            e.Placed = false;
        }

        private void ClearAll()
        {
            foreach (var e in _entries) DestroyRow(e);
            _entries.Clear();
        }

        private void RebuildRows()
        {
            foreach (var e in _entries) DestroyRow(e);
            _vrRenderers.Clear();
            // LateUpdate rebuilds each missing row with the new style.
        }

        // ------------------------------------------------------------------ see-through material

        private static Shader _seeThrough;
        private static bool _shaderReported;

        /// <summary>
        /// A material that honours alpha, for boxes that are meant to be translucent and rows
        /// that fade out. The panel's usual unlit shader is opaque, so it would draw a "50%
        /// black" box as solid black. The built-in sprite shader blends and runs under URP
        /// and in single-pass VR; URP's own unlit is set up for transparency as the fallback.
        /// </summary>
        private static Material SeeThrough(Color color, Texture tex, int queue)
        {
            if (_seeThrough == null)
            {
                foreach (var name in new[] { "Sprites/Default", "UI/Default", "Universal Render Pipeline/Unlit" })
                {
                    try { _seeThrough = Shader.Find(name); } catch { }
                    if (_seeThrough != null) break;
                }
                if (_seeThrough == null) _seeThrough = UiResources.Unlit;
                if (!_shaderReported)
                {
                    _shaderReported = true;
                    Plugin.Log.LogInfo("[KillFeed] box shader: " + (_seeThrough != null ? _seeThrough.name : "<none>"));
                }
            }

            var m = new Material(_seeThrough) { hideFlags = HideFlags.HideAndDontSave };
            if (_seeThrough != null && _seeThrough.name == "Universal Render Pipeline/Unlit")
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            if (tex != null)
            {
                m.mainTexture = tex;
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            }
            UiResources.TrySetColor(m, color);
            // Boxes before text, text before nothing: explicit, because sorting translucent
            // things by distance breaks down when they are millimetres apart.
            m.renderQueue = queue;
            return m;
        }

        // ------------------------------------------------------------------ eye camera only

        // Wrapped like every camera hook in the mod suite now is: RenderPipelineManager's camera
        // events give no isolation between subscribers, so one throwing silently stops every later
        // one sharing it - see GorillaRagdoll's DeathVignette.cs for the case that found it.
        private void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            try
            {
                if (cam == null || cam == _eye || _vrRenderers.Count == 0) return;
                for (int i = 0; i < _vrRenderers.Count; i++)
                    if (_vrRenderers[i] != null) _vrRenderers[i].forceRenderingOff = true;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[KillFeed] camera hook: " + ex.Message); }
        }

        private void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
        {
            try
            {
                if (cam == null || cam == _eye || _vrRenderers.Count == 0) return;
                for (int i = 0; i < _vrRenderers.Count; i++)
                    if (_vrRenderers[i] != null) _vrRenderers[i].forceRenderingOff = false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[KillFeed] camera hook: " + ex.Message); }
        }

        // ================================================================== monitor

        private GUIStyle _label;
        private int _labelSize = -1;
        private int _monitorFrame = -1;

        private void OnGUI()
        {
            if (!_style.showOnMonitor || _entries.Count == 0 || Event.current.type != EventType.Repaint) return;

            if (_label == null || _labelSize != _style.monitorTextSize)
            {
                _label = new GUIStyle(GUI.skin.label)
                {
                    fontSize = _style.monitorTextSize,
                    alignment = TextAnchor.MiddleLeft,
                    richText = false,
                    wordWrap = false,
                    clipping = TextClipping.Overflow,
                    padding = new RectOffset(0, 0, 0, 0),
                    margin = new RectOffset(0, 0, 0, 0),
                    font = _guiFont,
                    fontStyle = _style.bold ? FontStyle.Bold : FontStyle.Normal
                };
                _labelSize = _style.monitorTextSize;
            }

            float th = _label.CalcSize(new GUIContent("Hg")).y;
            float pad = _style.padding * th;
            float gap = th * 0.3f;
            float spacing = _style.spacing * th;
            float margin = _style.monitorMargin;
            float marginY = _style.MarginY;
            float offset = 0f;
            var saved = GUI.color;

            // IMGUI can repaint more than once in a frame, and moving the rows on every repaint
            // would run the slide at a speed that depends on how many repaints there were. So the
            // positions advance once per frame, and any further repaint draws them where they are.
            bool step = Time.frameCount != _monitorFrame;
            _monitorFrame = Time.frameCount;
            float k = 1f - Mathf.Exp(-12f * Time.deltaTime);

            foreach (var e in _entries)
            {
                float age = Time.time - e.Born;
                float arrive = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(age / ArriveTime));
                float alpha = Fade(age) * Leave(e);
                string anim = (_style.animation ?? "").ToLowerInvariant();
                if (anim == "fade" || anim == "pop") alpha *= arrive;

                bool showIcon = _style.ShowIcon && e.Icon != null;
                float iconH = showIcon ? th * _style.iconSize : 0f;
                float iconW = iconH * e.IconAspect;
                float killerW = _label.CalcSize(new GUIContent(e.Killer)).x;
                float verbW = string.IsNullOrEmpty(e.Verb) ? 0f : _label.CalcSize(new GUIContent(e.Verb)).x;
                float victimW = _label.CalcSize(new GUIContent(e.Victim)).x;

                float w = pad + killerW + gap + (showIcon ? iconW + gap : 0f) + (verbW > 0f ? verbW + gap : 0f) + victimW + pad;
                float h = Mathf.Max(th, iconH) + 2f * pad;
                float slide = anim == "slide" ? (1f - arrive) * (w + margin) : 0f;

                // Older rows glide to their new place; a row being drawn for the first time
                // starts where it belongs rather than sliding in from the top of the list.
                if (!e.MonPlaced) { e.MonY = offset; e.MonPlaced = true; }
                else if (step) e.MonY = Mathf.Lerp(e.MonY, offset, k);

                float x = _style.Right ? Screen.width - margin - w + slide : margin - slide;
                float y = _style.Top ? marginY + e.MonY : Screen.height - marginY - e.MonY - h;
                offset += h + spacing;

                Color frame = e.Mine && _style.LocalHighlight.a > 0.001f ? _style.LocalHighlight : _style.Border;
                if (frame.a > 0.001f) Fill(new Rect(x - 2f, y - 2f, w + 4f, h + 4f), frame, alpha);
                if (_style.Background.a > 0.001f) Fill(new Rect(x, y, w, h), _style.Background, alpha);

                float cx = x + pad, cy = y + h * 0.5f;
                cx = Text(e.Killer, cx, cy, killerW, th, _style.KillerColor, alpha) + gap;
                if (showIcon)
                {
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    GUI.DrawTexture(new Rect(cx, cy - iconH * 0.5f, iconW, iconH), e.Icon, ScaleMode.ScaleToFit);
                    cx += iconW + gap;
                }
                if (verbW > 0f) cx = Text(e.Verb, cx, cy, verbW, th, Color.white, alpha) + gap;
                Text(e.Victim, cx, cy, victimW, th, _style.VictimColor, alpha);
            }

            GUI.color = saved;
        }

        private float Text(string s, float x, float cy, float w, float th, Color color, float alpha)
        {
            var r = new Rect(x, cy - th * 0.5f, w + 2f, th);
            if (_style.textShadow)
            {
                GUI.color = new Color(1f, 1f, 1f, alpha * 0.85f);
                _label.normal.textColor = Color.black;
                GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), s, _label);
            }
            GUI.color = new Color(1f, 1f, 1f, alpha);
            _label.normal.textColor = color;
            GUI.Label(r, s, _label);
            return x + w;
        }

        private static void Fill(Rect r, Color c, float alpha)
        {
            GUI.color = new Color(c.r, c.g, c.b, c.a * alpha);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
        }

        // ================================================================== style file

        private void ResolveFonts()
        {
            bool trueBold;
            _font = ModFonts.Headset(_style.font, _style.bold, out trueBold);
            _fontLine = ModFonts.LineHeightAtSizeOne(_font);
            _lineOverEm = ModFonts.LineOverEm(_font != null ? _font : UiResources.GameTmpFont);
            // Only fake bold when there was no real bold face to use.
            _fontStyle = _style.bold && !trueBold ? FontStyles.Bold : FontStyles.Normal;
            _guiFont = ModFonts.Monitor(string.IsNullOrEmpty(_style.monitorFont) ? _style.font : _style.monitorFont);
        }

        /// <summary>
        /// Re-reads <c>killfeed.json</c> when it changes, checked once a second - cheap, and
        /// quick enough that saving in the designer shows up in the headset almost at once.
        /// Writes the defaults out the first time, so there is a file to find and edit.
        /// </summary>
        private void LoadStyle(bool force)
        {
            if (!force && Time.unscaledTime < _nextStyleCheck) return;
            _nextStyleCheck = Time.unscaledTime + 1f;

            string path = KillFeedStyle.FilePath;
            try
            {
                if (!File.Exists(path))
                {
                    if (!force) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, JsonUtility.ToJson(new KillFeedStyle(), true));
                    Plugin.Log.LogInfo("[KillFeed] wrote default style to " + path);
                }

                var stamp = File.GetLastWriteTimeUtc(path);
                if (!force && stamp == _styleStamp) return;
                _styleStamp = stamp;

                var style = new KillFeedStyle();
                JsonUtility.FromJsonOverwrite(File.ReadAllText(path), style);
                style.Sanitise();
                _style = style;
                Current = style;
                ResolveFonts();
                _label = null;
                RebuildRows();
                Plugin.Log.LogInfo("[KillFeed] style '" + style.preset + "' | " + style.Corner +
                                   (style.showInVr ? " | headset" : "") + (style.showOnMonitor ? " | monitor" : ""));
            }
            catch (Exception ex)
            {
                // Keep the last good style: a half-saved or mistyped file must not blank the log.
                Plugin.Log.LogWarning("[KillFeed] could not read " + path + ": " + ex.Message);
            }
        }
    }
}
