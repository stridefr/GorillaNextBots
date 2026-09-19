using System.Collections.Generic;
using NextBots.Config;
using NextBots.UI;
using UnityEngine;
using UnityEngine.AI;

namespace NextBots.Runtime
{
    /// <summary>
    /// Owns the live bots: spawns them, ticks their brains at a fixed rate, and cleans up.
    /// Also the <see cref="IBotDirector"/> the wrist panel drives.
    ///
    /// Brains tick at a fixed rate rather than per frame because the sightline raycasts are
    /// the expensive part, and decisions do not need 90 Hz. Bots are staggered across ticks
    /// so N bots do not all raycast on the same frame.
    /// </summary>
    public class BotManager : MonoBehaviour, IBotDirector
    {
        public NextBotSettings Settings = NextBotSettings.Active;
        public NavMeshProvider Nav;
        public Net.BotNetwork Net;

        private int _nextNetId = 1;

        /// <summary>Brain decisions per second.</summary>
        public float TickRate = 10f;

        private readonly List<NextBot> _bots = new List<NextBot>(8);
        private readonly WorldView _world = new WorldView();
        private float _tickAccum;

        public WorldView World => _world;
        public IReadOnlyList<NextBot> Bots => _bots;

        // ================================================================== IBotDirector

        public IReadOnlyList<string> SpawnTypes => BotSkins.Names;

        public int LiveCount => _bots.Count;

        public bool CanSpawn => Venue.CanSpawn;

        public string BlockedReason
        {
            get
            {
                var venue = Venue.BlockedReason;
                if (venue != null) return venue;
                if (Nav != null && !Nav.Ready && Nav.CurrentMode == NavMeshProvider.Mode.Unknown)
                    return "CHECKING NAVMESH...";
                return "";
            }
        }

        public bool TrySpawn(int typeIndex, Vector3 position, Quaternion rotation, out string message)
        {
            if (!Venue.CanSpawn)
            {
                message = Venue.BlockedReason;
                return false;
            }

            if (_bots.Count >= Mathf.Max(1, Settings.MaxBots))
            {
                message = "AT BOT LIMIT (" + Settings.MaxBots + ")";
                return false;
            }

            var skin = BotSkins.Get(typeIndex);
            if (skin == null)
            {
                message = "NO SKIN AT SLOT " + typeIndex;
                return false;
            }

            var bot = Create(skin, typeIndex, position, rotation);
            if (bot == null)
            {
                message = "SPAWN FAILED - SEE LOG";
                return false;
            }

            bot.NetId = _nextNetId++;
            _bots.Add(bot);

            if (Net != null) Net.SendSpawn(bot.NetId, typeIndex, skin.Name, bot.transform.position,
                                           bot.transform.eulerAngles.y);

            message = "SPAWNED " + skin.Name;
            Plugin.Log.LogInfo("[Bots] spawned '" + skin.Name + "' at " + position +
                               " (" + _bots.Count + " live, nav=" +
                               (Nav != null ? Nav.CurrentMode.ToString() : "?") + ")");
            return true;
        }

        public bool UndoLast(out string message)
        {
            for (int i = _bots.Count - 1; i >= 0; i--)
            {
                var bot = _bots[i];
                _bots.RemoveAt(i);
                if (bot == null) continue;
                var name = bot.SkinName;
                if (Net != null) Net.SendDespawn(bot.NetId);
                bot.Kill();
                message = "REMOVED " + name;
                return true;
            }
            message = "NOTHING TO UNDO";
            return false;
        }

        public int ClearAll()
        {
            var n = 0;
            for (int i = 0; i < _bots.Count; i++)
            {
                if (_bots[i] == null) continue;
                if (Net != null) Net.SendDespawn(_bots[i].NetId);
                _bots[i].Kill();
                n++;
            }
            _bots.Clear();
            if (n > 0) Plugin.Log.LogInfo("[Bots] cleared " + n);
            return n;
        }

        // ================================================================== spawning

