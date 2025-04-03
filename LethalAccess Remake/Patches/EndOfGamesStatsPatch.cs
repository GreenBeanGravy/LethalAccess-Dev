using GameNetcodeStuff;
using HarmonyLib;
using LethalAccess;
using System.Text;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

namespace LethalAccess.Patches
{
    [HarmonyPatch(typeof(HUDManager), "FillEndGameStats")]
    public static class HUDManagerFillEndGameStatsPatch
    {
        static void Postfix(HUDManager __instance, EndOfGameStats stats, int scrapCollected)
        {
            StringBuilder spokenMessage = new StringBuilder();
            spokenMessage.AppendLine("End of Round Stats:");

            // Scrap Collected
            spokenMessage.AppendLine($"Scrap Collected Value: ${scrapCollected}.");

            int currentProfit = TimeOfDay.Instance.quotaFulfilled;
            int profitQuota = TimeOfDay.Instance.profitQuota;

            // Quota Information
            spokenMessage.AppendLine($"Quota: ${currentProfit} of ${profitQuota}.");

            // Player Details
            for (int i = 0; i < __instance.playersManager.allPlayerScripts.Length; i++)
            {
                PlayerControllerB player = __instance.playersManager.allPlayerScripts[i];
                if (player != null && !player.playerUsername.StartsWith("Player #"))
                {
                    spokenMessage.AppendLine($"Player {i + 1}: {player.playerUsername}.");
                    spokenMessage.AppendLine($"State: {(player.isPlayerDead ? "Dead." : "Alive.")}");

                    if (player.isPlayerDead)
                    {
                        string causeOfDeath = player.causeOfDeath == CauseOfDeath.Abandoned ? "Abandoned." : "Deceased.";
                        spokenMessage.AppendLine($"Cause of Death: {causeOfDeath}.");
                    }

                    // Player Notes
                    if (stats.allPlayerStats.Length > i)
                    {
                        var playerStat = stats.allPlayerStats[i];
                        if (playerStat.playerNotes.Count > 0)
                        {
                            spokenMessage.AppendLine("Notes:");
                            foreach (var note in playerStat.playerNotes)
                            {
                                spokenMessage.AppendLine($"* {note}.");
                            }
                        }
                    }
                }
            }

            // Additional stats if available
            var elements = __instance.statsUIElements;
            if (!string.IsNullOrEmpty(elements.gradeLetter.text))
            {
                spokenMessage.AppendLine($"Grade: {elements.gradeLetter.text}.");
            }

            // Penalty Information
            if (__instance.endgameStatsAnimator.GetCurrentAnimatorStateInfo(0).IsName("displayPenalty"))
            {
                spokenMessage.AppendLine("Penalties have been applied due to player deaths.");
                if (elements.penaltyAddition != null && elements.penaltyTotal != null)
                {
                    spokenMessage.AppendLine($"Penalty Addition: {elements.penaltyAddition.text}.");
                    spokenMessage.AppendLine($"Penalty Total: {elements.penaltyTotal.text}.");
                }
            }

            // Speak the message
            Utilities.SpeakText(spokenMessage.ToString());
        }
    }
}