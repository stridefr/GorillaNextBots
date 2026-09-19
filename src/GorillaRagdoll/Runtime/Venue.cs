using System;
using Photon.Pun;

namespace GorillaRagdoll.Runtime
{
    /// <summary>
    /// Where ragdolling is allowed.
    ///
    /// This is a lighter gate than the NextBots one, because the failure mode is different.
    /// NextBots spawns networked entities and needs to be the simulating authority, so it
    /// demands master client. A ragdoll spawns nothing - but it does move your networked
    /// body, so to everyone else in the lobby you appear to slide along the floor or fly.
    /// That is disruptive in a public room and nowhere else, so the only hard rule kept is:
    /// <b>no public lobbies</b>.
    ///
    /// Offline and private rooms are both fine, and you do not need to be master.
    /// </summary>
    public static class Venue
    {
        /// <summary>True when ragdolling is permitted right now.</summary>
        public static bool Allowed => BlockedReason == null;

        /// <summary>
        /// Why ragdolling is refused, or null if it is not. Shown in the overlay, so it has
        /// to say something a person can act on.
        /// </summary>
        public static string BlockedReason
        {
            get
            {
                if (!Config.RagdollConfig.RestrictToPrivateRooms.Value) return null;

                try
                {
                    // Not in a room at all is the safest case there is.
                    if (!PhotonNetwork.InRoom) return null;
                    if (IsPublicRoom()) return "public lobby - ragdoll disabled";
                    return null;
                }
                catch (Exception ex)
                {
                    return "network state unknown (" + ex.GetType().Name + ")";
                }
            }
        }

        /// <summary>
        /// Public means "listed and joinable by strangers". GT's own definition is
        /// <c>NetworkSystem.SessionIsPrivate</c>; we use the game's property where we can and
        /// fall back to the raw Photon flag.
        ///
        /// Fails closed: if we cannot tell, we treat it as public.
        /// </summary>
        public static bool IsPublicRoom()
        {
            try
            {
                var net = NetworkSystem.Instance;
                if (net != null) return !net.SessionIsPrivate;
            }
            catch { /* fall through to Photon */ }

            try
            {
                var room = PhotonNetwork.CurrentRoom;
                if (room == null) return true;
                return room.IsVisible;
            }
            catch { return true; }
        }

        /// <summary>Short description for the overlay header and the log.</summary>
        public static string Describe()
        {
            try
            {
                if (!PhotonNetwork.InRoom) return "offline";
                var room = PhotonNetwork.CurrentRoom;
                return (IsPublicRoom() ? "PUBLIC " : "private ") + (room != null ? room.Name : "?");
            }
            catch { return "unknown"; }
        }
    }
}
