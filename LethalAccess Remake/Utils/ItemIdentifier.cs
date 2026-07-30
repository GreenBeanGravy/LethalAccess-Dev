using System.Collections.Generic;
using UnityEngine;
using System;

namespace LethalAccess
{
    public static class ItemIdentifier
    {
        // Dictionary mapping base names to their next available ID (starts from 1)
        private static Dictionary<string, int> nextAvailableIds = new Dictionary<string, int>();

        // Dictionary mapping Unity instanceIDs to our assigned IDs
        private static Dictionary<int, int> instanceIdToAssignedId = new Dictionary<int, int>();

        // Dictionary mapping base names to sets of assigned IDs for that type
        private static Dictionary<string, HashSet<int>> assignedIdsByType = new Dictionary<string, HashSet<int>>();

        // Dictionary to track what base name an instance belongs to (for cleanup)
        private static Dictionary<int, string> instanceIdToBaseName = new Dictionary<int, string>();

        // Objects that should never receive index values
        private static HashSet<string> excludedFromIndexing = new HashSet<string>
        {
            "EntranceTeleportA",
            "EntranceTeleportA(Clone)",
            "EntranceTeleportB",
            "EntranceTeleportB(Clone)",
            "TerminalScript",
            "StartGameLever",
            "ShipInside",
            "StorageCloset",
            "Bunkbeds",
            "LightSwitch",
            "ItemShip",
            "RedButton",
            "BellDinger",
            "ItemCounter",
            "PlacementBlocker (5)"
        };

        // Per-frame cache of "how many active GameObjects share this base name".
        private static Dictionary<string, int> baseNameCounts = new Dictionary<string, int>();
        private static int baseNameCountsFrame = -1;

        private static int GetBaseNameCount(string baseName)
        {
            int frame = Time.frameCount;
            if (frame != baseNameCountsFrame)
            {
                baseNameCountsFrame = frame;
                baseNameCounts.Clear();

                GameObject[] allObjects = GameObject.FindObjectsOfType<GameObject>();
                foreach (var obj in allObjects)
                {
                    if (obj == null)
                        continue;

                    string key = GetBaseName(obj.name);
                    baseNameCounts.TryGetValue(key, out int existing);
                    baseNameCounts[key] = existing + 1;
                }
            }

            baseNameCounts.TryGetValue(baseName, out int count);
            return count;
        }

        /// <summary>
        /// Clear all assigned identifiers. Called at the start of a round.
        ///
        /// These dictionaries are static and keyed by GetInstanceID(), and nothing used to clear
        /// them, so they grew for the lifetime of the process. Worse, Unity reuses instance ids
        /// after objects are destroyed, so a stale entry could make a fresh item inherit another
        /// item's number and be announced as e.g. "Key 3" when it is not.
        /// </summary>
        public static void ResetForNewRound()
        {
            nextAvailableIds.Clear();
            instanceIdToAssignedId.Clear();
            assignedIdsByType.Clear();
            instanceIdToBaseName.Clear();
            baseNameCounts.Clear();
            baseNameCountsFrame = -1;
            Debug.Log("[LethalAccess] ItemIdentifier state reset for new round.");
        }

        // Get or create a unique ID for an item
        public static int GetUniqueId(string gameObjectName, GameObject gameObject = null)
        {
            if (gameObject == null)
                return 0;

            // Check if this object type should be excluded from indexing
            if (excludedFromIndexing.Contains(gameObjectName))
            {
                return 0;
            }

            int instanceId = gameObject.GetInstanceID();

            // Check if we already have an ID for this exact object instance
            if (instanceIdToAssignedId.TryGetValue(instanceId, out int existingId))
            {
                return existingId;
            }

            // Get base name
            string baseName = GetBaseName(gameObjectName);

            // Check if the base name should be excluded from indexing
            if (excludedFromIndexing.Contains(baseName))
            {
                instanceIdToAssignedId[instanceId] = 0;
                instanceIdToBaseName[instanceId] = baseName;
                return 0;
            }

            // Count how many instances of this type exist, from the per-frame cache.
            // Do not reach for GameObject.FindObjectsOfType<GameObject>() here: it materialises a
            // managed array of every active GameObject in the scene - tens of thousands inside a
            // generated interior - and this runs once per item being named, so a category refresh
            // costs O(items x sceneObjects). The cached counts give identical numbering in one pass.
            bool hasMultipleInstances = GetBaseNameCount(baseName) > 1;

            // If this is the only instance, don't assign an ID
            if (!hasMultipleInstances)
            {
                instanceIdToAssignedId[instanceId] = 0;
                instanceIdToBaseName[instanceId] = baseName;
                return 0;
            }

            // Get the next available ID for this type
            if (!nextAvailableIds.TryGetValue(baseName, out int nextId))
            {
                nextId = 1;  // Start from 1 as requested
            }

            // Make sure we have a set for tracking assigned IDs for this type
            if (!assignedIdsByType.TryGetValue(baseName, out HashSet<int> assignedIds))
            {
                assignedIds = new HashSet<int>();
                assignedIdsByType[baseName] = assignedIds;
            }

            // Find the first available ID (should be nextId, but handles edge cases)
            while (assignedIds.Contains(nextId))
            {
                nextId++;
            }

            // Assign and store the ID
            assignedIds.Add(nextId);
            instanceIdToAssignedId[instanceId] = nextId;
            instanceIdToBaseName[instanceId] = baseName;

            // Update the next available ID
            nextAvailableIds[baseName] = nextId + 1;

            Debug.Log($"Assigned ID {nextId} to {gameObjectName} (base: {baseName}, instanceId: {instanceId})");

            return nextId;
        }

        // Mark an object as destroyed
        public static void MarkDestroyed(GameObject gameObject)
        {
            if (gameObject == null)
                return;

            int instanceId = gameObject.GetInstanceID();

            // If this instance doesn't have an assigned ID, nothing to do
            if (!instanceIdToAssignedId.TryGetValue(instanceId, out int assignedId))
                return;

            // Get the base name
            if (!instanceIdToBaseName.TryGetValue(instanceId, out string baseName))
            {
                baseName = GetBaseName(gameObject.name);
            }

            // Remove the ID from the assigned IDs for this type
            if (assignedIdsByType.TryGetValue(baseName, out HashSet<int> assignedIds))
            {
                assignedIds.Remove(assignedId);
                Debug.Log($"Removed ID {assignedId} from destroyed {baseName} object");
            }

            // Remove the instance mappings
            instanceIdToAssignedId.Remove(instanceId);
            instanceIdToBaseName.Remove(instanceId);
        }

        // Extract base name
        public static string GetBaseName(string gameObjectName)
        {
            string baseName = gameObjectName.Replace("(Clone)", "").Trim();
            if (baseName.EndsWith("Item"))
            {
                baseName = baseName.Substring(0, baseName.Length - 4);
            }
            return baseName;
        }

        // Get display name
        public static string GetDisplayName(string gameObjectName, GameObject gameObject = null)
        {
            string baseName = GetBaseName(gameObjectName);
            int id = 0;

            if (gameObject != null)
            {
                id = GetUniqueId(gameObjectName, gameObject);
            }

            if (id > 0)
            {
                return $"{baseName} {id}";
            }
            else
            {
                return baseName;
            }
        }

        // Debugging method - can remove in production
        public static void LogAssignedIds()
        {
            foreach (var kvp in assignedIdsByType)
            {
                Debug.Log($"Type: {kvp.Key}, Assigned IDs: {string.Join(", ", kvp.Value)}");
            }
        }
    }
}