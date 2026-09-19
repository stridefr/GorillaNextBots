using System.Text;
using UnityEngine;

namespace GorillaRagdoll.Runtime
{
    /// <summary>
    /// Read-only dump of the live avatar rig.
    ///
    /// <para>This exists because the rig's shape is not in the decompile and every wrong guess
    /// about it cost a build. It prints the flat mesh-bone list <i>and</i> the transform tree,
    /// because the two disagreeing is exactly the trap here: the renderer's <c>rootBone</c> is
    /// <c>body_new</c>, which has no children at all, so reading the tree from it finds a
    /// one-bone skeleton and silently tears the mesh apart.</para>
    /// </summary>
    public static class SkeletonDump
    {
        public static string Describe(VRRig rig)
        {
            if (rig == null) return "no rig";

            var sb = new StringBuilder(4096);

            var smr = PickRenderer(rig);
            if (smr != null)
            {
                var bones = smr.bones;
                sb.Append("renderer '").Append(smr.name).Append("', bones=")
                  .Append(bones != null ? bones.Length : 0)
                  .Append(", rootBone=").Append(smr.rootBone != null ? smr.rootBone.name : "<null>")
                  .AppendLine();

                if (bones != null)
                {
                    sb.AppendLine("--- mesh bone list (index: name <- parent) ---");
                    for (int i = 0; i < bones.Length; i++)
                    {
                        var b = bones[i];
                        sb.Append(i.ToString("00")).Append(": ")
                          .Append(b != null ? b.name : "<null>").Append("  <- ")
                          .Append(b != null && b.parent != null ? b.parent.name : "<none>")
                          .AppendLine();
                    }
                }
            }

            sb.AppendLine("--- IK targets ---");
            Target(sb, "head", rig.head);
            Target(sb, "leftHand", rig.leftHand);
            Target(sb, "rightHand", rig.rightHand);

            sb.AppendLine("--- rig hierarchy ---");
            Walk(rig.transform, 0, sb);
            return sb.ToString();
        }

        private static void Target(StringBuilder sb, string label, VRMap map)
        {
            sb.Append("  ").Append(label).Append(" -> ");
            if (map == null) { sb.AppendLine("<null map>"); return; }
            sb.AppendLine(map.rigTarget != null ? map.rigTarget.name : "<null target>");
        }

        private static SkinnedMeshRenderer PickRenderer(VRRig rig)
        {
            try
            {
                if (rig.bodyRenderer != null)
                {
                    var active = rig.bodyRenderer.ActiveBody;
                    if (active != null && active.sharedMesh != null) return active;
                }
            }
            catch { }
            return rig.mainSkin;
        }

        private static void Walk(Transform t, int depth, StringBuilder sb)
        {
            // Deep cosmetic sub-rigs (bracelets, watches) triple the output and are never
            // ragdoll bones; the skeleton itself is shallow.
            if (depth > 6) return;

            sb.Append(' ', depth * 2).Append(t.name);
            if (t.parent != null)
                sb.Append("   [d=").Append(depth)
                  .Append(" len=").Append(Vector3.Distance(t.position, t.parent.position).ToString("0.000"))
                  .Append(" kids=").Append(t.childCount).Append(']');
            sb.AppendLine();

            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb);
        }
    }
}
