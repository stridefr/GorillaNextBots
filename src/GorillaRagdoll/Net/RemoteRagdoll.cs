using System.Collections.Generic;
using GorillaRagdoll.Runtime;
using UnityEngine;

namespace GorillaRagdoll.Net
{
    /// <summary>One network snapshot of a ragdoll: the four poses the driver needs.</summary>
    public struct PoseSnapshot
    {
        public double Time;
        public Vector3 BodyPos, HeadPos, HandLPos, HandRPos;
        public Quaternion BodyRot, HeadRot, HandLRot, HandRRot;
    }

    /// <summary>
    /// Another player's ragdoll, drawn on their real avatar on this screen.
    ///
    /// <para>The owner's physics is authoritative and nothing is simulated here: two clients
    /// running the same ragdoll from slightly different starting poses land in different places
    /// within a second, and for a video everyone needs to see the same fall. So this only
    /// replays what it is told - buffered and interpolated, because snapshots arrive a dozen or
    /// so times a second, unevenly, while the screen draws at 90.</para>
    ///
    /// <para>The drawing itself is the ordinary <see cref="RigDriver"/> in its remote mode, fed
    /// from four hidden transforms that stand in for the owner's puppet.</para>
    /// </summary>
    public sealed class RemoteRagdoll : IPoseSource
    {
        /// <summary>Snapshots kept - over a second of history at any send rate the config
        /// allows, far more than the interpolation delay needs; the rest is slack for reordered
        /// packets.</summary>
        private const int MaxSnapshots = 24;

        public int Actor { get; }
        public VRRig Rig { get; }

        /// <summary>Local clock time of the last packet, for the silence timeout.</summary>
        public float LastHeard { get; private set; }

        public bool Started { get; private set; }

        /// <summary>
        /// The gap between this sender's snapshots, smoothed.
        ///
        /// <para>Measured rather than read from our own config, because it is the <i>sender's</i>
        /// rate that decides how far behind we must draw. Someone sending at 5Hz while this
        /// client is set to 30 would otherwise be drawn so close to "now" that every frame ran
        /// out of future and the ragdoll stuttered from snapshot to snapshot.</para>
        /// </summary>
        public double Interval { get; private set; } = 1.0 / 15.0;

        public Transform Body { get; private set; }
        public Transform Head { get; private set; }
        public Transform HandL { get; private set; }
        public Transform HandR { get; private set; }

        private readonly RigDriver _driver = new RigDriver(remote: true);
        private readonly List<PoseSnapshot> _snaps = new List<PoseSnapshot>(MaxSnapshots);
        private GameObject _holder;

        public RemoteRagdoll(int actor, VRRig rig)
        {
            Actor = actor;
            Rig = rig;

            _holder = new GameObject("GorillaRagdoll.Remote." + actor);
            _holder.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(_holder);
            Body = Child("Body");
            Head = Child("Head");
            HandL = Child("HandL");
            HandR = Child("HandR");
        }

        private Transform Child(string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(_holder.transform, false);
            return t;
        }

        /// <summary>
        /// Adds a snapshot, in time order.
        ///
        /// <para>Pose packets are unreliable on purpose - a dropped one is superseded a send
        /// interval later, and reliable delivery would queue them into rubber-banding. The price
        /// is that they can arrive out of order, so each is slotted in by timestamp rather than
        /// appended.</para>
        /// </summary>
        public void Push(PoseSnapshot s)
        {
            LastHeard = UnityEngine.Time.unscaledTime;

            // A server clock that jumps backwards a long way (it is a wrapping 32-bit
            // millisecond counter underneath) would otherwise strand every new snapshot behind
            // the old ones and freeze the ragdoll until it timed out.
            if (_snaps.Count > 0 && s.Time < _snaps[_snaps.Count - 1].Time - 60.0) _snaps.Clear();

            int i = _snaps.Count;
            while (i > 0 && _snaps[i - 1].Time > s.Time) i--;
            if (i > 0 && _snaps[i - 1].Time == s.Time) return;   // duplicate

            // Only in-order arrivals say anything about the send rate; a late packet slotting in
            // behind the newest would read as a tiny gap and drag the estimate down.
            if (i == _snaps.Count && i > 0)
            {
                double gap = s.Time - _snaps[i - 1].Time;
                if (gap > 0.01 && gap < 0.5) Interval = Interval * 0.8 + gap * 0.2;
            }
            _snaps.Insert(i, s);

            while (_snaps.Count > MaxSnapshots) _snaps.RemoveAt(0);
        }

        /// <summary>
        /// Poses the avatar for this frame. Called just before GT's IK reads its targets.
        /// </summary>
        /// <param name="renderTime">Server time to show - deliberately a little in the past,
        /// so there is almost always a snapshot on both sides of it to interpolate between.</param>
        public void Apply(double renderTime)
        {
            if (_snaps.Count == 0 || Rig == null) return;

            Sample(renderTime);

            if (!Started)
            {
                // Begin measures the rig against itself, so it does not matter that the four
                // stand-ins were only filled in a line ago - but they must exist before Tick.
                _driver.Begin(Rig, null);
                Started = true;
                Plugin.Log.LogInfo("[Net] showing ragdoll for actor " + Actor + " (" + Name() + ")");
            }

            _driver.Tick(this);
        }

        private void Sample(double t)
        {
            PoseSnapshot a = _snaps[0], b = _snaps[0];
            float k = 0f;

            if (t >= _snaps[_snaps.Count - 1].Time)
            {
                // Ran out of future: hold the newest rather than extrapolate. A ragdoll guessed
                // forward through a floor looks far worse than one that pauses for a packet.
                a = b = _snaps[_snaps.Count - 1];
            }
            else if (t > _snaps[0].Time)
            {
                for (int i = 1; i < _snaps.Count; i++)
                {
                    if (_snaps[i].Time < t) continue;
                    a = _snaps[i - 1];
                    b = _snaps[i];
                    double span = b.Time - a.Time;
                    k = span > 1e-6 ? (float)((t - a.Time) / span) : 1f;
                    break;
                }
            }

            Body.SetPositionAndRotation(Vector3.Lerp(a.BodyPos, b.BodyPos, k), Quaternion.Slerp(a.BodyRot, b.BodyRot, k));
            Head.SetPositionAndRotation(Vector3.Lerp(a.HeadPos, b.HeadPos, k), Quaternion.Slerp(a.HeadRot, b.HeadRot, k));
            HandL.SetPositionAndRotation(Vector3.Lerp(a.HandLPos, b.HandLPos, k), Quaternion.Slerp(a.HandLRot, b.HandLRot, k));
            HandR.SetPositionAndRotation(Vector3.Lerp(a.HandRPos, b.HandRPos, k), Quaternion.Slerp(a.HandRRot, b.HandRRot, k));
        }

        /// <summary>Hands the avatar back to the game and cleans up. Safe to call twice.</summary>
        public void End(string why)
        {
            if (Started)
            {
                _driver.End();
                Started = false;
                Plugin.Log.LogInfo("[Net] ragdoll for actor " + Actor + " ended (" + why + ")");
            }
            if (_holder != null)
            {
                Object.Destroy(_holder);
                _holder = null;
            }
            _snaps.Clear();
        }

        private string Name()
        {
            try { return Rig != null && Rig.Creator != null ? Rig.Creator.NickName : "?"; }
            catch { return "?"; }
        }
    }
}
