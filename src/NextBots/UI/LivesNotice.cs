using GorillaLocomotion;
using NextBots.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace NextBots.UI
{
    /// <summary>
    /// A message across your own view when you lose your last life with ghosts on: you are a
    /// ghost now. It fades in, holds, and fades out, in the headset (hung
    /// in front of your eyes, seen by nothing else) and on the monitor (a screen overlay).
    /// </summary>
    public sealed class LivesNotice : MonoBehaviour
    {
        private const float FadeIn = 0.25f, Hold = 2.2f, FadeOut = 1.6f;
        private const float Distance = 1.3f, Height = 0.09f;

        private static readonly Color Pale = new Color(0.85f, 0.9f, 1f);

        private int _lastLeft = -1;
        private bool _lastActive;
        private float _shownAt = -100f;

        private Transform _vr;
        private TextMeshPro _vrMain, _vrSub;
        private Camera _eye;
        private Canvas _canvas;
        private TextMeshProUGUI _monMain, _monSub;

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

        private void Update()
        {
            int me = LocalActor();
            if (!Lives.Active || me < 0)
            {
                _lastActive = false;
                _lastLeft = -1;
                SetAlpha(0f);
                return;
            }

            int left = Lives.Of(me);
            // Only becoming a ghost is announced; a lost life is shown by the hearts alone.
            if (_lastActive && _lastLeft > 0 && left <= 0 && Lives.GhostsOn) Show();
            _lastActive = true;
            _lastLeft = left;

            float t = Time.time - _shownAt;
            float a = t < FadeIn ? t / FadeIn
                    : t < FadeIn + Hold ? 1f
                    : 1f - (t - FadeIn - Hold) / FadeOut;
            SetAlpha(Mathf.Clamp01(a));
        }

        private void LateUpdate()
        {
            if (_vr == null || !_vr.gameObject.activeSelf) return;
            var p = GTPlayer.Instance;
            var cam = p != null ? p.mainCamera : Camera.main;
            if (cam == null) return;
            _eye = cam;
            if (_vr.parent != cam.transform) _vr.SetParent(cam.transform, false);
            _vr.localPosition = new Vector3(0f, 0.12f, Distance);
            _vr.localRotation = Quaternion.identity;
        }

        private void Show()
        {
            const string main = "YOU'RE A GHOST NOW";
            const string sub = "THE BOTS CAN'T SEE YOU";
            var colour = Pale;

            Build();
            _vrMain.text = main; _vrSub.text = sub;
            _monMain.text = main; _monSub.text = sub;
            _vrMain.color = _monMain.color = colour;
            _shownAt = Time.time;
            Plugin.Log.LogInfo("[Lives] on screen: " + main + (sub.Length > 0 ? " - " + sub : ""));
        }

        private void SetAlpha(float a)
        {
            if (_vr == null) return;
            bool on = a > 0.001f;
            if (_vr.gameObject.activeSelf != on) _vr.gameObject.SetActive(on);
            if (_canvas != null && _canvas.enabled != on) _canvas.enabled = on;
            if (!on) return;
            _vrMain.alpha = _vrSub.alpha = a;
            _monMain.alpha = _monSub.alpha = a;
        }

        // ------------------------------------------------------------------ build

        private void Build()
        {
            if (_vr != null) return;

            var style = KillFeed.Current;
            bool trueBold;
            var font = ModFonts.Headset(style != null ? style.font : "", false, out trueBold);
            if (font == null) font = UiResources.GameTmpFont;
            float line = ModFonts.LineHeightAtSizeOne(font);

            // Headset: in front of your eyes, drawn after nearly everything (the death overlay too).
            _vr = new GameObject("NextBots.LivesNotice").transform;
            DontDestroyOnLoad(_vr.gameObject);
            _vrMain = WorldText(_vr, font, line, Height, 0f);
            _vrSub = WorldText(_vr, font, line, Height * 0.4f, -Height * 0.85f);
            _vrSub.color = Color.white;

            // Monitor: a screen overlay, which the headset never shows.
            var go = new GameObject("NextBots.LivesNotice.Monitor", typeof(RectTransform));
            DontDestroyOnLoad(go);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 30001;
            var scaler = go.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _monMain = ScreenText(go.transform, font, 96f, 120f);
            _monSub = ScreenText(go.transform, font, 38f, 40f);
            _monSub.color = Color.white;
        }

        private static TextMeshPro WorldText(Transform parent, TMP_FontAsset font, float line, float height, float y)
        {
            var go = new GameObject("text");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, y, 0f);
            go.transform.localScale = Vector3.one * (height / Mathf.Max(0.0001f, line));
            var t = go.AddComponent<TextMeshPro>();
            t.font = font;
            t.fontSize = 1f;
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.rectTransform.sizeDelta = new Vector2(40f, 2f);
            t.outlineWidth = 0.2f;
            t.outlineColor = new Color32(0, 0, 0, 200);
            t.fontMaterial.renderQueue = 3960;
            return t;
        }

        private static TextMeshProUGUI ScreenText(Transform canvas, TMP_FontAsset font, float size, float y)
        {
            var rt = (RectTransform)new GameObject("text", typeof(RectTransform)).transform;
            rt.SetParent(canvas, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(1800f, size * 1.6f);
            rt.anchoredPosition = new Vector2(0f, y);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.font = font;
            t.fontSize = size;
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.raycastTarget = false;
            t.outlineWidth = 0.2f;
            t.outlineColor = new Color32(0, 0, 0, 200);
            return t;
        }

        // ------------------------------------------------------------------ eye only

        // Hung in front of your eyes, it would be a sign floating by your head to every other camera -
        // the ragdoll's third-person monitor view and the kill cam - so they skip it. Wrapped: these
        // events give no isolation between subscribers.
        private void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            try { SetVisible(cam == null || cam == _eye); }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[Lives] camera hook: " + ex.Message); }
        }

        private void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
        {
            try { SetVisible(true); }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[Lives] camera hook: " + ex.Message); }
        }

        private void SetVisible(bool on)
        {
            if (_vrMain == null || !_vr.gameObject.activeSelf) return;
            var a = _vrMain.renderer; var b = _vrSub.renderer;
            if (a != null) a.forceRenderingOff = !on;
            if (b != null) b.forceRenderingOff = !on;
        }

        private static int LocalActor()
        {
            var tagger = GorillaTagger.Instance;
            return tagger != null && tagger.offlineVRRig != null ? WorldView.ActorOf(tagger.offlineVRRig) : -1;
        }
    }
}