        private NextBot Create(BotSkins.Skin skin, int skinIndex, Vector3 position, Quaternion rotation)
        {
            var go = new GameObject("NextBot_" + skin.Name);
            go.transform.SetPositionAndRotation(position, rotation);

            var bot = go.AddComponent<NextBot>();

            // Prefer a real NavMeshAgent. Adding one where there is no mesh under it spams
            // Unity errors every frame and moves nothing, so check first and fall back.
            var placed = false;
            if (Nav == null || Nav.Ready)
            {
                NavMeshHit hit;
                if (NavMesh.SamplePosition(position, out hit, 4f, NavMesh.AllAreas))
                {
                    go.transform.position = hit.position;

                    var agent = go.AddComponent<NavMeshAgent>();

                    // Match the mesh's agent type, or this agent can see no navmesh at all.
                    // Set while disabled so it re-places itself against the right surface.
                    if (Nav != null)
                    {
                        agent.enabled = false;
                        agent.agentTypeID = Nav.AgentTypeID;
                        agent.enabled = true;
                    }
                    // Match the shape the mesh was baked for. An agent fatter than the bake
                    // radius cannot fit through the gaps the mesh says exist - including
                    // stairwells - and quietly fails to find paths.
                    agent.radius = Nav != null ? Nav.AgentRadius : 0.3f;
                    agent.height = Nav != null ? Nav.AgentHeight : 1.6f;
                    agent.baseOffset = 0f;
                    agent.autoRepath = true;
                    // We cross links ourselves with a real ballistic arc; Unity's automatic
                    // traversal slides the agent along a straight line, which looks like the
                    // bot is being dragged across the gap on a wire.
                    agent.autoTraverseOffMeshLink = false;
                    // Bots have to get out of each other's way, or several chasing the same
                    // player converge onto one line and merge into a single moving blob.
                    agent.obstacleAvoidanceType = ObstacleAvoidanceType.GoodQualityObstacleAvoidance;

                    // Equal priorities deadlock - two agents each politely yielding forever.
                    // Distinct values give a deterministic winner at every standoff.
                    agent.avoidancePriority = 30 + (_nextNetId * 7) % 40;

                    // We drive facing ourselves in NextBot.Update. Leaving the agent's own
                    // rotation on as well means two things write transform.rotation every
                    // frame and fight, which reads as jitter.
                    agent.updateRotation = false;
                    agent.updatePosition = true;
                    bot.Agent = agent;
                    placed = true;
                }
            }

            if (!placed)
            {
                // Tier 3: steer. Dumber, but it moves, and the brain's stuck escalation is
                // what stops it looking broken.
                bot.Steering = go.AddComponent<SteeringLocomotor>();
                bot.Steering.Destination = position;
                bot.Steering.Warp(position);
            }

            bot.SpawnPosition = go.transform.position;
            bot.Initialise(Settings, skin, skinIndex);
            bot.IsAuthority = true;
            bot.OnCaught = OnBotCaughtPlayer;
            return bot;
        }

        // ================================================================== tick

        private readonly List<Vector3> _keepAlive = new List<Vector3>(8);

        private void Start()
        {
            // Keep the baked volume covering the bots, not just the player.
            if (Nav != null) Nav.GetKeepAlivePoints = KeepAlivePoints;
        }

        private List<Vector3> KeepAlivePoints()
        {
            _keepAlive.Clear();
            for (int i = 0; i < _bots.Count; i++)
                if (_bots[i] != null) _keepAlive.Add(_bots[i].Position);
            return _keepAlive;
        }

        private void Update()
        {
            // A bot in a room we are no longer allowed to act in must not keep hunting.
            // Guests hold whatever the host sent them; the venue rule governs who may
            // *create* bots, not whether you are allowed to see the host's.
            if (_bots.Count > 0 && !Venue.CanSpawn && (Net == null || Net.IsAuthority))
            {
                Plugin.Log.LogInfo("[Bots] venue no longer permits bots (" +
                                   Venue.BlockedReason + "); clearing.");
                ClearAll();
                return;
            }

            if (_bots.Count == 0) return;

            var interval = 1f / Mathf.Max(1f, TickRate);
            _tickAccum += Time.deltaTime;
            if (_tickAccum < interval) return;

            var dt = _tickAccum;
            _tickAccum = 0f;

            // Only our own bots need the navmesh; a guest's puppets still want their sound
            // updated while it is being checked.
            var navReady = Nav == null || Nav.CurrentMode != NavMeshProvider.Mode.Unknown;

            _world.Refresh();

            for (int i = _bots.Count - 1; i >= 0; i--)
            {
                var bot = _bots[i];
                if (bot == null || !bot.Alive) { _bots.RemoveAt(i); continue; }

                // Puppet: the host does the thinking, we only do what is local to our ears.
                if (!bot.IsAuthority) { bot.RemoteTick(_world, dt); continue; }

                if (navReady) bot.BrainTick(_world, dt);
            }
        }

