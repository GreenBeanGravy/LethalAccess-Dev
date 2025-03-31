using HarmonyLib;
using UnityEngine;

namespace Green.LethalAccessPlugin.Patches
{
    [HarmonyPatch(typeof(PreInitSceneScript))]
    public static class PreInitScenePatch
    {
        [HarmonyPatch("Start")]
        [HarmonyPostfix]
        public static void Postfix(PreInitSceneScript __instance)
        {
            Debug.Log("LethalAccess: Pressing Continue button in PreInitScene");
            __instance.PressContinueButton();
        }
    }
}