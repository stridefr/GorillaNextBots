using System;
using System.Collections.Generic;
using UnityEngine;

namespace GorillaRagdoll.UI
{
    /// <summary>
    /// Lets another mod add a tab to the F4 menu.
    ///
    /// <para>The bridge mod owns settings this one knows nothing about - dust, ear ringing, the death
    /// overlay - and a second menu with its own key would mean two windows to find and two keys to
    /// remember. So the bridge registers a tab here instead, and it draws with the same widgets as
    /// every other tab: <see cref="RagdollMenu.Bool"/>, <see cref="RagdollMenu.Slider"/> and the
    /// rest are public for exactly this.</para>
    ///
    /// <para>A tab that throws is caught and reported inside the tab. It must not take the whole menu
    /// down with it, least of all the ragdoll's own tabs.</para>
    /// </summary>
    public static class MenuTabs
    {
        private sealed class Tab
        {
            public string Name;
            public Action Draw;
        }

        private static readonly List<Tab> Tabs = new List<Tab>(4);

        /// <summary>Changes whenever a tab is added, so the menu knows to rebuild its toolbar.</summary>
        public static int Version { get; private set; }

        /// <summary>Adds a tab, or replaces the one with the same name.</summary>
        public static void Add(string name, Action draw)
        {
            if (string.IsNullOrEmpty(name) || draw == null) return;

            foreach (var t in Tabs)
            {
                if (t.Name == name) { t.Draw = draw; Version++; return; }
            }
            Tabs.Add(new Tab { Name = name, Draw = draw });
            Version++;
        }

        public static int Count => Tabs.Count;

        public static string Name(int index) => index >= 0 && index < Tabs.Count ? Tabs[index].Name : "";

        internal static void Draw(int index)
        {
            if (index < 0 || index >= Tabs.Count) return;

            try
            {
                Tabs[index].Draw();
            }
            catch (ExitGUIException)
            {
                throw;   // how IMGUI unwinds a window that was just opened or closed; not an error
            }
            catch (Exception ex)
            {
                GUILayout.Label("<b>" + Tabs[index].Name + " tab failed</b>: " + ex.Message, RagdollMenu.Wrapped());
            }
        }
    }
}
