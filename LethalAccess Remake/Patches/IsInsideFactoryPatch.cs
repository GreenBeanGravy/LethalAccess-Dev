using GameNetcodeStuff;
using HarmonyLib;

namespace LethalAccess.Patches
{
    [HarmonyPatch(typeof(StartOfRound))]
    public static class IsInsideFactoryPatch
    {
        public static bool IsInFactory = false; // Public static field to hold the state
        private static bool previousIsInFactoryState = false; // Variable to track previous state

        [HarmonyPatch("Update")]
        [HarmonyPostfix]
        public static void Postfix()
        {
            var playerController = GameNetworkManager.Instance.localPlayerController as PlayerControllerB;
            if (playerController != null)
            {
                bool currentIsInsideFactory = playerController.isInsideFactory;

                // Check if the state has changed since the last frame
                if (currentIsInsideFactory != previousIsInFactoryState)
                {
                    // Update the tracked state
                    previousIsInFactoryState = currentIsInsideFactory;
                    IsInFactory = currentIsInsideFactory;

                    // Speak text based on whether the player is entering or leaving the facility
                    if (currentIsInsideFactory)
                    {
                        Utilities.SpeakText("You have entered the Facility.");
                    }
                    else
                    {
                        Utilities.SpeakText("You have left the Facility.");
                    }
                }
            }
        }
    }
}