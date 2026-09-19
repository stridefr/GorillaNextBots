using System;
using Photon.Pun;

namespace NextBots.Runtime
{
    /// <summary>
    /// Where the mod is allowed to actually do something.
    ///
    /// Three venues are permitted, in order of how safe they are:
    ///
    ///   - **Offline** - not in a room at all. Nobody else exists, so nothing can leak.
    ///   - **Private room, master client** - the "recording with friends" case, and the
    ///     authority model at the same time: one client simulates.
    ///   - Utilla modded rooms, when Utilla is working.
    ///
    /// The original design gated everything behind a Utilla modded lobby, but Utilla's
    /// modded-room routing broke with a game update.
    ///
    /// The part that has not moved: <b>a public lobby is a hard no.</b> Spawning entities that
    /// strangers did not opt into is what gets accounts banned and what turns this into a
    /// griefing tool. That check is first and has no override.
    ///
    /// Requiring master client is not only a venue rule - it is also the authority model.
    /// One client simulates; everyone else watches.
    /// </summary>
    public static class Venue
    {
        /// <summary>Set by Utilla's join/leave callbacks when they work. Never required.</summary>
        public static bool UtillaModdedRoom;

        /// <summary>True when spawning and networking are permitted.</summary>
        public static bool CanSpawn => BlockedReason == null;

        /// <summary>
        /// Why spawning is refused, or null if it is not. Written straight onto the panel, so
        /// it has to say something a person can act on.
        /// </summary>
        public static string BlockedReason
        {
            get
            {
                try
                {
                    // Offline is the safest venue there is: no room, therefore nobody else to
                    // affect. Nothing is sent and no stranger can see anything.
                    if (!PhotonNetwork.InRoom) return null;

                    // Hard rule, no exceptions, checked before anything else can allow.
                    if (IsPublicRoom()) return "PUBLIC LOBBY - DISABLED";

                    if (!PhotonNetwork.IsMasterClient) return "NEED TO BE LOBBY MASTER";

                    return null;
                }
                catch (Exception ex)
                {
                    return "NETWORK STATE UNKNOWN (" + ex.GetType().Name + ")";
                }
            }
        }

        /// <summary>
        /// Public means "listed and joinable by strangers". GT's own definition is
        /// <c>NetworkSystem.SessionIsPrivate == !CurrentRoom.IsVisible</c>; we use the game's
        /// property where we can and fall back to the raw Photon flag.
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

        /// <summary>Short description for the panel header and the log.</summary>
        public static string Describe()
        {
            try
            {
                if (!PhotonNetwork.InRoom) return "offline (solo - allowed)";
                var room = PhotonNetwork.CurrentRoom;
                var kind = IsPublicRoom() ? "PUBLIC" : "private";
                var master = PhotonNetwork.IsMasterClient ? "master" : "guest";
                return kind + " " + (room != null ? room.Name : "?") + " (" + master + ")" +
                       (UtillaModdedRoom ? " [utilla modded]" : "");
            }
            catch { return "unknown"; }
        }
    }
}
