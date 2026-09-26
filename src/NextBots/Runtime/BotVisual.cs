using NextBots.UI;
using UnityEngine;

namespace NextBots.Runtime
{
    /// <summary>
    /// The billboard. Classic nextbots are a flat image that always faces you, which is both
    /// the look people expect and far cheaper than a rigged model.
    ///
    /// Deliberately collider-free. The catch test is done in the brain by distance, so the
    /// bot never physically shoves a player - which in a game where being shoved moves your
    /// whole body would feel awful and would also desync everyone's position.
    /// </summary>
    public class BotVisual : MonoBehaviour
    {
        private Transform _billboard;
        private Material _material;
        private float _height = 2f;

        private Texture2D[] _frames;
        private float[] _delays;
        private int _frame;
        private float _frameTimer;

        public void Apply(BotSkins.Skin skin)
        {
            if (skin == null) return;

            _height = skin.Height;
            var width = skin.Height * Mathf.Max(0.15f, skin.AspectWidthOverHeight);

            if (_billboard == null)
            {
                Material mat;
                var go = UiResources.CreateQuad(transform, "billboard",
                    new Vector3(0f, skin.Height * 0.5f, 0f), new Vector2(width, skin.Height),
                    Color.white, out mat);
                _billboard = go.transform;

                // Blended by the image's own alpha, so a soft edge or a see-through part looks the
                // way it does in the file instead of solid.
                var mr = go.GetComponent<MeshRenderer>();
                var blended = UiResources.NewSeeThroughMaterial(skin.Texture);
                mr.sharedMaterial = blended;
                Destroy(mat);
                _material = blended;
            }
            else
            {
                _billboard.localPosition = new Vector3(0f, skin.Height * 0.5f, 0f);
                _billboard.localScale = new Vector3(width, skin.Height, 1f);
            }

            _frames = skin.Animated ? skin.Frames : null;
            _delays = skin.Animated ? skin.FrameDelays : null;
            _frame = 0;
            _frameTimer = 0f;

            if (_material != null && skin.Texture != null)
            {
                // URP unlit uses _BaseMap; the legacy path uses _MainTex. Set whichever exists.
                if (_material.HasProperty("_BaseMap")) _material.SetTexture("_BaseMap", skin.Texture);
                if (_material.HasProperty("_MainTex")) _material.SetTexture("_MainTex", skin.Texture);
                _material.mainTexture = skin.Texture;
                UiResources.TrySetColor(_material, Color.white);
            }
        }

        private void LateUpdate()
        {
            AdvanceAnimation();

            if (_billboard == null) return;

            var viewer = UiResources.Viewer;
            if (viewer == null) return;

            // Yaw-only billboard: a full look-at would tip the bot backwards when you look
            // down at it, which reads as the image falling over.
            var to = _billboard.position - viewer.position;
            to.y = 0f;
            if (to.sqrMagnitude < 0.0001f) return;
            _billboard.rotation = Quaternion.LookRotation(to.normalized, Vector3.up);
        }

        /// <summary>
        /// Step the GIF. Delays are per frame rather than a fixed rate, because GIFs
        /// routinely hold one frame far longer than the rest and a fixed rate loses that.
        /// </summary>
        private void AdvanceAnimation()
        {
            if (_frames == null || _frames.Length < 2 || _material == null) return;

            _frameTimer += Time.deltaTime;

            var guard = 0;
            while (_frameTimer >= CurrentDelay() && guard++ < 8)
            {
                _frameTimer -= CurrentDelay();
                _frame = (_frame + 1) % _frames.Length;
                SetTexture(_frames[_frame]);
            }
        }

        private float CurrentDelay()
        {
            if (_delays == null || _frame >= _delays.Length) return 0.1f;
            return Mathf.Max(0.02f, _delays[_frame]);
        }

        private void SetTexture(Texture2D tex)
        {
            if (_material == null || tex == null) return;
            if (_material.HasProperty("_BaseMap")) _material.SetTexture("_BaseMap", tex);
            if (_material.HasProperty("_MainTex")) _material.SetTexture("_MainTex", tex);
            _material.mainTexture = tex;
        }

        public float Height => _height;
    }
}
