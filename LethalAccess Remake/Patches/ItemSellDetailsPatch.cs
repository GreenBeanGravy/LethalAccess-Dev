using HarmonyLib;

namespace Green.LethalAccessPlugin.Patches
{
    [HarmonyPatch(typeof(DepositItemsDesk))]
    public class DepositItemsDeskPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch("SellAndDisplayItemProfits")]
        public static void Postfix(int profit, int newGroupCredits, DepositItemsDesk __instance)
        {
            // Speak the profit and new group credits
            string message = "Sold items for a profit of $" + profit.ToString() + ". New group total: $" + newGroupCredits.ToString();
            Utilities.SpeakText(message);

            // Additional logic if needed to speak about individual items sold
            GrabbableObject[] soldItems = __instance.deskObjectsContainer.GetComponentsInChildren<GrabbableObject>();
            foreach (GrabbableObject item in soldItems)
            {
                // You can customize this message as needed
                string itemName = item.itemProperties?.itemName ?? item.gameObject.name;
                int scrapValue = item.scrapValue;
                Utilities.SpeakText($"Sold {itemName} for ${scrapValue}");
            }
        }
    }
}