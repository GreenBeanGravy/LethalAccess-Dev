using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using Green.LethalAccessPlugin;
using LethalAccess;
using UnityEngine;
using UnityEngine.InputSystem;

public class InstantNavigationManager
{
    private LethalAccessPlugin plugin;
    private Dictionary<string, ConfigEntry<Key>> keybindConfigEntries = new Dictionary<string, ConfigEntry<Key>>();
    private Dictionary<string, ConfigEntry<string>> destinationConfigEntries = new Dictionary<string, ConfigEntry<string>>();
    private LethalAccessPlugin lethalAccessPlugin;
    private static readonly Dictionary<string, Key> defaultKeys = new Dictionary<string, Key>
    {
        { "InstantNavigation1", Key.Digit1 },
        { "InstantNavigation2", Key.Digit2 },
        { "InstantNavigation3", Key.Digit3 },
        { "InstantNavigation4", Key.Digit4 },
        { "InstantNavigation5", Key.Digit5 },
        { "InstantNavigation6", Key.Digit6 }
    };

    private static readonly Dictionary<string, string> defaultDestinations = new Dictionary<string, string>
    {
        { "InstantNavigation1", "EntranceTeleportA" },
        { "InstantNavigation2", "EntranceTeleportA(Clone)" },
        { "InstantNavigation3", "ShipInside" },
        { "InstantNavigation4", "NearestItem" },
        { "InstantNavigation5", "" },
        { "InstantNavigation6", "" }
    };

    public InstantNavigationManager(LethalAccessPlugin plugin)
    {
        this.plugin = plugin;
        RegisterConfigEntries();
        RegisterKeybinds();
        Debug.Log("InstantNavigationManager initialized with 6 navigation shortcuts");
    }

    private void RegisterConfigEntries()
    {
        foreach (KeyValuePair<string, Key> defaultKey in defaultKeys)
        {
            string keybindName = defaultKey.Key;
            Key defaultValue = defaultKey.Value;
            ConfigEntry<Key> configEntry = plugin.Config.Bind<Key>("InstantNavigation_Keys", keybindName, defaultValue, "Key for " + keybindName + " instant navigation");
            keybindConfigEntries[keybindName] = configEntry;
        }

        foreach (KeyValuePair<string, string> defaultDestination in defaultDestinations)
        {
            string destinationName = defaultDestination.Key;
            string defaultValue = defaultDestination.Value;
            ConfigEntry<string> configEntry = plugin.Config.Bind<string>("InstantNavigation_Destinations", destinationName, defaultValue, "Destination for " + destinationName + ". Use exact object name or 'NearestItem' for the nearest item.");
            destinationConfigEntries[destinationName] = configEntry;
        }
    }

    private void RegisterKeybinds()
    {
        foreach (KeyValuePair<string, ConfigEntry<Key>> keybindConfigEntry in keybindConfigEntries)
        {
            string keybindName = keybindConfigEntry.Key;
            Key keyValue = keybindConfigEntry.Value.Value;
            plugin.RegisterKeybind(keybindName, keyValue, delegate
            {
                NavigateToConfiguredDestination(keybindName);
            });
        }
    }

    private void NavigateToConfiguredDestination(string navigationKey)
    {
        if (LethalAccessPlugin.PlayerTransform == null || GameNetworkManager.Instance?.localPlayerController == null)
        {
            Utilities.SpeakText("Cannot navigate yet - player not initialized");
            return;
        }

        if (StartOfRound.Instance == null || !StartOfRound.Instance.shipHasLanded)
        {
            Utilities.SpeakText("Cannot navigate until ship has landed");
            return;
        }

        if (!destinationConfigEntries.TryGetValue(navigationKey, out ConfigEntry<string> destination) || string.IsNullOrEmpty(destination.Value))
        {
            Utilities.SpeakText("No destination configured for " + navigationKey);
            return;
        }

        string destinationValue = destination.Value;
        if (destinationValue.Equals("NearestItem", StringComparison.OrdinalIgnoreCase))
        {
            NavigateToNearestItem();
        }
        else
        {
            NavigateToNamedDestination(destinationValue);
        }
    }

