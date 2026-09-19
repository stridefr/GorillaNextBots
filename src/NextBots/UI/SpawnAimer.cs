using UnityEngine;
using UnityEngine.AI;

namespace NextBots.UI
{
    /// <summary>
    /// Point-and-place, Garry's-Mod style: raycast from the free hand, show a ghost at the
    /// hit point oriented to the surface, and report that point when SPAWN is pressed.
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

        private GameObject _ghost;
        private Transform _disc;
        private Transform _post;
        private Material _discMat;
        private Material _postMat;
        private LineRenderer _beam;

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
            ShowGhost(true);
            PoseGhost(_placement, hit.normal, color);
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

            // Disc lying on the surface, showing where the feet land.
            var discGo = UiResources.CreateQuad(_ghost.transform, "disc", Vector3.zero,
                new Vector2(0.9f, 0.9f), ColValid, out _discMat);
            _disc = discGo.transform;

            // Upright post, showing which way it will face and roughly how tall it is.
            var postGo = UiResources.CreateQuad(_ghost.transform, "post", Vector3.zero,
                new Vector2(0.25f, 1.4f), ColValid, out _postMat);
            _post = postGo.transform;

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

        private void ShowGhost(bool visible)
        {
            if (_disc != null) _disc.gameObject.SetActive(visible);
            if (_post != null) _post.gameObject.SetActive(visible);
        }

        private void PoseGhost(Vector3 point, Vector3 normal, Color color)
        {
            // Lift very slightly off the surface so the disc does not z-fight with the floor.
            _disc.position = point + normal * 0.01f;
            _disc.rotation = Quaternion.LookRotation(-normal, Vector3.up);

            _post.position = point + Vector3.up * 0.7f;
            var viewer = UiResources.Viewer;
            var toHead = viewer != null ? viewer.position - _post.position : Vector3.forward;
            toHead.y = 0f;
            if (toHead.sqrMagnitude > 0.0001f) _post.rotation = Quaternion.LookRotation(-toHead.normalized, Vector3.up);

            UiResources.TrySetColor(_discMat, color);
            UiResources.TrySetColor(_postMat, color);
        }

        private void DrawBeam(Vector3 from, Vector3 to, Color color)
        {
            if (_beam == null) return;
            _beam.SetPosition(0, from);
            _beam.SetPosition(1, to);
            _beam.startColor = color;
            _beam.endColor = color;
        }
    }
}
