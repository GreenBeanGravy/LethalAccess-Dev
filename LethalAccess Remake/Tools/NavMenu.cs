using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using Green.LethalAccessPlugin;
using DunGen;
using System.Threading;

namespace Green.LethalAccessPlugin
{
    /// <summary>
    /// NavMenu handles in-game navigation menus for locations, items, and other game objects.
    /// This class does NOT handle UI accessibility - that is now managed by UIAccessibilityManager.
    /// </summary>
    public class NavMenu : MonoBehaviour
    {
        private const float ScanRadius = 80f;
        private const string ItemsCategoryName = "Items";
        private const string UnlabeledCategoryName = "Unlabeled Nearby Objects";
        private const string ElevatorCategoryName = "Elevator";

        public Dictionary<string, List<string>> menuItems = new Dictionary<string, List<string>>();
        private Dictionary<string, string> displayNames = new Dictionary<string, string>();
        private Dictionary<string, string> sceneNames = new Dictionary<string, string>();
        private Dictionary<string, Vector3> registeredCoordinates = new Dictionary<string, Vector3>();
        public List<string> categories = new List<string>();
        public Dictionary<string, bool> categoryVisibility = new Dictionary<string, bool>();

        // Track registered menu items
        private static HashSet<string> registeredMenuItems = new HashSet<string>();

        // Dictionary to track actual GameObject references for better ID management
        private Dictionary<string, GameObject> menuItemObjects = new Dictionary<string, GameObject>();

        // Replace previous RoomManager with ElevatorManager.
        private ElevatorManager elevatorManager;

        public struct CategoryItemIndices
        {
            public int categoryIndex;
            public int itemIndex;

            public CategoryItemIndices(int categoryIndex, int itemIndex)
            {
                this.categoryIndex = categoryIndex;
                this.itemIndex = itemIndex;
            }
        }

        public CategoryItemIndices currentIndices = new CategoryItemIndices(0, 0);

        public List<string> ignoreKeywords = new List<string>
        {
            "Floor", "Wall", "Ceiling", "Terrain", "Collider", "collider", "scannode",
            "cube", "plane", "trigger", "placementcollider", "volume", "outofbounds",
            "mesh", "pipe", "hanginglight", "terrainmap", "cylinder", "bone", "elbow",
            "arm", "thigh", "spine", "playerphysicsbox", "shin", "player", "body",
            "placement", "tree", "road", "rock", "optimized", "wall", "container",
            "lineofsight2", "audio", "anomaly", "trigger", "collision", "shadow", "light", "reflectionprobe", "decal",
            "particle", "pointlight", "effect", "controller", "manager", "helper",
            "navmesh", "gamelogic", "post", "camerapoint", "cameraoption", "setting",
            "null", "empty", "placeholder", "spawn", "location", "anchor", "marker",
            "handle", "corner", "edge", "joint", "socket", "reference", "teleport",
            "teleportpoint", "system", "occluder", "occlusionarea", "skybox", "terrain",
            "grass", "vegetation", "foliage", "audio", "sound", "reverb", "echo", "sfx"
        };

        // Check if an item is a registered menu item
        public static bool IsRegisteredMenuItem(string gameObjectName)
        {
            return registeredMenuItems.Contains(gameObjectName);
        }

        public void Initialize()
        {
            try
            {
                Debug.Log("NavMenu: Initializing input actions.");

                // Register keybinds
                LethalAccess.LethalAccessPlugin.Instance.RegisterKeybind("MoveToNextItem", UnityEngine.InputSystem.Key.RightBracket, MoveToNextItem);
                LethalAccess.LethalAccessPlugin.Instance.RegisterKeybind("MoveToPreviousItem", UnityEngine.InputSystem.Key.LeftBracket, MoveToPreviousItem);
                LethalAccess.LethalAccessPlugin.Instance.RegisterKeybind("MoveToNextCategory", UnityEngine.InputSystem.Key.Equals, MoveToNextCategory);
                LethalAccess.LethalAccessPlugin.Instance.RegisterKeybind("MoveToPreviousCategory", UnityEngine.InputSystem.Key.Minus, MoveToPreviousCategory);
                LethalAccess.LethalAccessPlugin.Instance.RegisterKeybind("RefreshCurrentCategory", UnityEngine.InputSystem.Key.Semicolon, RefreshCurrentCategory);

                // Initialize base categories
                menuItems[ItemsCategoryName] = new List<string>();
                categories.Add(ItemsCategoryName);
                categoryVisibility[ItemsCategoryName] = true;

                menuItems[UnlabeledCategoryName] = new List<string>();
                categories.Add(UnlabeledCategoryName);
                categoryVisibility[UnlabeledCategoryName] = true;

                // Initialize Elevator category
                menuItems[ElevatorCategoryName] = new List<string>();
                // Insert Elevator category after Unlabeled Nearby Objects
                int unlabeledIndex = categories.IndexOf(UnlabeledCategoryName);
                if (unlabeledIndex != -1)
                    categories.Insert(unlabeledIndex + 1, ElevatorCategoryName);
                else
                    categories.Add(ElevatorCategoryName);
                categoryVisibility[ElevatorCategoryName] = true;

                // Initialize ElevatorManager (handles only the elevator)
                elevatorManager = new ElevatorManager(this);

                Debug.Log("NavMenu: Input actions and elevator management initialized.");
            }
            catch (Exception e)
            {
                Debug.LogError($"NavMenu: Error during initialization: {e.Message}");
            }
        }

        // Instead of refreshing room contents, we refresh the Elevator objects.
        public void RefreshRoomContents()
        {
            if (elevatorManager != null)
            {
                elevatorManager.UpdateElevatorObjects();
            }
        }

