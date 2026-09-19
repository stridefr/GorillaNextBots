using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace NextBots.Recon
{
    /// <summary>
    /// Read-only diagnostic pass. Answers the two architectural forks the mod was designed around:
    ///   1. Does a stock (non-custom) map have a GameEntityManager + GameAgentManager we can
    ///      ride for replication (Option A), or must we build our own channel (Option B)?
    ///   2. Does a stock map have a NavMesh to path on?
    /// Spawns nothing, sends nothing, patches nothing. F8 dumps; it also auto-dumps a few
    /// seconds after each scene settles so we capture every zone we visit.
    /// </summary>
    public class ReconDump : MonoBehaviour
    {
        public static KeyCode DumpKey = KeyCode.F8;

        private static readonly FieldInfo FactoryField =
            typeof(GameEntityManager).GetField("itemPrefabFactory", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo ZoneComponentsField =
            typeof(GameEntityManager).GetField("zoneComponents", BindingFlags.Instance | BindingFlags.NonPublic);

        private string _outDir;
        private int _dumpIndex;

        private void Awake()
        {
            _outDir = Path.Combine(Paths.PluginPath, "NextBots");
            Directory.CreateDirectory(_outDir);
            SceneManager.sceneLoaded += OnSceneLoaded;
            Plugin.Log.LogInfo("[Recon] ready. Press " + DumpKey + " to dump. Output -> " + _outDir);
            StartCoroutine(DelayedDump("startup", 8f));
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            StartCoroutine(DelayedDump("scene:" + scene.name, 5f));
        }

        private IEnumerator DelayedDump(string reason, float delay)
        {
            yield return new WaitForSeconds(delay);
            Dump(reason);
        }

        private void Update()
        {
            if (Input.GetKeyDown(DumpKey)) Dump("manual");
            if (Input.GetKeyDown(BakeTestKey)) StartCoroutine(BakeTest());
        }

        /// <summary>
        /// Opt-in, because unlike everything else here it changes state: it adds NavMesh data
        /// to the world. Still local-only - nothing is spawned and nothing is sent.
        /// </summary>
        public static KeyCode BakeTestKey = KeyCode.F9;

        private IEnumerator BakeTest()
        {
            Plugin.Log.LogInfo("[Recon] bake test starting...");

            var host = new GameObject("ReconBakeTest");
            var provider = host.AddComponent<Runtime.NavMeshProvider>();

            var pos = LocalPlayerPos() ?? Vector3.zero;
            provider.Evaluate(pos, force: true);

            var started = Time.realtimeSinceStartup;
            while (provider.CurrentMode == Runtime.NavMeshProvider.Mode.Unknown &&
                   Time.realtimeSinceStartup - started < 30f)
                yield return null;

            Plugin.Log.LogInfo("[Recon] bake test result: " + provider.Describe());
            Dump("baketest_" + provider.CurrentMode);

            // Leave the baked data in place so the dump's NavMesh section reflects it, but
            // note it in the log so it is not mistaken for a shipped mesh.
            Plugin.Log.LogInfo("[Recon] NOTE: baked NavMesh data left loaded for inspection. " +
                               "Restart the game to clear it.");
        }

        public void Dump(string reason)
        {
            var sb = new StringBuilder(16 * 1024);
            try
            {
                Header(sb, reason);
                DumpZones(sb);
                DumpEntityManagers(sb);
                DumpNavMesh(sb);
                DumpRigs(sb);
                DumpRenderingAssets(sb);
            }
            catch (Exception ex)
            {
                sb.AppendLine().AppendLine("!!! recon threw: " + ex);
            }

            var text = sb.ToString();
            var path = Path.Combine(_outDir, "recon-" + _dumpIndex.ToString("00") + "-" + Sanitize(reason) + ".txt");
            _dumpIndex++;
            try { File.WriteAllText(path, text); }
            catch (Exception ex) { Plugin.Log.LogError(ex); }
            Plugin.Log.LogInfo("[Recon] dumped '" + reason + "' -> " + path);
            Plugin.Log.LogInfo(text);
        }

        // ---------------------------------------------------------------- sections

        private static void Header(StringBuilder sb, string reason)
        {
            sb.AppendLine("=========================================================");
            sb.AppendLine(" NextBots recon dump   reason=" + reason + "   t=" + Time.realtimeSinceStartup.ToString("0.0") + "s");
            sb.AppendLine("=========================================================");
            sb.AppendLine("Unity            : " + Application.unityVersion);
            sb.AppendLine("Venue            : " + Runtime.Venue.Describe());
            sb.AppendLine("Spawning allowed : " + (Runtime.Venue.CanSpawn ? "yes" : "no - " + Runtime.Venue.BlockedReason));
            sb.AppendLine("In room          : " + SafeInRoom());
            sb.AppendLine("Room name        : " + SafeRoomName());
            sb.AppendLine("Is master client : " + SafeIsMaster());
            sb.AppendLine();
        }

        private static void DumpZones(StringBuilder sb)
        {
            sb.AppendLine("---- ZONES -------------------------------------------------");
            var zm = ZoneManagement.instance;
            if (zm == null) { sb.AppendLine("ZoneManagement.instance == null"); sb.AppendLine(); return; }

            sb.Append("activeZones: ");
            foreach (var z in zm.activeZones) sb.Append(z).Append(' ');
            sb.AppendLine();

            sb.Append("loadedScenes: ");
            try { foreach (var s in zm.GetAllLoadedScenes()) sb.Append(s).Append(' '); }
            catch (Exception ex) { sb.Append("<" + ex.GetType().Name + ">"); }
            sb.AppendLine();
            sb.AppendLine();
        }

        private static void DumpEntityManagers(StringBuilder sb)
        {
            sb.AppendLine("---- GAME ENTITY MANAGERS (Option A viability) --------------");
            sb.AppendLine("GameEntityManager.activeManager: " + Describe(GameEntityManager.activeManager));
            sb.AppendLine();

            var found = 0;
            foreach (GTZone zone in Enum.GetValues(typeof(GTZone)))
            {
                GameEntityManager mgr = null;
                try { mgr = GameEntityManager.GetManagerForZone(zone); }
                catch { /* ignore */ }
                if (mgr == null) continue;
                found++;
                DumpOneManager(sb, zone, mgr);
            }

            if (found == 0) sb.AppendLine("NO GameEntityManager EXISTS FOR ANY ZONE -> Option A is closed here.");
            sb.AppendLine("(managers found: " + found + ")");
            sb.AppendLine();
        }

        private static void DumpOneManager(StringBuilder sb, GTZone zone, GameEntityManager mgr)
        {
            sb.AppendLine("[" + zone + "] gameObject='" + mgr.name + "'");
            sb.AppendLine("    gameAgentManager : " + Describe(mgr.gameAgentManager) + "   <-- required to drive a GameAgent");
            sb.AppendLine("    ghostReactorMgr  : " + Describe(mgr.ghostReactorManager));
            sb.AppendLine("    customMapsMgr    : " + Describe(mgr.customMapsManager));
            sb.AppendLine("    superInfectionMgr: " + Describe(mgr.superInfectionManager));
            sb.AppendLine("    guard            : " + Describe(mgr.guard));
            sb.AppendLine("    boundsBoxCollider: " + Describe(mgr.boundsBoxCollider) +
                          (mgr.boundsBoxCollider != null ? " bounds=" + mgr.boundsBoxCollider.bounds : ""));

            SafeCall(sb, "    IsZoneActive     ", () => mgr.IsZoneActive().ToString());
            SafeCall(sb, "    IsZoneAuthority  ", () => mgr.IsZoneAuthority().ToString());
            SafeCall(sb, "    IsAuthority      ", () => mgr.IsAuthority().ToString());
            SafeCall(sb, "    HasAuthority     ", () => mgr.HasAuthority().ToString());
            SafeCall(sb, "    AuthorityPlayer  ", () =>
            {
                var p = mgr.GetAuthorityPlayer();
                return p == null ? "<null>" : p.NickName;
            });
            SafeCall(sb, "    entity count     ", () =>
            {
                var l = mgr.GetGameEntities();
                return l == null ? "<null>" : l.Count.ToString();
            });

            // Bounds test at the local player: RequestCreateItem refuses anything outside.
            var p2 = LocalPlayerPos();
            if (p2.HasValue)
                SafeCall(sb, "    boundsOK@player  ", () => mgr.IsPositionInManagerBounds(p2.Value).ToString());

            var zoneComps = ZoneComponentsField != null ? ZoneComponentsField.GetValue(mgr) as IList : null;
            sb.AppendLine("    zoneComponents   : " + (zoneComps == null ? "<unreadable>" : zoneComps.Count.ToString()) +
                          "   <-- each one can veto RequestCreateItem");
            if (zoneComps != null)
                foreach (var c in zoneComps) sb.AppendLine("        - " + (c == null ? "null" : c.GetType().Name));

            // The factory is the whole Option A story: entityTypeId == gameObject.name.GetStaticHash().
            var factory = FactoryField != null ? FactoryField.GetValue(mgr) as IDictionary : null;
            if (factory == null)
            {
                sb.AppendLine("    factory          : <unreadable>");
            }
            else
            {
                sb.AppendLine("    factory          : " + factory.Count + " prefabs registered");
                var shown = 0;
                foreach (DictionaryEntry e in factory)
                {
                    var go = e.Value as GameObject;
                    var hasAgent = go != null && go.GetComponentInChildren<GameAgent>(true) != null;
                    var hasEntity = go != null && go.GetComponentInChildren<GameEntity>(true) != null;
                    if (hasAgent || shown < 25)
                    {
                        sb.AppendLine("        " + e.Key + "  " + (go == null ? "<null>" : go.name) +
                                      (hasAgent ? "   [GameAgent]" : "") + (hasEntity ? " [GameEntity]" : ""));
                        shown++;
                    }
                }
                if (factory.Count > shown) sb.AppendLine("        ... " + (factory.Count - shown) + " more");
            }
            sb.AppendLine();
        }

        private static void DumpNavMesh(StringBuilder sb)
        {
            sb.AppendLine("---- NAVMESH (does a stock map give us anything to path on?) ----");
            try
            {
                var tri = NavMesh.CalculateTriangulation();
                var vcount = tri.vertices == null ? 0 : tri.vertices.Length;
                sb.AppendLine("CalculateTriangulation: vertices=" + vcount +
                              " indices=" + (tri.indices == null ? 0 : tri.indices.Length) +
                              " areas=" + (tri.areas == null ? 0 : tri.areas.Length));
                if (vcount > 0)
                {
                    var b = new Bounds(tri.vertices[0], Vector3.zero);
                    for (int i = 1; i < vcount; i++) b.Encapsulate(tri.vertices[i]);
                    sb.AppendLine("  navmesh bounds      : center=" + b.center + " size=" + b.size);
                }
            }
            catch (Exception ex) { sb.AppendLine("CalculateTriangulation threw: " + ex.Message); }

            var p = LocalPlayerPos();
            if (p.HasValue)
            {
                sb.AppendLine("local player pos      : " + p.Value);
                var radii = new[] { 1f, 3f, 10f, 30f, 100f };
                foreach (var r in radii)
                {
                    NavMeshHit hit;
                    var ok = NavMesh.SamplePosition(p.Value, out hit, r, NavMesh.AllAreas);
                    sb.AppendLine("  SamplePosition r=" + r + ": " +
                                  (ok ? "HIT at " + hit.position + " (dist " + Vector3.Distance(p.Value, hit.position).ToString("0.00") + ")"
                                      : "miss"));
                }
            }
            else sb.AppendLine("local player pos      : <no rig>");

            // Surfaces present in the loaded scenes, both namespaces the game ships.
            CountSurfaces(sb, "Unity.AI.Navigation.NavMeshSurface", typeof(Unity.AI.Navigation.NavMeshSurface));
            CountSurfaces(sb, "UnityEngine.AI.NavMeshSurface (NavMeshComponents)", typeof(UnityEngine.AI.NavMeshSurface));

            sb.AppendLine();
        }

        private static void CountSurfaces(StringBuilder sb, string label, Type t)
        {
            try
            {
                var objs = UnityEngine.Object.FindObjectsByType(t, FindObjectsInactive.Include, FindObjectsSortMode.None);
                sb.AppendLine(label + ": " + objs.Length);
                for (int i = 0; i < objs.Length && i < 12; i++)
                {
                    var c = objs[i] as Component;
                    if (c == null) continue;
                    sb.AppendLine("    - " + FullPath(c) + " (scene: " + c.gameObject.scene.name +
                                  ", active: " + c.gameObject.activeInHierarchy + ")");
                }
            }
            catch (Exception ex) { sb.AppendLine(label + ": <" + ex.GetType().Name + ">"); }
        }

        private static void DumpRigs(StringBuilder sb)
        {
            sb.AppendLine("---- RIGS ---------------------------------------------------");
            try
            {
                sb.AppendLine("VRRigCache.isInitialized: " + VRRigCache.isInitialized);
                var active = VRRigCache.ActiveRigs;
                sb.AppendLine("active rigs: " + (active == null ? 0 : active.Count));
                if (active != null)
                {
                    foreach (var rig in active)
                    {
                        if (rig == null) continue;
                        var head = rig.headConstraint;
                        sb.AppendLine("    - '" + rig.name + "' local=" + rig.isLocal +
                                      " actor=" + (rig.Creator == null ? "?" : rig.Creator.ActorNumber.ToString()) +
                                      " pos=" + rig.transform.position +
                                      " head=" + (head != null ? head.position.ToString() : "<null>") +
                                      " headFwd=" + (head != null ? head.forward.ToString() : "<null>"));
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("rig dump threw: " + ex.Message); }
            sb.AppendLine();
        }

        /// <summary>
        /// Shaders and fonts get stripped from a built player, so a mod cannot assume
        /// Shader.Find("Unlit/Color") resolves. Find out what actually survives before
        /// building any in-VR UI on it.
        /// </summary>
        private static void DumpRenderingAssets(StringBuilder sb)
        {
            sb.AppendLine("---- RENDERING ASSETS (for the in-VR panel) ------------------");

            var candidates = new[]
            {
                "Unlit/Color", "Unlit/Texture", "Sprites/Default", "UI/Default",
                "GUI/Text Shader", "Standard", "Legacy Shaders/Diffuse",
                "Universal Render Pipeline/Unlit", "Universal Render Pipeline/Lit",
                "TextMeshPro/Distance Field", "TextMeshPro/Mobile/Distance Field",
                "GorillaTag/UberShader"
            };
            foreach (var name in candidates)
            {
                Shader s = null;
                try { s = Shader.Find(name); } catch { /* ignore */ }
                sb.AppendLine("  Shader.Find(\"" + name + "\") : " + (s != null ? "OK" : "null"));
            }

            try
            {
                var fonts = Resources.FindObjectsOfTypeAll<TMPro.TMP_FontAsset>();
                sb.AppendLine("  TMP_FontAsset loaded     : " + fonts.Length);
                for (int i = 0; i < fonts.Length && i < 10; i++)
                    sb.AppendLine("      - " + fonts[i].name);
            }
            catch (Exception ex) { sb.AppendLine("  TMP_FontAsset scan       : <" + ex.GetType().Name + ">"); }

            try
            {
                var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                sb.AppendLine("  builtin LegacyRuntime.ttf: " + (f != null ? "OK" : "null"));
            }
            catch (Exception ex) { sb.AppendLine("  builtin LegacyRuntime.ttf: <" + ex.GetType().Name + ">"); }

            try
            {
                var fonts = Resources.FindObjectsOfTypeAll<Font>();
                sb.AppendLine("  Font loaded              : " + fonts.Length);
                for (int i = 0; i < fonts.Length && i < 10; i++)
                    sb.AppendLine("      - " + fonts[i].name);
            }
            catch (Exception ex) { sb.AppendLine("  Font scan                : <" + ex.GetType().Name + ">"); }

            sb.AppendLine();
        }

        // ---------------------------------------------------------------- helpers

        private static Vector3? LocalPlayerPos()
        {
            try
            {
                var tagger = GorillaTagger.Instance;
                if (tagger != null && tagger.offlineVRRig != null) return tagger.offlineVRRig.transform.position;
            }
            catch { /* ignore */ }
            return null;
        }

        private static void SafeCall(StringBuilder sb, string label, Func<string> f)
        {
            try { sb.AppendLine(label + ": " + f()); }
            catch (Exception ex) { sb.AppendLine(label + ": <" + ex.GetType().Name + ": " + ex.Message + ">"); }
        }

        private static string Describe(UnityEngine.Object o)
        {
            return o == null ? "<null>" : "'" + o.name + "'";
        }

        private static string FullPath(Component c)
        {
            if (c == null) return "<null>";
            var t = c.transform;
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        private static string SafeInRoom()
        {
            try { return Photon.Pun.PhotonNetwork.InRoom.ToString(); } catch { return "?"; }
        }

        private static string SafeRoomName()
        {
            try
            {
                var r = Photon.Pun.PhotonNetwork.CurrentRoom;
                return r == null ? "<none>" : r.Name;
            }
            catch { return "?"; }
        }

        private static string SafeIsMaster()
        {
            try { return Photon.Pun.PhotonNetwork.IsMasterClient.ToString(); } catch { return "?"; }
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            return sb.ToString();
        }
    }
}
