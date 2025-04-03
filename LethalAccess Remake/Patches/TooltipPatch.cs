using HarmonyLib;
using System;
using TMPro;
using UnityEngine;

namespace LethalAccess.Patches
{
    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.DisplayTip))]
    public static class HUDManager_DisplayTip_Patch
    {
        // Static field to store the time of the last speak operation
        private static DateTime lastSpeakTime = DateTime.MinValue;

        static void Postfix(string headerText, string bodyText, bool isWarning, bool useSave, string prefsKey)
        {
            // Check the time difference since the last speak operation
            if ((DateTime.Now - lastSpeakTime).TotalSeconds < 0.05)
            {
                // If less than 0.05 seconds have passed, do not speak again yet
                return;
            }

            // Combine the header and body text for speaking
            string fullTipMessage = $"{headerText}. {bodyText}";

            // Check if the fullTipMessage matches the specific message
            if (fullTipMessage == "Welcome!. Right-click to scan objects in the ship for info.")
            {
                // Replace it with the customized message
                Utilities.SpeakText("Welcome! Head over to your Terminal or land the ship to get started. Feel free to read your clipboard for more information.");
            }
            else
            {
                // For all other tips, speak the original combined message
                Utilities.SpeakText(fullTipMessage);
            }

            // Update the time of the last speak operation
            lastSpeakTime = DateTime.Now;
        }
    }

    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.openingDoorsSequence))]
    public static class StartOfRound_OpeningDoorsSequence_Patch
    {
        static void Postfix(StartOfRound __instance)
        {
            // Get the current level info directly from StartOfRound
            SelectableLevel currentLevel = __instance.currentLevel;

            if (currentLevel != null)
            {
                // Log the moon name we're using for debugging
                Debug.Log($"Opening doors on moon: {currentLevel.PlanetName}");

                // Check if HUD is available
                if (HUDManager.Instance != null &&
                    HUDManager.Instance.planetInfoHeaderText != null &&
                    HUDManager.Instance.planetInfoSummaryText != null &&
                    HUDManager.Instance.planetRiskLevelText != null)
                {
                    // Force update the HUD text if needed
                    if (!HUDManager.Instance.planetInfoHeaderText.text.Contains(currentLevel.PlanetName))
                    {
                        // Try to manually update the HUD text to match the current level
                        HUDManager.Instance.planetInfoHeaderText.text = "CELESTIAL BODY: " + currentLevel.PlanetName;
                        HUDManager.Instance.planetRiskLevelText.text = currentLevel.riskLevel;

                        // The summary text might be complex, so we'll leave it as is
                        Debug.Log("Updated HUD text to match current level: " + currentLevel.PlanetName);
                    }

                    // Use the HUD text for the announcement
                    string planetInfoMessage = $"{HUDManager.Instance.planetInfoHeaderText.text}: {HUDManager.Instance.planetInfoSummaryText.text}, Risk Level: {HUDManager.Instance.planetRiskLevelText.text}";
                    Utilities.SpeakText(planetInfoMessage);

                    Debug.Log("Announced planet info: " + planetInfoMessage);
                }
                else
                {
                    // If HUD elements aren't available, use direct level data
                    string fallbackMessage = $"CELESTIAL BODY: {currentLevel.PlanetName}, Risk Level: {currentLevel.riskLevel}";
                    Utilities.SpeakText(fallbackMessage);

                    Debug.Log("Used fallback moon announcement: " + fallbackMessage);
                }
            }
            else
            {
                Debug.LogWarning("Current level is null during opening doors sequence");
            }
        }
    }
}