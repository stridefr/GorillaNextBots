using GorillaLocomotion;
using UnityEngine;

namespace NextBotsRagdoll
{
    /// <summary>
    /// What a heavy hit does to your hearing: everything goes muffled, as if underwater, and a thin
    /// high whine sits on top of it, both fading as you come round.
    ///
    /// <para><b>Two effects, deliberately different.</b> The muffling is a low-pass filter on the
    /// game's audio listener, which sits on the whole mix - music, ambience, other players, the
    /// bots - so the world itself goes dull. The ringing is a tone made in code and played from its
    /// own source that <i>ignores</i> that filter: otherwise the muffling would squash the very
    /// whine it is meant to sit under. No sound file ships with it.</para>
    ///
    /// <para><b>One number drives both.</b> A hit sets a level from 0 to 1. The muffling follows it
    /// straight down after a short hold; the ringing has a slower tail, so a whine lingers a moment
    /// after the world has cleared, which is what tinnitus does. A second hit while the first is
    /// fading only lifts the level back up to its own strength - it never stacks past the maximum.</para>
    ///
    /// <para><b>Comfort.</b> The ring is quiet by default and hard-capped, the muffle never goes
    /// silent, and the whole effect is one line to switch off. Nothing here is loud.</para>
    /// </summary>
    public sealed class Daze : MonoBehaviour
    {
        public static Daze Instance { get; private set; }

        /// <summary>The whine, in Hz. Whole numbers, so the one-second loop closes on a whole cycle.</summary>
        private const int RingHz = 3150;
        private const int SampleRate = 44100;

        /// <summary>How long a hit holds at full before it starts to clear.</summary>
        private const float Hold = 0.35f;

        /// <summary>Nothing goes below this: never deaf, and always able to hear a bot coming.</summary>
        private const float FloorCutoff = 500f;
        private const float OpenCutoff = 22000f;

        private float _muffle;   // 0..1
        private float _ring;     // 0..1
        private float _sinceHit = 999f;

        private AudioListener _listener;
        private AudioLowPassFilter _filter;
        private bool _weAddedFilter;
        private bool _filterWasEnabled;
        private float _filterOldCutoff = OpenCutoff;
        private float _nextListenerLookup;

        private AudioSource _source;
        private AudioClip _clip;

        private void Awake() => Instance = this;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Release();
            if (_clip != null) Destroy(_clip);
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

            _muffle = Mathf.Max(_muffle, power);
            _ring = Mathf.Max(_ring, power);
            _sinceHit = 0f;

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
                if (_filter != null || (_source != null && _source.isPlaying)) Release();
                return;
            }

            if (!BridgeConfig.DazeEnabled.Value) { _muffle = _ring = 0f; Release(); return; }

            float dt = Time.unscaledDeltaTime;
            _sinceHit += dt;

            // A hold at full, then an exponential clear. The ring's tail is longer than the muffle's.
            float seconds = Mathf.Max(0.5f, BridgeConfig.DazeSeconds.Value);
            if (_sinceHit > Hold)
            {
                _muffle *= Mathf.Exp(-dt * 3f / seconds);
                _ring *= Mathf.Exp(-dt * 2.2f / seconds);
            }

