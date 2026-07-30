using HarmonyLib;
using UnityEngine;

namespace LethalAccess
{
    [HarmonyPatch(typeof(SaveFileUISlot))]
    public static class SaveFileUISlotPatch
    {
        [HarmonyPatch("SetFileToThis"), HarmonyPostfix]
        public static void SetFileToThisPostfix(SaveFileUISlot __instance)
        {
            MenuManager menu = UnityEngine.Object.FindObjectOfType<MenuManager>();

            // SetFileToThis returns without changing anything while a leaderboard request is in
            // flight (SaveFileUISlot.SetButtonColor). Announcing a selection that did not happen is
            // worse than saying nothing, so mirror the method's own early-out.
            if (menu != null && menu.requestingLeaderboard)
            {
                return;
            }

            // FileNum == -1 is the challenge moon, and the old guard returned here, so
            // selecting it was completely silent. It is also the one slot whose label is written
            // at runtime (SaveFileUISlot.OnEnable) and whose stats TMP is DISABLED rather than
            // blanked when the challenge has not been completed.
            if (__instance.fileNum == -1)
            {
                AnnouncementPolicy.Speak(DescribeChallengeMoon(__instance));
                return;
            }

            // Extracting group balance and days survived from fileStatsText
            string details = __instance.fileStatsText != null ? __instance.fileStatsText.text : null;
            string message;

            if (string.IsNullOrWhiteSpace(details))
            {
                // Construct the message for an empty file
                message = $"Set to File {__instance.fileNum + 1}, empty file.";
            }
            else
            {
                string[] splitDetails = details.Split('\n');
                string groupBalance = splitDetails.Length > 0 ? splitDetails[0] : "";
                string daysSurvived = splitDetails.Length > 1 ? splitDetails[1] : "";

                // Construct the message with details
                message = $"Set to File {__instance.fileNum + 1}, Balance: {groupBalance}, {daysSurvived}";
            }

            // Announce the message
            AnnouncementPolicy.Speak(message);
        }

        /// <summary>
        /// The week name comes off the live TMP, not from GetNameForWeekNumber: SaveFileUISlot
        /// writes it there on enable, and reading the label that is actually on screen is the only
        /// way the announcement cannot disagree with it.
        /// </summary>
        private static string DescribeChallengeMoon(SaveFileUISlot slot)
        {
            string weekName = slot.fileNameText != null ? slot.fileNameText.text : null;
            if (string.IsNullOrEmpty(weekName) && GameNetworkManager.Instance != null)
            {
                // The TMP is only filled in OnEnable; fall back so a selection is never silent.
                weekName = GameNetworkManager.Instance.GetNameForWeekNumber();
            }

            string message = string.IsNullOrEmpty(weekName)
                ? "Set to challenge moon."
                : $"Set to challenge moon {weekName}.";

            // fileStatsText is disabled, not blanked, for an unattempted challenge
            // (SaveFileUISlot.SetChallengeFileSettings), so .enabled is part of reading it.
            if (slot.fileStatsText != null && slot.fileStatsText.enabled &&
                !string.IsNullOrWhiteSpace(slot.fileStatsText.text))
            {
                message += " " + slot.fileStatsText.text.Replace("\n", ", ").Trim() + ".";
            }
            else
            {
                message += " Not yet completed.";
            }

            // Set by SaveFileUISlot.SetFileToThis itself on the not-yet-completed path, and shown
            // in a TMP that nothing focuses.
            if (slot.specialTipText != null && slot.specialTipText.enabled &&
                !string.IsNullOrWhiteSpace(slot.specialTipText.text))
            {
                message += " " + slot.specialTipText.text;
            }

            return message;
        }
    }
}
