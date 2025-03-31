using HarmonyLib;
using UnityEngine;

namespace Green.LethalAccessPlugin.Patches
{
    [HarmonyPatch(typeof(StartOfRound), "Update")]
    public static class ShipHasLandedPatch
    {
        private static bool previousShipHasLanded = false;

        [HarmonyPostfix]
        public static void Postfix(StartOfRound __instance)
        {
            // Check if the shipHasLanded field is accessible
            if (__instance.shipHasLanded)
            {
                // Speak only when the value changes from false to true
                if (!previousShipHasLanded)
                {
                    Utilities.SpeakText("The ship has landed.");
                    previousShipHasLanded = true;
                }
            }
            else
            {
                previousShipHasLanded = false;
            }
        }
    }

    [HarmonyPatch(typeof(StartOfRound), "ShipHasLeft")]
    public static class ShipHasLeftPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            Utilities.SpeakText("The ship has left.");
        }
    }
}