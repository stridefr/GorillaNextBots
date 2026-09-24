using System;
using System.Collections.Generic;
using GorillaLocomotion;
using GorillaRagdoll.Config;
using UnityEngine;
using UnityEngine.Rendering;

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
    ///
    /// <para><b>Visible to the wrong eye.</b> In normal play a first-person cosmetic is never
    /// something you see yourself - it sits at the edge of your own vision by design, close in
    /// front of the camera but off to the side of where you actually look, which is how glasses
    /// disappear from your own view while everyone else still sees them. Re-anchored onto the head
    /// bone it keeps that exact placement relative to the *body*, but the first-person camera
    /// modes place the eye at a bone-relative offset of their own that has no reason to line up
    /// with wherever the item happens to sit - so it can end up dead centre instead, filling the
    /// view. Rather than try to reproduce the original placement's geometry exactly, the
    /// re-anchored item is simply hidden from your own eye camera for as long as a first-person
    /// mode is showing it - exactly the outcome normal play already gives you - and left showing
    /// for every other camera: the monitor, the kill cam, third person.</para>
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

        private struct SwitchOn
        {
            public GameObject Node;
            public bool Was;
        }

        private readonly List<SwitchOn> _switchedOn = new List<SwitchOn>(4);
        private readonly List<Entry> _moved = new List<Entry>(8);
        private readonly List<LayerFix> _layerFixed = new List<LayerFix>(16);

        /// <summary>Renderers of items that rode the headset, hidden from your own eye camera only
        /// while a first-person mode is showing it.</summary>
        private readonly List<Renderer> _hideFromEye = new List<Renderer>(4);

        /// <summary>The one instance there ever is, for the static rendering hook to reach.</summary>
        private static CosmeticReanchor _active;

        /// <summary>Logged once, not every frame, if this ever throws.</summary>
        private static bool _hookFaulted;

        static CosmeticReanchor()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
        }

        /// <summary>Bones a stray can be attached to, best-match by distance.</summary>
        private static readonly string[] AnchorBones =
        {
            RagdollPlan.Head, RagdollPlan.Torso, RagdollPlan.HandLeft, RagdollPlan.HandRight
        };

        public int Count => _moved.Count + _switchedOn.Count;

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
            if (_moved.Count > 0 || _switchedOn.Count > 0) End();
            _active = this;

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

            List<GameObject> cosList = null, ovrList = null;
            try { cosList = rig.cosmetics; } catch { /* not ready */ }
            try { ovrList = rig.overrideCosmetics; } catch { /* not ready */ }

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

                // A first-person part is the headset-only copy of a cosmetic. The rig also carries the
                // copy meant for the body - on the head bone, switched off while the headset copy is
                // the one in use. That one is posed by the game for exactly the place it is going to
                // be seen from, so use it: switch it on for the ragdoll, and leave the headset copy
                // where it is. Moving the headset copy across instead is what put a second,
                // wrongly-placed pair of glasses on the face.
                if (ridesHeadset)
                {
                    var twin = FindBodyCopy(t, skeleton, cosList, ovrList);
                    if (twin != null)
                    {
                        _switchedOn.Add(new SwitchOn { Node = twin.gameObject, Was = twin.gameObject.activeSelf });
                        twin.gameObject.SetActive(true);
                        _hideFromEye.AddRange(twin.GetComponentsInChildren<Renderer>(true));
                        Plugin.Log.LogInfo("[Cosmetics]   '" + t.name + "' -> using the rig's own copy '" + twin.name + "' on '" +
                                           (twin.parent != null ? twin.parent.name : "<none>") + "'");
                        continue;
                    }
                }

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
                Vector3 worldScale = t.lossyScale;
                t.SetParent(anchor, true);
                if (unlag) t.SetPositionAndRotation(fixedPos, fixedRot);

                // Under a bone whose own scale is not the same on every axis, keeping the world pose
                // shears the item - glasses come out stretched. Give it the scale that lands its
                // world size where it was instead.
                Vector3 a = anchor.lossyScale;
                if (a.x > 0.0001f && a.y > 0.0001f && a.z > 0.0001f &&
                    Mathf.Max(a.x, Mathf.Max(a.y, a.z)) / Mathf.Min(a.x, Mathf.Min(a.y, a.z)) > 1.01f)
                {
                    t.localScale = new Vector3(worldScale.x / a.x, worldScale.y / a.y, worldScale.z / a.z);
                }
                Plugin.Log.LogInfo("[Cosmetics]     scale item " + Fmt(worldScale) + " under '" + anchor.name +
                                   "' " + Fmt(a) + " -> local " + Fmt(t.localScale));

                FixLayers(t, anchor.gameObject.layer);

                // Only the items that actually rode the headset need hiding from your own eye - a
                // held item or a chest badge that fell back to "nearest bone" is exactly as visible
                // to you in first person as your own hands already are, and should stay that way.
                if (ridesHeadset) _hideFromEye.AddRange(t.GetComponentsInChildren<Renderer>(true));
            }

            if (_moved.Count > 0 || _switchedOn.Count > 0)
                Plugin.Log.LogInfo("[Cosmetics] re-anchored " + _moved.Count + " stray item(s) onto the rig" +
                                   (_layerFixed.Count > 0 ? ", " + _layerFixed.Count + " node(s) moved off FirstPersonOnly so the monitor and kill cam can see them" : "") +
                                   (_hideFromEye.Count > 0 ? ", " + _hideFromEye.Count + " renderer(s) hidden from your own eye in first person" : ""));
        }

        private static string Fmt(Vector3 v) => "(" + v.x.ToString("0.###") + ", " + v.y.ToString("0.###") + ", " + v.z.ToString("0.###") + ")";

        /// <summary>The body-side copy of a headset cosmetic: the same name on the skeleton, or failing
        /// that the entry at the same place in the other list.</summary>
        private static Transform FindBodyCopy(Transform item, Transform skeleton,
                                              List<GameObject> cosList, List<GameObject> ovrList)
        {
            if (cosList == null) return null;
            foreach (var go in cosList)
                if (go != null && go.transform != item && go.transform.IsChildOf(skeleton) && go.name == item.name)
                    return go.transform;

            if (ovrList == null || ovrList.Count != cosList.Count) return null;
            int i = ovrList.FindIndex(o => o != null && o.transform == item);
            if (i < 0) return null;
            var c = cosList[i];
            return c != null && c.transform.IsChildOf(skeleton) ? c.transform : null;
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

            foreach (var sw in _switchedOn)
                if (sw.Node != null) sw.Node.SetActive(sw.Was);
            _switchedOn.Clear();

            foreach (var fix in _layerFixed)
                if (fix.Node != null) fix.Node.gameObject.layer = fix.Layer;
            _layerFixed.Clear();

            // Nothing left to hide, so the render hook's next look at this instance is a no-op
            // without needing to know the ragdoll has ended.
            foreach (var r in _hideFromEye) if (r != null) r.forceRenderingOff = false;
            _hideFromEye.Clear();
        }

        // ================================================================== the wrong-eye hide

        /// <summary>
        /// Both hooks below are wrapped, on purpose, even though nothing here looks like it should
        /// throw. <c>RenderPipelineManager.beginCameraRendering</c>/<c>endCameraRendering</c> are
        /// plain C# multicast events: Unity invokes every subscriber in turn with no isolation
        /// between them, so one subscriber throwing stops every subscriber registered *after* it for
        /// that call - including another mod's own per-camera hide/place logic. A cosmetic that is
        /// meant to disappear when it shouldn't render is a much smaller problem than an unrelated
        /// screen effect it broke on the way through never getting hidden or repositioned at all.
        /// </summary>
        private static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            try
            {
                var a = _active;
                if (a != null)
                    foreach (var sw in a._switchedOn)
                        if (sw.Node != null && !sw.Node.activeSelf) sw.Node.SetActive(true);
                if (a == null || a._hideFromEye.Count == 0 || !a.IsEyeCamera(cam) || !WantsHiddenFromEye()) return;
                foreach (var r in a._hideFromEye) if (r != null) r.forceRenderingOff = true;
            }
            catch (Exception ex) { LogHookFault(ex); }
        }

        private static void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
        {
            try
            {
                var a = _active;
                if (a == null || a._hideFromEye.Count == 0 || !a.IsEyeCamera(cam)) return;
                foreach (var r in a._hideFromEye) if (r != null) r.forceRenderingOff = false;
            }
            catch (Exception ex) { LogHookFault(ex); }
        }

        private static void LogHookFault(Exception ex)
        {
            if (_hookFaulted) return;
            _hookFaulted = true;
            Plugin.Log.LogWarning("[Cosmetics] the eye-hide camera hook threw and will stay quiet " +
                                  "from here on, rather than risk breaking another mod's own camera " +
                                  "hook sharing the same event: " + ex);
        }

        private bool IsEyeCamera(Camera cam)
        {
            if (cam == null) return false;
            var p = GTPlayer.Instance;
            return p != null && cam == p.mainCamera;
        }

        /// <summary>Only while a first-person mode is actually showing the eye where the item now
        /// sits - not in third person, where the whole point of re-anchoring is to be seen.</summary>
        private static bool WantsHiddenFromEye()
        {
            var mode = RagdollConfig.VrMode.Value;
            return mode == CameraMode.FirstPersonUnlocked || mode == CameraMode.FirstPersonLocked;
        }
    }
}
