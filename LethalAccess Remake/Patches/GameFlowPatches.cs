using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LethalAccess
{
    /// <summary>
    /// InitScene (online) and InitSceneLANMode run InitializeGame.SendToNextScene: a boot-up
    /// screen of about 3.5 s, and inside that same branch the cold opens. ColdOpen1 plays on the
    /// seventh launch and on saves from before v50; ColdOpen2 (the boot-up error) plays once on a
    /// later launch. Both choices are made in Awake, so a postfix there decides before Start runs.
    /// </summary>
    [HarmonyPatch(typeof(InitializeGame), "Awake")]
    internal static class InitializeGameSkipPatch
    {
        [HarmonyPostfix]
        private static void Postfix(InitializeGame __instance)
        {
            if (ConfigManager.DisableColdOpenEvents.Value)
            {
                __instance.playColdOpenCinematic = false;
                __instance.playColdOpenCinematic2 = false;
            }

            if (ConfigManager.SkipIntroScreens.Value)
            {
                // SendToNextScene then waits 0.2 s and loads MainMenu. The cold opens only live in
                // the boot-up branch, so this skips them too.
                __instance.runBootUpScreen = false;
            }
        }
    }

    /// <summary>
    /// The first visit to the main menu plays its opening animation; every later visit fires the
    /// animator's own "skipOpening" trigger. Firing it on the first visit as well.
    /// </summary>
    [HarmonyPatch(typeof(MenuManager), "Start")]
    internal static class MenuOpeningSkipPatch
    {
        [HarmonyPostfix]
        private static void Postfix(MenuManager __instance)
        {
            if (!ConfigManager.SkipIntroScreens.Value || __instance.isInitScene || __instance.menuAnimator == null)
                return;

            __instance.menuAnimator.SetTrigger("skipOpening");
        }
    }

    /// <summary>
    /// Faster ship landing and take-off for the host.
    ///
    /// The ship's movement is the shipAnimator ("OpenShip" to land, "ShipLeave" to take off), and
    /// the landing is timed by StartOfRound.openingDoorsSequence, about 21 s of WaitForSeconds
    /// that ends with shipHasLanded = true. Speeding up only the animator would land the ship and
    /// then leave everyone waiting for the old timer, so the sequence's waits are scaled by the
    /// same factor. Take-off ends with an animation event (ElevatorAnimationEvents
    /// .ElevatorFullyRunning -> ShipHasLeft), so the animator speed alone shortens it.
    ///
    /// Host only. Each client runs its own copy of both, and a client's shipHasLanded running
    /// ahead of the server's would offer a lever the server then refuses.
    /// </summary>
    [HarmonyPatch(typeof(StartOfRound))]
    internal static class FastShipPatch
    {
        private static readonly FieldInfo WaitSecondsField = AccessTools.Field(typeof(WaitForSeconds), "m_Seconds");
        private static bool landing;
        private static bool speedApplied;

        private static bool Enabled =>
            ConfigManager.FastShipLanding.Value &&
            ConfigManager.ShipLandingSpeed.Value > 1.01f &&
            StartOfRound.Instance != null &&
            StartOfRound.Instance.IsServer;

        [HarmonyPatch("openingDoorsSequence")]
        [HarmonyPostfix]
        private static void OpeningDoorsPostfix(ref IEnumerator __result)
        {
            if (Enabled)
            {
                __result = ScaledWaits(__result, ConfigManager.ShipLandingSpeed.Value);
            }
        }

        private static IEnumerator ScaledWaits(IEnumerator inner, float speed)
        {
            landing = true;
            try
            {
                while (inner.MoveNext())
                {
                    object current = inner.Current;
                    if (current is WaitForSeconds wait && WaitSecondsField != null)
                    {
                        yield return new WaitForSeconds((float)WaitSecondsField.GetValue(wait) / speed);
                    }
                    else
                    {
                        yield return current;
                    }
                }
            }
            finally
            {
                landing = false;
            }
        }

        /// <summary>
        /// Recomputed every frame, so the speed always returns to 1 once neither sequence is
        /// running, including after a disconnect cut the landing short.
        /// </summary>
        [HarmonyPatch("Update")]
        [HarmonyPostfix]
        private static void UpdatePostfix(StartOfRound __instance)
        {
            try
            {
                float speed = 1f;
                if (Enabled && (landing || __instance.shipIsLeaving))
                {
                    speed = ConfigManager.ShipLandingSpeed.Value;
                }

                // Leave the animator alone unless this patch changed it.
                if (speed == 1f && !speedApplied)
                    return;
                speedApplied = speed != 1f;

                SetSpeed(__instance.shipAnimator, speed);
                if (__instance.shipAnimatorObject != null)
                {
                    SetSpeed(__instance.shipAnimatorObject.GetComponent<Animator>(), speed);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LethalAccess] FastShipPatch: {ex.Message}");
            }
        }

        private static void SetSpeed(Animator animator, float speed)
        {
            if (animator != null && !Mathf.Approximately(animator.speed, speed))
            {
                animator.speed = speed;
            }
        }
    }
}
