using HarmonyLib;

// The patched method is declared on GrabbableObject, but __instance was declared as
// PlayerControllerB. Harmony emits a bare `ldarg.0` for __instance with no cast and no type
// check (MethodPatcher.EmitCallParameter), so this did not throw; it silently handed a
// GrabbableObject to a parameter typed PlayerControllerB. `.enabled` resolves to
// Behaviour.set_enabled, which both types inherit, so what actually executed was a toggle of
// the *item's* enabled flag, not the player's.
//
// The declared type is corrected here so the IL is valid and the code says what it does.
// Runtime behaviour is unchanged.
//
// UNVERIFIED: the purpose of this patch could not be established. GrabbableObject
// .ItemInteractLeftRightOnClient (GrabbableObject.ItemInteractLeftRightOnClient) only calls RequireCooldown,
// UseItemBatteries and the virtual ItemInteractLeftRight, none of which consult
// Behaviour.enabled, so toggling it appears to have no effect on that path. Left in place
// rather than removed, because the original intent is unknown and removing it could silently
// drop a workaround for something not reproduced here.
[HarmonyPatch(typeof(GrabbableObject), nameof(GrabbableObject.ItemInteractLeftRightOnClient))]
class ItemInteractLeftRightOnClientPatch
{
    static void Prefix(GrabbableObject __instance, ref bool __state)
    {
        __state = __instance.enabled;
        __instance.enabled = true;
    }

    static void Postfix(GrabbableObject __instance, bool __state)
    {
        __instance.enabled = __state;
    }
}