        private void OnBotCaughtPlayer(NextBot bot, int actorNumber)
        {
            // Everyone hears about it - the death log and the sounds need that - and only the
            // victim's own client runs the effect, because nobody else can move their rig.
            var info = CatchEffects.Describe(bot, actorNumber);
            if (Net != null) Net.SendCaught(info);
            CatchEffects.Dispatch(info);
        }

        /// <summary>This client's bot with this network id, host's or puppet; null if none.</summary>
        public NextBot FindByNetId(int netId)
        {
            for (int i = 0; i < _bots.Count; i++)
                if (_bots[i] != null && _bots[i].NetId == netId) return _bots[i];
            return null;
        }

        // ================================================================== replication

        /// <summary>A bot the host has spawned. Visual only - no brain, no agent, no physics.</summary>
        public void SpawnRemote(int netId, int skinIndex, string skinName, Vector3 position, float yaw)
        {
            for (int i = 0; i < _bots.Count; i++)
                if (_bots[i] != null && _bots[i].NetId == netId) return;   // already have it

            // By name, not slot: two people's skins folders rarely hold the same files in the
            // same order, and a slot number would quietly show the wrong face.
            var byName = BotSkins.IndexOf(skinName);
            if (byName >= 0) skinIndex = byName;
            else
                Plugin.Log.LogWarning("[Bots] host spawned skin '" + skinName + "', which is not " +
                                      "in your skins folder; showing slot " + skinIndex + " instead.");

            var skin = BotSkins.Get(skinIndex);
            if (skin == null) return;

            var go = new GameObject("NextBot_Remote_" + skin.Name);
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));

            var bot = go.AddComponent<NextBot>();
            bot.NetId = netId;
            bot.IsAuthority = false;
            bot.SpawnPosition = position;
            bot.Initialise(Settings, skin, skinIndex);
            bot.ApplyNetworkState(position, yaw, 0);

            _bots.Add(bot);
            Plugin.Log.LogInfo("[Bots] remote bot " + netId + " ('" + skin.Name + "') appeared.");
        }

        public void ApplyRemoteState(int netId, Vector3 position, float yaw, byte state)
        {
            for (int i = 0; i < _bots.Count; i++)
            {
                var b = _bots[i];
                if (b == null || b.NetId != netId) continue;
                if (b.IsAuthority) return;         // ours; the host does not tell us about it
                b.ApplyNetworkState(position, yaw, state);
                return;
            }
        }

        public void DespawnRemote(int netId)
        {
            for (int i = _bots.Count - 1; i >= 0; i--)
            {
                var b = _bots[i];
                if (b == null || b.NetId != netId) continue;
                _bots.RemoveAt(i);
                b.Kill();
                return;
            }
        }

        /// <summary>Drop every bot the host gave us, keeping our own.</summary>
        public int ClearRemote()
        {
            var n = 0;
            for (int i = _bots.Count - 1; i >= 0; i--)
            {
                var b = _bots[i];
                if (b == null) { _bots.RemoveAt(i); continue; }
                if (b.IsAuthority) continue;
                _bots.RemoveAt(i);
                b.Kill();
                n++;
            }
            if (n > 0) Plugin.Log.LogInfo("[Bots] dropped " + n + " remote bot(s).");
            return n;
        }

        /// <summary>Re-announce every bot, for someone who has just joined.</summary>
        public void ResendAll()
        {
            if (Net == null) return;
            for (int i = 0; i < _bots.Count; i++)
            {
                var b = _bots[i];
                if (b == null || !b.IsAuthority) continue;
                Net.SendSpawn(b.NetId, b.SkinIndex, b.SkinName, b.transform.position, b.transform.eulerAngles.y);
            }
        }

        private void OnDestroy() => ClearAll();
    }
}