        public void RemoveItem(string uniqueKey, string category)
        {
            try
            {
                if (menuItems.TryGetValue(category, out var items))
                {
                    items.Remove(uniqueKey);

                    // Also remove from tracked objects
                    if (menuItemObjects.ContainsKey(uniqueKey))
                    {
                        GameObject obj = menuItemObjects[uniqueKey];
                        // Mark as destroyed in the ItemIdentifier
                        if (obj != null)
                        {
                            ItemIdentifier.MarkDestroyed(obj);
                        }
                        menuItemObjects.Remove(uniqueKey);
                    }

                    Debug.Log($"Removed item '{uniqueKey}' from category '{category}'");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error removing item: {ex.Message}");
            }
        }

        public void RefreshMenu()
        {
            currentIndices.itemIndex = 0;
            SpeakCategoryAndFirstItem();
        }

        // Create a unique key that includes the unique identifier for the GameObject
        private string CreateUniqueKey(string gameObjectName, GameObject gameObject)
        {
            if (gameObject == null)
                return gameObjectName;

            int uniqueId = ItemIdentifier.GetUniqueId(gameObjectName, gameObject);
            if (uniqueId > 0)
            {
                return $"{gameObjectName}_{uniqueId}";
            }
            return gameObjectName;
        }

        public void RegisterMenuItem(string gameObjectName, string displayName, string category, string sceneName = "", Vector3? coordinates = null, GameObject gameObject = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(gameObjectName))
                {
                    Debug.LogError("NavMenu: gameObjectName cannot be null or empty.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    Debug.LogError("NavMenu: displayName cannot be null or empty.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(category))
                {
                    Debug.LogError("NavMenu: category cannot be null or empty.");
                    return;
                }

                gameObjectName = gameObjectName.Trim();
                displayName = displayName.Trim();
                category = category.Trim();

                // Create a unique key for this specific GameObject instance
                string uniqueKey = gameObjectName;
                if (gameObject != null)
                {
                    uniqueKey = CreateUniqueKey(gameObjectName, gameObject);
                    // Store the GameObject reference with the unique key
                    menuItemObjects[uniqueKey] = gameObject;
                }

                // Track this as a registered menu item
                registeredMenuItems.Add(uniqueKey);

                displayNames[uniqueKey] = displayName;
                if (!string.IsNullOrWhiteSpace(sceneName))
                {
                    sceneNames[uniqueKey] = sceneName.Trim();
                }

                if (!menuItems.ContainsKey(category))
                {
                    menuItems[category] = new List<string>();
                    if (category == UnlabeledCategoryName)
                        categories.Add(category);
                    else
                    {
                        int unlabeledIndex = categories.IndexOf(UnlabeledCategoryName);
                        if (unlabeledIndex != -1)
                            categories.Insert(unlabeledIndex, category);
                        else
                            categories.Add(category);
                    }
                    categoryVisibility[category] = true;
                    Debug.Log($"NavMenu: Added new category: {category}");
                }

                if (menuItems[category].Contains(uniqueKey))
                {
                    Debug.LogWarning($"NavMenu: Item '{uniqueKey}' already exists in category '{category}'. Skipping addition.");
                    return;
                }

                menuItems[category].Add(uniqueKey);
                if (coordinates.HasValue)
                {
                    registeredCoordinates[uniqueKey] = coordinates.Value;
                    Debug.Log($"NavMenu: Registered new item '{displayName}' ({uniqueKey}) at {coordinates.Value} in category '{category}'.");
                }
                else
                {
                    Debug.Log($"NavMenu: Registered new item '{displayName}' ({uniqueKey}) in category '{category}'.");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"NavMenu: Error registering menu item: {e.Message}");
            }
        }

