using System;
using System.Collections.Generic;
using GorillaLocomotion;
using UnityEngine;

namespace GorillaRagdoll.Runtime
{
    /// <summary>
    /// Keeps cosmetics attached to the gorilla while it is ragdolled.
    ///
    /// <para><b>Why they come off.</b> For the local player, head cosmetics are not parented to
    /// the head bone - they hang off the <i>camera</i>. That is correct in normal play: in
    /// first person your hat has to sit on your real head, which is wherever the headset is.
    /// But the ragdoll moves the avatar away from the camera, and anything parented outside the
    /// rig simply stays behind, floating where your head physically is.</para>
    ///
    /// <para><b>How this fixes it.</b> Rather than hardcoding the anchor paths a given game
    /// version happens to use, it finds strays structurally: an item follows the ragdoll only
    /// if it hangs off the <i>bone hierarchy</i>. Being somewhere under the rig object is not
    /// enough - the camera is part of that object too, so a hat parented to it passes a naive
    /// check while still riding your headset, which is how you get cosmetics that move with the
    /// body but rotate with your real head. Each stray is
    /// re-parented to the nearest real bone - so a hat lands on the head, a chest badge on the
    /// body, a held item on a hand - with <c>worldPositionStays</c>, which preserves the exact
    /// pose it already had. At the moment of collapse the head bone is still at the camera, so
    /// that pose is the correct one by construction and there is no offset to guess at.</para>
    ///
    /// <para>Original parent and local transform are recorded per item, so getting up puts
    /// everything back exactly rather than approximately.</para>
    ///
    /// <para><b>Re-anchoring alone is not enough.</b> A first-person cosmetic normally lives on
    /// Unity's <c>FirstPersonOnly</c> layer - the layer meant for things staged right at the
    /// headset that look like floating junk from anywhere else, so the mod's own cameras (the
    /// monitor, the kill cam lens) deliberately exclude it. That is correct for a cosmetic still
    /// riding the headset, but once this class has moved one onto the head bone it is exactly
    /// that kind of junk no more - and left on that layer it is invisible to every one of those
    /// cameras regardless of how correctly it is now posed. So every reanchored item, and
    /// everything under it, is also switched onto the layer its new bone renders on for as long
    /// as the ragdoll lasts, and put back with the rest on <see cref="End"/>.</para>
    /// </summary>
    public sealed class CosmeticReanchor
    {
        private struct Entry
        {
            public Transform Item;
            public Transform Parent;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public Vector3 LocalScale;
        }

        private struct LayerFix
        {
            public Transform Node;
            public int Layer;
        }

        private readonly List<Entry> _moved = new List<Entry>(8);
        private readonly List<LayerFix> _layerFixed = new List<LayerFix>(16);

        /// <summary>Bones a stray can be attached to, best-match by distance.</summary>
        private static readonly string[] AnchorBones =
        {
            RagdollPlan.Head, RagdollPlan.Torso, RagdollPlan.HandLeft, RagdollPlan.HandRight
        };

        public int Count => _moved.Count;

