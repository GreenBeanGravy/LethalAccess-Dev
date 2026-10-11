using System;
using HarmonyLib;

namespace LethalAccess.Patches
{
    /// <summary>
    /// The announcement policy applied to the settings subsystem.
    ///
    /// Every utterance in here now hangs off a method the USER invoked. The previous version
    /// announced from storage plumbing, which is why "Settings saved." fired at times the user had
    /// done nothing settings-related: <see cref="IngamePlayerSettings.SaveSettingsToPrefs"/> has
    /// exactly two callers, and NEITHER of them is a settings save:
    ///   IngamePlayerSettings.ResetSettingsToDefault: a destructive WIPE, announced as a save;
    ///   IngamePlayerSettings.SaveChangedSettings, which PreInitSceneScript.ChooseLaunchOption
    ///   calls once at boot, so picking Online/LAN on the launch screen said "Settings saved."
    ///   (live-confirmed on this build, attributed to the old PostfixSaveSettingsToPrefs).
    /// A postfix on SaveSettingsToPrefs also cannot report failure: it swallows its own exception at
    /// IngamePlayerSettings.SaveSettingsToPrefs, puts up the corrupted-file screen, and returns normally.
    /// Success and failure are only distinguishable through `encounteredErrorDuringSave`, hence the
    /// postfix on SaveChangedSettings instead.
    /// </summary>
    internal class SettingsUIAccessPatch
    {
        [HarmonyPatch(typeof(IngamePlayerSettings))]
        public static class IngamePlayerSettingsPatch
        {
            /// <summary>
            /// The one place a settings save is actually a settings save. Confirm changes ->
            /// SettingsOption.ConfirmSettings -> here.
            /// </summary>
            [HarmonyPatch(nameof(IngamePlayerSettings.SaveChangedSettings))]
            [HarmonyPostfix]
            public static void PostfixSaveChangedSettings(IngamePlayerSettings __instance)
            {
                // encounteredErrorDuringSave is set by DisplaySaveFileError and is never
                // cleared, so once the save file is unwritable this correctly keeps reporting
                // failure instead of claiming a save that did not happen.
                AnnouncementPolicy.Speak(__instance.encounteredErrorDuringSave
                    ? "Settings could not be saved."
                    : "Settings saved.");
            }

            /// <summary>
            /// "Reset all to default" wipes every setting AND writes it to disk immediately; there
            /// is no confirmation and no undo. It used to be announced as "Settings saved.", which
            /// is the opposite of a warning.
            /// </summary>
            [HarmonyPatch(nameof(IngamePlayerSettings.ResetSettingsToDefault))]
            [HarmonyPostfix]
            public static void PostfixResetSettingsToDefault()
            {
                AnnouncementPolicy.Speak("All settings reset to defaults.");
            }

            /// <summary>
            /// QuickMenuManager.CloseQuickMenu calls DiscardChangedSettings UNCONDITIONALLY
            /// whenever the settings panel is open, so every ESC out of Settings used to claim
            /// changes had been thrown away. DiscardChangedSettings clears the flag on its first
            /// line, so the only place the truth still exists is before the call.
            /// </summary>
            [HarmonyPatch(nameof(IngamePlayerSettings.DiscardChangedSettings))]
            [HarmonyPrefix]
            public static void PrefixDiscardChangedSettings(IngamePlayerSettings __instance, out bool __state)
            {
                __state = __instance.changesNotApplied;
            }

            [HarmonyPatch(nameof(IngamePlayerSettings.DiscardChangedSettings))]
            [HarmonyPostfix]
            public static void PostfixDiscardChangedSettings(bool __state)
            {
                if (__state)
                {
                    AnnouncementPolicy.Speak("All unsaved changes have been discarded.");
                }
            }

