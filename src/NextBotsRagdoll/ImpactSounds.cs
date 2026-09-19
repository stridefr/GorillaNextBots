using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using NextBots.Runtime;
using UnityEngine;

namespace NextBotsRagdoll
{
    /// <summary>
    /// Source-engine-style physics sounds for the ragdoll: a soft set, a hard set and a bone-crunch
    /// set chosen by impact speed, plus a hit set for the moment a bot slams into you.
    ///
    /// <para><b>The sounds are not shipped.</b> They are Valve's; each player points this at their
    /// own copy (see the README.txt written into the sounds folder). Any WAV works and file names
    /// do not matter - every WAV in a set's folder is one of its variations, the way a Source
    /// soundscript lists <c>body_medium_impact_hard1..6</c>.</para>
    ///
    /// <para>Loading goes through NextBots' own <c>WavLoader</c>, because this install ships no
    /// UnityWebRequest audio modules at all - WAV is the only format that can be read at runtime,
    /// and that loader is already proven here.</para>
    ///
    /// <para>Played the way Source plays them: a random variation that is never the same one
    /// twice running, pitch jittered 95-105%, volume from impact speed, and a global limit so a
    /// whole body landing at once is one thud rather than eight.</para>
    /// </summary>
    public sealed class ImpactSounds : MonoBehaviour
    {
        public static ImpactSounds Instance { get; private set; }

        public const string Soft = "impact_soft";
        public const string Hard = "impact_hard";
        public const string Break = "break";
        public const string Hit = "hit";

        public static string Directory =>
            Path.Combine(Paths.PluginPath, Path.Combine("NextBotsRagdoll", "sounds"));

        private const int Voices = 8;

        /// <summary>Source's own soundscripts jitter physics impacts by about this much.</summary>
        private const float PitchJitter = 0.05f;

        /// <summary>Shortest gap between any two impact sounds. A body hitting the floor lands on
        /// every limb within a few milliseconds; that should be one sound.</summary>
        private const float MinGap = 0.06f;

        private readonly Dictionary<string, List<AudioClip>> _sets =
            new Dictionary<string, List<AudioClip>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _lastPick =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private AudioSource[] _voices;
        private int _nextVoice;
        private float _lastImpactAt = -1f;

        private void Awake()
        {
            Instance = this;

            _voices = new AudioSource[Voices];
            for (int i = 0; i < Voices; i++)
            {
                var go = new GameObject("voice" + i);
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.minDistance = 1.5f;
                src.maxDistance = 40f;
                src.dopplerLevel = 0f;
                _voices[i] = src;
            }

            Load();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>Reads every set folder. Creates the folders and a README the first time.</summary>
        public void Load()
        {
            _sets.Clear();
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                foreach (var set in new[] { Soft, Hard, Break, Hit })
                    System.IO.Directory.CreateDirectory(Path.Combine(Directory, set));

                var readme = Path.Combine(Directory, "README.txt");
                if (!File.Exists(readme)) File.WriteAllText(readme, Readme);

                // Every folder is a set, not just the four built-in ones, so a bot profile can
                // name its own ("hitSound": "hit_heavy").
                foreach (var dir in System.IO.Directory.GetDirectories(Directory))
                {
                    var clips = new List<AudioClip>();
                    var files = System.IO.Directory.GetFiles(dir, "*.wav");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (var file in files)
                    {
                        var clip = WavLoader.Load(file, Path.GetFileNameWithoutExtension(file));
                        if (clip != null) clips.Add(clip);
                    }
                    _sets[Path.GetFileName(dir)] = clips;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Sound] could not read " + Directory + ": " + ex.Message);
            }

            var summary = new System.Text.StringBuilder("[Sound] ");
            foreach (var kv in _sets) summary.Append(kv.Key).Append('=').Append(kv.Value.Count).Append(' ');
            if (Count(Soft) + Count(Hard) + Count(Break) + Count(Hit) == 0)
                summary.Append("| no WAVs yet - see ").Append(Path.Combine(Directory, "README.txt"));
            Plugin.Log.LogInfo(summary.ToString());
        }

        private int Count(string set)
        {
            List<AudioClip> clips;
            return _sets.TryGetValue(set, out clips) ? clips.Count : 0;
        }

        /// <summary>
        /// A limb or the torso hit something at <paramref name="speed"/> m/s. Picks the set and
        /// volume the way Source does for a physics prop: nothing below the soft threshold, soft
        /// sounds scaling up to the hard threshold, hard ones above it, and the bone-crunch past
        /// the break threshold.
        /// </summary>
        public void OnImpact(Vector3 position, float speed)
        {
            if (!BridgeConfig.SoundsEnabled.Value) return;
            if (Time.time - _lastImpactAt < MinGap) return;

            float soft = BridgeConfig.SoftSpeed.Value;
            float hard = Mathf.Max(soft + 0.1f, BridgeConfig.HardSpeed.Value);
            float brk = Mathf.Max(hard + 0.1f, BridgeConfig.BreakSpeed.Value);
            if (speed < soft) return;

            string set;
            float volume;
            if (speed >= brk)
            {
                set = Break;
                volume = 1f;
            }
            else if (speed >= hard)
            {
                set = Hard;
                volume = Mathf.Lerp(0.7f, 1f, Mathf.InverseLerp(hard, brk, speed));
            }
            else
            {
                set = Soft;
                volume = Mathf.Lerp(0.25f, 0.7f, Mathf.InverseLerp(soft, hard, speed));
            }

            if (Play(set, position, volume, 1f)) _lastImpactAt = Time.time;
        }

