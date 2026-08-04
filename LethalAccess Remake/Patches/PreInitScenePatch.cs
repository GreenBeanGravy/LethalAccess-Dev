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
    /// The pre-init scene (InitSceneLaunchOptions): the first-run SET-UP panels, the LAUNCH MODE
    /// screen every player sees at boot, and the corrupted-save-file screen.
    ///
    /// Two defects live here.
    ///
    /// FIRST: "no speech, no navigation" on the LAUNCH MODE screen on a first run. One missing
    /// line inside a single synchronous PressContinueButton call:
    ///   1. LaunchSettingsPanels[2] (LANOrOnline) is activated, so OnlineButton becomes active and
    ///      its SelectUIForGamepad.OnEnable calls Button.Select(). Focus is correct.
    ///   2. Two lines later continueButton.SetActive(false) runs that button's
    ///      SelectUIForGamepad.OnDisable, which calls SetSelectedGameObject(null) and WIPES the
    ///      selection made in step 1.
    ///   3. PressContinueButton returns without re-selecting. SkipToFinalSetting does the identical
    ///      deactivate and then repairs it (PreInitSceneScript.SkipToFinalSetting); PressContinueButton has
    ///      no equivalent.
    /// EventSystem.m_FirstSelected is null in this scene (verified live), so nothing recovers, and
    /// the announcer is gated on a non-null selection - silent AND unnavigable from one cause.
    /// A postfix that re-seeds focus is the whole fix.
    ///
    /// SECOND: the first-run SET-UP screen, which the mod had never described at all - no title,
    /// no labels, no positions, and a CONFIRM button whose "n of m" fell out of a sibling scan that
    /// finds exactly one Button under Canvas/GameObject and therefore returns nothing.
    ///
    /// On a save with playerHasFinishedSetup == true the LAUNCH MODE screen already works (verified
    /// live: OnlineButton focused, navigation Vertical, " Online, button, 1 of 2"). Everything here
    /// is written to be idempotent so that path is not disturbed; the only thing it gains is the
    /// screen title, which it has never spoken.
    /// </summary>
    [HarmonyPatch(typeof(PreInitSceneScript))]
    public static class PreInitScenePatch
    {
        public const string ScreenId = "PreInitLaunchOptions";
        public const string CorruptedScreenId = "PreInitFilesCorrupted";

        // waitToLoadSettings does ES3.Init plus an unconditional WaitForSeconds(0.5f) before it
        // enables anything, so a couple of seconds is normal. This bound only exists so that a
        // never-satisfied wait becomes a log line instead of a screen that is silent forever.
        private const float StartupTimeoutSeconds = 15f;

        // The live PreInitSceneScript. Every descriptor delegate reads through this rather than
        // capturing transforms, so a destroyed scene degrades to "root did not resolve" instead of
        // to a MissingReferenceException raised from inside the registry.
        private static PreInitSceneScript script;

        private static PreInitSceneScript registeredFor;
        private static PreInitSceneScript corruptedRegisteredFor;

        // Which launch panel has already been announced, so that two paths settling on the same one
        // say it once. Reset per PreInitSceneScript instance in PostfixStart.
        private static int lastAnnouncedPanel = -1;

        #region Patches

        /// <summary>
        /// Start is private, so it is patched by string name.
        ///
        /// The old implementation called __instance.PressContinueButton() from here. That is frame
        /// 0: the container Canvas/GameObject ships inactive and is only switched on by
        /// IngamePlayerSettings.waitToLoadSettings after an unconditional WaitForSeconds(0.5f), so
        /// blackTransition.SetTrigger targeted an Animator on an inactive object ("Animator has not
        /// been initialized" in the log) and the panel change happened before anything could see
        /// it. The work is now a coroutine that waits on the container's STATE.
        /// </summary>
        [HarmonyPatch("Start")]
        [HarmonyPostfix]
        public static void PostfixStart(PreInitSceneScript __instance)
        {
            script = __instance;

            // A fresh scene means the panel this static last described is gone.
            lastAnnouncedPanel = -1;

            // Registered now, while its root is still inactive, so the notifier is in place before
            // DisplaySaveFileError can enable it. Attaching to an inactive object defers OnEnable,
            // which is exactly what we want here.
            RegisterCorruptedScreen(__instance);

            // The launch screen is registered here for the SAME reason, and registering it later
            // was an observed defect rather than a theoretical one. waitToLoadSettings activates
            // launchSettingsPanelsContainer after a delay, and SelectUIForGamepad.OnEnable selects
            // OnlineButton in that same moment. If the descriptor is not in place by then, the
            // announcer describes the button from its raw scene text ("Online, button, 1 of 2"),
            // and the descriptor describes it AGAIN when it lands ("Online, recommended, button,
            // 1 of 2") - the user hears one control twice, worded differently, with the screen
            // title wedged between. Registering against the still-inactive root defers resolution
            // until the notifier's OnEnable, which is exactly the frame the screen appears.
            //
            // Suppressing the interval instead does NOT work: AnnouncementPolicy.Suppress
            // force-releases after 2 s and waitToLoadSettings routinely takes longer, so the scope
            // expired mid-wait and then got double-released by its own disposal.
            RegisterLaunchScreen(__instance);

            __instance.StartCoroutine(PreInitFlow(__instance));
        }

        /// <summary>
        /// Guards a latent IndexOutOfRangeException. PreInitSceneScript checks
        /// `currentLaunchSettingPanel &lt; Length` and then PRE-increments before indexing, so
        /// calling it on the final panel throws
        /// IndexOutOfRangeException. Unreachable from the UI because continueButton is deactivated
        /// when the final panel is reached - and reachable the moment this mod, or any other, calls
        /// the method directly.
        /// </summary>
        [HarmonyPatch("PressContinueButton")]
        [HarmonyPrefix]
        public static bool PrefixPressContinueButton(PreInitSceneScript __instance, ref bool __state)
        {
            script = __instance;

            // __state answers "did the original actually advance a panel?", so the postfix can tell
            // a real panel change from a call this prefix swallowed.
            __state = false;

            GameObject[] panels = __instance.LaunchSettingsPanels;
            if (panels == null || panels.Length == 0)
            {
                Debug.LogError("[LethalAccess] PressContinueButton was called but LaunchSettingsPanels is " +
                               "empty, so there is no panel to advance to. The pre-init screen is broken.");
                return false;
            }

            if (__instance.currentLaunchSettingPanel >= panels.Length - 1)
            {
                Debug.Log("[LethalAccess] Ignored a PressContinueButton call on the final launch panel; " +
                          "the vanilla method would have indexed past the end of LaunchSettingsPanels.");
                return false;
            }

            __state = true;
            return true;
        }

        /// <summary>
        /// Runs after SelectUIForGamepad.OnDisable has nulled the selection, which is the whole
        /// point: this is the re-selection PressContinueButton never had.
        /// </summary>
        [HarmonyPatch("PressContinueButton")]
        [HarmonyPostfix]
        public static void PostfixPressContinueButton(PreInitSceneScript __instance, bool __state)
        {
            if (!__state)
            {
                // The prefix blocked the call, so nothing on screen moved. Announcing a panel
                // change that did not happen would be a lie.
                return;
            }

            AnnouncePanelChange(__instance);
        }

        /// <summary>
        /// The returning-player path. SkipToFinalSetting already selects OnlineModeButton itself,
        /// so focus must not be fought over here - but it has never said WHICH screen the player
        /// is on.
        ///
        /// This runs from waitToLoadSettings in the same frame that enables the container, which is
        /// EARLIER than the coroutine's own announce. Whichever gets there first names the screen;
        /// AnnouncePanelChange's per-panel guard makes the loser a no-op. Both paths are kept
        /// because MonoBehaviour coroutine order is undefined and neither is guaranteed to be the
        /// one that runs.
        /// </summary>
        [HarmonyPatch("SkipToFinalSetting")]
        [HarmonyPostfix]
        public static void PostfixSkipToFinalSetting(PreInitSceneScript __instance)
        {
            script = __instance;

            if (registeredFor == __instance)
            {
                // Coroutine update order between MonoBehaviours is not defined, so handle the
                // ordering where registration already happened: nothing else would announce the
                // switch to LAUNCH MODE.
                AnnouncePanelChange(__instance);
                return;
            }

            FocusManager.ClearLastAnnounced();
        }

        /// <summary>
        /// The corrupted-save screen. Activating FileCorruptedPanel has already run the registry's
        /// notifier, so the notice has been read out and focus seeded; the game's own
        /// SetSelectedGameObject on the last line re-selects the same button, so there is nothing to
        /// repair there.
        ///
        /// What is left is navigation: Canvas/FilesCorruptedScreen/Panel/Confirm ships
        /// Navigation.Mode.None (verified live), which makes it a dead end for the arrow keys - and
        /// unreachable FROM anything, since Selectable.FindSelectable skips Mode.None candidates -
        /// whenever VerticalNavigationOnly is off. That button deletes every save file, so it must
        /// not be the only thing a player can reach and it must not be reachable by accident.
        /// </summary>
        [HarmonyPatch("EnableFileCorruptedScreen")]
        [HarmonyPostfix]
        public static void PostfixEnableFileCorruptedScreen(PreInitSceneScript __instance)
        {
            script = __instance;

            if (corruptedRegisteredFor != __instance)
            {
                // Start's postfix did not run (or ran for another instance). Registering now, with
                // the root already active, makes Unity run the notifier's OnEnable synchronously,
                // so the notice is still announced.
                RegisterCorruptedScreen(__instance);
            }

            if (corruptedRegisteredFor != __instance)
            {
                // RegisterCorruptedScreen already said why. Do not follow it with ScreenShown on an
                // unregistered id, which would only add a second, less useful complaint.
                return;
            }

            // No-op if the notifier already fired this frame; the registry de-duplicates by frame.
            UIScreenRegistry.ScreenShown(CorruptedScreenId);
            RebuildChain(CorruptedScreenId);
        }

        #endregion

        #region Startup flow

        /// <summary>
        /// Waits for the launch-options container to actually be on screen, then decides what the
        /// player is looking at and describes it.
        ///
        /// Waiting on STATE rather than on a 0.5 s sleep is not fussiness:
        /// IngamePlayerSettings.waitToLoadSettings owns that timing, and it can branch into
        /// DisplaySaveFileError instead, in which case the container is never enabled at all and the
        /// corrupted-save screen comes up instead.
        /// </summary>
        private static IEnumerator PreInitFlow(PreInitSceneScript s)
        {
            float deadline = Time.unscaledTime + StartupTimeoutSeconds;

            while (true)
            {
                if (s == null)
                {
                    yield break;
                }

                if (s.FileCorruptedPanel != null && s.FileCorruptedPanel.activeInHierarchy)
                {
                    // LoadSettingsFromPrefs threw. The corrupted-save screen owns the display and
                    // has its own descriptor; there is no launch-options screen to describe.
                    yield break;
                }

                if (s.launchSettingsPanelsContainer != null && s.launchSettingsPanelsContainer.activeInHierarchy)
                {
                    break;
                }

                if (Time.unscaledTime > deadline)
                {
                    Debug.LogError("[LethalAccess] The launch-options panels never became active within " +
                                   StartupTimeoutSeconds + " s and the corrupted-save screen never came up " +
                                   "either. IngamePlayerSettings.waitToLoadSettings did not finish, so the " +
                                   "pre-init screen will not be announced or navigable.");
                    yield break;
                }

                yield return null;
            }

            // SetLaunchPanelsEnabled and SkipToFinalSetting are consecutive statements in
            // waitToLoadSettings, but coroutine update order between MonoBehaviours is undefined, so
            // the loop above can break in the frame BEFORE SkipToFinalSetting runs. One frame makes
            // the returning-player check below correct under either ordering.
            yield return null;
            if (s == null)
            {
                yield break;
            }

            GameObject[] panels = s.LaunchSettingsPanels;
            if (panels == null || panels.Length == 0)
            {
                Debug.LogError("[LethalAccess] The pre-init scene has no LaunchSettingsPanels, so its screen " +
                               "cannot be described. Nothing on it will be announced.");
                yield break;
            }

            if (s.currentLaunchSettingPanel == 0 && panels.Length > 2)
            {
                // The gamma panel asks the player to raise brightness "until the symbol on the right
                // is barely visible" - there is nothing a blind player can act on, and the mod has
                // always skipped it. Done by hand rather than through PressContinueButton because
                // this is not a user action: it must not fire the black transition.
                //
                // Guarded on Length > 2 so this can never land on the FINAL panel, which vanilla
                // finalises by deactivating continueButton and rewriting the header - work this
                // shortcut does not do.
                if (panels[0] != null)
                {
                    panels[0].SetActive(false);
                }
                s.currentLaunchSettingPanel = 1;
                if (panels[1] != null)
                {
                    panels[1].SetActive(true);
                }
            }

            // No ScreenShown call here. Registration happens in the Start postfix against a
            // still-inactive root, so the notifier's OnEnable has already fired it by the time this
            // coroutine resumes, and the descriptor is flagged SuppressAnnounceOnShow so that
            // firing did not speak.
            //
            // RegisterLaunchScreen is still called, but only as an idempotent guard for the case
            // where the Start postfix did not run for this instance; it returns immediately when
            // this script is already registered.
            RegisterLaunchScreen(s);

            // The panel has settled now, so this is the moment the screen can be named correctly.
            // On a first run this is the ONLY announcer: the gamma skip above is a silent state
            // change, and SkipToFinalSetting never runs because the player has not finished setup.
            // On a returning-player boot SkipToFinalSetting has usually announced already, and the
            // per-panel guard inside makes this second call a no-op rather than an echo.
            AnnouncePanelChange(s);
        }

        #endregion

        #region Announcement

        /// <summary>
        /// One panel changed to another: rewire navigation for what is now visible, say the new
        /// screen title, and put focus on the first control of the new panel.
        /// </summary>
        private static void AnnouncePanelChange(PreInitSceneScript s)
        {
            GameObject[] panels = s.LaunchSettingsPanels;
            if (panels == null || panels.Length == 0)
            {
                Debug.LogError("[LethalAccess] The pre-init panel changed but LaunchSettingsPanels is empty; " +
                               "there is nothing to focus and nothing to announce.");
                return;
            }

            int index = s.currentLaunchSettingPanel;
            if (index < 0 || index >= panels.Length)
            {
                Debug.LogError("[LethalAccess] The pre-init panel index is " + index + ", which is outside " +
                               "LaunchSettingsPanels (length " + panels.Length + "). Focus cannot be seeded.");
                return;
            }

            // Several paths legitimately settle on the same panel - PostfixSkipToFinalSetting and
            // the coroutine can both land on the final one, in either order, depending on
            // MonoBehaviour update order. Announcing per PANEL rather than per call makes that
            // harmless. The scene is loaded once per process and lastAnnouncedPanel is reset when a
            // new PreInitSceneScript appears, so no panel is ever wrongly skipped.
            if (index == lastAnnouncedPanel)
            {
                return;
            }
            lastAnnouncedPanel = index;

            RebuildChain(ScreenId);

            Transform target = null;

            if (index >= panels.Length - 1 && s.OnlineModeButton != null)
            {
                // This is the re-seed that fixes the silent LAUNCH MODE screen described at the top
                // of this file. OnlineModeButton is a serialized field wired to
                // Canvas/GameObject/LANOrOnline/OnlineButton, so no lookup is needed - and using the
                // field rather than the screen order means focus is restored even if the descriptor
                // failed to resolve.
                target = s.OnlineModeButton.transform;
            }

            if (target == null && panels[index] != null)
            {
                target = FirstVisibleUnder(panels[index].transform);
            }

            if (target == null)
            {
                Debug.LogError("[LethalAccess] The pre-init screen advanced to panel '" +
                               (panels[index] != null ? panels[index].name : "<null>") +
                               "' but no focusable control could be found on it. The keyboard is dead " +
                               "on this screen.");
                return;
            }

            // Lifts the mute the descriptor's SuppressAnnounceOnShow put on this screen. Must come
            // before the announcement, or the announcement and the control that follows it are
            // swallowed by the very mute this call exists to clear.
            UIScreenRegistry.MarkSettled(ScreenId);

            // Speaks the title, then seeds focus; the newly focused control is announced by
            // UIAccessibilityManager on the next Update and the speech queue is FIFO, so the two
            // arrive in that order.
            AnnouncementPolicy.AnnounceScreen(ScreenTitle(), target);
        }

        /// <summary>
        /// The live header plus the active panel's instruction text. Both are read from the scene
        /// every time: the header is rewritten to "LAUNCH MODE" at runtime, and hand-authored
        /// strings in this game go stale (the news popup still says "Version 80" on version 81), so
        /// nothing here is ever synthesised.
        /// </summary>
        private static string ScreenTitle()
        {
            PreInitSceneScript s = script;
            if (s == null)
            {
                return null;
            }

            string header = (s.headerText != null) ? Flatten(s.headerText.text) : null;
            string instruction = ActivePanelInstruction(s);

            if (string.IsNullOrEmpty(header))
            {
                return instruction;
            }
            if (string.IsNullOrEmpty(instruction))
            {
                return header;
            }
            return header + ". " + instruction;
        }

        private static string ActivePanelInstruction(PreInitSceneScript s)
        {
            GameObject[] panels = s.LaunchSettingsPanels;
            if (panels == null)
            {
                return null;
            }

            int index = s.currentLaunchSettingPanel;
            if (index < 0 || index >= panels.Length || panels[index] == null)
            {
                return null;
            }

            // LANOrOnline has no Instruction child - its header ("LAUNCH MODE") already says
            // everything - so absence here is normal and must not be reported as a fault.
            Transform instruction = panels[index].transform.Find("Instruction");
            if (instruction == null)
            {
                return null;
            }

            TextMeshProUGUI tmp = instruction.GetComponent<TextMeshProUGUI>();
            if (tmp == null)
            {
                Debug.LogWarning("[LethalAccess] '" + panels[index].name + "/Instruction' has no " +
                                 "TextMeshProUGUI, so the panel's instructions cannot be read out.");
                return null;
            }

            return Flatten(tmp.text);
        }

        #endregion

        #region Screen registration

        private static void RegisterLaunchScreen(PreInitSceneScript s)
        {
            if (registeredFor == s)
            {
                return;
            }

            UIScreenDescriptor d = new UIScreenDescriptor();
            d.Id = ScreenId;
            d.Title = ScreenTitle;

            // The container switches on with the GAMMA panel up and then advances - either by
            // SkipToFinalSetting for a returning player or by the gamma skip in PreInitFlow on a
            // first run - inside the SAME frame. Announcing on show therefore read the title of a
            // panel nobody sees: every boot opened with "SET-UP. Adjust screen brightness until the
            // symbol on the right is barely visible." followed immediately by the real screen, and
            // on a first run the index went with it ("Confirm, button, 2 of 2" for a panel that has
            // four entries).
            //
            // AnnouncePanelChange is the single announcer instead. It runs on every path that
            // settles a panel and is idempotent per panel, so the screen is announced exactly once,
            // after the panel it is describing is actually the one on screen.
            d.SuppressAnnounceOnShow = true;

            // Resolved from the component reference, not from a path: this scene is unloaded the
            // moment a launch mode is chosen, and GameObject.Find cannot see inactive objects
            // anyway. Entry paths below are relative to this root, which is a Transform walk, not a
            // scene-wide search.
            d.ResolveRoot = delegate
            {
                PreInitSceneScript live = script;
                return (live != null && live.launchSettingsPanelsContainer != null)
                    ? live.launchSettingsPanelsContainer.transform
                    : null;
            };

            // FocusOnOpen is deliberately null. Exactly one panel is visible at a time, so "the
            // first visible entry in authored order" is always the right answer: the gamma slider,
            // then ChooseDevice, then OnlineButton, as the panels advance.
            d.FocusOnOpen = null;

            // ONE section, not one per panel, and Confirm last.
            //
            // Putting Confirm in its own section so that it announces "4 of 4" cannot work here:
            // TryGetPosition's index is scoped to the SECTION, so an own-section Confirm announces
            // "1 of 1" instead. Keeping the whole screen in one section is what delivers the right
            // numbers, because only one panel's controls are ever visible at once:
            //   gamma panel  -> "Gamma and brightness 1 of 2", "Confirm 2 of 2"
            //   voice panel  -> "Input device 1 of 4" ... "Confirm 4 of 4"
            //   launch mode  -> "Online 1 of 2", "LAN 2 of 2"   (Confirm is deactivated by then)
            // It also gives the arrow keys a single unbroken chain, which is what makes the three
            // mic controls safe: they are NOT in a column (ChooseDevice 246,70.3 / SpeakerButton
            // -142.1,1.2 / TalkMode 95,-37), so some pairs are only reachable horizontally today
            // and suppressing left/right without this authored order would strand them.
            UISection section = new UISection("Launch options");

            Transform root = (s.launchSettingsPanelsContainer != null)
                ? s.launchSettingsPanelsContainer.transform
                : null;

            // Direct plus RelativePath throughout: Direct is the live component reference (or a
            // one-time Transform.Find off the root, which walks inactive children where
            // GameObject.Find would not), and RelativePath is the fallback the registry uses if
            // Direct is ever destroyed. Precedence is Direct, then Resolver, then RelativePath.
            UIEntry gamma = new UIEntry();
            gamma.Direct = (s.gammaSlider != null) ? s.gammaSlider.transform : null;
            gamma.RelativePath = "GammaSetting/BrightnessSetting/Slider";
            gamma.Label = "Gamma and brightness";
            gamma.Type = "slider";
            // Not needed for left/right to adjust the value - Slider overrides the finders and
            // returns null while mode is Automatic and the axis is Horizontal, and its OnMove steps
            // the value whenever the finder returns null - but flagged so the behaviour survives the
            // explicit chain RebuildChain writes, which switches the mode away from Automatic.
            gamma.KeepsHorizontal = true;
            gamma.State = GammaState;
            section.Entries.Add(gamma);

            section.Entries.Add(MicEntry(root, "VoiceChatSetting/MicSettings/ChooseDevice", "Input device"));
            section.Entries.Add(MicEntry(root, "VoiceChatSetting/MicSettings/SpeakerButton", "Microphone"));
            section.Entries.Add(MicEntry(root, "VoiceChatSetting/MicSettings/TalkMode", "Microphone mode"));

            UIEntry online = new UIEntry();
            online.Direct = (s.OnlineModeButton != null) ? s.OnlineModeButton.transform : null;
            online.RelativePath = "LANOrOnline/OnlineButton";
            // Authored rather than scraped: the button holds three TMPs and the game's own
            // GetComponentInChildren stops at the first one, so the "(Recommended)" child is never
            // read. Dropping it would hide the only steer the game gives a new player.
            online.Label = "Online, recommended";
            online.Type = "button";
            section.Entries.Add(online);

            UIEntry lan = new UIEntry();
            // No serialized field points at this one, so it is found once off the root.
            lan.Direct = (root != null) ? root.Find("LANOrOnline/LANButton") : null;
            lan.RelativePath = "LANOrOnline/LANButton";
            lan.Label = "LAN";
            lan.Type = "button";
            section.Entries.Add(lan);

            UIEntry confirm = new UIEntry();
            confirm.Direct = (s.continueButton != null) ? s.continueButton.transform : null;
            confirm.RelativePath = "ContinueButton";
            confirm.Label = "Confirm";
            confirm.Type = "button";
            section.Entries.Add(confirm);

            d.Sections.Add(section);

            UIScreenRegistry.Register(d);
            registeredFor = s;
        }

        /// <summary>One of the three voice-chat controls.</summary>
        private static UIEntry MicEntry(Transform root, string relativePath, string label)
        {
            // Resolved once, here, so the entry and its state provider can never end up reading two
            // different objects. Transform.Find walks inactive children (GameObject.Find does not),
            // and the voice-chat panel is inactive whenever this runs.
            Transform control = (root != null) ? root.Find(relativePath) : null;
            if (control == null)
            {
                Debug.LogWarning("[LethalAccess] '" + relativePath + "' (" + label + ") was not found under the " +
                                 "launch-options container; falling back to path resolution, and its current " +
                                 "value will not be announced.");
            }

            // All three carry a SettingsOption whose textElement is the TMP the game itself keeps up
            // to date (verified live: ChooseDevice -> Text (TMP), SpeakerButton -> EnabledText,
            // TalkMode -> Text), so state is read from what the sighted player is looking at rather
            // than re-derived from IngamePlayerSettings. Looked up once, and complained about once,
            // because the state provider runs on every announcement.
            SettingsOption option = (control != null) ? control.GetComponent<SettingsOption>() : null;
            if (control != null && (option == null || option.textElement == null))
            {
                Debug.LogWarning("[LethalAccess] '" + control.name + "' (" + label + ") has no SettingsOption " +
                                 "with a textElement, so its current value will not be announced.");
                option = null;
            }

            UIEntry e = new UIEntry();
            e.Direct = control;
            e.RelativePath = relativePath;
            e.Label = label;
            // "button", not "toggle": SpeakerButton is a plain UnityEngine.UI.Button with a
            // SettingsOption that swaps a sprite (verified live - no Toggle component anywhere on
            // the three). Declaring "toggle" would make the registry's type validation log an error
            // and would promise the announcer a checked/unchecked state that does not exist.
            e.Type = "button";
            e.State = delegate { return SettingsOptionValue(option); };
            return e;
        }

        private static void RegisterCorruptedScreen(PreInitSceneScript s)
        {
            if (corruptedRegisteredFor == s)
            {
                return;
            }
            if (s.FileCorruptedPanel == null)
            {
                Debug.LogError("[LethalAccess] PreInitSceneScript.FileCorruptedPanel is not wired, so the " +
                               "corrupted-save screen cannot be described. If save loading fails, the player " +
                               "will get a silent screen whose only button deletes every save file.");
                return;
            }

            UIScreenDescriptor d = new UIScreenDescriptor();
            d.Id = CorruptedScreenId;

            d.ResolveRoot = delegate
            {
                PreInitSceneScript live = script;
                return (live != null && live.FileCorruptedPanel != null)
                    ? live.FileCorruptedPanel.transform
                    : null;
            };

            // The notice itself is the title, read live. It is the sentence that explains why the
            // game stopped and what the button is about to do.
            d.Title = CorruptedNotice;

            // The game has stopped and is asking a question; its one button deletes every save.
            d.Modal = true;

            // Match what the game does on the last line of EnableFileCorruptedScreen rather than
            // fight it: seeding anything else would be immediately overwritten. The notice has
            // already been spoken as the title, and both texts stay reachable with Up.
            d.FocusOnOpen = delegate
            {
                PreInitSceneScript live = script;
                return (live != null && live.FileCorruptedRestartButton != null)
                    ? live.FileCorruptedRestartButton.transform
                    : null;
            };

            UISection section = new UISection("Corrupted save files");

            UIEntry notice = new UIEntry();
            notice.RelativePath = "Panel/NotificationText";
            // No Label: the announcer reads the live TMP, which is the content the player needs.
            notice.Type = "text";
            section.Entries.Add(notice);

            UIEntry tip = new UIEntry();
            tip.RelativePath = "NotificationText (1)";
            tip.Type = "text";
            section.Entries.Add(tip);

            UIEntry confirm = new UIEntry();
            confirm.Direct = (s.FileCorruptedRestartButton != null)
                ? s.FileCorruptedRestartButton.transform
                : null;
            confirm.RelativePath = "Panel/Confirm";
            // The scene text is "[ Delete files and restart]". Spelled out here because this button
            // permanently deletes LCGeneralSaveData and all three save slots, and "Confirm" - which
            // is what the GameObject is called - would not tell anyone that.
            confirm.Label = "Delete files and restart";
            confirm.Type = "button";
            section.Entries.Add(confirm);

            d.Sections.Add(section);

            UIScreenRegistry.Register(d);
            corruptedRegisteredFor = s;
        }

        private static string CorruptedNotice()
        {
            PreInitSceneScript s = script;
            if (s == null || s.FileCorruptedDialoguePanel == null)
            {
                return null;
            }

            Transform t = s.FileCorruptedDialoguePanel.transform.Find("NotificationText");
            if (t == null)
            {
                Debug.LogWarning("[LethalAccess] The corrupted-save panel has no 'NotificationText' child, so " +
                                 "the reason the game stopped cannot be read out.");
                return null;
            }

            TextMeshProUGUI tmp = t.GetComponent<TextMeshProUGUI>();
            return (tmp != null) ? Flatten(tmp.text) : null;
        }

        #endregion

        #region Navigation

        /// <summary>
        /// Write an explicit up/down chain over whatever is visible right now, in authored order.
        ///
        /// SelectableNavigationPatch already routes up/down through this order for claimed controls,
        /// so this is redundant while VerticalNavigationOnly is on. It is not redundant when that
        /// setting is off, and this screen is where that matters most: the three mic controls are
        /// not in a column, and the corrupted-save Confirm ships Navigation.Mode.None, which is a
        /// dead end in both directions. Blanket navigation rewrites are avoided elsewhere because
        /// they destroy authored state with no clean restore; here the objects belong to a scene
        /// that is unloaded a fraction of a second after the player chooses a launch mode.
        /// </summary>
        private static void RebuildChain(string screenId)
        {
            if (screenId == ScreenId && registeredFor == null)
            {
                return;
            }
            if (screenId == CorruptedScreenId && corruptedRegisteredFor == null)
            {
                return;
            }

            List<Transform> order = UIScreenRegistry.VisibleFlatOrder(screenId);
            List<Selectable> chain = new List<Selectable>(order.Count);

            for (int i = 0; i < order.Count; i++)
            {
                if (order[i] == null)
                {
                    continue;
                }
                Selectable sel = order[i].GetComponent<Selectable>();
                if (sel != null)
                {
                    chain.Add(sel);
                }
            }

            if (chain.Count == 0)
            {
                Debug.LogWarning("[LethalAccess] Screen '" + screenId + "' has no visible controls to chain " +
                                 "together, so the arrow keys will do nothing on it.");
                return;
            }

            // No wrap: the top and bottom of a screen are dead ends, which is what a screen-reader
            // user expects from the arrow keys.
            NavigationEnforcer.BuildChain(chain, false);
        }

        /// <summary>
        /// The first control of a panel, in authored order rather than hierarchy order.
        /// </summary>
        private static Transform FirstVisibleUnder(Transform panel)
        {
            if (panel == null)
            {
                return null;
            }

            if (registeredFor != null)
            {
                List<Transform> order = UIScreenRegistry.VisibleFlatOrder(ScreenId);
                for (int i = 0; i < order.Count; i++)
                {
                    if (order[i] != null && order[i].IsChildOf(panel))
                    {
                        return order[i];
                    }
                }
            }

            // The screen model could not answer, so fall back to hierarchy order - which is DRAW
            // order in this game and does not track visual order. Worth saying out loud, because a
            // focus that lands in the wrong place is much harder to diagnose than one that does not
            // land at all.
            Selectable[] found = panel.GetComponentsInChildren<Selectable>(false);
            if (found.Length > 0)
            {
                Debug.LogWarning("[LethalAccess] No authored entry of the pre-init screen is visible under '" +
                                 panel.name + "'; focusing '" + found[0].name + "' from a hierarchy scan " +
                                 "instead. The screen descriptor is probably stale.");
                return found[0].transform;
            }

            return null;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// The value one of the mic controls is currently displaying: "ENABLED"/"DISABLED" for the
        /// microphone toggle, the device name for the input-device button, "Voice activation" or
        /// "Push to talk" for the mode button.
        ///
        /// IngamePlayerSettings.UpdateGameToMatchSettings writes all three of these before
        /// SetLaunchPanelsEnabled runs, so they are accurate by the time this screen is on show.
        /// </summary>
        private static string SettingsOptionValue(SettingsOption option)
        {
            // Null was already reported once at registration; the scene can also be torn down
            // between an announcement being scheduled and the state being read.
            if (option == null || option.textElement == null)
            {
                return null;
            }

            string text = option.textElement.text;
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            // The game formats two of the three as "<caption>: <value>" ("Current input device: \n
            // X", "MODE: Voice activation"). The caption repeats the label we already announce, so
            // drop it; a control whose text has no colon (the ENABLED/DISABLED toggle) is left whole.
            int colon = text.IndexOf(':');
            if (colon >= 0 && colon + 1 < text.Length)
            {
                text = text.Substring(colon + 1);
            }

            return Flatten(text);
        }

        /// <summary>
        /// The gamma slider as a percentage of its own range. The raw value runs -20 to 20 (verified
        /// live), which means nothing said out loud; "50 percent" does, and SpeechUtils already
        /// expands "%" to "percent".
        /// </summary>
        private static string GammaState()
        {
            PreInitSceneScript s = script;
            if (s == null || s.gammaSlider == null)
            {
                return null;
            }

            Slider slider = s.gammaSlider;
            float span = slider.maxValue - slider.minValue;
            if (span <= 0f)
            {
                // A zero-width range would make the percentage meaningless, so say the raw value.
                return slider.value.ToString("0");
            }

            return Mathf.RoundToInt((slider.value - slider.minValue) / span * 100f) + "%";
        }

        /// <summary>
        /// Collapse the scene's line breaks and padding into a single spoken line. Several of these
        /// TMPs are authored with embedded newlines and trailing blank lines.
        /// </summary>
        private static string Flatten(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            string[] parts = text.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }
            return string.Join(" ", parts);
        }

        #endregion
    }
}