        private void AnnounceItemWithDetails(string category, string uniqueKey, GameObject gameObject = null)
        {
            try
            {
                if (gameObject == null)
                {
                    gameObject = FindGameObjectByUniqueKey(uniqueKey);
                }

                string displayName = GetDisplayNameForObject(gameObject, uniqueKey);
                float distance = 0f;

                if (gameObject != null && LethalAccess.LethalAccessPlugin.PlayerTransform != null)
                {
                    distance = PathValidationExtension.GetPathDistance(
                        LethalAccess.LethalAccessPlugin.PlayerTransform.position,
                        gameObject.transform.position
                    );
                }

                List<string> items = menuItems[category];
                int totalItems = items.Count;
                int currentIndex = items.IndexOf(uniqueKey) + 1;

                string distanceText = distance > 0
                    ? $", {distance:F1} meters away"
                    : "";

                string indexText = totalItems > 0
                    ? $", {currentIndex} of {totalItems}"
                    : "";

                if (gameObject != null)
                {
                    Utilities.SpeakText($"{category}, {displayName}{distanceText}{indexText}");
                    LethalAccess.LethalAccessPlugin.currentLookTarget = gameObject;
                }
                else
                {
                    string fallback = displayNames.ContainsKey(uniqueKey) ? displayNames[uniqueKey] : uniqueKey;
                    Utilities.SpeakText($"{category}, {fallback}, Not found nearby{indexText}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in AnnounceItemWithDetails: {ex.Message}");
                Utilities.SpeakText("Error announcing item details");
            }
        }

        private void MoveToNextItem()
        {
            try
            {
                if (categories.Count > 0)
                {
                    string currentCategory = categories[currentIndices.categoryIndex];
                    List<string> items = menuItems[currentCategory];
                    if (items.Count > 0)
                    {
                        currentIndices.itemIndex = (currentIndices.itemIndex + 1) % items.Count;
                        string uniqueKey = items[currentIndices.itemIndex];
                        UnityMainThreadDispatcher.Instance().Enqueue(() =>
                        {
                            AnnounceItemWithDetails(currentCategory, uniqueKey);
                        });
                    }
                    else
                    {
                        Utilities.SpeakText($"No items in category {currentCategory}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in MoveToNextItem: {ex.Message}");
                Utilities.SpeakText("Error moving to next item");
            }
        }

        private void MoveToPreviousItem()
        {
            try
            {
                if (categories.Count > 0)
                {
                    string currentCategory = categories[currentIndices.categoryIndex];
                    List<string> items = menuItems[currentCategory];
                    if (items.Count > 0)
                    {
                        currentIndices.itemIndex = (currentIndices.itemIndex - 1 + items.Count) % items.Count;
                        string uniqueKey = items[currentIndices.itemIndex];
                        UnityMainThreadDispatcher.Instance().Enqueue(() =>
                        {
                            AnnounceItemWithDetails(currentCategory, uniqueKey);
                        });
                    }
                    else
                    {
                        Utilities.SpeakText($"No items in category {currentCategory}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in MoveToPreviousItem: {ex.Message}");
                Utilities.SpeakText("Error moving to previous item");
            }
        }

        private async void MoveToNextCategory()
        {
            try
            {
                if (categories.Count <= 0)
                {
                    Debug.Log("No categories available");
                    return;
                }

                // Save current category for comparison
                int originalCategoryIndex = currentIndices.categoryIndex;
                int loopProtection = 0;

                do
                {
                    // Increment with protection against collection changes
                    currentIndices.categoryIndex = (currentIndices.categoryIndex + 1) % Mathf.Max(1, categories.Count);

                    // Protect against infinite loops if no categories are visible
                    loopProtection++;
                    if (loopProtection > categories.Count + 1)
                    {
                        Debug.LogWarning("Loop protection triggered in MoveToNextCategory - no visible categories found");
                        Utilities.SpeakText("No accessible categories available");
                        return;
                    }
                }
                while (categories.Count > 0 &&
                       currentIndices.categoryIndex < categories.Count &&
                       (!categoryVisibility.TryGetValue(categories[currentIndices.categoryIndex], out bool isVisible) || !isVisible));

                // Validate category index is still valid after potential collection changes
                if (currentIndices.categoryIndex >= categories.Count)
                {
                    currentIndices.categoryIndex = 0;
                }

                // Only refresh if we actually changed category
                if (currentIndices.categoryIndex != originalCategoryIndex)
                {
                    currentIndices.itemIndex = 0;
                    try
                    {
                        // Safely get current category
                        string currentCategory = categories.Count > currentIndices.categoryIndex ?
                                                 categories[currentIndices.categoryIndex] : null;

                        if (!string.IsNullOrEmpty(currentCategory))
                        {
                            await RefreshCategory(currentCategory);
                            AnnounceCurrentCategoryAndFirstItem();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error refreshing category: {ex.Message}\n{ex.StackTrace}");
                        Utilities.SpeakText("Error loading category content");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in MoveToNextCategory: {ex.Message}\n{ex.StackTrace}");
                Utilities.SpeakText("Error moving to next category");
            }
        }

        private async void MoveToPreviousCategory()
        {
            try
            {
                if (categories.Count <= 0)
                {
                    Debug.Log("No categories available");
                    return;
                }

                // Save current category for comparison
                int originalCategoryIndex = currentIndices.categoryIndex;
                int loopProtection = 0;

                do
                {
                    // Decrement with protection against collection changes
                    currentIndices.categoryIndex = (currentIndices.categoryIndex - 1 + Mathf.Max(1, categories.Count)) % Mathf.Max(1, categories.Count);

                    // Protect against infinite loops if no categories are visible
                    loopProtection++;
                    if (loopProtection > categories.Count + 1)
                    {
                        Debug.LogWarning("Loop protection triggered in MoveToPreviousCategory - no visible categories found");
                        Utilities.SpeakText("No accessible categories available");
                        return;
                    }
                }
                while (categories.Count > 0 &&
                       currentIndices.categoryIndex < categories.Count &&
                       (!categoryVisibility.TryGetValue(categories[currentIndices.categoryIndex], out bool isVisible) || !isVisible));

                // Validate category index is still valid after potential collection changes
                if (currentIndices.categoryIndex >= categories.Count)
                {
                    currentIndices.categoryIndex = 0;
                }

                // Only refresh if we actually changed category
                if (currentIndices.categoryIndex != originalCategoryIndex)
                {
                    currentIndices.itemIndex = 0;
                    try
                    {
                        // Safely get current category
                        string currentCategory = categories.Count > currentIndices.categoryIndex ?
                                                 categories[currentIndices.categoryIndex] : null;

                        if (!string.IsNullOrEmpty(currentCategory))
                        {
                            await RefreshCategory(currentCategory);
                            AnnounceCurrentCategoryAndFirstItem();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error refreshing category: {ex.Message}\n{ex.StackTrace}");
                        Utilities.SpeakText("Error loading category content");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in MoveToPreviousCategory: {ex.Message}\n{ex.StackTrace}");
                Utilities.SpeakText("Error moving to previous category");
            }
        }

        private void AnnounceCurrentCategoryAndFirstItem()
        {
            try
            {
                if (categories.Count <= 0 || currentIndices.categoryIndex >= categories.Count)
                {
                    Utilities.SpeakText("No categories available");
                    return;
                }

                string currentCategory = categories[currentIndices.categoryIndex];
                if (string.IsNullOrEmpty(currentCategory))
                {
                    Utilities.SpeakText("Unknown category");
                    return;
                }

                if (!menuItems.TryGetValue(currentCategory, out List<string> items) || items == null)
                {
                    Utilities.SpeakText($"{currentCategory}, No items");
                    return;
                }

                if (items.Count <= 0)
                {
                    Utilities.SpeakText($"{currentCategory}, No items");
                    return;
                }

                if (currentIndices.itemIndex >= items.Count)
                {
                    currentIndices.itemIndex = 0;
                }

                if (currentIndices.itemIndex < items.Count)
                {
                    string uniqueKey = items[currentIndices.itemIndex];
                    AnnounceItemWithDetails(currentCategory, uniqueKey);
                }
                else
                {
                    Utilities.SpeakText($"{currentCategory}, No valid items");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in AnnounceCurrentCategoryAndFirstItem: {ex.Message}\n{ex.StackTrace}");
                Utilities.SpeakText("Error announcing category");
            }
        }

        private void AnnounceCategoryAndFirstItem()
        {
            try
            {
                if (categories.Count <= 0 || currentIndices.categoryIndex >= categories.Count)
                {
                    Utilities.SpeakText("No categories available");
                    return;
                }

                string currentCategory = categories[currentIndices.categoryIndex];
                if (string.IsNullOrEmpty(currentCategory))
                {
                    Utilities.SpeakText("Unknown category");
                    return;
                }

                if (!menuItems.TryGetValue(currentCategory, out List<string> items) || items == null)
                {
                    Utilities.SpeakText($"{currentCategory}, No items");
                    return;
                }

                if (items.Count <= 0)
                {
                    Utilities.SpeakText($"{currentCategory}, No items");
                    return;
                }

                if (currentIndices.itemIndex >= items.Count)
                {
                    currentIndices.itemIndex = 0;
                }

                if (currentIndices.itemIndex < items.Count)
                {
                    string uniqueKey = items[currentIndices.itemIndex];
                    AnnounceItemWithDetails(currentCategory, uniqueKey);
                }
                else
                {
                    Utilities.SpeakText($"{currentCategory}, No valid items");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in AnnounceCategoryAndFirstItem: {ex.Message}\n{ex.StackTrace}");
                Utilities.SpeakText("Error announcing category");
            }
        }

        private void SetCurrentLookTarget(GameObject target)
        {
            try
            {
                if (target != null)
                {
                    LethalAccess.LethalAccessPlugin.currentLookTarget = target;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in SetCurrentLookTarget: {ex.Message}");
            }
        }

        private async void RefreshCurrentCategory()
        {
            try
            {
                if (categories.Count > 0)
                {
                    string currentCategory = categories[currentIndices.categoryIndex];
                    await RefreshCategory(currentCategory);
                    currentIndices.itemIndex = 0;
                    AnnounceCurrentCategoryAndFirstItem();
                    SetCurrentLookTargetToFirstItem();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in RefreshCurrentCategory: {ex.Message}");
                Utilities.SpeakText("Error refreshing category");
            }
        }

        public async Task RefreshCategory(string category)
        {
            if (string.IsNullOrEmpty(category))
            {
                Debug.LogError("RefreshCategory called with null or empty category name");
                return;
            }

            try
            {
                List<string> items = new List<string>();
                Dictionary<string, GameObject> temporaryObjectMap = new Dictionary<string, GameObject>();

                if (category == ElevatorCategoryName)
                {
                    if (elevatorManager != null)
                    {
                        try
                        {
                            elevatorManager.UpdateElevatorObjects();

                            // Safely get items from the elevator category
                            if (menuItems.TryGetValue(ElevatorCategoryName, out var elevatorItems))
                            {
                                items = new List<string>(elevatorItems);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"Error updating elevator objects: {ex.Message}");
                        }
                    }
                }
                else if (category == ItemsCategoryName)
                {
                    try
                    {
                        var result = await ScanForItemsAsync();
                        items = result.Item1;
                        temporaryObjectMap = result.Item2;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error scanning for items: {ex.Message}");
                        // Create empty results to continue
                        items = new List<string>();
                        temporaryObjectMap = new Dictionary<string, GameObject>();
                    }
                }
                else if (category == UnlabeledCategoryName)
                {
                    try
                    {
                        var result = await ScanForUnlabeledObjectsAsync();
                        items = result.Item1;
                        temporaryObjectMap = result.Item2;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error scanning for unlabeled objects: {ex.Message}");
                        // Create empty results to continue
                        items = new List<string>();
                        temporaryObjectMap = new Dictionary<string, GameObject>();
                    }
                }
                else
                {
                    if (menuItems.TryGetValue(category, out var existing) && existing != null)
                    {
                        // Create a copy to avoid reference issues
                        items = new List<string>(existing);
                    }
                }

                try
                {
                    await UnityMainThreadDispatcher.Instance().EnqueueAsync(() =>
                    {
                        // Add extra null checks here since we're dispatching to the main thread
                        // and the game state might have changed
                        if (menuItems == null)
                        {
                            Debug.LogError("menuItems dictionary is null");
                            menuItems = new Dictionary<string, List<string>>();
                        }

                        // Ensure the category exists
                        if (!menuItems.ContainsKey(category))
                        {
                            menuItems[category] = new List<string>();
                        }

                        try
                        {
                            // Remove any items that no longer exist (for dynamic categories)
                            if (category == ItemsCategoryName || category == UnlabeledCategoryName)
                            {
                                List<string> itemsToRemove = new List<string>();
                                List<string> currentCategoryItems = menuItems[category];

                                foreach (var oldItem in currentCategoryItems)
                                {
                                    if (!items.Contains(oldItem))
                                    {
                                        itemsToRemove.Add(oldItem);
                                    }
                                }

                                // Now safely remove items and cleanup references
                                foreach (var itemToRemove in itemsToRemove)
                                {
                                    try
                                    {
                                        if (menuItemObjects != null && menuItemObjects.TryGetValue(itemToRemove, out GameObject obj) && obj != null)
                                        {
                                            ItemIdentifier.MarkDestroyed(obj);
                                            menuItemObjects.Remove(itemToRemove);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.LogError($"Error cleaning up item reference: {ex.Message}");
                                    }
                                }
                            }

                            // Add new object references (with null checks)
                            if (temporaryObjectMap != null && menuItemObjects != null)
                            {
                                foreach (var kvp in temporaryObjectMap)
                                {
                                    if (!string.IsNullOrEmpty(kvp.Key) && kvp.Value != null)
                                    {
                                        menuItemObjects[kvp.Key] = kvp.Value;
                                    }
                                }
                            }

                            // Finally, update the items list
                            menuItems[category] = items;
                            currentIndices.itemIndex = 0;
                            Debug.Log($"Refreshed category '{category}' with {items.Count} items.");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"Error during menuItems update on main thread: {ex.Message}\n{ex.StackTrace}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error dispatching to main thread: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in RefreshCategory: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private async Task<(List<string>, Dictionary<string, GameObject>)> ScanForItemsAsync()
        {
            return await Task.Run(() =>
            {
                List<string> items = new List<string>();
                Dictionary<string, GameObject> objectMap = new Dictionary<string, GameObject>();

                try
                {
                    if (LethalAccess.LethalAccessPlugin.PlayerTransform == null)
                        return (items, objectMap);

                    Vector3 playerPos = LethalAccess.LethalAccessPlugin.PlayerTransform.position;

                    // Perform physics operations in batches to prevent overloading
                    Collider[] colliders = new Collider[0];
                    try
                    {
                        colliders = Physics.OverlapSphere(playerPos, ScanRadius);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error in OverlapSphere: {ex.Message}");
                        return (items, objectMap);
                    }

                    DepositItemsDesk depositItemsDesk = null;
                    try
                    {
                        depositItemsDesk = UnityEngine.Object.FindObjectOfType<DepositItemsDesk>();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error finding DepositItemsDesk: {ex.Message}");
                        // Continue without deposit desk reference
                    }

                    List<Collider> validColliders = new List<Collider>();

                    // First pass - filter colliders (with individual try/catch for each)
                    foreach (Collider c in colliders)
                    {
                        try
                        {
                            if (c == null || c.gameObject == null)
                                continue;

                            // Check if object has PhysicsProp tag
                            if (!c.gameObject.CompareTag("PhysicsProp"))
                                continue;

                            // Get ANY component that inherits from GrabbableObject
                            GrabbableObject grabbable = c.GetComponent<GrabbableObject>();
                            if (grabbable == null)
                                continue;

                            // Validate grabbable properties
                            if (grabbable.isHeld || grabbable.isPocketed || grabbable.deactivated)
                                continue;

                            // Check deposit desk
                            if (depositItemsDesk != null && depositItemsDesk.itemsOnCounter.Contains(grabbable))
                                continue;

                            // Skip path validation for initial filtering to improve performance
                            validColliders.Add(c);
                        }
                        catch (Exception ex)
                        {
                            // Log but continue with other colliders
                            Debug.LogError($"Error processing collider: {ex.Message}");
                        }
                    }

                    // Second pass - sort and process in manageable batches
                    int batchSize = 20; // Process in smaller batches
                    int totalBatches = Mathf.CeilToInt((float)validColliders.Count / batchSize);

                    for (int batchIndex = 0; batchIndex < totalBatches; batchIndex++)
                    {
                        int startIdx = batchIndex * batchSize;
                        int endIdx = Mathf.Min(startIdx + batchSize, validColliders.Count);

                        for (int i = startIdx; i < endIdx; i++)
                        {
                            try
                            {
                                Collider collider = validColliders[i];
                                if (collider == null || collider.gameObject == null)
                                    continue;

                                // Now do more expensive path validation
                                if (!PathValidationExtension.ShouldIncludeObject(collider.gameObject, playerPos, true))
                                    continue;

                                GrabbableObject grabbable = collider.GetComponent<GrabbableObject>();
                                if (grabbable == null)
                                    continue;

                                string gameObjectName = collider.gameObject.name;

                                // Get the item name directly from the object's properties
                                string itemDisplayName = "";
                                if (grabbable.itemProperties != null)
                                {
                                    itemDisplayName = grabbable.itemProperties.itemName;
                                }

                                // If the properties don't have a name, use a simple fallback
                                if (string.IsNullOrEmpty(itemDisplayName))
                                {
                                    itemDisplayName = ItemIdentifier.GetBaseName(gameObjectName);
                                }

                                // Get the unique ID using the actual GameObject reference
                                int id = ItemIdentifier.GetUniqueId(gameObjectName, collider.gameObject);
                                string finalDisplayName = id > 0 ? $"{itemDisplayName} {id}" : itemDisplayName;

                                // Create a unique key for this specific GameObject instance
                                string uniqueKey = id > 0 ? $"{gameObjectName}_{id}" : gameObjectName;

                                items.Add(uniqueKey);
                                objectMap[uniqueKey] = collider.gameObject;

                                UnityMainThreadDispatcher.Instance().Enqueue(() =>
                                {
                                    try
                                    {
                                        RegisterMenuItem(gameObjectName, finalDisplayName, ItemsCategoryName, gameObject: collider.gameObject);
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.LogError($"Error registering menu item: {ex.Message}");
                                    }
                                });
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"Error processing item in batch: {ex.Message}");
                                // Continue with other items
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Global error in ScanForItemsAsync: {ex.Message}\n{ex.StackTrace}");
                }

                return (items, objectMap);
            });
        }

        private async Task<(List<string>, Dictionary<string, GameObject>)> ScanForUnlabeledObjectsAsync()
        {
            return await Task.Run(() =>
            {
                List<string> objects = new List<string>();
                Dictionary<string, GameObject> objectMap = new Dictionary<string, GameObject>();

                try
                {
                    if (LethalAccess.LethalAccessPlugin.PlayerTransform == null)
                        return (objects, objectMap);

                    Vector3 playerPos = LethalAccess.LethalAccessPlugin.PlayerTransform.position;

                    // OPTIMIZATION: Reduce scan radius for unlabeled objects to improve performance
                    float optimizedScanRadius = ScanRadius * 0.8f; // 80% of original radius

                    // Cache all objects in the scene once instead of using physics calls which are expensive
                    List<GameObject> nearbyObjects = new List<GameObject>();

                    // Queue this operation to run on main thread
                    bool objectsFetched = false;
                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        try
                        {
                            // Find objects efficiently using Scene's root objects
                            // This is faster than FindObjectsOfType or physics operations
                            var rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();

                            foreach (var rootObj in rootObjects)
                            {
                                // Skip ignored objects early
                                if (ShouldIgnoreObject(rootObj.name))
                                    continue;

                                // Only add if within rough distance
                                Vector3 rootPos = rootObj.transform.position;
                                float roughDistance = Vector3.Distance(playerPos, rootPos);

                                if (roughDistance <= optimizedScanRadius * 1.2f) // Add some margin
                                {
                                    nearbyObjects.Add(rootObj);

                                    // Also get immediate children for better coverage
                                    foreach (Transform child in rootObj.transform)
                                    {
                                        if (child != null && !ShouldIgnoreObject(child.name))
                                        {
                                            float childRoughDistance = Vector3.Distance(playerPos, child.position);
                                            if (childRoughDistance <= optimizedScanRadius * 1.2f)
                                            {
                                                nearbyObjects.Add(child.gameObject);
                                            }
                                        }
                                    }
                                }
                            }

                            objectsFetched = true;
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"Error fetching objects: {ex.Message}");
                            objectsFetched = true;
                        }
                    });

                    // Wait for objects to be fetched on main thread
                    while (!objectsFetched)
                    {
                        Thread.Sleep(5);
                    }

                    // Process the objects in batches
                    int batchSize = 15; // Smaller batch size for more responsive updates
                    List<GameObject> sortedObjects = nearbyObjects.OrderBy(obj => {
                        try
                        {
                            return Vector3.Distance(playerPos, obj.transform.position);
                        }
                        catch
                        {
                            return float.MaxValue;
                        }
                    }).ToList();

                    int totalBatches = Mathf.CeilToInt((float)sortedObjects.Count / batchSize);
                    int maxProcessedObjects = 75; // Cap the number of processed objects for performance
                    int processedCount = 0;

                    for (int batchIndex = 0; batchIndex < totalBatches && processedCount < maxProcessedObjects; batchIndex++)
                    {
                        int startIdx = batchIndex * batchSize;
                        int endIdx = Mathf.Min(startIdx + batchSize, sortedObjects.Count);

                        for (int i = startIdx; i < endIdx && processedCount < maxProcessedObjects; i++)
                        {
                            try
                            {
                                GameObject obj = sortedObjects[i];
                                if (obj == null)
                                    continue;

                                // Skip objects with obvious ignore keywords in name
                                if (ShouldIgnoreObject(obj.name))
                                    continue;

                                // OPTIMIZATION: Use lightweight distance check before expensive path validation
                                float distance = Vector3.Distance(playerPos, obj.transform.position);
                                if (distance > optimizedScanRadius)
                                    continue;

                                // OPTIMIZATION: Skip objects with too many children (likely complex objects)
                                if (obj.transform.childCount > 20)
                                    continue;

                                // Now do more expensive path validation
                                bool validPath = false;

                                // Queue path validation on main thread
                                bool validationComplete = false;
                                UnityMainThreadDispatcher.Instance().Enqueue(() =>
                                {
                                    try
                                    {
                                        validPath = PathValidationExtension.ShouldIncludeObject(obj, playerPos, true);
                                    }
                                    catch { }
                                    validationComplete = true;
                                });

                                // Wait for validation to complete
                                while (!validationComplete)
                                {
                                    Thread.Sleep(1);
                                }

                                if (!validPath)
                                    continue;

                                string gameObjectName = obj.name;

                                // Get the unique ID for this GameObject instance
                                int uniqueId = 0;
                                bool idGenerated = false;

                                UnityMainThreadDispatcher.Instance().Enqueue(() =>
                                {
                                    try
                                    {
                                        uniqueId = ItemIdentifier.GetUniqueId(gameObjectName, obj);
                                        idGenerated = true;
                                    }
                                    catch
                                    {
                                        idGenerated = true;
                                    }
                                });

                                while (!idGenerated)
                                {
                                    Thread.Sleep(1);
                                }

                                // Create a unique key for this specific GameObject instance
                                string uniqueKey = uniqueId > 0 ? $"{gameObjectName}_{uniqueId}" : gameObjectName;

                                // Get unique display name
                                string uniqueDisplayName = "";
                                bool nameGenerated = false;

                                UnityMainThreadDispatcher.Instance().Enqueue(() =>
                                {
                                    try
                                    {
                                        uniqueDisplayName = ItemIdentifier.GetDisplayName(gameObjectName, obj);
                                        nameGenerated = true;
                                    }
                                    catch
                                    {
                                        uniqueDisplayName = gameObjectName;
                                        nameGenerated = true;
                                    }
                                });

                                while (!nameGenerated)
                                {
                                    Thread.Sleep(1);
                                }

                                objects.Add(uniqueKey);
                                objectMap[uniqueKey] = obj;

                                UnityMainThreadDispatcher.Instance().Enqueue(() =>
                                {
                                    try
                                    {
                                        RegisterMenuItem(gameObjectName, uniqueDisplayName, UnlabeledCategoryName, gameObject: obj);
                                    }
                                    catch { }
                                });

                                processedCount++;
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"Error processing unlabeled item in batch: {ex.Message}");
                            }
                        }

                        // Small pause between batches to prevent thread hogging
                        Thread.Sleep(2);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Global error in ScanForUnlabeledObjectsAsync: {ex.Message}\n{ex.StackTrace}");
                }

                return (objects, objectMap);
            });
        }

        private bool ShouldIgnoreObject(string objectName)
        {
            // Convert to lowercase once for comparison
            string lowerName = objectName.ToLower();

            // Quick reject for common ignore patterns
            if (lowerName.Contains("collider") || lowerName.Contains("trigger") ||
                lowerName.Contains("volume") || lowerName.Contains("mesh") ||
                lowerName.Contains("floor") || lowerName.Contains("wall") ||
                lowerName.Contains("ceiling"))
            {
                return true;
            }

            // Check against ignore keywords list
            foreach (var keyword in ignoreKeywords)
            {
                if (lowerName.Contains(keyword.ToLower()))
                {
                    return true;
                }
            }

            return false;
        }

        private void SpeakCategoryAndFirstItem()
        {
            try
            {
                if (categories.Count <= 0 || currentIndices.categoryIndex >= categories.Count)
                {
                    Utilities.SpeakText("No categories available");
                    return;
                }

                string currentCategory = categories[currentIndices.categoryIndex];
                if (string.IsNullOrEmpty(currentCategory))
                {
                    Utilities.SpeakText("Unknown category");
                    return;
                }

                if (!menuItems.TryGetValue(currentCategory, out List<string> items) || items == null)
                {
                    Utilities.SpeakText($"{currentCategory}, No items");
                    return;
                }

                if (items.Count <= 0)
                {
                    Utilities.SpeakText($"{currentCategory}, No items");
                    return;
                }

                if (currentIndices.itemIndex >= items.Count)
                {
                    currentIndices.itemIndex = 0;
                }

                if (currentIndices.itemIndex < items.Count)
                {
                    string uniqueKey = items[currentIndices.itemIndex];
                    AnnounceItemWithDetails(currentCategory, uniqueKey);
                }
                else
                {
                    Utilities.SpeakText($"{currentCategory}, No valid items");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in SpeakCategoryAndFirstItem: {ex.Message}");
                Utilities.SpeakText("Error announcing category");
            }
        }

        private void SetCurrentLookTargetToFirstItem()
        {
            try
            {
                if (categories.Count > 0)
                {
                    string currentCategory = categories[currentIndices.categoryIndex];
                    if (menuItems.ContainsKey(currentCategory) && menuItems[currentCategory].Count > 0)
                    {
                        string uniqueKey = menuItems[currentCategory][0];
                        UnityMainThreadDispatcher.Instance().Enqueue(() =>
                        {
                            GameObject gameObject = FindGameObjectByUniqueKey(uniqueKey);
                            if (gameObject != null)
                            {
                                LethalAccess.LethalAccessPlugin.currentLookTarget = gameObject;
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in SetCurrentLookTargetToFirstItem: {ex.Message}");
            }
        }

        private GameObject FindGameObjectByUniqueKey(string uniqueKey)
        {
            if (string.IsNullOrEmpty(uniqueKey))
            {
                return null;
            }

            // First check our tracked GameObject references - this is the primary lookup method
            if (menuItemObjects != null && menuItemObjects.TryGetValue(uniqueKey, out GameObject trackedObj) && trackedObj != null)
            {
                return trackedObj;
            }

            // If we didn't find it in menuItemObjects, try to parse the unique key
            string baseGameObjectName = uniqueKey;
            int uniqueId = 0;

            // Check if the uniqueKey contains an ID part
            int underscoreIndex = uniqueKey.LastIndexOf('_');
            if (underscoreIndex > 0 && underscoreIndex < uniqueKey.Length - 1)
            {
                string idPart = uniqueKey.Substring(underscoreIndex + 1);
                if (int.TryParse(idPart, out uniqueId))
                {
                    baseGameObjectName = uniqueKey.Substring(0, underscoreIndex);
                }
            }

            for (int retry = 0; retry <= 2; retry++)
            {
                try
                {
                    // Scene-specific search
                    if (sceneNames != null && sceneNames.TryGetValue(uniqueKey, out string sceneName) && !string.IsNullOrEmpty(sceneName))
                    {
                        try
                        {
                            UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneByName(sceneName);
                            if (scene.isLoaded)
                            {
                                GameObject[] objectsInScene = scene.GetRootGameObjects();
                                foreach (GameObject obj in objectsInScene)
                                {
                                    try
                                    {
                                        if (obj == null) continue;

                                        GameObject foundObject = null;
                                        Transform foundTrans = obj.transform.Find(baseGameObjectName);
                                        if (foundTrans != null)
                                        {
                                            foundObject = foundTrans.gameObject;

                                            // If we have a uniqueId, verify it's the right instance
                                            if (uniqueId > 0)
                                            {
                                                int objId = ItemIdentifier.GetUniqueId(baseGameObjectName, foundObject);
                                                if (objId != uniqueId) continue;
                                            }
                                        }

                                        if (foundObject != null)
                                        {
                                            // Store reference for next time
                                            if (menuItemObjects != null)
                                            {
                                                menuItemObjects[uniqueKey] = foundObject;
                                            }
                                            return foundObject;
                                        }
                                    }
                                    catch (Exception) { continue; }
                                }
                            }
                        }
                        catch (Exception) { /* Continue to next search method */ }
                    }

                    // General search for instances that match the name and id
                    try
                    {
                        GameObject[] allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
                        foreach (GameObject obj in allObjects)
                        {
                            if (obj != null && obj.name == baseGameObjectName)
                            {
                                // If we have a uniqueId, verify it's the right instance
                                if (uniqueId > 0)
                                {
                                    int objId = ItemIdentifier.GetUniqueId(baseGameObjectName, obj);
                                    if (objId != uniqueId) continue;
                                }

                                // Store reference for next time
                                if (menuItemObjects != null)
                                {
                                    menuItemObjects[uniqueKey] = obj;
                                }
                                return obj;
                            }
                        }
                    }
                    catch (Exception) { /* Continue to coordinates fallback */ }

                    // Coordinates fallback
                    if (registeredCoordinates != null && registeredCoordinates.TryGetValue(uniqueKey, out Vector3 coordinates))
                    {
                        try
                        {
                            GameObject newObject = new GameObject(baseGameObjectName);
                            newObject.transform.position = coordinates;

                            // Store reference for next time
                            if (menuItemObjects != null)
                            {
                                menuItemObjects[uniqueKey] = newObject;
                            }

                            Debug.Log($"Created new GameObject '{baseGameObjectName}' at {coordinates}");
                            return newObject;
                        }
                        catch (Exception) { /* Final fallback failed */ }
                    }

                    // Only log warning on final retry
                    if (retry == 2)
                    {
                        Debug.LogWarning($"GameObject '{uniqueKey}' not found after 3 attempts.");
                    }

                    // Brief pause between retries to allow potential loading
                    if (retry < 2)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error in FindGameObjectByUniqueKey (retry {retry}): {ex.Message}");

                    // Only do pause on non-final attempts
                    if (retry < 2)
                    {
                        System.Threading.Thread.Sleep(50);
                    }
                }
            }

            return null;
        }

        public string GetDisplayNameForObject(GameObject gameObject, string uniqueKey = null)
        {
            try
            {
                // Check if we have a stored display name for this unique key
                if (!string.IsNullOrEmpty(uniqueKey) && displayNames.TryGetValue(uniqueKey, out string storedName))
                {
                    return storedName;
                }

                // Get the object name, either from the GameObject or from the current menu item
                string objectName;
                if (gameObject != null)
                {
                    objectName = gameObject.name;
                }
                else if (!string.IsNullOrEmpty(uniqueKey))
                {
                    // Extract base name from uniqueKey (remove _ID suffix if present)
                    int underscoreIndex = uniqueKey.LastIndexOf('_');
                    if (underscoreIndex > 0)
                    {
                        objectName = uniqueKey.Substring(0, underscoreIndex);
                    }
                    else
                    {
                        objectName = uniqueKey;
                    }
                }
                else if (currentIndices.categoryIndex < categories.Count)
                {
                    string currentCategory = categories[currentIndices.categoryIndex];
                    if (currentIndices.itemIndex < menuItems[currentCategory].Count)
                    {
                        objectName = menuItems[currentCategory][currentIndices.itemIndex];
                    }
                    else
                    {
                        return "Unknown Object";
                    }
                }
                else
                {
                    return "Unknown Object";
                }

                // Let the ItemIdentifier handle naming with IDs, passing the actual GameObject reference
                return ItemIdentifier.GetDisplayName(objectName, gameObject);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in GetDisplayNameForObject: {ex.Message}");
                return "Unknown Object";
            }
        }

        public void HideCategory(string category)
        {
            if (categoryVisibility.ContainsKey(category))
            {
                categoryVisibility[category] = false;
            }
        }

        public void UnhideCategory(string category)
        {
            if (categoryVisibility.ContainsKey(category))
            {
                categoryVisibility[category] = true;
            }
        }

        private Dictionary<string, bool> previousCategoryVisibility = new Dictionary<string, bool>();

        public void UpdateCategoriesVisibility(bool isShipLanded, string currentPlanetName)
        {
            try
            {
                if (isShipLanded)
                {
                    UpdateCategoryVisibility("Factory", currentPlanetName != "71 Gordion");
                    UpdateCategoryVisibility("Company Building", currentPlanetName == "71 Gordion");
                }
                else
                {
                    UpdateCategoryVisibility("Factory", false);
                    UpdateCategoryVisibility("Company Building", false);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in UpdateCategoriesVisibility: {ex.Message}");
            }
        }

        private void UpdateCategoryVisibility(string category, bool shouldBeVisible)
        {
            try
            {
                if (!previousCategoryVisibility.ContainsKey(category) || previousCategoryVisibility[category] != shouldBeVisible)
                {
                    if (shouldBeVisible)
                    {
                        UnhideCategory(category);
                        Utilities.SpeakText($"{category} category is now available.");
                    }
                    else
                    {
                        HideCategory(category);
                        Utilities.SpeakText($"{category} category is now hidden.");
                    }
                    previousCategoryVisibility[category] = shouldBeVisible;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in UpdateCategoryVisibility: {ex.Message}");
            }
        }
    }
}