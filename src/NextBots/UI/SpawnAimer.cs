using NextBots.Runtime;
using UnityEngine;
using UnityEngine.AI;

namespace NextBots.UI
{
    /// <summary>
    /// Point-and-place, Garry's-Mod style: raycast from the free hand, show a ghost of the bot
    /// where it would land, and report that point when SPAWN is pressed.
    ///
    /// <para><b>The ghost is the bot.</b> A see-through copy of the selected bot's own picture, at
    /// its real size, standing on the spot it would be placed on and turned to face you the way the
    /// real one does, over a thin ring on the floor. Green-tinted where it can go, and where it
    /// cannot - a wall, a ceiling, anywhere off the walkable surface - only a small red ring on
    /// whatever you are pointing at, because a full-size shape standing in a wall says nothing
    /// useful. (It used to be an opaque green square laid on the hit surface plus a green bar,
    /// which pointed at a nearby wall made a large flat L-shape in your face.)</para>
    ///
    /// The ray direction is taken as (fingertip - wrist) rather than any transform's
    /// forward axis, so it does not depend on GT's hand-rig orientation convention being
    /// what we assumed.
    /// </summary>
    public class SpawnAimer : MonoBehaviour
    {
        public bool UseRightHand = true;
        public float MaxRange = 25f;

        /// <summary>Snap the placement onto the navigable surface, within this radius.</summary>
        public float NavSnapRadius = 2.0f;

        private static readonly Color ColValid = new Color(0.35f, 0.95f, 0.55f, 1f);
        private static readonly Color ColInvalid = new Color(0.95f, 0.35f, 0.30f, 1f);

        /// <summary>How solid the ghost is: enough to read the bot, clearly not a real one.</summary>
        private const float GhostAlpha = 0.45f;

        /// <summary>The bot SPAWN would place, set by the menu.</summary>
        public int SkinIndex;

        private GameObject _ghost;
        private Transform _ring;
        private Transform _body;
        private Material _ringMat;
        private Material _bodyMat;
        private Texture2D _ringTex;
        private LineRenderer _beam;

        // What the body is currently showing, so it is only rebuilt when the selection changes.
        private BotSkins.Skin _shownSkin;
        private int _frame;
        private float _frameTimer;

        private bool _hasPlacement;
        private Vector3 _placement;
        private Quaternion _placementRot;
        private bool _navOk;

        private void Awake() => BuildGhost();

        public void SetActive(bool active)
        {
            if (_ghost != null && _ghost.activeSelf != active) _ghost.SetActive(active);
            if (_beam != null) _beam.enabled = active;
            if (!active) _hasPlacement = false;
        }

        /// <summary>Where a bot would land right now, already snapped to the NavMesh.</summary>
        public bool TryGetPlacement(out Vector3 position, out Quaternion rotation)
        {
            position = _placement;
            rotation = _placementRot;
            return _hasPlacement && _navOk;
        }

        private void Update()
        {
            if (_ghost == null || !_ghost.activeSelf) return;

            Vector3 origin, direction;
            if (!TryGetRay(out origin, out direction))
            {
                _hasPlacement = false;
                ShowGhost(false);
                return;
            }

            RaycastHit hit;
            if (!Physics.Raycast(origin, direction, out hit, MaxRange,
                                 Runtime.WorldView.SightMask, QueryTriggerInteraction.Ignore))
            {
                _hasPlacement = false;
                ShowGhost(false);
                DrawBeam(origin, origin + direction * MaxRange, ColInvalid);
                return;
            }

            _placement = hit.point;
            _placementRot = Quaternion.LookRotation(
                Vector3.ProjectOnPlane(direction, Vector3.up).sqrMagnitude > 0.0001f
                    ? Vector3.ProjectOnPlane(direction, Vector3.up).normalized
                    : Vector3.forward,
                Vector3.up);
            _hasPlacement = true;

            // A placement off the navigable surface would drop a bot that cannot move, so
            // snap to it and colour the ghost by whether that succeeded.
            NavMeshHit navHit;
            _navOk = NavMesh.SamplePosition(hit.point, out navHit, NavSnapRadius, NavMesh.AllAreas);
            if (_navOk) _placement = navHit.position;

            var color = _navOk ? ColValid : ColInvalid;
            if (_navOk) PoseGhost(_placement);
            else PoseInvalid(hit.point, hit.normal);
            DrawBeam(origin, hit.point, color);
        }

