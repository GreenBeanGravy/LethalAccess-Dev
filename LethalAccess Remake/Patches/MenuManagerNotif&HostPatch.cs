using HarmonyLib;
using UnityEngine;

namespace LethalAccess.Patches
{
    [HarmonyPatch(typeof(MenuManager))]
    public static class MenuManagerNotificationPatch
    {
        // Patch for DisplayMenuNotification
        [HarmonyPatch(nameof(MenuManager.DisplayMenuNotification))]
        [HarmonyPostfix]
        public static void Postfix(MenuManager __instance, string notificationText, string buttonText)
        {
            if (__instance.menuNotificationText != null)
            {
                string readText = __instance.menuNotificationText.text;
                Debug.Log($"Menu Notification: {readText}");
                Utilities.SpeakText(readText);
            }
        }
    }

    /// <summary>
    /// "Set to Private" used to be announced merely from opening the host menu.
    ///
    /// Three separate faults, all fixed here:
    ///  * ClickHostButton ends with HostSetLobbyPublic(hostSettings_LobbyPublic) purely to refresh
    ///    the two Animators (MenuManager.ClickHostButton). The postfix cannot tell that refresh from a
    ///    button press on its own, so HostMenuAccessPatch stamps the frame ClickHostButton runs on
    ///    and this postfix ignores that one call.
    ///  * In LAN mode MenuManager.HostSetLobbyPublic returns early WITHOUT assigning
    ///    hostSettings_LobbyPublic, yet the old postfix still announced the argument - it reported
    ///    a state change that never happened.
    ///  * The sentence explaining what public and private mean is written into
    ///    privatePublicDescription (MenuManager.HostSetLobbyPublic), a TMP nothing focuses, so it was
    ///    never read out.
    /// </summary>
    [HarmonyPatch(typeof(MenuManager))]
    public static class MenuManagerHostSetLobbyPublicPatch
    {
        // Null until the first observed value. Used only to tell whether ServerTagInputField is
        // about to appear or disappear - the announcement itself is NOT gated on a change, because
        // pressing "Public" while already public must still confirm that the press registered.
        private static bool? lastKnownPublic;

        [HarmonyPatch(nameof(MenuManager.HostSetLobbyPublic))]
        [HarmonyPostfix]
        public static void Postfix(MenuManager __instance, bool setPublic)
        {
            if (GameNetworkManager.Instance != null && GameNetworkManager.Instance.disableSteam)
            {
                // The method bailed before assigning hostSettings_LobbyPublic; nothing changed and
                // LAN mode has no public/private choice to report.
                return;
            }

            bool changed = !lastKnownPublic.HasValue || lastKnownPublic.Value != setPublic;
            lastKnownPublic = setPublic;

            if (Time.frameCount == HostMenuAccess.SuppressLobbyPublicFrame)
            {
                // Opening the host menu. The Public/Private buttons announce their own state when
                // focus reaches them, so nothing needs to be said here.
                return;
            }

            string message = setPublic ? "Set to Public." : "Set to Private.";

            if (__instance.privatePublicDescription != null &&
                !string.IsNullOrEmpty(__instance.privatePublicDescription.text))
            {
                message += " " + __instance.privatePublicDescription.text;
            }

            if (changed)
            {
                // The tag field is not merely cosmetic - it joins or leaves the up/down order, so
                // silence here would leave the user with a chain that changed length unannounced.
                message += setPublic
                    ? " Server tag field added below the server name."
                    : " Server tag field removed.";
            }

            AnnouncementPolicy.Speak(message);

            // MenuManager.Update - not this method - toggles ServerTagInputField from
            // hostSettings_LobbyPublic (MenuManager.Update), so the chain can only be rebuilt
            // to match one frame from now.
            HostMenuAccess.ScheduleChainRebuild(__instance);
        }
    }
}
