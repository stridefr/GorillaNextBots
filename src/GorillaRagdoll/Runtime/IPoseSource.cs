using UnityEngine;

namespace GorillaRagdoll.Runtime
{
    /// <summary>
    /// The four poses <see cref="RigDriver"/> draws an avatar from.
    ///
    /// <para>That is the whole contract, and it is deliberately small. The local ragdoll
    /// supplies them from a physics puppet; a remote ragdoll supplies them from interpolated
    /// network snapshots. The driver cannot tell the difference and does not need to - which is
    /// why other players' ragdolls are drawn by exactly the code that draws yours.</para>
    /// </summary>
    public interface IPoseSource
    {
        Transform Body { get; }
        Transform Head { get; }
        Transform HandL { get; }
        Transform HandR { get; }
    }
}
