using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace LethalAccess
{
    /// <summary>
    /// Makes menu navigation vertical-only, and routes up/down through the authored screen order
    /// instead of Unity's geometric search.
    ///
    /// Why prefixes on the finders rather than rewriting `navigation` on live components:
    ///   - Non-destructive and fully reversible. Blanket-writing Navigation onto scene Selectables
    ///     destroys authored state with no clean restore; one bug leaves the UI mis-wired until a
    ///     scene reload.
    ///   - No invalidation story needed. Every SetActive, every instantiated LobbySlot, every
    ///     LoadKeybindsUI rebuild changes the Selectable set and there is no event for it, so a
    ///     rewrite approach ends up sweeping every frame. This costs one dictionary lookup per
    ///     arrow press.
    ///   - It covers controls the mod has never heard of, including ones added by other mods.
    ///
    /// Composition with Slider and Scrollbar (verified against the decompiled UnityEngine.UI):
    /// both OVERRIDE all four finders and return null WITHOUT calling base when
    /// mode == Automatic and their axis matches, and their OnMove steps the value whenever the
    /// finder returned null. Patching the BASE method therefore composes correctly in both
    /// directions: an Automatic horizontal slider never reaches this patch and keeps adjusting,
    /// and one wired into an Explicit chain calls base, gets null from here, and also adjusts.
    /// Sliders do not need KeepsHorizontal to adjust - flag them only to keep them out of the
    /// up/down chain. TMP_Dropdown does NOT override, so it goes inert on left/right unless it is
    /// flagged KeepsHorizontal and given an option-cycling handler.
    /// </summary>
    [HarmonyPatch(typeof(Selectable))]
    internal static class SelectableNavigationPatch
    {
        // Read through a null check: BepInEx binds config in LACore.Awake before PatchAll, but a
        // NullReferenceException raised from inside a navigation finder would be very hard to
        // attribute, and "navigation silently stopped working" is not an acceptable failure.
        private static bool Enabled
        {
            get
            {
                var entry = ConfigManager.VerticalNavigationOnly;
                return entry != null && entry.Value;
            }
        }

        [HarmonyPatch(nameof(Selectable.FindSelectableOnLeft))]
        [HarmonyPrefix]
        public static bool PrefixFindSelectableOnLeft(Selectable __instance, ref Selectable __result)
        {
            return SuppressHorizontal(__instance, ref __result);
        }

        [HarmonyPatch(nameof(Selectable.FindSelectableOnRight))]
        [HarmonyPrefix]
        public static bool PrefixFindSelectableOnRight(Selectable __instance, ref Selectable __result)
        {
            return SuppressHorizontal(__instance, ref __result);
        }

        [HarmonyPatch(nameof(Selectable.FindSelectableOnUp))]
        [HarmonyPrefix]
        public static bool PrefixFindSelectableOnUp(Selectable __instance, ref Selectable __result)
        {
            return RouteVertical(__instance, ref __result, -1);
        }

        [HarmonyPatch(nameof(Selectable.FindSelectableOnDown))]
        [HarmonyPrefix]
        public static bool PrefixFindSelectableOnDown(Selectable __instance, ref Selectable __result)
        {
            return RouteVertical(__instance, ref __result, 1);
        }

        /// <summary>
        /// Left and right stop moving focus, so they are free to mean "adjust this control".
        /// </summary>
        private static bool SuppressHorizontal(Selectable instance, ref Selectable result)
        {
            if (!Enabled)
            {
                return true;
            }
            if (UIScreenRegistry.KeepsHorizontal(instance.gameObject))
            {
                return true;
            }

            result = null;
            return false;
        }

        /// <summary>
        /// Up and down follow the authored screen order when the registry claims this control,
        /// and vanilla geometry otherwise.
        /// </summary>
        private static bool RouteVertical(Selectable instance, ref Selectable result, int delta)
        {
            if (!Enabled)
            {
                return true;
            }

            GameObject go = instance.gameObject;
            if (!UIScreenRegistry.ClaimsSelectable(go))
            {
                // Not described by any screen: leave Unity's geometric search alone rather than
                // guessing. Unregistered UI keeps working exactly as it did.
                return true;
            }

            // Selectable.Navigate silently ignores a target that fails IsActive() - i.e. one
            // whose component is disabled even though its GameObject is on - so a single step is
            // not enough: that case would eat the keypress with no announcement and no clue why.
            // Step past such entries. The walk ends by itself at the end of the order; the bound
            // only exists so a future bug in NextInFlatOrder cannot spin here.
            const int maxSteps = 128;
            for (int step = 1; step <= maxSteps; step++)
            {
                Transform next = UIScreenRegistry.NextInFlatOrder(go, delta * step);
                if (next == null)
                {
                    // Top or bottom of the authored order. Stop here rather than falling through
                    // to geometry, which would jump somewhere the screen model never mentions.
                    result = null;
                    return false;
                }

                Selectable sel = next.GetComponent<Selectable>();
                if (sel == null)
                {
                    // UIScreenRegistry injects a Selectable on every claimed entry that lacks
                    // one, so this means the object was replaced since the screen resolved.
                    Debug.LogWarning("[LethalAccess] Authored navigation target '" + next.name +
                                     "' has no Selectable; skipping it. The screen probably needs " +
                                     "UIScreenRegistry.Invalidate().");
                    continue;
                }

                if (!sel.IsActive())
                {
                    continue;
                }

                result = sel;
                return false;
            }

            Debug.LogError("[LethalAccess] Vertical navigation from '" + go.name + "' gave up after " + maxSteps +
                           " steps without finding a usable neighbour.");
            result = null;
            return false;
        }
    }
}