            /// <summary>
            /// The single funnel every control on the settings panel goes through
            /// (SettingsOption.SetSettingsOptionInt/Float -> IngamePlayerSettings.SetOption).
            /// Re-announcing the focused control here replaces three hand-written sentences that
            /// covered two sliders and the microphone and left the five dropdowns and six
            /// sprite-toggles completely silent, and it obeys rule 3: the user hears what the
            /// control IS and what it is NOW, not a free-floating "volume changed".
            ///
            /// 3.2: this goes through UIAccessibilityManager.RequestReAnnounce rather than
            /// AnnouncementPolicy.ReAnnounceFocused because the same funnel also carries the three
            /// SLIDERS. SettingsOption.SetSettingsOptionFloat is wired to Slider.onValueChanged
            /// (SettingsOption.SetSettingsOptionFloat), and Slider.OnMove calls Set() on every repeat of a held
            /// arrow (Slider.OnMove; InputSystemUIInputModule repeats at ~10/s after a 0.5 s
            /// delay) and on every frame of a mouse drag. Each of those cleared the announcer's
            /// memo and produced a fresh ~2 s "Master volume, slider, 55 percent, 4 of 4" on the
            /// next frame; SpeechQueue drops nothing, so the backlog was unbounded and the screen
            /// reader ended up describing values the user had long since passed.
            ///
            /// RequestReAnnounce coalesces the re-announcement for CONTINUOUS controls only. The
            /// five dropdowns, the six sprite-toggles and the mic buttons are discrete and still
            /// announce immediately, once per press, exactly as before.
            /// </summary>
            [HarmonyPatch(nameof(IngamePlayerSettings.SetOption))]
            [HarmonyPostfix]
            public static void PostfixSetOption()
            {
                UIAccessibilityManager.RequestReAnnounce();
            }

            [HarmonyPatch(typeof(InitializeGame))]
            public static class InitializeGamePatch
            {
                [HarmonyPatch("Start")]
                [HarmonyPostfix]
                public static void PostfixStart()
                {
                    // Kept as the mod's liveness cue: it is attributable to the user launching the
                    // game. Routed through AnnouncementPolicy so there is one speech front door
                    //; the side effect is that it now respects the audio-cues config toggle,
                    // where Utilities.SpeakText ignored it.
                    AnnouncementPolicy.Speak("Game Initialized.");
                }
            }
        }

        /// <summary>
        /// The boot-time save. ChooseLaunchOption calls SetPlayerFinishedLaunchOptions and
        /// SaveChangedSettings (PreInitSceneScript.ChooseLaunchOption) before loading the next scene, so
        /// picking a launch mode used to be announced as a settings save. Suppress the call and
        /// announce the choice the user actually made.
        /// </summary>
        [HarmonyPatch(typeof(PreInitSceneScript))]
        public static class PreInitSceneLaunchOptionPatch
        {
            public sealed class LaunchState
            {
                public IDisposable Suppression;
                public bool AlreadyChosen;
            }

            [HarmonyPatch(nameof(PreInitSceneScript.ChooseLaunchOption))]
            [HarmonyPrefix]
            public static void PrefixChooseLaunchOption(bool ___choseLaunchOption, out LaunchState __state)
            {
                __state = new LaunchState
                {
                    // The method no-ops when it has already run once (PreInitSceneScript.ChooseLaunchOption);
                    // announcing a mode change that did not happen would be a lie.
                    AlreadyChosen = ___choseLaunchOption,
                    Suppression = AnnouncementPolicy.Suppress()
                };
            }

            /// <summary>
            /// A finalizer rather than a postfix so the suppression scope is released even if the
            /// save throws: a scope left open would silence the mod, which is the worst failure
            /// this file can have. The announcement is made here too, AFTER the release, because a
            /// postfix would still be inside the scope and would be swallowed.
            /// </summary>
            [HarmonyPatch(nameof(PreInitSceneScript.ChooseLaunchOption))]
            [HarmonyFinalizer]
            public static void FinalizerChooseLaunchOption(bool online, LaunchState __state, Exception __exception)
            {
                if (__state == null)
                {
                    // The prefix did not run (it threw, or another patch skipped it); there is
                    // nothing to release and nothing we can honestly say about the choice.
                    return;
                }

                if (__state.Suppression != null)
                {
                    __state.Suppression.Dispose();
                    __state.Suppression = null;
                }

                if (__exception != null || __state.AlreadyChosen)
                {
                    return;
                }

                AnnouncementPolicy.Speak(online ? "Online mode selected." : "LAN mode selected.");
            }
        }

        // The second Terminal.LoadNewNode postfix used to live here. It spoke
        // __instance.currentText in full, unconditionally, while TerminalAccessPatch
        // installs its own prefix+postfix pair on the same method that speaks only the part of the
        // buffer the user has not heard yet. Harmony applied both and the order between two
        // postfixes on one method is undefined, so every terminal page was read twice: once whole,
        // once as the delta, in whichever order. Deleted rather than moved: TerminalAccessPatch
        // patched LoadNewNode and nothing else was patched here, and its postfix already covers
        // every case this one did (clearPreviousText and a null node both fall back to the whole
        // buffer, an empty buffer is skipped, and an empty delta falls back to node.displayText),
        // so nothing was lost. It also reaches the queue through AnnouncementPolicy.Speak, which
        // honours suppression scopes and the audio-cues toggle that Utilities.SpeakText predates.
    }
}
