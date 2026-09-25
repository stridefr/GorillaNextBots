using System.Collections.Generic;
using GorillaLocomotion;
using GorillaRagdoll.Net;
using UnityEngine;

namespace NextBotsRagdoll
{
    /// <summary>
    /// Dust for other players' ragdolls, made from the body this client is already showing.
    ///
    /// <para>Every remote ragdoll is posed here, every frame, from what its owner sends. So when one
    /// of its parts comes down fast and stops at the floor, that is a landing on this screen, at
    /// exactly the moment it is seen - and it needs nothing more from the network than the pose
    /// that is already arriving. The impact message the owner also sends still carries the sound;
    /// the dust comes from here instead, so it always lines up with the body you are watching and
    /// never doubles up.</para>
    /// </summary>
    public sealed class RemoteDust : MonoBehaviour
    {
        /// <summary>Faster than this (m/s) in one frame is a pose jump, not a fall.</summary>
        private const float MaxBelievable = 40f;

        /// <summary>No dust from a body for this long after it goes down: its first frames jump
        /// from where the player stood.</summary>
        private const float Settle = 0.3f;

        /// <summary>Per part, not per body: one landing is one puff.</summary>
        private const float PerBodyGap = 0.35f;

        private sealed class Part
        {
            public Vector3 Last;
            public bool Has;

            /// <summary>The fastest this part has been coming down lately, fading over a few tenths
            /// of a second. A remote body is smoothed between the poses it is sent, so a landing does
            /// not stop in one frame here - it stops over several. Comparing against a recent peak,
            /// not the frame before, is what catches it.</summary>
            public float Peak;
        }

        private sealed class Body
        {
            public RemoteRagdoll Source;
            public readonly Part[] Parts = { new Part(), new Part(), new Part(), new Part() };
            public float Since;
            public float LastPuff = -10f;
            public bool Seen;
        }

        private static int _logged;

        private readonly Dictionary<RemoteRagdoll, Body> _bodies = new Dictionary<RemoteRagdoll, Body>();
        private readonly List<RemoteRagdoll> _gone = new List<RemoteRagdoll>(4);

        private void LateUpdate()
        {
            if (!BridgeConfig.DustEnabled.Value || !BridgeConfig.DustForOthers.Value || Dust.Instance == null)
            {
                _bodies.Clear();
                return;
            }

            float dt = Time.deltaTime;
            if (dt <= 0.0001f) return;

            foreach (var b in _bodies.Values) b.Seen = false;

            foreach (var rd in RagdollNet.All)
            {
                if (rd == null || !rd.Started) continue;
                Body b;
                if (!_bodies.TryGetValue(rd, out b))
                {
                    b = new Body { Source = rd };
                    _bodies[rd] = b;
                }
                b.Seen = true;
                b.Since += dt;

                Check(b, 0, rd.Body, dt);
                Check(b, 1, rd.Head, dt);
                Check(b, 2, rd.HandL, dt);
                Check(b, 3, rd.HandR, dt);
            }

            _gone.Clear();
            foreach (var kv in _bodies) if (!kv.Value.Seen) _gone.Add(kv.Key);
            foreach (var k in _gone) _bodies.Remove(k);
        }

        private static void Check(Body b, int i, Transform t, float dt)
        {
            if (t == null) return;
            var p = b.Parts[i];
            var pos = t.position;
            if (!p.Has)
            {
                p.Last = pos;
                p.Has = true;
                return;
            }

            var vel = (pos - p.Last) / dt;
            p.Last = pos;

            // A jump no body makes is a pose reset or a teleport, not a fall.
            if (vel.magnitude > MaxBelievable) { p.Peak = 0f; return; }

            p.Peak = Mathf.Max(p.Peak * Mathf.Exp(-dt / 0.35f), -vel.y);

            if (b.Since < Settle || Time.time - b.LastPuff < PerBodyGap) return;
            if (p.Peak < BridgeConfig.DustMinSpeed.Value) return;

            // Most of that fall gone, next to the floor: it landed.
            if (vel.magnitude > p.Peak * 0.3f) return;
            if (!NearFloor(pos)) return;

            float speed = p.Peak;
            p.Peak = 0f;
            b.LastPuff = Time.time;
            Dust.Instance.Puff(pos, speed);
            if (_logged < 5)
            {
                _logged++;
                Plugin.Log.LogInfo("[Dust] another player's ragdoll landed at " + speed.ToString("0.0") + " m/s (actor " +
                                   b.Source.Actor + ")");
            }
        }

        private static bool NearFloor(Vector3 pos)
        {
            int mask;
            try { mask = GTPlayer.LocomotionEnabledLayers; }
            catch { mask = ~0; }
            return Physics.Raycast(pos + Vector3.up * 0.2f, Vector3.down, 0.9f, mask, QueryTriggerInteraction.Ignore);
        }
    }
}
