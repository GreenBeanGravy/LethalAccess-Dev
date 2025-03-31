using UnityEngine;
using UnityEngine.AI;
using System.Collections.Generic;
using System;

namespace Green.LethalAccessPlugin
{
    public static class PathValidationExtension
    {
        // Increased vertical difference for registered items to handle wall objects better
        private static readonly float registeredMaxVerticalDiff = 45f;
        private static readonly float registeredPathLengthMultiplier = 2.5f;

        // Configuration variables for unlabeled/dynamic objects
        private static readonly float unlabeledMaxVerticalDiff = 20f;
        private static readonly float unlabeledSamplingDistance = 10f;
        private static readonly float unlabeledPathLengthMultiplier = 2f;

        private static readonly HashSet<string> whitelistedObjects = new HashSet<string>
        {
            "TerminalScript",
            "StartGameLever",
            "ShipInside",
            "StorageCloset",
            "Bunkbeds",
            "LightSwitch",
            "ItemShip",
            "RedButton",
            "EntranceTeleportA",
            "EntranceTeleportA(Clone)",
            "EntranceTeleportB",
            "EntranceTeleportB(Clone)",
            "BellDinger",
            "ItemCounter"
        };

        public static bool IsPathValid(Vector3 startPos, Vector3 targetPos, float maxPathLength = 100f, bool isUnlabeled = false, bool isRegisteredItem = false)
        {
            try
            {
                NavMeshPath path = new NavMeshPath();

                // For registered items and whitelisted objects, be more permissive
                if (isRegisteredItem)
                {
                    float verticalDiff = Mathf.Abs(targetPos.y - startPos.y);
                    if (verticalDiff > registeredMaxVerticalDiff)
                    {
                        Debug.Log($"Registered item vertical difference too high: {verticalDiff} > {registeredMaxVerticalDiff}");
                        return false;
                    }

                    // Try direct path calculation first
                    bool pathFound = NavMesh.CalculatePath(startPos, targetPos, NavMesh.AllAreas, path);

                    // If direct path fails, try with sampling
                    if (!pathFound || path.status != NavMeshPathStatus.PathComplete)
                    {
                        NavMeshHit startHitReg, endHitReg;
                        bool startValid = NavMesh.SamplePosition(startPos, out startHitReg, 5f, NavMesh.AllAreas);
                        bool endValid = NavMesh.SamplePosition(targetPos, out endHitReg, 5f, NavMesh.AllAreas);

                        if (!startValid || !endValid)
                        {
                            // For registered items, we'll create a special case handling
                            // Let's just allow this path despite the NavMesh issues
                            Debug.Log($"Allowing registered item despite NavMesh sampling issues: {startValid}, {endValid}");
                            return true;
                        }

                        pathFound = NavMesh.CalculatePath(startHitReg.position, endHitReg.position, NavMesh.AllAreas, path);
                        if (!pathFound)
                        {
                            Debug.Log("Path calculation failed for registered item even with sampling");
                            // Still allow for registered items
                            return true;
                        }
                    }

                    // Be lenient with path status for registered items
                    if (path.status != NavMeshPathStatus.PathComplete)
                    {
                        Debug.Log($"Allowing registered item with partial path: {path.status}");
                        // For registered items, allow partial paths too
                        return true;
                    }

                    // Check path length with increased multiplier for registered items
                    float actualMaxPathLength = maxPathLength * registeredPathLengthMultiplier;
                    float pathLength = CalculatePathLength(path);
                    if (pathLength > actualMaxPathLength)
                    {
                        Debug.Log($"Path too long for registered item: {pathLength} > {actualMaxPathLength}");
                        return false;
                    }

                    return true;
                }

                // For non-registered items, do more standard NavMesh check
                float maxVerticalDiff = isUnlabeled ? unlabeledMaxVerticalDiff : registeredMaxVerticalDiff;
                float samplingDistance = isUnlabeled ? unlabeledSamplingDistance : unlabeledSamplingDistance;
                float pathLengthMultiplier = isUnlabeled ? unlabeledPathLengthMultiplier : registeredPathLengthMultiplier;

                // Vertical check
                float vertDiff = Mathf.Abs(targetPos.y - startPos.y);
                if (vertDiff > maxVerticalDiff)
                {
                    return false;
                }

                // NavMesh sampling with safe fallbacks
                NavMeshHit startHit = new NavMeshHit();
                NavMeshHit endHit = new NavMeshHit();

                bool startPosValid = NavMesh.SamplePosition(startPos, out startHit, samplingDistance, NavMesh.AllAreas);
                bool endPosValid = NavMesh.SamplePosition(targetPos, out endHit, samplingDistance, NavMesh.AllAreas);

                if (!startPosValid || !endPosValid)
                {
                    return false;
                }

                // Calculate path with exception handling
                bool found = false;
                try
                {
                    found = NavMesh.CalculatePath(startHit.position, endHit.position, NavMesh.AllAreas, path);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"NavMesh.CalculatePath threw exception: {ex.Message}");
                    return false;
                }

                if (!found)
                {
                    return false;
                }

                // Path status check
                if (path.status != NavMeshPathStatus.PathComplete)
                {
                    return false;
                }

                // Path length check
                float actualLength = maxPathLength * pathLengthMultiplier;
                float totalLength = CalculatePathLength(path);
                return totalLength <= actualLength;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in IsPathValid: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static float CalculatePathLength(NavMeshPath path)
        {
            try
            {
                if (path == null || path.corners == null || path.corners.Length < 2)
                {
                    return 0f;
                }

                float totalLength = 0f;
                for (int i = 0; i < path.corners.Length - 1; i++)
                {
                    totalLength += Vector3.Distance(path.corners[i], path.corners[i + 1]);
                }
                return totalLength;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in CalculatePathLength: {ex.Message}");
                return float.MaxValue; // Return max value to likely fail length checks
            }
        }

        public static bool ShouldIncludeObject(GameObject obj, Vector3 playerPos, bool isUnlabeled = false)
        {
            try
            {
                if (obj == null)
                {
                    return false;
                }

                if (whitelistedObjects.Contains(obj.name))
                {
                    return true;
                }

                // Check if this is a manually registered menu item
                bool isRegisteredItem = NavMenu.IsRegisteredMenuItem(obj.name);

                // Extra safety for whitelisted or registered items
                if (isRegisteredItem)
                {
                    return true;
                }

                return IsPathValid(playerPos, obj.transform.position, 100f, isUnlabeled, isRegisteredItem);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in ShouldIncludeObject: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        public static float GetPathDistance(Vector3 startPos, Vector3 targetPos)
        {
            try
            {
                NavMeshPath path = new NavMeshPath();

                // Try direct calculation first
                bool calculated = false;
                try
                {
                    calculated = NavMesh.CalculatePath(startPos, targetPos, NavMesh.AllAreas, path);
                }
                catch (Exception)
                {
                    // Silently handle any exceptions in path calculation
                    calculated = false;
                }

                if (calculated && path.status != NavMeshPathStatus.PathInvalid)
                {
                    float distance = CalculatePathLength(path);
                    if (distance > 0)
                    {
                        return distance;
                    }
                }

                // If that fails, try with sampling
                NavMeshHit navStartHit, navEndHit;
                bool startSampleValid = NavMesh.SamplePosition(startPos, out navStartHit, 5f, NavMesh.AllAreas);
                bool endSampleValid = NavMesh.SamplePosition(targetPos, out navEndHit, 5f, NavMesh.AllAreas);

                if (startSampleValid && endSampleValid)
                {
                    try
                    {
                        calculated = NavMesh.CalculatePath(navStartHit.position, navEndHit.position, NavMesh.AllAreas, path);
                        if (calculated && path.status != NavMeshPathStatus.PathInvalid)
                        {
                            float distance = CalculatePathLength(path);
                            if (distance > 0)
                            {
                                return distance;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Silently handle any exceptions in the second path calculation
                    }
                }

                // Fallback to straight-line distance
                return Vector3.Distance(startPos, targetPos);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in GetPathDistance: {ex.Message}");
                return Vector3.Distance(startPos, targetPos); // Fallback to direct distance
            }
        }
    }
}