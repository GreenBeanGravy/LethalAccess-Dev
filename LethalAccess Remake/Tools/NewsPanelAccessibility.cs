using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LethalAccess
{
    /// <summary>
    /// Accessibility for the three modal panels in the main menu that share one prefab shape:
    /// a Panel holding some read-only TextMeshProUGUI copy plus a single dismiss Button.
    ///
    ///   Canvas/MenuContainer/NewsPanel        - heading NotificationText, body DemoText, ResponseButton
    ///   Canvas/MenuContainer/MenuNotification - heading NotificationText, ResponseButton (no body)
    ///   Canvas/MenuContainer/LANWarning       - heading NotificationText, Confirm
    ///
    /// All three are broken in the same two ways:
    ///
    ///   1. The copy carries only a TextMeshProUGUI. No Selectable means no IMoveHandler, so the
    ///      EventSystem cannot reach it and nothing in this mod reads a text object that is not
    ///      focused. The words on a modal the user cannot dismiss without reading are unreachable
    ///      by construction, not by misconfiguration.
    ///   2. The dismiss button ships navigation.mode == None. Selectable's four finders all return
    ///      null for None, AND Selectable.FindSelectable skips None candidates, so nothing can
    ///      navigate off it and nothing can navigate onto it. Rewriting that button's navigation
    ///      is required, not optional.
    ///
    /// THE TRAP this file exists to avoid: under NewsPanel/Panel the sibling indices are
    /// NotificationText = 8, ResponseButton = 9, DemoText = 10, while the anchored Y values are
    /// +126.97, -211.61 and +40.57. Reading order is heading -> body -> Continue but CHILD order is
    /// heading -> Continue -> body. Anything derived from GetChild(i) or GetComponentsInChildren
    /// produces the wrong order in a way that still "works" and passes a casual test. Content is
    /// therefore resolved BY NAME, in an authored order, and never by index.
    /// </summary>
    public static class NewsPanelAccessibility
    {
        public const string NewsScreenId = "NewsPopup";
        public const string MenuNotificationScreenId = "MenuNotification";
        public const string LanWarningScreenId = "LANWarning";

        // Screen id -> instance id of the root we last wired. These are scene objects that get
        // toggled, never rebuilt, so re-applying to the same instance must not re-register (that
        // would drop the registry's claims and, because the activation notifier already exists,
        // nothing would re-resolve them). A scene reload produces a new instance and re-registers.
        private static readonly Dictionary<string, int> appliedRoots = new Dictionary<string, int>();

        /// <summary>
        /// The news pop-up. Focus lands on the heading, so the user hears the news instead of the
        /// dismiss button, and Down walks heading -> body -> Continue.
        ///
        /// Nothing here synthesises a version string: the heading in this build reads
        /// "Welcome to Version 80!" while GameNetworkManager.gameVersionNum is 81. It is stale
        /// hand-authored scene text and the only honest thing to announce is whatever the live TMP
        /// says.
        /// </summary>
        public static void ApplyNewsPanel(GameObject newsPanel)
        {
            Apply(newsPanel, NewsScreenId, "News",
                  new string[] { "NotificationText", "DemoText" }, "ResponseButton", true);
        }

        /// <summary>
        /// The generic menu notification (disconnect reasons, Steam failures, incompatible save
        /// files). Public so <c>MenuManagerNotificationPatch</c> can call it after
        /// <c>MenuManager.DisplayMenuNotification</c> rewrites the text on an already-open panel:
        /// that path does not re-run OnEnable, so the screen would not otherwise re-announce.
        ///
        /// <paramref name="focusNotificationText"/> defaults to FALSE on purpose. The existing
        /// DisplayMenuNotification postfix already speaks menuNotificationText once; focusing the
        /// heading as well would say the same sentence twice. Focus therefore lands on the dismiss
        /// button and the heading stays one Up press away. If that postfix ever stops speaking the
        /// text, pass true here or the notification becomes silent.
        /// </summary>
        public static void ApplyMenuNotification(GameObject menuNotification, bool focusNotificationText = false)
        {
            Apply(menuNotification, MenuNotificationScreenId, "Notification",
                  new string[] { "NotificationText" }, "ResponseButton", focusNotificationText);
        }

        /// <summary>
        /// The LAN-mode warning. Its Confirm button is another Mode.None dead end.
        ///
        /// Panel children, dumped from a LAN-mode launch on 2026-10-14: NotificationText (TMP, the
        /// warning copy), Image (decor), Confirm. Only NotificationText is read.
        /// </summary>
        public static void ApplyLanWarning(GameObject lanWarning)
        {
            Apply(lanWarning, LanWarningScreenId, "LAN warning",
                  new string[] { "NotificationText" }, "Confirm", true);
        }

        /// <summary>
        /// Wire one panel: make the copy focusable, give the dismiss button a real navigation
        /// chain, and register the authored reading order with UIScreenRegistry.
        /// </summary>
        /// <param name="contentNames">
        /// Child names under Panel, in reading order.
        /// </param>
        /// <param name="focusContent">
        /// True: the screen opens on the first piece of copy. False: it opens on the dismiss
        /// button, for panels whose text is already spoken by something else.
        /// </param>
        private static void Apply(GameObject panelRoot, string screenId, string title,
                                  string[] contentNames, string actionName, bool focusContent)
        {
            if (panelRoot == null)
            {
                Debug.LogError("[LethalAccess] NewsPanelAccessibility.Apply('" + screenId +
                               "') was given a null panel. Nothing on it can be reached.");
                return;
            }

            Transform root = panelRoot.transform;

            // Re-entry: the host agent's DisplayMenuNotification postfix can call in repeatedly for
            // the same panel. Re-announce it (the text just changed) but do not re-register.
            int prevRootId;
            if (appliedRoots.TryGetValue(screenId, out prevRootId) &&
                prevRootId == panelRoot.GetInstanceID())
            {
                AnnounceIfVisible(panelRoot, screenId);
                return;
            }

            Transform panel = root.Find("Panel");
            if (panel == null)
            {
                Debug.LogError("[LethalAccess] '" + panelRoot.name + "' has no 'Panel' child, so screen '" +
                               screenId + "' cannot be described. The scene layout has changed.");
                return;
            }

            Transform action = panel.Find(actionName);
            if (action == null)
            {
                Debug.LogError("[LethalAccess] Screen '" + screenId + "': '" + panelRoot.name + "/Panel/" +
                               actionName + "' does not exist. The dismiss control has been renamed; " +
                               "leaving this panel on the game's own focus rather than guessing.");
                return;
            }

            // Button derives from Selectable, so this finds it. If it is gone the control cannot be
            // activated either, and half-wiring a modal the user cannot dismiss is worse than
            // leaving the game's own behaviour in place.
            Selectable actionSelectable = action.GetComponent<Selectable>();
            if (actionSelectable == null)
            {
                Debug.LogError("[LethalAccess] Screen '" + screenId + "': '" + action.name +
                               "' has no Selectable, so it is neither focusable nor clickable. " +
                               "This panel has been left alone.");
                return;
            }

            List<Transform> content = new List<Transform>();
            for (int i = 0; i < contentNames.Length; i++)
            {
                Transform t = panel.Find(contentNames[i]);
                if (t == null)
                {
                    // Bail rather than skip: the order is authored, and a hole in it silently
                    // drops a paragraph the user is expected to read before dismissing.
                    Debug.LogError("[LethalAccess] Screen '" + screenId + "': '" + panelRoot.name +
                                   "/Panel/" + contentNames[i] + "' does not exist. The panel has been " +
                                   "renamed or restructured; its text is not being announced.");
                    return;
                }
                content.Add(t);
            }

            // Explicit chain with null left/right. This is belt-and-braces against
            // SelectableNavigationPatch, which routes up/down through the authored screen order,
            // but only while ConfigManager.VerticalNavigationOnly is true. With that switched off
            // the scene's own Mode.None on the dismiss button reasserts itself and the modal
            // becomes a dead end again, so the chain has to exist in the components too.
            List<Selectable> chain = new List<Selectable>(content.Count + 1);
            for (int i = 0; i < content.Count; i++)
            {
                Selectable s = FocusInjection.MakeFocusable(content[i].gameObject);
                if (s != null)
                {
                    chain.Add(s);
                }
            }
            chain.Add(actionSelectable);
            NavigationEnforcer.BuildChain(chain, false);

            List<UISection> sections = new List<UISection>();
            if (content.Count > 0)
            {
                UIEntry[] contentEntries = new UIEntry[content.Count];
                for (int i = 0; i < content.Count; i++)
                {
                    contentEntries[i] = new UIEntry
                    {
                        // Direct, so the registry claims exactly the objects the chain above was
                        // written on: a path re-resolution that landed somewhere else would leave
                        // navigation and position announcements describing different objects.
                        Direct = content[i],
                        // Never consulted (Direct wins) but it is what the registry's error
                        // messages print, so a resolution failure names the object instead of
                        // saying "<empty entry>".
                        RelativePath = "Panel/" + content[i].name,
                        // Label deliberately left null: these read live from the TMP. Authoring a
                        // label here would freeze whatever the copy said the day it was written.
                        Type = i == 0 ? "heading" : "text"
                    };
                }
                sections.Add(new UISection("Content", contentEntries));
            }

            sections.Add(new UISection("Actions", new UIEntry
            {
                Direct = action,
                RelativePath = "Panel/" + action.name,
                // No Label: MenuNotification's button text is rewritten per notification
                // ("[ Back ]" vs "Continue") by MenuManager.DisplayMenuNotification.
                Type = "button"
            }));

            UIScreenDescriptor descriptor = new UIScreenDescriptor();
            descriptor.Id = screenId;
            descriptor.ResolveRoot = delegate { return root; };
            descriptor.Sections = sections;
            descriptor.Title = delegate { return title; };

            // All three of these panels are modal: they cover the menu and the only way on is
            // through their dismiss button. Trapping focus is what makes them behave that way for
            // a keyboard; without it the arrows walked the main menu underneath, reading out
            // buttons the panel was covering, and the panel was never announced at all because the
            // main-menu registration landed in the same frame and won. The trap lifts when the
            // panel hides, and the registry then hands focus back to the screen beneath.
            descriptor.TrapsFocus = true;

            // Escape dismisses the pop-up. Its button is not labelled "Back" (the game rewrites
            // its text per notification), so the default label scan cannot find it.
            descriptor.EscapeTarget = delegate { return action; };
            if (!focusContent)
            {
                descriptor.FocusOnOpen = delegate { return action; };
            }
            // else leave FocusOnOpen null: the registry then opens on the first visible entry,
            // which is the first Content entry, or the dismiss button when a panel turned out to
            // have no readable copy.

            UIScreenRegistry.Register(descriptor);
            // Recorded only once the wiring above succeeded, so a bail-out earlier in this method
            // leaves the panel eligible for another attempt rather than latching a half-done state.
            appliedRoots[screenId] = panelRoot.GetInstanceID();

            AnnounceIfVisible(panelRoot, screenId);
        }

        /// <summary>
        /// Register attaches the activation notifier, and Unity runs OnEnable inside AddComponent
        /// when the root is already active, so a panel that is open by the time we get here has
        /// usually announced itself already. It has NOT when the notifier already existed, which is
        /// the re-Apply case. ScreenShown ignores a second call in the same frame, so driving it
        /// here covers both without risking a doubled announcement.
        /// </summary>
        private static void AnnounceIfVisible(GameObject panelRoot, string screenId)
        {
            if (UIUtils.IsUIElementVisible(panelRoot))
            {
                UIScreenRegistry.ScreenShown(screenId);
            }
        }
    }
}
