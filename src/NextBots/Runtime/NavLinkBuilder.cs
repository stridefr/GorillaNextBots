using System.Collections;
using System.Collections.Generic;
using NextBots.Config;
using UnityEngine;
using UnityEngine.AI;
// Both shipped navigation DLLs declare NavMeshLink; pick the modern one explicitly.
using NavLink = Unity.AI.Navigation.NavMeshLink;

namespace NextBots.Runtime
{
    /// <summary>
    /// Teaches the pathfinder that the bot can jump and fall.
    ///
    /// Unity's runtime NavMeshBuilder produces walkable surface only - it never generates
    /// off-mesh links. Without them A* has no idea a drop or a gap can be crossed, so a bot
    /// standing on a balcony above its target sees no route at all and either freezes or
    /// walks at the nearest wall. Giving the bot a jump does not help by itself: the *path*
    /// has to know the jump exists before it can plan through it.
    ///
    /// So after each bake we find the open edges of the mesh, probe outward and downward for
    /// somewhere to land, and drop a NavMeshLink across the gap. From then on the route
    /// planner will happily plan "run to this ledge, drop off, carry on" as a single path.
    ///
    /// Work is capped and spread across frames: a large bake has thousands of boundary edges
    /// and probing all of them in one frame would stall.
    /// </summary>
    public class NavLinkBuilder : MonoBehaviour
    {
        /// <summary>Upper bound on generated links, so a complex map cannot flood the scene.</summary>
        public int MaxLinks = 400;

        /// <summary>Minimum spacing between link start points, to avoid stacking dozens on one ledge.</summary>
        public float MinSpacing = 2.0f;

        /// <summary>Boundary edges to consider per frame.</summary>
        public int EdgesPerFrame = 250;

        private readonly List<GameObject> _links = new List<GameObject>(128);
        private Coroutine _running;

        public int LinkCount => _links.Count;

        public void Rebuild(NavMeshProvider nav, NextBotSettings cfg)
        {
            if (_running != null) StopCoroutine(_running);
            _running = StartCoroutine(BuildRoutine(nav, cfg));
        }

        public void Clear()
        {
            for (int i = 0; i < _links.Count; i++)
                if (_links[i] != null) Destroy(_links[i]);
            _links.Clear();
        }

        private void OnDestroy() => Clear();

        // ======================================================================

        private IEnumerator BuildRoutine(NavMeshProvider nav, NextBotSettings cfg)
        {
            Clear();
            if (cfg == null || (!cfg.JumpEnabled && !cfg.FallEnabled)) yield break;

            NavMeshTriangulation tri;
            try { tri = NavMesh.CalculateTriangulation(); }
            catch { yield break; }

            if (tri.vertices == null || tri.indices == null || tri.indices.Length < 3) yield break;

            var boundary = FindBoundaryEdges(tri);
            if (boundary.Count == 0) yield break;

            var started = Time.realtimeSinceStartup;
            var placed = new List<Vector3>(MaxLinks);
            var processed = 0;

            foreach (var e in boundary)
            {
                if (_links.Count >= MaxLinks) break;

                if (++processed % EdgesPerFrame == 0) yield return null;

                var from = e.Mid;

                // Do not stack links on top of each other.
                var tooClose = false;
                for (int i = 0; i < placed.Count; i++)
                {
                    if ((placed[i] - from).sqrMagnitude < MinSpacing * MinSpacing) { tooClose = true; break; }
                }
                if (tooClose) continue;

                Vector3 landing;
                bool oneWay;
                if (!TryFindLanding(from, e.Outward, cfg, out landing, out oneWay)) continue;

                CreateLink(from, landing, oneWay, nav);
                placed.Add(from);
            }

            Plugin.Log.LogInfo("[NavLinks] built " + _links.Count + " link(s) from " +
                               boundary.Count + " boundary edges in " +
                               (Time.realtimeSinceStartup - started).ToString("0.00") + "s");
            _running = null;
        }

