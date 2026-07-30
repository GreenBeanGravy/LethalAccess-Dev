using System.Collections;
using System.Text;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

namespace LethalAccess.Patches
{
    /// <summary>
    /// The end-of-round stats screen, spoken when it is actually on screen.
    ///
    /// The previous implementation was a single postfix on FillEndGameStats. That method is called
    /// from EndOfGameClientRpc BEFORE the EndOfGame coroutine starts, and the coroutine waits a
    /// full second before pulling the "displayStats" animator trigger - so the mod read the whole
    /// panel over the ship-leaving audio while the panel was still hidden.
    ///
    /// The rest of the screen arrives much later still, on the coroutine's clock:
    ///     +1.0 s  displayStats trigger (panel appears)
    ///     +2.0 s  end-stats music
    ///     +8.0 s  SetPlayerLevel  (then an up-to-5 s XP animation)
    ///    ~+10.0 s FillChallengeResultsStats (challenge files only; rank arrives async)
    ///    ~+13.0 s displayPenalty trigger + ApplyPenalty
    /// so each of those gets its own patch and speaks when its own numbers exist.
    ///
    /// The old penalty block read statsUIElements.penaltyAddition / penaltyTotal from inside the
    /// FillEndGameStats postfix - roughly thirteen seconds before ApplyPenalty writes them. It was
    /// gated on the animator already being in "displayPenalty", which nothing resets between
    /// rounds, so it was dead on the first round of a session and read the PREVIOUS round's
    /// penalty on every round after that. It is deleted; ApplyPenalty is patched instead.
    /// </summary>
    public static class EndOfRoundStatsAccess
    {
        // Just past the coroutine's own 1 s wait, so the panel is up and the ship-leaving audio
        // has finished before the first word.
        private const float StatsAnnounceDelaySeconds = 1.2f;

        // SetPlayerLevelSmoothly animates for at most 5 s and stops LevellingAudio when it ends.
        private const float LevelAnimationTimeoutSeconds = 5.5f;

        // The Steam leaderboard round-trip has no completion callback we can reach from here.
        private const float ChallengeRankTimeoutSeconds = 10f;

        // EndOfGameClientRpc's Harmony postfix runs twice on a host (once for the send stage, once
        // for the execute stage). Nothing in the method's signature distinguishes them, so the
        // second one is dropped on time.
        private const float RpcDedupeSeconds = 3f;

        private static float lastCompanyAnnounceTime = -999f;

        /// <summary>
        /// Coroutines need a live MonoBehaviour. HUDManager owns every field being read and lives
        /// for the whole round, so if it is gone there is nothing to announce anyway.
        /// </summary>
        private static bool TryStart(IEnumerator routine, string what)
        {
            HUDManager hud = HUDManager.Instance;
            if (hud == null)
            {
                Debug.LogError("[LethalAccess] End-of-round: HUDManager.Instance is null, so " + what +
                               " cannot be announced.");
                return false;
            }
            hud.StartCoroutine(routine);
            return true;
        }

        #region Main stats panel

