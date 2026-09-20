using GorillaLocomotion;
using UnityEngine;

namespace NextBotsRagdoll
{
    /// <summary>
    /// What a heavy hit does to your hearing: everything goes muffled, and a thin high whine sits on
    /// top of it, both fading as you come round.
    ///
    /// <para><b>One piece of audio processing, not two effects.</b> The muffling and the whine are both
    /// done in <see cref="DazeDsp"/>, on the audio listener's output: the whole mix is low-passed
    /// first and the whine is added <i>afterwards</i>. That order is the point. An earlier version
    /// used the engine's own low-pass filter on the listener and played the whine from a separate
    /// source that was meant to bypass it; in the game the whine came out muffled anyway. Adding it
    /// after the filter, in the same callback, leaves no way for that to happen.</para>
    ///
    /// <para><b>One number drives both.</b> A hit sets a level from 0 to 1. The muffling follows it
    /// down after a short hold; the whine has a slower tail, so it lingers a moment after the world has
    /// cleared, which is what a ringing ear does. A second hit while the first is fading only lifts the
    /// level back up to its own strength - it never stacks past the maximum.</para>
    ///
    /// <para><b>Comfort.</b> The whine is quiet by default and generated, so there is no sound file; the
    /// muffling is moderate and never silences anything; and the game's audio is left exactly as found
    /// when it ends.</para>
    /// </summary>
    public sealed class Daze : MonoBehaviour
    {
        public static Daze Instance { get; private set; }

        /// <summary>How long a hit holds at full before it starts to clear.</summary>
        private const float Hold = 0.35f;

        /// <summary>Everything above this is left alone, i.e. no muffling at all.</summary>
        private const float OpenCutoff = 20000f;

        /// <summary>The dullest it can go at <c>Muffle = 1</c>: about the sound through a wall.</summary>
        private const float DullestCutoff = 1400f;

        private float _muffle;   // 0..1
        private float _ring;     // 0..1
        private float _sinceHit = 999f;

        private AudioListener _listener;
        private DazeDsp _dsp;
        private bool _dspNeedsSample;
        private float _nextListenerLookup;

        private void Awake() => Instance = this;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Release();
        }

        private void OnDisable() => Release();

        // ================================================================== hitting

        /// <summary>
        /// Something hit hard. <paramref name="power"/> is 0 to 1: 1 is the worst hit there is. It
        /// only ever raises what is already there.
        /// </summary>
        public void Hit(float power)
        {
            if (!BridgeConfig.DazeEnabled.Value) return;
            power = Mathf.Clamp01(power);
            if (power < 0.05f) return;

            // The bang first, at the head, then the ringing behind it. Only for a proper hit; a bump
            // that barely registers should not go off like a flashbang.
            if (power >= 0.5f && ImpactSounds.Instance != null)
            {
                var eye = ViewCameras.Eye;
                if (eye != null) ImpactSounds.Instance.PlayConcussion(eye.transform.position, 0.55f + 0.3f * power);
            }

            bool fresh = _muffle <= 0.001f && _ring <= 0.001f;
            _muffle = Mathf.Max(_muffle, power);
            _ring = Mathf.Max(_ring, power);
            _sinceHit = 0f;
            if (fresh) _dspNeedsSample = true;

            Plugin.Log.LogInfo("[Daze] hit " + power.ToString("0.00") + " | muffle " +
                               BridgeConfig.DazeMuffle.Value.ToString("0.00") + " ring " +
                               BridgeConfig.DazeRingVolume.Value.ToString("0.00") + " for ~" +
                               BridgeConfig.DazeSeconds.Value.ToString("0.0") + "s");
        }

        // ================================================================== every frame

