using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LethalAccess.Patches
{
    /// <summary>
    /// Registers the main menu as a real screen.
    ///
    /// It was the last surface still described only by the legacy path map, which knows a control's
    /// label but not what screen it belongs to, so every announcement there opened with the control
    /// name and no context ("Host, button, 1 of 5" rather than "Main Menu, Host, button, 1 of 5").
    ///
    /// The authored order here is deliberately identical to the group registered in
    /// LACore.RegisterUIElements. Both exist for now, and the registry wins for position while the
    /// path map still supplies text for anything the registry does not claim; keeping them in step
    /// means the two can never disagree about what "3 of 5" refers to. If the group is ever removed,
    /// nothing here changes.
    /// </summary>
    [HarmonyPatch(typeof(MenuManager))]
    public static class MainMenuAccessPatch
    {
        private const string ScreenId = "MainMenu";

        /// <summary>Guards against re-registering for the same MenuManager on every Start.</summary>
        private static MenuManager registeredFor;

        [HarmonyPatch("Start")]
        [HarmonyPostfix]
        public static void PostfixStart(MenuManager __instance)
        {
            // isInitScene is the pre-init copy of MenuManager; it has no main-button panel and the
            // launch-options screen owns that scene.
            if (__instance == null || __instance.isInitScene)
            {
                return;
            }

            if (registeredFor == __instance)
            {
                return;
            }

            if (__instance.menuButtons == null)
            {
                Debug.LogError("[LethalAccess] MenuManager.menuButtons is null, so the main menu cannot be " +
                               "registered as a screen. Its controls will still be announced from the path " +
                               "map, but without the screen name.");
                return;
            }

            Register(__instance);
        }

        private static void Register(MenuManager m)
        {
            MenuManager captured = m;

            var d = new UIScreenDescriptor();
            d.Id = ScreenId;

            // Component reference, never GameObject.Find: the panel is deactivated whenever a
            // sub-screen (host settings, server list) is open, and Find cannot see it then.
            d.ResolveRoot = delegate
            {
                MenuManager live = captured;
                return (live != null && live.menuButtons != null) ? live.menuButtons.transform : null;
            };

            // One section, so the announcement prefix falls back to the title, which is what the
            // user calls this screen. A section name like "Buttons" would be meaningless spoken.
            d.Title = delegate { return "Main Menu"; };

            var entries = new List<UIEntry>();
            entries.Add(new UIEntry("HostButton", "Host", "button"));
            entries.Add(new UIEntry("JoinACrew", "Join a crew", "button"));
            entries.Add(new UIEntry("SettingsButton", "Settings", "button"));
            entries.Add(new UIEntry("Credits", "Credits", "button"));
            entries.Add(new UIEntry("QuitButton", "Quit", "button"));

            // Present on the main-buttons panel but only active on the LAN path or after a join
            // code is entered. Optional so their absence is not reported as a broken registration.
            var startLan = new UIEntry("StartLAN", "Join LAN session", "button");
            startLan.Optional = true;
            entries.Add(startLan);

            var joinCode = new UIEntry("JoinCode", "Room code", "input field");
            joinCode.Optional = true;
            entries.Add(joinCode);

            d.Sections = new List<UISection> { new UISection("Main Menu", entries.ToArray()) };

            UIScreenRegistry.Register(d);
            registeredFor = m;
        }
    }
}
