using HarmonyLib;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Green.LethalAccessPlugin.Patches
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

    [HarmonyPatch(typeof(MenuManager))]
    public static class MenuManagerHostSetLobbyPublicPatch
    {
        // Patch for HostSetLobbyPublic
        [HarmonyPatch(nameof(MenuManager.HostSetLobbyPublic))]
        [HarmonyPostfix]
        public static void Postfix(bool setPublic)
        {
            if (setPublic)
            {
                Debug.Log("Set to Public");
                Utilities.SpeakText("Set to Public");
            }
            else
            {
                Debug.Log("Set to Private");
                Utilities.SpeakText("Set to Private");
            }
        }
    }
}