        [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.FillEndGameStats))]
        public static class FillEndGameStatsPatch
        {
            [HarmonyPostfix]
            public static void Postfix(HUDManager __instance, EndOfGameStats stats, int scrapCollected)
            {
                // Stash only. Speaking from here is what put the readout a second ahead of the
                // panel and on top of the ship audio.
                TryStart(SpeakStatsAfterPanelAppears(__instance, stats), "the end-of-round stats");
            }
        }

        private static IEnumerator SpeakStatsAfterPanelAppears(HUDManager hud, EndOfGameStats stats)
        {
            yield return new WaitForSeconds(StatsAnnounceDelaySeconds);

            if (hud == null || hud.statsUIElements == null)
            {
                Debug.LogError("[LethalAccess] End-of-round stats were stashed but the HUD was gone by the " +
                               "time the panel should have appeared; nothing was announced.");
                yield break;
            }

            EndOfGameStatUIElements ui = hud.statsUIElements;
            StringBuilder sb = new StringBuilder();
            sb.Append("End of round. ");

            if (ui.allPlayersDeadOverlay != null && ui.allPlayersDeadOverlay.enabled)
            {
                sb.Append("All players dead. ");
            }

            // Read the numbers off the panel rather than recomputing them: these are the two
            // strings the sighted player is looking at.
            if (ui.quotaNumerator != null && ui.quotaDenominator != null)
            {
                sb.Append("Scrap collected: $").Append(ui.quotaNumerator.text)
                  .Append(" of $").Append(ui.quotaDenominator.text).Append(". ");
            }

            if (ui.gradeLetter != null && !string.IsNullOrEmpty(ui.gradeLetter.text))
            {
                sb.Append("Grade: ").Append(ui.gradeLetter.text).Append(". ");
            }

            TimeOfDay tod = TimeOfDay.Instance;
            if (tod != null)
            {
                sb.Append("Profit quota: $").Append(tod.quotaFulfilled)
                  .Append(" of $").Append(tod.profitQuota).Append(". ");
            }

            AppendRoster(sb, hud, stats);

            AnnouncementPolicy.Speak(sb.ToString());
        }

        private static void AppendRoster(StringBuilder sb, HUDManager hud, EndOfGameStats stats)
        {
            if (hud.playersManager == null || hud.playersManager.allPlayerScripts == null)
            {
                return;
            }

            PlayerControllerB[] players = hud.playersManager.allPlayerScripts;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB p = players[i];
                if (p == null)
                {
                    continue;
                }

                // The game's own predicate for "this slot gets a row on the panel". The old filter
                // was !playerUsername.StartsWith("Player #"), which is a guess at the same thing
                // and disagrees with it for anyone who never renamed themselves and for anyone who
                // did name themselves "Player #2".
                if (!p.disconnectedMidGame && !p.isPlayerDead && !p.isPlayerControlled)
                {
                    continue;
                }

                sb.Append(p.playerUsername).Append(": ");

                if (p.isPlayerDead)
                {
                    sb.Append(p.causeOfDeath == CauseOfDeath.Abandoned ? "missing" : "deceased");
                }
                else if (p.disconnectedMidGame)
                {
                    sb.Append("alive, disconnected");
                }
                else
                {
                    sb.Append("alive");
                }
                sb.Append(". ");

                AppendNotes(sb, stats, i);
            }
        }

        private static void AppendNotes(StringBuilder sb, EndOfGameStats stats, int playerIndex)
        {
            if (stats == null || stats.allPlayerStats == null || stats.allPlayerStats.Length <= playerIndex)
            {
                return;
            }

            var playerStat = stats.allPlayerStats[playerIndex];
            if (playerStat == null || playerStat.playerNotes == null || playerStat.playerNotes.Count == 0)
            {
                return;
            }

            // FillEndGameStats writes at most three notes into the panel, so the cap keeps this
            // from reading notes that are not on the screen.
            int shown = Mathf.Min(3, playerStat.playerNotes.Count);
            sb.Append("Notes: ");
            for (int j = 0; j < shown; j++)
            {
                sb.Append(playerStat.playerNotes[j]).Append(". ");
            }
        }

        #endregion

        #region Player level (+8 s, then an animation)

        [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.SetPlayerLevel))]
        public static class SetPlayerLevelPatch
        {
            [HarmonyPostfix]
            public static void Postfix(HUDManager __instance)
            {
                TryStart(SpeakLevelWhenSettled(__instance), "the player level");
            }
        }

        private static IEnumerator SpeakLevelWhenSettled(HUDManager hud)
        {
            // SetPlayerLevel either writes the fields immediately (no XP gained) or hands off to
            // SetPlayerLevelSmoothly, which plays LevellingAudio for the whole animation. Waiting
            // on the audio reads the settled numbers instead of a frame of the count-up.
            float deadline = Time.realtimeSinceStartup + LevelAnimationTimeoutSeconds;
            yield return null;

            while (hud != null && hud.LevellingAudio != null && hud.LevellingAudio.isPlaying &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (hud == null || hud.playerLevelText == null || hud.playerLevelXPCounter == null)
            {
                Debug.LogError("[LethalAccess] End-of-round: the player-level readouts are missing, so the " +
                               "new rank was not announced.");
                yield break;
            }

            AnnouncementPolicy.Speak("Rank: " + hud.playerLevelText.text + ", " + hud.playerLevelXPCounter.text + ".");
        }

        #endregion

        #region Challenge results (challenge files only)

        [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.FillChallengeResultsStats))]
        public static class FillChallengeResultsStatsPatch
        {
            [HarmonyPostfix]
            public static void Postfix(HUDManager __instance)
            {
                TryStart(SpeakChallengeResults(__instance), "the challenge results");
            }
        }

        private static IEnumerator SpeakChallengeResults(HUDManager hud)
        {
            if (hud == null || hud.statsUIElements == null)
            {
                yield break;
            }

            EndOfGameStatUIElements ui = hud.statsUIElements;

            if (ui.challengeCollectedText != null && !string.IsNullOrEmpty(ui.challengeCollectedText.text))
            {
                AnnouncementPolicy.Speak(ui.challengeCollectedText.text);
            }

            if (ui.challengeRankText == null)
            {
                yield break;
            }

            // The rank is written by GetRankAndSubmitScore, an `async void` awaiting two Steam
            // calls. There is nothing to patch that reliably marks its completion, so the panel's
            // own text is polled - it is the same thing a sighted player is waiting to see.
            string before = ui.challengeRankText.text;
            float deadline = Time.realtimeSinceStartup + ChallengeRankTimeoutSeconds;

            while (Time.realtimeSinceStartup < deadline)
            {
                if (ui.challengeRankText == null)
                {
                    yield break;
                }

                string now = ui.challengeRankText.text;
                if (!string.IsNullOrEmpty(now) && now != before)
                {
                    AnnouncementPolicy.Speak(now == "---"
                        ? "Global rank unavailable."
                        : "Global rank " + now.TrimStart('#') + ".");
                    yield break;
                }
                yield return null;
            }

            // Saying nothing here would be indistinguishable from the mod having missed the
            // screen entirely.
            AnnouncementPolicy.Speak("Global rank did not arrive.");
        }

        #endregion

        #region Penalty (~+13 s)

        [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.ApplyPenalty))]
        public static class ApplyPenaltyPatch
        {
            [HarmonyPostfix]
            public static void Postfix(HUDManager __instance, int playersDead, int bodiesInsured)
            {
                // ApplyPenalty writes penaltyAddition and penaltyTotal in its own body, so a
                // postfix is the first moment those two strings are true.
                if (__instance == null || __instance.statsUIElements == null)
                {
                    return;
                }

                EndOfGameStatUIElements ui = __instance.statsUIElements;
                StringBuilder sb = new StringBuilder();
                sb.Append("Penalty applied. ");
                sb.Append(playersDead).Append(playersDead == 1 ? " casualty, " : " casualties, ");
                sb.Append(bodiesInsured).Append(bodiesInsured == 1 ? " body recovered. " : " bodies recovered. ");

                if (ui.penaltyTotal != null && !string.IsNullOrEmpty(ui.penaltyTotal.text))
                {
                    sb.Append(ui.penaltyTotal.text).Append(".");
                }

                AnnouncementPolicy.Speak(sb.ToString());
            }
        }

        #endregion

        #region Moons with no stats panel (the Company)

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.EndOfGameClientRpc))]
        public static class EndOfGameClientRpcPatch
        {
            [HarmonyPostfix]
            public static void Postfix(StartOfRound __instance, int scrapCollectedOnServer)
            {
                if (__instance == null || __instance.currentLevel == null)
                {
                    return;
                }

                // planetHasTime gates every stats call in EndOfGameClientRpc and in the EndOfGame
                // coroutine, so on the Company nothing above this region ever fires and the round
                // used to end in complete silence.
                if (__instance.currentLevel.planetHasTime)
                {
                    return;
                }

                if (Time.realtimeSinceStartup - lastCompanyAnnounceTime < RpcDedupeSeconds)
                {
                    return;
                }
                lastCompanyAnnounceTime = Time.realtimeSinceStartup;

                StringBuilder sb = new StringBuilder();
                sb.Append("Leaving ").Append(__instance.currentLevel.PlanetName).Append(". ");
                sb.Append("Scrap value returned: $").Append(scrapCollectedOnServer).Append(". ");

                TimeOfDay tod = TimeOfDay.Instance;
                if (tod != null)
                {
                    sb.Append("Profit quota: $").Append(tod.quotaFulfilled)
                      .Append(" of $").Append(tod.profitQuota).Append(". ");
                    sb.Append(tod.daysUntilDeadline).Append(tod.daysUntilDeadline == 1 ? " day left." : " days left.");
                }

                AnnouncementPolicy.Speak(sb.ToString());
            }
        }

        #endregion
    }
}
