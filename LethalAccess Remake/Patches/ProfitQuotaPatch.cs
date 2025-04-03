using UnityEngine;
using UnityEngine.InputSystem;
using HarmonyLib;

namespace LethalAccess.Patches
{
    public class ProfitQuotaPatch : MonoBehaviour
    {
        private const string SpeakProfitQuotaKeybindName = "SpeakProfitQuotaKey";
        private const Key SpeakProfitQuotaDefaultKey = Key.K;

        public void Initialize()
        {
            Debug.Log("ProfitQuotaPatch: Initializing input actions.");
            LACore.Instance.RegisterKeybind(SpeakProfitQuotaKeybindName, SpeakProfitQuotaDefaultKey, SpeakProfitQuota);
            Debug.Log("ProfitQuotaPatch: Input actions are registered.");
        }

        private void SpeakProfitQuota()
        {
            TimeOfDay timeOfDayInstance = TimeOfDay.Instance;
            if (timeOfDayInstance != null)
            {
                // Speak the current profit and quota
                int currentProfit = timeOfDayInstance.quotaFulfilled;
                int profitQuota = timeOfDayInstance.profitQuota;
                Debug.Log($"[ProfitQuotaPatch] Current profit: ${currentProfit}, Profit Quota: ${profitQuota}");
                Utilities.SpeakText($"Profit Quota: {currentProfit} of ${profitQuota}");

                // Speak the number of days left
                int daysLeft = timeOfDayInstance.daysUntilDeadline;
                Utilities.SpeakText($"{daysLeft} days left, ");
            }
            else
            {
                Debug.LogError("[ProfitQuotaPatch] TimeOfDay instance is not available.");
            }

            // Speak additional stats
            SpeakAdditionalStats();
        }

        private void SpeakAdditionalStats()
        {
            Terminal terminalInstance = Object.FindObjectOfType<Terminal>();
            StartOfRound startOfRoundInstance = StartOfRound.Instance;

            if (terminalInstance != null && startOfRoundInstance != null)
            {
                int groupCredits = terminalInstance.groupCredits;
                float companyBuyingRate = startOfRoundInstance.companyBuyingRate;

                // Convert the company buying rate to a percentage and round it to the nearest integer
                int companyBuyingRatePercentage = Mathf.RoundToInt(companyBuyingRate * 100);

                string additionalStats = $"Group Balance: ${groupCredits}, " +
                    $"Company Buy Rate: {companyBuyingRatePercentage} percent.";

                Utilities.SpeakText(additionalStats);
            }
            else
            {
                Debug.LogError("[ProfitQuotaPatch] Unable to access Terminal or StartOfRound instances.");
            }
        }
    }

    [HarmonyPatch(typeof(TimeOfDay), nameof(TimeOfDay.UpdateProfitQuotaCurrentTime))]
    public static class TimeOfDayUpdateProfitQuotaCurrentTimePatch
    {
        static void Postfix(TimeOfDay __instance)
        {
            // Extract the number of days until the deadline
            int daysLeft = __instance.daysUntilDeadline;

            // Construct the message
            string message = $"{daysLeft} days left.";

            // Announce the message
            Utilities.SpeakText(message);

            // Optional: Log the message for debugging
            Debug.Log(message);
        }
    }
}