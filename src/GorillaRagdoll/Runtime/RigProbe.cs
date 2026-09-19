using System;
using System.Collections;
using System.Reflection;
using System.Collections.Generic;
using System.Text;
using GorillaLocomotion;
using UnityEngine;

namespace GorillaRagdoll.Runtime
{
    /// <summary>
    /// A whole-avatar state snapshot that can be diffed against an earlier one.
    ///
    /// <para>This exists because every previous theory about the leftover corruption was
    /// reasoned from the decompile and the rig <i>shape</i>, and several were wrong. The
    /// decompile says what the game's code does; it cannot say which transform is actually
    /// left displaced on this install, with these cosmetics, after a real collapse. So rather
    /// than guess again, capture the local TRS of <b>every</b> transform under the rig plus
    /// the IK wiring and the cosmetic parents, and print only what changed.</para>
    ///
    /// <para>The important property is that it is a <i>local</i> TRS dump. World positions all
    /// move together when the rig moves and tell you nothing; a local TRS that differs from the
    /// baseline is, by definition, something nobody put back.</para>
    /// </summary>
    public static class RigProbe
    {
        private const int MaxDepth = 8;
        private const int MaxNodes = 900;

        /// <summary>Lines per log call. One 100KB message risks being clipped by a log sink,
        /// and is an unreadable wall either way.</summary>
        private const int ChunkLines = 60;

        /// <summary>
        /// Insertion-ordered string map. <c>Dictionary</c> happens to enumerate in insertion
        /// order when nothing is ever removed, but that is an implementation detail rather than
        /// a promise - and a probe whose field order shuffles between runs is much harder to
        /// read than one that does not.
        /// </summary>
        private sealed class Snap
        {
            public readonly List<string> Keys = new List<string>(1024);
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(1024);

            public int Count { get { return Keys.Count; } }
            public bool Has(string k) { return _values.ContainsKey(k); }
            public string Get(string k) { string v; return _values.TryGetValue(k, out v) ? v : null; }

            public void Add(string k, string v)
            {
                if (!_values.ContainsKey(k)) { Keys.Add(k); _values[k] = v; return; }
                // Duplicate names are normal here - two cosmetics of the same type, sibling
                // bones sharing a name - and silently dropping one would hide exactly the kind
                // of stray this is looking for.
                for (int i = 2; i < 100; i++)
                {
                    string alt = k + "#" + i;
                    if (!_values.ContainsKey(alt)) { Keys.Add(alt); _values[alt] = v; return; }
                }
            }
        }

        private static Snap _baseline;
        private static string _baselineTag;

        /// <summary>
        /// Snapshot and log. The first capture that is allowed to becomes the baseline and
        /// prints in full; later ones print only what has drifted from it.
        /// </summary>
        /// <param name="baseline">Whether this capture may become the baseline. The boot dump
        /// passes false: cosmetics finish spawning after it, so a baseline taken there reports
        /// every hat and badge as "added" on every later diff and buries the real drift. The
        /// first pre-collapse is the honest reference - fully dressed, idle, and not yet
        /// touched by any of this.</param>
        public static void Capture(string tag, bool full = false, bool baseline = true)
        {
            VRRig rig = null;
            try { rig = VRRig.LocalRig; } catch { }
            if (rig == null) { Plugin.Log.LogWarning("[Probe] " + tag + ": no local rig"); return; }

            Snap now;
            try { now = Snapshot(rig); }
            catch (Exception ex) { Plugin.Log.LogError("[Probe] " + tag + " threw: " + ex); return; }

            if (_baseline == null || full)
            {
                LogFull(tag, now);
                if (_baseline == null && baseline)
                {
                    _baseline = now;
                    _baselineTag = tag;
                    Plugin.Log.LogInfo("[Probe] baseline set at '" + tag + "'");
                }
                return;
            }

            if (_baseline == null) return;
            LogDiff(tag, _baseline, now);
        }

        /// <summary>Forget the baseline so the next capture becomes one. Bound to the menu so a
        /// run can be re-based without restarting the game.</summary>
        public static void ResetBaseline()
        {
            _baseline = null;
            _baselineTag = null;
            Plugin.Log.LogInfo("[Probe] baseline cleared");
        }

        private static void LogFull(string tag, Snap now)
        {
            var lines = new List<string>(now.Count);
            foreach (var k in now.Keys) lines.Add("  " + k + " = " + now.Get(k));
            Emit("[Probe] " + tag + " - FULL (" + now.Count + " fields)", lines);
        }

