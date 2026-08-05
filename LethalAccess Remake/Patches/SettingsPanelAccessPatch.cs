using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LethalAccess
{
    /// <summary>
    /// The settings panel, described once for BOTH copies.
    ///
    /// `Canvas/MenuContainer/SettingsPanel` (main menu) and
    /// `Systems/UI/Canvas/QuickMenu/SettingsPanel` (pause menu) are the same prefab layout:
    /// identical 24 direct children in identical order, identical components, identical navigation
    /// flags. Both roots ship inactive. So the descriptor below is built from a panel ROOT rather
    /// than from an absolute path, and whichever copy is alive gets registered when it opens.
    ///
    /// The panel root is found by walking UP from a SettingsOption, because SettingsOption.OnEnable
    /// fires for every control the moment either panel is activated - there is no C# entry point
    /// that opens the panel (both are serialized UnityEvents on the Settings button), and
    /// GameObject.Find cannot see an inactive root anyway.
    ///
    /// The section order below is the requested reordering. The important part is that the four
    /// action buttons are LAST: the scene's own order puts them first (children [4]..[7]) and the
    /// entry point the game selects, BackButton, has `selectOnRight = SetToDefault`, so a single
    /// right-arrow press from where the panel opens used to land on the control that wipes every
    /// setting. With Actions at the end and horizontal focus movement suppressed, "Reset all to
    /// default" can only be reached deliberately, from the bottom of the list.
    ///
    /// NOTHING here is flagged KeepsHorizontal, and that is deliberate. Marking the three sliders
    /// KeepsHorizontal looks like the way to preserve their left/right value adjustment and is
    /// exactly backwards: KeepsHorizontal means "let the base finder run", i.e. let left/right MOVE
    /// FOCUS, which is the defect this file exists to fix. Sliders keep their
    /// value adjustment without it: Slider overrides FindSelectableOnLeft/Right and returns null for
    /// a horizontal axis under Automatic, and returns null through the patched base under Vertical
    /// or Explicit, and Slider.OnMove steps the value whenever the finder returned null
    /// (UnityEngine.UI/Slider.OnMove). Dropdowns are opened with Submit and stepped with
    /// up/down inside the option list, so they do not need horizontal movement either.
    /// </summary>
    [HarmonyPatch]
    internal static class SettingsPanelAccessPatch
    {
        // Screen id -> the root it was built for, so a scene reload (new root, same id) rebuilds
        // and the steady state costs one dictionary lookup per SettingsOption.OnEnable.
        private static readonly Dictionary<string, Transform> registeredRoots =
            new Dictionary<string, Transform>();

        private const string PanelRootName = "SettingsPanel";
        private const string ConfirmPanelPath = "PleaseConfirmChangesPanel";
        private const string ConfirmButtonPath = "PleaseConfirmChangesPanel/Panel/Confirm";
        private const string DiscardButtonPath = "PleaseConfirmChangesPanel/Panel/Discard";
        private const string BackButtonPath = "BackButton";

        private static bool warnedMicTextMissing;

        #region Entry point

        /// <summary>
        /// Fires 22 times when either panel opens (once per SettingsOption). Everything after the
        /// first call is a dictionary hit.
        /// </summary>
        [HarmonyPatch(typeof(SettingsOption), nameof(SettingsOption.OnEnable))]
        [HarmonyPostfix]
        public static void PostfixSettingsOptionOnEnable(SettingsOption __instance)
        {
            Transform root = FindPanelRoot(__instance.transform);
            if (root == null)
            {
                // Expected, not a failure: the launch-options scene carries four SettingsOptions
                // (gamma slider, speaker, device, talk mode) that live under
                // Canvas/GameObject/... with no SettingsPanel ancestor at all. Logging here would
                // fire on every launch-screen panel switch.
                return;
            }

            EnsureRegistered(root);
        }

        private static Transform FindPanelRoot(Transform from)
        {
            for (Transform t = from; t != null; t = t.parent)
            {
                if (t.name == PanelRootName)
                {
                    return t;
                }
            }
            return null;
        }

        private static void EnsureRegistered(Transform root)
        {
            string id = ScreenId(root);

            Transform known;
            if (registeredRoots.TryGetValue(id, out known) && known == root)
            {
                return;
            }

            registeredRoots[id] = root;

            // Register while the panel is ALREADY open: UIScreenRegistry attaches its notifier
            // immediately and AddComponent on an active GameObject runs OnEnable synchronously, so
            // this call itself announces the screen and seeds focus. That is what we want here -
            // the panel is open right now and nothing else is going to tell us about it.
            UIScreenRegistry.Register(BuildPanelDescriptor(id, root));
            UIScreenRegistry.Register(BuildConfirmDescriptor(id, root));

            // The scene's Settings button selects BackButton AFTER activating the panel, so the
            // seed the registration just made is about to be overwritten. See the seeder.
            Transform back = UIUtils.FindByPathIncludingInactive(root, BackButtonPath);
            SettingsPanelFocusSeeder seeder = root.GetComponent<SettingsPanelFocusSeeder>();
            if (seeder == null)
            {
                seeder = root.gameObject.AddComponent<SettingsPanelFocusSeeder>();
            }
            seeder.ScreenId = id;
            seeder.GameSelectedOnOpen = back;

            BuildConfirmChain(root);
        }

        /// <summary>
        /// Full hierarchy path, so the main-menu and pause-menu copies get distinct ids and a scene
        /// reload reuses the id it had before (UIScreenRegistry.Register replaces in place).
        /// </summary>
        private static string ScreenId(Transform root)
        {
            string path = root.name;
            for (Transform t = root.parent; t != null; t = t.parent)
            {
                path = t.name + "/" + path;
            }
            return path;
        }

        #endregion

        #region Descriptors

        private static UIScreenDescriptor BuildPanelDescriptor(string id, Transform root)
        {
            Transform captured = root;

            return new UIScreenDescriptor
            {
                Id = id,
                // A destroyed Transform compares equal to null through Unity's operator, so this
                // reports "gone" rather than handing the registry a dead object after a scene load.
                ResolveRoot = delegate { return captured == null ? null : captured; },
                Title = delegate { return "Settings"; },

                // Null means "first visible entry", which by construction is the first entry of
                // Audio. Spelling it out as a path would only add a way for the two to disagree,
                // and the null form degrades correctly if SpeakerButton is ever inactive.
                FocusOnOpen = null,

                // The five content sections are ordered by SCREEN POSITION, not by the order the
                // entries are written. This panel is a three-column layout and its sections are
                // logical groupings that run across the columns, so the authored order does not
                // track what the eye sees: Display is written Brightness, Fullscreen, Framerate,
                // PixelRes but sits at screen heights 710, 892, 1085, 783 - i.e. pressing Down
                // moved the highlight UP the screen twice in a row. Measured in the running game.
                //
                // Actions is deliberately NOT sorted: Back and Reset measure one pixel apart on
                // different columns, and "reset all to default" must stay last on purpose rather
                // than by whichever happens to be lower this build.
                Sections = new List<UISection>
                {
                    new UISection("Audio",
                        new UIEntry("MicSettings/SpeakerButton", "Microphone", "checkbox"),
                        new UIEntry("MicSettings/TalkMode", "Microphone mode", "button")
                        {
                            // Two named modes, and which one is active was announced nowhere:
                            // the control said only "Microphone mode, button", so the setting it
                            // exists to show was unreadable.
                            State = TalkModeState(captured)
                        },
                        new UIEntry("MicSettings/ChooseDevice", "Input device", "button")
                        {
                            // The device name exists ONLY in this button's own TMP - the
                            // previous registration supplied a fixed DisplayName ("Current Input
                            // Device"), and because registered names beat scraped text the mod
                            // permanently hid the one thing the user needed to hear. A live state
                            // provider is the whole point of registering this control.
                            State = MicDeviceState(captured)
                        },
                        new UIEntry("MasterVolume/Slider", "Master volume", "slider"))
                    { OrderByScreenPosition = true },

                    new UISection("Controls",
                        new UIEntry("ControlsOptions/LookSensitivity/Slider", "Look sensitivity", "slider"),
                        new UIEntry("ControlsOptions/InvertYAxis", "Invert Y-Axis", "checkbox"),
                        new UIEntry("ToggleSprint", "Toggle Sprint", "checkbox"),
                        new UIEntry("HeadBobbing", "Head Bobbing", "checkbox"))
                    { OrderByScreenPosition = true },

                    new UISection("Display",
                        new UIEntry("BrightnessSetting/Slider", "Gamma/Brightness", "slider"),
                        new UIEntry("FullscreenMode", "Display mode", "dropdown"),
                        new UIEntry("FramerateCap", "Frame rate cap", "dropdown"),
                        new UIEntry("PixelRes", "Pixel Resolution", "dropdown"))
                    { OrderByScreenPosition = true },

                    new UISection("Graphics",
                        new UIEntry("TerrainDetail", "Terrain / Grass Detail", "dropdown"),
                        new UIEntry("MotionBlur", "Motion Blur", "dropdown"),
                        new UIEntry("AdvancedLighting", "Indirect lighting", "checkbox"))
                    { OrderByScreenPosition = true },

                    new UISection("Accessibility",
                        new UIEntry("ArachniphobiaMode", "Arachnophobia Mode", "checkbox"),
                        new UIEntry("InvertScreenOption", "Flip Screen Horizontally", "checkbox")
                        {
                            // QuickMenuManager.specialOption: activated only when
                            // IngamePlayerSettings.flipCamera is set, which Awake does on April 1
                            // (IngamePlayerSettings.Awake). Absent/inactive is normal, and
                            // positions are counted over visible entries so nothing shifts.
                            Optional = true
                        })
                    { OrderByScreenPosition = true },

                    new UISection("Actions",
                        new UIEntry("KeybindingsButton", "Change keybinds", "button"),
                        new UIEntry("Confirm", "Confirm changes", "button"),
                        new UIEntry(BackButtonPath, "Back", "button")
                        {
                            State = ChangesPendingState
                        },
                        new UIEntry("SetToDefault", "Reset all to default", "button")
                        {
                            // No confirmation dialog, no undo, and it writes to disk immediately.
                            State = delegate { return "destructive"; }
                        })
                }
            };
        }

        /// <summary>
        /// The unsaved-changes modal. It is a child of the settings panel but behaves as its own
        /// screen: it covers the panel, it has two controls, and the user arrives at it by pressing
        /// Escape rather than by navigating.
        /// </summary>
        private static UIScreenDescriptor BuildConfirmDescriptor(string panelId, Transform root)
        {
            Transform captured = root;

            return new UIScreenDescriptor
            {
                Id = panelId + "/" + ConfirmPanelPath,
                ResolveRoot = delegate
                {
                    return captured == null
                        ? null
                        : UIUtils.FindByPathIncludingInactive(captured, ConfirmPanelPath);
                },
                Title = delegate { return "Unsaved changes. Confirm or discard."; },
                Modal = true,

                // Escape backs out the way the game's own Escape path does in gameplay: by
                // discarding. Neither button is labelled "Back", so without this Escape would
                // announce there is no back control on a screen that plainly has a way back.
                EscapeTarget = delegate
                {
                    return captured == null
                        ? null
                        : UIUtils.FindByPathIncludingInactive(captured, DiscardButtonPath);
                },

                // Matches what the game does: DisplayConfirmChangesScreen selects Discard
                // (IngamePlayerSettings.DisplayConfirmChangesScreen) immediately after activating the panel. Naming
                // the same control here means our seed and the game's Select agree instead of
                // fighting, so the user hears the modal announced once.
                FocusOnOpen = delegate
                {
                    return captured == null
                        ? null
                        : UIUtils.FindByPathIncludingInactive(captured, DiscardButtonPath);
                },

                Sections = new List<UISection>
                {
                    new UISection("Unsaved changes",
                        new UIEntry(ConfirmButtonPath, "Confirm changes", "button"),
                        new UIEntry(DiscardButtonPath, "Discard changes", "button"))
                }
            };
        }

        #endregion

        #region State providers

        /// <summary>
        /// `IngamePlayerSettings.SetChangesNotAppliedTextVisible` retitles this one button between
        /// "BACK"/"Back" and "DISCARD"/"Discard changes" depending on hidden state, so scraping its
        /// text made one control announce two different names with no explanation. The authored
        /// label stays "Back"; the difference that actually matters -
        /// that pressing it now throws work away - is said out loud instead.
        /// </summary>
        private static string ChangesPendingState()
        {
            IngamePlayerSettings settings = IngamePlayerSettings.Instance;
            if (settings == null)
            {
                // A DontDestroyOnLoad singleton that is somehow absent while its own panel is on
                // screen: say nothing extra rather than guess at the pending state.
                return null;
            }

            return settings.changesNotApplied
                ? "unsaved changes pending, activating this discards them"
                : null;
        }

        private static Func<string> MicDeviceState(Transform root)
        {
            return delegate
            {
                if (root == null)
                {
                    return null;
                }

                Transform t = UIUtils.FindByPathIncludingInactive(root, "MicSettings/ChooseDevice");
                if (t == null)
                {
                    return null;
                }

                SettingsOption option = t.GetComponent<SettingsOption>();
                if (option != null && option.textElement != null)
                {
                    // This TMP is the single place the live device name is written, by
                    // RefreshAndDisplayCurrentMicrophone and SwitchMicrophoneSetting.
                    // Reading it - rather than settings.micDevice or unsavedSettings.micDevice -
                    // is the only way to always match what is on screen, because those two writers
                    // target different Settings instances.
                    return NormaliseDeviceText(option.textElement.text);
                }

                if (!warnedMicTextMissing)
                {
                    warnedMicTextMissing = true;
                    Debug.LogError("[LethalAccess] The microphone device button has no SettingsOption.textElement, " +
                                   "so the device name cannot be read from the control the user is on. Falling back " +
                                   "to IngamePlayerSettings.unsavedSettings.micDevice.");
                }

                IngamePlayerSettings settings = IngamePlayerSettings.Instance;
                if (settings == null)
                {
                    return null;
                }

                string device = settings.unsavedSettings.micDevice;
                // "LCNoMic" is the game's sentinel for "nothing detected" (LoadSettingsFromPrefs :182).
                return string.IsNullOrEmpty(device) || device == "LCNoMic" ? "no device" : device;
            };
        }

        /// <summary>
        /// Which of the two microphone modes is active.
        ///
        /// The button is not a checkbox: its two values are named states ("Push to talk" /
        /// "Voice activation"), not on and off, so it publishes as a button carrying a value.
        /// Read from the same TMP the player sees, for the reason MicDeviceState gives; the
        /// fallback is unsavedSettings.pushToTalk, which is the field
        /// `IngamePlayerSettings.SetMicPushToTalk` toggles and derives that text from.
        /// </summary>
        private static Func<string> TalkModeState(Transform root)
        {
            return delegate
            {
                if (root == null)
                {
                    return null;
                }

                Transform t = UIUtils.FindByPathIncludingInactive(root, "MicSettings/TalkMode");
                if (t != null)
                {
                    SettingsOption option = t.GetComponent<SettingsOption>();
                    if (option != null && option.textElement != null)
                    {
                        string text = StripCaption(option.textElement.text, "MODE:");
                        if (!string.IsNullOrEmpty(text))
                        {
                            return text;
                        }
                    }
                }

                IngamePlayerSettings settings = IngamePlayerSettings.Instance;
                if (settings == null)
                {
                    return null;
                }

                return settings.unsavedSettings.pushToTalk ? "Push to talk" : "Voice activation";
            };
        }

        // The game writes "Current input device: \n <name>" into this button's TMP. The caption is
        // already the entry's authored label, and the embedded newline leaves a run of spaces.
        private static string NormaliseDeviceText(string raw)
        {
            return StripCaption(raw, "Current input device:");
        }

        /// <summary>
        /// One line of the value, with a leading caption removed. These TMP labels carry the
        /// control's name and its value in a single string, and the name is spoken already.
        /// </summary>
        private static string StripCaption(string raw, string caption)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            string text = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();

            if (text.StartsWith(caption, StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(caption.Length);
            }

            while (text.Contains("  "))
            {
                text = text.Replace("  ", " ");
            }

            text = text.Trim();
            return text.Length == 0 ? null : text;
        }

        #endregion

        #region Confirm-changes modal

        private static void BuildConfirmChain(Transform root)
        {
            Transform confirm = UIUtils.FindByPathIncludingInactive(root, ConfirmButtonPath);
            Transform discard = UIUtils.FindByPathIncludingInactive(root, DiscardButtonPath);

            if (confirm == null || discard == null)
            {
                Debug.LogError("[LethalAccess] The unsaved-changes modal under '" + root.name +
                               "' is missing Confirm and/or Discard, so its navigation cannot be built. " +
                               "Pressing Escape with pending changes will strand the keyboard on it.");
                return;
            }

            // Redundant while VerticalNavigationOnly is on (SelectableNavigationPatch routes claimed
            // controls through the registry), but it is what makes the modal behave when that config
            // is off: the scene already links Confirm down->Discard and Discard up->Confirm, and this
            // additionally removes the horizontal links so no arrow leaves a two-button modal.
            NavigationEnforcer.BuildChain(confirm.GetComponent<Selectable>(), discard.GetComponent<Selectable>());
        }

        /// <summary>
        /// The show path is announced by the modal's own screen descriptor; this exists for
        /// the two things `DisplayConfirmChangesScreen` does unconditionally, which are wrong on the
        /// hide path (IngamePlayerSettings.DisplayConfirmChangesScreen):
        ///   - `KeybindsPanel.SetActive(!visible)` FORCE-OPENS the keybinds overlay when hiding;
        ///   - `PleaseConfirmChangesSettingsPanelBackButton.Select()` parks the EventSystem on the
        ///     Discard button that line 781 deactivated one statement earlier, which kills arrow
        ///     navigation with no announcement.
        /// Vanilla only ever calls this with visible = true (PlayerControllerB.OpenMenu_performed), so the
        /// repair below is for other mods and for any future caller - it is not dead code by
        /// accident, and it costs nothing on the show path.
        /// </summary>
        [HarmonyPatch(typeof(IngamePlayerSettings), nameof(IngamePlayerSettings.DisplayConfirmChangesScreen))]
        [HarmonyPostfix]
        public static void PostfixDisplayConfirmChangesScreen(bool visible)
        {
            if (visible)
            {
                return;
            }

            GameObject confirmPanel;
            GameObject keybindsPanel;
            ResolveManagerPanels(out confirmPanel, out keybindsPanel);

            if (keybindsPanel != null && keybindsPanel.activeSelf)
            {
                // Nothing asked for the keybinds overlay; the game turned it on as the inverse of
                // the modal it was turning off.
                keybindsPanel.SetActive(false);
            }

            if (confirmPanel == null)
            {
                return;
            }

            Transform root = FindPanelRoot(confirmPanel.transform);
            if (root == null)
            {
                Debug.LogWarning("[LethalAccess] The unsaved-changes modal is not under a '" + PanelRootName +
                                 "' ancestor, so focus cannot be returned to the settings panel.");
                return;
            }

            Transform target = UIScreenRegistry.ResolveFocusTarget(ScreenId(root));
            if (target != null)
            {
                FocusManager.Seed(target);
            }
        }

        /// <summary>
        /// Mirrors the manager lookup DisplayConfirmChangesScreen itself performs, including the
        /// MenuManager-wins-over-QuickMenuManager order, so we always repair the same panel the
        /// game just touched. FindObjectOfType only sees active objects - that is exactly what the
        /// game relies on to tell the main menu apart from the pause menu.
        /// </summary>
        private static void ResolveManagerPanels(out GameObject confirmPanel, out GameObject keybindsPanel)
        {
            confirmPanel = null;
            keybindsPanel = null;

            MenuManager menuManager = UnityEngine.Object.FindObjectOfType<MenuManager>();
            if (menuManager != null)
            {
                confirmPanel = menuManager.PleaseConfirmChangesSettingsPanel;
                keybindsPanel = menuManager.KeybindsPanel;
                return;
            }

            QuickMenuManager quickMenuManager = UnityEngine.Object.FindObjectOfType<QuickMenuManager>();
            if (quickMenuManager != null)
            {
                confirmPanel = quickMenuManager.PleaseConfirmChangesSettingsPanel;
                keybindsPanel = quickMenuManager.KeybindsPanel;
            }
        }

        #endregion
    }

    /// <summary>
    /// Puts focus back where the screen model says it belongs, one frame after the panel opens.
    ///
    /// This is not defensive coding, it is a race the descriptor cannot win on its own. Opening
    /// Settings runs a serialized onClick list, and the `Selectable.Select()` entry targeting
    /// BackButton runs AFTER the `EnableUIPanel` entry that activates the panel - so the panel's
    /// OnEnable (and therefore UIScreenRegistry.ScreenShown, and therefore the seed) has already
    /// happened by the time the game overwrites the selection. With the action buttons moved to the
    /// end of the order, BackButton is the wrong place to start.
    ///
    /// LateUpdate rather than a coroutine: every onClick listener runs during the Update phase, so
    /// LateUpdate of the SAME frame is the earliest point at which the whole list is guaranteed to
    /// have run, and it is early enough to beat the announcer when script order happens to put
    /// UIAccessibilityManager.Update before the EventSystem's.
    /// </summary>
    internal sealed class SettingsPanelFocusSeeder : MonoBehaviour
    {
        public string ScreenId;

        /// <summary>The control the scene hard-selects on open; the only selection we will override.</summary>
        public Transform GameSelectedOnOpen;

        private int enabledFrame = -1;

        private void OnEnable()
        {
            enabledFrame = Time.frameCount;
        }

        private void OnDisable()
        {
            enabledFrame = -1;
        }

        private void LateUpdate()
        {
            if (enabledFrame < 0)
            {
                return;
            }

            // Two frames of cover, then stop looking: anything later is the user navigating and
            // must never be overridden.
            if (Time.frameCount - enabledFrame > 1)
            {
                enabledFrame = -1;
                return;
            }

            if (string.IsNullOrEmpty(ScreenId) || UIScreenRegistry.ActiveScreenId != ScreenId)
            {
                // A sub-panel (keybinds, the unsaved-changes modal) is on top; its focus is its own.
                return;
            }

            EventSystem es = EventSystem.current;
            if (es == null)
            {
                return;
            }

            GameObject selected = es.currentSelectedGameObject;
            bool selectionIsTheGamesGuess = selected == null ||
                                            (GameSelectedOnOpen != null && selected == GameSelectedOnOpen.gameObject);
            if (!selectionIsTheGamesGuess)
            {
                enabledFrame = -1;
                return;
            }

            Transform target = UIScreenRegistry.ResolveFocusTarget(ScreenId);
            if (target == null)
            {
                // ScreenShown has already warned about a screen with nothing visible to focus.
                return;
            }

            if (selected != null && target.gameObject == selected)
            {
                enabledFrame = -1;
                return;
            }

            FocusManager.Seed(target);
            enabledFrame = -1;
        }
    }
}
