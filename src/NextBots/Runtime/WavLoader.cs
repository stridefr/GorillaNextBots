using System;
using System.IO;
using UnityEngine;

namespace NextBots.Runtime
{
    /// <summary>
    /// Loads a WAV file from disk into an AudioClip, by hand.
    ///
    /// Unity's usual route for runtime audio is UnityWebRequestMultimedia, but this install
    /// ships no UnityWebRequest modules at all, so OGG and MP3 are simply not decodable here.
    /// WAV is, because it is uncompressed - parse the header, convert the samples to float,
    /// hand them to AudioClip.Create. No dependencies, no decoder to get wrong.
    ///
    /// Handles the formats a converter will realistically emit: 8/16/24/32-bit PCM and
    /// 32-bit float, mono or stereo, any sample rate.
    /// </summary>
    public static class WavLoader
    {
        public static AudioClip Load(string path, string clipName)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                return FromBytes(bytes, clipName);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Audio] could not load '" + path + "': " + ex.Message);
                return null;
            }
        }

        public static AudioClip FromBytes(byte[] b, string clipName)
        {
            if (b == null || b.Length < 44) throw new Exception("file too short to be a WAV");

            if (b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F')
                throw new Exception("not a RIFF file");
            if (b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
                throw new Exception("not a WAVE file");

            int channels = 0, sampleRate = 0, bits = 0, format = 0;
            var dataOffset = -1;
            var dataLength = 0;

            // Walk the chunk list rather than assuming a 44-byte header: real files carry
            // LIST/fact/junk chunks and the data does not start where you expect.
            var pos = 12;
            while (pos + 8 <= b.Length)
            {
                var id = new string(new[] { (char)b[pos], (char)b[pos + 1], (char)b[pos + 2], (char)b[pos + 3] });
                var size = BitConverter.ToInt32(b, pos + 4);
                var body = pos + 8;
                if (size < 0 || body + size > b.Length) size = b.Length - body;

                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(b, body);
                    channels = BitConverter.ToInt16(b, body + 2);
                    sampleRate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToInt16(b, body + 14);
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    dataLength = size;
                }

                pos = body + size;
                if ((size & 1) != 0) pos++;      // chunks are word-aligned
            }

            if (dataOffset < 0) throw new Exception("no data chunk");
            if (channels <= 0 || sampleRate <= 0) throw new Exception("no usable fmt chunk");

            // 1 = PCM, 3 = IEEE float, 0xFFFE = extensible (still PCM payload for our purposes)
            if (format != 1 && format != 3 && format != unchecked((short)0xFFFE))
                throw new Exception("compressed WAV (format " + format + ") is not supported - export as PCM");

            var bytesPerSample = Mathf.Max(1, bits / 8);
            var sampleCount = dataLength / bytesPerSample;
            var samples = new float[sampleCount];

            if (format == 3 && bits == 32)
            {
                for (int i = 0; i < sampleCount; i++)
                    samples[i] = BitConverter.ToSingle(b, dataOffset + i * 4);
            }
            else
            {
                switch (bits)
                {
                    case 8:
                        // 8-bit WAV is unsigned, centred on 128.
                        for (int i = 0; i < sampleCount; i++)
                            samples[i] = (b[dataOffset + i] - 128) / 128f;
                        break;
                    case 16:
                        for (int i = 0; i < sampleCount; i++)
                            samples[i] = BitConverter.ToInt16(b, dataOffset + i * 2) / 32768f;
                        break;
                    case 24:
                        for (int i = 0; i < sampleCount; i++)
                        {
                            var o = dataOffset + i * 3;
                            int v = (b[o] | (b[o + 1] << 8) | (sbyte)b[o + 2] << 16);
                            samples[i] = v / 8388608f;
                        }
                        break;
                    case 32:
                        for (int i = 0; i < sampleCount; i++)
                            samples[i] = BitConverter.ToInt32(b, dataOffset + i * 4) / 2147483648f;
                        break;
                    default:
                        throw new Exception(bits + "-bit WAV is not supported");
                }
            }

            var frames = sampleCount / channels;
            if (frames <= 0) throw new Exception("no samples");

            var clip = AudioClip.Create(clipName, frames, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
