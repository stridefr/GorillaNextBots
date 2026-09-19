using UnityEngine;

namespace NextBots.UI
{
    /// <summary>
    /// Fingertip positions for the local player's two hands, plus the previous frame's, so
    /// button tests can sweep the segment between them.
    ///
    /// VR hand transforms are wrists, not fingertips - a button driven off raw hand position
    /// fires when your arm swings past. Rather than guess an offset, this uses
    /// <c>GorillaTagger.leftHandTriggerCollider</c> / <c>rightHandTriggerCollider</c>, which
    /// are the transforms GT's own pressable buttons collide against.
    /// </summary>
    public class HandTips
    {
        public struct Tip
        {
            public bool Valid;
            public Vector3 Position;
            public Vector3 Previous;
            public float Speed;
        }

        public Tip Left;
        public Tip Right;

        private Transform _leftTip;
        private Transform _rightTip;
        private bool _primed;

        public bool Resolved => _leftTip != null && _rightTip != null;

        public Tip For(bool isLeft) => isLeft ? Left : Right;

        /// <summary>Re-acquire the tip transforms. Cheap no-op once they are found.</summary>
        public bool Resolve()
        {
            if (_leftTip != null && _rightTip != null) return true;

            var tagger = GorillaTagger.Instance;
            if (tagger == null) return false;

            if (tagger.leftHandTriggerCollider != null) _leftTip = tagger.leftHandTriggerCollider.transform;
            if (tagger.rightHandTriggerCollider != null) _rightTip = tagger.rightHandTriggerCollider.transform;

            return _leftTip != null && _rightTip != null;
        }

        public void Update(float dt)
        {
            if (!Resolve())
            {
                Left.Valid = false;
                Right.Valid = false;
                _primed = false;
                return;
            }

            Left = Step(Left, _leftTip, dt, _primed);
            Right = Step(Right, _rightTip, dt, _primed);
            _primed = true;
        }

        private static Tip Step(Tip prev, Transform t, float dt, bool primed)
        {
            var pos = t.position;
            var previous = primed && prev.Valid ? prev.Position : pos;
            return new Tip
            {
                Valid = true,
                Position = pos,
                Previous = previous,
                Speed = dt > 0.0001f ? Vector3.Distance(pos, previous) / dt : 0f
            };
        }
    }
}
