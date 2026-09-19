using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

namespace NextBots.Runtime
{
    /// <summary>
    /// Answers "can the bots path here?" for whatever map we are in, escalating through:
    ///
    ///   1. <b>Existing</b>  - the map shipped a baked NavMesh under the player. Use it.
    ///   2. <b>Baked</b>     - bake one at runtime from the map's physics colliders.
    ///   3. <b>Steering</b>  - baking found nothing either; bots fall back to raycast steering.
    ///
    /// Uses <see cref="NavMeshBuilder"/> directly rather than a NavMeshSurface component. The
    /// component silently produces an empty mesh when it collects no geometry, which is
    /// exactly the failure we hit first time (a "successful" bake in 0.04s that yielded
    /// nothing usable). Driving the builder ourselves means we can count the collected
    /// sources and say what actually happened.
    /// </summary>
    public class NavMeshProvider : MonoBehaviour
    {
        public enum Mode { Unknown, Existing, Baked, Steering }

        public Mode CurrentMode { get; private set; } = Mode.Unknown;
        public bool Ready => CurrentMode == Mode.Existing || CurrentMode == Mode.Baked;
        public string Status { get; private set; } = "not evaluated";

        /// <summary>Extent of the initial bake around the player, in metres.</summary>
        public Vector3 BakeVolume = new Vector3(120f, 60f, 120f);

        /// <summary>
        /// How close to the edge of what is already baked the player may get before the
        /// covered area grows.
        /// </summary>
        public float EdgeMargin = 25f;

        /// <summary>
        /// Ceiling on the covered area. Bake cost grows with volume, so this bounds the worst
        /// case; beyond it the box starts sliding again rather than growing forever.
        /// </summary>
        public Vector3 MaxCoverage = new Vector3(320f, 120f, 320f);

        // Agent shape. A nextbot is roughly monke-sized; too large a radius and it refuses to
        // fit through GT's doorways and the mesh comes out empty in exactly the places we need.
        public float AgentRadius = 0.30f;
        public float AgentHeight = 1.60f;

        /// <summary>
        /// Step height. Has to comfortably clear a stair riser or the mesh simply stops at the
        /// bottom of every staircase and bots can never go up.
        /// </summary>
        public float AgentClimb = 0.90f;

        public float AgentSlope = 60f;

        /// <summary>
        /// Voxel size drives how much detail survives the bake. At 0.15 m a stair tread is
        /// only a voxel or two deep and steps get smeared into an unwalkable slope; 0.09 m
        /// resolves them. Costs bake time, but the bake is async and was running in ~0.1s.
        /// </summary>
        public float VoxelSize = 0.09f;

        public int TileSize = 128;

        /// <summary>How close to the player a bake must land to count as usable.</summary>
        public float UsableRadius = 8f;

        /// <summary>
        /// The agent type the mesh is baked for. Agents MUST be set to this same id.
        ///
        /// Agent type ids are content hashes, not indices - the project's default came out as
        /// 1479372276 here, not 0. Baking as type 0 while the agents defaulted to the project
        /// type meant every agent saw an empty world: SamplePosition still found the mesh
        /// (it ignores agent type) so everything looked healthy, but CalculatePath returned
        /// false and the bots never moved.
        /// </summary>
        public int AgentTypeID { get; private set; }

        private NavMeshData _data;
        private NavMeshDataInstance _instance;
        private Vector3 _bakedCenter;

        /// <summary>
        /// Everything baked so far, as one region.
        ///
        /// The first version re-centred a fixed box on the player, which meant each bake
        /// discarded the last - the mesh had no memory, bots standing in the old area lost
        /// the floor, and separate bakes came out as disconnected islands the pathfinder
        /// could not route between. Growing a single region instead keeps one connected
        /// surface, so a route from one end of a building to the other actually exists.
        /// </summary>
        private Bounds _covered;
        private bool _hasCoverage;
        private bool _busy;
        private Bounds _lastBakedBounds;
        private float _nextCheck;

        private readonly List<NavMeshBuildSource> _sources = new List<NavMeshBuildSource>(4096);
        private readonly List<NavMeshBuildMarkup> _markups = new List<NavMeshBuildMarkup>(0);

        public event Action<Mode> ModeChanged;

        /// <summary>
        /// Positions that must stay covered by the bake, whatever the player does - i.e. the
        /// live bots. The baked box follows the player, so without this a bot standing where
        /// the player used to be silently loses the ground under it and goes inert.
        /// </summary>
        public Func<List<Vector3>> GetKeepAlivePoints;

