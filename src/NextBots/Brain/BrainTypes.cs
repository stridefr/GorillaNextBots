using System.Collections.Generic;
using UnityEngine;

namespace NextBots.Brain
{
    /// <summary>
    /// The decision ladder, highest priority first. Strike is committed: nothing below it
    /// may interrupt it. Colours are what the debug overlay draws.
    /// </summary>
    public enum BotState
    {
        Idle,

        /// <summary>Default: run them down.</summary>
        Chase,

        /// <summary>
        /// Hidden by geometry - something solid is between us. Hold position at the cover
        /// and wait. Do not route around noisily.
        /// </summary>
        AmbushHold,

        /// <summary>
        /// Hidden only by facing - clear line, but their back is turned. Keep closing while
        /// they cannot see us.
        /// </summary>
        AmbushCreep,

        /// <summary>Committed charge out of an ambush. Fixed duration, uninterruptible.</summary>
        Strike,

        /// <summary>Stuck escalation 1: stop being clever about geometry, just run at them.</summary>
        Charge,

        /// <summary>Stuck escalation 2: teleport near the target rather than sit in a corner.</summary>
        Reset
    }

    public static class BotStateColors
    {
        public static Color Of(BotState state)
        {
            switch (state)
            {
                case BotState.Chase:       return new Color(1.00f, 0.45f, 0.10f); // orange
                case BotState.AmbushHold:  return new Color(0.55f, 0.30f, 0.90f); // violet
                case BotState.AmbushCreep: return new Color(0.90f, 0.25f, 0.75f); // magenta
                case BotState.Strike:      return new Color(1.00f, 0.10f, 0.10f); // red
                case BotState.Charge:      return new Color(1.00f, 0.90f, 0.15f); // yellow
                case BotState.Reset:       return new Color(0.20f, 0.85f, 0.95f); // cyan
                default:                   return new Color(0.55f, 0.55f, 0.55f); // grey
            }
        }
    }

    /// <summary>A candidate target, sampled once per brain tick.</summary>
    public struct PlayerSnapshot
    {
        /// <summary>Photon actor number. -1 is invalid.</summary>
        public int ActorNumber;

        /// <summary>Rig root, roughly at the feet.</summary>
        public Vector3 Position;

        /// <summary>Body centre - what the bot aims at and raycasts to for reachability.</summary>
        public Vector3 Center;

        /// <summary>Head position, for the "can they see us" raycast.</summary>
        public Vector3 HeadPosition;

        /// <summary>
        /// Head forward. Rotating the forward axis by head rotation and dotting it against
        /// the direction to the bot is the entire ambush feature.
        /// </summary>
        public Vector3 HeadForward;

        /// <summary>Derived by differencing positions, so it works for remote players too.</summary>
        public Vector3 Velocity;

        public bool IsValid;
    }

    /// <summary>
    /// What the brain can do to a bot. Implemented over a real GameAgent under Option A, or
    /// over a locally simulated body under Option B - the brain does not care which.
    /// </summary>
    public interface IBotBody
    {
        Vector3 Position { get; }
        Vector3 Center { get; }
        Vector3 Velocity { get; }
        bool IsOnNavMesh { get; }

        /// <summary>Max movement speed. Raised for the duration of a strike.</summary>
        float Speed { get; set; }

        void SetDestination(Vector3 destination);
        void SetStopped(bool stopped);

        /// <summary>Hard reset. Teleport/respawn rather than leave the bot in a corner.</summary>
        void Warp(Vector3 position);

        /// <summary>Look at a world point without moving there (used while holding in cover).</summary>
        void FaceTowards(Vector3 worldPoint);

        /// <summary>
        /// The agent's actual computed path, for the debug overlay. With
        /// <c>NavMeshPath.corners</c> we can draw the real route rather than a guess at it.
        /// Returns false if there is no path to show.
        /// </summary>
        bool TryGetPathCorners(List<Vector3> into);
    }

    /// <summary>Everything the brain needs to know about the world.</summary>
    public interface IWorldView
    {
        IReadOnlyList<PlayerSnapshot> Players { get; }

        /// <summary>True if solid geometry blocks the segment. Used for both questions.</summary>
        bool LineBlocked(Vector3 from, Vector3 to);

        /// <summary>Nearest point on the navigable surface, or null if there is none nearby.</summary>
        Vector3? SampleNavigable(Vector3 near, float radius);

        /// <summary>
        /// Where the player's body centre was <paramref name="delay"/> seconds ago.
        ///
        /// Chasing the target's live position is what makes a bot impossible to juke: it
        /// re-solves for exactly where you are, ten times a second, so a sidestep is matched
        /// before it means anything. Aiming at a slightly stale position gives feints and
        /// direction changes somewhere to land.
        /// </summary>
        Vector3 DelayedCenter(int actorNumber, float delay, Vector3 fallback);

        float Now { get; }
    }
}
