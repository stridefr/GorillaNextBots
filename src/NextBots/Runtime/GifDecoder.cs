using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NextBots.Runtime
{
    /// <summary>
    /// A GIF decoder, written from scratch because Unity cannot read GIF at all - not
    /// through Texture2D.LoadImage, and not through UnityWebRequest either (which this
    /// install ships no modules for anyway).
    ///
    /// Animated nextbots are the whole aesthetic, so this decodes every frame rather than
    /// just the first: header, colour tables, graphic control extensions for per-frame
    /// delays and transparency, and the LZW-compressed image data.
    ///
    /// Handles the parts real GIFs use: interlacing, local colour tables, transparency, and
    /// the disposal methods that matter (keep / restore-to-background / restore-to-previous).
    /// Frames are composited onto a persistent canvas, because most GIFs only store the
    /// rectangle that changed rather than a full image each time.
    /// </summary>
    public static class GifDecoder
    {
        public class Frame
        {
            public Texture2D Texture;
            public float Delay;          // seconds
        }

        public class Result
        {
            public List<Frame> Frames = new List<Frame>();
            public int Width;
            public int Height;
        }

        public static Result Load(string path)
        {
            try { return Decode(File.ReadAllBytes(path)); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[GIF] could not decode '" + Path.GetFileName(path) + "': " + ex.Message);
                return null;
            }
        }

        public static Result Decode(byte[] b)
        {
            var p = 0;

            if (b.Length < 13) throw new Exception("too short");
            if (b[0] != 'G' || b[1] != 'I' || b[2] != 'F') throw new Exception("not a GIF");
            p = 6;

            var width = b[p] | (b[p + 1] << 8);
            var height = b[p + 2] | (b[p + 3] << 8);
            var packed = b[p + 4];
            var bgIndex = b[p + 5];
            p += 7;

            Color32[] globalTable = null;
            if ((packed & 0x80) != 0)
            {
                var size = 2 << (packed & 0x07);
                globalTable = ReadPalette(b, ref p, size);
            }

            var result = new Result { Width = width, Height = height };

            // Persistent canvas: frames are usually partial rectangles composited onto it.
            var canvas = new Color32[width * height];
            var clear = new Color32(0, 0, 0, 0);
            for (int i = 0; i < canvas.Length; i++) canvas[i] = clear;

            var delay = 0.1f;
            var transparentIndex = -1;
            var disposal = 0;

            while (p < b.Length)
            {
                var block = b[p++];

                if (block == 0x3B) break;                    // trailer

                if (block == 0x21)                           // extension
                {
                    var label = b[p++];
                    if (label == 0xF9)                       // graphic control
                    {
                        var size = b[p++];
                        var flags = b[p];
                        disposal = (flags >> 2) & 0x07;
                        var d = b[p + 1] | (b[p + 2] << 8);
                        delay = d <= 1 ? 0.1f : d / 100f;    // 0/1 hundredths means "as fast as possible"
                        transparentIndex = (flags & 0x01) != 0 ? b[p + 3] : -1;
                        p += size;
                        p = SkipBlocks(b, p);
                    }
                    else
                    {
                        p = SkipBlocks(b, p);
                    }
                    continue;
                }

                if (block != 0x2C) continue;                 // not an image descriptor

                var fx = b[p] | (b[p + 1] << 8);
                var fy = b[p + 2] | (b[p + 3] << 8);
                var fw = b[p + 4] | (b[p + 5] << 8);
                var fh = b[p + 6] | (b[p + 7] << 8);
                var fpacked = b[p + 8];
                p += 9;

                var table = globalTable;
                if ((fpacked & 0x80) != 0)
                {
                    var size = 2 << (fpacked & 0x07);
                    table = ReadPalette(b, ref p, size);
                }
                if (table == null) throw new Exception("no colour table");

                var interlaced = (fpacked & 0x40) != 0;

                // Snapshot for restore-to-previous.
                Color32[] previous = null;
                if (disposal == 3)
                {
                    previous = new Color32[canvas.Length];
                    Array.Copy(canvas, previous, canvas.Length);
                }

                var indices = DecodeLzw(b, ref p, fw * fh);

                // Composite this frame's rectangle onto the canvas.
                for (int row = 0; row < fh; row++)
                {
                    var srcRow = interlaced ? InterlacedRow(row, fh) : row;
                    for (int col = 0; col < fw; col++)
                    {
                        var idx = indices[srcRow * fw + col];
                        if (idx == transparentIndex) continue;
                        if (idx >= table.Length) continue;

                        var x = fx + col;
                        var y = fy + row;
                        if (x < 0 || x >= width || y < 0 || y >= height) continue;

                        // GIF rows run top-down; Unity textures run bottom-up.
                        canvas[(height - 1 - y) * width + x] = table[idx];
                    }
                }

                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                tex.SetPixels32(canvas);
                tex.Apply(false, false);

                result.Frames.Add(new Frame { Texture = tex, Delay = delay });

                // Apply disposal for the *next* frame.
                if (disposal == 2)
                {
                    var bg = (globalTable != null && bgIndex < globalTable.Length)
                        ? new Color32(0, 0, 0, 0) : clear;
                    for (int row = 0; row < fh; row++)
                        for (int col = 0; col < fw; col++)
                        {
                            var x = fx + col;
                            var y = fy + row;
                            if (x < 0 || x >= width || y < 0 || y >= height) continue;
                            canvas[(height - 1 - y) * width + x] = bg;
                        }
                }
                else if (disposal == 3 && previous != null)
                {
                    Array.Copy(previous, canvas, canvas.Length);
                }

                transparentIndex = -1;
            }

            if (result.Frames.Count == 0) throw new Exception("no frames");
            return result;
        }

        // ---------------------------------------------------------------- helpers

        private static Color32[] ReadPalette(byte[] b, ref int p, int size)
        {
            var table = new Color32[size];
            for (int i = 0; i < size; i++)
            {
                table[i] = new Color32(b[p], b[p + 1], b[p + 2], 255);
                p += 3;
            }
            return table;
        }

        private static int SkipBlocks(byte[] b, int p)
        {
            while (p < b.Length)
            {
                var len = b[p++];
                if (len == 0) break;
                p += len;
            }
            return p;
        }

        /// <summary>GIF interlacing stores rows in four passes; map a stored row to its real one.</summary>
        private static int InterlacedRow(int row, int height)
        {
            var pass1 = (height + 7) / 8;
            var pass2 = pass1 + (height + 3) / 8;
            var pass3 = pass2 + (height + 1) / 4;

            if (row < pass1) return row * 8;
            if (row < pass2) return (row - pass1) * 8 + 4;
            if (row < pass3) return (row - pass2) * 4 + 2;
            return (row - pass3) * 2 + 1;
        }

        /// <summary>
        /// LZW as GIF uses it: variable code width starting at minimum+1, a clear code and an
        /// end code above the palette, and the dictionary reset whenever clear appears. Data
        /// arrives in sub-blocks which have to be stitched into one bit stream.
        /// </summary>
        private static byte[] DecodeLzw(byte[] b, ref int p, int pixelCount)
        {
            var minCodeSize = b[p++];

            // Stitch the sub-blocks.
            var data = new List<byte>(1024);
            while (p < b.Length)
            {
                var len = b[p++];
                if (len == 0) break;
                for (int i = 0; i < len && p < b.Length; i++) data.Add(b[p++]);
            }

            var clearCode = 1 << minCodeSize;
            var endCode = clearCode + 1;
            var codeSize = minCodeSize + 1;
            var nextCode = endCode + 1;

            var dictPrefix = new int[4096];
            var dictSuffix = new int[4096];
            for (int i = 0; i < clearCode; i++) { dictPrefix[i] = -1; dictSuffix[i] = i; }

            var output = new byte[pixelCount];
            var outPos = 0;

            var bitPos = 0;
            var previousCode = -1;
            var stack = new int[4096];

            while (outPos < pixelCount)
            {
                // Pull `codeSize` bits, little-endian across bytes.
                if ((bitPos + codeSize) > data.Count * 8) break;

                var code = 0;
                for (int i = 0; i < codeSize; i++)
                {
                    var bit = (data[(bitPos + i) >> 3] >> ((bitPos + i) & 7)) & 1;
                    code |= bit << i;
                }
                bitPos += codeSize;

                if (code == clearCode)
                {
                    codeSize = minCodeSize + 1;
                    nextCode = endCode + 1;
                    previousCode = -1;
                    continue;
                }
                if (code == endCode) break;

                int current;
                var sp = 0;

                if (code < nextCode && (code < clearCode || dictPrefix[code] != -1 || code < clearCode))
                {
                    current = code;
                }
                else
                {
                    // KwKwK case: the code is not in the dictionary yet.
                    if (previousCode < 0) break;
                    stack[sp++] = FirstByte(dictPrefix, dictSuffix, previousCode, clearCode);
                    current = previousCode;
                }

                // Walk the chain back to a root, pushing suffixes.
                var guard = 0;
                while (current >= clearCode && guard++ < 4096)
                {
                    stack[sp++] = dictSuffix[current];
                    current = dictPrefix[current];
                }
                stack[sp++] = dictSuffix[current < 0 ? 0 : current];

                // Emit reversed.
                while (sp > 0 && outPos < pixelCount) output[outPos++] = (byte)stack[--sp];

                if (previousCode >= 0 && nextCode < 4096)
                {
                    dictPrefix[nextCode] = previousCode;
                    dictSuffix[nextCode] = FirstByte(dictPrefix, dictSuffix, code < nextCode ? code : previousCode, clearCode);
                    nextCode++;

                    if (nextCode >= (1 << codeSize) && codeSize < 12) codeSize++;
                }

                previousCode = code;
            }

            return output;
        }

        private static int FirstByte(int[] prefix, int[] suffix, int code, int clearCode)
        {
            var guard = 0;
            while (code >= clearCode && guard++ < 4096) code = prefix[code];
            return suffix[code < 0 ? 0 : code];
        }
    }
}