        private void Update()
        {
            if (_muffle <= 0.001f && _ring <= 0.001f)
            {
                if (_dsp != null) Release();
                return;
            }

            if (!BridgeConfig.DazeEnabled.Value) { _muffle = _ring = 0f; Release(); return; }

            float dt = Time.unscaledDeltaTime;
            _sinceHit += dt;

            // A hold at full, then an exponential clear. The whine's tail is longer than the muffle's.
            float seconds = Mathf.Max(0.5f, BridgeConfig.DazeSeconds.Value);
            if (_sinceHit > Hold)
            {
                _muffle *= Mathf.Exp(-dt * 3f / seconds);
                _ring *= Mathf.Exp(-dt * 2.2f / seconds);
            }

            var dsp = Attach();
            if (dsp == null) return;

            // A WAV in the ring folder replaces the generated tone. Looked up once per effect, not
            // every frame: reading a clip's samples is not free.
            if (_dspNeedsSample)
            {
                _dspNeedsSample = false;
                float[] mono; int rate;
                if (ImpactSounds.Instance != null && ImpactSounds.Instance.TryGetRing(out mono, out rate))
                {
                    dsp.SetSample(mono, rate);
                    Plugin.Log.LogInfo("[Daze] ringing from the ring folder's WAV (" + mono.Length + " samples at " + rate + " Hz)");
                }
                else dsp.SetSample(null, 0);
            }

            // Cutoff runs on a log scale, because that is how hearing works: halving the frequency is
            // the same step wherever you start. Muffle sets how far down it can go.
            float amount = Mathf.Clamp01(BridgeConfig.DazeMuffle.Value);
            float floor = OpenCutoff * Mathf.Pow(DullestCutoff / OpenCutoff, amount);
            // Closed over a tenth of a second rather than in one step, so it does not click.
            float t = Mathf.Pow(Mathf.Clamp01(_muffle), 0.8f) * Mathf.SmoothStep(0f, 1f, _sinceHit / 0.1f);
            dsp.Cutoff = OpenCutoff * Mathf.Pow(floor / OpenCutoff, t);

            // Eased in at the very start so the whine never clicks on, and shaped so it is barely there
            // when the level is low. RingVolume is the amplitude at the very worst.
            float attack = Mathf.Clamp01(_sinceHit / 0.06f);
            float gain = Mathf.Pow(Mathf.Clamp01(_ring), 1.5f) * attack * Mathf.Clamp01(BridgeConfig.DazeRingVolume.Value) * 0.35f;
            dsp.RingGain = Mathf.Min(gain, 0.3f);
            dsp.Active = true;
        }

        // ================================================================== plumbing

        /// <summary>Finds the game's audio listener and puts the processor beside it. Anything on the
        /// same object as an AudioListener processes the listener's whole output.</summary>
        private DazeDsp Attach()
        {
            if (_dsp != null && _listener != null && _listener.isActiveAndEnabled) return _dsp;
            if (Time.unscaledTime < _nextListenerLookup) return null;
            _nextListenerLookup = Time.unscaledTime + 1f;

            // The old listener is gone or has moved: take the processor off it before using a new one.
            Release();

            AudioListener found = null;
            var p = GTPlayer.Instance;
            var cam = p != null ? p.mainCamera : null;
            if (cam != null) found = cam.GetComponent<AudioListener>();
            if (found == null) found = FindFirstObjectByType<AudioListener>();
            if (found == null) return null;

            _listener = found;
            _dsp = found.gameObject.AddComponent<DazeDsp>();
            _dspNeedsSample = true;
            Plugin.Log.LogInfo("[Daze] audio processing on the listener '" + found.name + "' at " +
                               AudioSettings.outputSampleRate + " Hz");
            return _dsp;
        }

