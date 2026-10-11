using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LethalAccess
{
    /// <summary>
    /// The in-game pause menu (QuickMenu) as authored screens.
    ///
    /// The screen this replaces had no model at all: every announcement came from
    /// Utilities.GetTextFromComponent falling through to gameObject.name, and every arrow press
    /// came from Unity's geometric search. That search is the root of the worst defect here:
    /// Selectable.FindSelectable scores EVERY enabled Selectable in the scene whose
    /// navigation.mode != None, it is not scoped to a panel, so in multiplayer Down from "Quit"
    /// left the pause buttons entirely for an unlabelled player-list control, and MainButtons'
    /// wrapAround made Up from "Resume" pick the FURTHEST candidate in the opposite direction,
    /// again inside the player list. Solo it looks fine only because PlayerList is inactive.
    ///
    /// Three screens are described here:
    ///   quickmenu.main         - MainButtons + PlayerList, rooted on the QuickMenu container
    ///   quickmenu.leaveconfirm - the "leave the game?" prompt
    ///   quickmenu.kickconfirm  - the "kick out X?" prompt
    ///
    /// The settings panel inside this menu belongs to SettingsPanelAccessPatch and the keybinds
    /// overlay to the KepRemapPanel.LoadKeybindsUI postfix: one implementation each covers both
    /// the main-menu and in-game copies. Nothing here claims a SettingsPanel or KeybindsPanel
    /// object, and nothing here moves focus while either is up.
    ///
    /// Why quickmenu.main is rooted on menuContainer rather than on MainButtons: the player list
    /// is a SIBLING of MainButtons, not a child, and one screen has to cover both or the two
    /// panels end up as two simultaneously-visible screens fighting over ActiveScreenId and over
    /// focus every time the menu opens. The cost is that ScreenShown/ScreenHidden only fire on
    /// the whole menu opening and closing, so the panel-switch postfixes below re-announce
    /// explicitly.
    /// </summary>
    [HarmonyPatch(typeof(QuickMenuManager))]
    internal static class AccessibleQuickMenuPatch
    {
        internal const string MainScreenId = "quickmenu.main";
        internal const string LeaveScreenId = "quickmenu.leaveconfirm";
        internal const string KickScreenId = "quickmenu.kickconfirm";

        // Every patch below is on a QuickMenuManager instance method, so this is always set from
        // __instance before anything reads it. Held so the descriptor delegates can resolve from
        // serialized references instead of paths.
        private static QuickMenuManager qmm;

        // CloseQuickMenu calls CloseQuickMenuPanels on its way out. Without this stamp the panel
        // postfix would announce "Pause menu" at the exact moment the menu disappears. A frame
        // number rather than a bool so an exception thrown between the two cannot leave the
        // suppression latched on forever.
        private static int closeQuickMenuFrame = -1;

        // The KickButton the open kick prompt came from, so cancelling returns focus to it. The
        // vanilla Deny button calls Select() on SettingsPanel/BackButton, the wrong panel, and
        // inactive at that moment, which is what stranded the keyboard off-screen.
        private static Transform kickReturnFocus;

        #region Patches

        [HarmonyPatch(nameof(QuickMenuManager.OpenQuickMenu))]
        [HarmonyPostfix]
        public static void PostfixOpenQuickMenu(QuickMenuManager __instance)
        {
            // On the first open this registration is what attaches the activation notifier to an
            // already-active menuContainer, so ScreenShown ("Pause menu" + focus on Resume) runs
            // inside EnsureScreens. On later opens the notifier has already fired from
            // menuContainer's OnEnable, before this postfix.
            EnsureScreens(__instance);

            // OpenQuickMenu toggles playerListPanel AFTER menuContainer, so the chain has to be
            // rebuilt once the player list's real visibility is known.
            SyncChains();
        }

        [HarmonyPatch(nameof(QuickMenuManager.CloseQuickMenu))]
        [HarmonyPrefix]
        public static void PrefixCloseQuickMenu()
        {
            closeQuickMenuFrame = Time.frameCount;
        }

        [HarmonyPatch(nameof(QuickMenuManager.CloseQuickMenuPanels))]
        [HarmonyPostfix]
        public static void PostfixCloseQuickMenuPanels(QuickMenuManager __instance)
        {
            EnsureScreens(__instance);

            // Vanilla resets leaveGameConfirmPanel and settingsPanel here but NOT
            // ConfirmKickUserPanel. Left set, it re-enables with its PlayerList parent later and
            // a stale "Kick out X?" prompt reappears, which now also means an announced screen
            // change and a stolen focus. Reset it with the panels it belongs to.
            if (__instance.ConfirmKickUserPanel != null && __instance.ConfirmKickUserPanel.activeSelf)
            {
                __instance.ConfirmKickUserPanel.SetActive(false);
            }

            SyncChains();

            if (closeQuickMenuFrame == Time.frameCount)
            {
                // Called from CloseQuickMenu: the whole menu is going away, so there is no screen
                // to announce.
                return;
            }

            // Returning from Settings or from the leave prompt is a screen change and rule 4 says
            // it announces. The game's own onClick then Selects the button you came from
            // (MainButtons/Settings or MainButtons/Quit), which runs after this and wins, so the
            // user hears "Pause menu" followed by the control they left from, not by Resume.
            UIScreenRegistry.ScreenShown(MainScreenId);
        }

        [HarmonyPatch(nameof(QuickMenuManager.CloseQuickMenu))]
        [HarmonyPostfix]
        public static void PostfixCloseQuickMenu()
        {
            closeQuickMenuFrame = -1;
        }

        [HarmonyPatch(nameof(QuickMenuManager.EnableUIPanel))]
        [HarmonyPostfix]
        public static void PostfixEnableUIPanel(QuickMenuManager __instance, GameObject enablePanel)
        {
            EnsureScreens(__instance);

            // EnableUIPanel always hides the player list, so the main chain changes shape whether
            // or not MainButtons itself was the panel being shown.
            SyncChains();

            if (enablePanel != null && enablePanel == __instance.mainButtonsPanel)
            {
                UIScreenRegistry.ScreenShown(MainScreenId);
            }

            // Any other panel (SettingsPanel today) announces itself through its own screen
            // descriptor. Deliberately no focus work here: moving focus would fight
            // SettingsPanelAccessPatch, and the game's onClick Selects that panel's Back button
            // immediately afterwards.
        }

        [HarmonyPatch(nameof(QuickMenuManager.DisableUIPanel))]
        [HarmonyPostfix]
        public static void PostfixDisableUIPanel(QuickMenuManager __instance, GameObject enablePanel)
        {
            EnsureScreens(__instance);

            // Hiding anything other than MainButtons brings the player list back, so the main
            // chain has to be rebuilt either way.
            SyncChains();

            if (enablePanel != null && enablePanel != __instance.mainButtonsPanel &&
                __instance.mainButtonsPanel != null &&
                UIUtils.IsUIElementVisible(__instance.mainButtonsPanel))
            {
                UIScreenRegistry.ScreenShown(MainScreenId);
            }
        }

        [HarmonyPatch(nameof(QuickMenuManager.LeaveGame))]
        [HarmonyPostfix]
        public static void PostfixLeaveGame(QuickMenuManager __instance)
        {
            EnsureScreens(__instance);

            // leaveGameConfirmPanel was activated part-way through LeaveGame, so the screen has
            // already announced itself and seeded focus on Cancel. What it could NOT see is
            // leaveGameClarificationText.enabled, which LeaveGame writes on its LAST line, after
            // the panel is up. Re-resolve so the warning joins (or leaves) the authored order,
            // then speak it.
            UIScreenRegistry.Invalidate(LeaveScreenId);
            SyncChains();

            Transform panel = __instance.leaveGameConfirmPanel != null
                ? __instance.leaveGameConfirmPanel.transform
                : null;

            string prompt = ReadText(ResolveLeavePrompt(panel));
            if (!string.IsNullOrEmpty(prompt))
            {
                AnnouncementPolicy.Speak(prompt);
            }
            else
            {
                Debug.LogError("[LethalAccess] The leave-game prompt text could not be found under '" +
                               (panel != null ? panel.name : "<null panel>") +
                               "'; the question is on screen but will not be spoken.");
            }

            // Host, outside orbit: "The file will NOT save! Your ship must be in orbit." This is
            // the single most consequential sentence in the pause menu and nothing has ever
            // spoken it.
            TextMeshProUGUI clarification = __instance.leaveGameClarificationText;
            if (clarification != null && clarification.enabled && !string.IsNullOrEmpty(clarification.text))
            {
                AnnouncementPolicy.Speak(clarification.text);
            }
        }

        [HarmonyPatch(nameof(QuickMenuManager.KickUserFromServer))]
        [HarmonyPostfix]
        public static void PostfixKickUserFromServer(QuickMenuManager __instance, int playerObjId)
        {
            EnsureScreens(__instance);

            // KickUserFromServer writes ConfirmKickPlayerText BEFORE activating the panel, so the
            // screen title already read the live "Kick out X?"; nothing to re-announce here.
            SyncChains();

            kickReturnFocus = null;
            if (__instance.playerListSlots != null &&
                playerObjId >= 0 && playerObjId < __instance.playerListSlots.Length)
            {
                kickReturnFocus = KickButtonOf(__instance.playerListSlots[playerObjId]);
            }
            else
            {
                // AddUserToPlayerList accepts 0..4 while playerListSlots has four entries, so a
                // five-player build would index past the end. Never mirror that array without a
                // bound check.
                Debug.LogWarning("[LethalAccess] Kick confirmation opened for player slot " + playerObjId +
                                 ", which is outside playerListSlots; focus cannot be returned to its " +
                                 "Kick button when the prompt closes.");
            }
        }

        [HarmonyPatch(nameof(QuickMenuManager.CancelKickUserFromServer))]
        [HarmonyPostfix]
        public static void PostfixCancelKickUserFromServer(QuickMenuManager __instance)
        {
            SyncChains();
            RestoreKickFocus();
        }

        [HarmonyPatch(nameof(QuickMenuManager.ConfirmKickUserFromServer))]
        [HarmonyPostfix]
        public static void PostfixConfirmKickUserFromServer(QuickMenuManager __instance)
        {
            // ConfirmKickUserFromServer early-returns without closing the panel when
            // playerObjToKick is outside 1..3, which includes the host's own slot. The prompt is
            // still on screen in that case, so moving focus would strand the user off it.
            if (__instance.ConfirmKickUserPanel != null && __instance.ConfirmKickUserPanel.activeSelf)
            {
                AnnouncementPolicy.Speak("That player cannot be kicked.");
                return;
            }

            SyncChains();

            // The Confirm button's own onClick already Selects MainButtons/Resume, and the kicked
            // player's row is about to disappear, so returning focus to their Kick button (what
            // Cancel does) would be pointing at a control that is on its way out. Only step in if
            // that Select did not land somewhere usable.
            kickReturnFocus = null;
            EventSystem es = EventSystem.current;
            if (es != null && es.currentSelectedGameObject != null &&
                UIUtils.IsUIElementVisible(es.currentSelectedGameObject))
            {
                return;
            }

            Transform resume = MainButton("Resume");
            if (resume != null && UIUtils.IsUIElementVisible(resume.gameObject))
            {
                FocusManager.Seed(resume);
            }
        }

        [HarmonyPatch(nameof(QuickMenuManager.AddUserToPlayerList))]
        [HarmonyPostfix]
        public static void PostfixAddUserToPlayerList(QuickMenuManager __instance, int playerObjectId)
        {
            if (__instance.playerListSlots == null ||
                playerObjectId < 0 || playerObjectId >= __instance.playerListSlots.Length)
            {
                // The game's own guard is 0..4 against a four-entry array; do not repeat it.
                Debug.LogWarning("[LethalAccess] AddUserToPlayerList reported slot " + playerObjectId +
                                 ", which is outside playerListSlots; the player list was not rebuilt.");
                return;
            }

            // The usernameHeader is written just above this, so the rebuilt labels carry the new
            // player's name. Every player-list label is baked at rebuild time for exactly this
            // reason: AddUserToPlayerList is the only writer of that text.
            if (!EnsureScreens(__instance))
            {
                RebuildMain();
            }
        }

        [HarmonyPatch(nameof(QuickMenuManager.RemoveUserFromPlayerList))]
        [HarmonyPostfix]
        public static void PostfixRemoveUserFromPlayerList(QuickMenuManager __instance, int playerObjectId)
        {
            if (__instance.playerListSlots == null ||
                playerObjectId < 0 || playerObjectId >= __instance.playerListSlots.Length)
            {
                Debug.LogWarning("[LethalAccess] RemoveUserFromPlayerList reported slot " + playerObjectId +
                                 ", which is outside playerListSlots; the player list was not rebuilt.");
                return;
            }

            if (!EnsureScreens(__instance))
            {
                RebuildMain();
            }
        }

        #endregion

        #region Registration

        /// <summary>
        /// Register the three screens for this QuickMenuManager. Idempotent per instance; a new
        /// gameplay scene brings a new instance and re-registers against it. Returns true when it
        /// actually registered, so a caller that was about to rebuild anyway can skip doing it
        /// twice.
        /// </summary>
        private static bool EnsureScreens(QuickMenuManager instance)
        {
            if (instance == null)
            {
                Debug.LogError("[LethalAccess] AccessibleQuickMenuPatch was reached with no QuickMenuManager; " +
                               "the pause menu has no screen model and will fall back to geometric navigation.");
                return false;
            }

            if (qmm == instance)
            {
                return false;
            }

            qmm = instance;
            kickReturnFocus = null;

            // Both confirm panels are inactive at this point, so registering them attaches their
            // notifiers without announcing anything. The main screen is registered last because
            // its root IS active while the menu is open, and attaching a notifier to an active
            // GameObject runs OnEnable synchronously, which is how the first open announces
            // "Pause menu" and lands focus on Resume.
            UIScreenRegistry.Register(BuildLeaveDescriptor());
            UIScreenRegistry.Register(BuildKickDescriptor());
            RebuildMain();
            return true;
        }

        /// <summary>
        /// Re-author the main screen. Player-list labels are static strings baked from the live
        /// slot data, so anything that changes who is in the lobby has to come through here.
        /// </summary>
        private static void RebuildMain()
        {
            if (qmm == null)
            {
                return;
            }

            UIScreenRegistry.Register(BuildMainDescriptor());

            // Register drops the old claims and starts an empty state; without this the screen
            // has no entries until it is next shown.
            UIScreenRegistry.Invalidate(MainScreenId);

            IsolateHostKickButton();
            SyncChains();
        }

        /// <summary>
        /// Slot 0 is the host, and its KickButton ships with an EMPTY onClick list: it is
        /// focusable and does nothing. It is left out of the authored order, which is enough
        /// while vertical-only navigation is on; taking it out of Unity's navigation as well
        /// makes it unreachable under every mode instead of only that one.
        /// </summary>
        private static void IsolateHostKickButton()
        {
            if (qmm == null || qmm.playerListSlots == null || qmm.playerListSlots.Length == 0)
            {
                return;
            }

            Transform kick = KickButtonOf(qmm.playerListSlots[0]);
            if (kick == null)
            {
                return;
            }

            Selectable s = kick.GetComponent<Selectable>();
            if (s != null)
            {
                NavigationEnforcer.Isolate(s);
            }
        }

        /// <summary>
        /// Write the authored order into real Explicit navigation.
        ///
        /// SelectableNavigationPatch already routes up/down through the registry, but only while
        /// VerticalNavigationOnly is on. Writing the chain as well means the cross-panel jump and
        /// the wrap into the middle of the player list stay fixed with that setting off, and it
        /// has a second effect worth having: an Explicit slider with a null selectOnLeft still
        /// steps its value on Left/Right (Slider.OnMove adjusts whenever the finder returns
        /// null), so the voice sliders become keyboard-adjustable either way.
        /// </summary>
        private static void SyncChains()
        {
            BuildChainFor(MainScreenId);
            BuildChainFor(LeaveScreenId);
            BuildChainFor(KickScreenId);
        }

        private static void BuildChainFor(string screenId)
        {
            List<Transform> order = UIScreenRegistry.VisibleFlatOrder(screenId);
            if (order.Count == 0)
            {
                // The panel is closed. Its Selectables keep the chain they had, all of them
                // inactive, so there is nothing to correct and nothing to warn about.
                return;
            }

            List<Selectable> chain = new List<Selectable>(order.Count);
            for (int i = 0; i < order.Count; i++)
            {
                if (order[i] == null)
                {
                    continue;
                }
                Selectable s = order[i].GetComponent<Selectable>();
                if (s != null)
                {
                    chain.Add(s);
                }
            }

            if (chain.Count > 0)
            {
                NavigationEnforcer.BuildChain(chain, false);
            }
        }

        #endregion

        #region Descriptors

        private static UIScreenDescriptor BuildMainDescriptor()
        {
            UIScreenDescriptor d = new UIScreenDescriptor();
            d.Id = MainScreenId;
            d.ResolveRoot = ResolveMenuContainer;
            d.Title = () => "Pause menu";
            d.FocusOnOpen = () => MainButton("Resume");

            d.Sections.Add(new UISection(
                "Pause menu",
                new UIEntry { Resolver = r => MainButton("Resume"), Label = "Resume", Type = "button" },
                new UIEntry
                {
                    Resolver = r => MainButton("InvitePlayers"),
                    Label = "Invite friends",
                    Type = "button",
                    State = InviteFriendsState
                },
                new UIEntry { Resolver = r => MainButton("Settings"), Label = "Settings", Type = "button" },
                new UIEntry { Resolver = r => MainButton("Quit"), Label = "Quit", Type = "button" }));

            List<UIEntry> players = BuildPlayerEntries();
            if (players.Count > 0)
            {
                d.Sections.Add(new UISection("Players", players.ToArray()));
            }

            return d;
        }

        /// <summary>
        /// DisableInviteFriendsButton only sets CanvasGroup.alpha = 0.2 (Button.interactable is
        /// untouched), so the control announced identically whether or not it would do anything.
        /// Read the predicate the button itself uses rather than the alpha, so the state is right
        /// even if a future build stops dimming it.
        /// </summary>
        private static string InviteFriendsState()
        {
            GameNetworkManager gnm = GameNetworkManager.Instance;
            if (gnm != null && gnm.gameHasStarted)
            {
                return "unavailable after the game has started";
            }
            return null;
        }

        private static List<UIEntry> BuildPlayerEntries()
        {
            List<UIEntry> entries = new List<UIEntry>();

            QuickMenuManager m = qmm;
            if (m == null || m.playerListSlots == null)
            {
                return entries;
            }

            for (int i = 0; i < m.playerListSlots.Length; i++)
            {
                PlayerListSlot slot = m.playerListSlots[i];
                if (slot == null || !slot.isConnected)
                {
                    continue;
                }

                // Every control in a slot is a leaf with no text of its own, so without the name
                // in the label they announce as "ProfileIcon, button" and "Slider 5, slider" and
                // there is no way to tell whose they are. The name itself lives on a sibling
                // whose parent has an Image but no Button, so it is not reachable either.
                string name = SlotName(slot, i);

                if (slot.usernameHeader != null)
                {
                    entries.Add(new UIEntry
                    {
                        Direct = slot.usernameHeader.transform,
                        Label = name,
                        Type = "text"
                    });
                }

                if (slot.volumeSlider != null)
                {
                    // Hidden for whichever slot is the local player (volumeSliderContainer is
                    // switched off in AddUserToPlayerList), hence Optional. No State provider:
                    // the announcer's slider branch reads the live value, and a second copy here
                    // would say the number twice.
                    entries.Add(new UIEntry
                    {
                        Direct = slot.volumeSlider.transform,
                        Label = name + ", voice volume",
                        Type = "slider",
                        Optional = true
                    });
                }

                Transform profile = ProfileIconOf(slot);
                if (profile != null)
                {
                    entries.Add(new UIEntry
                    {
                        Direct = profile,
                        Label = name + ", open Steam profile",
                        Type = "button"
                    });
                }

                if (i > 0)
                {
                    // Slot 0's KickButton has no handler at all. See IsolateHostKickButton.
                    Transform kick = KickButtonOf(slot);
                    if (kick != null)
                    {
                        entries.Add(new UIEntry
                        {
                            Direct = kick,
                            Label = "kick " + name,
                            Type = "button",
                            // Only shown to the host (SetActive(IsServer)); absent-but-inactive
                            // is the normal case for a client.
                            Optional = true
                        });
                    }
                }
            }

            return entries;
        }

        private static UIScreenDescriptor BuildLeaveDescriptor()
        {
            UIScreenDescriptor d = new UIScreenDescriptor();
            d.Id = LeaveScreenId;
            d.ResolveRoot = ResolveLeavePanel;

            // No title: the prompt IS the screen change, and it is read live off the TMP by the
            // LeaveGame postfix (which runs after the game decides whether the save warning
            // applies). A synthesised title here would only duplicate it.
            d.Title = null;
            d.FocusOnOpen = () => LeaveChild("Quit (2)");

            d.Sections.Add(new UISection(
                "Prompt",
                new UIEntry
                {
                    Resolver = ResolveLeavePrompt,
                    Type = "text"
                    // No Label: the question is authored scene text and must be read live.
                },
                new UIEntry
                {
                    // The save warning is toggled through TextMeshProUGUI.enabled, not through
                    // GameObject.SetActive, and IsUIElementVisible cannot see that. Returning
                    // null while it is disabled is what keeps it out of the order and out of the
                    // counts instead of announcing an empty line.
                    Resolver = ResolveLeaveClarification,
                    Type = "text",
                    Optional = true
                }));

            // The two buttons are NAMED "Quit (1)" and "Quit (2)" in the scene, which is why they
            // announced as a pair of identical "Quit"s. Their on-screen labels are Confirm and
            // Cancel; authoring those keeps what is heard and what is displayed the same.
            d.Sections.Add(new UISection(
                "Choices",
                new UIEntry { Resolver = r => LeaveChild("Quit (1)"), Label = "Confirm", Type = "button" },
                new UIEntry { Resolver = r => LeaveChild("Quit (2)"), Label = "Cancel", Type = "button" }));

            return d;
        }

        private static UIScreenDescriptor BuildKickDescriptor()
        {
            UIScreenDescriptor d = new UIScreenDescriptor();
            d.Id = KickScreenId;
            d.ResolveRoot = ResolveKickPanel;

            // KickUserFromServer writes this text before activating the panel, so by the time the
            // notifier fires the live string already names the player.
            d.Title = () =>
            {
                QuickMenuManager m = qmm;
                return m != null && m.ConfirmKickPlayerText != null ? m.ConfirmKickPlayerText.text : null;
            };

            // Cancel, not Kick: this is destructive and one Enter away.
            d.FocusOnOpen = () => KickChild("Panel/Deny");
            d.Modal = true;

            d.Sections.Add(new UISection(
                "Prompt",
                new UIEntry
                {
                    Resolver = r =>
                    {
                        QuickMenuManager m = qmm;
                        return m != null && m.ConfirmKickPlayerText != null
                            ? m.ConfirmKickPlayerText.transform
                            : null;
                    },
                    Type = "text"
                }));

            d.Sections.Add(new UISection(
                "Choices",
                new UIEntry { Resolver = r => KickChild("Panel/Confirm"), Label = "Kick", Type = "button" },
                new UIEntry { Resolver = r => KickChild("Panel/Deny"), Label = "Cancel", Type = "button" }));

            return d;
        }

        #endregion

        #region Resolution helpers

        private static Transform ResolveMenuContainer()
        {
            QuickMenuManager m = qmm;
            return m != null && m.menuContainer != null ? m.menuContainer.transform : null;
        }

        private static Transform ResolveLeavePanel()
        {
            QuickMenuManager m = qmm;
            return m != null && m.leaveGameConfirmPanel != null ? m.leaveGameConfirmPanel.transform : null;
        }

        private static Transform ResolveKickPanel()
        {
            QuickMenuManager m = qmm;
            return m != null && m.ConfirmKickUserPanel != null ? m.ConfirmKickUserPanel.transform : null;
        }

        private static Transform MainButton(string childName)
        {
            QuickMenuManager m = qmm;
            if (m == null || m.mainButtonsPanel == null)
            {
                return null;
            }
            return m.mainButtonsPanel.transform.Find(childName);
        }

        private static Transform LeaveChild(string childPath)
        {
            Transform root = ResolveLeavePanel();
            return root != null ? root.Find(childPath) : null;
        }

        private static Transform KickChild(string childPath)
        {
            Transform root = ResolveKickPanel();
            return root != null ? root.Find(childPath) : null;
        }

        /// <summary>
        /// The "Would you like to leave the game?" TMP. It is the one control on these screens
        /// with no serialized reference to resolve from, so the scene name is tried first and a
        /// structural search backs it up: the only text under this panel that is neither the save
        /// warning nor a button label.
        /// </summary>
        private static Transform ResolveLeavePrompt(Transform root)
        {
            if (root == null)
            {
                return null;
            }

            Transform named = root.Find("Text (TMP) (1)");
            if (named != null)
            {
                return named;
            }

            QuickMenuManager m = qmm;
            Transform clarification = m != null && m.leaveGameClarificationText != null
                ? m.leaveGameClarificationText.transform
                : null;

            TextMeshProUGUI[] texts = root.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                TextMeshProUGUI t = texts[i];
                if (t == null || t.transform == clarification)
                {
                    continue;
                }
                if (t.GetComponentInParent<Button>() != null)
                {
                    continue;
                }
                if (string.IsNullOrEmpty(t.text))
                {
                    continue;
                }
                return t.transform;
            }

            return null;
        }

        private static Transform ResolveLeaveClarification(Transform root)
        {
            QuickMenuManager m = qmm;
            if (m == null || m.leaveGameClarificationText == null)
            {
                return null;
            }
            return m.leaveGameClarificationText.enabled ? m.leaveGameClarificationText.transform : null;
        }

        private static Transform ProfileIconOf(PlayerListSlot slot)
        {
            if (slot == null || slot.slotContainer == null)
            {
                return null;
            }

            Transform t = slot.slotContainer.transform.Find("ProfileIcon");
            if (t == null)
            {
                Debug.LogError("[LethalAccess] Player-list slot '" + slot.slotContainer.name +
                               "' has no child named 'ProfileIcon'. The open-Steam-profile control cannot be " +
                               "labelled or placed in the reading order.");
            }
            return t;
        }

        /// <summary>
        /// PlayerListSlot.KickUserButton is the Button itself in the shipped scene. Check rather
        /// than assume: a container here would put a dead stop in the reading order.
        /// </summary>
        private static Transform KickButtonOf(PlayerListSlot slot)
        {
            if (slot == null || slot.KickUserButton == null)
            {
                return null;
            }

            Transform t = slot.KickUserButton.transform;
            if (t.GetComponent<Button>() != null)
            {
                return t;
            }

            Button inner = t.GetComponentInChildren<Button>(true);
            if (inner != null)
            {
                return inner.transform;
            }

            Debug.LogError("[LethalAccess] Player-list kick control '" + t.name + "' has no Button anywhere " +
                           "under it; it is not being announced, because focusing it would do nothing.");
            return null;
        }

        private static string SlotName(PlayerListSlot slot, int index)
        {
            if (slot != null && slot.usernameHeader != null)
            {
                string n = slot.usernameHeader.text;
                if (!string.IsNullOrEmpty(n) && n.Trim().Length > 0)
                {
                    return n.Trim();
                }
            }
            // AddUserToPlayerList is the only writer of that header, so an empty one means the
            // slot was marked connected without a name; say which row it is rather than nothing.
            return "player " + (index + 1);
        }

        private static string ReadText(Transform t)
        {
            if (t == null)
            {
                return null;
            }
            TextMeshProUGUI tmp = t.GetComponent<TextMeshProUGUI>();
            return tmp != null ? tmp.text : null;
        }

        private static void RestoreKickFocus()
        {
            Transform target = kickReturnFocus;
            kickReturnFocus = null;

            if (target != null && UIUtils.IsUIElementVisible(target.gameObject))
            {
                FocusManager.Seed(target);
                return;
            }

            // The row went away while the prompt was up (the player disconnected). Fall back to
            // the pause buttons rather than leaving selection on Deny's onClick target, which is
            // SettingsPanel/BackButton, a different panel, and inactive.
            Transform resume = MainButton("Resume");
            if (resume != null && UIUtils.IsUIElementVisible(resume.gameObject))
            {
                FocusManager.Seed(resume);
                return;
            }

            Debug.LogWarning("[LethalAccess] The kick prompt closed but neither its Kick button nor Resume is " +
                             "visible; focus recovery is left to FocusManager's watchdog.");
        }

        #endregion
    }
}
