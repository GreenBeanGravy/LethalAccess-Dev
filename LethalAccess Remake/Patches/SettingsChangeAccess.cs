using HarmonyLib;
using UnityEngine;

namespace Green.LethalAccessPlugin.Patches
{
    internal class SettingsUIAccessPatch
    {
        public static bool micEnabled; // Mic status
        public static string CURRENT_INPUT_DEVICE; // Current input device
        public static bool pushToTalkEnabled;

        [HarmonyPatch(typeof(IngamePlayerSettings))]
        public static class IngamePlayerSettingsPatch
        {
            public static void UpdateMicStatusAndDevice(bool enabled, string device)
            {
                micEnabled = enabled;
                CURRENT_INPUT_DEVICE = device;
            }

            public static void UpdatePushToTalkStatus(bool enabled)
            {
                pushToTalkEnabled = enabled;
            }

            public static void FetchInitialSettings()
            {
                // Ensure IngamePlayerSettings.Instance is properly referenced
                if (IngamePlayerSettings.Instance != null)
                {
                    micEnabled = IngamePlayerSettings.Instance.unsavedSettings.micEnabled;
                    pushToTalkEnabled = IngamePlayerSettings.Instance.unsavedSettings.pushToTalk;
                    CURRENT_INPUT_DEVICE = IngamePlayerSettings.Instance.unsavedSettings.micDevice;
                }
                else
                {
                    Debug.LogError("IngamePlayerSettings.Instance is null. Default settings could not be fetched.");
                }
            }

            // Improved patch for saving settings
            [HarmonyPatch(nameof(IngamePlayerSettings.SaveSettingsToPrefs))]
            [HarmonyPostfix]
            public static void PostfixSaveSettingsToPrefs(IngamePlayerSettings __instance)
            {
                string onlineModeStatus = __instance.settings.startInOnlineMode ? "Online" : "Offline";
                string invertYAxisStatus = __instance.settings.invertYAxis ? "Inverted" : "Normal";
                Utilities.SpeakText($"Settings saved.");
            }

            // Improved patch for discarding changes
            [HarmonyPatch(nameof(IngamePlayerSettings.DiscardChangedSettings))]
            [HarmonyPostfix]
            public static void PostfixDiscardChangedSettings()
            {
                Utilities.SpeakText("All unsaved changes have been discarded.");
            }

            // Improved patch for changing master volume
            [HarmonyPatch(nameof(IngamePlayerSettings.ChangeMasterVolume))]
            [HarmonyPostfix]
            public static void PostfixChangeMasterVolume(int setTo)
            {
                Utilities.SpeakText($"{setTo}% Master Volume.");
            }

            // Improved patch for changing look sensitivity
            [HarmonyPatch(nameof(IngamePlayerSettings.ChangeLookSens))]
            [HarmonyPostfix]
            public static void PostfixChangeLookSens(int setTo)
            {
                Utilities.SpeakText($"{setTo} sensitivity.");
            }

            // Improved patch for microphone enabled setting
            [HarmonyPatch(nameof(IngamePlayerSettings.SetMicrophoneEnabled))]
            [HarmonyPostfix]
            public static void PostfixSetMicrophoneEnabled(IngamePlayerSettings __instance)
            {
                string micStatus = __instance.unsavedSettings.micEnabled ? "enabled" : "disabled";
                Utilities.SpeakText($"Microphone is now {micStatus}.");

                // Update mic status in MenuManagerPatch
                UpdateMicStatusAndDevice(__instance.unsavedSettings.micEnabled, __instance.unsavedSettings.micDevice);
            }

            [HarmonyPatch(nameof(IngamePlayerSettings.SetMicPushToTalk))]
            [HarmonyPostfix]
            public static void PostfixSetMicPushToTalk(IngamePlayerSettings __instance)
            {
                string pushToTalkStatus = __instance.unsavedSettings.pushToTalk ? "Push to talk" : "Voice activation";
                Utilities.SpeakText($"Microphone mode set to {pushToTalkStatus}.");

                // Update push to talk status in MenuManagerPatch
                UpdatePushToTalkStatus(__instance.unsavedSettings.pushToTalk);
            }


            // Patch for SwitchMicrophoneSetting
            [HarmonyPatch(nameof(IngamePlayerSettings.SwitchMicrophoneSetting))]
            [HarmonyPostfix]
            public static void PostfixSwitchMicrophoneSetting(IngamePlayerSettings __instance)
            {
                string newMic = __instance.unsavedSettings.micDevice;
                Utilities.SpeakText($"Microphone switched to: {newMic}");

                // Update mic status and current input device in MenuManagerPatch
                UpdateMicStatusAndDevice(__instance.unsavedSettings.micEnabled, newMic);
            }

            // ... additional patches and classes as needed 

            // New Harmony Patch for InitializeGame
            [HarmonyPatch(typeof(InitializeGame))]
            public static class InitializeGamePatch
            {
                [HarmonyPatch("Start")]
                [HarmonyPostfix]
                public static void PostfixStart()
                {
                    Utilities.SpeakText("Game Initialized.");
                    FetchInitialSettings();
                }
            }
        }

        // Existing Patch for Terminal's LoadNewNode method
        [HarmonyPatch(typeof(Terminal))]
        public static class TerminalPatch
        {
            [HarmonyPatch(nameof(Terminal.LoadNewNode))]
            [HarmonyPostfix]
            public static void PostfixLoadNewNode(Terminal __instance)
            {
                if (!string.IsNullOrEmpty(__instance.currentText))
                {
                    Utilities.SpeakText(__instance.currentText.Trim());
                }
            }
        }
    }
}