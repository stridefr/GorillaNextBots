using System;
using System.Collections.Generic;
using UnityEngine;

namespace NextBotsRagdoll
{
    /// <summary>
    /// Makes a hit land as one moment.
    ///
    /// <para><b>Why it needed doing.</b> A hit sets off half a dozen things - the thud, the flashbang
    /// bang, the death cry, the red flash, the colour draining, the dust, the ringing - and they used
    /// to be triggered in whatever order the code happened to reach them. That does not make them
    /// arrive together. Sound has a delay a picture does not: the engine mixes audio into a buffer
    /// and hands it to the sound card a few buffers later, typically 20 to 90 ms, and more on a
    /// Bluetooth headset. A flash on the very frame the hit is detected therefore reaches the eye
    /// before the bang reaches the ear, and it shows: the hit feels like two events, and in a
    /// recording it looks like the audio is out of sync.</para>
    ///
    /// <para><b>The timeline.</b> Everything heard starts at once, the visuals are held back by the
    /// audio latency so they land with it, and the cry - which in a real hit follows the impact rather
    /// than coinciding with it - comes a beat after the thud:</para>
    /// <code>
    ///   t = 0                 thud + flashbang bang + the muffling starts        (heard)
    ///   t = PainDelay         the death cry
    ///   t = audio latency     red flash + colour drain + dust burst              (seen)
    /// </code>
    ///
    /// <para><b>Tunable by ear.</b> The latency is estimated from the audio settings, which is right for
    /// most setups but not for a Bluetooth headset or an unusual sound card, so
    /// <c>ExtraDelayMs</c> adds to it (or subtracts, if a recording shows the picture late).</para>
    /// </summary>
    public sealed class HitBeat : MonoBehaviour
    {
        public static HitBeat Instance { get; private set; }

        private struct Timed
        {
            public float At;
            public Action Run;
        }

        private readonly List<Timed> _due = new List<Timed>(8);

        private void Awake()
        {
            Instance = this;
            Plugin.Log.LogInfo("[Beat] estimated audio latency " + (AudioLatency() * 1000f).ToString("0") +
                               " ms | visuals are held back by " + (VisualDelay() * 1000f).ToString("0") + " ms");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ================================================================== timing

        /// <summary>
        /// How long sound takes to reach the ear once the game asks for it: the audio buffers. This is
        /// an estimate - the engine does not report what the driver and the device add on top.
        /// </summary>
        public static float AudioLatency()
        {
            try
            {
                int length, count;
                AudioSettings.GetDSPBufferSize(out length, out count);
                int rate = Mathf.Max(8000, AudioSettings.outputSampleRate);
                return Mathf.Clamp(length * Mathf.Max(1, count) / (float)rate, 0f, 0.2f);
            }
            catch { return 0.05f; }
        }

        /// <summary>How long the picture waits so that it lands with the sound.</summary>
        public static float VisualDelay()
        {
            float d = BridgeConfig.BeatSyncToAudio.Value ? AudioLatency() : 0f;
            d += BridgeConfig.BeatExtraDelayMs.Value / 1000f;
            return Mathf.Clamp(d, 0f, 0.5f);
        }

        /// <summary>Runs <paramref name="action"/> after this many seconds, or now if it is not later.</summary>
        public void After(float seconds, Action action)
        {
            if (action == null) return;
            if (seconds <= 0.001f) { Safe(action); return; }
            _due.Add(new Timed { At = Time.unscaledTime + seconds, Run = action });
        }

        private void Update()
        {
            if (_due.Count == 0) return;

            float now = Time.unscaledTime;
            for (int i = _due.Count - 1; i >= 0; i--)
            {
                if (_due[i].At > now) continue;
                var run = _due[i].Run;
                _due.RemoveAt(i);
                Safe(run);
            }
        }

        private static void Safe(Action a)
        {
            try { a(); }
            catch (Exception ex) { Plugin.Log.LogWarning("[Beat] " + ex.Message); }
        }

        // ================================================================== the beat

        /// <summary>
        /// A hit, at <paramref name="at"/>. <paramref name="mine"/> is true when it is the local
        /// player who was hit: only then does it get the vignette and the ringing, which are effects on
        /// <i>your</i> senses. Someone else's hit is still seen and heard, in the world, on the same
        /// timeline.
        /// </summary>
        public void Fire(Vector3 at, BotProfile profile, bool killed, bool mine, float dazePower,
                         bool heard = true, bool seen = true)
        {
            var sounds = ImpactSounds.Instance;

            // ---- heard: now
            if (sounds != null && heard)
            {
                sounds.OnBotHit(at, profile, dust: false);
                if (KnockdownBridge.WantsDeathSound(killed))
                    After(BridgeConfig.BeatPainDelayMs.Value / 1000f, () => sounds.PlayDeath(at));
            }
            if (mine && Daze.Instance != null) Daze.Instance.Hit(dazePower);   // the bang and the muffling

            // ---- seen: when the sound reaches the ear
            float scale = profile != null ? profile.Scale : 1f;
            After(VisualDelay(), () =>
            {
                if (mine && DeathVignette.Instance != null) DeathVignette.Instance.Begin();
                if (seen && Dust.Instance != null)
                    Dust.Instance.Puff(at, BridgeConfig.HardSpeed.Value * 1.3f * scale, 2.2f);
            });
        }
    }
}
