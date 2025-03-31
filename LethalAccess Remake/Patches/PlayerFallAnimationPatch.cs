using HarmonyLib;
using GameNetcodeStuff;

namespace Green.LethalAccessPlugin
{
    [HarmonyPatch(typeof(PlayerControllerB))]
    public class PlayerFallAnimationPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        static void Prefix(PlayerControllerB __instance, ref bool ___isFallingNoJump, ref bool ___isFallingFromJump)
        {
            // If we're pathfinding, prevent falling animations
            if (Pathfinder.Instance != null && Pathfinder.Instance.IsPathfinding)
            {
                ___isFallingNoJump = false;
                ___isFallingFromJump = false;
            }
        }
    }
}