        /// <summary>
        /// Origin at the fingertip, direction along the hand. Convention-free: it uses the
        /// two transforms GT already maintains rather than assuming an axis.
        /// </summary>
        private bool TryGetRay(out Vector3 origin, out Vector3 direction)
        {
            origin = Vector3.zero;
            direction = Vector3.forward;

            var tagger = GorillaTagger.Instance;
            var rig = tagger != null ? tagger.offlineVRRig : null;
            if (rig == null) return false;

            var wrist = UseRightHand ? rig.rightHandTransform : rig.leftHandTransform;
            var tipGo = UseRightHand ? tagger.rightHandTriggerCollider : tagger.leftHandTriggerCollider;
            if (wrist == null || tipGo == null) return false;

            var tip = tipGo.transform.position;
            var delta = tip - wrist.position;

            // With no headset the hand transforms are untracked and collapse together, which
            // would give a zero-length (or wildly wrong) aim vector. Fall back to looking
            // where the camera looks so the mod is still driveable flat on the desktop.
            if (delta.sqrMagnitude < 1e-4f) return TryGetViewerRay(out origin, out direction);

            origin = tip;
            direction = delta.normalized;
            return true;
        }

        /// <summary>Aim down the view direction - the desktop/no-VR path.</summary>
        public static bool TryGetViewerRay(out Vector3 origin, out Vector3 direction)
        {
            origin = Vector3.zero;
            direction = Vector3.forward;

            var viewer = UiResources.Viewer;
            if (viewer == null) return false;

            origin = viewer.position + viewer.forward * 0.15f;
            direction = viewer.forward;
            return true;
        }

        // ---------------------------------------------------------------- ghost

        private void BuildGhost()
        {
            _ghost = new GameObject("NextBotsGhost");
            _ghost.transform.SetParent(transform, false);

            // A thin ring on the floor: where the feet land, without covering what is there.
            _ringTex = BuildRingTexture(128);
            _ringMat = SeeThrough(_ringTex);
            _ring = Quad("ring", _ringMat);

            // The bot itself, see-through. Its picture and size are filled in from the selection.
            _bodyMat = SeeThrough(null);
            _body = Quad("body", _bodyMat);

            _beam = _ghost.AddComponent<LineRenderer>();
            _beam.useWorldSpace = true;
            _beam.positionCount = 2;
            _beam.startWidth = 0.008f;
            _beam.endWidth = 0.004f;
            _beam.material = UiResources.NewColorMaterial(ColValid);
            _beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _beam.receiveShadows = false;

            _ghost.SetActive(false);
        }

        private Transform Quad(string name, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_ghost.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = UiResources.Quad;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return go.transform;
        }

        private void ShowGhost(bool visible)
        {
            if (_ring != null) _ring.gameObject.SetActive(visible);
            if (_body != null) _body.gameObject.SetActive(visible);
        }

        /// <summary>A valid spot: the bot standing on it, facing you, over its ring.</summary>
        private void PoseGhost(Vector3 point)
        {
            var skin = SelectedSkin();
            if (skin != _shownSkin) ApplySkin(skin);
            Animate();

            float height = skin != null ? skin.Height : 2f;
            float width = height * Mathf.Max(0.15f, skin != null ? skin.AspectWidthOverHeight : 0.5f);

            _body.gameObject.SetActive(true);
            _body.position = point + Vector3.up * (height * 0.5f);
            _body.localScale = new Vector3(width, height, 1f);

            // Yaw-only, turned the same way the real bot's billboard is, so the ghost shows the
            // picture exactly as the bot will look standing there.
            var viewer = UiResources.Viewer;
            var to = viewer != null ? _body.position - viewer.position : Vector3.forward;
            to.y = 0f;
            if (to.sqrMagnitude > 0.0001f) _body.rotation = Quaternion.LookRotation(to.normalized, Vector3.up);

            float d = Mathf.Clamp(width, 0.5f, 1.6f);
            _ring.gameObject.SetActive(true);
            _ring.position = point + Vector3.up * 0.02f;
            _ring.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            _ring.localScale = new Vector3(d, d, 1f);

            // The picture keeps its own colours with a light green cast; the ring is the colour.
            SetColor(_bodyMat, new Color(0.8f, 1f, 0.85f, GhostAlpha));
            SetColor(_ringMat, new Color(ColValid.r, ColValid.g, ColValid.b, 0.85f));
        }

