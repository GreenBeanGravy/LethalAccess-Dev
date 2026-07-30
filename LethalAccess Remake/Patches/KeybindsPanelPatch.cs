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
                AnnouncementPolicy.Speak("All keybinds reset to defaults");

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

        internal static void EnsureInputUtilsPatches()
        {
            if (inputUtilsPatchesTried)
            {
                return;
            }
            inputUtilsPatchesTried = true;

            Type rebindButton = AccessTools.TypeByName("LethalCompanyInputUtils.Components.RebindButton");
            if (rebindButton == null)
            {
                // Not installed. The vanilla hooks above cover the shipped UI on their own.
                return;
            }

            inputUtilsHarmony = new Harmony("LethalAccess.Keybinds.InputUtils");

            PatchInputUtils(rebindButton, "StartRebinding", null, nameof(PostfixStartRebinding));
            PatchInputUtils(rebindButton, "FinishRebinding", null, nameof(PostfixFinishRebinding));
            PatchInputUtils(rebindButton, "ResetToDefault", null, nameof(PostfixResetToDefault));
            PatchInputUtils(rebindButton, "RemoveBind", null, nameof(PostfixRemoveBind));
            PatchInputUtils(rebindButton, "ResetAllToDefaults",
                            nameof(PrefixResetAllToDefaults), nameof(PostfixResetAllToDefaults));
        }

        private static void PatchInputUtils(Type type, string methodName, string prefixName, string postfixName)
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

            // Narrow, reported, non-silent: this runs inside KepRemapPanel.LoadKeybindsUI's
            // postfix, so an exception from patching a THIRD-PARTY method would propagate into the
            // game's own OnEnable and take the whole panel - including the screen registration
            // that makes it navigable at all - down with it. One announcement going missing is
            // recoverable; a keybinds panel that throws on open is not. The failure is logged in
            // full, and the other hooks still get attached.
            try
            {
                inputUtilsHarmony.Patch(target, prefix, postfix);
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

        public static void PostfixStartRebinding(MonoBehaviour __instance)
        {
            KeybindsAccessibility.AnnounceInputUtilsRebindStart(__instance);
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
        }

        public static void PostfixResetAllToDefaults()
        {
            bulkResetInProgress = false;
            AnnouncementPolicy.Speak("All keybinds reset to defaults");
            // InputUtils' reset does not rebuild the UI, so the rows survive - only their
            // bindings, and the visibility of the Reset buttons, have changed.
            KeybindsAccessibility.RefreshDynamicState();
            AnnouncementPolicy.ReAnnounceFocused();
        }

        #endregion
    }
}
