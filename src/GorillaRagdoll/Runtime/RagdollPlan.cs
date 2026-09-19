namespace GorillaRagdoll.Runtime
{
    /// <summary>One link in the puppet chain: a bone, what it hangs off, and what it is.</summary>
    public struct PuppetLink
    {
        public string Bone;
        public string Parent;
        public BonePart Part;

        public PuppetLink(string bone, string parent, BonePart part)
        {
            Bone = bone;
            Parent = parent;
            Part = part;
        }
    }

    /// <summary>
    /// Gorilla Tag's avatar rig, verified from a live skeleton dump, plus the puppet chain
    /// built from it. None of this is recoverable from the decompile.
    ///
    /// <code>
    /// rig                        &lt;- sits 1.65m BELOW the avatar (VRRig renderTransform offset)
    ///   hand.L, hand.R           &lt;- IK targets, direct children of rig
    ///   head                     &lt;- IK target, direct child of rig
    ///   body_pivot
    ///     body
    ///     shoulder.L -> upper_arm.L -> forearm.L
    ///     shoulder.R -> upper_arm.R -> forearm.R
    /// </code>
    ///
    /// <para>Note the shape: the hands and head are <b>targets</b>, parented to the root rather
    /// than to the limbs that reach them, and <c>GorillaIK</c> solves the arm bones to span
    /// shoulder-to-hand. The 32 mesh bones are <c>*_new</c> leaves hanging off these
    /// (<c>body_new</c> under <c>body</c>), with <c>_nhd</c>/<c>_skl</c> siblings for the other
    /// body types; they are zero-length markers. And there are no legs.</para>
    ///
    /// <para><b>The puppet chain below is deliberately a full skeleton anyway.</b> It is never
    /// rendered, so it does not have to satisfy the IK - it only has to fall like a body. The
    /// visible gorilla is GT's own rig, posed from this puppet's head and hands.</para>
    /// </summary>
    public static class RagdollPlan
    {
        /// <summary>Parents always precede their children.</summary>
        public static readonly PuppetLink[] Chain =
        {
            new PuppetLink("body",        null,          BonePart.Torso),
            new PuppetLink("head",        "body",        BonePart.Head),

            new PuppetLink("shoulder.L",  "body",        BonePart.Shoulder),
            new PuppetLink("upper_arm.L", "shoulder.L",  BonePart.UpperArm),
            new PuppetLink("forearm.L",   "upper_arm.L", BonePart.Forearm),
            new PuppetLink("hand.L",      "forearm.L",   BonePart.Hand),

            new PuppetLink("shoulder.R",  "body",        BonePart.Shoulder),
            new PuppetLink("upper_arm.R", "shoulder.R",  BonePart.UpperArm),
            new PuppetLink("forearm.R",   "upper_arm.R", BonePart.Forearm),
            new PuppetLink("hand.R",      "forearm.R",   BonePart.Hand),
        };

        public const string Torso = "body";
        public const string Head = "head";
        public const string HandLeft = "hand.L";
        public const string HandRight = "hand.R";
    }
}