        /// <summary>
        /// Generates the jump/drop links after each bake. Without these the pathfinder has
        /// no idea the bot can leave the ground, so it can never plan a route over a gap.
        /// </summary>
        public NavLinkBuilder Links;

        /// <summary>
        /// Layers that plausibly hold world collision. Built by exclusion rather than by
        /// listing what we want: GT puts map geometry on several layers and a narrow mask is
        /// how the first attempt collected almost nothing.
        /// </summary>
        public static readonly int BakeMask = BuildBakeMask();

        private static int BuildBakeMask()
        {
            // Players, hands, cosmetics, UI and effects - never world collision.
            int[] exclude =
            {
                2,  // IgnoreRaycast
                5,  // UI
                7,  // GorillaEquipment
                8,  // GorillaBodyCollider
                10, // GorillaHand
                11, // GorillaTrigger
                12, // MetaReportScreen
                13, // GorillaHead
                14, // GorillaTagCollider
                16, // GorillaEquipmentContainer
                17, // LCKHide
                19, // FirstPersonOnly
                20, // GorillaParticle
                21, // GorillaCosmetics
                22, // MirrorOnly
                24, // GorillaHandSocket
                25, // GorillaCosmeticParticle
                27, // NoMirror
                28, // GorillaSlingshotCollider
                31  // Bake
            };
            var mask = 0;
            foreach (var layer in exclude) mask |= 1 << layer;
            return ~mask;
        }

        // ======================================================================

        public void Evaluate(Vector3 near, bool force = false)
        {
            if (_busy) return;
            if (!force && CurrentMode != Mode.Unknown && Time.time < _nextCheck) return;
            _nextCheck = Time.time + 2f;
            StartCoroutine(EvaluateRoutine(near, force));
        }

        private float _bakedSlope = -1f;
        private float _bakedStep = -1f;

        private void Update()
        {
            var p = LocalPlayerPosition();
            if (!p.HasValue) return;

            // Slope changes the mesh, not just how it is used, so it needs a fresh bake.
            var slope = Config.NextBotSettings.Active.MaxSlope;
            var step = Config.NextBotSettings.Active.StepHeight;
            if (CurrentMode != Mode.Unknown && !_busy &&
                (Mathf.Abs(slope - _bakedSlope) > 0.5f || Mathf.Abs(step - _bakedStep) > 0.02f))
            {
                Plugin.Log.LogInfo("[NavMesh] max slope changed " + _bakedSlope + " -> " + slope + "; re-baking.");
                _bakedSlope = slope;
                _bakedStep = step;
                Evaluate(p.Value, force: true);
                return;
            }

            if (CurrentMode == Mode.Unknown) { Evaluate(p.Value); return; }

            // Grow the covered region as the player approaches its edge, rather than
            // re-centring and throwing away what we already know.
            if (CurrentMode != Mode.Existing && !_busy && _hasCoverage && NeedsExpansion(p.Value))
            {
                Plugin.Log.LogInfo("[NavMesh] player near the edge of covered ground; expanding.");
                Evaluate(p.Value, force: true);
            }
        }

        private IEnumerator EvaluateRoutine(Vector3 near, bool force)
        {
            _busy = true;

            if (!force && HasNavMeshNear(near, UsableRadius))
            {
                SetMode(Mode.Existing, "map shipped a NavMesh");
                _busy = false;
                yield break;
            }

            yield return Bake(near);

            // Check against where the player is NOW, not where the bake was centred - they
            // move while it runs, and the first version compared against a stale point.
            var check = LocalPlayerPosition() ?? near;
            if (HasNavMeshNear(check, UsableRadius))
            {
                _bakedCenter = near;
                _covered = _lastBakedBounds;
                _hasCoverage = true;
                _bakedSlope = Config.NextBotSettings.Active.MaxSlope;
                _bakedStep = Config.NextBotSettings.Active.StepHeight;
                SetMode(Mode.Baked, "runtime bake succeeded");

                // The surface changed, so every link across it is stale.
                if (Links != null) Links.Rebuild(this, Config.NextBotSettings.Active);
            }
            else if (CurrentMode == Mode.Baked || CurrentMode == Mode.Existing)
            {
                // A rejected re-bake is not a reason to tear down working pathfinding; the
                // previous mesh is still loaded. Back off and retry rather than demote.
                _nextCheck = Time.time + 5f;
                Plugin.Log.LogInfo("[NavMesh] re-bake rejected; keeping " + CurrentMode + ".");
            }
            else
            {
                SetMode(Mode.Steering, "bake produced no surface near the player");
            }

            _busy = false;
        }

