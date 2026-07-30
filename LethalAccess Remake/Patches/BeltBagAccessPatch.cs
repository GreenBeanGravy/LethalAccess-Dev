using HarmonyLib;
using UnityEngine;

namespace LethalAccess.Patches
{
    /// <summary>
    /// The belt bag, which had no mod coverage of any kind.
    ///
    /// One line is responsible for most of it: HUDManager.OpenSpecialMenu only calls
    /// SetSelectedGameObject when StartOfRound.Instance.localPlayerUsingController is true. On
    /// mouse and keyboard nothing is ever focused, so the announcer has nothing to announce and no
    /// arrow key does anything - the screen might as well not exist. Everything else in this file
    /// depends on that selection being seeded.
    /// </summary>
    public static class BeltBagAccessPatch
    {
        // BeltBagItem.ItemInteractLeftRight refuses to add anything once objectsInBag.Count
        // reaches this. It is a literal in the game, not a property of inventorySlots.Length.
        private const int BagCapacity = 15;

        #region Opening and closing

        [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.OpenSpecialMenu))]
        public static class OpenSpecialMenuPatch
        {
            [HarmonyPostfix]
            public static void Postfix(HUDManager __instance, SpecialHUDMenu menu)
            {
                if (__instance.specialHUDMenus == null)
                {
                    Debug.LogError("[LethalAccess] OpenSpecialMenu: specialHUDMenus is null, so no focus can be " +
                                   "seeded and the menu is unreachable by keyboard.");
                    return;
                }

                // The game indexes with (int)(menu - 1) and would throw on SpecialHUDMenu.None or
                // on any value a future update adds; guard it rather than inherit the crash.
                int index = (int)menu - 1;
                if (index < 0 || index >= __instance.specialHUDMenus.Length)
                {
                    Debug.LogError("[LethalAccess] OpenSpecialMenu was called with menu '" + menu + "' (index " +
                                   index + "), which is outside specialHUDMenus; no focus was seeded.");
                    return;
                }

                SpecialHUDMenuObject entry = __instance.specialHUDMenus[index];
                if (entry == null)
                {
                    Debug.LogError("[LethalAccess] OpenSpecialMenu: specialHUDMenus[" + index + "] is null.");
                    return;
                }

                if (menu != SpecialHUDMenu.BeltBagInventory)
                {
                    // Any future special menu still deserves a focus, but only the belt bag has a
                    // navigator that knows how to describe it.
                    if (entry.defaultSelectedGameObject != null)
                    {
                        FocusManager.Seed(entry.defaultSelectedGameObject);
                    }
                    return;
                }

                BeltBagNavigator.Ensure();
                if (BeltBagNavigator.Instance != null)
                {
                    BeltBagNavigator.Instance.NoteMenuOpened(entry.defaultSelectedGameObject);
                }
            }
        }

        [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.CloseSpecialMenus))]
        public static class CloseSpecialMenusPatch
        {
            [HarmonyPrefix]
            public static void Prefix(HUDManager __instance, ref SpecialHUDMenu __state)
            {
                // OpenSpecialMenu calls CloseSpecialMenus first thing, so an unconditional postfix
                // would say "bag closed" immediately before "bag open".
                __state = __instance.currentSpecialMenu;
            }

            [HarmonyPostfix]
            public static void Postfix(SpecialHUDMenu __state)
            {
                if (__state == SpecialHUDMenu.BeltBagInventory)
                {
                    AnnouncementPolicy.Speak("Bag closed.");
                }
            }
        }

        #endregion

        #region Slot labelling

        [HarmonyPatch(typeof(BeltBagInventoryUI), nameof(BeltBagInventoryUI.FillSlots))]
        public static class FillSlotsPatch
        {
            [HarmonyPostfix]
            public static void Postfix(BeltBagInventoryUI __instance)
            {
                BeltBagNavigator.Ensure();
                if (BeltBagNavigator.Instance == null)
                {
                    return;
                }

                BeltBagNavigator.Instance.BuildScreen(__instance);

                // FillSlots also runs after every take, at which point the focused slot's contents
                // have changed underneath the user. Re-announcing the focused control (rather than
                // emitting a separate sentence) is how every other state change in this mod
                // reports itself. Skipped on the opening call, where currentSpecialMenu is still
                // None and the open announcement is about to speak.
                HUDManager hud = HUDManager.Instance;
                if (hud != null && hud.currentSpecialMenu == SpecialHUDMenu.BeltBagInventory)
                {
                    AnnouncementPolicy.ReAnnounceFocused();
                }
            }
        }

        #endregion

        #region Taking an item out

        /// <summary>
        /// Stops the mouse-drag setup from running when the keyboard confirmed the slot, and is
        /// itself one of the two places a keyboard take can be completed.
        ///
        /// ClickInventorySlot is the mouse handler: it sets grabbingItemSlot, hides the icon and
        /// hands the item to the cursor, and the only thing that finishes the gesture is an
        /// ActivateItem.canceled callback that raycasts Mouse.current.position against the panel.
        /// A keyboard cannot produce that, so if the submit key reaches the slot Button the item
        /// is stranded on the cursor.
        ///
        /// The EventSystem dispatches Submit from ITS OWN Update, and Unity does not define
        /// whether that runs before or after BeltBagNavigator.Update - so neither of them may
        /// assume it goes first. This prefix therefore does not read a flag the navigator sets;
        /// it asks the navigator, which asks the keyboard device the same frame-scoped question
        /// its own Update asks. Whichever of the two runs first performs the take and records the
        /// frame; the other only suppresses. Exactly one take happens in either ordering.
        ///
        /// A click that is NOT attributable to the confirm key is a real mouse click and vanilla
        /// runs untouched: the drag-and-drop it starts is the only form of the gesture a mouse
        /// can finish.
        /// </summary>
        [HarmonyPatch(typeof(BeltBagInventoryUI), nameof(BeltBagInventoryUI.ClickInventorySlot))]
        public static class ClickInventorySlotPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(BeltBagInventoryUI __instance, int slotNumber)
            {
                BeltBagNavigator nav = BeltBagNavigator.Instance;
                if (nav == null)
                {
                    // Nothing of ours has labelled or focused this panel, so no keypress of ours
                    // can have produced this click. Leaving vanilla alone is the only safe answer.
                    return true;
                }

                return !nav.ClaimKeyboardSlotClick(__instance, slotNumber);
            }
        }

        // Public only because it appears in the signature of the public Harmony patch methods
        // below; C# refuses a public method whose parameter type is less accessible.
        public struct RemoveState
        {
            public int CountBefore;
            public string ItemName;
            public bool WasBusy;
            public bool WasOccupied;
        }

        [HarmonyPatch(typeof(BeltBagInventoryUI), nameof(BeltBagInventoryUI.RemoveItemFromUI))]
        public static class RemoveItemFromUIPatch
        {
            [HarmonyPrefix]
            public static void Prefix(BeltBagInventoryUI __instance, int slot, ref RemoveState __state)
            {
                __state = default(RemoveState);
                __state.CountBefore = -1;

                BeltBagItem bag = __instance.currentBeltBag;
                if (bag == null || bag.objectsInBag == null)
                {
                    return;
                }

                __state.CountBefore = bag.objectsInBag.Count;
                __state.WasBusy = bag.tryingAddToBag;

                if (slot >= 0 && slot < bag.objectsInBag.Count && bag.objectsInBag[slot] != null)
                {
                    __state.WasOccupied = true;
                    GrabbableObject item = bag.objectsInBag[slot];
                    __state.ItemName = item.itemProperties != null && !string.IsNullOrEmpty(item.itemProperties.itemName)
                        ? item.itemProperties.itemName
                        : item.gameObject.name;
                }
            }

            [HarmonyPostfix]
            public static void Postfix(BeltBagInventoryUI __instance, RemoveState __state)
            {
                if (__state.CountBefore < 0 || !__state.WasOccupied)
                {
                    return;
                }

                BeltBagItem bag = __instance.currentBeltBag;
                int countAfter = bag != null && bag.objectsInBag != null ? bag.objectsInBag.Count : __state.CountBefore;

                if (countAfter < __state.CountBefore)
                {
                    AnnouncementPolicy.Speak("Took " + __state.ItemName + ".");
                    return;
                }

                // The only guard that can reject an occupied slot is tryingAddToBag - an insert is
                // still in flight to the server. Silence here would look identical to a dead key.
                AnnouncementPolicy.Speak(__state.WasBusy
                    ? "Cannot take " + __state.ItemName + ", bag is busy."
                    : "Could not take " + __state.ItemName + ".");
            }
        }

        #endregion

        #region Putting an item in

        [HarmonyPatch(typeof(BeltBagItem), nameof(BeltBagItem.PutObjectInBagLocalClient))]
        public static class PutObjectInBagPatch
        {
            [HarmonyPostfix]
            public static void Postfix(BeltBagItem __instance, GrabbableObject gObject)
            {
                // Runs on every client, so it has to be limited to the player actually holding the
                // bag or everyone in the lobby hears someone else's insert.
                if (!IsLocalHolder(__instance) || gObject == null)
                {
                    return;
                }

                string name = gObject.itemProperties != null && !string.IsNullOrEmpty(gObject.itemProperties.itemName)
                    ? gObject.itemProperties.itemName
                    : gObject.gameObject.name;

                int count = __instance.objectsInBag != null ? __instance.objectsInBag.Count : 0;
                AnnouncementPolicy.Speak("Put " + name + " in bag, " + count + " of " + BagCapacity + ".");
            }
        }

        [HarmonyPatch(typeof(BeltBagItem), nameof(BeltBagItem.ItemInteractLeftRight))]
        public static class ItemInteractLeftRightPatch
        {
            [HarmonyPostfix]
            public static void Postfix(BeltBagItem __instance, bool right)
            {
                // The method returns silently on a full bag, so pressing the insert key on a full
                // bag is indistinguishable from the key not being bound.
                if (right || !IsLocalHolder(__instance) || __instance.objectsInBag == null)
                {
                    return;
                }
                if (__instance.tryingAddToBag || __instance.objectsInBag.Count < BagCapacity)
                {
                    return;
                }

                AnnouncementPolicy.Speak("Bag is full, " + BagCapacity + " items.");
            }
        }

        private static bool IsLocalHolder(BeltBagItem bag)
        {
            if (bag == null || bag.playerHeldBy == null || GameNetworkManager.Instance == null)
            {
                return false;
            }
            return bag.playerHeldBy == GameNetworkManager.Instance.localPlayerController;
        }

        #endregion
    }
}
