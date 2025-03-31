using HarmonyLib;
using UnityEngine;

namespace Green.LethalAccessPlugin.Patches
{
    [HarmonyPatch(typeof(ItemCharger), "ChargeItem")]
    public static class ItemRecharger_ChargeItem_Patch
    {
        // This postfix runs after the original ChargeItem method
        static void Postfix()
        {
            // Speak when an item is recharged
            SpeakItemRecharged(); // This calls a custom method to handle the speaking functionality
        }

        // Custom method to handle speaking functionality
        private static void SpeakItemRecharged()
        {
            Debug.Log("Item recharged!"); // Placeholder for demonstration
            Utilities.SpeakText("Item recharged!");
        }
    }
}