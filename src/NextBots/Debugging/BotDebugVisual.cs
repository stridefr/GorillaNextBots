using System.Collections.Generic;
using NextBots.Brain;
using NextBots.UI;
using TMPro;
using UnityEngine;

namespace NextBots.Debugging
{
    /// <summary>
    /// Draws what a bot is actually doing: its real computed path (from
    /// <c>NavMeshPath.corners</c>, not a guess), its destination, the line to its target, and
    /// a state label - all coloured by state, matching the simulator's colour coding.
    ///
    /// Worth having early. Almost every behaviour bug shows up here as an obviously wrong
    /// line before it is visible as odd movement.
    /// </summary>
    public class BotDebugVisual : MonoBehaviour
    {
        private LineRenderer _path;
        private LineRenderer _toTarget;
        private Transform _destMarker;
        private Material _destMat;
        private PanelText _stateLabel;

        private readonly List<Vector3> _corners = new List<Vector3>(32);
        private readonly List<Vector3> _smoothed = new List<Vector3>(96);
        private Vector3[] _drawn = new Vector3[0];

        /// <summary>Points drawn along the route. More = smoother curve, slightly more cost.</summary>
        public int CurveResolution = 48;

        /// <summary>How quickly the drawn line eases to a newly computed route.</summary>
        public float EaseSpeed = 9f;

        private void Awake()
        {
            _path = MakeLine("path", 0.03f, 0.03f);
            _toTarget = MakeLine("toTarget", 0.012f, 0.012f);

            Material mat;
            var marker = UiResources.CreateQuad(transform, "dest", Vector3.zero,
                new Vector2(0.5f, 0.5f), Color.white, out mat);
            _destMarker = marker.transform;
            _destMat = mat;

            _stateLabel = new PanelText(transform, "state", Vector3.zero, 3f, 0.12f,
                Color.white, TextAlignmentOptions.Center);
        }

        /// <summary>
        /// Resample a corner list into a smooth polyline. Catmull-Rom passes through every
        /// original corner, so the drawn curve still shows the real route rather than a
        /// vaguely similar one.
        /// </summary>
        private static void BuildCurve(List<Vector3> corners, List<Vector3> into, int resolution)
        {
            into.Clear();
            if (corners.Count < 2) return;

            if (corners.Count == 2)
            {
                for (int i = 0; i <= resolution; i++)
                    into.Add(Vector3.Lerp(corners[0], corners[1], i / (float)resolution));
                return;
            }

            var segments = corners.Count - 1;
            var perSegment = Mathf.Max(2, resolution / segments);

            for (int seg = 0; seg < segments; seg++)
            {
                // Duplicate the ends so the curve starts and finishes exactly on the path.
                var p0 = corners[Mathf.Max(seg - 1, 0)];
                var p1 = corners[seg];
                var p2 = corners[seg + 1];
                var p3 = corners[Mathf.Min(seg + 2, corners.Count - 1)];

                for (int i = 0; i < perSegment; i++)
                {
                    var t = i / (float)perSegment;
                    into.Add(CatmullRom(p0, p1, p2, p3, t));
                }
            }
            into.Add(corners[corners.Count - 1]);
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            var t2 = t * t;
            var t3 = t2 * t;
            return 0.5f * ((2f * p1) +
                           (-p0 + p2) * t +
                           (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                           (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        /// <summary>
        /// Move the drawn points toward the freshly computed ones instead of replacing them,
        /// so a recomputed route slides into place rather than blinking.
        /// </summary>
        private void EaseToward(List<Vector3> target)
        {
            if (target.Count == 0) { _drawn = new Vector3[0]; return; }

            // Point count changes whenever the route does; rebuild rather than ease across a
            // mismatched pair, which would smear points along the wrong parts of the curve.
            if (_drawn.Length != target.Count)
            {
                _drawn = new Vector3[target.Count];
                for (int i = 0; i < target.Count; i++) _drawn[i] = target[i] + Vector3.up * 0.05f;
                return;
            }

            var k = 1f - Mathf.Exp(-EaseSpeed * Time.deltaTime);   // frame-rate independent
            for (int i = 0; i < target.Count; i++)
                _drawn[i] = Vector3.Lerp(_drawn[i], target[i] + Vector3.up * 0.05f, k);
        }

        private LineRenderer MakeLine(string name, float startWidth, float endWidth)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 0;
            lr.startWidth = startWidth;
            lr.endWidth = endWidth;
            lr.numCapVertices = 2;
            lr.material = UiResources.NewColorMaterial(Color.white);
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        public void SetVisible(bool visible)
        {
            if (gameObject.activeSelf != visible) gameObject.SetActive(visible);
        }

        /// <summary>Refresh from the bot. Call once per brain tick, not per frame.</summary>
        public void Render(IBotBody body, NextBotBrain brain, IWorldView world)
        {
            if (body == null || brain == null) return;

            var color = BotStateColors.Of(brain.State);

            // Real path, straight off the agent - then smoothed and eased.
            //
            // Raw NavMesh corners are sparse and change wholesale whenever the route is
            // recomputed, which is ten times a second. Drawn directly that is a hard-angled
            // line snapping around the map. Resampling along a Catmull-Rom curve gives a
            // readable route, and easing the drawn points toward the new one turns a jump
            // cut into a slide.
            _corners.Clear();
            if (body.TryGetPathCorners(_corners) && _corners.Count >= 2)
            {
                BuildCurve(_corners, _smoothed, CurveResolution);
                EaseToward(_smoothed);

                _path.positionCount = _drawn.Length;
                for (int i = 0; i < _drawn.Length; i++) _path.SetPosition(i, _drawn[i]);
                _path.startColor = color;
                _path.endColor = new Color(color.r, color.g, color.b, 0.5f);
                _path.enabled = _drawn.Length >= 2;
            }
            else
            {
                _path.enabled = false;
                _drawn = new Vector3[0];
            }

            // Destination.
            _destMarker.position = brain.Destination + Vector3.up * 0.03f;
            _destMarker.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            UiResources.TrySetColor(_destMat, color);

            // Line to the current target, so retargeting is visible as it happens.
            PlayerSnapshot target;
            var view = world as Runtime.WorldView;
            if (view != null && brain.TargetActor >= 0 && view.TryGetPlayer(brain.TargetActor, out target))
            {
                _toTarget.positionCount = 2;
                _toTarget.SetPosition(0, body.Center);
                _toTarget.SetPosition(1, target.Center);
                _toTarget.startColor = color;
                _toTarget.endColor = new Color(color.r, color.g, color.b, 0.25f);
                _toTarget.enabled = true;
            }
            else _toTarget.enabled = false;

            // State label, billboarded at the bot's head.
            if (_stateLabel != null)
            {
                _stateLabel.Text = brain.State.ToString().ToUpperInvariant() + "\n" + brain.LastTransition;
                _stateLabel.Color = color;
                var t = _stateLabel.GameObject.transform;
                t.position = body.Position + Vector3.up * 2.2f;
                var viewer = UiResources.Viewer;
                if (viewer != null)
                {
                    var to = t.position - viewer.position;
                    to.y = 0f;
                    if (to.sqrMagnitude > 0.0001f) t.rotation = Quaternion.LookRotation(to.normalized, Vector3.up);
                }
            }
        }
    }
}
