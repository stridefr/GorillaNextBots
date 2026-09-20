using System.Collections.Generic;
using GorillaRagdoll.Config;
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
    /// <para><b>Playback runs on its own clock.</b> Not <c>PhotonNetwork.Time</c>: that is a
    /// millisecond counter re-synced whenever a packet lands, so reading it once a frame gives a
    /// time that stutters by a few milliseconds either way - and a stuttering clock is a
    /// stuttering ragdoll however high the frame rate. Instead the clock here advances by the
    /// frame's own delta time and is nudged, never jumped, toward where the buffer says it
    /// should be: at most 15% fast or slow, which is imperceptible on a tumbling body but
    /// catches up within a second.</para>
    ///
    /// <para><b>How far behind it runs</b> is measured rather than assumed. Snapshots are timed
    /// on arrival as well as by their own timestamps, so a sender whose packets come in bursts -
    /// which is what happens when Photon coalesces sends - is drawn further back, with enough
    /// buffer to ride the burst out. A steady sender is drawn closer to live.</para>
    ///
    /// <para><b>Between snapshots it curves.</b> Straight lines between 50 ms samples of a
    /// tumbling body read as a series of little corners; a Catmull-Rom curve through four
    /// snapshots passes through the same points with a continuous direction. Rotations use
    /// squad, the quaternion equivalent, for the same reason.</para>
    ///
    /// <para><b>When a packet is late</b> the body keeps moving the way it was, briefly and with
    /// the motion easing off, rather than freezing and then jumping when the packet lands. Past
    /// <c>NetExtrapolate</c> it holds still, because a ragdoll guessed too far forward goes
    /// through the floor.</para>
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

        /// <summary>How unevenly this sender's packets actually land, smoothed. Burst delivery
        /// shows up here and nowhere else.</summary>
        public double Jitter { get; private set; }

        /// <summary>How far behind the newest snapshot playback is running, in seconds.</summary>
        public double Delay { get; private set; }

        public Transform Body { get; private set; }
        public Transform Head { get; private set; }
        public Transform HandL { get; private set; }
        public Transform HandR { get; private set; }

        private readonly RigDriver _driver = new RigDriver(remote: true);
        private readonly List<PoseSnapshot> _snaps = new List<PoseSnapshot>(MaxSnapshots);
        private GameObject _holder;

        private double _play;            // playback clock, in the sender's time
        private bool _playing;
        private float _lastArrival = -1f;

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
            float arrival = Time.unscaledTime;
            LastHeard = arrival;

            // A server clock that jumps backwards a long way (it is a wrapping 32-bit
            // millisecond counter underneath) would otherwise strand every new snapshot behind
            // the old ones and freeze the ragdoll until it timed out.
            if (_snaps.Count > 0 && s.Time < _snaps[_snaps.Count - 1].Time - 60.0)
            {
                _snaps.Clear();
                _playing = false;
            }

            int i = _snaps.Count;
            while (i > 0 && _snaps[i - 1].Time > s.Time) i--;
            if (i > 0 && _snaps[i - 1].Time == s.Time) return;   // duplicate

            // Only in-order arrivals say anything about the send rate; a late packet slotting in
            // behind the newest would read as a tiny gap and drag the estimate down.
            if (i == _snaps.Count && i > 0)
            {
                double gap = s.Time - _snaps[i - 1].Time;
                if (gap > 0.01 && gap < 0.5) Interval = Interval * 0.8 + gap * 0.2;

                // How late this packet was compared with the one before it, in arrival terms.
                // Photon flushes its queue at a fixed rate, so a sender faster than that flush
                // arrives in bursts: two packets together, then a longer gap. That shows here.
                if (_lastArrival >= 0f)
                {
                    double arrivalGap = arrival - _lastArrival;
                    double off = System.Math.Abs(arrivalGap - Interval);
                    if (off < 1.0) Jitter = Jitter * 0.85 + off * 0.15;
                }
            }
            _lastArrival = arrival;
            _snaps.Insert(i, s);

            while (_snaps.Count > MaxSnapshots) _snaps.RemoveAt(0);
        }

        /// <summary>
        /// Poses the avatar for this frame. Called just before GT's IK reads its targets.
        /// </summary>
        /// <param name="dt">Seconds since the last frame this was called on.</param>
        public void Apply(float dt)
        {
            if (_snaps.Count == 0 || Rig == null) return;

            Tick(dt);
            Sample(_play);

            if (!Started)
            {
                // Begin measures the rig against itself, so it does not matter that the four
                // stand-ins were only filled in a line ago - but they must exist before Tick.
                _driver.Begin(Rig, null);
                Started = true;
                Plugin.Log.LogInfo("[Net] showing ragdoll for actor " + Actor + " (" + Name() + ")" +
                                   " | " + (1.0 / System.Math.Max(0.001, Interval)).ToString("0") + "Hz");
            }

            _driver.Tick(this);
        }

        /// <summary>
        /// Advances the playback clock: forward by real time, and gently toward where the buffer
        /// says it should be. Correcting the speed instead of the position is the whole point -
        /// setting the clock every frame is what makes a stuttering network clock visible.
        /// </summary>
        private void Tick(float dt)
        {
            double newest = _snaps[_snaps.Count - 1].Time;

            // Enough buffer for the measured unevenness, and never less than one interval, so
            // there is a snapshot on each side of the clock to curve between.
            double want = System.Math.Max(Interval * 1.25 + Jitter * 2.0, Interval * 1.1);
            want *= Mathf.Clamp(RagdollConfig.NetSmoothness.Value, 0.25f, 4f);
            Delay = System.Math.Min(want, 0.6);

            double target = newest - Delay;

            if (!_playing)
            {
                _play = target;
                _playing = true;
                return;
            }

            _play += dt;
            double error = target - _play;

            // A long way out means a rejoin, a clock resync or a stall - start again rather than
            // creep there over ten seconds.
            if (System.Math.Abs(error) > 0.5)
            {
                _play = target;
                return;
            }

            // Otherwise pull at most 15% off the speed of time. Enough to close a tenth of a
            // second inside a second; far too small to see on a tumbling body.
            _play += Mathf.Clamp((float)error * 0.5f, -0.15f, 0.15f) * dt;
        }

        // ------------------------------------------------------------------ sampling

        private void Sample(double t)
        {
            int last = _snaps.Count - 1;

            if (t >= _snaps[last].Time)
            {
                Extrapolate(t);
                return;
            }

            if (t <= _snaps[0].Time)
            {
                Set(_snaps[0], _snaps[0], _snaps[0], _snaps[0], 0f);
                return;
            }

            int i = 1;
            while (i < last && _snaps[i].Time < t) i++;

            // The four the curve needs: one before, the two either side, one after.
            var b = _snaps[i - 1];
            var c = _snaps[i];
            var a = _snaps[i - 2 >= 0 ? i - 2 : i - 1];
            var d = _snaps[i + 1 <= last ? i + 1 : i];

            double span = c.Time - b.Time;
            float k = span > 1e-6 ? (float)((t - b.Time) / span) : 1f;
            Set(a, b, c, d, k);
        }

        /// <summary>
        /// Past the newest snapshot: carry on the way the body was moving, easing off, and stop
        /// when the guess gets too old to trust. Rotations are held rather than spun on - a
        /// mis-guessed spin is far more obvious than a pause.
        /// </summary>
        private void Extrapolate(double t)
        {
            int last = _snaps.Count - 1;
            var newest = _snaps[last];
            double ahead = t - newest.Time;
            float limit = Mathf.Max(0f, RagdollConfig.NetExtrapolate.Value) * 0.001f;

            if (last == 0 || ahead <= 0 || limit <= 0f)
            {
                Set(newest, newest, newest, newest, 0f);
                return;
            }

            var prev = _snaps[last - 1];
            double span = newest.Time - prev.Time;
            if (span < 1e-6)
            {
                Set(newest, newest, newest, newest, 0f);
                return;
            }

            // The guess slows as it ages and stops at the allowance, rather than running on at
            // full speed and then snapping back: distance = limit * (x - x*x/2), which grows
            // fastest at first and flattens exactly at the limit.
            float x = Mathf.Clamp01((float)(ahead / limit));
            float ex = limit * (x - x * x * 0.5f);

            float inv = (float)(1.0 / span);
            Body.SetPositionAndRotation(newest.BodyPos + (newest.BodyPos - prev.BodyPos) * inv * ex, newest.BodyRot);
            Head.SetPositionAndRotation(newest.HeadPos + (newest.HeadPos - prev.HeadPos) * inv * ex, newest.HeadRot);
            HandL.SetPositionAndRotation(newest.HandLPos + (newest.HandLPos - prev.HandLPos) * inv * ex, newest.HandLRot);
            HandR.SetPositionAndRotation(newest.HandRPos + (newest.HandRPos - prev.HandRPos) * inv * ex, newest.HandRRot);
        }

        private void Set(PoseSnapshot a, PoseSnapshot b, PoseSnapshot c, PoseSnapshot d, float k)
        {
            Body.SetPositionAndRotation(Curve(a.BodyPos, b.BodyPos, c.BodyPos, d.BodyPos, k),
                                        Squad(a.BodyRot, b.BodyRot, c.BodyRot, d.BodyRot, k));
            Head.SetPositionAndRotation(Curve(a.HeadPos, b.HeadPos, c.HeadPos, d.HeadPos, k),
                                        Squad(a.HeadRot, b.HeadRot, c.HeadRot, d.HeadRot, k));
            HandL.SetPositionAndRotation(Curve(a.HandLPos, b.HandLPos, c.HandLPos, d.HandLPos, k),
                                         Squad(a.HandLRot, b.HandLRot, c.HandLRot, d.HandLRot, k));
            HandR.SetPositionAndRotation(Curve(a.HandRPos, b.HandRPos, c.HandRPos, d.HandRPos, k),
                                         Squad(a.HandRRot, b.HandRRot, c.HandRRot, d.HandRRot, k));
        }

        /// <summary>
        /// Catmull-Rom through the middle two points. It passes through every snapshot exactly -
        /// nothing is invented - but arrives and leaves along the direction the body was actually
        /// travelling, which is what removes the corners.
        /// </summary>
        private static Vector3 Curve(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2f * b) +
                           (-a + c) * t +
                           (2f * a - 5f * b + 4f * c - d) * t2 +
                           (-a + 3f * b - 3f * c + d) * t3);
        }

        /// <summary>Catmull-Rom's equivalent for rotations: slerp between the middle pair, bent
        /// by control rotations worked out from their neighbours.</summary>
        private static Quaternion Squad(Quaternion a, Quaternion b, Quaternion c, Quaternion d, float t)
        {
            a = Align(b, a);
            c = Align(b, c);
            d = Align(c, d);

            var s1 = Control(a, b, c);
            var s2 = Control(b, c, d);
            return Quaternion.Slerp(Quaternion.Slerp(b, c, t), Quaternion.Slerp(s1, s2, t), 2f * t * (1f - t));
        }

        /// <summary>Quaternions are double covers: q and -q are the same rotation, and blending
        /// between the two forms takes the long way round.</summary>
        private static Quaternion Align(Quaternion to, Quaternion q)
        {
            float dot = to.x * q.x + to.y * q.y + to.z * q.z + to.w * q.w;
            return dot < 0f ? new Quaternion(-q.x, -q.y, -q.z, -q.w) : q;
        }

        private static Quaternion Control(Quaternion prev, Quaternion q, Quaternion next)
        {
            var inv = Quaternion.Inverse(q);
            var l1 = Log(inv * prev);
            var l2 = Log(inv * next);
            var e = new Vector3(-(l1.x + l2.x) * 0.25f, -(l1.y + l2.y) * 0.25f, -(l1.z + l2.z) * 0.25f);
            return q * Exp(e);
        }

        private static Vector3 Log(Quaternion q)
        {
            float v = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
            if (v < 1e-6f) return Vector3.zero;
            float angle = Mathf.Atan2(v, Mathf.Clamp(q.w, -1f, 1f));
            float s = angle / v;
            return new Vector3(q.x * s, q.y * s, q.z * s);
        }

        private static Quaternion Exp(Vector3 v)
        {
            float angle = v.magnitude;
            if (angle < 1e-6f) return Quaternion.identity;
            float s = Mathf.Sin(angle) / angle;
            return new Quaternion(v.x * s, v.y * s, v.z * s, Mathf.Cos(angle));
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
            _playing = false;
        }

        private string Name()
        {
            try { return Rig != null && Rig.Creator != null ? Rig.Creator.NickName : "?"; }
            catch { return "?"; }
        }
    }
}