        /// <summary>Puts everything back as it was: the game's audio is not ours to leave altered.</summary>
        private void Release()
        {
            if (_dsp != null) Destroy(_dsp);
            _dsp = null;
            _listener = null;
        }
    }

    /// <summary>
    /// The audio processing itself, run on the audio thread by <c>OnAudioFilterRead</c>: a low-pass
    /// over the whole mix, then the whine added on top of the result.
    ///
    /// <para>Everything here runs on a thread the engine owns, so it allocates nothing, touches no
    /// Unity objects, and reads its three settings as plain fields the main thread writes.</para>
    /// </summary>
    public sealed class DazeDsp : MonoBehaviour
    {
        // Written by the main thread, read by the audio thread. Single floats and a bool are written
        // atomically, and a value a buffer late is inaudible.
        public volatile float Cutoff = 20000f;
        public volatile float RingGain;
        public volatile bool Active;

        // An optional WAV to ring with instead of the sines, already made loopable. Swapped in as a
        // whole array reference, so the audio thread sees either the old one or the new, never half.
        private volatile float[] _sample;
        private double _samplePos;
        private double _sampleStep = 1.0;

        /// <summary>Use this mono WAV for the ringing, looped, in place of the generated tone. Null goes
        /// back to the tone.</summary>
        public void SetSample(float[] mono, int rate)
        {
            if (mono == null || mono.Length < 512 || rate <= 0) { _sample = null; return; }

            // Loud enough to matter, and a loop with no seam: the tail is faded into the head over the
            // last stretch, so the end runs straight into the start.
            float peak = 0.0001f;
            for (int i = 0; i < mono.Length; i++) peak = Mathf.Max(peak, Mathf.Abs(mono[i]));

            int x = Mathf.Min(mono.Length / 4, Mathf.Max(64, rate / 10));
            int len = mono.Length - x;
            var loop = new float[len];
            for (int i = 0; i < len; i++) loop[i] = mono[i] / peak;
            for (int i = 0; i < x; i++)
            {
                float t = i / (float)x;
                loop[i] = (mono[i] * t + mono[len + i] * (1f - t)) / peak;
            }

            _sampleStep = rate / (double)_rate;
            _samplePos = 0;
            _sample = loop;
        }

        private int _rate = 48000;

        // Biquad low-pass state, per channel.
        private readonly float[] _z1 = new float[8];
        private readonly float[] _z2 = new float[8];
        private float _b0, _b1, _b2, _a1, _a2;
        private float _coefCutoff = -1f;

        // The whine: three sines a few Hz apart so it shimmers slowly, the way a real ring does,
        // instead of sounding like a test tone.
        private double _p1, _p2, _p3;
        private float _lastGain;
        private bool _wasActive;

        private void Awake()
        {
            // Read here, on the main thread: the audio thread may not call into the engine.
            _rate = AudioSettings.outputSampleRate;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!Active)
            {
                if (_wasActive) Reset();
                return;
            }
            _wasActive = true;

            if (channels < 1 || channels > 8) return;

            float cutoff = Cutoff;
            bool filtering = cutoff < 19000f;
            if (filtering && Mathf.Abs(cutoff - _coefCutoff) > 1f) Coefficients(cutoff);

            int frames = data.Length / channels;
            float gainTo = RingGain;
            float gainFrom = _lastGain;

            var sample = _sample;
            double inc1 = 2.0 * System.Math.PI * 3150.0 / _rate;
            double inc2 = 2.0 * System.Math.PI * 3187.0 / _rate;
            double inc3 = 2.0 * System.Math.PI * 6311.0 / _rate;

            for (int i = 0; i < frames; i++)
            {
                // The whine, with its gain ramped across the buffer so it never steps.
                float g = gainFrom + (gainTo - gainFrom) * (i / (float)frames);
                float ring = 0f;
                if (g > 1e-6f)
                {
                    if (sample != null)
                    {
                        int i0 = (int)_samplePos;
                        int i1 = i0 + 1 >= sample.Length ? 0 : i0 + 1;
                        float f = (float)(_samplePos - i0);
                        ring = (sample[i0] + (sample[i1] - sample[i0]) * f) * g;
                        _samplePos += _sampleStep;
                        if (_samplePos >= sample.Length) _samplePos -= sample.Length;
                    }
                    else
                    {
                        ring = (float)(System.Math.Sin(_p1) * 0.55 + System.Math.Sin(_p2) * 0.3 + System.Math.Sin(_p3) * 0.1) * g;
                    }
                }
                _p1 += inc1; _p2 += inc2; _p3 += inc3;

                int at = i * channels;
                for (int c = 0; c < channels; c++)
                {
                    float x = data[at + c];

                    if (filtering)
                    {
                        // Direct form II transposed.
                        float y = _b0 * x + _z1[c];
                        _z1[c] = _b1 * x - _a1 * y + _z2[c];
                        _z2[c] = _b2 * x - _a2 * y;
                        x = y;
                    }

                    // After the filter, so nothing the filter does can reach it.
                    data[at + c] = x + ring;
                }
            }

            // Keep the phases small; a double drifts audibly after hours otherwise.
            const double twoPi = 2.0 * System.Math.PI;
            if (_p1 > twoPi) _p1 -= twoPi;
            if (_p2 > twoPi) _p2 -= twoPi;
            if (_p3 > twoPi) _p3 -= twoPi;
            _lastGain = gainTo;
        }

        /// <summary>A second-order Butterworth low-pass: flat below the cutoff and rolling off
        /// smoothly above it, with no resonant bump to colour the sound.</summary>
        private void Coefficients(float cutoff)
        {
            float w0 = 2f * Mathf.PI * Mathf.Min(cutoff, _rate * 0.45f) / _rate;
            float cos = Mathf.Cos(w0);
            float alpha = Mathf.Sin(w0) / (2f * 0.7071f);

            float a0 = 1f + alpha;
            _b0 = (1f - cos) * 0.5f / a0;
            _b1 = (1f - cos) / a0;
            _b2 = _b0;
            _a1 = -2f * cos / a0;
            _a2 = (1f - alpha) / a0;
            _coefCutoff = cutoff;
        }

        private void Reset()
        {
            System.Array.Clear(_z1, 0, _z1.Length);
            System.Array.Clear(_z2, 0, _z2.Length);
            _coefCutoff = -1f;
            _lastGain = 0f;
            _wasActive = false;
        }
    }
}
