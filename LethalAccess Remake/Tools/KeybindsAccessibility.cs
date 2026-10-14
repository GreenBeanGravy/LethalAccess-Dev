using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace LethalAccess
{
    /// <summary>
    /// The "REMAP CONTROLS" screen: the largest single screen in the game, and the only one that
    /// can leave a blind user with a dead keyboard and no way out.
    ///
    /// Two completely different UIs live behind one component. LethalCompanyInputUtils prefixes
    /// KepRemapPanel.OnEnable, reparents the vanilla Scroll View under a deactivated
    /// "Legacy Holder(Clone)" and REPLACES remappableKeys with an empty list, so vanilla
    /// LoadKeybindsUI loops over nothing and the 40 RemapKeySlot clones are never created. When
    /// InputUtils is installed (it is, on this install) the InputUtils branch below is the one
    /// that runs. Both are implemented because the mod must still work without it.
    ///
    /// Nothing here can be done through path registration: every control is a runtime clone and
    /// all 40 vanilla rows share the path ".../RemapKeySlot(Clone)/Button", so one path key would
    /// describe forty different controls. Everything is registered BY INSTANCE.
    /// </summary>
    public static class KeybindsAccessibility
    {
        /// <summary>The screen the arrow keys walk: categories, bind rows, panel actions.</summary>
        public const string ScreenId = "keybinds";

        /// <summary>
        /// The per-row extras (gamepad bind, reset, remove) live on a SECOND screen.
        ///
        /// This is not decoration. SelectableNavigationPatch drives up/down from the registry's
        /// flat order for every claimed control, and KeepsHorizontal is only honoured for claimed
        /// controls: there is no path- or type-based exemption. So a control that must keep
        /// left/right MUST be claimed, and anything claimed by the main screen also joins its
        /// up/down order. Putting the extras on their own screen keeps them out of the row
        /// walk while still letting them keep left/right, and gives them a sane vertical order
        /// of their own: down from a gamepad bind goes to the NEXT ROW's gamepad bind, because
        /// this screen's sections are columns rather than rows.
        /// </summary>
        public const string ExtrasScreenId = "keybinds.rowcontrols";

        /// <summary>
        /// Repeat cadence for the "waiting for a key" prompt. The vanilla rebind suppresses every
        /// matching input event AND disables InputSystem.actions, so during a rebind the keyboard
        /// is genuinely dead: a user who missed the first prompt has no other way to find out
        /// what is happening.
        /// </summary>
        private const float PromptRepeatSeconds = 5f;

        /// <summary>
        /// Hard stop for a vanilla rebind. IngamePlayerSettings.RebindKey never calls
        /// WithTimeout, and its only documented exit is the OpenMenu control, which is useless if
        /// OpenMenu happens to be unbound, because then RebindKey throws on
        /// controls[0] AFTER InputSystem.actions.Disable() has already run. This is the only
        /// guaranteed way out. THIS CHANGES GAME BEHAVIOUR: a vanilla rebind that would have
        /// waited forever now gives up.
        /// </summary>
        private const float RebindTimeoutSeconds = 15f;

        // ---------------------------------------------------------------- built model

        /// <summary>One bind row and the extras that hang off it horizontally.</summary>
        private sealed class Row
        {
            public string Action;
            public Selectable Primary;              // the control the vertical walk stops on
            public readonly List<Selectable> Horizontal = new List<Selectable>();

            // The InputUtils RebindButtons behind the row. Delete acts on these.
            public object KbmButton;
            public object GamepadButton;
            public Selectable GamepadSelectable;

            // The category the row belongs to, and its RebindItem, the object a collapse hides.
            public Category Category;
            public GameObject Item;
        }

        /// <summary>
        /// One InputUtils section ("Lethal Company", "LethalAccess", one per mod with keys), and the
        /// button in the category list on the left that names it.
        ///
        /// The categories are collapsible here although InputUtils draws them as one long list.
        /// Walking forty rows of the base game to reach the one mod section you came for is the
        /// keyboard equivalent of scrolling past it with your eyes shut, so the screen opens with
        /// every category closed: Enter on one shows its rows and lands on the first, and Escape or
        /// moving out past either end closes it again.
        /// </summary>
        private sealed class Category
        {
            public string Name;
            public int Index;           // InputUtils' section index; what RemapContainerController.JumpTo takes
            public Selectable Header;   // null when there is no category list, which leaves the rows always shown
            public bool IsBaseGame;     // InputUtils always builds the game's own section first
            public readonly List<Row> Rows = new List<Row>();
            public readonly List<UIEntry> Entries = new List<UIEntry>();
        }

        private static readonly List<Category> categories = new List<Category>();

        // By name, because Build runs twice per open and rebuilds the Category objects each time;
        // the second build must keep what the first one decided. Null = every category closed.
        private static string expandedCategoryName;

        // The frame Escape was last used up here. The in-game Escape reaches this screen by more than
        // one route, and a second route in the same frame must not then close the whole panel.
        private static int escapeConsumedFrame = -1;

        // Ended inside the frame Escape cancelled it: the same press must not also close anything.
        private static int lastRebindEndFrame = -10;

        // The binds list scrolls under a sticky section header, which covers the top of the view.
        private static ScrollRect bindsScrollRect;
        private static float bindsTopInset;
        private static Component remapContainer;
        private static GameObject lastScrolledSelection;

        /// <summary>One category button and the exact delegate THIS class put on it.</summary>
        private sealed class SectionHook
        {
            public Button Button;
            public UnityEngine.Events.UnityAction Action;
        }

        private static KepRemapPanel panel;
        private static GameObject extrasAnchor;
        private static readonly List<Row> rows = new List<Row>();
        private static readonly List<UISection> mainSections = new List<UISection>();
        private static readonly List<Selectable> mainChain = new List<Selectable>();

        // Section entries get an onClick listener that moves focus into the section they scroll
        // to. Their GameObjects survive across panel opens (InputUtils reuses the container) and
        // LoadKeybindsUI runs twice per open, so the listener has to be taken off before it is put
        // on again or it stacks.
        //
        // This used to be a HashSet of GameObject instance ids that HookSectionEntry refused to
        // hook twice, and nothing ever cleared it. Unity REUSES instance ids after a destroy, so a
        // rebuilt SectionEntry that landed on a retired id was silently left with no listener at
        // all: pressing that category then scrolled the list and said nothing, which is the exact
        // defect the hook exists to fix. Keeping our own delegate and doing
        // RemoveListener-then-AddListener is idempotent and never consults an id.
        private static readonly List<SectionHook> sectionHooks = new List<SectionHook>();

        // ---------------------------------------------------------------- rebind state

        private static SettingsOption activeRebindOption;
        private static string activeRebindPrompt;
        private static float activeRebindStartedAt;
        private static float lastPromptAt;

        /// <summary>The SettingsOption currently waiting for a key, or null.</summary>
        public static SettingsOption ActiveRebindOption
        {
            get { return activeRebindOption; }
        }

        #region Build / teardown

        /// <summary>
        /// Called from the LoadKeybindsUI postfix. Safe to call repeatedly: InputUtils causes
        /// LoadKeybindsUI to run twice per panel open (once from OnEnable's own body, once from
        /// LcInputActionApi.LoadIntoUI in its postfix).
        /// </summary>
        public static void Build(KepRemapPanel target)
        {
            if (target == null)
            {
                Debug.LogError("[LethalAccess] KeybindsAccessibility.Build called with no panel.");
                return;
            }

            panel = target;
            rows.Clear();
            categories.Clear();
            mainSections.Clear();
            mainChain.Clear();
            extrasColumnChains.Clear();
            remapContainer = null;
            bindsScrollRect = null;
            bindsTopInset = 0f;
            InvalidateConflicts();
            // Everything below is about to be re-hooked. Taking the old listeners off here also
            // covers the case a per-button remove cannot: a category button that survives the
            // rebuild but is no longer enumerated would otherwise keep a listener pointing at a
            // section list that has just been emptied.
            UnhookSectionEntries();

            Transform root = panel.transform;

            List<UISection> extrasSections = new List<UISection>();

            bool built = InputUtilsBridge.Available && BuildInputUtils(root, extrasSections);
            if (!built)
            {
                BuildVanilla(root);
            }

            AddPanelActions(root);

            if (mainSections.Count == 0)
            {
                Debug.LogWarning("[LethalAccess] Keybinds panel: nothing was found to register. " +
                                 "Neither the vanilla RemapKeySlot rows nor an InputUtils remap container " +
                                 "resolved, so this screen has no keyboard order.");
                return;
            }

            RebuildMainChain();
            ApplyCollapse();
            WatchForLeavingRows();
            RegisterScreens(extrasSections);
            WireNavigation();
            IsolateScrollbars(root);
            EnsureWatchdog();
        }

        /// <summary>The vertical chain, in exactly the order the sections put the controls in.</summary>
        private static void RebuildMainChain()
        {
            mainChain.Clear();
            for (int s = 0; s < mainSections.Count; s++)
            {
                List<UIEntry> entries = mainSections[s].Entries;
                for (int e = 0; e < entries.Count; e++)
                {
                    Selectable selectable = entries[e].Direct != null ? entries[e].Direct.GetComponent<Selectable>() : null;
                    if (selectable != null)
                    {
                        mainChain.Add(selectable);
                    }
                }
            }
        }

        /// <summary>
        /// Prefix on KepRemapPanel.OnDisable. Unregistering (rather than only clearing) destroys
        /// the screen's activation notifier, which matters: the notifier fires OnEnable BEFORE
        /// LoadKeybindsUI on the next open, and would announce a screen whose entries all point at
        /// clones destroyed by UnloadKeybindsUI.
        /// </summary>
        public static void Teardown()
        {
            ReenableDisabledActions();
            UIScreenRegistry.Unregister(ScreenId);
            UIScreenRegistry.Unregister(ExtrasScreenId);
            AnnouncerBridge.ClearScreen(ScreenId);
            AnnouncerBridge.ClearScreen(ExtrasScreenId);

            if (extrasAnchor != null)
            {
                UnityEngine.Object.Destroy(extrasAnchor);
                extrasAnchor = null;
            }

            rows.Clear();
            categories.Clear();
            mainSections.Clear();
            mainChain.Clear();
            // The category buttons OUTLIVE this teardown (InputUtils reuses the container), so a
            // listener left on one would fire against the section list that was just cleared.
            UnhookSectionEntries();
            panel = null;

            // Every open starts with the categories closed. The rows stay hidden while the panel
            // is shut, which costs nothing: the next open hides them again before anyone looks.
            expandedCategoryName = null;
            remapContainer = null;
            bindsScrollRect = null;
            lastScrolledSelection = null;

            // Not cleared: activeRebindOption. SettingsOption.OnDisable cancels an in-flight
            // rebind as the panel closes, and the watchdog still has to notice that and restore
            // InputSystem.actions if the game did not.
        }

        /// <summary>
        /// Re-read everything whose visibility or value can change without the panel being
        /// rebuilt: Reset/Remove buttons appear and disappear with RebindButton.UpdateState, and
        /// every binding string changes after a rebind. Registry entries hold live delegates, so
        /// only the horizontal wiring and the announcer's cached positions need refreshing.
        /// </summary>
        public static void RefreshDynamicState()
        {
            if (panel == null)
            {
                return;
            }

            UIScreenRegistry.Invalidate(ScreenId);
            UIScreenRegistry.Invalidate(ExtrasScreenId);

            for (int i = 0; i < rows.Count; i++)
            {
                WireHorizontal(rows[i]);
            }

            // Positions change when a category opens or closes, and the spoken fallback was handed
            // fixed ones.
            PublishToAnnouncer(ScreenId, mainSections);
            PublishToAnnouncer(ExtrasScreenId, publishedExtrasSections);
        }

        // The extras screen's sections as last registered, so a refresh can re-publish them.
        private static List<UISection> publishedExtrasSections = new List<UISection>();

        #endregion

        #region Vanilla branch

        /// <summary>
        /// The shipped UI: one RemapKeySlot clone per remappable key, plus one clone of
        /// sectionTextPrefab ("REBIND CONTROLLERS") that separates the keyboard rows from the
        /// controller rows. That divider carries no SettingsOption, which is exactly how it is
        /// told apart, and it becomes a section boundary rather than a focus stop.
        /// </summary>
        private static void BuildVanilla(Transform root)
        {
            List<UIEntry> keyboard = new List<UIEntry>();
            List<UIEntry> controller = new List<UIEntry>();
            List<UIEntry> current = keyboard;
            bool sawDivider = false;

            for (int i = 0; i < panel.keySlots.Count; i++)
            {
                GameObject slot = panel.keySlots[i];
                if (slot == null)
                {
                    continue;
                }

                SettingsOption option = slot.GetComponentInChildren<SettingsOption>(true);
                if (option == null)
                {
                    if (sawDivider)
                    {
                        Debug.Log("[LethalAccess] Keybinds panel: a second section divider ('" +
                                  slot.name + "') was found. Only two sections are modelled, so its rows " +
                                  "join the controller section.");
                    }
                    sawDivider = true;
                    current = controller;
                    continue;
                }

                Button button = option.GetComponent<Button>();
                if (button == null)
                {
                    // The prefab puts the Button on the SettingsOption object; if that ever
                    // changes, take the nearest one rather than dropping the row.
                    button = slot.GetComponentInChildren<Button>(true);
                    if (button == null)
                    {
                        Debug.LogError("[LethalAccess] Keybinds row '" + slot.name + "' has a SettingsOption " +
                                       "but no Button anywhere under it, so it cannot be focused or activated.");
                        continue;
                    }
                    Debug.Log("[LethalAccess] Keybinds row '" + slot.name + "': the Button is no longer on the " +
                              "SettingsOption object; using the nearest Button in the slot instead.");
                }

                string action = ReadSlotActionName(slot, option);
                if (string.IsNullOrEmpty(action))
                {
                    action = Clean(slot.name.Replace("(Clone)", string.Empty));
                    Debug.LogWarning("[LethalAccess] Keybinds row '" + slot.name + "' has no action-name text; " +
                                     "it will announce as its object name.");
                }

                SettingsOption capturedOption = option;
                Row row = new Row();
                row.Action = action;
                row.Primary = button;
                rows.Add(row);

                UIEntry entry = new UIEntry();
                entry.Direct = button.transform;
                entry.Label = action;
                entry.Type = "button";
                entry.State = delegate { return ReadVanillaBinding(capturedOption); };
                current.Add(entry);

                mainChain.Add(button);
            }

            if (keyboard.Count > 0)
            {
                mainSections.Add(new UISection("Keyboard and mouse", keyboard.ToArray()));
            }
            if (controller.Count > 0)
            {
                mainSections.Add(new UISection("Controller", controller.ToArray()));
            }
        }

        /// <summary>
        /// KepRemapPanel.LoadKeybindsUI writes the action name into the slot's FIRST
        /// TextMeshProUGUI (a SIBLING of the Button), while the binding goes into Button/Key, a
        /// CHILD. That is why a focused row announces the key instead of the action: any
        /// GetComponentInChildren starting at the Button finds Key first and stops.
        /// </summary>
        private static string ReadSlotActionName(GameObject slot, SettingsOption option)
        {
            Transform named = slot.transform.Find("Text (1)");
            if (named != null)
            {
                TextMeshProUGUI tmp = named.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    return Clean(tmp.text);
                }
            }

            // Fall back to "first TMP that is not inside the Button's subtree", which is what
            // LoadKeybindsUI actually targets, rather than trusting the object name.
            Transform optionRoot = option != null ? option.transform : null;
            TextMeshProUGUI[] all = slot.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || IsDescendantOf(all[i].transform, optionRoot))
                {
                    continue;
                }
                string text = Clean(all[i].text);
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
            return null;
        }

        private static string ReadVanillaBinding(SettingsOption option)
        {
            if (option == null || option.currentlyUsedKeyText == null)
            {
                return null;
            }
            string text = Clean(option.currentlyUsedKeyText.text);
            return string.IsNullOrEmpty(text) ? "not bound" : text;
        }

        #endregion

        #region InputUtils branch

        /// <summary>The position group the category headers share, and their section name.</summary>
        private const string CategoriesSection = "Categories";

        /// <summary>
        /// The InputUtils UI: a category list on the left, and a scrolling binds list whose
        /// content holds, in creation order, a section anchor followed by that section's
        /// RebindItems. Only the keyboard-and-mouse bind button of each row joins the vertical
        /// walk; the gamepad bind and the Reset buttons hang off it horizontally.
        ///
        /// The arrow-key order puts each category's rows directly under its header, so a category
        /// reads like a tree: header, its rows when open, the next header.
        /// </summary>
        private static bool BuildInputUtils(Transform root, List<UISection> extrasSections)
        {
            Component container = FindComponent(root, InputUtilsBridge.RemapContainer);
            if (container == null)
            {
                return false;
            }

            Component[] items = root.GetComponentsInChildren(InputUtilsBridge.RebindItem, true);
            if (items == null || items.Length == 0)
            {
                Debug.LogWarning("[LethalAccess] Keybinds panel: an InputUtils remap container is present but it " +
                                 "holds no RebindItems, so there are no bind rows to register.");
                return false;
            }

            remapContainer = container;

            List<UIEntry> options = BuildContainerOptions(root, container);
            if (options.Count > 0)
            {
                mainSections.Add(new UISection("Options", options.ToArray()));
            }

            Dictionary<int, Category> byIndex = BuildCategoryList(root);

            List<UIEntry> gamepadColumn = new List<UIEntry>();
            List<UIEntry> resetColumn = new List<UIEntry>();
            List<Selectable> gamepadChain = new List<Selectable>();
            List<Selectable> resetChain = new List<Selectable>();

            // Every RebindItem and every section anchor is a direct child of the binds list
            // content, so sibling order here IS creation order, the order the user sees.
            Transform content = items[0].transform.parent;
            if (content == null)
            {
                Debug.LogError("[LethalAccess] Keybinds panel: the InputUtils RebindItems have no common parent, " +
                               "so their order and their section membership cannot be determined.");
                return false;
            }
            bindsScrollRect = content.GetComponentInParent<ScrollRect>(true);

            Category current = null;
            int anchorOrdinal = -1;

            for (int i = 0; i < content.childCount; i++)
            {
                Transform child = content.GetChild(i);

                Component anchor = InputUtilsBridge.SectionHeaderAnchor != null
                    ? child.GetComponent(InputUtilsBridge.SectionHeaderAnchor)
                    : null;
                if (anchor != null)
                {
                    // Anchors are created alongside the category entries, one per section and in
                    // the same order, so the n-th anchor is section n.
                    anchorOrdinal++;
                    current = CategoryFor(byIndex, anchorOrdinal, ReadSectionName(anchor));
                    if (bindsTopInset <= 0f)
                    {
                        bindsTopInset = InputUtilsBridge.StickyHeaderHeight(anchor);
                    }
                    continue;
                }

                Component item = child.GetComponent(InputUtilsBridge.RebindItem);
                if (item == null)
                {
                    continue;
                }

                if (current == null)
                {
                    current = CategoryFor(byIndex, 0, null);
                }

                AddRebindRow(item, current, gamepadColumn, resetColumn, gamepadChain, resetChain);
            }

            List<int> order = new List<int>(byIndex.Keys);
            order.Sort();
            for (int i = 0; i < order.Count; i++)
            {
                Category category = byIndex[order[i]];
                categories.Add(category);

                if (category.Header != null)
                {
                    UISection headerSection = new UISection(CategoriesSection, BuildHeaderEntry(category));
                    headerSection.PositionGroup = CategoriesSection;
                    mainSections.Add(headerSection);
                }
                if (category.Entries.Count > 0)
                {
                    mainSections.Add(new UISection(category.Name, category.Entries.ToArray()));
                }
            }

            if (gamepadColumn.Count > 0)
            {
                extrasSections.Add(new UISection("Gamepad bindings", gamepadColumn.ToArray()));
            }
            if (resetColumn.Count > 0)
            {
                extrasSections.Add(new UISection("Reset to default", resetColumn.ToArray()));
            }

            extrasColumnChains.Clear();
            extrasColumnChains.Add(gamepadChain);
            extrasColumnChains.Add(resetChain);

            return true;
        }

        private static Category CategoryFor(Dictionary<int, Category> byIndex, int index, string name)
        {
            Category category;
            if (!byIndex.TryGetValue(index, out category))
            {
                category = new Category();
                category.Index = index;
                category.IsBaseGame = index == 0;
                byIndex[index] = category;
            }
            if (string.IsNullOrEmpty(category.Name))
            {
                category.Name = !string.IsNullOrEmpty(name) ? name : (index == 0 ? "Bindings" : "Section " + (index + 1));
            }
            return category;
        }

        /// <summary>
        /// The category's own entry: a button that opens and closes it. Expanded is the state a
        /// screen reader reads ("collapsed"); State says the same thing for the spoken fallback.
        /// </summary>
        private static UIEntry BuildHeaderEntry(Category category)
        {
            string name = category.Name;
            int count = category.Rows.Count;

            UIEntry entry = new UIEntry();
            entry.Direct = category.Header.transform;
            entry.Label = name;
            entry.Type = "button";
            entry.Expanded = delegate { return expandedCategoryName == name; };
            entry.State = delegate { return expandedCategoryName == name ? "expanded" : "collapsed"; };
            entry.Description = delegate { return count == 1 ? "1 control." : count + " controls."; };
            entry.Help = delegate
            {
                return "Press Enter to show the " + name + " controls and move to the first one. Escape, or " +
                       "moving up past the first or down past the last, hides them again.";
            };
            return entry;
        }

        private static readonly List<List<Selectable>> extrasColumnChains = new List<List<Selectable>>();

        private static void AddRebindRow(Component item, Category category,
                                         List<UIEntry> gamepadColumn, List<UIEntry> resetColumn,
                                         List<Selectable> gamepadChain, List<Selectable> resetChain)
        {
            string action = Clean(ReadText(InputUtilsBridge.GetField(item, InputUtilsBridge.ItemControlNameLabel)));
            if (string.IsNullOrEmpty(action))
            {
                action = Clean(item.gameObject.name.Replace("(Clone)", string.Empty));
            }

            object kbm = InputUtilsBridge.GetField(item, InputUtilsBridge.ItemKbmButton);
            object gamepad = InputUtilsBridge.GetField(item, InputUtilsBridge.ItemGamepadButton);

            Selectable kbmSelectable = InputUtilsBridge.GetSelectable(kbm);
            if (kbmSelectable == null)
            {
                Debug.LogWarning("[LethalAccess] Keybinds row '" + action + "' has no keyboard-and-mouse rebind " +
                                 "button, so it cannot be reached by the arrow keys.");
                return;
            }

            Row row = new Row();
            row.Action = action;
            row.Primary = kbmSelectable;
            row.KbmButton = kbm;
            row.GamepadButton = gamepad;
            row.Category = category;
            row.Item = item.gameObject;
            rows.Add(row);
            category.Rows.Add(row);

            object capturedKbm = kbm;
            UIEntry kbmEntry = new UIEntry();
            kbmEntry.Direct = kbmSelectable.transform;
            kbmEntry.Label = action;
            kbmEntry.Type = "button";
            // Read straight off the binding rather than off bindLabel: RebindButton.UpdateState
            // BLANKS bindLabel whenever it shows a glyph sprite instead (which is every gamepad
            // bind and every mouse bind), and the announcement then falls back to "Rebind Button".
            kbmEntry.State = delegate { return DescribeBinding(capturedKbm); };
            kbmEntry.Description = delegate { return ConflictWarning(capturedKbm); };
            kbmEntry.Help = delegate
            {
                return "Press Enter, then the new key or mouse button, to rebind " + action +
                       ". Escape cancels a rebind. Press Delete to remove this binding. " +
                       "Right arrow reaches reset and the gamepad binding. Escape closes " + category.Name + ".";
            };
            // Left and right are this row's extras. This is the one place in the whole mod where
            // horizontal movement is deliberately kept alive; see ExtrasScreenId.
            kbmEntry.KeepsHorizontal = true;
            // A closed category's rows are switched off on purpose. Without this every one of
            // them logged "resolved but is inactive" on each rebuild, over fifty lines a time,
            // and Unity's logging is slow enough to show in how long the screen takes to open.
            kbmEntry.Optional = category.Header != null;
            category.Entries.Add(kbmEntry);

            row.Horizontal.Add(kbmSelectable);

            // Left-to-right reading order across the row: the keyboard bind and its reset, then
            // the gamepad bind and its reset.
            AddExtra(row, InputUtilsBridge.GetField(kbm, InputUtilsBridge.ButtonResetButton),
                     action + ", reset keyboard binding to default", "button", resetColumn, resetChain, null,
                     "Resets the keyboard binding for " + action + " to its default.");

            Selectable gamepadSelectable = AddExtra(row, gamepad, action + ", gamepad binding", "button",
                     gamepadColumn, gamepadChain, gamepad,
                     "Press Enter, then a gamepad button, to rebind " + action +
                     ". Press Delete to remove this binding.");
            row.GamepadSelectable = gamepadSelectable;

            AddExtra(row, InputUtilsBridge.GetField(gamepad, InputUtilsBridge.ButtonResetButton),
                     action + ", reset gamepad binding to default", "button", resetColumn, resetChain, null,
                     "Resets the gamepad binding for " + action + " to its default.");

            // The Remove buttons are NOT focus stops. Delete does their job from the bind itself,
            // and as stops they were a trap: they were published on a screen the tree did not read,
            // so they were navigable and silent, and a Down press from one walked a column of
            // nothing but Remove buttons. They stay clickable for the mouse.
            Selectable removeKbm = InputUtilsBridge.ResolveSelectable(
                InputUtilsBridge.GetField(kbm, InputUtilsBridge.ButtonRemoveButton));
            Selectable removeGamepad = InputUtilsBridge.ResolveSelectable(
                InputUtilsBridge.GetField(gamepad, InputUtilsBridge.ButtonRemoveButton));
            DropFromKeyboardNavigation(removeKbm);
            DropFromKeyboardNavigation(removeGamepad);
        }

        private static void DropFromKeyboardNavigation(Selectable s)
        {
            if (s == null)
            {
                return;
            }
            Navigation nav = s.navigation;
            nav.mode = Navigation.Mode.None;
            s.navigation = nav;
        }

        /// <summary>
        /// Register one horizontal neighbour of a row. <paramref name="bindingSource"/> is the
        /// RebindButton whose current binding should be read as this control's state, or null when
        /// the control has no value of its own (Reset).
        /// </summary>
        private static Selectable AddExtra(Row row, object component, string label, string type,
                                           List<UIEntry> column, List<Selectable> columnChain, object bindingSource,
                                           string help)
        {
            Selectable selectable = InputUtilsBridge.ResolveSelectable(component);
            if (selectable == null)
            {
                return null;
            }

            row.Horizontal.Add(selectable);
            columnChain.Add(selectable);

            UIEntry entry = new UIEntry();
            entry.Direct = selectable.transform;
            entry.Label = label;
            entry.Type = type;
            entry.KeepsHorizontal = true;
            entry.Help = delegate { return help; };
            // Reset is hidden by RebindButton.UpdateState when it does not apply, so it
            // legitimately resolves-but-is-invisible; the registry skips invisible entries.
            entry.Optional = true;
            if (bindingSource != null)
            {
                object captured = bindingSource;
                entry.State = delegate { return DescribeBinding(captured); };
                entry.Description = delegate { return ConflictWarning(captured); };
            }
            column.Add(entry);
            return selectable;
        }

        /// <summary>
        /// The category list on the left: one SectionEntry button per InputUtils section, keyed by
        /// its section index. Each becomes the open/close control of its category.
        /// </summary>
        private static Dictionary<int, Category> BuildCategoryList(Transform root)
        {
            Dictionary<int, Category> result = new Dictionary<int, Category>();
            if (InputUtilsBridge.SectionEntry == null)
            {
                return result;
            }

            Component[] entries = root.GetComponentsInChildren(InputUtilsBridge.SectionEntry, true);
            if (entries == null)
            {
                return result;
            }

            for (int i = 0; i < entries.Length; i++)
            {
                Component entry = entries[i];
                Selectable selectable = entry.GetComponent<Selectable>();
                if (selectable == null)
                {
                    continue;
                }

                int index = SectionIndexOf(entry);
                if (result.ContainsKey(index))
                {
                    Debug.LogWarning("[LethalAccess] Keybinds: two category buttons claim section " + index +
                                     "; only the first is used.");
                    continue;
                }

                // The first TMP under a SectionEntry is the ">" indicator, which the announcer's
                // special-character stripping reduces to "", hence "SectionEntry". The real name
                // is on the component's own `label` field.
                string name = Clean(ReadText(InputUtilsBridge.GetField(entry, InputUtilsBridge.SectionEntryLabel)));

                Category category = new Category();
                category.Index = index;
                category.IsBaseGame = index == 0;
                category.Name = string.IsNullOrEmpty(name) ? null : name;
                category.Header = selectable;
                result[index] = category;

                HookSectionEntry(entry, index);
            }

            return result;
        }

        private static int SectionIndexOf(Component sectionEntry)
        {
            object value = InputUtilsBridge.GetField(sectionEntry, InputUtilsBridge.SectionEntryIndex);
            return value is int ? (int)value : 0;
        }

        /// <summary>
        /// A category button only scrolls the binds list; it never moves focus, so pressing one
        /// used to be completely silent. Move focus to the first bind of the section it jumped to.
        ///
        /// Remove-then-add rather than a "have I hooked this one before?" test: the removal is
        /// matched against the delegate recorded for this exact Button object, so a re-open cannot
        /// stack a second listener AND a rebuilt button can never be skipped. NEVER
        /// onClick.RemoveAllListeners(): these buttons also carry InputUtils' own
        /// scroll-to-section handler and the game's click sound, and stripping those is the
        /// mistake this mod has to work around elsewhere, not a cheaper way to write this.
        /// </summary>
        private static void HookSectionEntry(Component sectionEntry, int sectionIndex)
        {
            Button button = sectionEntry.GetComponent<Button>();
            if (button == null)
            {
                // BuildCategoryList already registered this entry as a focus stop because it has a
                // Selectable, so without a Button the user can reach it and press it to no effect.
                Debug.LogWarning("[LethalAccess] Keybinds category " + sectionIndex + " has no Button component, " +
                                 "so pressing it cannot open it; it will scroll the list silently.");
                return;
            }

            RemoveHookFor(button);

            int captured = sectionIndex;
            UnityEngine.Events.UnityAction action = delegate { ToggleCategory(captured); };

            SectionHook hook = new SectionHook();
            hook.Button = button;
            hook.Action = action;
            sectionHooks.Add(hook);

            button.onClick.AddListener(action);
        }

        /// <summary>
        /// Drop the listener this class previously put on <paramref name="button"/>, matched by
        /// MANAGED REFERENCE so a destroyed button and a new one that reused its instance id are
        /// never confused: Unity's own == and GetHashCode both compare instance ids, which is
        /// precisely what made the old id set unsafe. Dead entries are pruned on the way past.
        /// </summary>
        private static void RemoveHookFor(Button button)
        {
            for (int i = sectionHooks.Count - 1; i >= 0; i--)
            {
                SectionHook hook = sectionHooks[i];
                bool destroyed = hook.Button == null;   // Unity's overloaded ==, so this covers destroyed too
                if (!destroyed && !object.ReferenceEquals(hook.Button, button))
                {
                    continue;
                }
                if (!destroyed && hook.Action != null)
                {
                    hook.Button.onClick.RemoveListener(hook.Action);
                }
                sectionHooks.RemoveAt(i);
            }
        }

        /// <summary>Take every listener this class added to a category button back off again.</summary>
        private static void UnhookSectionEntries()
        {
            for (int i = 0; i < sectionHooks.Count; i++)
            {
                SectionHook hook = sectionHooks[i];
                if (hook.Button != null && hook.Action != null)
                {
                    hook.Button.onClick.RemoveListener(hook.Action);
                }
            }
            sectionHooks.Clear();
        }

        private static Category FindCategory(int index)
        {
            for (int i = 0; i < categories.Count; i++)
            {
                if (categories[i].Index == index)
                {
                    return categories[i];
                }
            }
            return null;
        }

        private static Category FindCategory(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            for (int i = 0; i < categories.Count; i++)
            {
                if (categories[i].Name == name)
                {
                    return categories[i];
                }
            }
            return null;
        }

        /// <summary>
        /// A category button was pressed (Enter, or a click). InputUtils' own listener has already
        /// scrolled the list to the section; this opens or closes it.
        /// </summary>
        private static void ToggleCategory(int index)
        {
            Category category = FindCategory(index);
            if (category == null)
            {
                Debug.Log("[LethalAccess] Keybinds: category " + index + " was pressed but is not part of the screen model.");
                return;
            }

            if (expandedCategoryName == category.Name)
            {
                Collapse(true);
                return;
            }
            Expand(category);
        }

        /// <summary>Show one category's rows, only one at a time, and move to its first row.</summary>
        private static void Expand(Category category)
        {
            expandedCategoryName = category.Name;
            ApplyCollapse();
            RefreshDynamicState();

            // Scroll so the section starts at the top of the list. JumpTo forces a layout pass
            // first, which is also what makes the rows that were just switched on measurable.
            InputUtilsBridge.JumpTo(remapContainer, category.Index);

            Row first = null;
            for (int i = 0; i < category.Rows.Count; i++)
            {
                Row row = category.Rows[i];
                if (row.Primary != null && UIUtils.IsUIElementVisible(row.Primary.gameObject))
                {
                    first = row;
                    break;
                }
            }

            if (first == null)
            {
                AnnouncementPolicy.Speak(category.Name + " has no controls.", true);
                return;
            }

            FocusManager.CancelSticky();
            FocusManager.Seed(first.Primary.transform);
            ScrollIntoView(first.Primary.gameObject);
        }

        /// <summary>Hide whichever category is open. With focusHeader, focus lands on its button.</summary>
        private static void Collapse(bool focusHeader)
        {
            Category category = FindCategory(expandedCategoryName);
            if (category == null && expandedCategoryName == null)
            {
                return;
            }

            // Cleared first: seeding the header below runs its select hook, which would otherwise
            // see a category still open and come straight back here.
            expandedCategoryName = null;
            ApplyCollapse();
            RefreshDynamicState();

            if (focusHeader && category != null && category.Header != null)
            {
                FocusManager.Seed(category.Header.transform);
            }
        }

        /// <summary>
        /// Switch every category's rows on or off to match expandedCategoryName. A category with no
        /// header has nothing to open it with, so its rows always stay shown.
        ///
        /// Deactivating a row takes its RebindButtons out of InputUtils' instance list, which is
        /// what "reset all" walks; ExpandAllForBulkReset puts them back for that.
        /// </summary>
        private static void ApplyCollapse()
        {
            for (int c = 0; c < categories.Count; c++)
            {
                Category category = categories[c];
                bool show = category.Header == null || category.Name == expandedCategoryName;
                for (int r = 0; r < category.Rows.Count; r++)
                {
                    GameObject item = category.Rows[r].Item;
                    if (item != null && item.activeSelf != show)
                    {
                        item.SetActive(show);
                    }
                }
            }
        }

        /// <summary>Prefix on RebindButton.ResetAllToDefaults, which only reaches active rows.</summary>
        internal static void ExpandAllForBulkReset()
        {
            for (int r = 0; r < rows.Count; r++)
            {
                if (rows[r].Item != null && !rows[r].Item.activeSelf)
                {
                    rows[r].Item.SetActive(true);
                }
            }
        }

        /// <summary>Postfix on RebindButton.ResetAllToDefaults: close again what was closed.</summary>
        internal static void RestoreCollapseAfterBulkReset()
        {
            ApplyCollapse();
        }

        /// <summary>
        /// Escape, from either the menu-scene handler or the in-game routes. True when it was used
        /// here (closing the open category), so nothing else may act on the same press.
        /// </summary>
        internal static bool HandleEscape()
        {
            if (!IsPanelOpen)
            {
                return false;
            }

            int frame = Time.frameCount;
            if (escapeConsumedFrame == frame)
            {
                return true;
            }

            // The rebind owns Escape: it cancels it. A rebind that ended this very frame was most
            // likely cancelled by this press, which must not then also close something.
            if (pendingInputUtilsRebind != null || activeRebindOption != null || frame - lastRebindEndFrame <= 1)
            {
                escapeConsumedFrame = frame;
                return true;
            }

            // An open option list (Control priority) is the dropdown's: its own Cancel closes it.
            // IsExpanded holds through the fade-out, so this also covers a Cancel that ran first.
            EventSystem es = EventSystem.current;
            GameObject selected = es != null ? es.currentSelectedGameObject : null;
            TMPro.TMP_Dropdown openList = selected != null ? selected.GetComponentInParent<TMPro.TMP_Dropdown>() : null;
            if (openList != null && openList.IsExpanded)
            {
                escapeConsumedFrame = frame;
                return true;
            }

            if (expandedCategoryName == null)
            {
                return false;
            }

            escapeConsumedFrame = frame;
            Collapse(true);
            return true;
        }

        /// <summary>
        /// Something outside the open category's rows took focus: one of the category buttons
        /// (including its own), the options at the top or the panel's own buttons. Moving out of the
        /// list closes it. Rows of other categories cannot be reached while closed, so these are
        /// the only ways out.
        /// </summary>
        internal static void NoteSelectedOutsideRows()
        {
            if (panel == null || expandedCategoryName == null)
            {
                return;
            }
            Collapse(false);
        }

        /// <summary>
        /// Puts a select hook on every control that is outside the category rows. A hook rather than
        /// a per-frame check, so the category is already closed in the same frame focus arrives:
        /// the screen reader then hears "collapsed" once, not "expanded" and a correction.
        /// </summary>
        private static void WatchForLeavingRows()
        {
            for (int s = 0; s < mainSections.Count; s++)
            {
                List<UIEntry> entries = mainSections[s].Entries;
                for (int e = 0; e < entries.Count; e++)
                {
                    Transform t = entries[e].Direct;
                    if (t == null || IsRowControl(t.gameObject))
                    {
                        continue;
                    }
                    if (t.GetComponent<KeybindsLeaveRowsWatcher>() == null)
                    {
                        t.gameObject.AddComponent<KeybindsLeaveRowsWatcher>();
                    }
                }
            }
        }

        private static bool IsRowControl(GameObject go)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Primary != null && rows[i].Primary.gameObject == go)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The panel-wide InputUtils controls. Two of them are not Selectables at all:
        /// SwitchClickZone implements only IPointerClickHandler, so the Global/Local save-context
        /// switch is mouse-only until a Button is synthesised onto it.
        /// </summary>
        private static List<UIEntry> BuildContainerOptions(Transform root, Component container)
        {
            List<UIEntry> result = new List<UIEntry>();

            Component contextSwitch = FindComponent(root, InputUtilsBridge.ContextSwitch);
            if (contextSwitch != null && InputUtilsBridge.SwitchClickZone != null)
            {
                // The "?" next to the switch is a PopOverTrigger on a child that only reacts to the
                // mouse. Its text is read here so H can speak it on either button.
                string switchHelp = FormatHelp(InputUtilsBridge.ReadPopOverRaw(contextSwitch.gameObject));

                Component[] zones = contextSwitch.GetComponentsInChildren(InputUtilsBridge.SwitchClickZone, true);
                for (int i = 0; zones != null && i < zones.Length; i++)
                {
                    Button synthesised = InputUtilsBridge.MakeClickZoneFocusable(zones[i]);
                    if (synthesised == null)
                    {
                        continue;
                    }

                    // Match on the object name ("Global"/"Local"), not the label: the label is a
                    // TMP that a locale can translate, while BindingOverrideType.ToString() cannot.
                    string captured = zones[i].gameObject.name;

                    // "Save bindings Global" described neither what the control is nor what it does:
                    // it is not a save command, it picks which set of controls this screen shows and
                    // edits. The two are mutually exclusive, which is what a radio button is.
                    string shown = Clean(FirstChildText(zones[i].transform));
                    if (string.IsNullOrEmpty(shown))
                    {
                        shown = captured;
                    }
                    string help = switchHelp;

                    UIEntry entry = new UIEntry();
                    entry.Direct = synthesised.transform;
                    entry.Label = shown + " controls";
                    entry.Type = "radio button";
                    entry.State = delegate { return InputUtilsBridge.DescribeContext(captured); };
                    entry.Description = delegate { return string.IsNullOrEmpty(help) ? null : "Press H for help."; };
                    entry.Help = delegate { return help; };
                    result.Add(entry);
                    mainChain.Add(synthesised);
                }
            }

            TMP_Dropdown priority = FindDropdown(root, container);
            if (priority != null)
            {
                // The "?" lives on the dropdown's container, beside it rather than on it.
                string priorityHelp = FormatHelp(InputUtilsBridge.ReadPopOverRaw(
                    priority.transform.parent != null ? priority.transform.parent.gameObject : priority.gameObject));

                UIEntry entry = new UIEntry();
                entry.Direct = priority.transform;
                entry.Label = "Control priority";
                entry.Type = "dropdown";
                entry.Description = delegate { return string.IsNullOrEmpty(priorityHelp) ? null : "Press H for help."; };
                entry.Help = delegate { return priorityHelp; };
                // Deliberately NOT KeepsHorizontal. TMP_Dropdown does not override the navigation
                // finders the way Slider does, so left/right would not cycle its options even if
                // they were allowed through; that would need a cycling handler of its own. Submit
                // opens the option list, whose Toggles are unclaimed and navigate normally.
                result.Add(entry);
                mainChain.Add(priority);
            }

            // "Show Legacy Controls" is deliberately NOT registered when it would open an empty
            // panel. InputUtils empties remappableKeys before LoadKeybindsUI runs, so the legacy
            // holder contains zero rows on every install where InputUtils is active; the button
            // is left on screen for mouse users but is not a keyboard stop.
            Button legacy = InputUtilsBridge.GetField(container, InputUtilsBridge.ContainerLegacyButton) as Button;
            if (legacy != null)
            {
                int legacyCount = CountVanillaRows();
                if (legacyCount > 0)
                {
                    UIEntry entry = new UIEntry();
                    entry.Direct = legacy.transform;
                    entry.Label = "Show legacy controls, " + legacyCount + " remaining";
                    entry.Type = "button";
                    result.Add(entry);
                    mainChain.Add(legacy);
                }
                else
                {
                    Debug.Log("[LethalAccess] Keybinds: 'Show Legacy Controls' holds 0 rows, so it is not " +
                              "registered as a keyboard stop.");
                }
            }

            return result;
        }

        private static int CountVanillaRows()
        {
            int count = 0;
            if (panel == null)
            {
                return 0;
            }
            for (int i = 0; i < panel.keySlots.Count; i++)
            {
                if (panel.keySlots[i] != null && panel.keySlots[i].GetComponentInChildren<SettingsOption>(true) != null)
                {
                    count++;
                }
            }
            return count;
        }

        private static TMP_Dropdown FindDropdown(Transform root, Component container)
        {
            TMP_Dropdown fromField =
                InputUtilsBridge.GetField(container, InputUtilsBridge.ContainerPriorityDropdown) as TMP_Dropdown;
            if (fromField != null)
            {
                return fromField;
            }
            return root.GetComponentInChildren<TMP_Dropdown>(true);
        }

        private static string ReadSectionName(Component anchor)
        {
            object header = InputUtilsBridge.GetField(anchor, InputUtilsBridge.AnchorSectionHeader);
            if (header == null)
            {
                return null;
            }
            return Clean(ReadText(InputUtilsBridge.GetField(header, InputUtilsBridge.SectionHeaderLabel)));
        }

        private static string DescribeBinding(object rebindButton)
        {
            return InputUtilsBridge.DescribeBinding(rebindButton);
        }

        // The RebindButton whose rebind we announced the start of. FinishRebinding is also called
        // from OnDisable, ResetToDefault and RemoveBind, where no rebind was ever in flight.
        private static Component pendingInputUtilsRebind;

        // Where the binding stood when the rebind began, so the end can say whether it moved.
        private static string pendingStartPath;

        // What the FinishRebinding prefix saw on the operation. FinishRebinding nulls it, so by the
        // time any postfix runs the evidence is gone.
        private static bool finishSawCanceled;
        private static bool finishSawTimeout;

        /// <summary>
        /// InputUtils waits 5 seconds by default. That is long enough to find a key you can see and
        /// not long enough to find one you cannot, and nothing about missing it is recoverable
        /// except starting over.
        /// </summary>
        private const float InputUtilsRebindSeconds = 10f;

        /// <summary>True from the moment an InputUtils rebind begins until it ends.</summary>
        public static bool InputUtilsRebindInProgress
        {
            get { return pendingInputUtilsRebind != null; }
        }

        /// <summary>
        /// An InputUtils rebind is not the vanilla hard lock: RebindButton.Update cancels it
        /// after `timeout` seconds and its OnCancel restores the row, but it is silent, and it
        /// suppresses every matching input event for the whole of that window, so the prompt has
        /// to say how the user gets out and how long they have.
        /// </summary>
        public static void AnnounceInputUtilsRebindStart(Component rebindButton)
        {
            if (rebindButton == null)
            {
                return;
            }

            pendingInputUtilsRebind = rebindButton;
            pendingStartPath = InputUtilsBridge.EffectivePath(rebindButton);
            finishSawCanceled = false;
            finishSawTimeout = false;
            InputUtilsBridge.SetRebindTimeout(rebindButton, InputUtilsRebindSeconds);

            string action = InputUtilsBridge.RowActionName(rebindButton);
            bool gamepad = InputUtilsBridge.IsGamepadRow(rebindButton);
            int seconds = Mathf.Max(1, Mathf.RoundToInt(InputUtilsRebindSeconds));

            if (gamepad)
            {
                // RebindButton.RebindGamepad registers no canceling control at all; the timeout is
                // genuinely the only way out.
                AnnouncementPolicy.Speak("Press the new gamepad binding for " + action +
                                         ". There is no cancel button; this times out in " + seconds + " seconds.", true);
                return;
            }

            // RebindKbm cancels through the literal "<Keyboard>/escape", not through the OpenMenu
            // action, so this really is Escape regardless of how OpenMenu is bound.
            AnnouncementPolicy.Speak("Press the new keyboard or mouse binding for " + action +
                                     ". Press Escape to cancel, or wait " + seconds + " seconds.", true);
        }

        // Actions this class switched off so a rebind could start. InputUtils switches every base
        // game action off while the screen is open and back on when it closes; this puts back only
        // what it had to take, in case it closes the screen before InputUtils' own pass.
        private static readonly List<InputAction> disabledForRebind = new List<InputAction>();

        /// <summary>
        /// Prefix on RebindButton.StartRebinding. Input System refuses to start an interactive
        /// rebind of an action that is enabled ("Cannot rebind action ... while it is enabled")
        /// and when it throws, StartRebinding never reaches its postfix: no prompt, no sound, and a
        /// bind that simply does nothing. Observed live on a composite (Walk forward) while the
        /// action was enabled for a reason this class never found. Switching the one action off
        /// first is exactly what InputUtils already does for all of them.
        /// </summary>
        public static void PrepareRebindTarget(Component rebindButton)
        {
            RemappableKey key = InputUtilsBridge.GetField(rebindButton, InputUtilsBridge.ButtonKey) as RemappableKey;
            InputAction action = key != null && key.currentInput != null ? key.currentInput.action : null;
            if (action == null || !action.enabled)
            {
                return;
            }

            Debug.Log("[LethalAccess] Keybinds: '" + action.name + "' was enabled, which Input System refuses to " +
                      "rebind; disabling it for the rebind.");
            action.Disable();
            if (!disabledForRebind.Contains(action))
            {
                disabledForRebind.Add(action);
            }
        }

        /// <summary>StartRebinding threw. Say so: the alternative is a bind that silently does nothing.</summary>
        public static void ReportRebindFailure(Component rebindButton, Exception error)
        {
            string action = InputUtilsBridge.RowActionName(rebindButton);
            Debug.LogError("[LethalAccess] Keybinds: rebinding '" + action + "' failed to start: " + error);
            pendingInputUtilsRebind = null;
            lastRebindEndFrame = Time.frameCount;
            AnnouncementPolicy.Speak("Could not start rebinding " + action + ".", true);
        }

        private static void ReenableDisabledActions()
        {
            for (int i = 0; i < disabledForRebind.Count; i++)
            {
                InputAction a = disabledForRebind[i];
                if (a != null && a.actionMap != null && a.actionMap.asset != null && !a.enabled)
                {
                    a.Enable();
                }
            }
            disabledForRebind.Clear();
        }

        /// <summary>
        /// Prefix on FinishRebinding. The only moment the operation still exists, and so the only
        /// moment "completed", "cancelled" and "timed out" can be told apart.
        /// </summary>
        public static void NoteInputUtilsRebindFinishing(Component rebindButton)
        {
            if (rebindButton == null || pendingInputUtilsRebind != rebindButton)
            {
                return;
            }
            InputUtilsBridge.ReadOperationOutcome(rebindButton, out finishSawCanceled, out finishSawTimeout);
        }

        /// <summary>
        /// FinishRebinding is reached by a completed rebind, a cancelled one and a timed-out one.
        /// Each gets its own sentence, because "Push to talk is now T" after a timeout reads as
        /// though the key you just pressed was accepted.
        /// </summary>
        public static void AnnounceInputUtilsRebindEnd(Component rebindButton)
        {
            if (rebindButton == null || pendingInputUtilsRebind != rebindButton)
            {
                return;
            }
            pendingInputUtilsRebind = null;
            lastRebindEndFrame = Time.frameCount;
            InvalidateConflicts();

            string action = InputUtilsBridge.RowActionName(rebindButton);
            string kind = InputUtilsBridge.IsGamepadRow(rebindButton) ? "gamepad" : "keyboard";
            string binding = InputUtilsBridge.DescribeBinding(rebindButton);
            if (string.IsNullOrEmpty(binding))
            {
                binding = "not bound";
            }

            string text;
            if (finishSawCanceled)
            {
                text = (finishSawTimeout ? "Timed out. " : "Cancelled. ") + action + " is still " + binding + ".";
            }
            else
            {
                string now = InputUtilsBridge.EffectivePath(rebindButton);
                bool moved = !string.Equals(now, pendingStartPath, StringComparison.OrdinalIgnoreCase);
                text = moved
                    ? action + ", " + kind + " binding set to " + binding + "."
                    : action + " is still " + binding + ".";

                string warning = ConflictWarning(rebindButton);
                if (!string.IsNullOrEmpty(warning))
                {
                    text += " " + warning;
                }
            }

            pendingStartPath = null;
            AnnouncementPolicy.Speak(text, true);
            RefreshDynamicState();
        }

        /// <summary>Reset-to-default and remove-binding outcomes.</summary>
        public static void AnnounceInputUtilsBindingChanged(Component rebindButton, string verb, bool includeBinding)
        {
            if (rebindButton == null)
            {
                return;
            }

            InvalidateConflicts();
            string action = InputUtilsBridge.RowActionName(rebindButton);
            string kind = InputUtilsBridge.IsGamepadRow(rebindButton) ? "gamepad" : "keyboard";

            if (!includeBinding)
            {
                AnnouncementPolicy.Speak(action + ", " + kind + " binding " + verb);
            }
            else
            {
                string binding = InputUtilsBridge.DescribeBinding(rebindButton);
                if (string.IsNullOrEmpty(binding))
                {
                    binding = "not bound";
                }
                AnnouncementPolicy.Speak(action + ", " + kind + " binding " + verb + ", now " + binding);
            }

            RefreshDynamicState();
            LeaveHiddenResetButton(rebindButton);
        }

        /// <summary>
        /// RebindButton.UpdateState hides a Reset button as soon as its binding is back at the
        /// default, which pressing it just did. The EventSystem keeps a hidden object selected, and
        /// no arrow key moves from it, so the keyboard went dead after every reset. Focus moves to
        /// the binding that was reset.
        /// </summary>
        private static void LeaveHiddenResetButton(Component rebindButton)
        {
            EventSystem es = EventSystem.current;
            GameObject selected = es != null ? es.currentSelectedGameObject : null;
            if (selected == null || selected.activeInHierarchy)
            {
                return;
            }

            Selectable bind = InputUtilsBridge.GetSelectable(rebindButton);
            if (bind == null || !UIUtils.IsUIElementVisible(bind.gameObject))
            {
                return;
            }

            // The result was just queued; without this the focus change would flush it.
            SpeechUtils.PreserveQueuedThroughNextFocusChange();
            FocusManager.Seed(bind.transform);
        }

        #endregion

        #region Conflicts, help and Delete

        /// <summary>True while the remap-controls panel is built and showing.</summary>
        public static bool IsPanelOpen
        {
            get { return panel != null && panel.gameObject.activeInHierarchy; }
        }

        // Conflicts are worked out for every row at once and kept until a binding changes. They
        // used to be recomputed per row on every read (each row scanning every other row by
        // reflection), and the accessibility tree reads every row's description on each rebuild,
        // twice a second, which made the open screen stutter.
        private static int bindingGeneration;
        private static int conflictsBuiltFor = -1;
        private static readonly Dictionary<object, string> conflictByButton = new Dictionary<object, string>();

        /// <summary>A binding may have changed. Cheap; the work happens on the next read.</summary>
        internal static void InvalidateConflicts()
        {
            bindingGeneration++;
        }

        /// <summary>
        /// "Conflict: also used by Walk left and Speak time (LethalAccess).", or null when this
        /// binding is its own. Live, so every row sharing a binding reports it, not only the one
        /// that was just changed.
        /// </summary>
        internal static string ConflictWarning(object rebindButton)
        {
            if (rebindButton == null)
            {
                return null;
            }
            if (conflictsBuiltFor != bindingGeneration)
            {
                BuildConflicts();
            }
            string text;
            return conflictByButton.TryGetValue(rebindButton, out text) ? text : null;
        }

        private sealed class BindSlot
        {
            public object Button;
            public Row Row;
            public bool Overridden;
        }

        private static void BuildConflicts()
        {
            conflictsBuiltFor = bindingGeneration;
            conflictByButton.Clear();

            // Keyboard and gamepad bindings never collide with each other, so they are grouped
            // separately.
            Dictionary<string, List<BindSlot>> byPath = new Dictionary<string, List<BindSlot>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows.Count; i++)
            {
                AddSlot(byPath, rows[i], rows[i].KbmButton, "k:");
                AddSlot(byPath, rows[i], rows[i].GamepadButton, "g:");
            }

            // LethalAccess's own keys are only rows here when InputUtils hosts them; otherwise they
            // are config keys the mod reads directly, and are added by name.
            bool modKeysAreRows = ModKeybinds.HostedByInputUtils;

            foreach (KeyValuePair<string, List<BindSlot>> group in byPath)
            {
                bool keyboard = group.Key.StartsWith("k:", StringComparison.Ordinal);
                List<string> modUses = null;
                if (keyboard && !modKeysAreRows)
                {
                    Key key;
                    if (TryParseKeyboardPath(group.Key.Substring(2), out key))
                    {
                        modUses = LACore.DescribeModKeyUses(key);
                    }
                }

                List<BindSlot> slots = group.Value;
                if (slots.Count < 2 && (modUses == null || modUses.Count == 0))
                {
                    continue;
                }

                for (int i = 0; i < slots.Count; i++)
                {
                    BindSlot mine = slots[i];
                    List<string> names = new List<string>();

                    for (int j = 0; j < slots.Count; j++)
                    {
                        BindSlot other = slots[j];
                        if (j == i || other.Row == mine.Row)
                        {
                            continue;
                        }

                        // Two controls the game itself ships on one key (Interact and the third item
                        // action both default to E) are a design, not a mistake: the game uses them
                        // in different situations. Only within the game's own section, and only while
                        // both are still at their defaults. A mod key on a game key always fires
                        // both, so that is always said.
                        bool sameSection = other.Row.Category == mine.Row.Category;
                        if (sameSection && mine.Row.Category != null && mine.Row.Category.IsBaseGame &&
                            !mine.Overridden && !other.Overridden)
                        {
                            continue;
                        }

                        string name = other.Row.Action;
                        if (!sameSection && other.Row.Category != null)
                        {
                            name += " (" + other.Row.Category.Name + ")";
                        }
                        if (!names.Contains(name))
                        {
                            names.Add(name);
                        }
                    }

                    if (modUses != null)
                    {
                        for (int m = 0; m < modUses.Count; m++)
                        {
                            if (!names.Contains(modUses[m]))
                            {
                                names.Add(modUses[m]);
                            }
                        }
                    }

                    if (names.Count > 0)
                    {
                        conflictByButton[mine.Button] = "Conflict: also used by " + JoinNames(names) + ".";
                    }
                }
            }
        }

        private static void AddSlot(Dictionary<string, List<BindSlot>> byPath, Row row, object button, string prefix)
        {
            if (button == null)
            {
                return;
            }
            string path = InputUtilsBridge.EffectivePath(button);
            if (InputUtilsBridge.IsUnboundPath(path))
            {
                return;
            }

            BindSlot slot = new BindSlot();
            slot.Button = button;
            slot.Row = row;
            slot.Overridden = InputUtilsBridge.HasOverride(button);

            string key = prefix + path;
            List<BindSlot> list;
            if (!byPath.TryGetValue(key, out list))
            {
                list = new List<BindSlot>(2);
                byPath[key] = list;
            }
            list.Add(slot);
        }

        private static string JoinNames(List<string> names)
        {
            if (names.Count == 1)
            {
                return names[0];
            }
            string last = names[names.Count - 1];
            return string.Join(", ", names.GetRange(0, names.Count - 1).ToArray()) + " and " + last;
        }

        /// <summary>"&lt;Keyboard&gt;/leftArrow" becomes Key.LeftArrow. False for mouse, gamepad and anything unmapped.</summary>
        private static bool TryParseKeyboardPath(string path, out Key key)
        {
            key = Key.None;
            const string prefix = "<Keyboard>/";
            if (string.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string name = path.Substring(prefix.Length);
            if (name.StartsWith("#(") && name.EndsWith(")") && name.Length == 4)
            {
                name = name.Substring(2, 1);
            }
            if (name.Length == 1 && char.IsDigit(name[0]))
            {
                name = "Digit" + name;
            }

            return Enum.TryParse(name, true, out key) && key != Key.None;
        }

        /// <summary>
        /// Pop-over text is written with blank lines and " - " list markers for a tooltip bubble;
        /// spoken, that is a wall. Sentences read better.
        /// </summary>
        private static string FormatHelp(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            string[] parts = text.Replace("\r", string.Empty).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            System.Text.StringBuilder sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string line = parts[i].Trim().Replace(" - ", ": ");
                if (line.Length == 0)
                {
                    continue;
                }
                sb.Append(line);
                char end = line[line.Length - 1];
                if (end != '.' && end != '!' && end != '?' && end != ':')
                {
                    sb.Append('.');
                }
                sb.Append(' ');
            }
            return Clean(sb.ToString());
        }

        /// <summary>
        /// Per-frame keys for the remap screen, driven from the panel's own watchdog component.
        ///
        ///   H       speaks the focused control's help. The "?" beside Global/Local and the priority
        ///           dropdown is a mouse-hover tooltip and nothing else.
        ///   Delete  removes the focused keyboard or gamepad binding, replacing the Remove buttons
        ///           that used to be focus stops.
        ///
        /// Both are ignored while any rebind is waiting for a key. That is not politeness: the key
        /// being bound IS a key press, and Delete or H bound as a new key must not also run.
        /// </summary>
        internal static void TickKeys()
        {
            if (panel == null)
            {
                return;
            }

            // Keep the focused control on screen. The selection changes inside the EventSystem's
            // own update, so a frame's delay here is the earliest the layout can be measured anyway.
            EventSystem current = EventSystem.current;
            GameObject selection = current != null ? current.currentSelectedGameObject : null;
            if (selection != lastScrolledSelection)
            {
                lastScrolledSelection = selection;
                if (selection != null)
                {
                    ScrollIntoView(selection);
                }
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            bool help = keyboard.hKey.wasPressedThisFrame;
            bool delete = keyboard.deleteKey.wasPressedThisFrame;
            if (!help && !delete)
            {
                return;
            }

            if (pendingInputUtilsRebind != null || activeRebindOption != null)
            {
                return;
            }

            EventSystem es = EventSystem.current;
            GameObject focused = es != null ? es.currentSelectedGameObject : null;
            if (focused == null)
            {
                return;
            }

            if (help)
            {
                SpeakHelpFor(focused);
            }
            else
            {
                RemoveBindingAt(focused);
            }
        }

        private static void SpeakHelpFor(GameObject focused)
        {
            UIEntry entry;
            string text = null;
            if (UIScreenRegistry.TryGetEntry(focused, out entry) && entry != null && entry.Help != null)
            {
                text = entry.Help();
            }

            AnnouncementPolicy.Speak(string.IsNullOrEmpty(text) ? "No help for this control." : text, true);
        }

        private static void RemoveBindingAt(GameObject focused)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                object target = null;
                string kind = null;

                if (row.Primary != null && row.Primary.gameObject == focused)
                {
                    target = row.KbmButton;
                    kind = "keyboard";
                }
                else if (row.GamepadSelectable != null && row.GamepadSelectable.gameObject == focused)
                {
                    target = row.GamepadButton;
                    kind = "gamepad";
                }

                if (target == null)
                {
                    continue;
                }

                if (InputUtilsBridge.IsUnboundPath(InputUtilsBridge.EffectivePath(target)))
                {
                    AnnouncementPolicy.Speak(row.Action + " has no " + kind + " binding to remove.", true);
                    return;
                }

                // RemoveBind's own postfix says "removed" and refreshes the screen.
                InputUtilsBridge.RemoveBind(target);
                return;
            }

            AnnouncementPolicy.Speak("Delete removes a binding. Move to a control's keyboard or gamepad binding first.", true);
        }

        #endregion

        #region Scrolling

        /// <summary>
        /// Scroll whichever list holds this control just far enough that it is fully on screen.
        ///
        /// Neither list follows the keyboard by itself: a ScrollRect only moves for the mouse wheel,
        /// a drag or its scrollbar, so arrowing down the binds left the highlight below the bottom
        /// edge, out of sight of anyone watching. A bind row is scrolled as a whole row, so its
        /// gamepad and reset buttons come with it, and the binds list keeps the row below the
        /// sticky section header that covers its top.
        /// </summary>
        private static void ScrollIntoView(GameObject selected)
        {
            if (selected == null || !selected.activeInHierarchy)
            {
                return;
            }

            ScrollRect scroll = selected.GetComponentInParent<ScrollRect>();
            if (scroll == null || scroll.content == null || !IsDescendantOf(selected.transform, scroll.content))
            {
                return;
            }

            RectTransform target = selected.transform as RectTransform;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Item != null && IsDescendantOf(selected.transform, rows[i].Item.transform))
                {
                    target = rows[i].Item.transform as RectTransform;
                    break;
                }
            }
            if (target == null)
            {
                return;
            }

            RectTransform viewport = scroll.viewport != null ? scroll.viewport : scroll.transform as RectTransform;
            if (viewport == null)
            {
                return;
            }

            Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(viewport, target);
            Rect view = viewport.rect;
            float top = view.yMax - (scroll == bindsScrollRect ? bindsTopInset : 0f);
            float bottom = view.yMin;

            // Positive: the control is above the visible area; negative: below it.
            float overshoot = 0f;
            if (bounds.max.y > top || bounds.size.y > top - bottom)
            {
                overshoot = bounds.max.y - top;
            }
            else if (bounds.min.y < bottom)
            {
                overshoot = bounds.min.y - bottom;
            }
            if (Mathf.Abs(overshoot) < 0.5f)
            {
                return;
            }

            scroll.StopMovement();
            Vector2 position = scroll.content.anchoredPosition;
            position.y -= overshoot;
            scroll.content.anchoredPosition = position;

            // Never past either end: an elastic list would spring back and take the control with it.
            float normalized = scroll.verticalNormalizedPosition;
            if (normalized < 0f || normalized > 1f)
            {
                scroll.verticalNormalizedPosition = Mathf.Clamp01(normalized);
            }
        }

        #endregion

        #region Panel actions

        /// <summary>
        /// Back and SetDefault ship identical Explicit targets that are ALL OUTSIDE the panel
        /// (up = ControlsOptions/LookSensitivity/Slider, down = SettingsPanel/BackButton,
        /// right = SettingsPanel/SetToDefault), and KeybindsPanel is a child of SettingsPanel with
        /// nothing deactivating the parent's controls, so any direction from either button drops
        /// the user onto the settings screen behind the overlay. Both are re-wired here and both
        /// are claimed by the screen, which is what stops the arrow keys leaving the panel at all.
        ///
        /// Both child names are live-confirmed against the running game: the KepRemapPanel object
        /// (Canvas/MenuContainer/SettingsPanel/KeybindsPanel) has exactly BG, "Controls (1)", Back,
        /// SetDefault and Scroll View as DIRECT children, and Back and SetDefault are both Buttons
        /// shipping navigation.mode Explicit. FindByPathIncludingInactive walks direct children
        /// only, which is why that distinction matters.
        /// </summary>
        private static void AddPanelActions(Transform root)
        {
            List<UIEntry> actions = new List<UIEntry>();
            bool haveSetDefault = false;
            bool haveBack = false;

            Transform setDefault = UIUtils.FindByPathIncludingInactive(root, "SetDefault");
            if (setDefault != null)
            {
                Selectable s = setDefault.GetComponent<Selectable>();
                if (s != null)
                {
                    UIEntry entry = new UIEntry();
                    entry.Direct = setDefault;
                    entry.Label = "Reset all keybinds to default";
                    entry.Type = "button";
                    entry.State = delegate { return "destructive"; };
                    actions.Add(entry);
                    mainChain.Add(s);
                    haveSetDefault = true;
                }
            }

            Transform back = UIUtils.FindByPathIncludingInactive(root, "Back");
            if (back != null)
            {
                Selectable s = back.GetComponent<Selectable>();
                if (s != null)
                {
                    UIEntry entry = new UIEntry();
                    entry.Direct = back;
                    entry.Label = "Back";
                    entry.Type = "button";
                    actions.Add(entry);
                    mainChain.Add(s);
                    haveBack = true;
                }
            }

            if (actions.Count > 0)
            {
                mainSections.Add(new UISection("Panel actions", actions.ToArray()));
            }

            // Warn when EITHER fails, not only when both do. Losing just one still costs the panel
            // something the keyboard cannot get back: an unclaimed button keeps its shipped
            // Explicit targets, every one of which points OUTSIDE this overlay.
            if (!haveBack && !haveSetDefault)
            {
                Debug.LogWarning("[LethalAccess] Keybinds panel: neither 'Back' nor 'SetDefault' resolved to a " +
                                 "Selectable, so the panel has no registered way out and the arrow keys may escape " +
                                 "it onto the settings screen behind.");
            }
            else if (!haveBack)
            {
                Debug.LogWarning("[LethalAccess] Keybinds panel: 'Back' did not resolve to a Selectable. It is the " +
                                 "panel's only registered way out, so the keyboard now has none, and its shipped " +
                                 "Explicit navigation still points at the settings screen behind this overlay.");
            }
            else if (!haveSetDefault)
            {
                Debug.LogWarning("[LethalAccess] Keybinds panel: 'SetDefault' did not resolve to a Selectable, so " +
                                 "'reset all keybinds to default' cannot be reached from the keyboard at all.");
            }
        }

        #endregion

        #region Registration and navigation

        private static void RegisterScreens(List<UISection> extrasSections)
        {
            UIScreenDescriptor main = new UIScreenDescriptor();
            main.Id = ScreenId;
            main.ResolveRoot = ResolvePanelRoot;
            main.Sections = new List<UISection>(mainSections);
            main.Title = ResolveTitle;
            main.Hint = ResolveHint;
            main.FocusOnOpen = ResolveOpeningFocus;
            main.OnEscape = HandleEscape;

            // A dialog in the tree. Its name and description are then read when focus first lands
            // inside, which is how the "press H for help" line gets heard: a screen reader does not
            // read a plain pane's name on arrival at all, and the title used to be one.
            main.Modal = true;
            if (extrasSections.Count > 0)
            {
                // The gamepad and reset buttons are on their own screen for keyboard order only;
                // the tree has to publish them under this one or they are focusable and unread.
                main.CompanionScreens = new List<string> { ExtrasScreenId };
            }

            // Register can announce and seed focus synchronously when the root is already active,
            // which it is, because this runs from LoadKeybindsUI inside OnEnable. That is the
            // intended path for the first build of a panel open.
            bool wasActive = UIScreenRegistry.ActiveScreenId == ScreenId;
            UIScreenRegistry.Register(main);

            // Register only resolves entries as a side effect of ScreenShown, and ScreenShown is
            // skipped when the notifier already exists, which is the case for the SECOND
            // LoadKeybindsUI of every InputUtils panel open. Resolve explicitly so the flat order
            // is never empty.
            UIScreenRegistry.Invalidate(ScreenId);

            if (extrasSections.Count > 0)
            {
                EnsureExtrasAnchor();

                UIScreenDescriptor extras = new UIScreenDescriptor();
                extras.Id = ExtrasScreenId;
                extras.ResolveRoot = ResolveExtrasRoot;
                extras.Sections = new List<UISection>(extrasSections);
                extras.Title = null;
                UIScreenRegistry.Register(extras);
                UIScreenRegistry.Invalidate(ExtrasScreenId);
            }
            else
            {
                // A rebuild that produced no row extras (the vanilla UI, or a UI whose gamepad
                // column vanished) must not leave the previous registration claiming destroyed
                // objects; claims are what the arrow keys consult.
                UIScreenRegistry.Unregister(ExtrasScreenId);
            }

            publishedExtrasSections = new List<UISection>(extrasSections);
            PublishToAnnouncer(ScreenId, mainSections);
            PublishToAnnouncer(ExtrasScreenId, publishedExtrasSections);

            if (!wasActive && UIScreenRegistry.ActiveScreenId != ScreenId && IsPanelVisible())
            {
                // The notifier was already on the root (a rebuild while the panel stayed open, or
                // a re-registration), so nothing announced the screen. Do it once.
                UIScreenRegistry.ScreenShown(ScreenId);
            }

            // Something later in the panel's open sequence selects its Back button, overwriting the
            // seed ScreenShown just made. Live-confirmed: the flat order resolves 25 bind rows and
            // FocusOnOpen names the first, yet focus lands on "Panel actions, Back, button, 2 of 2"
            // and stays there, so the user arrives on the way OUT of a screen they just opened.
            // A one-shot seed cannot win that race; this re-asserts across it and stops as soon as
            // it lands.
            if (IsPanelVisible())
            {
                Transform first = ResolveOpeningFocus();
                if (first != null)
                {
                    FocusManager.SeedSticky(first, 12);
                }
                else
                {
                    Debug.LogWarning("[LethalAccess] Keybinds panel: nothing visible to focus, so the panel " +
                                     "opens whereever the game last selected. The rows may not have been built yet.");
                }
            }
        }

        /// <summary>
        /// The extras screen is anchored to a deliberately INACTIVE empty GameObject.
        ///
        /// UIScreenRegistry.Register attaches a ScreenActivationNotifier to a screen's root; on an
        /// active root that fires immediately and both announces the screen and moves focus into
        /// it. The extras are not a screen the user ever "opens": they are horizontal neighbours
        /// of rows on another screen, so their root must never become active. An inactive anchor
        /// gives them registry claims (and therefore labels, positions and KeepsHorizontal)
        /// without ever announcing or stealing focus. Entry resolution is unaffected because every
        /// extras entry resolves through Direct, not through a path under the root.
        /// </summary>
        private static void EnsureExtrasAnchor()
        {
            if (extrasAnchor != null)
            {
                return;
            }
            extrasAnchor = new GameObject("LethalAccess Keybind Row Controls (registry anchor)");
            extrasAnchor.SetActive(false);
            if (panel != null)
            {
                extrasAnchor.transform.SetParent(panel.transform, false);
            }
        }

        private static void PublishToAnnouncer(string screenId, List<UISection> sections)
        {
            AnnouncerBridge.ClearScreen(screenId);

            for (int s = 0; s < sections.Count; s++)
            {
                UISection section = sections[s];
                int visible = 0;
                for (int e = 0; e < section.Entries.Count; e++)
                {
                    if (IsEntryVisible(section.Entries[e]))
                    {
                        visible++;
                    }
                }

                int index = 0;
                for (int e = 0; e < section.Entries.Count; e++)
                {
                    UIEntry entry = section.Entries[e];

                    // A category header is a one-entry section of a wider set ("Categories"), and
                    // only the registry knows its place in that set.
                    int groupIndex = 0;
                    int groupCount = 0;
                    string ignored;
                    bool grouped = !string.IsNullOrEmpty(section.PositionGroup) && entry.Direct != null &&
                                   UIScreenRegistry.TryGetPosition(entry.Direct.gameObject, out groupIndex,
                                                                   out groupCount, out ignored);
                    if (grouped)
                    {
                        UIEntry headerEntry = entry;
                        AnnouncerBridge.RegisterObject(entry.Direct.gameObject,
                            delegate { return ComposeText(headerEntry); },
                            entry.Type, screenId, groupIndex, groupCount);
                        continue;
                    }

                    // Only VISIBLE entries are registered, and `index` only advances for them.
                    // Registering a hidden entry gave it the LAST VISIBLE entry's index; it kept
                    // the position it inherited, so if RebindButton.UpdateState later revealed it
                    // (Reset and Remove appear and disappear with the binding) without a rebuild it
                    // announced a duplicate position. Nothing is lost by the skip: a hidden control
                    // cannot be focused, and when it is revealed UIScreenRegistry still claims it,
                    // supplying both its label+state (UIAccessibilityManager.GetElementText source
                    // 2 composes exactly the same string as ComposeText below) and its true
                    // position (TryGetPosition recomputes over live visibility). IsEntryVisible
                    // also covers Direct == null.
                    if (!IsEntryVisible(entry))
                    {
                        continue;
                    }
                    index++;

                    UIEntry captured = entry;
                    AnnouncerBridge.RegisterObject(
                        entry.Direct.gameObject,
                        delegate { return ComposeText(captured); },
                        entry.Type,
                        screenId,
                        index,
                        visible);
                }
            }
        }

        private static bool IsEntryVisible(UIEntry entry)
        {
            return entry.Direct != null && UIUtils.IsUIElementVisible(entry.Direct.gameObject);
        }

        private static string ComposeText(UIEntry entry)
        {
            string label = entry.Label;
            string state = entry.State != null ? entry.State() : null;
            if (string.IsNullOrEmpty(state))
            {
                return label;
            }
            return string.IsNullOrEmpty(label) ? state : label + ", " + state;
        }

        private static void WireNavigation()
        {
            // With VerticalNavigationOnly on, up/down for claimed controls comes from the registry
            // and this chain is not consulted. It is still required: the RemapKeySlot prefab ships
            // navigation.mode = None, so with the option off every finder returns null and no row
            // can ever be selected by the keyboard at all.
            NavigationEnforcer.BuildChain(mainChain, false);

            for (int i = 0; i < extrasColumnChains.Count; i++)
            {
                if (extrasColumnChains[i].Count > 0)
                {
                    NavigationEnforcer.BuildChain(extrasColumnChains[i], false);
                }
            }

            // BuildChain nulls both horizontals, so the row wiring has to come afterwards.
            for (int i = 0; i < rows.Count; i++)
            {
                WireHorizontal(rows[i]);
            }
        }

        /// <summary>
        /// Left/right across one row, skipping controls that RebindButton.UpdateState has hidden.
        /// Up and down are left exactly as BuildChain wrote them.
        /// </summary>
        private static void WireHorizontal(Row row)
        {
            List<Selectable> live = new List<Selectable>(row.Horizontal.Count);
            for (int i = 0; i < row.Horizontal.Count; i++)
            {
                Selectable s = row.Horizontal[i];
                if (s != null && s.gameObject.activeInHierarchy && s.IsInteractable())
                {
                    live.Add(s);
                }
            }

            for (int i = 0; i < live.Count; i++)
            {
                Selectable s = live[i];
                Navigation nav = s.navigation;
                nav.mode = Navigation.Mode.Explicit;
                nav.selectOnLeft = i > 0 ? live[i - 1] : null;
                nav.selectOnRight = i < live.Count - 1 ? live[i + 1] : null;
                s.navigation = nav;
            }
        }

        /// <summary>
        /// Scrollbars are focus stops that announce "Handle, ui element" and are the usual way a
        /// horizontal press escapes a scrolling panel. Taking them out of navigation entirely
        /// leaves the mouse wheel and the ScrollRect untouched.
        /// </summary>
        private static void IsolateScrollbars(Transform root)
        {
            Scrollbar[] bars = root.GetComponentsInChildren<Scrollbar>(true);
            for (int i = 0; i < bars.Length; i++)
            {
                NavigationEnforcer.Isolate(bars[i]);
            }
        }

        #endregion

        #region Screen resolution helpers

        private static Transform ResolvePanelRoot()
        {
            return panel != null ? panel.transform : null;
        }

        private static Transform ResolveExtrasRoot()
        {
            return extrasAnchor != null ? extrasAnchor.transform : null;
        }

        private static bool IsPanelVisible()
        {
            return panel != null && UIUtils.IsUIElementVisible(panel.gameObject);
        }

        private static Transform ResolveFirstBindRow()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                Selectable s = rows[i].Primary;
                if (s != null && UIUtils.IsUIElementVisible(s.gameObject))
                {
                    return s.transform;
                }
            }
            return null;
        }

        /// <summary>
        /// The first category button when the categories are collapsible (the user picks one),
        /// otherwise the first row.
        /// </summary>
        private static Transform ResolveOpeningFocus()
        {
            for (int i = 0; i < categories.Count; i++)
            {
                Selectable header = categories[i].Header;
                if (header != null && UIUtils.IsUIElementVisible(header.gameObject))
                {
                    return header.transform;
                }
            }
            return ResolveFirstBindRow();
        }

        /// <summary>Read once on arrival, after the title.</summary>
        private static string ResolveHint()
        {
            const string keys = "Press H for help on the focused control, and Delete to remove a binding.";
            for (int i = 0; i < categories.Count; i++)
            {
                if (categories[i].Header != null)
                {
                    return "Each section is collapsed: press Enter on one to show its controls. " + keys;
                }
            }
            return keys;
        }

        /// <summary>
        /// The panel heading is a bare TextMeshProUGUI on no Selectable, so it has never been
        /// announced. Read it live rather than hard-coding a title: the same trap as the news
        /// popup's stale "Welcome to Version 80!".
        /// </summary>
        private static string ResolveTitle()
        {
            Transform root = ResolvePanelRoot();
            if (root != null)
            {
                Transform heading = UIUtils.FindByPathIncludingInactive(root, "Controls (1)");
                if (heading != null)
                {
                    TextMeshProUGUI tmp = heading.GetComponent<TextMeshProUGUI>();
                    if (tmp == null)
                    {
                        tmp = heading.GetComponentInChildren<TextMeshProUGUI>(true);
                    }
                    string live = Clean(tmp != null ? tmp.text : null);
                    if (!string.IsNullOrEmpty(live))
                    {
                        return live;
                    }
                }
            }
            // The object name is live-confirmed: KeybindsPanel's heading child is "Controls (1)",
            // a bare TextMeshProUGUI currently reading "REMAP CONTROLS". This fallback is reached
            // only if a later version renames that child or leaves its text empty, and it will not
            // follow a localisation, which is why the live read above is tried first.
            return "Remap controls";
        }

        #endregion

        #region Rebind prompt and watchdog

        /// <summary>
        /// Record that a vanilla rebind has begun. Called from the RebindKey PREFIX so the
        /// watchdog knows which row to restore even if RebindKey itself throws, which it does
        /// when OpenMenu has no bound control, AFTER InputSystem.actions.Disable() has run.
        /// </summary>
        public static void NoteRebindStarting(SettingsOption optionUI)
        {
            activeRebindOption = optionUI;
            activeRebindStartedAt = Time.unscaledTime;
            lastPromptAt = Time.unscaledTime;
            activeRebindPrompt = null;
        }

        /// <summary>Called from the RebindKey POSTFIX, once the operation is actually running.</summary>
        public static void NoteRebindStarted(SettingsOption optionUI, string prompt)
        {
            activeRebindOption = optionUI;
            activeRebindPrompt = prompt;
            activeRebindStartedAt = Time.unscaledTime;
            lastPromptAt = Time.unscaledTime;
        }

        public static void ClearActiveRebind()
        {
            activeRebindOption = null;
            activeRebindPrompt = null;
        }

        /// <summary>
        /// The action name of a vanilla keybind row, read from the slot rather than from the
        /// focused Button: the binding lives under the Button, the action name is a sibling.
        /// </summary>
        public static string ActionNameFor(SettingsOption optionUI)
        {
            if (optionUI == null || optionUI.transform.parent == null)
            {
                return "this control";
            }
            string name = ReadSlotActionName(optionUI.transform.parent.gameObject, optionUI);
            return string.IsNullOrEmpty(name) ? "this control" : name;
        }

        public static string CurrentBindingFor(SettingsOption optionUI)
        {
            string binding = ReadVanillaBinding(optionUI);
            return string.IsNullOrEmpty(binding) ? "not bound" : binding;
        }

        /// <summary>
        /// The exact control RebindKey passed to WithCancelingThrough, not a guess at "Escape".
        /// Returns null when OpenMenu has no such control, which is the case that makes the
        /// timeout the only way out.
        /// </summary>
        public static string CancelControlName(bool gamepadRebinding)
        {
            if (InputSystem.actions == null)
            {
                return null;
            }
            InputAction openMenu = InputSystem.actions.FindAction("OpenMenu");
            if (openMenu == null)
            {
                return null;
            }
            int index = gamepadRebinding ? 1 : 0;
            if (openMenu.controls.Count <= index)
            {
                return null;
            }
            InputControl control = openMenu.controls[index];
            if (control == null)
            {
                return null;
            }
            return string.IsNullOrEmpty(control.displayName) ? control.name : control.displayName;
        }

        public static string BuildRebindPrompt(SettingsOption optionUI, bool gamepadRebinding)
        {
            string action = ActionNameFor(optionUI);
            string cancel = CancelControlName(gamepadRebinding);
            string wait = "or wait " + Mathf.RoundToInt(RebindTimeoutSeconds) + " seconds";

            if (string.IsNullOrEmpty(cancel))
            {
                return "Press the new key for " + action +
                       ". No cancel key is bound, so nothing else will work until you press a key " + wait + ".";
            }
            return "Press the new key for " + action + ". Press " + cancel + " to cancel, " + wait + ".";
        }

        private static void EnsureWatchdog()
        {
            if (panel == null)
            {
                return;
            }
            if (panel.gameObject.GetComponent<KeybindsRebindWatchdog>() == null)
            {
                panel.gameObject.AddComponent<KeybindsRebindWatchdog>();
            }
        }

        /// <summary>
        /// Runs every frame while the keybinds panel is open. Two jobs, both of which exist only
        /// because a vanilla rebind is otherwise a silent, permanent trap:
        ///
        ///   1. Repeat the "waiting for a key" prompt, because every matching input event is
        ///      suppressed (RebindingOperation.OnEvent sets eventPtr.handled) and
        ///      InputSystem.actions is disabled, so nothing else in the game can speak.
        ///   2. Notice that the operation has ENDED while the row is still showing
        ///      "waitingForInput", and restore the game. This is not hypothetical: the operation's
        ///      cancel path (the OpenMenu control passed to WithCancelingThrough) has no callback
        ///      attached, so vanilla leaves InputSystem.actions DISABLED and the row stuck until
        ///      the whole panel is closed. Calling the game's own CancelRebind here is what
        ///      re-enables input.
        /// </summary>
        internal static void TickWatchdog()
        {
            SettingsOption option = activeRebindOption;
            if (option == null)
            {
                return;
            }

            IngamePlayerSettings settings = IngamePlayerSettings.Instance;
            if (settings == null)
            {
                ClearActiveRebind();
                return;
            }

            bool waiting = option.waitingForInput != null && option.waitingForInput.activeSelf;
            if (!waiting)
            {
                // CompleteRebind or SettingsOption.OnDisable already put the row back.
                ClearActiveRebind();
                return;
            }

            // rebindingOperation is NEVER set back to null: the game only Disposes it, and
            // Dispose does not clear the operation's flags. `started` is the only field that
            // actually tracks whether a rebind is in flight.
            InputActionRebindingExtensions.RebindingOperation operation = settings.rebindingOperation;
            bool live = operation != null && operation.started;

            float now = Time.unscaledTime;

            if (live)
            {
                if (!string.IsNullOrEmpty(activeRebindPrompt) && now - lastPromptAt >= PromptRepeatSeconds)
                {
                    lastPromptAt = now;
                    AnnouncementPolicy.Speak(activeRebindPrompt);
                }

                if (now - activeRebindStartedAt >= RebindTimeoutSeconds)
                {
                    Debug.Log("[LethalAccess] Keybind rebind timed out after " + RebindTimeoutSeconds +
                              " s and was cancelled; the vanilla operation has no timeout of its own.");
                    operation.Cancel();
                }
                return;
            }

            // Started has cleared but the row still says "waiting": the operation was cancelled
            // (or threw) and nothing restored the game.
            Debug.Log("[LethalAccess] Keybind rebind ended without the game restoring input; " +
                      "calling CancelRebind to re-enable InputSystem.actions and reset the row.");
            settings.CancelRebind(option);
            // CancelRebind's postfix announces the outcome; it checks that this option was the
            // active one, so clearing has to happen after.
            ClearActiveRebind();
        }

        /// <summary>
        /// Re-select the row that now occupies a remembered position. "Set to defaults" destroys
        /// every slot including the focused one, and Unity has no focus recovery of its own.
        /// </summary>
        public static bool TryFocusPosition(string sectionName, int oneBasedIndex)
        {
            if (string.IsNullOrEmpty(sectionName) || oneBasedIndex < 1)
            {
                return false;
            }

            // The rebuild that destroyed the old row also re-armed the opening seed, which would
            // take focus back to the first category a frame later.
            FocusManager.CancelSticky();

            for (int s = 0; s < mainSections.Count; s++)
            {
                if (mainSections[s].Name != sectionName)
                {
                    continue;
                }

                int visible = 0;
                List<UIEntry> entries = mainSections[s].Entries;
                for (int e = 0; e < entries.Count; e++)
                {
                    if (!IsEntryVisible(entries[e]))
                    {
                        continue;
                    }
                    visible++;
                    if (visible == oneBasedIndex)
                    {
                        FocusManager.Seed(entries[e].Direct);
                        return true;
                    }
                }

                // The section got shorter; land on its last row rather than nowhere.
                for (int e = entries.Count - 1; e >= 0; e--)
                {
                    if (IsEntryVisible(entries[e]))
                    {
                        FocusManager.Seed(entries[e].Direct);
                        return true;
                    }
                }
                return false;
            }
            return false;
        }

        #endregion

        #region Small helpers

        private static bool IsDescendantOf(Transform candidate, Transform ancestor)
        {
            if (ancestor == null || candidate == null)
            {
                return false;
            }
            Transform t = candidate;
            while (t != null)
            {
                if (t == ancestor)
                {
                    return true;
                }
                t = t.parent;
            }
            return false;
        }

        private static Component FindComponent(Transform root, Type type)
        {
            if (root == null || type == null)
            {
                return null;
            }
            return root.GetComponentInChildren(type, true);
        }

        private static string ReadText(object maybeTmp)
        {
            TMP_Text tmp = maybeTmp as TMP_Text;
            return tmp != null ? tmp.text : null;
        }

        private static string FirstChildText(Transform t)
        {
            if (t == null)
            {
                return null;
            }
            TMP_Text tmp = t.GetComponentInChildren<TMP_Text>(true);
            return tmp != null ? tmp.text : null;
        }

        /// <summary>Collapse the newlines and runs of spaces TMP labels are full of.</summary>
        internal static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder(text.Length);
            bool lastWasSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool isSpace = c == ' ' || c == '\t' || c == '\n' || c == '\r';
                if (isSpace)
                {
                    if (!lastWasSpace && sb.Length > 0)
                    {
                        sb.Append(' ');
                    }
                    lastWasSpace = true;
                    continue;
                }
                lastWasSpace = false;
                sb.Append(c);
            }

            string result = sb.ToString().Trim();
            return result.Length == 0 ? null : result;
        }

        #endregion
    }

    /// <summary>
    /// Drives KeybindsAccessibility.TickWatchdog. Lives on the KepRemapPanel GameObject so it
    /// stops by itself when the panel closes, and uses unscaled time because the in-game pause
    /// menu runs at timeScale 0 where every Time.time-based timer silently never fires.
    /// </summary>
    public sealed class KeybindsRebindWatchdog : MonoBehaviour
    {
        private void Update()
        {
            KeybindsAccessibility.TickWatchdog();
            KeybindsAccessibility.TickKeys();
        }
    }

    /// <summary>
    /// On every keybinds-screen control that is not a bind row: being selected means the user has
    /// left the open category's rows, which closes it. Holds no state, so it is harmless on a
    /// control that outlives the screen model (InputUtils reuses its container).
    /// </summary>
    public sealed class KeybindsLeaveRowsWatcher : MonoBehaviour, ISelectHandler
    {
        public void OnSelect(BaseEventData eventData)
        {
            KeybindsAccessibility.NoteSelectedOutsideRows();
        }
    }

    /// <summary>
    /// Everything that talks to LethalCompanyInputUtils, by reflection only.
    ///
    /// The mod must load and work when InputUtils is absent, so there is no assembly reference and
    /// no type in a signature anywhere outside this class. Every member is resolved once; anything
    /// missing is reported once, loudly, and degrades to the vanilla branch rather than throwing
    /// inside a UI callback.
    /// </summary>
    internal static class InputUtilsBridge
    {
        private const string Ns = "LethalCompanyInputUtils.Components.";

        private static bool probed;
        private static bool available;

        internal static Type RemapContainer;
        internal static Type RebindItem;
        internal static Type RebindButton;
        internal static Type SectionEntry;
        internal static Type SectionHeaderAnchor;
        internal static Type SectionHeader;
        internal static Type SwitchClickZone;
        internal static Type ContextSwitch;
        internal static Type PopOverTrigger;

        internal static FieldInfo ItemControlNameLabel;
        internal static FieldInfo ItemKbmButton;
        internal static FieldInfo ItemGamepadButton;

        internal static FieldInfo ButtonSelectable;
        internal static FieldInfo ButtonResetButton;
        internal static FieldInfo ButtonRemoveButton;
        internal static FieldInfo ButtonKey;
        internal static FieldInfo ButtonTimeout;
        internal static FieldInfo ButtonTimeoutTimer;
        internal static FieldInfo ButtonOperation;
        internal static MethodInfo ButtonRemoveBind;

        internal static FieldInfo SectionEntryLabel;
        internal static FieldInfo SectionEntryIndex;
        internal static FieldInfo AnchorSectionHeader;
        internal static FieldInfo SectionHeaderLabel;

        internal static FieldInfo ClickZoneOnClick;
        internal static FieldInfo ContextCurrent;

        internal static FieldInfo TriggerText;
        internal static FieldInfo TriggerIsToken;

        internal static FieldInfo ContainerLegacyButton;
        internal static FieldInfo ContainerPriorityDropdown;
        internal static MethodInfo ContainerJumpTo;
        internal static MethodInfo ContainerHideHighestLayer;
        internal static MethodInfo ButtonUpdateState;

        private static MethodInfo localeGetString;
        private static MethodInfo localeContainsToken;

        /// <summary>
        /// An InputUtils translation token ("OverridePriority.PreferGlobal") as the player reads it,
        /// or the text unchanged when it is not one. The priority dropdown's option list is built
        /// from raw tokens and a LocaleResolver translates each label on its first Update, so the
        /// option focused when the list opens is announced a frame before that happens.
        /// </summary>
        internal static string TranslateToken(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('.') < 0 || !Available ||
                localeContainsToken == null || localeGetString == null)
            {
                return text;
            }
            try
            {
                object known = localeContainsToken.Invoke(null, new object[] { text.Trim() });
                if (!(known is bool) || !(bool)known)
                {
                    return text;
                }
                string translated = localeGetString.Invoke(null, new object[] { text.Trim() }) as string;
                return string.IsNullOrEmpty(translated) ? text : translated;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LethalAccess] Could not translate InputUtils token '" + text + "': " + e.Message);
                localeContainsToken = null;
                return text;
            }
        }

        internal static bool Available
        {
            get
            {
                if (!probed)
                {
                    Probe();
                }
                return available;
            }
        }

        /// <summary>
        /// The InputUtils assembly, found through its plugin entry. Types are then looked up in it
        /// directly. AccessTools.TypeByName (what this used before) falls back to walking every
        /// type of every loaded assembly when a name is not in mscorlib, which with eleven lookups
        /// and a Roslyn-sized assembly loaded cost about 650 ms, all of it inside the first open
        /// of the keybinds screen.
        /// </summary>
        private static Assembly FindInputUtilsAssembly()
        {
            BepInEx.PluginInfo info;
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(ModKeybinds.InputUtilsGuid, out info) &&
                info != null && info.Instance != null)
            {
                return info.Instance.GetType().Assembly;
            }

            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < loaded.Length; i++)
            {
                if (loaded[i].GetName().Name == "LethalCompanyInputUtils")
                {
                    return loaded[i];
                }
            }
            return null;
        }

        private static Assembly inputUtils;

        private static Type FindType(string fullName)
        {
            return inputUtils != null ? inputUtils.GetType(fullName, false) : null;
        }

        private static void Probe()
        {
            probed = true;

            inputUtils = FindInputUtilsAssembly();
            RemapContainer = FindType(Ns + "RemapContainerController");
            if (RemapContainer == null)
            {
                Debug.Log("[LethalAccess] LethalCompanyInputUtils is not installed; using the vanilla keybinds UI.");
                return;
            }

            RebindItem = FindType(Ns + "RebindItem");
            RebindButton = FindType(Ns + "RebindButton");
            SectionEntry = FindType(Ns + "Section.SectionEntry");
            SectionHeaderAnchor = FindType(Ns + "Section.SectionHeaderAnchor");
            SectionHeader = FindType(Ns + "Section.SectionHeader");
            SwitchClickZone = FindType(Ns + "Switch.SwitchClickZone");
            ContextSwitch = FindType(Ns + "Switch.BindingOverrideContextSwitch");
            PopOverTrigger = FindType(Ns + "PopOvers.PopOverTrigger");

            if (RebindItem == null || RebindButton == null)
            {
                Debug.LogError("[LethalAccess] LethalCompanyInputUtils is installed but RebindItem/RebindButton could " +
                               "not be found. The keybinds screen will fall back to the vanilla layout, which " +
                               "InputUtils has emptied - expect no reachable rows. This mod needs updating for " +
                               "the installed InputUtils version.");
                return;
            }

            ItemControlNameLabel = AccessTools.Field(RebindItem, "controlNameLabel");
            ItemKbmButton = AccessTools.Field(RebindItem, "kbmButton");
            ItemGamepadButton = AccessTools.Field(RebindItem, "gamepadButton");

            ButtonSelectable = AccessTools.Field(RebindButton, "button");
            ButtonResetButton = AccessTools.Field(RebindButton, "resetButton");
            ButtonRemoveButton = AccessTools.Field(RebindButton, "removeButton");
            ButtonKey = AccessTools.Field(RebindButton, "_key");
            ButtonTimeout = AccessTools.Field(RebindButton, "timeout");
            ButtonTimeoutTimer = AccessTools.Field(RebindButton, "_timeoutTimer");
            ButtonOperation = AccessTools.Field(RebindButton, "_rebindingOperation");
            ButtonRemoveBind = AccessTools.Method(RebindButton, "RemoveBind");

            SectionEntryLabel = SectionEntry != null ? AccessTools.Field(SectionEntry, "label") : null;
            SectionEntryIndex = SectionEntry != null ? AccessTools.Field(SectionEntry, "sectionIndex") : null;
            AnchorSectionHeader = SectionHeaderAnchor != null
                ? AccessTools.Field(SectionHeaderAnchor, "sectionHeader") : null;
            SectionHeaderLabel = SectionHeader != null ? AccessTools.Field(SectionHeader, "label") : null;

            ClickZoneOnClick = SwitchClickZone != null ? AccessTools.Field(SwitchClickZone, "onClick") : null;
            ContextCurrent = ContextSwitch != null ? AccessTools.Field(ContextSwitch, "currentContext") : null;

            TriggerText = PopOverTrigger != null ? AccessTools.Field(PopOverTrigger, "text") : null;
            TriggerIsToken = PopOverTrigger != null ? AccessTools.Field(PopOverTrigger, "textIsLangToken") : null;

            ContainerLegacyButton = AccessTools.Field(RemapContainer, "legacyButton");
            ContainerPriorityDropdown = AccessTools.Field(RemapContainer, "overridePriorityDropdown");
            ContainerJumpTo = AccessTools.Method(RemapContainer, "JumpTo", new Type[] { typeof(int) });
            ContainerHideHighestLayer = AccessTools.Method(RemapContainer, "HideHighestLayer");
            ButtonUpdateState = AccessTools.Method(RebindButton, "UpdateState");

            Type locale = FindType("LethalCompanyInputUtils.Localization.LocaleManager");
            localeGetString = locale != null ? AccessTools.Method(locale, "GetString", new Type[] { typeof(string) }) : null;
            localeContainsToken = locale != null ? AccessTools.Method(locale, "ContainsToken", new Type[] { typeof(string) }) : null;

            if (ItemControlNameLabel == null || ItemKbmButton == null || ButtonSelectable == null || ButtonKey == null)
            {
                Debug.LogError("[LethalAccess] LethalCompanyInputUtils is installed but its RebindItem/RebindButton " +
                               "fields have been renamed. Keybind rows would announce nothing useful, so the " +
                               "InputUtils branch is disabled.");
                return;
            }

            available = true;
        }

        internal static object GetField(object target, FieldInfo field)
        {
            if (target == null || field == null)
            {
                return null;
            }
            object value = field.GetValue(target);
            UnityEngine.Object unityValue = value as UnityEngine.Object;
            if (unityValue != null)
            {
                // Unity's overloaded == is the only thing that reports a destroyed object as
                // absent; a plain null check would hand back a dead reference.
                return unityValue == null ? null : value;
            }
            return value;
        }

        internal static Selectable GetSelectable(object rebindButton)
        {
            object value = GetField(rebindButton, ButtonSelectable);
            return value as Selectable;
        }

        /// <summary>RemapContainerController.JumpTo: scroll the binds list so a section starts at the top.</summary>
        internal static void JumpTo(Component container, int sectionIndex)
        {
            if (container == null || ContainerJumpTo == null)
            {
                return;
            }
            ContainerJumpTo.Invoke(container, new object[] { sectionIndex });
        }

        /// <summary>
        /// Height of the section header that sticks to the top of the binds list. The anchor's own
        /// height is no use: InputUtils zeroes the first one.
        /// </summary>
        internal static float StickyHeaderHeight(Component anchor)
        {
            Component header = GetField(anchor, AnchorSectionHeader) as Component;
            RectTransform rect = header != null ? header.transform as RectTransform : null;
            return rect != null ? rect.rect.height : 0f;
        }

        /// <summary>
        /// A row's horizontal neighbours are a mix of RebindButtons (whose Selectable lives in a
        /// `button` field) and plain Buttons. Reading the RebindButton field off a plain Button
        /// would throw, so the type has to be checked first rather than caught afterwards.
        /// </summary>
        internal static Selectable ResolveSelectable(object component)
        {
            Component c = component as Component;
            if (c == null)
            {
                return null;
            }
            if (RebindButton != null && RebindButton.IsInstanceOfType(c))
            {
                return GetSelectable(c);
            }
            Selectable direct = c as Selectable;
            return direct != null ? direct : c.GetComponent<Selectable>();
        }

        /// <summary>
        /// The binding a RebindButton currently shows, read from the action rather than from
        /// bindLabel. UpdateState blanks bindLabel whenever it uses a glyph sprite instead (every
        /// gamepad bind and every mouse bind), which is why those rows announced "Rebind Button".
        /// InputUtils postfixes InputControlPath.ToHumanReadableString to turn its two
        /// "not bound" sentinel paths into an empty string, so an empty result means unbound.
        /// </summary>
        internal static string DescribeBinding(object rebindButton)
        {
            RemappableKey key = GetField(rebindButton, ButtonKey) as RemappableKey;
            if (key == null || key.currentInput == null)
            {
                return null;
            }

            InputAction action = key.currentInput.action;
            if (action == null)
            {
                return null;
            }

            int index = RebindingIndexOf(key, action);
            if (index < 0 || index >= action.bindings.Count)
            {
                return "not supported on this device";
            }

            string path = action.bindings[index].effectivePath;
            if (path == null)
            {
                return "not bound";
            }

            // The conversion walks the control layouts and was most of what reading the screen
            // cost; a path always reads the same, so each is converted once.
            string human;
            if (!humanReadable.TryGetValue(path, out human))
            {
                human = ModKeybinds.SpokenKeyName(InputControlPath.ToHumanReadableString(
                    path, InputControlPath.HumanReadableStringOptions.OmitDevice));
                humanReadable[path] = human;
            }
            return string.IsNullOrEmpty(human) ? "not bound" : human;
        }

        private static readonly Dictionary<string, string> humanReadable = new Dictionary<string, string>();

        /// <summary>The raw effective binding path of a RebindButton, or null when it has no key.</summary>
        internal static string EffectivePath(object rebindButton)
        {
            RemappableKey key = GetField(rebindButton, ButtonKey) as RemappableKey;
            if (key == null || key.currentInput == null)
            {
                return null;
            }

            InputAction action = key.currentInput.action;
            if (action == null)
            {
                return null;
            }

            int index = RebindingIndexOf(key, action);
            if (index < 0 || index >= action.bindings.Count)
            {
                return null;
            }
            return action.bindings[index].effectivePath;
        }

        /// <summary>
        /// InputUtils stores "no binding" as two sentinel paths, and a gamepad row that was never
        /// bound reports an empty one. None of them is a key anyone pressed, so none can conflict.
        /// </summary>
        internal static bool IsUnboundPath(string path)
        {
            return string.IsNullOrEmpty(path) ||
                   path.IndexOf("Not-Bound", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True when the user has moved this binding off the game's default.</summary>
        internal static bool HasOverride(object rebindButton)
        {
            RemappableKey key = GetField(rebindButton, ButtonKey) as RemappableKey;
            if (key == null || key.currentInput == null || key.currentInput.action == null)
            {
                return false;
            }

            InputAction action = key.currentInput.action;
            int index = RebindingIndexOf(key, action);
            return index >= 0 && index < action.bindings.Count && action.bindings[index].hasOverrides;
        }

        internal static bool IsGamepadButton(object rebindButton)
        {
            RemappableKey key = GetField(rebindButton, ButtonKey) as RemappableKey;
            return key != null && key.gamepadOnly;
        }

        /// <summary>RebindButton.RemoveBind, so Delete does exactly what the Remove button does.</summary>
        internal static void RemoveBind(object rebindButton)
        {
            if (rebindButton == null || ButtonRemoveBind == null)
            {
                Debug.LogError("[LethalAccess] RebindButton.RemoveBind was not found, so Delete cannot remove a binding.");
                return;
            }
            ButtonRemoveBind.Invoke(rebindButton, null);
        }

        /// <summary>
        /// Give a rebind more than InputUtils' 5 seconds. StartRebinding has already armed the
        /// timer from `timeout`, so both are set: the field for the next rebind and the timer for
        /// this one.
        /// </summary>
        internal static void SetRebindTimeout(object rebindButton, float seconds)
        {
            if (rebindButton == null)
            {
                return;
            }
            if (ButtonTimeoutTimer != null)
            {
                ButtonTimeoutTimer.SetValue(rebindButton, seconds);
            }
        }

        /// <summary>
        /// What the in-flight operation looks like at the instant FinishRebinding starts: cancelled
        /// or not, and whether the timer had run out. Nothing afterwards can tell: the method
        /// nulls the operation as its first act.
        /// </summary>
        internal static void ReadOperationOutcome(object rebindButton, out bool canceled, out bool timedOut)
        {
            canceled = false;
            timedOut = false;
            if (rebindButton == null)
            {
                return;
            }

            object op = ButtonOperation != null ? ButtonOperation.GetValue(rebindButton) : null;
            InputActionRebindingExtensions.RebindingOperation operation =
                op as InputActionRebindingExtensions.RebindingOperation;
            canceled = operation != null && operation.canceled;

            if (ButtonTimeoutTimer != null)
            {
                object t = ButtonTimeoutTimer.GetValue(rebindButton);
                timedOut = t is float && (float)t <= 0f;
            }
        }

        /// <summary>
        /// The action name for the row a RebindButton belongs to. Read from the RebindItem's own
        /// controlNameLabel field; the label is a SIBLING of the bind button, so anything that
        /// searches downwards from the button finds the binding instead.
        /// </summary>
        internal static string RowActionName(Component rebindButton)
        {
            if (rebindButton == null || RebindItem == null)
            {
                return "this control";
            }

            Component item = rebindButton.GetComponentInParent(RebindItem, true);
            if (item == null)
            {
                return "this control";
            }

            TMP_Text label = GetField(item, ItemControlNameLabel) as TMP_Text;
            string text = KeybindsAccessibility.Clean(label != null ? label.text : null);
            return string.IsNullOrEmpty(text) ? "this control" : text;
        }

        internal static bool IsGamepadRow(Component rebindButton)
        {
            RemappableKey key = GetField(rebindButton, ButtonKey) as RemappableKey;
            return key != null && key.gamepadOnly;
        }

        internal static float RebindTimeout(Component rebindButton)
        {
            object value = GetField(rebindButton, ButtonTimeout);
            return value is float ? (float)value : 5f;
        }

        /// <summary>
        /// A local copy of RemappableKeyExtensions.GetRebindingIndex. Reimplemented rather than
        /// invoked reflectively because it is a static extension on a vanilla type and calling it
        /// by reflection every announcement costs more than the fifteen lines it contains.
        /// Checked 2026-10-14 against the decompilation of the installed LethalCompanyInputUtils
        /// 0.7.10: identical apart from the null-key early return. Re-diff after an InputUtils
        /// update, or gamepad rows will read the wrong binding.
        /// </summary>
        private static int RebindingIndexOf(RemappableKey key, InputAction action)
        {
            if (action.controls.Count == 0)
            {
                if (action.bindings.Count == 0)
                {
                    return -1;
                }
                if (key.gamepadOnly && action.bindings.Count > 1)
                {
                    return 1;
                }
                return 0;
            }
            if (key.rebindingIndex >= 0)
            {
                return key.rebindingIndex;
            }
            return action.GetBindingIndexForControl(action.controls[0]);
        }

        /// <summary>
        /// Give a SwitchClickZone a Button that forwards into its existing onClick. The zone
        /// implements only IPointerClickHandler and does not derive from Selectable, so without
        /// this the Global/Local save-context switch cannot be reached or operated at all without
        /// a mouse.
        /// </summary>
        internal static Button MakeClickZoneFocusable(Component zone)
        {
            if (zone == null || ClickZoneOnClick == null)
            {
                return null;
            }

            Button existing = zone.GetComponent<Button>();
            if (existing != null)
            {
                return existing;
            }

            UnityEngine.Events.UnityEvent forwarded = ClickZoneOnClick.GetValue(zone) as UnityEngine.Events.UnityEvent;
            if (forwarded == null)
            {
                Debug.LogError("[LethalAccess] SwitchClickZone on '" + zone.gameObject.name + "' has no usable " +
                               "onClick event, so the Global/Local switch cannot be made keyboard-operable.");
                return null;
            }

            Button button = zone.gameObject.AddComponent<Button>();
            // Selectable.Awake runs inside AddComponent and adopts any Graphic on the object as
            // its targetGraphic, which would recolour the switch on focus. Clear it afterwards.
            button.transition = Selectable.Transition.None;
            button.targetGraphic = null;
            button.interactable = true;
            button.onClick.AddListener(delegate { forwarded.Invoke(); });
            return button;
        }

        internal static string DescribeContext(string zoneLabel)
        {
            if (ContextCurrent == null || string.IsNullOrEmpty(zoneLabel))
            {
                return null;
            }
            object current = ContextCurrent.GetValue(null);
            if (current == null)
            {
                return null;
            }
            string name = current.ToString();
            return string.Equals(name, zoneLabel, StringComparison.OrdinalIgnoreCase) ? "selected" : "not selected";
        }

        /// <summary>
        /// PopOverTrigger only implements IPointerEnter/ExitHandler, so its text is mouse-only.
        /// Append it to the control it annotates rather than making tooltips separate focus stops.
        /// </summary>
        internal static string ReadPopOverText(GameObject go)
        {
            return KeybindsAccessibility.Clean(ReadPopOverRaw(go));
        }

        /// <summary>
        /// The same text with its line breaks intact. Help is built from the paragraphs, and Clean
        /// collapses them into one run before anything can split on them.
        /// </summary>
        internal static string ReadPopOverRaw(GameObject go)
        {
            if (go == null || PopOverTrigger == null || TriggerText == null)
            {
                return null;
            }

            // Usually on the control itself (Reset, Remove). The "?" beside the Global/Local switch
            // and the priority dropdown is a child of their container instead, so look down too.
            Component trigger = go.GetComponent(PopOverTrigger);
            if (trigger == null)
            {
                trigger = go.GetComponentInChildren(PopOverTrigger, true);
            }
            if (trigger == null)
            {
                return null;
            }

            string text = TriggerText.GetValue(trigger) as string;
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            bool isToken = TriggerIsToken != null && (bool)TriggerIsToken.GetValue(trigger);
            if (!isToken)
            {
                return text;
            }

            if (localeGetString == null)
            {
                // Speaking a raw token ("RebindButton.ResetToDefault.PopOver") would be worse than
                // saying nothing, and the control still has a real label of its own.
                return null;
            }
            return localeGetString.Invoke(null, new object[] { text }) as string;
        }
    }

    /// <summary>
    /// Bridge to UIAccessibilityManager's by-instance registration.
    ///
    /// RegisterObject/UnregisterObject/ClearScreen exist on UIAccessibilityManager and could be
    /// called directly. They are bound by reflection instead (the same approach FocusManager uses
    /// to reach that class's private `lastAnnouncedObject`), so that a signature change downgrades
    /// to one log line rather than forty keybind rows silently announcing the wrong text.
    /// </summary>
    internal static class AnnouncerBridge
    {
        private static bool probed;
        private static MethodInfo register;
        private static MethodInfo unregister;
        private static MethodInfo clearScreen;

        private static void Probe()
        {
            probed = true;

            Type t = typeof(UIAccessibilityManager);
            register = t.GetMethod("RegisterObject", BindingFlags.Public | BindingFlags.Instance, null,
                new Type[] { typeof(GameObject), typeof(Func<string>), typeof(string), typeof(string), typeof(int), typeof(int) },
                null);
            unregister = t.GetMethod("UnregisterObject", BindingFlags.Public | BindingFlags.Instance, null,
                new Type[] { typeof(GameObject) }, null);
            clearScreen = t.GetMethod("ClearScreen", BindingFlags.Public | BindingFlags.Instance, null,
                new Type[] { typeof(string) }, null);

            if (register == null)
            {
                Debug.LogWarning("[LethalAccess] UIAccessibilityManager has no " +
                                 "RegisterObject(GameObject, Func<string>, string, string, int, int). Keybind rows " +
                                 "are registered on UIScreenRegistry regardless, so navigation and positions are " +
                                 "correct; whether the announcer speaks the action name depends on it reading " +
                                 "UIScreenRegistry.TryGetEntry. If focused rows announce the KEY instead of the " +
                                 "ACTION, this is why.");
            }
        }

        internal static void RegisterObject(GameObject go, Func<string> text, string type, string screenId,
                                            int index, int count)
        {
            if (!probed)
            {
                Probe();
            }
            UIAccessibilityManager manager = UIAccessibilityManager.Instance;
            if (register == null || manager == null || go == null)
            {
                return;
            }
            register.Invoke(manager, new object[] { go, text, type, screenId, index, count });
        }

        internal static void UnregisterObject(GameObject go)
        {
            if (!probed)
            {
                Probe();
            }
            UIAccessibilityManager manager = UIAccessibilityManager.Instance;
            if (unregister == null || manager == null || go == null)
            {
                return;
            }
            unregister.Invoke(manager, new object[] { go });
        }

        internal static void ClearScreen(string screenId)
        {
            if (!probed)
            {
                Probe();
            }
            UIAccessibilityManager manager = UIAccessibilityManager.Instance;
            if (clearScreen == null || manager == null)
            {
                return;
            }
            clearScreen.Invoke(manager, new object[] { screenId });
        }
    }
}