        private static void LogDiff(string tag, Snap before, Snap after)
        {
            var lines = new List<string>(64);
            int changed = 0, added = 0, removed = 0;

            foreach (var k in after.Keys)
            {
                if (!before.Has(k))
                {
                    added++;
                    lines.Add("  + " + k + " = " + after.Get(k));
                }
                else if (before.Get(k) != after.Get(k))
                {
                    changed++;
                    lines.Add("  ~ " + k);
                    lines.Add("      was " + before.Get(k));
                    lines.Add("      now " + after.Get(k));
                }
            }
            foreach (var k in before.Keys)
                if (!after.Has(k)) { removed++; lines.Add("  - " + k + " = " + before.Get(k)); }

            if (lines.Count == 0)
                lines.Add("  (identical - nothing the probe watches has drifted)");

            Emit("[Probe] " + tag + " vs baseline '" + _baselineTag + "': " +
                 changed + " changed, " + added + " added, " + removed + " removed", lines);
        }

        /// <summary>Header plus body, split across log calls so no single message is large
        /// enough for a sink to clip.</summary>
        private static void Emit(string header, List<string> lines)
        {
            var sb = new StringBuilder(8192);
            sb.AppendLine(header);
            int n = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                sb.AppendLine(lines[i]);
                if (++n < ChunkLines) continue;
                Plugin.Log.LogInfo(sb.ToString());
                sb.Length = 0;
                n = 0;
            }
            if (sb.Length > 0) Plugin.Log.LogInfo(sb.ToString());
        }

        // ------------------------------------------------------------------ snapshot

        private static Snap Snapshot(VRRig rig)
        {
            var s = new Snap();

            var p = GTPlayer.Instance;
            if (p != null)
            {
                s.Add("player.localPos", V(p.transform.localPosition));
                s.Add("player.localRot", Q(p.transform.localRotation));
                s.Add("player.worldRot", Q(p.transform.rotation));
                s.Add("player.up", V(p.transform.up));
                s.Add("player.scale", V(p.transform.localScale));
                s.Add("player.disableMovement", p.disableMovement.ToString());
                if (p.playerRigidBody != null)
                    s.Add("player.kinematic", p.playerRigidBody.isKinematic.ToString());
                if (p.mainCamera != null)
                {
                    s.Add("camera.path", Path(rig.transform, p.mainCamera.transform));
                    s.Add("camera.localPos", V(p.mainCamera.transform.localPosition));
                    s.Add("camera.localRot", Q(p.mainCamera.transform.localRotation));
                }
                Transform ct = null;
                try { ct = p.CosmeticsHeadTarget; } catch { }
                if (ct != null)
                {
                    s.Add("cosHeadTarget.path", Path(rig.transform, ct));
                    s.Add("cosHeadTarget.localPos", V(ct.localPosition));
                    s.Add("cosHeadTarget.localRot", Q(ct.localRotation));
                    s.Add("cosHeadTarget.children", ct.childCount.ToString());
                    for (int i = 0; i < ct.childCount; i++)
                    {
                        var c = ct.GetChild(i);
                        s.Add("cosHeadTarget.child[" + c.name + "]",
                            "lp=" + V(c.localPosition) + " lr=" + Q(c.localRotation) +
                            " ls=" + V(c.localScale) + " active=" + c.gameObject.activeSelf);
                    }
                }
            }

            // ---- the rig object itself
            s.Add("vrrig.parent", rig.transform.parent != null ? rig.transform.parent.name : "<none>");
            s.Add("vrrig.localPos", V(rig.transform.localPosition));
            s.Add("vrrig.localRot", Q(rig.transform.localRotation));
            s.Add("vrrig.localScale", V(rig.transform.localScale));
            s.Add("vrrig.lossyScale", V(rig.transform.lossyScale));
            s.Add("vrrig.enabled", rig.enabled.ToString());
            s.Add("vrrig.activeSelf", rig.gameObject.activeSelf.ToString());
            s.Add("vrrig.postTickRunning", rig.PostTickRunning.ToString());
            try { s.Add("vrrig.scaleFactor", rig.scaleFactor.ToString("F4")); } catch { }

            // ---- who the IK actually drives. Settles the rig-shape questions the README could
            // only guess at, because these are the transform references the solver holds.
            GorillaIK ik = null;
            try { ik = rig.GetComponent<GorillaIK>(); } catch { }
            if (ik == null) s.Add("ik", "<no GorillaIK on the rig>");
            else
            {
                s.Add("ik.enabled", ik.enabled.ToString());
                s.Add("ik.usingUpdatedIK", ik.usingUpdatedIK.ToString());
                s.Add("ik.canUseUpdatedIK", ik.canUseUpdatedIK.ToString());
                s.Add("ik.renderDisplacement", V(ik.renderDisplacement));
                s.Add("ik.bodyInitialRot", Q(ik.bodyInitialRot));
                s.Add("ik.lerpBodyRot", Q(ik.lerpBodyRot));
                s.Add("ik.isPlayerIK", (GorillaIK.playerIK == ik).ToString());
                Bone(s, rig, "ik.headBone", ik.headBone);
                Bone(s, rig, "ik.bodyBone", ik.bodyBone);
                Bone(s, rig, "ik.leftUpperArm", ik.leftUpperArm);
                Bone(s, rig, "ik.leftLowerArm", ik.leftLowerArm);
                Bone(s, rig, "ik.leftHand", ik.leftHand);
                Bone(s, rig, "ik.rightUpperArm", ik.rightUpperArm);
                Bone(s, rig, "ik.rightLowerArm", ik.rightLowerArm);
                Bone(s, rig, "ik.rightHand", ik.rightHand);
                Bone(s, rig, "ik.targetHead", ik.targetHead);
                Bone(s, rig, "ik.targetLeft", ik.targetLeft);
                Bone(s, rig, "ik.targetRight", ik.targetRight);

                // Is the head bone still where it belongs relative to the torso? This is the
                // single number the reported symptom is about.
                if (ik.headBone != null && ik.bodyBone != null)
                {
                    s.Add("d(head,body)", Vector3.Distance(ik.headBone.position, ik.bodyBone.position).ToString("F4"));
                    s.Add("head.inBodySpace", V(ik.bodyBone.InverseTransformPoint(ik.headBone.position)));
                }
                if (ik.headBone != null)
                    s.Add("d(head,vrrig)", Vector3.Distance(ik.headBone.position, rig.transform.position).ToString("F4"));
            }

            Map(s, rig, "map.head", rig.head);
            Map(s, rig, "map.leftHand", rig.leftHand);
            Map(s, rig, "map.rightHand", rig.rightHand);

            IkManager(s, ik);

            // Components on the transforms that matter. "VR Constraints" is not just a name -
            // if the head target is driven by a Unity constraint rather than by MapMine, then
            // writing to it is a no-op and the whole head story is different.
            if (rig.head != null) Components(s, "comp.head.rigTarget", rig.head.rigTarget);
            if (ik != null)
            {
                Components(s, "comp.headBone", ik.headBone);
                Components(s, "comp.bodyBone", ik.bodyBone);
                Components(s, "comp.targetHead", ik.targetHead);
            }

            // ---- registered cosmetics and where they currently hang
            Registry(s, rig, "cos", () => rig.cosmetics);
            Registry(s, rig, "ovr", () => rig.overrideCosmetics);

            // ---- and finally every transform under the rig, by local TRS. Anything that did
            // not get put back shows up here and nowhere else.
            int budget = MaxNodes;
            Walk(s, rig.transform, rig.transform, 0, ref budget);
            if (budget <= 0) s.Add("tree.truncated", "hit " + MaxNodes + " nodes");

            return s;
        }

