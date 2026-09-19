using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GorillaRagdoll.Cameras
{
    /// <summary>
    /// Makes scenery between the camera and the body see-through, so the gorilla is never
    /// hidden even when the camera has nowhere to back off to.
    ///
    /// <para>Pulling the camera in solves occlusion, but in a tight room it solves it by
    /// putting the lens against the gorilla's back, which is its own kind of unusable. So the
    /// two work together: the camera still refuses to sit inside a wall, and anything left
    /// between it and the body is faded instead.</para>
    ///
    /// <para><b>Shared materials are the hazard here.</b> GT's world geometry reuses the same
    /// material asset across the whole map, so editing it would turn every wall using it
    /// transparent. Everything below therefore works on per-renderer <i>instances</i>
    /// (<c>renderer.materials</c>), leaving the shared assets untouched; the originals are put
    /// back and the instances destroyed the moment a renderer stops blocking the view.</para>
    ///
    /// <para>One honest caveat: a material is not per-camera, so a wall faded for the monitor
    /// is faded in the headset too for as long as it blocks. It is restored on get-up, and it
    /// only ever affects surfaces directly between the camera and your own body.</para>
    /// </summary>
    public sealed class OccluderFade
    {
        private sealed class Faded
        {
            public Renderer Renderer;
            public Material[] Original;
            public Material[] Instances;
            public int LastSeen;
        }

        private readonly Dictionary<Renderer, Faded> _faded = new Dictionary<Renderer, Faded>(8);
        private readonly List<Renderer> _stale = new List<Renderer>(8);

        /// <summary>Renderers belonging to the player; never fade the thing we are looking at.</summary>
        private Transform _exclude;

        public int Count => _faded.Count;

        public void SetExclusion(Transform rigRoot) => _exclude = rigRoot;

        /// <summary>
        /// Fades whatever lies between <paramref name="from"/> (the body) and
        /// <paramref name="to"/> (the camera), and restores anything that has moved out of the
        /// way since last frame.
        /// </summary>
        public void Update(Vector3 from, Vector3 to, float radius, int mask, float alpha)
        {
            Vector3 delta = to - from;
            float dist = delta.magnitude;
            if (dist < 0.05f) { RestoreAll(); return; }

            var hits = Physics.SphereCastAll(from, radius, delta / dist, dist, mask,
                                             QueryTriggerInteraction.Ignore);
            foreach (var hit in hits)
            {
                var r = Resolve(hit.collider);
                if (r == null) continue;
                if (_exclude != null && r.transform.IsChildOf(_exclude)) continue;

                Fade(r, alpha);
            }

            // Anything not hit this frame is no longer in the way.
            _stale.Clear();
            foreach (var kv in _faded)
                if (kv.Value.LastSeen != Time.frameCount) _stale.Add(kv.Key);

            foreach (var r in _stale) Restore(r);
        }

        private static Renderer Resolve(Collider c)
        {
            if (c == null) return null;
            var r = c.GetComponent<Renderer>();
            return r != null ? r : c.GetComponentInParent<Renderer>();
        }

        private void Fade(Renderer r, float alpha)
        {
            Faded entry;
            if (_faded.TryGetValue(r, out entry))
            {
                entry.LastSeen = Time.frameCount;
                return;
            }

            var original = r.sharedMaterials;
            if (original == null || original.Length == 0) return;

            // renderer.materials hands back per-renderer copies, so nothing we do here can
            // reach the shared asset every other wall on the map is using.
            var instances = r.materials;
            foreach (var m in instances) MakeTransparent(m, alpha);

            _faded[r] = new Faded
            {
                Renderer = r,
                Original = original,
                Instances = instances,
                LastSeen = Time.frameCount
            };
        }

        /// <summary>
        /// Converts an opaque material instance to alpha-blended. Every write is guarded by
        /// <c>HasProperty</c>, because GT uses custom shaders alongside URP's and a missing
        /// property should mean "this one will not fade", never an exception.
        /// </summary>
        private static void MakeTransparent(Material m, float alpha)
        {
            if (m == null) return;

            m.SetOverrideTag("RenderType", "Transparent");
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0f);

            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.renderQueue = (int)RenderQueue.Transparent;

            SetAlpha(m, "_BaseColor", alpha);
            SetAlpha(m, "_Color", alpha);
        }

        private static void SetAlpha(Material m, string property, float alpha)
        {
            if (!m.HasProperty(property)) return;
            var c = m.GetColor(property);
            c.a = alpha;
            m.SetColor(property, c);
        }

        private void Restore(Renderer r)
        {
            Faded entry;
            if (!_faded.TryGetValue(r, out entry)) return;
            _faded.Remove(r);

            if (entry.Renderer != null) entry.Renderer.sharedMaterials = entry.Original;
            if (entry.Instances != null)
                foreach (var m in entry.Instances)
                    if (m != null) Object.Destroy(m);
        }

        /// <summary>Puts every faded surface back. Must run before the camera goes away.</summary>
        public void RestoreAll()
        {
            if (_faded.Count == 0) return;

            _stale.Clear();
            foreach (var kv in _faded) _stale.Add(kv.Key);
            foreach (var r in _stale) Restore(r);
            _faded.Clear();
        }
    }
}