    private void NavigateToNearestItem()
    {
        if (LethalAccessPlugin.PlayerTransform == null)
        {
            Utilities.SpeakText("Unable to find player position");
            return;
        }

        Vector3 playerPosition = LethalAccessPlugin.PlayerTransform.position;
        float closestDistance = float.MaxValue;
        GameObject closestItem = null;

        try
        {
            GameObject[] physicsProps = GameObject.FindGameObjectsWithTag("PhysicsProp");
            foreach (GameObject prop in physicsProps)
            {
                if (prop == null)
                    continue;

                try
                {
                    GrabbableObject grabbable = prop.GetComponent<GrabbableObject>();
                    if (grabbable != null && !grabbable.isHeld && !grabbable.isPocketed && !grabbable.deactivated)
                    {
                        float distance = Vector3.Distance(playerPosition, prop.transform.position);
                        if (distance < closestDistance)
                        {
                            closestDistance = distance;
                            closestItem = prop;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("Error processing item: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Error finding nearest item: " + ex.Message);
            Utilities.SpeakText("Error finding nearest item");
            return;
        }

        if (closestItem != null)
        {
            string itemName = "Unknown Item";
            try
            {
                GrabbableObject grabbable = closestItem.GetComponent<GrabbableObject>();
                if (grabbable != null && grabbable.itemProperties != null)
                {
                    itemName = grabbable.itemProperties.itemName;
                    if (string.IsNullOrEmpty(itemName))
                    {
                        itemName = grabbable.itemProperties.itemName;
                    }
                }

                if (string.IsNullOrEmpty(itemName))
                {
                    itemName = closestItem.name.Replace("(Clone)", "");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("Error getting item name: " + ex.Message);
                itemName = closestItem.name.Replace("(Clone)", "");
            }

            Utilities.SpeakText("Navigating to nearest item: " + itemName);
            LethalAccessPlugin.currentLookTarget = closestItem;
            RegisterObjectWithNavMenu(closestItem, itemName, true);
            NavigateToTarget(closestItem);
        }
        else
        {
            Utilities.SpeakText("No items found nearby");
        }
    }

    private void RegisterObjectWithNavMenu(GameObject obj, string displayName, bool isItem = false)
    {
        try
        {
            if (plugin.navMenu != null)
            {
                string category = isItem ? "Items" : DetermineCategory(obj.name);
                plugin.navMenu.RegisterMenuItem(obj.name, displayName, category, "", null, obj);
                Debug.Log("Registered " + obj.name + " as '" + displayName + "' in category '" + category + "'");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Error registering object with NavMenu: " + ex.Message);
        }
    }

    private void NavigateToNamedDestination(string destinationName)
    {
        GameObject targetObject = null;
        try
        {
            targetObject = GameObject.Find(destinationName);
            if (targetObject == null)
            {
                GameObject[] allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
                foreach (GameObject obj in allObjects)
                {
                    if (obj != null && obj.name == destinationName)
                    {
                        targetObject = obj;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Error finding destination object: " + ex.Message);
            Utilities.SpeakText("Error finding destination");
            return;
        }

        if (targetObject != null)
        {
            string friendlyName = GetFriendlyName(destinationName);
            Utilities.SpeakText("Navigating to " + friendlyName);
            LethalAccessPlugin.currentLookTarget = targetObject;
            RegisterObjectWithNavMenu(targetObject, friendlyName);
            NavigateToTarget(targetObject);
        }
        else
        {
            Utilities.SpeakText("Destination " + destinationName + " not found");
        }
    }

    private string GetFriendlyName(string objectName)
    {
        switch (objectName)
        {
            case "EntranceTeleportA":
                return "Factory Entrance";
            case "EntranceTeleportA(Clone)":
                return "Factory Exit";
            case "EntranceTeleportB":
                return "Fire Escape Entrance";
            case "EntranceTeleportB(Clone)":
                return "Fire Escape Exit";
            case "TerminalScript":
                return "Terminal";
            case "StartGameLever":
                return "Ship Start Lever";
            case "ShipInside":
                return "Inside of Ship";
            case "StorageCloset":
                return "Storage Closet";
            case "Bunkbeds":
                return "Bunk Beds";
            case "LightSwitch":
                return "Light Switch";
            case "ItemShip":
                return "Item Ship";
            case "RedButton":
                return "Teleporter Button";
            case "BellDinger":
                return "Sell Bell";
            case "ItemCounter":
                return "Item Counter";
            default:
                return objectName;
        }
    }

    private string DetermineCategory(string objectName)
    {
        if (objectName.Contains("Factory") || objectName.Contains("Entrance") ||
            objectName == "EntranceTeleportA" || objectName == "EntranceTeleportA(Clone)" ||
            objectName == "EntranceTeleportB" || objectName == "EntranceTeleportB(Clone)")
        {
            return "Factory";
        }

        if (objectName.Contains("Ship") || objectName == "TerminalScript" ||
            objectName == "StartGameLever" || objectName == "ShipInside")
        {
            return "Ship";
        }

        if (objectName.Contains("Closet") || objectName == "Bunkbeds" || objectName == "LightSwitch")
        {
            return "Ship Utilities";
        }

        if (objectName.Contains("Bell") || objectName.Contains("Counter") ||
            objectName == "BellDinger" || objectName == "ItemCounter")
        {
            return "Company Building";
        }

        return "Other Utilities";
    }

    private void NavigateToTarget(GameObject target)
    {
        try
        {
            if (LethalAccessPlugin.PlayerTransform == null)
            {
                Utilities.SpeakText("Player not initialized, cannot navigate");
                return;
            }

            Pathfinder pathfinder = LethalAccessPlugin.PlayerTransform.GetComponent<Pathfinder>();
            if (pathfinder == null)
            {
                pathfinder = LethalAccessPlugin.PlayerTransform.gameObject.AddComponent<Pathfinder>();
            }

            if (pathfinder != null)
            {
                pathfinder.NavigateTo(target);
            }
            else
            {
                Utilities.SpeakText("Could not initialize pathfinder component");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Error navigating to target: " + ex.Message);
            Utilities.SpeakText("Error starting navigation");
        }
    }
}