using System.Collections.Generic;
using NextBots.Brain;
using UnityEngine;
using UnityEngine.AI;

namespace NextBots.Runtime
{
    /// <summary>
    /// The bots' shared view of the world: who is targetable, where their heads point, and
    /// what blocks line of sight. Sampled once per brain tick and shared by every bot, so
    /// N bots cost one rig sweep rather than N.
    /// </summary>
    public class WorldView : IWorldView
    {
        /// <summary>
        /// Exactly the mask GT's own agent visibility check uses
        /// (CustomMapsAIBehaviourController line 53), so our sightlines agree with the
        /// game's. Note the layer is named "Gorilla Object", with a space.
        /// </summary>
        public static readonly int SightMask = LayerMask.GetMask("Default", "Gorilla Object");

        private readonly List<PlayerSnapshot> _players = new List<PlayerSnapshot>(16);
        private readonly Dictionary<int, VelocitySample> _velocity = new Dictionary<int, VelocitySample>(16);

        public IReadOnlyList<PlayerSnapshot> Players => _players;
        public float Now => Time.time;

        /// <summary>Actors the bots must not target (e.g. nobody, currently).</summary>
        public readonly HashSet<int> Excluded = new HashSet<int>();

        private struct Trace
        {
            public float Time;
            public Vector3 Center;
        }

        // ~1.5s of history at the brain's tick rate is plenty for a reaction delay, and
        // bounded so a long session cannot grow it.
        private const int TraceCapacity = 24;
        private readonly Dictionary<int, Queue<Trace>> _history = new Dictionary<int, Queue<Trace>>(16);

        private struct VelocitySample
        {
            public Vector3 Position;
            public float Time;
            public Vector3 Velocity;
        }

        /// <summary>Re-sample every rig. Call once per brain tick, before ticking any bot.</summary>
        public void Refresh()
        {
            _players.Clear();

            var rigs = VRRigCache.ActiveRigs;
            if (rigs == null) return;

            var now = Time.time;

            for (int i = 0; i < rigs.Count; i++)
            {
                var rig = rigs[i];
                if (rig == null || !rig.isActiveAndEnabled) continue;

                var actor = ActorOf(rig);
                if (actor < 0 || Excluded.Contains(actor)) continue;
                if (Lives.IsGhost(actor)) continue;   // out of lives: the bots do not see them

                var head = rig.headConstraint;
                var root = rig.transform.position;
                var headPos = head != null ? head.position : root + Vector3.up * 1.2f;
                var headFwd = head != null ? head.forward : rig.transform.forward;

                _players.Add(new PlayerSnapshot
                {
                    ActorNumber = actor,
                    Position = root,
                    Center = Vector3.Lerp(root, headPos, 0.5f),
                    HeadPosition = headPos,
                    HeadForward = headFwd,
                    Velocity = TrackVelocity(actor, root, now),
                    IsValid = true
                });

                RecordTrace(actor, Vector3.Lerp(root, headPos, 0.5f), now);
            }
        }

        /// <summary>
        /// Derived by differencing positions rather than read off the rig, so it works
        /// identically for the local player and for remote ones (whose velocity GT does not
        /// expose). Smoothed, because a raw frame delta on a networked rig is very noisy and
        /// would make the lead term jitter.
        /// </summary>
        private Vector3 TrackVelocity(int actor, Vector3 position, float now)
        {
            VelocitySample prev;
            if (!_velocity.TryGetValue(actor, out prev))
            {
                _velocity[actor] = new VelocitySample { Position = position, Time = now, Velocity = Vector3.zero };
                return Vector3.zero;
            }

            var dt = now - prev.Time;
            if (dt <= 0.0001f) return prev.Velocity;

            var instant = (position - prev.Position) / dt;

            // Discard teleports (respawn, stump warp) - a 200 m/s "velocity" would fling the
            // lead point across the map.
            if (instant.sqrMagnitude > 40f * 40f) instant = Vector3.zero;

            var smoothed = Vector3.Lerp(prev.Velocity, instant, Mathf.Clamp01(dt * 8f));

            _velocity[actor] = new VelocitySample { Position = position, Time = now, Velocity = smoothed };
            return smoothed;
        }

        private void RecordTrace(int actor, Vector3 center, float now)
        {
            Queue<Trace> q;
            if (!_history.TryGetValue(actor, out q))
            {
                q = new Queue<Trace>(TraceCapacity);
                _history[actor] = q;
            }

            q.Enqueue(new Trace { Time = now, Center = center });
            while (q.Count > TraceCapacity) q.Dequeue();
        }

        public Vector3 DelayedCenter(int actorNumber, float delay, Vector3 fallback)
        {
            if (delay <= 0.001f) return fallback;

            Queue<Trace> q;
            if (!_history.TryGetValue(actorNumber, out q) || q.Count == 0) return fallback;

            var want = Time.time - delay;

            // Oldest sample at or before the wanted time; the queue is in order, so the last
            // one that still qualifies is the closest match without interpolating.
            var best = fallback;
            var found = false;
            foreach (var t in q)
            {
                if (t.Time > want) break;
                best = t.Center;
                found = true;
            }

            // History does not reach back that far yet - use the oldest we have rather than
            // snapping to the live position, so the delay eases in instead of popping.
            if (!found)
            {
                foreach (var t in q) { best = t.Center; break; }
            }

            return best;
        }

        public bool LineBlocked(Vector3 from, Vector3 to)
        {
            var delta = to - from;
            var dist = delta.magnitude;
            if (dist < 0.01f) return false;

            return Physics.Raycast(from, delta / dist, dist, SightMask, QueryTriggerInteraction.Ignore);
        }

        public Vector3? SampleNavigable(Vector3 near, float radius)
        {
            NavMeshHit hit;
            if (NavMesh.SamplePosition(near, out hit, radius, NavMesh.AllAreas)) return hit.position;
            return null;
        }

        public bool TryGetPlayer(int actor, out PlayerSnapshot snapshot)
        {
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].ActorNumber == actor)
                {
                    snapshot = _players[i];
                    return true;
                }
            }
            snapshot = default;
            return false;
        }

        public void ForgetPlayer(int actor)
        {
            _velocity.Remove(actor);
            _history.Remove(actor);
        }

        public static int ActorOf(VRRig rig)
        {
            // VRRig.OwningNetPlayer is [Obsolete] "unreliable"; Creator is the supported path.
            var creator = rig.Creator;
            if (creator != null) return creator.ActorNumber;

            // The local offline rig has no Creator until the room is joined.
            if (rig.isOfflineVRRig || rig.isLocal)
            {
                var local = NetworkSystem.Instance != null ? NetworkSystem.Instance.LocalPlayer : null;
                if (local != null) return local.ActorNumber;
            }
            return -1;
        }
    }
}
