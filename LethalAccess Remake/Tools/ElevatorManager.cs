using System;
using System.Collections.Generic;
using LethalAccess;
using UnityEngine;

public class ElevatorManager
{
    private const string ELEVATOR_CATEGORY = "Elevator";
    private NavMenu navMenu;
    private float scanRadius = 80f;

    public ElevatorManager(NavMenu navMenu)
    {
        this.navMenu = navMenu;
    }

    public void UpdateElevatorObjects()
    {
        // Check if elevator category exists and clear items if it does
        if (navMenu.menuItems.ContainsKey(ELEVATOR_CATEGORY))
        {
            navMenu.menuItems[ELEVATOR_CATEGORY].Clear();
        }
        else
        {
            // Create new category and add it to the menu
            navMenu.menuItems[ELEVATOR_CATEGORY] = new List<string>();

            int unlabeledIndex = navMenu.categories.IndexOf("Unlabeled Nearby Objects");
            if (unlabeledIndex != -1)
            {
                // Insert after the unlabeled category
                navMenu.categories.Insert(unlabeledIndex + 1, ELEVATOR_CATEGORY);
            }
            else
            {
                // Just add at the end if unlabeled category doesn't exist
                navMenu.categories.Add(ELEVATOR_CATEGORY);
            }
        }

        // Find the elevator controller
        MineshaftElevatorController elevatorController = UnityEngine.Object.FindObjectOfType<MineshaftElevatorController>();
        if (elevatorController == null || elevatorController.elevatorInsidePoint == null)
        {
            return;
        }

        // Check if player is in range of the elevator
        Transform playerTransform = LACore.PlayerTransform;
        if (playerTransform == null ||
            Vector3.Distance(playerTransform.position, elevatorController.elevatorInsidePoint.position) > scanRadius)
        {
            return;
        }

        // Get and register elevator objects
        List<GameObject> elevatorObjects = GetElevatorObjects(elevatorController);
        foreach (GameObject obj in elevatorObjects)
        {
            if (obj != null)
            {
                string displayName = GetDisplayName(obj);
                navMenu.RegisterMenuItem(obj.name, displayName, ELEVATOR_CATEGORY, "", null);
            }
        }
    }

    // Created once and repositioned thereafter. Creating them fresh inside
    // UpdateElevatorObjects() - which runs on every refresh of the Elevator category - leaks three
    // GameObjects per refresh, because nothing destroys the previous set.
    private GameObject topButtonMarker;
    private GameObject bottomButtonMarker;
    private GameObject controlButtonMarker;

    private GameObject EnsureMarker(ref GameObject marker, string name)
    {
        if (marker == null)
        {
            marker = new GameObject(name);
        }
        return marker;
    }

    private List<GameObject> GetElevatorObjects(MineshaftElevatorController elevator)
    {
        List<GameObject> elevatorObjects = new List<GameObject>();
        Vector3 basePosition = elevator.elevatorInsidePoint.position;

        // Define offsets for buttons based on elevator position
        Vector3 topButtonOffset;
        Vector3 bottomButtonOffset;

        if (elevator.elevatorIsAtBottom)
        {
            topButtonOffset = new Vector3(-1.71f, 38.55f, 2.18f);
            bottomButtonOffset = new Vector3(-2.34f, 1.43f, -1.42f);
        }
        else
        {
            topButtonOffset = new Vector3(-1.71f, 1.23f, 2.19f);
            bottomButtonOffset = new Vector3(-2.42f, -36f, -1.45f);
        }

        // Control button offset (relative to elevator inside point)
        Vector3 controlButtonLocalPosition = new Vector3(-0.49f, 1.81f, -1.12f);

        // Top button
        GameObject topButton = EnsureMarker(ref topButtonMarker, "TopElevatorButton");
        topButton.transform.SetParent(null, false);
        topButton.transform.position = basePosition + topButtonOffset;
        elevatorObjects.Add(topButton);

        // Bottom button
        GameObject bottomButton = EnsureMarker(ref bottomButtonMarker, "BottomElevatorButton");
        bottomButton.transform.SetParent(null, false);
        bottomButton.transform.position = basePosition + bottomButtonOffset;
        elevatorObjects.Add(bottomButton);

        // Control button inside elevator
        GameObject controlButton = EnsureMarker(ref controlButtonMarker, "ElevatorControlButton");
        controlButton.transform.SetParent(elevator.elevatorInsidePoint, false);
        controlButton.transform.localPosition = controlButtonLocalPosition;
        controlButton.transform.localRotation = Quaternion.identity;
        elevatorObjects.Add(controlButton);

        // Add the elevator inside point itself
        GameObject insidePoint = elevator.elevatorInsidePoint.gameObject;
        if (insidePoint != null && !elevatorObjects.Contains(insidePoint))
        {
            elevatorObjects.Add(insidePoint);
        }

        return elevatorObjects;
    }

    private string GetDisplayName(GameObject obj)
    {
        if (obj == null)
        {
            return "Unknown Object";
        }

        if (obj.name.Equals("TopElevatorButton", StringComparison.OrdinalIgnoreCase))
        {
            return "Top Elevator Button";
        }

        if (obj.name.Equals("ElevatorControlButton", StringComparison.OrdinalIgnoreCase))
        {
            return "Elevator Control Button";
        }

        if (obj.name.Equals("BottomElevatorButton", StringComparison.OrdinalIgnoreCase))
        {
            return "Bottom Elevator Button";
        }

        if (obj.name.Equals("InsideElevatorPoint", StringComparison.OrdinalIgnoreCase))
        {
            return "Elevator Entry Point";
        }

        return obj.name;
    }
}