        /// <summary>The bot connecting. Deeper and harder for heavier bots, via the profile.</summary>
        public void OnBotHit(Vector3 position, BotProfile profile)
        {
            if (!BridgeConfig.SoundsEnabled.Value) return;

            string set = profile != null && !string.IsNullOrEmpty(profile.hitSound) ? profile.hitSound : Hit;
            float pitch = profile != null ? profile.Pitch : 1f;
            float volume = profile != null ? Mathf.Clamp(0.75f + 0.25f * profile.Scale, 0.5f, 1f) : 1f;
            Play(set, position, volume, pitch);

            // The hit is itself an impact; stop the body's first contact doubling it.
            _lastImpactAt = Time.time;
        }

        /// <summary>
        /// One variation from a set. An empty set falls back to its nearest relative - break to
        /// hard, hit to break to hard, hard to soft - so a half-filled folder still makes noise
        /// in the right places.
        /// </summary>
        private bool Play(string set, Vector3 position, float volume, float pitch)
        {
            var clips = Resolve(set);
            if (clips == null) return false;

            int last;
            _lastPick.TryGetValue(set, out last);
            int pick = UnityEngine.Random.Range(0, clips.Count);
            if (clips.Count > 1 && pick == last) pick = (pick + 1) % clips.Count;
            _lastPick[set] = pick;

            var voice = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % _voices.Length;

            voice.transform.position = position;
            voice.clip = clips[pick];
            voice.volume = Mathf.Clamp01(volume * BridgeConfig.SoundVolume.Value);
            voice.pitch = pitch * UnityEngine.Random.Range(1f - PitchJitter, 1f + PitchJitter);
            voice.Play();
            return true;
        }

        private List<AudioClip> Resolve(string set)
        {
            List<AudioClip> clips;
            while (!string.IsNullOrEmpty(set))
            {
                if (_sets.TryGetValue(set, out clips) && clips.Count > 0) return clips;
                set = Fallback(set);
            }
            return null;
        }

        private static string Fallback(string set)
        {
            if (string.Equals(set, Soft, StringComparison.OrdinalIgnoreCase)) return null;
            if (string.Equals(set, Hard, StringComparison.OrdinalIgnoreCase)) return Soft;
            if (string.Equals(set, Break, StringComparison.OrdinalIgnoreCase)) return Hard;
            if (string.Equals(set, Hit, StringComparison.OrdinalIgnoreCase)) return Break;
            return Hit;   // a profile's own set, e.g. hit_heavy
        }

        private const string Readme =
@"Impact sounds for NextBotsRagdoll
=================================

Each folder here is a sound set. Drop WAV files into it; every WAV is one variation and
one is picked at random each time. File names do not matter.

  impact_soft/   your ragdoll bumps something at low speed
  impact_hard/   a proper landing
  break/         a very hard landing - the bone crunch
  hit/           the moment a bot slams into you

Any other folder you make is a set too, and a bot profile can use it by name
(""hitSound"": ""hit_heavy"" in ../bots/NAME.json).

Using the Garry's Mod / Half-Life 2 sounds
-------------------------------------------
Open garrysmod/sourceengine/hl2_sound_misc_dir.vpk from your own install with
VPKEdit or GCFScape and export these (names from HL2's physics soundscripts):

  impact_soft/   sound/physics/body/body_medium_impact_soft1.wav ... soft7.wav
  impact_hard/   sound/physics/body/body_medium_impact_hard1.wav ... hard6.wav
  break/         sound/physics/body/body_medium_break2.wav ... break4.wav
  hit/           sound/physics/flesh/flesh_impact_hard1.wav ... hard6.wav

If a file is refused in the BepInEx log, it is compressed (some Source WAVs are ADPCM).
Open it in Audacity and export as WAV, signed 16-bit PCM.

These are Valve's sounds. Fine for your own game - do not ship them inside a mod you
publish; tell people to copy them from their own install instead.
";
    }

    /// <summary>
    /// Sits on each ragdoll body part and reports how hard it hits things. Collision callbacks
    /// rather than the velocity-change test the active ragdoll uses: here the contact point and
    /// the relative speed are exactly what is wanted, and a sound needs a position.
    /// </summary>
    internal sealed class ImpactListener : MonoBehaviour
    {
        /// <summary>The puppet is built wherever the body was standing, often already touching
        /// the floor. That first contact is not an impact.</summary>
        private const float ArmDelay = 0.15f;

        /// <summary>One limb rattling against the floor should not machine-gun.</summary>
        private const float PerPartGap = 0.12f;

        private float _armedAt;
        private float _lastAt = -1f;

        private void Awake() => _armedAt = Time.time + ArmDelay;

        private void OnCollisionEnter(Collision c)
        {
            if (Time.time < _armedAt || Time.time - _lastAt < PerPartGap) return;

            var sounds = ImpactSounds.Instance;
            if (sounds == null) return;

            float speed = c.relativeVelocity.magnitude;
            if (speed < BridgeConfig.SoftSpeed.Value) return;

            _lastAt = Time.time;
            sounds.OnImpact(c.contactCount > 0 ? c.GetContact(0).point : transform.position, speed);
        }
    }
}
