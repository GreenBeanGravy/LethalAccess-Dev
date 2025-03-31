using HarmonyLib;

namespace Green.LethalAccessPlugin
{
    [HarmonyPatch(typeof(SaveFileUISlot))]
    public static class SaveFileUISlotPatch
    {
        [HarmonyPatch("SetFileToThis"), HarmonyPostfix]
        public static void SetFileToThisPostfix(SaveFileUISlot __instance)
        {
            if (__instance.fileNum == -1) return; // Do not announce if file number is 0

            // Extracting group balance and days survived from fileStatsText
            string details = __instance.fileStatsText.text;
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
            Utilities.SpeakText(message);
        }
    }
}