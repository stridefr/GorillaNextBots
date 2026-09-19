using Photon.Pun;
using UnityEngine;

namespace NextBots.Runtime
{
    /// <summary>
    /// Player names and positions by Photon actor number, for the death log and catch messages.
    ///
    /// <para>The name comes from the player's rig - <c>VRRig.playerNameVisible</c>, the text on
    /// their nametag - rather than the raw Photon nickname. GT has already run that through its
    /// name filter and the viewer's own nametag setting, so the death log shows exactly what
    /// the person reading it sees above that player's head, and never a name the game would
    /// have hidden.</para>
    /// </summary>
    public static class PlayerNames
    {
        public static string Of(int actor)
        {
            string name;
            return TryOf(actor, out name) ? name : actor >= 0 ? "PLAYER " + actor : "PLAYER";
        }

        public static bool TryOf(int actor, out string name)
        {
            name = null;
            var rig = RigOf(actor);
            if (rig != null && !string.IsNullOrEmpty(rig.playerNameVisible)) name = rig.playerNameVisible;

            if (name == null)
            {
                try
                {
                    var room = PhotonNetwork.CurrentRoom;
                    var p = room != null ? room.GetPlayer(actor) : null;
                    if (p != null && !string.IsNullOrEmpty(p.NickName)) name = p.NickName;
                }
                catch { /* offline, or they left */ }
            }

            if (name == null)
            {
                try
                {
                    var net = NetworkSystem.Instance;
                    if (net != null && (net.LocalPlayer == null || net.LocalPlayer.ActorNumber == actor))
                    {
                        var mine = net.GetMyNickName();
                        if (!string.IsNullOrEmpty(mine)) name = mine;
                    }
                }
                catch { /* best effort */ }
            }

            if (name == null) return false;
            name = Clean(name);
            return name.Length > 0;
        }

        public static bool TryGetPosition(int actor, out Vector3 position)
        {
            var rig = RigOf(actor);
            if (rig == null) { position = Vector3.zero; return false; }
            var head = rig.headConstraint;
            position = head != null ? Vector3.Lerp(rig.transform.position, head.position, 0.5f) : rig.transform.position;
            return true;
        }

        /// <summary>The rig currently owned by this actor. Pooled rigs get reassigned when players
        /// leave and join, so ownership is checked against <c>Creator</c> each time.</summary>
        public static VRRig RigOf(int actor)
        {
            try
            {
                var rigs = VRRigCache.ActiveRigs;
                if (rigs != null)
                {
                    for (int i = 0; i < rigs.Count; i++)
                    {
                        var r = rigs[i];
                        if (r != null && r.Creator != null && r.Creator.ActorNumber == actor) return r;
                    }
                }

                // Offline, or before the room hands out creators, the local rig has none.
                var net = NetworkSystem.Instance;
                if (net == null || net.LocalPlayer == null || net.LocalPlayer.ActorNumber == actor)
                    return VRRig.LocalRig;
            }
            catch { /* rigs mid-teardown */ }
            return null;
        }

        /// <summary>Names are drawn as plain text; nothing that looks like markup gets through.</summary>
        private static string Clean(string name)
        {
            name = name.Replace("<", "").Replace(">", "").Trim();
            return name.Length > 24 ? name.Substring(0, 24) : name;
        }
    }
}