            ApplyMuffle();
            ApplyRing();
        }

        private void ApplyMuffle()
        {
            var listener = FindListener();
            if (listener == null) return;

            if (_filter == null)
            {
                _filter = listener.GetComponent<AudioLowPassFilter>();
                _weAddedFilter = _filter == null;
                if (_filter == null) _filter = listener.gameObject.AddComponent<AudioLowPassFilter>();
                else { _filterWasEnabled = _filter.enabled; _filterOldCutoff = _filter.cutoffFrequency; }
                _filter.lowpassResonanceQ = 1f;
                Plugin.Log.LogInfo("[Daze] low-pass on the audio listener '" + listener.name + "'" +
                                   (_weAddedFilter ? "" : " (it already had one)"));
            }

            // Cutoff runs on a log scale, because that is how hearing works: halving the frequency is
            // the same step wherever you start.
            float floor = Mathf.Lerp(OpenCutoff, FloorCutoff, Mathf.Clamp01(BridgeConfig.DazeMuffle.Value));
            float t = Mathf.Pow(Mathf.Clamp01(_muffle), 0.8f);
            _filter.cutoffFrequency = OpenCutoff * Mathf.Pow(floor / OpenCutoff, t);
            _filter.enabled = true;
        }

        private void ApplyRing()
        {
            if (BridgeConfig.DazeRingVolume.Value <= 0.001f) { StopRing(); return; }

            if (_source == null && !BuildRing()) return;

            // Eased in at the very start so the tone never clicks on, and shaped so it is barely
            // there when the level is low.
            float attack = Mathf.Clamp01(_sinceHit / 0.06f);
            float v = Mathf.Pow(Mathf.Clamp01(_ring), 1.5f) * attack * Mathf.Clamp01(BridgeConfig.DazeRingVolume.Value);
            _source.volume = Mathf.Min(v, 0.6f);

            if (v > 0.003f && !_source.isPlaying) _source.Play();
            else if (v <= 0.003f && _source.isPlaying) _source.Stop();
        }

        // ================================================================== plumbing

        private AudioListener FindListener()
        {
            if (_listener != null && _listener.isActiveAndEnabled) return _listener;
            if (Time.unscaledTime < _nextListenerLookup) return null;
            _nextListenerLookup = Time.unscaledTime + 1f;

            // The old listener is gone or has moved: hand its filter back before taking a new one.
            ReleaseFilter();

            AudioListener found = null;
            var p = GTPlayer.Instance;
            var cam = p != null ? p.mainCamera : null;
            if (cam != null) found = cam.GetComponent<AudioListener>();
            if (found == null) found = FindFirstObjectByType<AudioListener>();
            _listener = found;
            return _listener;
        }

        private bool BuildRing()
        {
            try
            {
                // A one-second loop of three sines a few Hz apart, so it shimmers slowly the way a
                // real ring does instead of sounding like a test tone. Every frequency is a whole
                // number, so the loop closes on a whole cycle and has no seam.
                var data = new float[SampleRate];
                for (int i = 0; i < data.Length; i++)
                {
                    float t = i / (float)SampleRate;
                    float s = Mathf.Sin(2f * Mathf.PI * RingHz * t) * 0.55f
                            + Mathf.Sin(2f * Mathf.PI * (RingHz + 37) * t) * 0.3f
                            + Mathf.Sin(2f * Mathf.PI * (RingHz * 2 + 11) * t) * 0.1f;
                    data[i] = s * 0.5f;
                }
                _clip = AudioClip.Create("NextBotsRagdoll.Ring", SampleRate, 1, SampleRate, false);
                _clip.SetData(data, 0);

                var go = new GameObject("NextBotsRagdoll.Ring");
                go.transform.SetParent(transform, false);
                _source = go.AddComponent<AudioSource>();
                _source.clip = _clip;
                _source.loop = true;
                _source.playOnAwake = false;
                _source.spatialBlend = 0f;
                _source.volume = 0f;
                // The point of the separate source: the muffling must not squash the whine.
                _source.bypassListenerEffects = true;
                _source.bypassReverbZones = true;
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[Daze] could not build the ring: " + ex.Message);
                BridgeConfig.DazeRingVolume.Value = 0f;
                return false;
            }
        }

        private void StopRing()
        {
            if (_source != null && _source.isPlaying) _source.Stop();
        }

        private void ReleaseFilter()
        {
            if (_filter == null) return;
            if (_weAddedFilter) Destroy(_filter);
            else
            {
                _filter.cutoffFrequency = _filterOldCutoff;
                _filter.enabled = _filterWasEnabled;
            }
            _filter = null;
            _weAddedFilter = false;
        }

        /// <summary>Puts everything back as it was: the game's audio is not ours to leave altered.</summary>
        private void Release()
        {
            ReleaseFilter();
            StopRing();
        }
    }
}