        /// <summary>Somewhere it cannot go: a small red ring on the surface, and no bot.</summary>
        private void PoseInvalid(Vector3 point, Vector3 normal)
        {
            _body.gameObject.SetActive(false);
            _ring.gameObject.SetActive(true);
            _ring.position = point + normal * 0.02f;
            _ring.rotation = Quaternion.LookRotation(-normal, Mathf.Abs(normal.y) > 0.9f ? Vector3.forward : Vector3.up);
            _ring.localScale = new Vector3(0.45f, 0.45f, 1f);
            SetColor(_ringMat, new Color(ColInvalid.r, ColInvalid.g, ColInvalid.b, 0.9f));
        }

        private BotSkins.Skin SelectedSkin()
        {
            try { return BotSkins.Get(SkinIndex); }
            catch { return null; }
        }

        private void ApplySkin(BotSkins.Skin skin)
        {
            _shownSkin = skin;
            _frame = 0;
            _frameTimer = 0f;
            SetTexture(skin != null ? skin.Texture : null);
        }

        /// <summary>A GIF bot's ghost moves too, at the file's own frame delays.</summary>
        private void Animate()
        {
            var skin = _shownSkin;
            if (skin == null || !skin.Animated) return;

            _frameTimer += Time.deltaTime;
            float delay = skin.FrameDelays != null && _frame < skin.FrameDelays.Length
                ? Mathf.Max(0.02f, skin.FrameDelays[_frame]) : 0.1f;
            if (_frameTimer < delay) return;

            _frameTimer -= delay;
            _frame = (_frame + 1) % skin.Frames.Length;
            SetTexture(skin.Frames[_frame]);
        }

        private void SetTexture(Texture tex)
        {
            if (_bodyMat == null) return;
            if (_bodyMat.HasProperty("_BaseMap")) _bodyMat.SetTexture("_BaseMap", tex);
            if (_bodyMat.HasProperty("_MainTex")) _bodyMat.SetTexture("_MainTex", tex);
            _bodyMat.mainTexture = tex;
        }

        private static void SetColor(Material m, Color c) => UiResources.TrySetColor(m, c);

        /// <summary>
        /// A material that honours alpha. The panel's usual unlit shader is opaque, which is why the
        /// old ghost was a solid slab. The sprite shader blends and runs under URP and in single-pass
        /// VR; URP's own unlit, set up for transparency by hand, is the fallback.
        /// </summary>
        private static Material SeeThrough(Texture tex)
        {
            Shader shader = null;
            foreach (var name in new[] { "Sprites/Default", "UI/Default", "Universal Render Pipeline/Unlit" })
            {
                try { shader = Shader.Find(name); } catch { /* next */ }
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
            if (tex != null)
            {
                m.mainTexture = tex;
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            }
            m.renderQueue = 3000;
            return m;
        }

        /// <summary>A soft-edged ring with a faint fill, built in code.</summary>
        private static Texture2D BuildRingTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "NextBotsGhostRing", wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave
            };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float ring = Mathf.SmoothStep(0.78f, 0.84f, r) * (1f - Mathf.SmoothStep(0.94f, 1.0f, r));
                    float fill = r < 0.84f ? 0.12f : 0f;
                    byte a = (byte)(Mathf.Clamp01(Mathf.Max(ring, fill)) * 255f);
                    px[y * size + x] = new Color32(255, 255, 255, a);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private void DrawBeam(Vector3 from, Vector3 to, Color color)
        {
            if (_beam == null) return;
            _beam.SetPosition(0, from);
            _beam.SetPosition(1, to);
            _beam.startColor = color;
            _beam.endColor = color;
            // The beam's shader ignores vertex colour, so red for "can't go here" has to be set on
            // the material too - it stayed green whatever you pointed at.
            UiResources.TrySetColor(_beam.sharedMaterial, color);
        }
    }
}
