using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LethalAccess
{
    /// <summary>
    /// Hooks for the keybinds / "REMAP CONTROLS" screen.
    ///
    /// The build hook is on LoadKeybindsUI, NOT on OnEnable. LethalCompanyInputUtils prefixes
    /// OnEnable (building its own container and emptying remappableKeys) and its postfix calls
    /// LcInputActionApi.LoadIntoUI, which calls LoadKeybindsUI a second time. Hooking
    /// LoadKeybindsUI therefore covers both the vanilla and the InputUtils path, and always runs
    /// after the row objects of whichever UI is live already exist.
    ///
    /// The rebind hooks exist because starting a rebind is the single worst accessibility defect
    /// in the game: IngamePlayerSettings.RebindKey shows a plain inactive `waitingForInput` object
    /// (no event, nothing to announce), disables InputSystem.actions, and starts an interactive
    /// rebind that suppresses every matching input event - so the UI module sees nothing and
    /// LACore.CheckKeybinds, which polls Keyboard.current directly, sees nothing either. It never
    /// calls WithTimeout. Nothing in the game says a word about any of it.
    /// </summary>
    public static class KeybindsPanelPatch
    {
        #region Panel lifecycle

        /// <summary>
        /// Times every open, start of OnEnable (before InputUtils' prefix builds its container) to
        /// the end of it (after the second LoadKeybindsUI), so a slow open names itself in the log.
        /// </summary>
        [HarmonyPatch(typeof(KepRemapPanel), "OnEnable")]
        internal static class OpenTimingPatch
        {
            private static long startedAt;

            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static void Prefix()
            {
                startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix()
            {
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) * 1000.0 /
                            System.Diagnostics.Stopwatch.Frequency;
                Debug.Log("[LethalAccess] Keybinds screen opened in " + ms.ToString("F0") + " ms.");
            }
        }

        [HarmonyPatch(typeof(KepRemapPanel), "LoadKeybindsUI")]
        internal static class LoadKeybindsUIPatch
        {
            // Last, so this runs after any other mod that reshapes the panel in the same postfix
            // slot - notably LcInputActionApi.LoadIntoUI's repositioning pass.
            [HarmonyPostfix]
            [HarmonyPriority(Priority.Last)]
            public static void Postfix(KepRemapPanel __instance)
            {
                EnsureInputUtilsPatches();
                KeybindsAccessibility.Build(__instance);
            }
        }

        [HarmonyPatch(typeof(KepRemapPanel), "OnDisable")]
        internal static class OnDisablePatch
        {
            // Prefix: UnloadKeybindsUI destroys every slot, so the screen model has to be dropped
            // while its transforms are still valid.
            [HarmonyPrefix]
            public static void Prefix()
            {
                KeybindsAccessibility.Teardown();
            }
        }

        #endregion

        #region Vanilla rebind

        [HarmonyPatch(typeof(IngamePlayerSettings), "RebindKey")]
        internal static class RebindKeyPatch
        {
            /// <summary>
            /// Record the row BEFORE the body runs. RebindKey disables InputSystem.actions and
            /// shows waitingForInput before it touches WithCancelingThrough, and that line throws
            /// when the OpenMenu action has no control at the requested index - leaving the game
            /// with input disabled, the row stuck, and no postfix. The watchdog can only recover
            /// from that if it already knows which row to restore.
            /// </summary>
            [HarmonyPrefix]
            public static void Prefix(SettingsOption optionUI)
            {
                KeybindsAccessibility.NoteRebindStarting(optionUI);
            }

            [HarmonyPostfix]
            public static void Postfix(SettingsOption optionUI, bool gamepadRebinding)
            {
                string prompt = KeybindsAccessibility.BuildRebindPrompt(optionUI, gamepadRebinding);
                KeybindsAccessibility.NoteRebindStarted(optionUI, prompt);
                AnnouncementPolicy.Speak(prompt, true);
            }
        }

        [HarmonyPatch(typeof(IngamePlayerSettings), "CompleteRebind")]
        internal static class CompleteRebindPatch
        {
            [HarmonyPostfix]
            public static void Postfix(SettingsOption optionUI)
            {
                // CompleteRebind has already written the new key into currentlyUsedKeyText and
                // re-enabled it, so this reads the value the user just chose.
                string action = KeybindsAccessibility.ActionNameFor(optionUI);
                string binding = KeybindsAccessibility.CurrentBindingFor(optionUI);
                AnnouncementPolicy.Speak(action + " bound to " + binding);

                KeybindsAccessibility.ClearActiveRebind();
                KeybindsAccessibility.RefreshDynamicState();
            }
        }

        [HarmonyPatch(typeof(IngamePlayerSettings), "CancelRebind")]
        internal static class CancelRebindPatch
        {
            [HarmonyPostfix]
            public static void Postfix(SettingsOption optionUI)
            {
                // SettingsOption.OnDisable cancels with optionUI = null as the panel closes, and
                // ResetAllKeybinds cancels unconditionally. Neither is a user cancelling a rebind,
                // so neither should say "rebind cancelled".
                if (optionUI == null || KeybindsAccessibility.ActiveRebindOption != optionUI)
                {
                    return;
                }

                string action = KeybindsAccessibility.ActionNameFor(optionUI);
                string binding = KeybindsAccessibility.CurrentBindingFor(optionUI);
                AnnouncementPolicy.Speak("Rebind cancelled, " + action + " still " + binding);

                KeybindsAccessibility.ClearActiveRebind();
                KeybindsAccessibility.RefreshDynamicState();
            }
        }

        #endregion

        #region Reset all keybinds

        private static int resetAllSpokenFrame = -1;

        /// <summary>
        /// With InputUtils installed one press of "Set to defaults" runs both resets in the same
        /// frame: the game's own ResetAllKeybinds is a persistent listener on the button, which
        /// InputUtils' RemoveAllListeners cannot remove, and InputUtils adds its ResetAllToDefaults.
        /// Each used to say so.
        /// </summary>
        internal static void SpeakResetAllOnce()
        {
            if (resetAllSpokenFrame == Time.frameCount)
            {
                return;
            }
            resetAllSpokenFrame = Time.frameCount;
            AnnouncementPolicy.Speak("All keybinds reset to defaults");
        }

        [HarmonyPatch(typeof(IngamePlayerSettings), "ResetAllKeybinds")]
        internal static class ResetAllKeybindsPatch
        {
            private static string rememberedSection;
            private static int rememberedIndex;

            /// <summary>
            /// ResetAllKeybinds -> UpdateAllKeybindOptions -> ResetKeybindsUI -> UnloadKeybindsUI
            /// DESTROYS every slot, including whichever one is focused. The scene-authored
            /// KeybindsPanel/SetDefault.onClick calls Back.Select() first so that path survives -
            /// but SettingsPanel/SetToDefault carries the same handler with no such protection,
            /// and InputUtils strips the protective Select() with onClick.RemoveAllListeners().
            /// Unity has no focus recovery of its own, so without this the keyboard dies.
            /// </summary>
            [HarmonyPrefix]
            public static void Prefix()
            {
                rememberedSection = null;
                rememberedIndex = 0;

                EventSystem es = EventSystem.current;
                GameObject focused = es != null ? es.currentSelectedGameObject : null;
                if (focused == null)
                {
                    return;
                }

                // That protective Back.Select() has already run by now, so "focused" is Back even
                // though the user pressed SetDefault. Remember the button that was pressed.
                if (focused.name == "Back" && focused.transform.parent != null)
                {
                    Transform setDefault = focused.transform.parent.Find("SetDefault");
                    if (setDefault != null && setDefault.gameObject.activeInHierarchy)
                    {
                        focused = setDefault.gameObject;
                    }
                }

                int index;
                int count;
                string section;
                if (UIScreenRegistry.TryGetPosition(focused, out index, out count, out section))
                {
                    rememberedSection = section;
                    rememberedIndex = index;
                }
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                SpeakResetAllOnce();

                if (rememberedSection != null && !KeybindsAccessibility.TryFocusPosition(rememberedSection, rememberedIndex))
                {
                    // Not an error: the panel may not be open at all (SettingsPanel/SetToDefault
                    // reaches here from the settings screen). FocusManager's watchdog re-seeds
                    // whichever registered screen is actually visible.
                    Debug.Log("[LethalAccess] Keybinds were reset but position " + rememberedIndex + " of section '" +
                              rememberedSection + "' could not be re-focused; leaving recovery to FocusManager.");
                }

                rememberedSection = null;
                rememberedIndex = 0;
            }
        }

        #endregion

        #region LethalCompanyInputUtils rebind hooks

        // InputUtils' own rebind is not a hard lock - RebindButton.Update cancels it after
        // `timeout` seconds and its OnCancel restores the UI - but it is completely silent, and it
        // suppresses input for the whole of that window. These four hooks are attached by
        // reflection so the mod still loads when InputUtils is absent.

        private static Harmony inputUtilsHarmony;
        private static bool inputUtilsPatchesTried;

        // RebindButton.ResetAllToDefaults calls ResetToDefault on EVERY instance, which without
        // this flag would produce one utterance per bind on the whole screen.
        private static bool bulkResetInProgress;

        /// <summary>
        /// Called once from LACore.Awake. It used to run on the first open of the keybinds screen,
        /// where building these patches (and finding the types by a whole-domain scan) was most of
        /// the pause before the screen appeared. Still safe to call again; it only acts once.
        /// </summary>
        internal static void EnsureInputUtilsPatches()
        {
            if (inputUtilsPatchesTried)
            {
                return;
            }
            inputUtilsPatchesTried = true;

            if (!InputUtilsBridge.Available || InputUtilsBridge.RebindButton == null)
            {
                // Not installed. The vanilla hooks above cover the shipped UI on their own.
                return;
            }

            Type rebindButton = InputUtilsBridge.RebindButton;
            inputUtilsHarmony = new Harmony("LethalAccess.Keybinds.InputUtils");

            PatchInputUtils(rebindButton, "StartRebinding", nameof(PrefixStartRebinding), nameof(PostfixStartRebinding),
                            nameof(FinalizerStartRebinding));
            PatchInputUtils(rebindButton, "FinishRebinding", nameof(PrefixFinishRebinding), nameof(PostfixFinishRebinding));
            PatchInputUtils(rebindButton, "ResetToDefault", null, nameof(PostfixResetToDefault));
            PatchInputUtils(rebindButton, "RemoveBind", null, nameof(PostfixRemoveBind));
            PatchInputUtils(rebindButton, "ResetAllToDefaults",
                            nameof(PrefixResetAllToDefaults), nameof(PostfixResetAllToDefaults));
            PatchInputUtils(rebindButton, "UpdateState", null, nameof(PostfixUpdateState));
            PatchInputUtils(InputUtilsBridge.RemapContainer, "HideHighestLayer", nameof(PrefixHideHighestLayer), null);
        }

        private static void PatchInputUtils(Type type, string methodName, string prefixName, string postfixName,
                                            string finalizerName = null)
        {
            MethodInfo target = AccessTools.Method(type, methodName);
            if (target == null)
            {
                Debug.LogError("[LethalAccess] LethalCompanyInputUtils." + type.Name + "." + methodName +
                               " was not found, so that step of rebinding will be silent. The installed " +
                               "InputUtils version differs from the one this mod was written against.");
                return;
            }

            HarmonyMethod prefix = prefixName == null
                ? null
                : new HarmonyMethod(AccessTools.Method(typeof(KeybindsPanelPatch), prefixName));
            HarmonyMethod postfix = postfixName == null
                ? null
                : new HarmonyMethod(AccessTools.Method(typeof(KeybindsPanelPatch), postfixName));
            HarmonyMethod finalizer = finalizerName == null
                ? null
                : new HarmonyMethod(AccessTools.Method(typeof(KeybindsPanelPatch), finalizerName));

            // Narrow, reported, non-silent: this runs inside KepRemapPanel.LoadKeybindsUI's
            // postfix, so an exception from patching a THIRD-PARTY method would propagate into the
            // game's own OnEnable and take the whole panel - including the screen registration
            // that makes it navigable at all - down with it. One announcement going missing is
            // recoverable; a keybinds panel that throws on open is not. The failure is logged in
            // full, and the other hooks still get attached.
            try
            {
                inputUtilsHarmony.Patch(target, prefix: prefix, postfix: postfix, finalizer: finalizer);
            }
            catch (Exception e)
            {
                Debug.LogError("[LethalAccess] Failed to hook LethalCompanyInputUtils." + type.Name + "." +
                               methodName + "; that step of rebinding will be announced by nothing. " + e);
            }
        }

        // __instance is declared as MonoBehaviour rather than the real type so this file needs no
        // reference to the InputUtils assembly; Harmony accepts any base type of the declaring
        // class here.

        public static void PrefixStartRebinding(MonoBehaviour __instance)
        {
            KeybindsAccessibility.PrepareRebindTarget(__instance);
        }

        // Returning null swallows the exception, which is the point: the failure is reported to the
        // user and the log here, and letting it propagate would only add a stack trace to a Unity
        // button callback that nobody is reading.
        public static Exception FinalizerStartRebinding(MonoBehaviour __instance, Exception __exception)
        {
            if (__exception != null)
            {
                KeybindsAccessibility.ReportRebindFailure(__instance, __exception);
            }
            return null;
        }

        public static void PostfixStartRebinding(MonoBehaviour __instance)
        {
            KeybindsAccessibility.AnnounceInputUtilsRebindStart(__instance);
        }

        public static void PrefixFinishRebinding(MonoBehaviour __instance)
        {
            if (bulkResetInProgress)
            {
                return;
            }
            KeybindsAccessibility.NoteInputUtilsRebindFinishing(__instance);
        }

        public static void PostfixFinishRebinding(MonoBehaviour __instance)
        {
            if (bulkResetInProgress)
            {
                return;
            }
            KeybindsAccessibility.AnnounceInputUtilsRebindEnd(__instance);
        }

        public static void PostfixResetToDefault(MonoBehaviour __instance)
        {
            if (bulkResetInProgress)
            {
                return;
            }
            KeybindsAccessibility.AnnounceInputUtilsBindingChanged(__instance, "reset to default", true);
        }

        public static void PostfixRemoveBind(MonoBehaviour __instance)
        {
            if (bulkResetInProgress)
            {
                return;
            }
            KeybindsAccessibility.AnnounceInputUtilsBindingChanged(__instance, "removed", false);
        }

        public static void PrefixResetAllToDefaults()
        {
            bulkResetInProgress = true;
            // InputUtils resets only the rows that are switched on, and a closed category's rows
            // are switched off.
            KeybindsAccessibility.ExpandAllForBulkReset();
        }

        public static void PostfixResetAllToDefaults()
        {
            KeybindsAccessibility.RestoreCollapseAfterBulkReset();
            bulkResetInProgress = false;
            SpeakResetAllOnce();
            // InputUtils' reset does not rebuild the UI, so the rows survive - only their
            // bindings, and the visibility of the Reset buttons, have changed.
            KeybindsAccessibility.RefreshDynamicState();
            AnnouncementPolicy.ReAnnounceFocused();
        }

        // UpdateState runs after every change to a binding, whatever made it (a rebind, a reset, the
        // Global/Local switch, a device change), which makes it the one place to learn that the
        // cached conflict warnings are stale.
        public static void PostfixUpdateState()
        {
            KeybindsAccessibility.InvalidateConflicts();
        }

        // In game, Escape on the remap screen reaches InputUtils' CloseContainerLayer, which closes
        // the whole panel. An open category closes first.
        public static bool PrefixHideHighestLayer()
        {
            return !KeybindsAccessibility.HandleEscape();
        }

        #endregion

        #region In-game Escape

        /// <summary>
        /// In game, Escape is the OpenMenu action and PlayerControllerB acts on it: with unsaved
        /// changes it swaps the remap screen for the confirm-changes panel, otherwise it closes the
        /// menu. On the remap screen with a category open, it closes the category instead.
        /// </summary>
        [HarmonyPatch(typeof(GameNetcodeStuff.PlayerControllerB), "OpenMenu_performed")]
        internal static class OpenMenuPerformedPatch
        {
            [HarmonyPrefix]
            [HarmonyPriority(Priority.First)]
            public static bool Prefix(UnityEngine.InputSystem.InputAction.CallbackContext context)
            {
                if (!context.performed)
                {
                    return true;
                }

                // An open option list (Motion Blur, Control priority) closes on the UI module's
                // own Cancel. Letting the game act too closed the whole settings panel under it
                // and left nothing selected.
                EventSystem es = EventSystem.current;
                GameObject selected = es != null ? es.currentSelectedGameObject : null;
                TMPro.TMP_Dropdown openList = selected != null ? selected.GetComponentInParent<TMPro.TMP_Dropdown>() : null;
                if (openList != null && openList.IsExpanded)
                {
                    return false;
                }

                return !KeybindsAccessibility.HandleEscape();
            }
        }

        #endregion
    }
}
