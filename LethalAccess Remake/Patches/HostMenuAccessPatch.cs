using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LethalAccess
{
    /// <summary>
    /// The host menu (Canvas/MenuContainer/LobbyHostSettings) and the challenge leaderboard
    /// that shares its root.
    ///
    /// Two things were completely broken here and both are structural, not cosmetic:
    ///
    ///  1. The lobby name could not be typed. ServerNameField ships
    ///     navigation.mode = None with all four links null. Selectable.FindSelectable skips
    ///     Mode.None candidates outright, so the field is not a valid target from ANY direction,
    ///     nothing links to it, and ClickHostButton sets no selection at all. A mouse works only
    ///     because TMP_InputField.OnPointerDown calls SetSelectedGameObject itself, bypassing the
    ///     Mode.None guard in Selectable.OnPointerDown. The fix is to give every member of the
    ///     chain, the input fields included, Mode.Explicit and real up/down links.
    ///
    ///  2. ClickHostButton neither moves selection nor hides menuButtons, so focus stayed
    ///     on MainButtons/HostButton and Down walked the main menu behind the overlay. The scene
    ///     was authored expecting menuButtons to be hidden: the Back button's first serialized
    ///     onClick is EnableUIPanel(MainButtons), i.e. an undo of a hide nobody ever performed.
    ///
    /// The authored order lives in <see cref="UIScreenRegistry"/> (labels, types, live state,
    /// section membership, "n of m") and is mirrored into real Navigation links by
    /// <see cref="NavigationEnforcer"/>, so navigation is correct whether or not
    /// ConfigManager.VerticalNavigationOnly is on. Both branches of the screen (Steam and LAN) are
    /// declared as one list; only one of OptionsNormal/LANOptions is ever active, and the registry
    /// skips entries that are not visible, so the order needs no rebuild when the mode changes.
    /// </summary>
    public static class HostMenuAccess
    {
        public const string HostScreenId = "HostSettings";
        public const string LeaderboardScreenId = "ChallengeLeaderboard";

        // Paths relative to LobbyHostSettings.
        private const string NormalOptions = "HostSettingsContainer/LobbyHostOptions/OptionsNormal";
        private const string LanOptions = "HostSettingsContainer/LobbyHostOptions/LANOptions";
        private const string PathServerName = NormalOptions + "/ServerNameField";
        private const string PathServerTag = NormalOptions + "/ServerTagInputField";
        private const string PathPublic = NormalOptions + "/Public";
        private const string PathPrivate = NormalOptions + "/Private";
        private const string PathLanServerName = LanOptions + "/ServerNameField";
        private const string PathAllowRemote = LanOptions + "/AllowRemote";
        private const string PathLocal = LanOptions + "/Local";
        private const string PathFile1 = "FilesPanel/File1";
        private const string PathFile2 = "FilesPanel/File2";
        private const string PathFile3 = "FilesPanel/File3";
        private const string PathChallengeMoon = "FilesPanel/ChallengeMoonButton";
        private const string PathConfirm = "HostSettingsContainer/Confirm";
        private const string PathBack = "HostSettingsContainer/Back";
        private const string PathDelete1 = PathFile1 + "/DeleteButton";
        private const string PathDelete2 = PathFile2 + "/DeleteButton";
        private const string PathDelete3 = PathFile3 + "/DeleteButton";

        // Paths relative to ChallengeLeaderboard.
        private const string LeaderboardList = "LobbyList (1)";
        private const string PathRefresh = LeaderboardList + "/ListPanel/RefreshButton";
        private const string PathSort = LeaderboardList + "/ListPanel/Dropdown";
        private const string PathRemoveScore = LeaderboardList + "/RemoveScore";
        private const string PathRetry = LeaderboardList + "/Retry";
        private const string PathLeaderboardBack = LeaderboardList + "/Back";

        // MenuManager has no static instance and the panel outlives any single patch call, so the
        // one the patches hand us is cached and re-found if the menu scene is reloaded.
        private static MenuManager menu;

        // Which MenuManager the descriptors were registered against. A scene reload builds a new
        // one, and the old descriptors point at destroyed transforms.
        private static int registeredForMenuId;

        private static bool rebuildQueued;

        private static readonly HashSet<int> hookedFields = new HashSet<int>();
        private static readonly Dictionary<int, string> lastFieldText = new Dictionary<int, string>();

        /// <summary>
        /// The frame on which ClickHostButton is running. ClickHostButton ends with
        /// HostSetLobbyPublic(hostSettings_LobbyPublic) purely to refresh the two Animators
        /// (MenuManager.ClickHostButton), and the postfix on that method cannot otherwise tell that refresh
        /// from a real button press, which is why simply opening the host menu announced
        /// "Set to Private". Stamping the frame rather than holding a flag means a throw
        /// inside ClickHostButton cannot leave public/private permanently silent.
        /// </summary>
        public static int SuppressLobbyPublicFrame = -1;

        /// <summary>True once both screen descriptors exist for the current MenuManager.</summary>
        public static bool IsRegistered
        {
            get { return registeredForMenuId != 0 && menu != null && menu.GetInstanceID() == registeredForMenuId; }
        }

        #region Model

        /// <summary>One declared control: where it is, what to call it, and its live state.</summary>
        private sealed class Ctl
        {
            public string Path;
            public string Label;
            public string Type;
            public Func<string> State;
            public bool Optional;

            public Ctl(string path, string label, string type)
            {
                Path = path;
                Label = label;
                Type = type;
            }
        }

        private static MenuManager Menu
        {
            get
            {
                if (menu == null)
                {
                    menu = UnityEngine.Object.FindObjectOfType<MenuManager>();
                }
                return menu;
            }
        }

        private static Transform HostRoot()
        {
            MenuManager m = Menu;
            if (m == null || m.HostSettingsScreen == null)
            {
                return null;
            }
            return m.HostSettingsScreen.transform;
        }

        private static Transform LeaderboardRoot()
        {
            MenuManager m = Menu;
            if (m == null || m.leaderboardContainer == null)
            {
                return null;
            }
            return m.leaderboardContainer.transform;
        }

        private static bool IsHostPanelVisible()
        {
            Transform root = HostRoot();
            return root != null && UIUtils.IsUIElementVisible(root.gameObject);
        }

        private static Transform Resolve(Transform root, string relativePath)
        {
            if (root == null)
            {
                return null;
            }
            return UIUtils.FindByPathIncludingInactive(root, relativePath);
        }

        private static bool IsLan()
        {
            return GameNetworkManager.Instance != null && GameNetworkManager.Instance.disableSteam;
        }

        #endregion

        #region Registration

        /// <summary>
        /// Declare both screens. Registering while the host panel is already on screen makes
        /// UIScreenRegistry resolve, announce and seed focus inside this call, which is exactly
        /// what we want from the ClickHostButton postfix, because the panel is activated two
        /// statements earlier.
        /// </summary>
        public static void EnsureRegistered(MenuManager instance)
        {
            if (instance == null)
            {
                Debug.LogError("[LethalAccess] HostMenuAccess.EnsureRegistered called with no MenuManager; " +
                               "the host menu will keep announcing raw object names.");
                return;
            }

            menu = instance;
            int id = instance.GetInstanceID();
            if (registeredForMenuId == id)
            {
                return;
            }
            registeredForMenuId = id;

            // A new MenuManager means new TMP_InputField instances; the old ids can never come
            // back, and keeping them would let a recycled id look already-hooked.
            hookedFields.Clear();
            lastFieldText.Clear();

            RegisterHostScreen();
            RegisterLeaderboardScreen();
        }

        private static void RegisterHostScreen()
        {
            // Steam and LAN controls are declared in one list. MenuManager.ClickHostButton leaves
            // only one of OptionsNormal / LANOptions active, and the registry counts and steps
            // over only visible entries, so this single order yields
            //   Steam: name -> [tag] -> Public -> Private -> files -> confirm -> back
            //   LAN:   name -> allow remote -> local -> files -> confirm -> back
            // without any need to re-declare the screen when the mode changes.
            //
            // The three Steam-mode controls are deliberately NOT Optional: they are the default
            // path, and a renamed path there is the exact defect this file exists to fix, so it
            // must produce an error rather than a silently empty screen. "Resolved but inactive"
            // (which is what LAN mode makes them) is only a Debug.Log, and only on first resolve.
            List<Ctl> options = new List<Ctl>
            {
                new Ctl(PathServerName, "Server name", "input field")
                {
                    State = InputFieldState(PathServerName)
                },
                new Ctl(PathServerTag, "Server tag", "input field")
                {
                    // Shown and hidden every frame from hostSettings_LobbyPublic
                    // (MenuManager.Update), so it is absent for the whole of a private lobby.
                    State = InputFieldState(PathServerTag), Optional = true
                },
                new Ctl(PathPublic, "Public", "button")
                {
                    State = LobbyPublicState(true)
                },
                new Ctl(PathPrivate, "Private", "button")
                {
                    State = LobbyPublicState(false)
                },
                // LAN mode only reachable with -lan; inactive in every normal session.
                new Ctl(PathLanServerName, "Server name", "input field")
                {
                    State = InputFieldState(PathLanServerName), Optional = true
                },
                new Ctl(PathAllowRemote, "Allow remote connections", "button") { Optional = true },
                new Ctl(PathLocal, "Local connections only", "button") { Optional = true }
            };

            // Each file is followed by its own delete button. Requested, and it does put a
            // destructive control one Down press from every save: the deletes used to sit in a
            // section of their own at the very end, which is a stronger guard than a label. They
            // keep the label that names the file they delete, and they now also announce
            // themselves as destructive, which is the same warning "Reset all to default" carries.
            List<Ctl> files = new List<Ctl>
            {
                new Ctl(PathFile1, "Save file 1", "button") { State = SaveFileState(PathFile1, 0) },
                new Ctl(PathDelete1, "Delete save file 1", "button") { State = DestructiveState, Optional = true },
                new Ctl(PathFile2, "Save file 2", "button") { State = SaveFileState(PathFile2, 1) },
                new Ctl(PathDelete2, "Delete save file 2", "button") { State = DestructiveState, Optional = true },
                new Ctl(PathFile3, "Save file 3", "button") { State = SaveFileState(PathFile3, 2) },
                new Ctl(PathDelete3, "Delete save file 3", "button") { State = DestructiveState, Optional = true },
                new Ctl(PathChallengeMoon, "Challenge moon", "button") { State = ChallengeMoonState }
            };

            List<Ctl> actions = new List<Ctl>
            {
                new Ctl(PathConfirm, "Confirm and host", "button"),
                new Ctl(PathBack, "Back to main menu", "button")
            };

            // The scene puts each row's DeleteButton on FileN.selectOnRight, and that button's
            // onClick opens the delete confirmation and hides this panel: one keypress from an
            // unguarded delete with no announcement in between. BuildChain still nulls every
            // horizontal link, so right-arrow cannot reach a delete button; the only route to one
            // is down onto it, where it names the file and says it is destructive.
            //
            // To offer the right-arrow shortcut back as an opt-in, add a `DeleteOnRightArrow`
            // entry to ConfigManager and OR it into RebuildHostChain.
            UIScreenDescriptor d = new UIScreenDescriptor
            {
                Id = HostScreenId,
                ResolveRoot = HostRoot,
                // Said on every arrival, so it is the name alone.
                Title = () => "Host settings",
                FocusOnOpen = ResolveNameField
            };
            d.Sections.Add(Section("Host options", options));
            d.Sections.Add(Section("Save files", files));
            d.Sections.Add(Section("Host actions", actions));

            UIScreenRegistry.Register(d);
        }

        private static void RegisterLeaderboardScreen()
        {
            // EnableLeaderboardDisplay only swaps HostSettingsContainer for ChallengeLeaderboard;
            // FilesPanel stays on screen (MenuManager.EnableLeaderboardDisplay). The file rows therefore keep
            // their own screen and are deliberately NOT claimed here: two descriptors claiming
            // one GameObject makes the earlier screen's positions wrong. Getting from the
            // leaderboard back to the files is what Retry is for.
            List<Ctl> controls = new List<Ctl>
            {
                new Ctl(PathRefresh, "Refresh leaderboard", "button"),
                new Ctl(PathSort, "Sort leaderboard", "dropdown"),
                // Only shown once a score has been submitted (MenuManager.EnableLeaderboardDisplay).
                new Ctl(PathRemoveScore, "Remove my score", "button") { Optional = true },
                new Ctl(PathRetry, "Back to host settings", "button"),
                new Ctl(PathLeaderboardBack, "Back to main menu", "button")
            };

            UIScreenDescriptor d = new UIScreenDescriptor
            {
                Id = LeaderboardScreenId,
                ResolveRoot = LeaderboardRoot,
                Title = () => "Challenge leaderboard.",
                FocusOnOpen = () => Resolve(LeaderboardRoot(), PathRefresh)
            };
            d.Sections.Add(Section("Leaderboard", controls));

            UIScreenRegistry.Register(d);
        }

        private static UISection Section(string name, List<Ctl> controls)
        {
            UIEntry[] entries = new UIEntry[controls.Count];
            for (int i = 0; i < controls.Count; i++)
            {
                Ctl c = controls[i];
                entries[i] = new UIEntry(c.Path, c.Label, c.Type)
                {
                    State = c.State,
                    Optional = c.Optional
                };
            }
            return new UISection(name, entries);
        }

        #endregion

        #region State providers

        // Said on arrival, before the button is pressed. The delete confirmation that follows is
        // the real guard; this is what stops the user reaching it by accident in the first place.
        private static readonly Func<string> DestructiveState = delegate { return "destructive"; };

        private static Func<string> InputFieldState(string relativePath)
        {
            return delegate
            {
                Transform t = Resolve(HostRoot(), relativePath);
                if (t == null)
                {
                    return null;
                }
                TMP_InputField field = t.GetComponent<TMP_InputField>();
                if (field == null)
                {
                    return null;
                }
                return string.IsNullOrEmpty(field.text) ? "empty" : field.text;
            };
        }

        /// <summary>
        /// Which of PUBLIC/PRIVATE is chosen is otherwise conveyed only by an Animator
        /// bool ("isPressed", MenuManager.HostSetLobbyPublic). These are plain Buttons, not Toggles, so
        /// nothing about them announces state on its own.
        /// </summary>
        private static Func<string> LobbyPublicState(bool thisButtonMeansPublic)
        {
            return delegate
            {
                MenuManager m = Menu;
                if (m == null)
                {
                    return null;
                }
                return m.hostSettings_LobbyPublic == thisButtonMeansPublic ? "selected" : "not selected";
            };
        }

        private static Func<string> SaveFileState(string relativePath, int fileNum)
        {
            return delegate { return DescribeSaveFile(relativePath, fileNum); };
        }

        private static SaveFileUISlot ResolveSlot(string relativePath)
        {
            Transform t = Resolve(HostRoot(), relativePath);
            return t == null ? null : t.GetComponent<SaveFileUISlot>();
        }

        private static string SlotStats(SaveFileUISlot slot)
        {
            // fileStatsText is disabled rather than blanked for an unattempted challenge file
            // (SaveFileUISlot.SetChallengeFileSettings), so .enabled is part of the reading.
            if (slot == null || slot.fileStatsText == null || !slot.fileStatsText.enabled)
            {
                return null;
            }
            string stats = slot.fileStatsText.text;
            if (string.IsNullOrEmpty(stats) || stats.Trim().Length == 0)
            {
                return null;
            }
            // The stats TMP is two lines ("$1234\nDays: 5"); a comma reads as a pause.
            return stats.Replace("\n", ", ").Trim();
        }

        private static string DescribeSaveFile(string relativePath, int fileNum)
        {
            SaveFileUISlot slot = ResolveSlot(relativePath);
            string stats = SlotStats(slot);
            string state = stats == null ? "empty" : stats;

            MenuManager m = Menu;
            if (m != null && m.filesCompatible != null &&
                fileNum >= 0 && fileNum < m.filesCompatible.Length && !m.filesCompatible[fileNum])
            {
                // The scene shows this as a separate TMP alert that nothing focuses.
                state += ", incompatible";
            }

            if (IsSelectedFile(fileNum))
            {
                state += ", selected";
            }
            return state;
        }

        private static string ChallengeMoonState()
        {
            SaveFileUISlot slot = ResolveSlot(PathChallengeMoon);
            string state = null;

            // The week name is written into fileNameText by SaveFileUISlot.OnEnable; read the
            // live TMP rather than re-deriving it from GetNameForWeekNumber, so the announcement
            // can never disagree with what is on screen.
            if (slot != null && slot.fileNameText != null && !string.IsNullOrEmpty(slot.fileNameText.text))
            {
                state = slot.fileNameText.text;
            }

            string stats = SlotStats(slot);
            state = Join(state, stats == null ? "not yet completed" : stats);

            if (IsSelectedFile(-1))
            {
                state = Join(state, "selected");
            }
            return state;
        }

        private static bool IsSelectedFile(int fileNum)
        {
            return GameNetworkManager.Instance != null && GameNetworkManager.Instance.saveFileNum == fileNum;
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a))
            {
                return b;
            }
            if (string.IsNullOrEmpty(b))
            {
                return a;
            }
            return a + ", " + b;
        }

        #endregion

        #region Navigation

        /// <summary>
        /// Write the authored order into real Navigation links.
        ///
        /// Built from the registry's VISIBLE order, not the declared one: a chain that steps
        /// through a hidden ServerTagInputField eats the keypress, because Selectable.Navigate
        /// drops a target that fails IsActive(). SelectableNavigationPatch skips invisible entries
        /// by itself, but it only runs while ConfigManager.VerticalNavigationOnly is on, and the
        /// links have to be right either way.
        ///
        /// BuildChain also nulls selectOnLeft/selectOnRight on every member, which is what removes
        /// the one-keypress route from a save file row to its delete button and the
        /// Private -> File1 cross-panel jump.
        /// </summary>
        public static void RebuildHostChain()
        {
            BuildChainForScreen(HostScreenId, IsHostPanelVisible());
        }

        public static void RebuildLeaderboardChain()
        {
            Transform root = LeaderboardRoot();
            BuildChainForScreen(LeaderboardScreenId, root != null && UIUtils.IsUIElementVisible(root.gameObject));
        }

        /// <param name="expectVisible">
        /// True when the panel is on screen, so an empty order is a fault rather than the ordinary
        /// "this screen is closed" case.
        /// </param>
        private static void BuildChainForScreen(string screenId, bool expectVisible)
        {
            List<Transform> order = UIScreenRegistry.VisibleFlatOrder(screenId);
            if (order.Count == 0)
            {
                if (expectVisible)
                {
                    Debug.LogError("[LethalAccess] Screen '" + screenId + "' is on screen but not one of its " +
                                   "declared controls resolved to a visible object, so it has no navigation at " +
                                   "all. The relative paths in HostMenuAccess are stale.");
                }
                return;
            }

            List<Selectable> chain = new List<Selectable>(order.Count);
            for (int i = 0; i < order.Count; i++)
            {
                Selectable s = order[i].GetComponent<Selectable>();
                if (s == null)
                {
                    // UIScreenRegistry injects one on every claimed entry, so this means the
                    // object was swapped out since the screen last resolved.
                    Debug.LogWarning("[LethalAccess] Host menu: '" + order[i].name + "' has no Selectable and " +
                                     "was left out of the navigation chain.");
                    continue;
                }
                chain.Add(s);
            }

            NavigationEnforcer.BuildChain(chain, false);
        }

        /// <summary>
        /// Rebuild one frame later.
        ///
        /// ServerTagInputField's active state is not changed by HostSetLobbyPublic; it is driven
        /// from MenuManager.Update reading hostSettings_LobbyPublic, so
        /// immediately after a Public/Private press the panel still has its old shape. A coroutine
        /// that yields once resumes after every Update has run, which is the first moment the
        /// chain can match what is on screen.
        /// </summary>
        public static void ScheduleChainRebuild(MonoBehaviour host)
        {
            if (host == null || rebuildQueued)
            {
                return;
            }
            rebuildQueued = true;
            host.StartCoroutine(RebuildNextFrame());
        }

        private static IEnumerator RebuildNextFrame()
        {
            yield return null;
            rebuildQueued = false;
            if (IsHostPanelVisible())
            {
                RebuildHostChain();
            }
        }

        /// <summary>The server-name field the user can actually see: LAN mode has its own.</summary>
        private static Transform ResolveNameField()
        {
            Transform root = HostRoot();
            if (root == null)
            {
                return null;
            }
            if (IsLan())
            {
                return Resolve(root, PathLanServerName);
            }
            MenuManager m = Menu;
            if (m != null && m.lobbyNameInputField != null)
            {
                return m.lobbyNameInputField.transform;
            }
            return Resolve(root, PathServerName);
        }

        #endregion

        #region Typing feedback

        /// <summary>
        /// Announcement is edge-triggered on selection change, so a text field said its
        /// contents once, at focus time, and then went silent for the whole of the edit. These
        /// hooks are what make typing audible. The HashSet is what stops a second listener being
        /// added every time the panel is re-opened.
        /// </summary>
        public static void HookTextFields()
        {
            Transform root = HostRoot();
            if (root == null)
            {
                return;
            }
            HookField(Resolve(root, PathServerName), "Server name");
            HookField(Resolve(root, PathServerTag), "Server tag");
            HookField(Resolve(root, PathLanServerName), "Server name");
        }

        private static void HookField(Transform t, string label)
        {
            if (t == null)
            {
                return;
            }
            TMP_InputField field = t.GetComponent<TMP_InputField>();
            if (field == null)
            {
                Debug.LogError("[LethalAccess] Host menu: '" + t.name + "' was expected to be a TMP_InputField " +
                               "but is not; typing in it will not be announced.");
                return;
            }

            int id = field.GetInstanceID();
            if (!hookedFields.Add(id))
            {
                return;
            }

            lastFieldText[id] = field.text ?? string.Empty;
            string capturedLabel = label;
            GameObject go = field.gameObject;
            field.onValueChanged.AddListener(delegate(string value) { OnFieldChanged(go, id, capturedLabel, value); });
            field.onEndEdit.AddListener(delegate(string value) { OnFieldEndEdit(go, capturedLabel, value); });
        }

        // These echoes are for Tolk only. When the tree publishes the field, the screen reader
        // already speaks typed and deleted characters from the TextPattern, and speaking them
        // here as well says everything twice.
        private static void OnFieldChanged(GameObject go, int id, string label, string value)
        {
            string now = value ?? string.Empty;
            string before;
            if (!lastFieldText.TryGetValue(id, out before))
            {
                before = string.Empty;
            }
            lastFieldText[id] = now;
            if (AnnouncementPolicy.UiTreeSpeaksFor(go))
            {
                return;
            }

            if (now.Length == before.Length + 1 && now.StartsWith(before, StringComparison.Ordinal))
            {
                AnnouncementPolicy.Speak(DescribeCharacter(now[now.Length - 1]));
                return;
            }
            if (now.Length == 0)
            {
                AnnouncementPolicy.Speak(label + " cleared");
                return;
            }
            if (now.Length == before.Length - 1 && before.StartsWith(now, StringComparison.Ordinal))
            {
                AnnouncementPolicy.Speak(DescribeCharacter(before[before.Length - 1]) + " deleted");
                return;
            }
            // Paste, a mid-string edit, or a multi-character delete: the individual difference is
            // not worth reconstructing, and the whole value is what the user needs to hear.
            AnnouncementPolicy.Speak(now);
        }

        private static void OnFieldEndEdit(GameObject go, string label, string value)
        {
            if (AnnouncementPolicy.UiTreeSpeaksFor(go))
            {
                return;
            }
            AnnouncementPolicy.Speak(string.IsNullOrEmpty(value)
                ? label + " left empty"
                : label + " set to " + value);
        }

        private static string DescribeCharacter(char c)
        {
            if (c == ' ')
            {
                // A lone space is silence, which reads as the keypress having been ignored.
                return "space";
            }
            return c.ToString();
        }

        #endregion
    }

    /// <summary>
    /// Harmony entry points for the host menu. Kept separate from the model above so the patch
    /// surface is visible at a glance.
    /// </summary>
    [HarmonyPatch(typeof(MenuManager))]
    public static class HostMenuAccessPatch
    {
        [HarmonyPatch(nameof(MenuManager.ClickHostButton))]
        [HarmonyPrefix]
        public static void PrefixClickHostButton()
        {
            // Brackets the HostSetLobbyPublic refresh call at the end of ClickHostButton; see
            // HostMenuAccess.SuppressLobbyPublicFrame.
            HostMenuAccess.SuppressLobbyPublicFrame = Time.frameCount;
        }

        [HarmonyPatch(nameof(MenuManager.ClickHostButton))]
        [HarmonyPostfix]
        public static void PostfixClickHostButton(MenuManager __instance)
        {
            if (__instance.menuButtons == null)
            {
                Debug.LogError("[LethalAccess] MenuManager.menuButtons is not assigned, so the main menu buttons " +
                               "cannot be hidden behind the host panel; Down will keep walking them.");
            }
            else
            {
                // The game's own Back handler calls EnableUIPanel(MainButtons) to undo this,
                // so a hidden main menu is the state the scene was authored for. SetActive(false)
                // is idempotent, and the hosting-failure path restores it through
                // MenuManager.SetLoadingScreen(false).
                __instance.menuButtons.SetActive(false);
            }

            // Registering while the panel is on screen resolves, announces and seeds focus inside
            // this call. On later opens the notifier has already done that from OnEnable, two
            // statements before this postfix.
            HostMenuAccess.EnsureRegistered(__instance);

            HostMenuAccess.RebuildHostChain();
            HostMenuAccess.HookTextFields();

            // Seeding again is harmless when ScreenShown already did it (EventSystem ignores a
            // re-select of the same object) and is the only seed on the path where the screen was
            // already registered but focus had been left elsewhere.
            Transform focus = UIScreenRegistry.ResolveFocusTarget(HostMenuAccess.HostScreenId);
            if (focus != null)
            {
                FocusManager.Seed(focus);
            }

            // Catches ServerTagInputField, whose visibility MenuManager.Update settles next frame.
            HostMenuAccess.ScheduleChainRebuild(__instance);
        }

        /// <summary>
        /// ConfirmHostButton writes "Enter a lobby name!" into tipTextHostSettings and
        /// returns. No mod code reads that TMP, so pressing Confirm with an empty name did nothing
        /// and said nothing. This is a void prefix: the original still runs and still performs its
        /// own guard.
        /// </summary>
        [HarmonyPatch(nameof(MenuManager.ConfirmHostButton))]
        [HarmonyPrefix]
        public static void PrefixConfirmHostButton(MenuManager __instance)
        {
            TMP_InputField field = __instance.lobbyNameInputField;
            if (field == null)
            {
                Debug.LogError("[LethalAccess] MenuManager.lobbyNameInputField is not assigned; the empty-name " +
                               "guard on Confirm cannot be announced.");
                return;
            }

            if (string.IsNullOrEmpty(field.text))
            {
                AnnouncementPolicy.Speak("Enter a lobby name.");

                // Seed re-announces the field, so the user hears where they have been put.
                // In LAN mode the visible field is LANOptions/ServerNameField, which the scene does
                // NOT bind to MenuManager.lobbyNameInputField (the serialized reference is the
                // OptionsNormal one). Checked in a LAN launch 2026-10-14: text set on the LAN field
                // does not reach lobbyNameInputField, a vanilla quirk this patch must not paper over
                // by copying text between fields. It does not make Confirm unsatisfiable: the bound
                // field is pre-filled from the save or "<Steam name>'s Crew", and ConfirmHostButton
                // never saves an empty name, so the bound field is never empty.
                Transform target = UIScreenRegistry.ResolveFocusTarget(HostMenuAccess.HostScreenId);
                if (target != null)
                {
                    FocusManager.Seed(target);
                }
                return;
            }

            if (field.text.Length > 40)
            {
                // MenuManager.ConfirmHostButton truncates silently.
                AnnouncementPolicy.Speak("Lobby name shortened to 40 characters.");
            }
        }

        /// <summary>
        /// The leaderboard swap is otherwise silent: SaveFileUISlot.SetFileToThis can call this
        /// with true from a plain file-row press (SaveFileUISlot.SetFileToThis).
        /// </summary>
        [HarmonyPatch(nameof(MenuManager.EnableLeaderboardDisplay))]
        [HarmonyPostfix]
        public static void PostfixEnableLeaderboardDisplay(MenuManager __instance, bool enable)
        {
            // Deliberately does NOT register the screens. The first call of all comes from inside
            // MenuManager.ClickHostButton, one statement before LobbyHostSettings is activated;
            // registering there would resolve and seed focus against the panel's pre-LAN-swap
            // shape. The ClickHostButton postfix registers instead, by which point the panel has
            // settled.
            if (!HostMenuAccess.IsRegistered)
            {
                if (enable)
                {
                    Debug.LogError("[LethalAccess] The challenge leaderboard opened before the host menu was " +
                                   "registered, so its controls have no labels or navigation. This should be " +
                                   "unreachable - the leaderboard is only reachable from the host panel.");
                }
                return;
            }

            if (enable)
            {
                // ScreenShown fired from the notifier when leaderboardContainer was activated, so
                // the title and focus are already handled; the chain has to wait until after
                // removeScoreButton's own SetActive calls, which is now.
                HostMenuAccess.RebuildLeaderboardChain();
                return;
            }

            // ClickHostButton calls EnableLeaderboardDisplay(false) BEFORE it activates
            // LobbyHostSettings (MenuManager.ClickHostButton), so on that path there is nothing on
            // screen yet to chain or focus; the ClickHostButton postfix does both a moment later.
            // Seeding here anyway would make FocusManager.Seed refuse an invisible target and log
            // a warning on every single open.
            if (!IsHostVisible(__instance))
            {
                return;
            }

            HostMenuAccess.RebuildHostChain();

            Transform file1 = UIUtils.FindByPathIncludingInactive(
                __instance.HostSettingsScreen.transform, "FilesPanel/File1");
            if (file1 != null)
            {
                FocusManager.Seed(file1);
            }
        }

        private static bool IsHostVisible(MenuManager instance)
        {
            return instance.HostSettingsScreen != null &&
                   UIUtils.IsUIElementVisible(instance.HostSettingsScreen);
        }
    }
}