        /// <summary>
        /// Somewhere to land beyond this edge: first a drop straight off it, then a gap to
        /// jump across, then a ledge to climb onto. Nearest option wins, so a bot prefers
        /// stepping down to leaping.
        /// </summary>
        private static bool TryFindLanding(Vector3 from, Vector3 outward, NextBotSettings cfg,
                                           out Vector3 landing, out bool oneWay)
        {
            landing = Vector3.zero;
            oneWay = true;

            var reach = Mathf.Max(cfg.JumpMaxDistance, 1.5f);

            for (var d = 0.8f; d <= reach; d += 0.7f)
            {
                var ahead = from + outward * d;

                // --- drop down: a one-way fall off the edge.
                if (cfg.FallEnabled)
                {
                    NavMeshHit below;
                    if (NavMesh.SamplePosition(ahead + Vector3.down * (cfg.LedgeMaxDrop * 0.5f),
                            out below, cfg.LedgeMaxDrop * 0.6f, NavMesh.AllAreas))
                    {
                        var drop = from.y - below.position.y;
                        if (drop > cfg.LedgeMinDrop && drop <= cfg.LedgeMaxDrop &&
                            Clear(from, below.position))
                        {
                            landing = below.position;
                            oneWay = true;         // it cannot climb back up the same way
                            return true;
                        }
                    }
                }

                if (!cfg.JumpEnabled) continue;

                // --- across a gap, or up onto a ledge. Both are jumpable both ways.
                NavMeshHit across;
                if (NavMesh.SamplePosition(ahead, out across, 0.8f, NavMesh.AllAreas))
                {
                    var rise = across.position.y - from.y;
                    var gap = Vector3.Distance(new Vector3(from.x, 0f, from.z),
                                               new Vector3(across.position.x, 0f, across.position.z));

                    // Ignore anything we could simply walk to.
                    if (gap < 1.0f) continue;
                    if (rise > cfg.ClimbMaxHeight) continue;
                    if (rise < -cfg.LedgeMaxDrop) continue;

                    if (Clear(from, across.position))
                    {
                        landing = across.position;
                        oneWay = rise < -cfg.LedgeMinDrop;   // a big drop is one-way
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Nothing solid between the two ends, so the link is over a gap and not through a wall.</summary>
        private static bool Clear(Vector3 a, Vector3 b)
        {
            var lift = Vector3.up * 0.4f;
            return !Physics.Linecast(a + lift, b + lift,
                NavMeshProvider.BakeMask, QueryTriggerInteraction.Ignore);
        }

        private void CreateLink(Vector3 from, Vector3 to, bool oneWay, NavMeshProvider nav)
        {
            var go = new GameObject("NextBotsNavLink");
            go.transform.SetParent(transform, false);
            go.transform.position = from;
            go.hideFlags = HideFlags.HideAndDontSave;

            var link = go.AddComponent<NavLink>();
            link.startPoint = Vector3.zero;                       // local to this transform
            link.endPoint = go.transform.InverseTransformPoint(to);
            link.width = 0.6f;
            link.bidirectional = !oneWay;
            link.autoUpdate = false;
            if (nav != null) link.agentTypeID = nav.AgentTypeID;

            _links.Add(go);
        }

        // ---------------------------------------------------------------- edges

        private struct Edge
        {
            public Vector3 Mid;
            public Vector3 Outward;
        }

        /// <summary>
        /// Open edges of the navmesh - those belonging to exactly one triangle. These are the
        /// ledges, gap sides and doorway thresholds: every place the walkable surface stops.
        /// </summary>
        private static List<Edge> FindBoundaryEdges(NavMeshTriangulation tri)
        {
            var counts = new Dictionary<long, int>(tri.indices.Length);
            var sample = new Dictionary<long, Edge>(tri.indices.Length);

            for (int t = 0; t + 2 < tri.indices.Length; t += 3)
            {
                var i0 = tri.indices[t];
                var i1 = tri.indices[t + 1];
                var i2 = tri.indices[t + 2];

                AddEdge(counts, sample, tri, i0, i1, i2);
                AddEdge(counts, sample, tri, i1, i2, i0);
                AddEdge(counts, sample, tri, i2, i0, i1);
            }

            var result = new List<Edge>(256);
            foreach (var kv in counts)
                if (kv.Value == 1) result.Add(sample[kv.Key]);

            return result;
        }

        private static void AddEdge(Dictionary<long, int> counts, Dictionary<long, Edge> sample,
                                    NavMeshTriangulation tri, int a, int b, int opposite)
        {
            // Key on quantised positions, not indices: the triangulation repeats vertices, so
            // index pairs would report shared edges as boundaries.
            var key = EdgeKey(tri.vertices[a], tri.vertices[b]);

            int n;
            counts.TryGetValue(key, out n);
            counts[key] = n + 1;
            if (n > 0) return;

            var pa = tri.vertices[a];
            var pb = tri.vertices[b];
            var mid = (pa + pb) * 0.5f;

            // Outward = away from the triangle's third corner, flattened.
            var outward = mid - tri.vertices[opposite];
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.0001f) outward = Vector3.forward;

            sample[key] = new Edge { Mid = mid, Outward = outward.normalized };
        }

        private static long EdgeKey(Vector3 a, Vector3 b)
        {
            var ka = Quantise(a);
            var kb = Quantise(b);
            return ka < kb ? (ka * 31 + kb) : (kb * 31 + ka);
        }

        private static long Quantise(Vector3 v)
        {
            // ~1cm buckets, packed. Enough to match coincident vertices without merging
            // genuinely different ones.
            long x = Mathf.RoundToInt(v.x * 100f);
            long y = Mathf.RoundToInt(v.y * 100f);
            long z = Mathf.RoundToInt(v.z * 100f);
            return (x * 73856093L) ^ (y * 19349663L) ^ (z * 83492791L);
        }
    }
}
