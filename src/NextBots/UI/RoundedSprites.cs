using System.Collections.Generic;
using UnityEngine;

namespace NextBots.UI
{
    /// <summary>
    /// Nine-sliced rounded rectangles, drawn in code: a filled one for a box and a ring for its
    /// outline. One texture per corner radius and ring thickness, made once and reused. Sliced, the
    /// corners keep their shape however wide or tall the row is.
    /// </summary>
    public static class RoundedSprites
    {
        private static readonly Dictionary<long, Sprite> Cache = new Dictionary<long, Sprite>();

        /// <summary>A filled rounded rectangle with corners <paramref name="radius"/> texels round,
        /// at <paramref name="pixelsPerUnit"/>.</summary>
        public static Sprite Fill(int radius, float pixelsPerUnit) => Get(radius, 0, pixelsPerUnit);

        /// <summary>A ring <paramref name="thickness"/> texels wide whose outer edge has corners
        /// <paramref name="radius"/> texels round.</summary>
        public static Sprite Ring(int radius, int thickness, float pixelsPerUnit) =>
            Get(radius, Mathf.Max(1, thickness), pixelsPerUnit);

        private static Sprite Get(int radius, int ring, float ppu)
        {
            radius = Mathf.Clamp(radius, 1, 256);
            ring = Mathf.Min(ring, radius);
            long key = ((long)radius << 40) | ((long)ring << 24) | (long)Mathf.RoundToInt(ppu * 100f);
            Sprite s;
            if (Cache.TryGetValue(key, out s) && s != null) return s;

            int size = radius * 2 + 2;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "NextBots.Rounded" + radius + (ring > 0 ? "r" + ring : ""),
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px0 = x + 0.5f, py0 = y + 0.5f;
                    float a = Coverage(px0, py0, 0f, size, radius);
                    if (ring > 0) a -= Coverage(px0, py0, ring, size, radius - ring);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);

            float edge = radius + 1;
            s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), ppu, 0,
                              SpriteMeshType.FullRect, new Vector4(edge, edge, edge, edge));
            s.name = tex.name;
            s.hideFlags = HideFlags.HideAndDontSave;
            Cache[key] = s;
            return s;
        }

        /// <summary>How much of a pixel at (x, y) lies inside a rounded square inset by
        /// <paramref name="inset"/> from a texture <paramref name="size"/> wide.</summary>
        private static float Coverage(float x, float y, float inset, int size, float radius)
        {
            float half = size * 0.5f - inset;
            float r = Mathf.Max(0f, radius);
            float qx = Mathf.Abs(x - size * 0.5f) - (half - r);
            float qy = Mathf.Abs(y - size * 0.5f) - (half - r);
            float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
            return Mathf.Clamp01(0.5f - outside);
        }
    }
}
