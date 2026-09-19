using System.Collections.Generic;
using UnityEngine;

namespace GorillaRagdoll.Runtime
{
    /// <summary>Finding bones in the avatar rig, without tripping over cosmetics.</summary>
    public static class RigBones
    {
        /// <summary>
        /// Shallowest transform with this exact name, breadth-first.
        ///
        /// <para>Shallowest matters. Cosmetics carry their own sub-rigs with colliding names -
        /// a friendship bracelet contains transforms literally called <c>rig</c> and
        /// <c>hand.R</c> - so a depth-first search can happily return a bone belonging to a
        /// piece of jewellery. Breadth-first always reaches the real skeleton first.</para>
        /// </summary>
        public static Transform Find(Transform root, string name)
        {
            if (root == null) return null;

            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (t.name == name) return t;
                for (int i = 0; i < t.childCount; i++) queue.Enqueue(t.GetChild(i));
            }
            return null;
        }
    }
}
