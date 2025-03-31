using HarmonyLib;
using UnityEngine;
using GameNetcodeStuff;
using TMPro;

namespace Green.LethalAccessPlugin.Patches
{
    [HarmonyPatch(typeof(PlayerControllerB))]
    public class HoverTextPatch
    {
        private static string previousItemName = ""; // Track the previously displayed item name

        [HarmonyPatch("SetHoverTipAndCurrentInteractTrigger")]
        [HarmonyPostfix]
        public static void Postfix(PlayerControllerB __instance)
        {
            if (__instance.cursorTip == null) return;

            // If we're in a special state, clear the previous item name and return
            if (__instance.isGrabbingObjectAnimation || __instance.inSpecialMenu || __instance.quickMenuManager.isMenuOpen)
            {
                previousItemName = "";
                return;
            }

            Ray interactRay = new Ray(__instance.gameplayCamera.transform.position,
                                    __instance.gameplayCamera.transform.forward);

            if (Physics.Raycast(interactRay, out RaycastHit hit, __instance.grabDistance, 64))
            {
                if (hit.collider.CompareTag("PhysicsProp"))
                {
                    GrabbableObject grabbableObject = hit.collider.gameObject.GetComponent<GrabbableObject>();
                    if (grabbableObject != null)
                    {
                        string itemName = grabbableObject.itemProperties.itemName;

                        // Check inventory space
                        bool hasEmptySlot = false;
                        for (int i = 0; i < __instance.ItemSlots.Length; i++)
                        {
                            if (__instance.ItemSlots[i] == null)
                            {
                                hasEmptySlot = true;
                                break;
                            }
                        }

                        // Handle inventory full case
                        if (!hasEmptySlot)
                        {
                            __instance.cursorTip.text = $"Inventory full! Cannot grab {itemName}";
                            previousItemName = itemName; // Update previous item name
                            return;
                        }

                        // Handle line of sight check
                        if (Physics.Linecast(__instance.gameplayCamera.transform.position,
                                           grabbableObject.transform.position,
                                           1073741824, QueryTriggerInteraction.Ignore))
                        {
                            if (!string.IsNullOrEmpty(previousItemName))
                            {
                                Utilities.SpeakText($"No longer looking at {previousItemName}");
                                previousItemName = "";
                            }
                            return;
                        }

                        // Handle pre-game state
                        if (!GameNetworkManager.Instance.gameHasStarted &&
                            !grabbableObject.itemProperties.canBeGrabbedBeforeGameStart &&
                            StartOfRound.Instance.testRoom == null)
                        {
                            __instance.cursorTip.text = $"Cannot hold {itemName} until ship has landed";
                            previousItemName = itemName; // Update previous item name
                            return;
                        }

                        // Handle custom grab tooltip
                        if (!string.IsNullOrEmpty(grabbableObject.customGrabTooltip))
                        {
                            __instance.cursorTip.text = $"{grabbableObject.customGrabTooltip} ({itemName})";
                        }
                        else
                        {
                            __instance.cursorTip.text = $"Grab {itemName} : [E], ";
                        }

                        // Add weight information if significant
                        if (grabbableObject.itemProperties.weight > 1f)
                        {
                            __instance.cursorTip.text += $" - Weight: {grabbableObject.itemProperties.weight}";
                        }

                        // Add two-handed information
                        if (grabbableObject.itemProperties.twoHanded)
                        {
                            __instance.cursorTip.text += " (Two-Handed)";
                        }

                        // Add value information if it has scrap value
                        if (grabbableObject.scrapValue > 0)
                        {
                            __instance.cursorTip.text += $" - Value: ${grabbableObject.scrapValue}";
                        }

                        previousItemName = itemName; // Update previous item name
                    }
                }
                else
                {
                    // If we're not looking at a PhysicsProp but were previously looking at an item
                    if (!string.IsNullOrEmpty(previousItemName))
                    {
                        Utilities.SpeakText($"No longer looking at {previousItemName}");
                        previousItemName = "";
                    }
                }
            }
            else
            {
                // If we're not looking at anything but were previously looking at an item
                if (!string.IsNullOrEmpty(previousItemName))
                {
                    Utilities.SpeakText($"No longer looking at {previousItemName}");
                    previousItemName = "";
                }
            }
        }
    }
}