        /// <summary>Is the player close enough to the edge of the baked region to warrant growing it?</summary>
        private bool NeedsExpansion(Vector3 p)
        {
            var inner = _covered;
            inner.Expand(-EdgeMargin * 2f);      // Expand takes total size, hence the doubling
            return !inner.Contains(p);
        }

        public static bool HasNavMeshNear(Vector3 point, float radius)
        {
            NavMeshHit hit;
            return NavMesh.SamplePosition(point, out hit, radius, NavMesh.AllAreas);
        }

        // ======================================================================
        //  Baking
        // ======================================================================

        private IEnumerator Bake(Vector3 center)
        {
            // Start from what we already cover and grow it to take in the player and a
            // working margin, so previously explored ground stays walkable.
            Bounds bounds;
            if (_hasCoverage)
            {
                bounds = _covered;
                bounds.Encapsulate(new Bounds(center, BakeVolume));

                // Respect the ceiling: past it, slide the region instead of growing it.
                var size = bounds.size;
                if (size.x > MaxCoverage.x || size.y > MaxCoverage.y || size.z > MaxCoverage.z)
                {
                    bounds = new Bounds(center, Vector3.Min(size, MaxCoverage));
                    Plugin.Log.LogInfo("[NavMesh] coverage ceiling reached; recentring on the player.");
                }
            }
            else
            {
                bounds = new Bounds(center, BakeVolume);
            }

            // Grow the box to keep existing bots on the mesh, but cap the growth: an
            // unbounded box would balloon the bake cost every time a bot wandered off.
            if (GetKeepAlivePoints != null)
            {
                List<Vector3> keep = null;
                try { keep = GetKeepAlivePoints(); }
                catch (Exception ex) { Plugin.Log.LogWarning("[NavMesh] keep-alive points threw: " + ex.Message); }

                if (keep != null)
                {
                    var maxExtent = BakeVolume * 1.6f;
                    for (int i = 0; i < keep.Count; i++)
                    {
                        var grown = bounds;
                        grown.Encapsulate(keep[i]);
                        if (grown.size.x <= maxExtent.x && grown.size.y <= maxExtent.y &&
                            grown.size.z <= maxExtent.z)
                            bounds = grown;
                    }
                }
            }
            _lastBakedBounds = bounds;
            var started = Time.realtimeSinceStartup;

            // ---- collect. Count everything, because "0 sources" and "bake failed" are very
            //      different problems and the log has to distinguish them.
            _sources.Clear();
            try
            {
                NavMeshBuilder.CollectSources(bounds, BakeMask,
                    NavMeshCollectGeometry.PhysicsColliders, 0, _markups, _sources);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[NavMesh] CollectSources threw: " + ex);
                yield break;
            }

            Plugin.Log.LogInfo("[NavMesh] collected " + _sources.Count +
                               " collider sources in " + BakeVolume + " around " + center);

            if (_sources.Count == 0)
            {
                // Try everything before giving up - a slightly wrong mesh beats none, and the
                // count tells us whether the mask was the problem.
                try
                {
                    NavMeshBuilder.CollectSources(bounds, ~0,
                        NavMeshCollectGeometry.PhysicsColliders, 0, _markups, _sources);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("[NavMesh] CollectSources (all layers) threw: " + ex);
                    yield break;
                }

                Plugin.Log.LogWarning("[NavMesh] curated mask found nothing; all-layers found " +
                                      _sources.Count + ".");
                if (_sources.Count == 0) yield break;   // old mesh, if any, is left alone
            }

            // ---- build into a NEW instance, never over the live one.
            //
            // Baking in place means a re-bake that finds nothing destroys the surface the
            // bots are currently standing on - observed live: a catch teleports the player,
            // that triggers a re-bake, the re-bake fails, and working pathfinding collapses
            // to steering. So the new mesh has to prove itself before the old one goes.
            var settings = BuildSettings();
            NavMeshData candidate = null;
            NavMeshDataInstance candidateInstance = default;
            AsyncOperation op = null;

            try
            {
                candidate = new NavMeshData(settings.agentTypeID);
                candidateInstance = NavMesh.AddNavMeshData(candidate);
                op = NavMeshBuilder.UpdateNavMeshDataAsync(candidate, settings, _sources, bounds);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[NavMesh] build threw: " + ex);
                if (candidateInstance.valid) candidateInstance.Remove();
                yield break;
            }

            while (op != null && !op.isDone)
            {
                if (Time.realtimeSinceStartup - started > 25f)
                {
                    Plugin.Log.LogWarning("[NavMesh] bake exceeded 25s; discarding candidate.");
                    if (candidateInstance.valid) candidateInstance.Remove();
                    yield break;
                }
                yield return null;
            }

            // ---- accept or reject. Both meshes are loaded right now, so sample against the
            //      player and keep whichever leaves them standing on something.
            var check = LocalPlayerPosition() ?? center;
            if (!HasNavMeshNear(check, UsableRadius))
            {
                Plugin.Log.LogWarning("[NavMesh] new bake has no surface near the player; " +
                                      "discarding it and keeping the previous mesh.");
                if (candidateInstance.valid) candidateInstance.Remove();
                yield break;
            }

            if (_instance.valid) _instance.Remove();
            _data = candidate;
            _instance = candidateInstance;

            var elapsed = Time.realtimeSinceStartup - started;
            var verts = 0;
            try { var tri = NavMesh.CalculateTriangulation(); verts = tri.vertices?.Length ?? 0; }
            catch { /* diagnostic only */ }

            Plugin.Log.LogInfo("[NavMesh] bake accepted in " + elapsed.ToString("0.00") +
                               "s from " + _sources.Count + " sources; navmesh now has " +
                               verts + " vertices.");
        }

