using GameNetcodeStuff;
using HarmonyLib;

[HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.ItemInteractLeftRightOnClient))]
class ItemInteractLeftRightOnClientPatch
{
    static void Prefix(PlayerControllerB __instance, ref bool __state)
    {
        __state = __instance.enabled;
        __instance.enabled = true;
    }

    static void Postfix(PlayerControllerB __instance, bool __state)
    {
        __instance.enabled = __state;
    }
}