        /// <summary>
        /// Must run while the avatar is still in its live pose - before the driver starts
        /// moving the rig - or the captured poses are already wrong.
        /// </summary>
        /// <param name="cameraWhenRigLastPlaced">
        /// Where the headset was the last time <c>VRRig.PostTick</c> placed the avatar from it,
        /// or null if that is not known yet. See <see cref="Begin"/>'s use of it below: without
        /// this the re-anchor bakes in a one-frame lag that grows with how fast you are moving.
        /// </param>
        public void Begin(VRRig rig, Pose? cameraWhenRigLastPlaced)
        {
            if (rig == null) return;

            // Capturing on top of an already-moved set would record a bone as an item's
            // "original" parent, and restoring to that later is exactly how a cosmetic ends up
            // permanently offset. Always start from a clean slate.
            if (_moved.Count > 0) End();

            var anchors = new List<Transform>(AnchorBones.Length);
            foreach (var name in AnchorBones)
            {
                var bone = RigBones.Find(rig.transform, name);
                if (bone != null) anchors.Add(bone);
            }
            if (anchors.Count == 0) return;

            // "Under the rig" is not the right test. For the local player the camera is part
            // of the rig object, so head cosmetics parented to it pass an IsChildOf check while
            // still riding your headset - they translate with the ragdoll but rotate with your
            // real head. What matters is whether something hangs off the *bone hierarchy*.
            Transform skeleton = RigBones.Find(rig.transform, "rig") ?? rig.transform;
            Transform camera = rig.mainCamera != null ? rig.mainCamera.transform : null;

            // GT's cosmetic spawner sorts parts into two lists, and this is the trap: Holdable
            // and Functional parts go to `cosmetics`, but FirstPerson and LocalRig parts go to
            // `overrideCosmetics` instead - and FirstPerson parts are parented to the player's
            // CosmeticsHeadTarget, not to a bone. Sweeping only `cosmetics` therefore finds
            // nothing at all to fix, which is exactly what it did.
            Transform headTarget = null;
            try { headTarget = GTPlayer.Instance != null ? GTPlayer.Instance.CosmeticsHeadTarget : null; }
            catch { /* not ready */ }

            var candidates = new List<Transform>(32);
            var seen = new HashSet<Transform>();
            Gather(candidates, seen, () => rig.cosmetics);
            Gather(candidates, seen, () => rig.overrideCosmetics);

            // Belt and braces: anything living under the first-person head anchor needs moving
            // whether or not the registries know about it.
            if (headTarget != null)
                for (int i = 0; i < headTarget.childCount; i++)
                    Consider(candidates, seen, headTarget.GetChild(i));

            foreach (var t in candidates)
            {
                if (t == null) continue;

                bool ridesHeadset = (headTarget != null && t.IsChildOf(headTarget)) ||
                                    (camera != null && t.IsChildOf(camera));
                if (!ridesHeadset && t.IsChildOf(skeleton)) continue;   // already on a bone

                // Provenance beats proximity. A first-person head cosmetic IS a head cosmetic,
                // whatever happens to be nearest at the moment you collapse - and in VR your
                // hands are very often up by your face, so picking the closest bone cheerfully
                // welds your hat to a wrist. Only fall back to distance for items whose origin
                // says nothing about where they belong.
                Transform anchor = ridesHeadset
                    ? RigBones.Find(rig.transform, RagdollPlan.Head)
                    : Nearest(anchors, t.position);
                if (anchor == null) anchor = Nearest(anchors, t.position);
                if (anchor == null) continue;

                Plugin.Log.LogInfo("[Cosmetics]   '" + t.name + "' from '" +
                                   (t.parent != null ? t.parent.name : "<none>") +
                                   "' -> '" + anchor.name + "'" +
                                   (ridesHeadset ? " (rode the headset)" : " (nearest bone)"));

                _moved.Add(new Entry
                {
                    Item = t,
                    Parent = t.parent,
                    LocalPosition = t.localPosition,
                    LocalRotation = t.localRotation,
                    LocalScale = t.localScale
                });

                // The avatar is posed from the headset in LateUpdate, so during Update - which
                // is when a collapse happens - the rig is a frame behind the camera. Preserving
                // an item's *current* world pose against a *stale* head bone therefore bakes
                // that gap in as a permanent offset for the whole ragdoll. It is invisible
                // standing still and grows in proportion to speed, which is exactly how it was
                // reported. Re-evaluating the item against the camera pose the rig was actually
                // placed from cancels it exactly.
                bool unlag = camera != null && cameraWhenRigLastPlaced.HasValue && t.IsChildOf(camera);
                Vector3 fixedPos = Vector3.zero;
                Quaternion fixedRot = Quaternion.identity;
                if (unlag)
                {
                    Pose then = cameraWhenRigLastPlaced.Value;
                    Quaternion camInv = Quaternion.Inverse(camera.rotation);
                    fixedPos = then.position + then.rotation * (camInv * (t.position - camera.position));
                    fixedRot = then.rotation * (camInv * t.rotation);
                }

                // worldPositionStays keeps the item's size across a parent with a different
                // scale; the pose is then corrected on top of it.
                t.SetParent(anchor, true);
                if (unlag) t.SetPositionAndRotation(fixedPos, fixedRot);

                FixLayers(t, anchor.gameObject.layer);
            }

            if (_moved.Count > 0)
                Plugin.Log.LogInfo("[Cosmetics] re-anchored " + _moved.Count + " stray item(s) onto the rig" +
                                   (_layerFixed.Count > 0 ? ", " + _layerFixed.Count + " node(s) moved off FirstPersonOnly so the monitor and kill cam can see them" : ""));
        }

        /// <summary>Pulls a cosmetic registry into the candidate list, tolerating a rig whose
        /// cosmetic data has not been registered yet (the accessors throw rather than
        /// returning null).</summary>
        private static void Gather(List<Transform> into, HashSet<Transform> seen,
                                   Func<List<GameObject>> source)
        {
            List<GameObject> list;
            try { list = source(); }
            catch { return; }
            if (list == null) return;

            foreach (var go in list)
                if (go != null) Consider(into, seen, go.transform);
        }

        private static void Consider(List<Transform> into, HashSet<Transform> seen, Transform t)
        {
            if (t == null || !t.gameObject.activeInHierarchy) return;
            if (seen.Add(t)) into.Add(t);
        }

        /// <summary>Puts <paramref name="root"/> and everything under it onto <paramref name="layer"/>,
        /// remembering each one that actually changed so <see cref="End"/> can put it back. A cosmetic
        /// can be a small hierarchy of its own - a frame and separate lenses, say - and Unity does not
        /// cascade a layer change to children by itself.</summary>
        private void FixLayers(Transform root, int layer)
        {
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
            {
                if (node.gameObject.layer == layer) continue;
                _layerFixed.Add(new LayerFix { Node = node, Layer = node.gameObject.layer });
                node.gameObject.layer = layer;
            }
        }

        private static Transform Nearest(List<Transform> anchors, Vector3 point)
        {
            Transform best = null;
            float bestSqr = float.MaxValue;
            foreach (var a in anchors)
            {
                float d = (a.position - point).sqrMagnitude;
                if (d < bestSqr) { bestSqr = d; best = a; }
            }
            return best;
        }

        /// <summary>Puts every moved item back on its original parent, exactly, and every layer it
        /// touched back to what it was.</summary>
        public void End()
        {
            foreach (var e in _moved)
            {
                if (e.Item == null) continue;
                e.Item.SetParent(e.Parent, false);
                e.Item.localPosition = e.LocalPosition;
                e.Item.localRotation = e.LocalRotation;
                e.Item.localScale = e.LocalScale;
            }
            _moved.Clear();

            foreach (var fix in _layerFixed)
                if (fix.Node != null) fix.Node.gameObject.layer = fix.Layer;
            _layerFixed.Clear();
        }
    }
}
