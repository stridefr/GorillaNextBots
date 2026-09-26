using System.Collections.Generic;
using NextBots.Runtime;
using UnityEngine;

namespace NextBots.UI
{
    /// <summary>
    /// Hearts: a row over every other player's head, and one over your own wrist, showing the
    /// lives the host says each has. Full hearts for lives left, empty ones for lives lost; a
    /// ghost's row fades. Everyone draws everyone's, from the same networked table.
    /// </summary>
    public sealed class LivesDisplay : MonoBehaviour
    {
        private const float HeadSize = 0.07f, WristSize = 0.03f;
        private const float HeadLift = 0.38f, WristLift = 0.07f;

        private sealed class Row
        {
            public Transform Root;
            public readonly List<Material> Hearts = new List<Material>(10);
            public int Shown = -1, Max = -1;
            public bool Seen;
        }

        private readonly Dictionary<int, Row> _rows = new Dictionary<int, Row>();
        private readonly List<int> _gone = new List<int>(8);
        private static Texture2D _full, _empty;

        private void LateUpdate()
        {
            var rigs = VRRigCache.ActiveRigs;
            var viewer = UiResources.Viewer;
            if (!Lives.Active || rigs == null || viewer == null)
            {
                Clear();
                return;
            }

            foreach (var r in _rows.Values) r.Seen = false;

            for (int i = 0; i < rigs.Count; i++)
            {
                var rig = rigs[i];
                if (rig == null || !rig.isActiveAndEnabled) continue;
                int actor = WorldView.ActorOf(rig);
                if (actor < 0) continue;

                bool mine = rig.isLocal || rig.isOfflineVRRig;
                Transform anchor = mine ? rig.leftHandTransform : (rig.headConstraint != null ? rig.headConstraint : rig.transform);
                if (anchor == null) continue;

                Row row;
                if (!_rows.TryGetValue(actor, out row)) { row = new Row(); _rows[actor] = row; }
                row.Seen = true;

                float size = mine ? WristSize : HeadSize;
                if (row.Root == null || row.Max != Lives.Max) Build(row, actor, size);

                int left = Lives.Of(actor);
                if (left != row.Shown) Paint(row, left, Lives.IsGhost(actor));

                row.Root.position = anchor.position + Vector3.up * (mine ? WristLift : HeadLift);
                var to = row.Root.position - viewer.position;
                if (to.sqrMagnitude > 0.0001f) row.Root.rotation = Quaternion.LookRotation(to.normalized, Vector3.up);
            }

            _gone.Clear();
            foreach (var kv in _rows) if (!kv.Value.Seen) _gone.Add(kv.Key);
            foreach (var a in _gone) { Destroy(_rows[a]); _rows.Remove(a); }
        }

        private void Build(Row row, int actor, float size)
        {
            Destroy(row);
            var root = new GameObject("NextBots.Lives." + actor).transform;
            root.SetParent(transform, false);
            row.Root = root;
            row.Max = Lives.Max;
            row.Shown = -1;

            float gap = size * 0.18f;
            float width = row.Max * size + (row.Max - 1) * gap;
            for (int i = 0; i < row.Max; i++)
            {
                var go = new GameObject("heart" + i);
                go.transform.SetParent(root, false);
                go.transform.localPosition = new Vector3(-width * 0.5f + size * 0.5f + i * (size + gap), 0f, 0f);
                go.transform.localScale = new Vector3(size, size, 1f);
                go.AddComponent<MeshFilter>().sharedMesh = UiResources.Quad;
                var mr = go.AddComponent<MeshRenderer>();
                var mat = UiResources.NewSeeThroughMaterial(Heart(true), 3050);
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                row.Hearts.Add(mat);
            }
        }

        private static void Paint(Row row, int left, bool ghost)
        {
            row.Shown = left;
            for (int i = 0; i < row.Hearts.Count; i++)
            {
                var m = row.Hearts[i];
                bool full = i < left;
                m.mainTexture = Heart(full);
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", m.mainTexture);
                UiResources.TrySetColor(m, new Color(1f, 1f, 1f, ghost ? 0.4f : 1f));
            }
        }

        private static void Destroy(Row row)
        {
            foreach (var m in row.Hearts) if (m != null) Object.Destroy(m);
            row.Hearts.Clear();
            if (row.Root != null) Object.Destroy(row.Root.gameObject);
            row.Root = null;
        }

        private void Clear()
        {
            if (_rows.Count == 0) return;
            foreach (var r in _rows.Values) Destroy(r);
            _rows.Clear();
        }

        /// <summary>A heart drawn in code: red and solid for a life left, a pale outline for one lost.</summary>
        private static Texture2D Heart(bool full)
        {
            var cached = full ? _full : _empty;
            if (cached != null) return cached;

            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = full ? "NextBots.HeartFull" : "NextBots.HeartEmpty",
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Four samples a pixel for a smooth edge.
                    float inside = 0f, inner = 0f;
                    for (int s = 0; s < 4; s++)
                    {
                        float fx = (x + 0.25f + 0.5f * (s & 1)) / size * 2.6f - 1.3f;
                        float fy = (y + 0.25f + 0.5f * (s >> 1)) / size * 2.6f - 1.25f;
                        if (InHeart(fx, fy, 1f)) inside += 0.25f;
                        if (InHeart(fx, fy, 0.72f)) inner += 0.25f;
                    }
                    Color c;
                    if (full)
                    {
                        // A darker rim round a bright red heart.
                        var rim = new Color(0.45f, 0.02f, 0.06f);
                        var fill = new Color(0.93f, 0.16f, 0.24f);
                        c = Color.Lerp(rim, fill, inner);
                        c.a = inside;
                    }
                    else
                    {
                        // Outline only, with a faint dark fill so it reads against a bright sky.
                        c = Color.Lerp(new Color(1f, 1f, 1f, inside * 0.9f), new Color(0f, 0f, 0f, 0.35f * inside), inner);
                    }
                    px[y * size + x] = c;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            if (full) _full = tex; else _empty = tex;
            return tex;
        }

        private static bool InHeart(float x, float y, float scale)
        {
            x /= scale; y /= scale;
            float a = x * x + y * y - 1f;
            return a * a * a - x * x * y * y * y <= 0f;
        }
    }
}
