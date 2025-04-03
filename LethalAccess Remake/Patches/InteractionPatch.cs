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

        // Method to handle the speaking functionality
        private static void SpeakInteraction(string text)
        {
            // Check specific texts and decide whether to speak them
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (text == "Climb : [E]") // Ignore "Climb : [E]"
                {
                    // Do nothing for "Climb : [E]"
                }
                else if (text == "Use door : [E]") // Customize or ignore "Use door : [E]"
                {
                    // If you want to customize the message:
                    // Utilities.SpeakText("Custom message here.");

                    // Or simply do nothing if you want to ignore it.
                }
                else // Speak all other texts
                {
                    Utilities.SpeakText(text);
                }
            }
        }
    }
}