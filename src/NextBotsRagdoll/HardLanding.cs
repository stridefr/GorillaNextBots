using GorillaLocomotion;
using UnityEngine;

namespace NextBotsRagdoll
{
    /// <summary>
    /// Landing hard on your feet - or your hands - throws up the same dust a ragdoll does.
    ///
    /// <para><b>Why it is not part of the ragdoll.</b> The ragdoll's impact listener only exists
    /// while you are a ragdoll. Dropping off a roof as a normal gorilla and slamming into the floor
    /// is just as much a hard landing, and it is the one people do all the time, so it has its own
    /// detector on the player's body.</para>
    ///
    /// <para><b>How it tells a landing from a stop.</b> Each physics step it reads the vertical
    /// speed of the player's rigidbody. A landing is a fast fall that ends suddenly - the speed
    /// going from well below zero to nearly nothing in one step - which is not what happens when
    /// you slow down in the air, catch a branch or climb. Nothing is measured from the ground
    /// itself, so it is the same on any surface.</para>
    ///
    /// <para><b>Everyone sees it.</b> It goes through <see cref="ImpactSounds.OnLanding"/>, which
    /// puffs the dust here and sends the same message a ragdoll's impact does, so other players see
    /// and hear your landing the way they see and hear your ragdoll's.</para>
    /// </summary>
    public sealed class HardLanding : MonoBehaviour
    {
        /// <summary>A fall has to be at least this fast, in m/s, on the step before it stops.</summary>
        private const float MinFall = 3f;

        /// <summary>...and the speed has to drop by at least this fraction of it.</summary>
        private const float StoppedFraction = 0.5f;

        private float _lastVy;
        private bool _hasLast;
        private float _lastAt = -99f;

        private void FixedUpdate()
        {
            if (!BridgeConfig.HardLandingEnabled.Value || !BridgeConfig.DustEnabled.Value) { _hasLast = false; return; }

            var p = GTPlayer.Instance;
            var rb = p != null ? p.playerRigidBody : null;
            if (rb == null || rb.isKinematic) { _hasLast = false; return; }

            // A ragdoll has its own impacts, and the venue rules are the ragdoll mod's.
            var ctrl = GorillaRagdoll.Plugin.Controller;
            if ((ctrl != null && ctrl.IsRagdolled) || !GorillaRagdoll.Runtime.Venue.Allowed)
            {
                _hasLast = false;
                return;
            }

            float vy = rb.linearVelocity.y;
            bool had = _hasLast;
            float last = _lastVy;
            _lastVy = vy;
            _hasLast = true;
            if (!had) return;

            float fall = -last;
            if (fall < Mathf.Max(MinFall, BridgeConfig.HardLandingMinSpeed.Value)) return;
            if (vy - last < fall * StoppedFraction) return;
            if (Time.time - _lastAt < 0.4f) return;

            Vector3 at;
            if (!FindFloor(p, out at)) return;

            _lastAt = Time.time;
            if (ImpactSounds.Instance != null) ImpactSounds.Instance.OnLanding(at, fall);

            // A mild daze, and only for a fall well past the puff threshold.
            if (BridgeConfig.DazeOnHardImpacts.Value && Daze.Instance != null &&
                fall >= BridgeConfig.HardLandingMinSpeed.Value * 1.5f)
                Daze.Instance.Hit(0.3f);
            Plugin.Log.LogInfo("[Landing] hard landing at " + fall.ToString("0.0") + " m/s");
        }

        /// <summary>The floor under the player's feet, and only if it really is right there: a fall
        /// that ends against a wall, or in a hand-hold in mid-air, is not a landing.</summary>
        private static bool FindFloor(GTPlayer p, out Vector3 floor)
        {
            floor = Vector3.zero;

            Vector3 centre = p.transform.position;
            float feet = centre.y;
            try
            {
                if (p.bodyCollider != null)
                {
                    var b = p.bodyCollider.bounds;
                    centre = b.center;
                    feet = b.min.y;
                }
            }
            catch { /* fall back to the player's own position */ }

            int mask;
            try { mask = GTPlayer.LocomotionEnabledLayers; }
            catch { mask = ~0; }

            RaycastHit hit;
            if (!Physics.Raycast(centre, Vector3.down, out hit, 3f, mask, QueryTriggerInteraction.Ignore)) return false;
            if (hit.normal.y < 0.5f) return false;
            if (feet - hit.point.y > 0.7f) return false;

            floor = hit.point;
            return true;
        }
    }
}