        /// <summary>
        /// Whether the local rig is still registered with the thing that actually poses it.
        ///
        /// <para><c>GorillaIKMgr</c> holds the head, body, arm and hand transforms in a
        /// <c>TransformAccessArray</c> and writes them from a Burst job every LateUpdate. A rig
        /// that falls out of <c>ikList</c> stops being written at all - the body keeps following
        /// the camera because <c>VRRig.PostTick</c> moves the rig root, but the head bone freezes
        /// at whatever rotation it last had, which is exactly a head that hangs off the torso
        /// with its cosmetics still attached. Everything here is private, hence reflection.</para>
        /// </summary>
        private static void IkManager(Snap s, GorillaIK ik)
        {
            var mgr = GorillaIKMgr.Instance;
            if (mgr == null) { s.Add("ikmgr", "<no instance>"); return; }

            s.Add("ikmgr.enabled", mgr.enabled.ToString());
            s.Add("ikmgr.playerIKset", (GorillaIKMgr.playerIK != null).ToString());

            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(GorillaIKMgr);

            try
            {
                var listField = type.GetField("ikList", F);
                var list = listField != null ? listField.GetValue(mgr) as IList : null;
                if (list == null) s.Add("ikmgr.ikList", "<unreadable>");
                else
                {
                    s.Add("ikmgr.ikList.count", list.Count.ToString());
                    int at = -1;
                    for (int i = 0; i < list.Count; i++)
                        if (ReferenceEquals(list[i], ik)) { at = i; break; }
                    s.Add("ikmgr.localIkIndex", at.ToString());
                    s.Add("ikmgr.localIkRegistered", (at >= 0).ToString());
                }

                var szField = type.GetField("actualListSz", F);
                if (szField != null) s.Add("ikmgr.actualListSz", szField.GetValue(mgr).ToString());

                var taaField = type.GetField("tAA", F);
                if (taaField != null)
                {
                    var taa = taaField.GetValue(mgr);
                    var lenProp = taa != null ? taa.GetType().GetProperty("length") : null;
                    s.Add("ikmgr.tAA.length", lenProp != null ? lenProp.GetValue(taa, null).ToString() : "<unreadable>");
                }

                var dirtyField = type.GetField("updatedSinceLastRun", F);
                if (dirtyField != null) s.Add("ikmgr.updatedSinceLastRun", dirtyField.GetValue(mgr).ToString());
            }
            catch (Exception ex)
            {
                s.Add("ikmgr.error", ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Components(Snap s, string key, Transform t)
        {
            if (t == null) { s.Add(key, "<null>"); return; }
            var sb = new StringBuilder(128);
            var comps = t.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(comps[i] == null ? "<missing script>" : comps[i].GetType().Name);
            }
            s.Add(key, sb.ToString());
        }

        private static void Bone(Snap s, VRRig rig, string key, Transform t)
        {
            if (t == null) { s.Add(key, "<null>"); return; }
            s.Add(key, Path(rig.transform, t) + " | lp=" + V(t.localPosition) + " lr=" + Q(t.localRotation));
        }

        private static void Map(Snap s, VRRig rig, string key, VRMap m)
        {
            if (m == null) { s.Add(key, "<null map>"); return; }
            s.Add(key + ".rigTarget", m.rigTarget != null ? Path(rig.transform, m.rigTarget) : "<null>");
            if (m.rigTarget != null)
            {
                s.Add(key + ".localPos", V(m.rigTarget.localPosition));
                s.Add(key + ".localRot", Q(m.rigTarget.localRotation));
            }
            s.Add(key + ".overrideTarget", m.overrideTarget != null ? Path(rig.transform, m.overrideTarget) : "<null>");
            s.Add(key + ".trackPosOffset", V(m.trackingPositionOffset));
            s.Add(key + ".trackRotOffset", V(m.trackingRotationOffset));
            s.Add(key + ".handholdOverride", m.handholdOverrideTarget != null
                ? Path(rig.transform, m.handholdOverrideTarget) : "<null>");
        }

        private static void Registry(Snap s, VRRig rig, string prefix,
                                     Func<List<GameObject>> source)
        {
            List<GameObject> list;
            try { list = source(); }
            catch (Exception ex) { s.Add(prefix, "<threw: " + ex.GetType().Name + ">"); return; }
            if (list == null) { s.Add(prefix, "<null>"); return; }

            s.Add(prefix + ".count", list.Count.ToString());
            for (int i = 0; i < list.Count; i++)
            {
                var go = list[i];
                if (go == null) { s.Add(prefix + "[" + i + "]", "<destroyed>"); continue; }
                var t = go.transform;
                s.Add(prefix + "[" + i + "] " + go.name,
                    "parent=" + (t.parent != null ? Path(rig.transform, t.parent) : "<none>") +
                    " lp=" + V(t.localPosition) + " lr=" + Q(t.localRotation) +
                    " ls=" + V(t.localScale) + " active=" + go.activeSelf);
            }
        }

        private static void Walk(Snap s, Transform root, Transform t, int depth, ref int budget)
        {
            if (budget-- <= 0 || depth > MaxDepth) return;

            if (depth > 0)
                s.Add("t." + Path(root, t),
                    "lp=" + V(t.localPosition) + " lr=" + Q(t.localRotation) +
                    " ls=" + V(t.localScale) + " active=" + t.gameObject.activeSelf +
                    " layer=" + t.gameObject.layer);

            for (int i = 0; i < t.childCount; i++) Walk(s, root, t.GetChild(i), depth + 1, ref budget);
        }

        // ------------------------------------------------------------------ formatting

        /// <summary>Path relative to the rig, so the same bone reads the same across runs. A
        /// transform outside the rig is prefixed with '!' and given its own scene path - that
        /// prefix alone answers "is this thing even part of the avatar".</summary>
        private static string Path(Transform root, Transform t)
        {
            if (t == null) return "<null>";

            var parts = new List<string>(12);
            var cur = t;
            while (cur != null && cur != root)
            {
                parts.Add(cur.name);
                cur = cur.parent;
            }

            string prefix = cur == root ? "" : "!";
            parts.Reverse();
            return prefix + string.Join("/", parts.ToArray());
        }

        private static string V(Vector3 v)
        {
            return "(" + v.x.ToString("F4") + "," + v.y.ToString("F4") + "," + v.z.ToString("F4") + ")";
        }

        private static string Q(Quaternion q)
        {
            var e = q.eulerAngles;
            return "(" + e.x.ToString("F2") + "," + e.y.ToString("F2") + "," + e.z.ToString("F2") + ")";
        }

    }
}
