using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LethalAccess
{
    /// <summary>
    /// Installs the server list driver. MenuManager.Start runs once per load of the menu scene,
    /// which is the earliest point at which serverListUIContainer and SteamLobbyManager both
    /// exist, and it runs again every time the player returns to the main menu.
    /// </summary>
    [HarmonyPatch(typeof(MenuManager), "Start")]
    internal static class ServerListInstallPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MenuManager __instance)
        {
            // The launch-options scene has its own MenuManager with no server list wired up;
            // MenuManager.Start itself returns immediately for it.
            if (__instance.isInitScene)
            {
                return;
            }

            ServerListAccessibility.Install(__instance);
        }
    }

    /// <summary>
    /// Pressing Join is silent from the press until either the game loads or an error notification
    /// appears - JoinLobbyAfterVerifying deactivates serverListUIContainer and SetLoadingScreen
    /// deactivates menuButtons, so the selection goes null and nothing announces anything.
    /// </summary>
    [HarmonyPatch(typeof(LobbySlot), nameof(LobbySlot.JoinButton))]
    internal static class LobbySlotJoinPatch
    {
        /// <summary>
        /// Deliberately a PREFIX. JoinButton delegates to JoinLobbyAfterVerifying, which sets
        /// GameNetworkManager.waitingForLobbyDataRefresh = true before returning - so a
        /// `!waitingForLobbyDataRefresh` guard in a POSTFIX is false on exactly the presses that
        /// DID something, and would suppress every successful announcement. Reading the flag
        /// before the call is the only place it still distinguishes an accepted press from a
        /// swallowed one.
        /// </summary>
        [HarmonyPrefix]
        public static void Prefix(LobbySlot __instance)
        {
            GameNetworkManager net = GameNetworkManager.Instance;
            if (net != null && net.waitingForLobbyDataRefresh)
            {
                // JoinButton's own early-out: the press does nothing at all. Saying so is the whole
                // point - the vanilla behaviour is a silent no-op.
                AnnouncementPolicy.Speak("Still loading servers.");
                return;
            }

            string name = __instance.LobbyName != null ? __instance.LobbyName.text : null;
            AnnouncementPolicy.Speak(string.IsNullOrEmpty(name) ? "Joining lobby." : "Joining " + name);
        }
    }

    /// <summary>
    /// Guards a vanilla NullReferenceException.
    ///
    /// This is the one patch in this file that works around a GAME bug rather than an accessibility
    /// gap: on the LobbyListItemChallenge prefab both modStateUnknownIcon and modStateTrueIcon are
    /// null, and SetModdedIcon dereferences them for Unknown and Modded. The throw happens inside
    /// SteamLobbyManager.loadLobbyListAndFilter, which abandons the coroutine and therefore every
    /// remaining lobby in the list - silently, from the player's point of view. It is unreachable
    /// with default filters only because the query pins dmods=f; turning on "With detected mods"
    /// makes it reachable.
    ///
    /// It changes vanilla behaviour: the game stops throwing here. Nothing else observes that
    /// exception, and the icon it was trying to enable does not exist on the prefab, so the only
    /// difference is that the rest of the list now loads.
    /// </summary>
    [HarmonyPatch(typeof(LobbySlot), nameof(LobbySlot.SetModdedIcon))]
    internal static class LobbySlotModdedIconPatch
    {
        private static readonly HashSet<string> reported = new HashSet<string>();

        [HarmonyPrefix]
        public static bool Prefix(LobbySlot __instance, ModdedState moddedState)
        {
            if (moddedState == ModdedState.Unknown && __instance.modStateUnknownIcon == null)
            {
                Report(__instance, "modStateUnknownIcon");
                return false;
            }

            if (moddedState == ModdedState.Modded && __instance.modStateTrueIcon == null)
            {
                Report(__instance, "modStateTrueIcon");
                return false;
            }

            return true;
        }

        private static void Report(LobbySlot slot, string fieldName)
        {
            // Once per prefab, not once per row: a full list is 20 rows.
            string key = slot.gameObject.name + "/" + fieldName;
            if (!reported.Add(key))
            {
                return;
            }

            Debug.LogWarning("[LethalAccess] LobbySlot." + fieldName + " is null on '" + slot.gameObject.name +
                             "'. Skipping the icon assignment; vanilla would throw here and abandon the rest of " +
                             "the lobby list. Modded status is still announced, read from the lobby's dmods key.");
        }
    }

    /// <summary>
    /// A refresh press that will be swallowed. RefreshServerListButton returns without acting while
    /// refreshServerListTimer is under half a second, and LoadServerList returns immediately while
    /// a lobby data refresh is already in flight; neither says anything, so a double press is
    /// indistinguishable from a broken button.
    /// </summary>
    [HarmonyPatch(typeof(SteamLobbyManager), nameof(SteamLobbyManager.RefreshServerListButton))]
    internal static class SteamLobbyManagerRefreshPatch
    {
        private static readonly FieldInfo RefreshTimerField =
            AccessTools.Field(typeof(SteamLobbyManager), "refreshServerListTimer");

        private static bool reportedMissingField;

        [HarmonyPrefix]
        public static void Prefix(SteamLobbyManager __instance)
        {
            GameNetworkManager net = GameNetworkManager.Instance;
            if (net != null && net.waitingForLobbyDataRefresh)
            {
                // RefreshServerListButton will call LoadServerList, which returns on this flag.
                AnnouncementPolicy.Speak("Still loading servers.");
                return;
            }

            if (RefreshTimerField == null)
            {
                if (!reportedMissingField)
                {
                    reportedMissingField = true;
                    Debug.LogError("[LethalAccess] SteamLobbyManager has no 'refreshServerListTimer' field, so a " +
                                   "refresh press that the game ignores cannot be reported. It will be silent.");
                }
                return;
            }

            float timer = (float)RefreshTimerField.GetValue(__instance);
            if (timer < 0.5f)
            {
                AnnouncementPolicy.Speak("Still refreshing.");
            }
        }
    }

    /// <summary>
    /// The two list filters are plain Buttons, so flipping one changes nothing the announcer would
    /// notice: the same object stays focused and its state lives on a sibling Image. Re-announcing
    /// the focused control is announcement rule 3 - the user hears the label, the type and the new
    /// state, not a free-floating "filter changed".
    /// </summary>
    [HarmonyPatch(typeof(SteamLobbyManager))]
    internal static class SteamLobbyManagerFilterPatch
    {
        [HarmonyPatch(nameof(SteamLobbyManager.ToggleSortWithModdedClients))]
        [HarmonyPostfix]
        public static void PostfixToggleModded()
        {
            AnnouncementPolicy.ReAnnounceFocused();
        }

        [HarmonyPatch(nameof(SteamLobbyManager.ToggleSortWithChallengeMoons))]
        [HarmonyPostfix]
        public static void PostfixToggleChallenge()
        {
            AnnouncementPolicy.ReAnnounceFocused();
        }
    }

    /// <summary>
    /// The loading screen is a dead end for the keyboard: SetLoadingScreen(true) deactivates both
    /// menuButtons and serverListUIContainer, so whatever was selected disappears and the screen
    /// itself has nothing focusable on it.
    /// </summary>
    [HarmonyPatch(typeof(MenuManager), nameof(MenuManager.SetLoadingScreen))]
    internal static class MenuManagerLoadingScreenPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MenuManager __instance, bool isLoading)
        {
            if (isLoading)
            {
                AnnounceLoading(__instance);
                return;
            }

            // Every non-loading path through SetLoadingScreen ends in DisplayMenuNotification,
            // which selects its own back button and is announced by MenuManagerNotificationPatch -
            // so there is normally nothing to do here. The exception is the launch-options scene,
            // where DisplayMenuNotification returns without doing anything: there, the selection
            // stays null and the keyboard is dead until something seeds it.
            EventSystem es = EventSystem.current;
            if (es == null || es.currentSelectedGameObject != null)
            {
                return;
            }

            SeedFirstMenuButton(__instance);
        }

        private static void AnnounceLoading(MenuManager menu)
        {
            // TODO(verify): MenuManager.loadingText is never written by Assembly-CSharp - it is
            // authored in the scene - while logText IS updated with connection progress
            // ("Connecting to host...", "Connection failed."). Which of the two the loading screen
            // actually displays could not be confirmed against the running game, because the
            // process was sitting in the launch-options scene where the main menu is not loaded.
            // Preferring loadingText and falling back to logText reads whichever is populated.
            string text = menu.loadingText != null ? menu.loadingText.text : null;

            if (string.IsNullOrEmpty(text) && menu.logText != null)
            {
                text = menu.logText.text;
            }

            AnnouncementPolicy.Speak(string.IsNullOrEmpty(text) ? "Loading." : text);
        }

        private static void SeedFirstMenuButton(MenuManager menu)
        {
            GameObject buttons = menu.menuButtons;
            if (buttons == null || !buttons.activeInHierarchy)
            {
                return;
            }

            Selectable[] candidates = buttons.GetComponentsInChildren<Selectable>(false);
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i].IsActive() && candidates[i].IsInteractable())
                {
                    // Sibling order, which is the only order available here; this is a recovery
                    // path for a dead keyboard, not a claim about reading order.
                    FocusManager.Seed(candidates[i].transform);
                    return;
                }
            }

            Debug.LogWarning("[LethalAccess] The loading screen closed with nothing selected and no interactable " +
                             "button under menuButtons to recover to; the keyboard has no focus.");
        }
    }
}
