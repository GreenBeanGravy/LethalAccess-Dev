using GameNetcodeStuff;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace LethalAccess.Patches
{
    [HarmonyPatch(typeof(PlayerControllerB), "Update")]
    public static class PlayerControllerBUpdatePatch
    {
        private static string lastInteractText = ""; // To keep track of the last interact text for comparison
        private static TMP_Text interactTextComponent = null; // Cache the TMP_Text component

        static void Postfix()
        {
            // Check if the interactTextComponent is null or was destroyed (e.g., changing levels/scenes)
            if (interactTextComponent == null || interactTextComponent.gameObject == null)
            {
                // Attempt to find the InteractText object again
                GameObject interactTextObj = GameObject.Find("Systems/UI/Canvas/PlayerCursor/InteractText");
                if (interactTextObj != null) // Make sure we've found the object
                {
                    interactTextComponent = interactTextObj.GetComponent<TMP_Text>();
                }
            }

            // Now, if we have a valid interactTextComponent, check its content
            if (interactTextComponent != null && interactTextComponent.text != lastInteractText)
            {
                // The text has changed, handle the new text
                SpeakInteraction(interactTextComponent.text);

                // Update the last interact text to the new text
                lastInteractText = interactTextComponent.text;
            }
        }

        private static void SpeakInteraction(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            // Auto pathfinding turns the camera itself and walks past doors it opens on its own,
            // so the door prompt it sweeps across is noise. Everyone else hears it.
            if (text.StartsWith("Use door") && Pathfinder.AutoWalkActive)
                return;
            Utilities.SpeakText(text);
        }
    }
}