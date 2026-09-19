using NextBots.Config;
using UnityEngine;

namespace NextBots.Runtime
{
    /// <summary>
    /// The chase loop.
    ///
    /// A nextbot sprinting at you in silence is a moving picture; the sound is most of why
    /// the original ones are frightening. The loop plays positionally from the bot, gets
    /// louder as it closes, and - the part that does the real work - goes muffled and quiet
    /// when there is something solid between you. That means you can hear it circling a wall
    /// and hear the moment it comes round the corner.
    ///
    /// Occlusion is free: the brain already raycasts the sightline every tick for the ambush
    /// logic, so we reuse that answer rather than casting again.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class BotAudio : MonoBehaviour
    {
        private AudioSource _source;
        private AudioLowPassFilter _lowPass;
        private NextBotSettings _cfg;

        private float _occlusion;          // 0 = clear line, 1 = fully blocked
        private float _basePitch = 1f;

        public bool HasClip => _source != null && _source.clip != null;

        public void Setup(AudioClip clip, NextBotSettings cfg)
        {
            _cfg = cfg ?? NextBotSettings.Active;

            _source = GetComponent<AudioSource>();
            _source.clip = clip;
            _source.loop = true;
            _source.playOnAwake = false;

            // Fully positional. 2D audio would defeat the entire point.
            _source.spatialBlend = 1f;
            _source.dopplerLevel = 0.15f;      // a little, not enough to sound silly
            _source.rolloffMode = AudioRolloffMode.Linear;
            _source.minDistance = 3f;
            _source.maxDistance = Mathf.Max(8f, _cfg.SoundMaxDistance);

            // Each bot slightly detuned, so three of them do not phase into one tone.
            _basePitch = Random.Range(0.94f, 1.06f);
            _source.pitch = _basePitch;

            _lowPass = gameObject.GetComponent<AudioLowPassFilter>();
            if (_lowPass == null) _lowPass = gameObject.AddComponent<AudioLowPassFilter>();
            _lowPass.cutoffFrequency = 22000f;

            if (clip != null && _cfg.SoundEnabled) _source.Play();
        }

        /// <summary>Called from the brain tick, which already knows whether the line is clear.</summary>
        public void Tick(bool blocked, float distance, float dt)
        {
            if (_source == null || _source.clip == null) return;

            if (!_cfg.SoundEnabled)
            {
                if (_source.isPlaying) _source.Pause();
                return;
            }
            if (!_source.isPlaying) _source.UnPause();

            _source.maxDistance = Mathf.Max(8f, _cfg.SoundMaxDistance);

            // Ease rather than snap, or stepping past a doorframe clicks.
            var target = blocked ? 1f : 0f;
            _occlusion = Mathf.MoveTowards(_occlusion, target, dt * 4f);

            // Through a wall: quieter and duller. Both matter - volume alone still sounds
            // like it is in the room with you.
            var muffle = Mathf.Lerp(1f, Mathf.Clamp01(1f - _cfg.OcclusionMuffle), _occlusion);
            _source.volume = _cfg.SoundVolume * muffle;
            _lowPass.cutoffFrequency = Mathf.Lerp(22000f, 750f, _occlusion);

            // Wind up slightly as it closes, so a bot bearing down on you is audibly worse
            // than one wandering at the far end of the map.
            var closeness = 1f - Mathf.Clamp01(distance / Mathf.Max(1f, _cfg.SoundMaxDistance));
            _source.pitch = _basePitch * Mathf.Lerp(1f, _cfg.SoundCloseUpPitch, closeness);
        }

        public void Stop()
        {
            if (_source != null && _source.isPlaying) _source.Stop();
        }
    }
}
