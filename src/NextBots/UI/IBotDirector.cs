using System.Collections.Generic;
using UnityEngine;

namespace NextBots.UI
{
    /// <summary>
    /// What the menu is allowed to ask of the bot system. Deliberately narrow so the UI does
    /// not depend on how bots are actually spawned or replicated - that is still an open
    /// question (Option A vs B) and the panel should not care.
    /// </summary>
    public interface IBotDirector
    {
        /// <summary>Spawnable types, in browse order.</summary>
        IReadOnlyList<string> SpawnTypes { get; }

        int LiveCount { get; }

        /// <summary>
        /// False when the mod is inert - not a modded lobby, or we do not hold authority.
        /// The menu shows <see cref="BlockedReason"/> instead of pretending to work.
        /// </summary>
        bool CanSpawn { get; }

        string BlockedReason { get; }

        /// <summary>Place a bot at an aimed point. Returns a line for the confirmation row.</summary>
        bool TrySpawn(int typeIndex, Vector3 position, Quaternion rotation, out string message);

        bool UndoLast(out string message);

        /// <summary>Returns how many were removed.</summary>
        int ClearAll();
    }
}
