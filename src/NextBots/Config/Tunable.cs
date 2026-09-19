using System;
using System.Collections.Generic;
using UnityEngine;

namespace NextBots.Config
{
    /// <summary>
    /// One live-tunable setting, described rather than hard-coded, so the in-VR panel can
    /// browse an unlimited number of them with four buttons (prev / next / minus / plus).
    /// Every setting reports back what a press actually did, which is what makes a button
    /// that worked-but-had-nothing-left-to-do distinguishable from one that is broken.
    /// </summary>
    public abstract class Tunable
    {
        protected Tunable(string key, string label, string group)
        {
            Key = key;
            Label = label;
            Group = group;
        }

        public string Key { get; }
        public string Label { get; }
        public string Group { get; }

        /// <summary>Current value rendered for the readout, e.g. "0.40" or "ON".</summary>
        public abstract string Display { get; }

        /// <summary>Step the value. Returns what happened, for the confirmation line.</summary>
        public abstract AdjustResult Adjust(int direction, bool coarse);

        /// <summary>Serialised form, for syncing settings to the rest of the lobby.</summary>
        public abstract float Raw { get; set; }
    }

    public enum AdjustOutcome
    {
        Changed,
        AtLimit
    }

    public readonly struct AdjustResult
    {
        public readonly AdjustOutcome Outcome;
        public readonly string Message;

        public AdjustResult(AdjustOutcome outcome, string message)
        {
            Outcome = outcome;
            Message = message;
        }

        public bool Changed => Outcome == AdjustOutcome.Changed;
    }

    public sealed class TunableFloat : Tunable
    {
        private readonly Func<float> _get;
        private readonly Action<float> _set;
        private readonly string _format;

        public float Min { get; }
        public float Max { get; }
        public float Step { get; }
        public float CoarseStep { get; }
        public string Unit { get; }

        public TunableFloat(string key, string label, string group,
                            Func<float> get, Action<float> set,
                            float min, float max, float step,
                            string unit = "", string format = "0.00", float coarseStep = 0f)
            : base(key, label, group)
        {
            _get = get;
            _set = set;
            Min = min;
            Max = max;
            Step = step;
            CoarseStep = coarseStep > 0f ? coarseStep : step * 5f;
            Unit = unit;
            _format = format;
        }

        public float Value
        {
            get { return _get(); }
            set { _set(Mathf.Clamp(value, Min, Max)); }
        }

        public override float Raw
        {
            get { return _get(); }
            set { _set(Mathf.Clamp(value, Min, Max)); }
        }

        public override string Display => _get().ToString(_format) + Unit;

        public override AdjustResult Adjust(int direction, bool coarse)
        {
            var before = _get();
            var step = coarse ? CoarseStep : Step;
            var after = Mathf.Clamp(before + step * Mathf.Sign(direction), Min, Max);

            // Snap away float dust so the readout doesn't show 0.30000001.
            after = Mathf.Round(after / step) * step;
            after = Mathf.Clamp(after, Min, Max);

            if (Mathf.Approximately(after, before))
            {
                var edge = direction > 0 ? "MAX" : "MIN";
                return new AdjustResult(AdjustOutcome.AtLimit,
                    Label + " AT " + edge + " (" + Display + ")");
            }

            _set(after);
            return new AdjustResult(AdjustOutcome.Changed, Label + " = " + Display);
        }
    }

    public sealed class TunableBool : Tunable
    {
        private readonly Func<bool> _get;
        private readonly Action<bool> _set;

        public TunableBool(string key, string label, string group, Func<bool> get, Action<bool> set)
            : base(key, label, group)
        {
            _get = get;
            _set = set;
        }

        public bool Value
        {
            get { return _get(); }
            set { _set(value); }
        }

        public override float Raw
        {
            get { return _get() ? 1f : 0f; }
            set { _set(value >= 0.5f); }
        }

        public override string Display => _get() ? "ON" : "OFF";

        public override AdjustResult Adjust(int direction, bool coarse)
        {
            // A toggle is never "at limit" - either direction flips it.
            _set(!_get());
            return new AdjustResult(AdjustOutcome.Changed, Label + " = " + Display);
        }
    }

    public sealed class TunableInt : Tunable
    {
        private readonly Func<int> _get;
        private readonly Action<int> _set;

        public int Min { get; }
        public int Max { get; }
        public int Step { get; }

        public TunableInt(string key, string label, string group,
                          Func<int> get, Action<int> set, int min, int max, int step = 1)
            : base(key, label, group)
        {
            _get = get;
            _set = set;
            Min = min;
            Max = max;
            Step = step;
        }

        public int Value
        {
            get { return _get(); }
            set { _set(Mathf.Clamp(value, Min, Max)); }
        }

        public override float Raw
        {
            get { return _get(); }
            set { _set(Mathf.Clamp(Mathf.RoundToInt(value), Min, Max)); }
        }

        public override string Display => _get().ToString();

        public override AdjustResult Adjust(int direction, bool coarse)
        {
            var before = _get();
            var after = Mathf.Clamp(before + Step * (int)Mathf.Sign(direction) * (coarse ? 5 : 1), Min, Max);
            if (after == before)
            {
                var edge = direction > 0 ? "MAX" : "MIN";
                return new AdjustResult(AdjustOutcome.AtLimit, Label + " AT " + edge + " (" + Display + ")");
            }
            _set(after);
            return new AdjustResult(AdjustOutcome.Changed, Label + " = " + Display);
        }
    }
}
