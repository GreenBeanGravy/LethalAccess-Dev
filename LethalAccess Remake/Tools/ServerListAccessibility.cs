using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LethalAccess
{
    /// <summary>
    /// Makes the Steam server list (Canvas/MenuContainer/LobbyList) usable without sight.
    ///
    /// This is a driver MonoBehaviour rather than a set of path registrations because the list is
    /// the one menu in this game whose controls do not exist until the moment they matter: every
    /// row is a clone of LobbySlotPrefab, so every row shares ONE hierarchy path
    /// (".../Content/LobbyListItem(Clone)/JoinButton") and every row's join button therefore
    /// announced the same thing - the button's own label, the word "Join". Everything that tells
    /// the rows apart lives on the parent LobbySlot component, and everything that orders them
    /// lives in the sibling order under SteamLobbyManager.levelListContainer.
    ///
    /// The three jobs here:
    ///   1. Announce the load. SteamLobbyManager.LoadServerList is `async void` and reports its
    ///      progress only by writing serverListBlankText, a plain TMP that nothing focuses, while
    ///      focus sits on BackToMenu the whole time. Without this the screen opens, says nothing
    ///      for several seconds, and never says how many servers arrived.
    ///   2. Describe every control on the screen by INSTANCE (UIAccessibilityManager.RegisterObject)
    ///      rather than by path, which is the only thing that can tell 20 identical clones apart.
    ///   3. Rebuild an explicit up/down chain every time the row set changes, so left and right
    ///      cannot eject you sideways into the scrollbar or the filter band.
    /// </summary>
    public class ServerListAccessibility : MonoBehaviour
    {
        /// <summary>Screen id for the instance registrations, so they can be cleared as a set.</summary>
        public const string ScreenId = "ServerList";

        /// <summary>
        /// How long the row count must hold still before the result is announced. LoadServerList
        /// awaits Steam and then fills the list from a coroutine that yields inside its
        /// offensive-name scan, so the count climbs over many frames; announcing on the first
        /// non-zero count would say "1 server found" and immediately be wrong.
        /// </summary>
        private const float RowCountSettleSeconds = 0.35f;

        private sealed class RowRef
        {
            public LobbySlot Slot;
            public Button Join;
        }

        private MenuManager menu;
        private SteamLobbyManager lobby;

        // Static controls. Resolved from component references and from onClick wiring rather than
        // from hard-coded paths - see ResolveStaticControls.
        private Selectable tagField;
        private Selectable distanceDropdown;
        private Button moddedArrow;
        private Button challengeArrow;
        private Button refreshButton;
        private Button backToMenu;

        private readonly List<RowRef> rows = new List<RowRef>();

        private bool wasVisible;
        private int lastChildCount = -1;
        private float countChangedAt;
        private bool loadAnnounced;
        private bool resultAnnounced;
        private bool staticControlsResolved;
        private bool warnedNoListContainer;
        private bool warnedNoNetworkManager;

        #region Installation

        /// <summary>
        /// Attach the driver to the menu scene's MenuManager. Idempotent: the menu scene is loaded
        /// again on every return to the main menu, and MenuManager.Start runs again with it.
        /// </summary>
        public static void Install(MenuManager menuManager)
        {
            if (menuManager == null)
            {
                Debug.LogError("[LethalAccess] ServerListAccessibility.Install was given no MenuManager; the " +
                               "server list will have no labels, positions or navigation.");
                return;
            }

            if (menuManager.GetComponent<ServerListAccessibility>() != null)
            {
                return;
            }

            menuManager.gameObject.AddComponent<ServerListAccessibility>();
        }

        private void Awake()
        {
            menu = GetComponent<MenuManager>();
            if (menu == null)
            {
                menu = UnityEngine.Object.FindObjectOfType<MenuManager>();
            }

            if (menu == null)
            {
                Debug.LogError("[LethalAccess] ServerListAccessibility found no MenuManager; the server list " +
                               "cannot be tracked. Disabling.");
                enabled = false;
                return;
            }

            if (menu.serverListUIContainer == null)
            {
                Debug.LogError("[LethalAccess] MenuManager.serverListUIContainer is not assigned, so the server " +
                               "list panel cannot be found. Disabling ServerListAccessibility.");
                enabled = false;
                return;
            }

            lobby = ResolveLobbyManager(menu);
            if (lobby == null)
            {
                Debug.LogError("[LethalAccess] No SteamLobbyManager could be found for the server list. Rows will " +
                               "fall back to generic announcements. Disabling ServerListAccessibility.");
                enabled = false;
            }
        }

        private void OnDisable()
        {
            // Leaving instance registrations behind after the menu scene unloads is not harmless:
            // Unity reuses instance ids, so a stale entry can eventually be handed to an unrelated
            // new object and describe it as a lobby.
            ClearRegistrations();
        }

        /// <summary>
        /// SteamLobbyManager.OnEnable clears the tag field every time the panel is shown, so the
        /// component is expected to live inside serverListUIContainer - but that is scene wiring,
        /// not something the game's code guarantees, and it could not be confirmed against the
        /// running game (the process was sitting in the launch-options scene, where the main menu
        /// is not loaded). Fall back to a whole-scene search that, unlike FindObjectOfType, can
        /// see the component while its panel is switched off.
        /// </summary>
        private static SteamLobbyManager ResolveLobbyManager(MenuManager menuManager)
        {
            SteamLobbyManager found = menuManager.serverListUIContainer.GetComponentInChildren<SteamLobbyManager>(true);
            if (found != null)
            {
                return found;
            }

            SteamLobbyManager[] all = Resources.FindObjectsOfTypeAll<SteamLobbyManager>();
            for (int i = 0; i < all.Length; i++)
            {
                // gameObject.scene.IsValid() excludes the prefab/asset copies, which belong to no
                // loaded scene.
                if (all[i] != null && all[i].gameObject.scene.IsValid())
                {
                    Debug.Log("[LethalAccess] SteamLobbyManager was not inside serverListUIContainer; using the " +
                              "scene instance on '" + all[i].gameObject.name + "' instead.");
                    return all[i];
                }
            }
            return null;
        }

        #endregion

        #region Frame loop

        private void Update()
        {
            if (menu == null || lobby == null)
            {
                // The menu scene was unloaded underneath us; the component goes with it.
                return;
            }

            GameObject container = menu.serverListUIContainer;
            if (container == null)
            {
                return;
            }

            bool visible = container.activeInHierarchy;
            if (visible != wasVisible)
            {
                wasVisible = visible;
                if (visible)
                {
                    OnShown();
                }
                else
                {
                    OnHidden();
                }
            }

            if (!visible)
            {
                return;
            }

            Transform list = lobby.levelListContainer;
            if (list == null)
            {
                if (!warnedNoListContainer)
                {
                    warnedNoListContainer = true;
                    Debug.LogError("[LethalAccess] SteamLobbyManager.levelListContainer is not assigned; server " +
                                   "list rows cannot be counted, ordered or navigated.");
                }
                return;
            }

            // childCount is only a change trigger, never the answer: the container also holds the
            // list headers and the "No available servers to join." text, so it is never the number
            // of lobbies. The authoritative count comes from CollectRows.
            if (list.childCount != lastChildCount)
            {
                lastChildCount = list.childCount;
                countChangedAt = Time.unscaledTime;
                Rebuild();
            }

            GameNetworkManager net = GameNetworkManager.Instance;
            if (net == null)
            {
                if (!warnedNoNetworkManager)
                {
                    warnedNoNetworkManager = true;
                    Debug.LogError("[LethalAccess] GameNetworkManager.Instance is null while the server list is " +
                                   "open, so the load cannot be tracked and no result will be announced.");
                }
                return;
            }

            if (net.waitingForLobbyDataRefresh)
            {
                if (!loadAnnounced)
                {
                    loadAnnounced = true;

                    // A refresh that actually starts re-arms the result announcement. This is what
                    // makes refreshing audible at all: LoadServerList destroys every row (taking
                    // focus with it) and then says nothing until the new rows appear.
                    resultAnnounced = false;
                    AnnouncementPolicy.Speak("Loading servers.");
                }
                return;
            }

            loadAnnounced = false;

            if (!resultAnnounced && Time.unscaledTime - countChangedAt >= RowCountSettleSeconds)
            {
                resultAnnounced = true;
                AnnounceResult();
            }
        }

        private void OnShown()
        {
            lastChildCount = -1;
            countChangedAt = Time.unscaledTime;
            loadAnnounced = false;
            resultAnnounced = false;
            rows.Clear();

            // "Loading servers." is deliberately not said here: the load flag is raised by the same
            // click that opens the panel, so Update says it on the very next frame - and stays
            // quiet in the case where nothing is actually loading.
            AnnouncementPolicy.Speak("Server list.");
        }

        private void OnHidden()
        {
            ClearRegistrations();
            rows.Clear();
            lastChildCount = -1;
            loadAnnounced = false;
            resultAnnounced = false;
        }

        private void AnnounceResult()
        {
            Rebuild();
            int n = rows.Count;

            if (n == 0)
            {
                // serverListBlankText is the game's own words for the outcome ("No available
                // servers to join.", or a stale "Loading server list..." if the query never
                // finished) - which is exactly what a sighted player is looking at.
                string blank = lobby.serverListBlankText != null ? lobby.serverListBlankText.text : null;
                AnnouncementPolicy.Speak(string.IsNullOrEmpty(blank) ? "No servers found." : blank);

                // Focus is normally still on BackToMenu here (the panel's serialized Select), but a
                // refresh that comes back empty has just destroyed whatever row was focused.
                if (backToMenu != null && EventSystem.current != null &&
                    EventSystem.current.currentSelectedGameObject == null)
                {
                    FocusManager.Seed(backToMenu.transform);
                }
                return;
            }

            AnnouncementPolicy.Speak(n == 1 ? "1 server found" : n + " servers found");

            if (IsEditingTagField())
            {
                // Moving focus out of a live TMP_InputField would drop the user mid-word. The count
                // has still been announced, and down still reaches the rows from here.
                return;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Join != null && rows[i].Join.gameObject.activeInHierarchy)
                {
                    FocusManager.Seed(rows[i].Join.transform);
                    return;
                }
            }

            Debug.LogError("[LethalAccess] " + n + " server list row(s) exist but none has a usable join button, " +
                           "so focus could not be placed in the list.");
        }

        private bool IsEditingTagField()
        {
            TMP_InputField field = lobby.serverTagInputField;
            return field != null && field.isFocused;
        }

        /// <summary>
        /// Re-derive the row set, then rewrite navigation and the instance registrations from it.
        /// Called on every change to the container's child count, so both stay in step with a list
        /// that is still filling in.
        /// </summary>
        private void Rebuild()
        {
            CollectRows();
            ResolveStaticControls();
            RebuildNavigation();
            RegisterControls();
        }

        #endregion

        #region Rows

        private void CollectRows()
        {
            rows.Clear();

            Transform list = lobby != null ? lobby.levelListContainer : null;
            if (list == null)
            {
                return;
            }

            for (int i = 0; i < list.childCount; i++)
            {
                Transform child = list.GetChild(i);
                if (!child.gameObject.activeInHierarchy)
                {
                    continue;
                }

                // Skips the list headers and the blank-list text, which are children of the same
                // container and carry no LobbySlot.
                LobbySlot slot = child.GetComponentInChildren<LobbySlot>(true);
                if (slot == null)
                {
                    continue;
                }

                RowRef row = new RowRef();
                row.Slot = slot;
                row.Join = FindJoinButton(child, slot);

                if (row.Join == null)
                {
                    Debug.LogError("[LethalAccess] Server list row '" + child.name + "' has no join button that " +
                                   "can be focused; that lobby is unreachable by keyboard.");
                }

                // Sibling order is the reading order here, unusually: loadLobbyListAndFilter lays
                // rows out top to bottom by dropping anchoredPosition 42 units per row, in the same
                // order it instantiates them.
                rows.Add(row);
            }
        }

        /// <summary>
        /// The row's join button. Identified by its onClick wiring first, because the row root also
        /// carries HostName and JoinCode buttons; both ship inactive, but "the only active button"
        /// is scene state, whereas "the button that calls LobbySlot.JoinButton" is the contract.
        /// </summary>
        private static Button FindJoinButton(Transform rowRoot, LobbySlot slot)
        {
            Button[] buttons = rowRoot.GetComponentsInChildren<Button>(true);

            for (int i = 0; i < buttons.Length; i++)
            {
                Button b = buttons[i];
                if (b.onClick == null)
                {
                    continue;
                }

                int calls = b.onClick.GetPersistentEventCount();
                for (int j = 0; j < calls; j++)
                {
                    if (ReferenceEquals(b.onClick.GetPersistentTarget(j), slot) &&
                        b.onClick.GetPersistentMethodName(j) == "JoinButton")
                    {
                        return b;
                    }
                }
            }

            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].gameObject.activeInHierarchy)
                {
                    return buttons[i];
                }
            }

            return null;
        }

        /// <summary>
        /// What one lobby row says.
        ///
        /// The name, population and mod state come from UIUtils.BuildLobbyRowLabel so that there is
        /// one implementation of a row's wording rather than several that can drift apart. The
        /// challenge-moon marker is added here because it is not derivable from the LobbySlot
        /// fields at all: it is conveyed in vanilla by a TMP reading "CHALLENGE MOON" that sits on
        /// no Selectable, and by the fact that the row was cloned from LobbySlotPrefabChallenge.
        ///
        /// No position is appended: the announcer supplies it from the instance registration, and
        /// adding one here would have every row numbered twice.
        /// </summary>
        public static string BuildRowLabel(LobbySlot slot)
        {
            if (slot == null)
            {
                return string.Empty;
            }

            string body = UIUtils.BuildLobbyRowLabel(slot, 0, 0);
            if (string.IsNullOrEmpty(body))
            {
                return string.Empty;
            }

            return IsChallengeRow(slot) ? "Challenge moon, " + body : body;
        }

        private static bool IsChallengeRow(LobbySlot slot)
        {
            // Instantiate keeps the prefab's name, so the clone is "LobbyListItemChallenge(Clone)".
            // SteamLobbyManager takes the LobbySlot with GetComponentInChildren, so the component
            // may sit on the row root or one level below it; check both rather than assuming which.
            if (NameIsChallenge(slot.gameObject.name))
            {
                return true;
            }
            Transform parent = slot.transform.parent;
            return parent != null && NameIsChallenge(parent.name);
        }

        private static bool NameIsChallenge(string objectName)
        {
            return !string.IsNullOrEmpty(objectName) &&
                   objectName.StartsWith("LobbyListItemChallenge", StringComparison.Ordinal);
        }

        #endregion

        #region Navigation

        /// <summary>
        /// Rewrite the whole screen as one explicit vertical chain.
        ///
        /// Everything on this screen ships as Navigation.Mode.Automatic with null links, and the
        /// join buttons are pinned to each row's right edge with the scrollbar beyond them and the
        /// filter band above - so Unity's geometric search sends right into the scrollbar and left
        /// into a filter. An explicit chain removes the horizontal links outright, which also means
        /// the fix holds when ConfigManager.VerticalNavigationOnly is turned off.
        /// </summary>
        private void RebuildNavigation()
        {
            List<Selectable> chain = new List<Selectable>();
            AddIfUsable(chain, tagField);
            AddIfUsable(chain, distanceDropdown);
            AddIfUsable(chain, moddedArrow);
            AddIfUsable(chain, challengeArrow);
            AddIfUsable(chain, refreshButton);

            for (int i = 0; i < rows.Count; i++)
            {
                AddIfUsable(chain, rows[i].Join);
            }

            AddIfUsable(chain, backToMenu);

            if (chain.Count == 0)
            {
                Debug.LogError("[LethalAccess] The server list has no usable controls to navigate; nothing was " +
                               "wired. The panel is either mid-rebuild or its layout has changed.");
                return;
            }

            // No wrap: the top and bottom of a list are dead ends, which is what a screen-reader
            // user expects from arrow keys.
            NavigationEnforcer.BuildChain(chain, false);

            IsolateScrollbars();
        }

        /// <summary>
        /// The one test for "the arrow keys stop here".
        ///
        /// An inactive or non-interactable link in an explicit chain is a silent dead stop:
        /// Selectable.Navigate ignores a target that fails IsActive(), so the keypress is eaten
        /// with no announcement - which is why <see cref="AddIfUsable"/> leaves those out.
        ///
        /// The SAME test has to decide the "n of m" positions in <see cref="RegisterControls"/>,
        /// or the two sets drift and the numbers describe a screen the user cannot walk. Positions
        /// are the primary orientation cue here, so they are counted over exactly the set the
        /// arrows visit and nothing else. Kept as one method rather than two copies of the
        /// condition for that reason.
        /// </summary>
        private static bool IsUsable(Selectable s)
        {
            return s != null && s.gameObject.activeInHierarchy && s.IsInteractable();
        }

        private static void AddIfUsable(List<Selectable> chain, Selectable s)
        {
            if (IsUsable(s))
            {
                chain.Add(s);
            }
        }

        private void IsolateScrollbars()
        {
            Scrollbar[] bars = menu.serverListUIContainer.GetComponentsInChildren<Scrollbar>(true);
            for (int i = 0; i < bars.Length; i++)
            {
                // A scrollbar is a mouse affordance: focus already follows the selection, so
                // stopping on one only costs the user a keypress and an announcement.
                NavigationEnforcer.Isolate(bars[i]);
            }
        }

        #endregion

        #region Control resolution

        /// <summary>
        /// One-shot: these are scene objects that live for as long as the panel does, and the
        /// lookups below log loudly when one is missing. Re-running it on every row change would
        /// turn one honest error into one per row.
        /// </summary>
        private void ResolveStaticControls()
        {
            if (staticControlsResolved)
            {
                return;
            }
            staticControlsResolved = true;

            Transform root = menu.serverListUIContainer.transform;

            tagField = lobby.serverTagInputField;
            if (tagField == null)
            {
                Debug.LogError("[LethalAccess] SteamLobbyManager.serverTagInputField is not assigned; the server " +
                               "tag filter cannot be labelled or reached.");
            }

            TMP_Dropdown tmp = root.GetComponentInChildren<TMP_Dropdown>(true);
            if (tmp != null)
            {
                distanceDropdown = tmp;
            }
            else
            {
                Dropdown legacy = root.GetComponentInChildren<Dropdown>(true);
                if (legacy != null)
                {
                    distanceDropdown = legacy;
                }
                else
                {
                    Debug.LogError("[LethalAccess] No dropdown was found on the server list, so the distance " +
                                   "sort is missing from the navigation chain.");
                }
            }

            // The two filters are plain Buttons both named "Arrow", so a name lookup cannot tell
            // them apart. Their onClick wiring can.
            moddedArrow = FindButtonInvoking(root, lobby, "ToggleSortWithModdedClients");
            challengeArrow = FindButtonInvoking(root, lobby, "ToggleSortWithChallengeMoons");
            refreshButton = FindButtonInvoking(root, lobby, "RefreshServerListButton");

            // No C# method to key on: the back button is wired to serialized SetActive calls.
            backToMenu = FindButtonNamed(root, "BackToMenu");
            if (backToMenu == null)
            {
                Debug.LogError("[LethalAccess] No 'BackToMenu' button was found on the server list. Nothing will " +
                               "be at the bottom of the navigation chain, so there may be no keyboard way out.");
            }
        }

        private static Button FindButtonInvoking(Transform root, UnityEngine.Object target, string methodName)
        {
            Button[] buttons = root.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < buttons.Length; i++)
            {
                Button b = buttons[i];
                if (b.onClick == null)
                {
                    continue;
                }

                int calls = b.onClick.GetPersistentEventCount();
                for (int j = 0; j < calls; j++)
                {
                    if (ReferenceEquals(b.onClick.GetPersistentTarget(j), target) &&
                        b.onClick.GetPersistentMethodName(j) == methodName)
                    {
                        return b;
                    }
                }
            }

            Debug.LogError("[LethalAccess] No button on the server list calls " + methodName +
                           "; that control is missing from the navigation chain and will announce generically.");
            return null;
        }

        private static Button FindButtonNamed(Transform root, string objectName)
        {
            Button[] buttons = root.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i].gameObject.name == objectName)
                {
                    return buttons[i];
                }
            }
            return null;
        }

        #endregion

        #region Labels

        /// <summary>
        /// Describe every control on the screen by instance.
        ///
        /// By instance and not by path, because the rows cannot be told apart by path - and because
        /// the whole panel is inactive for the entire main menu, which is precisely when
        /// GameObject.Find (the resolver behind the path registrations) cannot see it.
        ///
        /// Two runs, so the positions mean something: the list's own controls are numbered
        /// separately from the servers, and "3 of 7" on a row is 3 of 7 SERVERS rather than 3 of 12
        /// things on the screen. This matches how UIScreenRegistry scopes a position to a section.
        ///
        /// Everything except the rows could move to LACore's startup registration later; the rows
        /// themselves cannot, because they do not exist until the panel is populated.
        /// </summary>
        private void RegisterControls()
        {
            UIAccessibilityManager mgr = UIAccessibilityManager.Instance;
            if (mgr == null)
            {
                Debug.LogError("[LethalAccess] UIAccessibilityManager.Instance is null, so the server list cannot " +
                               "be labelled. Rows will announce as \"Join\" and the filters as \"Arrow\".");
                return;
            }

            // Rows are destroyed and recreated on every refresh, so the previous set of instance
            // ids must go before the new one is added.
            mgr.ClearScreen(ScreenId);

            // IsUsable, not "!= null". Every resolver in ResolveStaticControls returns a reference
            // that outlives its object being switched off: tagField is a serialized field on
            // SteamLobbyManager, and the four button lookups and the dropdown lookup all search
            // with the include-inactive overload of GetComponentsInChildren. So a null check is
            // strictly weaker than the test RebuildNavigation walks with, and this panel really
            // does ship switched-off Selectables (JoinCode, the TMP_InputField beside BackToMenu,
            // is inactive in the shipped scene). Counting a control the chain skipped announced
            // "Refresh server list, button, 5 of 6" on a screen with five stops on it.
            int controlCount = CountUsable(tagField, distanceDropdown, moddedArrow, challengeArrow,
                refreshButton, backToMenu);
            int position = 0;

            if (IsUsable(tagField))
            {
                position++;
                // "input field" is inside the text on purpose: the announcer appends the element
                // type only when the text does not already name it, and the Enter instruction has
                // to come last to be the thing the user is left holding.
                mgr.RegisterObject(tagField.gameObject, DescribeTagField, "input field",
                    ScreenId, position, controlCount);
            }

            if (IsUsable(distanceDropdown))
            {
                position++;
                // A provider rather than letting the announcer sniff it: with no provider the text
                // falls through to the first TMP in the dropdown's children, which is the caption -
                // so it would announce the selected option twice and never say what the control is.
                mgr.RegisterObject(distanceDropdown.gameObject, DescribeDistanceSort, "dropdown",
                    ScreenId, position, controlCount);
            }

            if (IsUsable(moddedArrow))
            {
                position++;
                // Declared "button" and not "checkbox": it IS a plain Button - the check state is a
                // sibling Image being enabled - and declaring a type no component backs makes the
                // announcer discard the declaration and log an error.
                mgr.RegisterObject(moddedArrow.gameObject, DescribeModdedFilter, "button",
                    ScreenId, position, controlCount);
            }

            if (IsUsable(challengeArrow))
            {
                position++;
                mgr.RegisterObject(challengeArrow.gameObject, DescribeChallengeFilter, "button",
                    ScreenId, position, controlCount);
            }

            if (IsUsable(refreshButton))
            {
                position++;
                mgr.RegisterObject(refreshButton.gameObject, () => "Refresh server list", "button",
                    ScreenId, position, controlCount);
            }

            if (IsUsable(backToMenu))
            {
                position++;
                mgr.RegisterObject(backToMenu.gameObject, () => "Back to menu", "button",
                    ScreenId, position, controlCount);
            }

            // The rows are their own run, and they get the same rule: AddIfUsable skips a row whose
            // join button is missing or not walkable, so numbering that row would number a stop the
            // arrows never make and push every row below it one out of step. Counted first, because
            // the denominator has to be known before the first row is registered.
            int rowCount = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (IsUsable(rows[i].Join))
                {
                    rowCount++;
                }
            }

            int rowPosition = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                Button join = rows[i].Join;
                if (!IsUsable(join))
                {
                    // Not logged again here: CollectRows already reports the missing-join-button
                    // case as an error, and a row that is merely inactive or non-interactable is
                    // the list mid-rebuild rather than a fault. Either way the lobby is left
                    // unnumbered as well as unreachable, instead of being given a position that
                    // lies about where it is.
                    continue;
                }

                rowPosition++;

                // Copied out of the list per iteration: a closure over `rows[i]` would read the
                // list at announce time, by which point a refresh may have replaced it.
                LobbySlot slot = rows[i].Slot;
                mgr.RegisterObject(join.gameObject, () => BuildRowLabel(slot), "button",
                    ScreenId, rowPosition, rowCount);
            }
        }

        private void ClearRegistrations()
        {
            UIAccessibilityManager mgr = UIAccessibilityManager.Instance;
            if (mgr != null)
            {
                mgr.ClearScreen(ScreenId);
            }
        }

        /// <summary>
        /// How many of these the arrow keys will actually stop on - the denominator of every
        /// "n of m" in the static run. Shares <see cref="IsUsable"/> with the chain builder, so the
        /// counted set and the walked set cannot drift apart again.
        /// </summary>
        private static int CountUsable(params Selectable[] items)
        {
            int n = 0;
            for (int i = 0; i < items.Length; i++)
            {
                if (IsUsable(items[i]))
                {
                    n++;
                }
            }
            return n;
        }

        #endregion

        #region Text providers

        private string DescribeTagField()
        {
            if (this == null || lobby == null || lobby.serverTagInputField == null)
            {
                // A registration that outlived its driver. Empty makes the announcer fall back to
                // its own description rather than reading a destroyed object.
                return string.Empty;
            }

            string value = lobby.serverTagInputField.text;
            string spoken = string.IsNullOrEmpty(value) ? "empty" : value;

            // The Enter instruction is load-bearing: TMP_InputField.OnSelect activates the field,
            // and an active field swallows the arrow keys, so a user who arrows onto it has no way
            // out unless they are told about Enter.
            //
            // No full stop after "arrows" - see TrimTrailingFullStop. The announcer appends the
            // position straight onto this string, and "...arrows., 1 of 6" is a full stop
            // immediately followed by a comma, which screen readers render as a double pause.
            return "Server tag filter, input field, " + spoken +
                   ". Press Enter when finished, then use up and down arrows";
        }

        private string DescribeModdedFilter()
        {
            if (this == null || lobby == null)
            {
                return string.Empty;
            }

            string text = DescribeFilter(moddedArrow, lobby.sortWithModdedClientsCheckbox,
                "With detected mods", lobby.sortWithModdedClientsWarning);

            // ToggleSortWithModdedClients switches this warning on with the filter. It is the only
            // place the game explains what the filter does, and it is pointer-free text that
            // nothing else in the mod reads.
            TextMeshProUGUI warning = lobby.sortWithModdedClientsWarning;
            if (warning != null && warning.enabled && !string.IsNullOrEmpty(warning.text))
            {
                // The shipped wording is "WARNING: Modifying Lethal Company is not recommended."
                // (read off the live scene), and it is the last thing this provider says before the
                // announcer appends ", 3 of 6" - the same full-stop-then-comma double pause as the
                // tag field. Trimmed here rather than rewritten, because these are the game's words.
                string spokenWarning = TrimTrailingFullStop(warning.text);
                if (spokenWarning.Length > 0)
                {
                    text += ". " + spokenWarning;
                }
            }
            return text;
        }

        private string DescribeChallengeFilter()
        {
            if (this == null || lobby == null)
            {
                return string.Empty;
            }
            return DescribeFilter(challengeArrow, lobby.sortWithChallengeMoonsCheckbox,
                "With challenge moon", null);
        }

        /// <summary>
        /// "Sort by distance" plus whichever option is showing. The dropdown's own caption TMP is
        /// the selected option's text, so reading the control without this says the option and
        /// never says what it controls.
        /// </summary>
        private string DescribeDistanceSort()
        {
            if (this == null || distanceDropdown == null)
            {
                return string.Empty;
            }

            string option = null;

            TMP_Dropdown tmp = distanceDropdown as TMP_Dropdown;
            if (tmp != null)
            {
                if (tmp.value >= 0 && tmp.value < tmp.options.Count)
                {
                    option = tmp.options[tmp.value].text;
                }
            }
            else
            {
                Dropdown legacy = distanceDropdown as Dropdown;
                if (legacy != null && legacy.value >= 0 && legacy.value < legacy.options.Count)
                {
                    option = legacy.options[legacy.value].text;
                }
            }

            // No fabricated names for the three sortByDistanceSetting values: if the option list is
            // empty, say what the control is and stop.
            return string.IsNullOrEmpty(option) ? "Sort by distance" : "Sort by distance, " + option;
        }

        /// <summary>
        /// These filters are NOT Toggles - they are plain Buttons whose "checked" state is a second
        /// sibling Image being enabled - so nothing the announcer can sniff will ever report their
        /// state. Without this they announce as "Arrow, button": no label, no state.
        /// </summary>
        private static string DescribeFilter(Button arrow, Image checkedIcon, string fallbackLabel,
            TextMeshProUGUI excludeText)
        {
            string label = FilterLabel(arrow, fallbackLabel, excludeText);

            if (checkedIcon == null)
            {
                // Do not invent a state that cannot be read.
                return label;
            }

            return label + ", " + (checkedIcon.enabled ? "checked" : "unchecked");
        }

        private static string FilterLabel(Button arrow, string fallback, TextMeshProUGUI exclude)
        {
            if (arrow == null || arrow.transform.parent == null)
            {
                return fallback;
            }

            // Read the label off the scene rather than hard-coding it, so a wording change in the
            // game cannot leave the mod announcing something no sighted player can see.
            TextMeshProUGUI[] texts = arrow.transform.parent.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                TextMeshProUGUI t = texts[i];
                if (t == exclude || t.transform.IsChildOf(arrow.transform))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(t.text))
                {
                    return t.text.Trim();
                }
            }
            return fallback;
        }

        /// <summary>
        /// Drop a sentence-ending full stop from the last words of a text provider.
        ///
        /// UIAccessibilityManager.GetElementIndex appends the position directly onto what a provider returns
        /// (", 3 of 6"), so a string ending in a full stop is spoken as "...recommended., 3 of 6" -
        /// a full stop immediately followed by a comma, which screen readers render as an awkward
        /// double pause. Trailing whitespace goes with it so a " ." cannot leave a dangling space
        /// in front of the comma; trailing runs of dots go too, so an ellipsis cannot be left as
        /// a stub.
        ///
        /// Only for strings that come from the game or the player. Literals written in this file
        /// simply do not end with a full stop - see DescribeTagField.
        /// </summary>
        private static string TrimTrailingFullStop(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            return text.TrimEnd('.', ' ', '\t', '\r', '\n');
        }

        #endregion
    }
}