        private NavMeshBuildSettings BuildSettings()
        {
            NavMeshBuildSettings s;
            try { s = NavMesh.GetSettingsByIndex(0); }
            catch { s = default; }

            // Keep whatever id the project's first agent type actually has, and publish it
            // so the agents can be set to match. Do NOT force it to 0.
            AgentTypeID = s.agentTypeID;
            s.agentRadius = AgentRadius;
            s.agentHeight = AgentHeight;
            s.agentClimb = Config.NextBotSettings.Active.StepHeight;
            s.agentSlope = Config.NextBotSettings.Active.MaxSlope;
            s.minRegionArea = 1f;
            s.overrideVoxelSize = true;

            // Voxel count scales with volume, so a fixed size that is affordable for a 120m
            // box is not affordable for a 320m one. Coarsen as the region grows - detail
            // matters most close in, and stairs still resolve at ~0.13.
            var span = _hasCoverage ? Mathf.Max(_covered.size.x, _covered.size.z) : BakeVolume.x;
            var scale = Mathf.Clamp(span / 120f, 1f, 2.2f);
            s.voxelSize = VoxelSize * scale;
            s.overrideTileSize = true;
            s.tileSize = TileSize;
            return s;
        }

        private void SetMode(Mode mode, string why)
        {
            Status = why;
            if (CurrentMode == mode) return;
            CurrentMode = mode;
            Plugin.Log.LogInfo("[NavMesh] mode = " + mode + " (" + why + ")");
            try { ModeChanged?.Invoke(mode); }
            catch (Exception ex) { Plugin.Log.LogError(ex); }
        }

        public void Clear()
        {
            if (_instance.valid) _instance.Remove();
            _instance = default;
            _data = null;
            _hasCoverage = false;
            CurrentMode = Mode.Unknown;
            Status = "cleared";
        }

        private void OnDestroy() => Clear();

        private static Vector3? LocalPlayerPosition()
        {
            var tagger = GorillaTagger.Instance;
            if (tagger == null || tagger.offlineVRRig == null) return null;
            return tagger.offlineVRRig.transform.position;
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(CurrentMode.ToString().ToUpperInvariant()).Append(" - ").Append(Status);
            if (_hasCoverage)
                sb.Append("  covering ").Append(Mathf.RoundToInt(_covered.size.x))
                  .Append('x').Append(Mathf.RoundToInt(_covered.size.z)).Append('m');
            try
            {
                var tri = NavMesh.CalculateTriangulation();
                sb.Append("  (verts ").Append(tri.vertices != null ? tri.vertices.Length : 0).Append(')');
            }
            catch { /* not important enough to fail on */ }
            return sb.ToString();
        }
    }
}
