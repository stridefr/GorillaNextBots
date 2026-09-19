using System.Text;
using GorillaLocomotion;
using UnityEngine;

namespace GorillaRagdoll.Runtime
{
    /// <summary>
    /// Picks the physics layer the ragdoll lives on.
    ///
    /// <para>This is chosen at runtime rather than hardcoded, because getting it wrong fails
    /// in two opposite and equally annoying ways. Pick a layer the collision matrix excludes
    /// from world geometry and the ragdoll falls through the map. Pick a gameplay layer -
    /// "Gorilla Tag Collider", "Gorilla Hand" - and the ragdoll starts tagging people.</para>
    ///
    /// <para>So: score a short whitelist of inert prop/world layers against the layers GT
    /// actually walks on (<c>GTPlayer.LocomotionEnabledLayers</c>, which is 0 Default,
    /// 9 Gorilla Object, 26 BuilderProp, 27 NoMirror on this build), and take the one that can
    /// touch the most of them. The decision is logged, so "my ragdoll fell through the floor"
    /// is a one-line diagnosis instead of a debugging session.</para>
    /// </summary>
    public static class RagdollLayer
    {
        /// <summary>Inert layers, best first. Nothing here has gameplay meaning attached.</summary>
        private static readonly string[] Candidates = { "Prop", "BuilderProp", "Default", "Gorilla Object" };

        private static int _cached = -1;

        public static int Resolve()
        {
            if (_cached >= 0) return _cached;

            int want;
            try { want = GTPlayer.LocomotionEnabledLayers; }
            catch { want = 1; }
            if (want == 0) want = 1;

            int best = 0, bestScore = -1;
            var sb = new StringBuilder(160);

            foreach (var name in Candidates)
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer < 0) continue;

                int score = 0;
                for (int w = 0; w < 32; w++)
                {
                    if ((want & (1 << w)) == 0) continue;
                    if (!Physics.GetIgnoreLayerCollision(layer, w)) score++;
                }

                sb.Append(name).Append('=').Append(score).Append(' ');
                if (score > bestScore) { bestScore = score; best = layer; }
            }

            _cached = best;
            Plugin.Log.LogInfo("[Layer] ragdoll on '" + LayerMask.LayerToName(best) + "' (" + best +
                               ") | locomotion mask 0x" + want.ToString("X") + " | scores: " + sb);
            return best;
        }

        /// <summary>Forget the cached choice, e.g. after a scene change.</summary>
        public static void Invalidate() => _cached = -1;
    }
}
