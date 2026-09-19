using System;
using UnityEngine;

namespace GorillaRagdoll.Runtime
{
    public enum BonePart { Torso, Head, Shoulder, UpperArm, Forearm, Hand, Thigh, Shin, Foot, Other }

    /// <summary>
    /// Turns bone names into ragdoll behaviour: what a bone is, how heavy, how far it bends.
    ///
    /// <para>The exact skeleton is a rig detail that is not in the decompile, so nothing here
    /// is allowed to be load-bearing. Name matching is a quality improvement on top of a
    /// structural fallback (see <see cref="RagdollRig"/>): if every name misses, every bone
    /// classifies as <see cref="BonePart.Other"/> and you still get a working ragdoll, just a
    /// more generic one. <c>DumpSkeletonKey</c> prints the real names so this can be tuned
    /// against facts rather than guesses.</para>
    ///
    /// <para>The head and hand collider numbers are not invented - they are lifted from
    /// <c>GorillaPawn</c>, which builds its own collision proxies on these same bones.</para>
    /// </summary>
    public static class BoneProfile
    {
        private static bool Has(string n, string s) => n.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Bones that must never get a rigidbody: fingers (too small and too many, they turn
        /// a ragdoll into a jittering mess) and non-anatomical attachment points.
        /// </summary>
        public static bool IsExcluded(string n)
        {
            return Has(n, "index") || Has(n, "middle") || Has(n, "thumb") || Has(n, "ring") ||
                   Has(n, "pinky") || Has(n, "finger") || Has(n, "digit") ||
                   Has(n, "eye") || Has(n, "jaw") || Has(n, "tongue") ||
                   Has(n, "cosmetic") || Has(n, "attach") || Has(n, "anchor") ||
                   Has(n, "nametag") || Has(n, "badge") || Has(n, "socket");
        }

        public static BonePart Classify(string n)
        {
            if (Has(n, "head") || Has(n, "skull") || Has(n, "neck")) return BonePart.Head;
            if (Has(n, "hand") || Has(n, "wrist")) return BonePart.Hand;
            if (Has(n, "foot") || Has(n, "ankle") || Has(n, "toe")) return BonePart.Foot;
            if (Has(n, "forearm") || Has(n, "lowerarm") || Has(n, "elbow")) return BonePart.Forearm;
            if (Has(n, "upperarm") || Has(n, "shoulder") || Has(n, "clavicle")) return BonePart.UpperArm;
            if (Has(n, "shin") || Has(n, "calf") || Has(n, "lowerleg") || Has(n, "knee")) return BonePart.Shin;
            if (Has(n, "thigh") || Has(n, "upperleg") || Has(n, "hip")) return BonePart.Thigh;
            if (Has(n, "body") || Has(n, "chest") || Has(n, "spine") || Has(n, "torso") ||
                Has(n, "pelvis") || Has(n, "root")) return BonePart.Torso;

            // GT names limbs plainly ("arm.L", "leg.L"), so these come last: they must not
            // shadow the more specific "forearm"/"upperleg" matches above.
            if (Has(n, "arm")) return BonePart.UpperArm;
            if (Has(n, "leg")) return BonePart.Thigh;
            return BonePart.Other;
        }

        /// <summary>Relative mass, normalised against the configured total later.</summary>
        public static float MassWeight(BonePart p)
        {
            switch (p)
            {
                // Only four bodies carry mass now (torso, head, two hands), so these are a
                // split of the whole gorilla rather than per-bone weights. The head and hands
                // are deliberately heavy: they are the only things that can tip the torso, and
                // a hand swinging on its tether is what makes the body roll over.
                case BonePart.Torso: return 10f;
                case BonePart.Head: return 3f;
                case BonePart.Shoulder: return 0.7f;
                case BonePart.UpperArm: return 1.8f;
                case BonePart.Forearm: return 1.3f;
                case BonePart.Hand: return 1.2f;
                case BonePart.Thigh: return 2.0f;
                case BonePart.Shin: return 1.3f;
                case BonePart.Foot: return 0.6f;
                default: return 1.0f;
            }
        }

        /// <summary>
        /// Joint limits in degrees, before <c>JointLooseness</c> scales them. Elbows and knees
        /// get a near-hinge (one wide swing, one tiny) because a limb that bends sideways is
        /// the single clearest tell that a ragdoll is fake.
        /// </summary>
        public static void Limits(BonePart p, out float swing1, out float swing2, out float twist)
        {
            switch (p)
            {
                case BonePart.Head: swing1 = 40f; swing2 = 30f; twist = 50f; break;
                // Loose on purpose. Stiff shoulders turn a gorilla's very long arms into
                // rigid struts, and since GT gorillas already stand on their arms the ragdoll
                // just props itself upright instead of collapsing.
                case BonePart.Shoulder: swing1 = 50f; swing2 = 40f; twist = 25f; break;
                case BonePart.UpperArm: swing1 = 75f; swing2 = 50f; twist = 45f; break;
                case BonePart.Forearm: swing1 = 90f; swing2 = 8f; twist = 20f; break;
                case BonePart.Hand: swing1 = 50f; swing2 = 30f; twist = 45f; break;
                case BonePart.Thigh: swing1 = 70f; swing2 = 40f; twist = 30f; break;
                case BonePart.Shin: swing1 = 90f; swing2 = 5f; twist = 15f; break;
                case BonePart.Foot: swing1 = 30f; swing2 = 20f; twist = 20f; break;
                case BonePart.Torso: swing1 = 30f; swing2 = 25f; twist = 25f; break;
                default: swing1 = 60f; swing2 = 40f; twist = 40f; break;
            }
        }

        /// <summary>
        /// Capsule fatness as a fraction of bone length. A torso sized like a limb reads as a
        /// plank, and - more practically - a thin torso collider lets the body sink into the
        /// floor while the arms hold it up.
        /// </summary>
        public static float RadiusRatio(BonePart p)
        {
            switch (p)
            {
                case BonePart.Torso: return 0.45f;
                case BonePart.Shoulder: return 0.55f;
                case BonePart.UpperArm: return 0.34f;
                case BonePart.Forearm: return 0.30f;
                default: return 0.32f;
            }
        }

        /// <summary>
        /// Colliders GT already uses on these bones, so the ragdoll's head and hands are the
        /// size the game thinks they are instead of whatever we would derive from bone length.
        /// Values from <c>GorillaPawn.Rebuild</c>. Bone-local, so player scaling handles itself.
        /// </summary>
        public static bool TryKnownCollider(BonePart part, string name, out Vector3 center, out float radius)
        {
            if (part == BonePart.Head)
            {
                center = new Vector3(0f, 0.13f, 0.015f);
                radius = 0.12f;
                return true;
            }
            if (part == BonePart.Hand)
            {
                bool left = Has(name, ".L") || Has(name, "left") || name.EndsWith("L", StringComparison.Ordinal);
                center = new Vector3(left ? -0.014f : 0.014f, 0.034f, 0f);
                radius = 0.044f;
                return true;
            }
            center = Vector3.zero;
            radius = 0f;
            return false;
        }
    }
}
