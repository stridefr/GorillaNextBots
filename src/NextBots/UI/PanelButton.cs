using System;
using UnityEngine;

namespace NextBots.UI
{
    /// <summary>
    /// A button on a hand-anchored panel, tested with maths rather than physics colliders.
    ///
    /// Two problems this exists to solve:
    ///
    /// - **Thin press volumes get skipped.** A hand moving 2 m/s covers ~22 mm per frame at
    ///   90 fps, so a 10 mm-thick button can be missed entirely between samples. This does a
    ///   swept segment-vs-box test between the previous and current fingertip position, so a
    ///   fast pass registers regardless of frame timing, and pads the box in depth on top.
    ///
    /// - **Hand-mounted colliders shove the player around.** There are no colliders here at
    ///   all, so a panel strapped to your wrist cannot push you.
    /// </summary>
    public class PanelButton
    {
        /// <summary>Depth padding either side of the panel plane. Generous on purpose.</summary>
        public const float DefaultDepth = 0.035f;

        public string Id;
        public string Label;

        /// <summary>Centre in panel-local metres. Origin is panel centre, +x right, +y up.</summary>
        public Vector2 Center;

        /// <summary>Size in panel-local metres.</summary>
        public Vector2 Size;

        public float Depth = DefaultDepth;
        public bool Enabled = true;

        /// <summary>Repeat while held. Off for one-shot actions like SPAWN or CLEAR ALL.</summary>
        public bool Repeats;

        /// <summary>Fires on press, and again on each repeat.</summary>
        public Action<PanelButton> OnPress;

        // ---- runtime state ----
        private bool _inside;
        private float _pressedAt;
        private float _lastRepeatAt;
        private int _repeatCount;

        public bool IsHeld => _inside;

        /// <summary>Seconds held before the first repeat.</summary>
        public float RepeatDelay = 0.45f;

        /// <summary>Seconds between repeats, before acceleration.</summary>
        public float RepeatInterval = 0.14f;

        /// <summary>Fastest repeat interval, reached after sustained holding.</summary>
        public float RepeatIntervalMin = 0.045f;

        public Rect LocalRect =>
            new Rect(Center.x - Size.x * 0.5f, Center.y - Size.y * 0.5f, Size.x, Size.y);

        /// <summary>
        /// Feed one fingertip for this frame, in panel-local space. Returns true if the
        /// button fired (initial press or a repeat).
        /// </summary>
        public bool Tick(Vector3 localPrev, Vector3 localNow, float now)
        {
            if (!Enabled)
            {
                _inside = false;
                return false;
            }

            var min = new Vector3(Center.x - Size.x * 0.5f, Center.y - Size.y * 0.5f, -Depth);
            var max = new Vector3(Center.x + Size.x * 0.5f, Center.y + Size.y * 0.5f, Depth);

            // Swept test: the finger may have passed clean through between samples.
            var hit = SegmentIntersectsBox(localPrev, localNow, min, max);

            if (hit && !_inside)
            {
                _inside = true;
                _pressedAt = now;
                _lastRepeatAt = now;
                _repeatCount = 0;
                Fire();
                return true;
            }

            if (!hit)
            {
                _inside = false;
                return false;
            }

            // Held. Hold-to-repeat, because stepping a value from 0.15 to 1.0 one press at a
            // time is miserable otherwise.
            if (!Repeats) return false;

            if (now - _pressedAt < RepeatDelay) return false;

            var interval = Mathf.Lerp(RepeatInterval, RepeatIntervalMin, Mathf.Clamp01(_repeatCount / 12f));
            if (now - _lastRepeatAt < interval) return false;

            _lastRepeatAt = now;
            _repeatCount++;
            Fire();
            return true;
        }

        /// <summary>Called when the panel closes or the hand leaves, so a hold does not latch.</summary>
        public void Release()
        {
            _inside = false;
            _repeatCount = 0;
        }

        private void Fire()
        {
            try { OnPress?.Invoke(this); }
            catch (Exception ex) { Plugin.Log.LogError("Button '" + Id + "' handler threw: " + ex); }
        }

        /// <summary>
        /// Slab method. Exact segment-vs-AABB, so no sub-sampling and nothing to skip.
        /// </summary>
        public static bool SegmentIntersectsBox(Vector3 a, Vector3 b, Vector3 min, Vector3 max)
        {
            var d = b - a;
            var tMin = 0f;
            var tMax = 1f;

            for (int axis = 0; axis < 3; axis++)
            {
                var origin = a[axis];
                var dir = d[axis];
                var lo = min[axis];
                var hi = max[axis];

                if (Mathf.Abs(dir) < 1e-8f)
                {
                    // Parallel to this slab: reject only if it starts outside it.
                    if (origin < lo || origin > hi) return false;
                    continue;
                }

                var inv = 1f / dir;
                var t1 = (lo - origin) * inv;
                var t2 = (hi - origin) * inv;
                if (t1 > t2) { var tmp = t1; t1 = t2; t2 = tmp; }

                if (t1 > tMin) tMin = t1;
                if (t2 < tMax) tMax = t2;
                if (tMin > tMax) return false;
            }

            return true;
        }
    }
}
