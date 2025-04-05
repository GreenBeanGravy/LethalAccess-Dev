using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using DunGen;
using GameNetcodeStuff;
using LethalAccess.Patches;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Key = UnityEngine.InputSystem.Key;

namespace LethalAccess
{
    [BepInPlugin(modGUID, modName, modVersion)]
    public class LACore : BaseUnityPlugin
    {
        private const string modGUID = "Green.LethalAccess";
        private const string modName = "LethalAccess";
        private const string modVersion = "1.0.0.0";

        public static Transform playerTransform;
        public static Transform cameraTransform;
        private const string REACHED_POSITION_SOUND_PATH = "LethalAccessAssets/ReachedPosition.wav";
        private AudioClip reachedPositionSound;
        private static Dictionary<string, ConfigEntry<Key>> keybindConfigEntries = new Dictionary<string, ConfigEntry<Key>>();
        private static Dictionary<string, Action> registeredActions = new Dictionary<string, Action>();
        public static Dictionary<string, List<Func<string>>> overriddenTexts = new Dictionary<string, List<Func<string>>>();
        private List<GameObject> previouslyFocusedUIElements = new List<GameObject>();
        public static GameObject currentLookTarget;
        private static GameObject lastAnnouncedObject;
        public static bool enableCustomKeybinds = true;
        private bool hasPlayedReachedSound = false;
        private bool isNavigatingWithPrevKey = false;
        private bool mainMenuActivated = false;
        private bool quickMenuActivated = false;
        private Dictionary<string, Dictionary<string, string>> customUINavigation = new Dictionary<string, Dictionary<string, string>>();
        private ControlTipAccessPatch controlTipAccessPatch;
        private ProfitQuotaPatch profitQuotaPatch;
        private PlayerHealthPatch playerHealthPatch;
        private TimeOfDayPatch timeOfDayPatch;
        public NavMenu navMenu;
        private Pathfinder pathfinder;
        private NorthSoundManager northSoundManager;
        private TileTracker tileTracker;
        private InstantNavigationManager instantNavigationManager;
        private NavigationAimAssist navigationAimAssist;
        private UIAccessibilityManager uiAccessibilityManager;
        private const float MAX_DISTANCE_TO_OBJECT = 15f;
        private const float DEFAULT_STOPPING_RADIUS = 2.2f;
        private float turnSpeed = 90f;
        private static bool hasLoggedPlayerTransformWarning = false;
        private static float lastPlayerTransformWarningTime = 0f;
        private static readonly float playerTransformWarningInterval = 5f;
        private static bool hasLoggedCameraTransformWarning = false;
        private static float lastCameraTransformWarningTime = 0f;
        private static readonly float cameraTransformWarningInterval = 5f;

        public static TileTracker TileTracker => Instance?.tileTracker;

        public static LACore Instance { get; private set; }

        public static Transform PlayerTransform
        {
            get
            {
                if (playerTransform == null)
                {
                    PlayerControllerB controller = GameNetworkManager.Instance?.localPlayerController;
                    if (controller != null)
                    {
                        playerTransform = controller.transform;
                        hasLoggedPlayerTransformWarning = false;
                    }
                    else
                    {
                        // Only log warnings if we're not in the main menu
                        if (!IsInMainMenu() && !IsInIntroScene())
                        {
                            float realtimeSinceStartup = Time.realtimeSinceStartup;
                            if (!hasLoggedPlayerTransformWarning || realtimeSinceStartup - lastPlayerTransformWarningTime > playerTransformWarningInterval)
                            {
                                Debug.LogWarning("LocalPlayerController is null. Cannot get PlayerTransform.");
                                hasLoggedPlayerTransformWarning = true;
                                lastPlayerTransformWarningTime = realtimeSinceStartup;
                            }
                        }
                    }
                }
                return playerTransform;
            }
        }

        public static Transform CameraTransform
        {
            get
            {
                if (cameraTransform == null)
                {
                    PlayerControllerB controller = GameNetworkManager.Instance?.localPlayerController;
                    if (controller != null && controller.gameplayCamera != null)
                    {
                        cameraTransform = controller.gameplayCamera.transform;
                        hasLoggedCameraTransformWarning = false;
                    }
                    else
                    {
                        // Only log warnings if we're not in the main menu
                        if (!IsInMainMenu() && !IsInIntroScene())
                        {
                            float realtimeSinceStartup = Time.realtimeSinceStartup;
                            if (!hasLoggedCameraTransformWarning || realtimeSinceStartup - lastCameraTransformWarningTime > cameraTransformWarningInterval)
                            {
                                Debug.LogWarning("LocalPlayerController or gameplayCamera is null. Cannot get CameraTransform.");
                                hasLoggedCameraTransformWarning = true;
                                lastCameraTransformWarningTime = realtimeSinceStartup;
                            }
                        }
                    }
                }
                return cameraTransform;
            }
        }

        // Helper booleans for active menu
        private static bool IsInMainMenu()
        {
            Scene currentScene = SceneManager.GetActiveScene();
            return currentScene.name == "MainMenu";
        }

        private static bool IsInIntroScene()
        {
            Scene currentScene = SceneManager.GetActiveScene();
            return currentScene.name == "InitScene" || currentScene.name == "InitSceneLaunchOptions";
        }

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                controlTipAccessPatch = new ControlTipAccessPatch();
                controlTipAccessPatch.Initialize();
                profitQuotaPatch = new ProfitQuotaPatch();
                profitQuotaPatch.Initialize();
                playerHealthPatch = new PlayerHealthPatch();
                playerHealthPatch.Initialize();
                timeOfDayPatch = new TimeOfDayPatch();
                timeOfDayPatch.Initialize();
                navMenu = new NavMenu();
                navMenu.Initialize();
                pathfinder = new Pathfinder();
                northSoundManager = gameObject.AddComponent<NorthSoundManager>();
                tileTracker = gameObject.AddComponent<TileTracker>();
                navigationAimAssist = gameObject.AddComponent<NavigationAimAssist>();
                navigationAimAssist.Initialize();
                Logger.LogInfo("Navigation aim assist initialized.");
                uiAccessibilityManager = gameObject.AddComponent<UIAccessibilityManager>();
                uiAccessibilityManager.Initialize(Logger);
                Harmony harmony = new Harmony("LethalAccess");
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                harmony.PatchAll(typeof(PreInitScenePatch));
                Logger.LogInfo("PreInitScene skip patch applied successfully.");
                Logger.LogInfo("LethalAccess initialized.");
                RegisterKeybinds();
                StartCoroutine(LoadReachedPositionSound());
                RegisterNavigationPoints();
                RegisterUIElements();
                instantNavigationManager = new InstantNavigationManager(this);
            }
            else
            {
                Destroy(this);
            }
        }

        private IEnumerator LoadReachedPositionSound()
        {
            // Load the reached position sound using the same pattern as other audio resources
            yield return StartCoroutine(LoadAudioClip(REACHED_POSITION_SOUND_PATH, clip => reachedPositionSound = clip));
            Debug.Log("Reached position sound loaded successfully");
        }

        private void RegisterNavigationPoints()
        {
            navMenu.RegisterMenuItem("ItemCounter", "Item Counter", "Company Building", "", new Vector3(-29.141f, -1.154f, -31.461f), null);
            navMenu.RegisterMenuItem("BellDinger", "Sell Bell", "Company Building", "CompanyBuilding", null);
            navMenu.RegisterMenuItem("EntranceTeleportA", "Enter Factory", "Factory", "", null);
            navMenu.RegisterMenuItem("EntranceTeleportA(Clone)", "Exit Factory", "Factory", "", null);
            navMenu.RegisterMenuItem("EntranceTeleportB", "Enter Fire Escape", "Factory", "", null);
            navMenu.RegisterMenuItem("EntranceTeleportB(Clone)", "Exit Fire Escape", "Factory", "", null);
            navMenu.RegisterMenuItem("TerminalScript", "Terminal", "Ship", "", null);
            navMenu.RegisterMenuItem("StartGameLever", "Start Ship Lever", "Ship", "", null);
            navMenu.RegisterMenuItem("ShipInside", "Inside of Ship", "Ship", "", null);
            navMenu.RegisterMenuItem("StorageCloset", "Storage Closet", "Ship Utilities", "", null);
            navMenu.RegisterMenuItem("PlacementBlocker (5)", "Charging Station", "Ship Utilities", "", null);
            navMenu.RegisterMenuItem("Bunkbeds", "Bunk Beds", "Ship Utilities", "", null);
            navMenu.RegisterMenuItem("LightSwitch", "Light Switch", "Ship Utilities", "", null);
            navMenu.RegisterMenuItem("ItemShip", "Item Ship", "Other Utilities", "", null);
            navMenu.RegisterMenuItem("RedButton", "Activate Teleporter", "Other Utilities", "", null);
        }

        private void RegisterUIElements()
        {
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/SettingsPanel/MicSettings/SpeakerButton", "Microphone Toggle", "toggle");
            uiAccessibilityManager.RegisterElement("Systems/UI/Canvas/QuickMenu/SettingsPanel/MicSettings/SpeakerButton", "Microphone Toggle", "toggle");
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/SettingsPanel/ControlsOptions/InvertYAxis", "Invert Y-Axis", "checkbox");
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/SettingsPanel/MicSettings/ChooseDevice", "Current Input Device", "toggle");
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/SettingsPanel/ControlsOptions/LookSensitivity/Slider", "Look Sensitivity", "slider");
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/SettingsPanel/MasterVolume/Slider", "Master Volume", "slider");
            uiAccessibilityManager.RegisterElement("Systems/UI/Canvas/QuickMenu/SettingsPanel/ControlsOptions/LookSensitivity/Slider", "Look Sensitivity", "slider");
            uiAccessibilityManager.RegisterElement("Systems/UI/Canvas/QuickMenu/SettingsPanel/MasterVolume/Slider", "Master Volume", "slider");
            uiAccessibilityManager.CreateGroup("MainMenuButtons", "Canvas/MenuContainer/MenuBackground/MainButtons/PlayButton", "Canvas/MenuContainer/MenuBackground/MainButtons/HostButton", "Canvas/MenuContainer/MenuBackground/MainButtons/JoinButton", "Canvas/MenuContainer/MenuBackground/MainButtons/SettingsButton", "Canvas/MenuContainer/MenuBackground/MainButtons/QuitButton");
            uiAccessibilityManager.CreateGroup("SettingsTabButtons", "Canvas/MenuContainer/SettingsPanel/TabButton", "Canvas/MenuContainer/SettingsPanel/TabButton (1)", "Canvas/MenuContainer/SettingsPanel/TabButton (2)");
            uiAccessibilityManager.CreateGroup("FileSelectionButtons", "Canvas/MenuContainer/LobbyHostSettings/FilesPanel/File1", "Canvas/MenuContainer/LobbyHostSettings/FilesPanel/File2", "Canvas/MenuContainer/LobbyHostSettings/FilesPanel/File3", "Canvas/MenuContainer/LobbyHostSettings/FilesPanel/ChallengeMoonButton");
            uiAccessibilityManager.CreateGroup("MicSettings", "Canvas/MenuContainer/SettingsPanel/MicSettings/SpeakerButton", "Canvas/MenuContainer/SettingsPanel/MicSettings/PushToTalkKey", "Canvas/MenuContainer/SettingsPanel/MicSettings/ChooseDevice");

            uiAccessibilityManager.SetCustomText("Canvas/MenuContainer/SettingsPanel/MicSettings/SpeakerButton", new List<Func<string>>
            {
                () => "Microphone Toggle: " + Utilities.GetTextFromGameObject("Canvas/MenuContainer/SettingsPanel/MicSettings/SpeakerButton")
            });

            uiAccessibilityManager.SetCustomText("Systems/UI/Canvas/QuickMenu/SettingsPanel/MicSettings/SpeakerButton", new List<Func<string>>
            {
                () => "Microphone Toggle: " + Utilities.GetTextFromGameObject("Systems/UI/Canvas/QuickMenu/SettingsPanel/MicSettings/SpeakerButton")
            });

            uiAccessibilityManager.SetCustomText("Canvas/MenuContainer/SettingsPanel/ControlsOptions/LookSensitivity/Slider", new List<Func<string>>
            {
                () => string.Format("{0} {1}", Utilities.GetTextFromGameObject("Canvas/MenuContainer/SettingsPanel/ControlsOptions/LookSensitivity/Text (1)"), Utilities.GetSliderValue("Canvas/MenuContainer/SettingsPanel/ControlsOptions/LookSensitivity/Slider/Handle Slide Area/Handle"))
            });

            uiAccessibilityManager.SetCustomText("Canvas/MenuContainer/SettingsPanel/MasterVolume/Slider", new List<Func<string>>
            {
                () => string.Format("{0} {1}%", Utilities.GetTextFromGameObject("Canvas/MenuContainer/SettingsPanel/MasterVolume/Text (1)"), Utilities.GetSliderValue("Canvas/MenuContainer/SettingsPanel/MasterVolume/Slider/Handle Slide Area/Handle"))
            });

            uiAccessibilityManager.SetCustomText("Systems/UI/Canvas/QuickMenu/SettingsPanel/ControlsOptions/LookSensitivity/Slider", new List<Func<string>>
            {
                () => string.Format("{0} {1}", Utilities.GetTextFromGameObject("Systems/UI/Canvas/QuickMenu/SettingsPanel/ControlsOptions/LookSensitivity/Text (1)"), Utilities.GetSliderValue("Systems/UI/Canvas/QuickMenu/SettingsPanel/ControlsOptions/LookSensitivity/Slider/Handle Slide Area/Handle"))
            });

            uiAccessibilityManager.SetCustomText("Systems/UI/Canvas/QuickMenu/SettingsPanel/MasterVolume/Slider", new List<Func<string>>
            {
                () => string.Format("{0} {1}%", Utilities.GetTextFromGameObject("Systems/UI/Canvas/QuickMenu/SettingsPanel/MasterVolume/Text (1)"), Utilities.GetSliderValue("Systems/UI/Canvas/QuickMenu/SettingsPanel/MasterVolume/Slider/Handle Slide Area/Handle"))
            });

            uiAccessibilityManager.SetCustomText("Canvas/MenuContainer/SettingsPanel/MicSettings/ChooseDevice", new List<Func<string>>
            {
                delegate
                {
                    string textFromGameObject = Utilities.GetTextFromGameObject("Canvas/MenuContainer/SettingsPanel/MicSettings/ChooseDevice");
                    return string.IsNullOrEmpty(textFromGameObject) ? "Current Input Device" : textFromGameObject.Replace("\n", " ").Trim();
                }
            });

            uiAccessibilityManager.SetCustomText("Canvas/MenuContainer/SettingsPanel/ControlsOptions/InvertYAxis", new List<Func<string>>
            {
                delegate
                {
                    string result = "Invert Y-Axis";
                    GameObject checkmark = GameObject.Find("Canvas/MenuContainer/SettingsPanel/ControlsOptions/InvertYAxis/Checkmark");
                    if (checkmark != null)
                    {
                        bool activeSelf = checkmark.activeSelf;
                        return result;
                    }
                    return result;
                }
            });

            uiAccessibilityManager.SetCustomText("Canvas/MenuContainer/LobbyList/ListPanel/Scroll View/Viewport/Content/LobbyListItem(Clone)/JoinButton", new List<Func<string>>
            {
                () => Utilities.GetLobbyInfoFromJoinButton(EventSystem.current.currentSelectedGameObject)
            });

            uiAccessibilityManager.SetNavigation("Canvas/MenuContainer/LobbyHostSettings/FilesPanel/ChallengeMoonButton", "Up", "Canvas/MenuContainer/LobbyHostSettings/FilesPanel/File3");
            uiAccessibilityManager.SetNavigation("Canvas/MenuContainer/LobbyHostSettings/FilesPanel/File3", "Down", "Canvas/MenuContainer/LobbyHostSettings/FilesPanel/ChallengeMoonButton");
        }

        private async void Update()
        {
            if (enableCustomKeybinds)
            {
                CheckKeybinds();
            }

            if (currentLookTarget != null && (pathfinder == null || !pathfinder.IsPathfinding))
            {
                Utilities.LookAtObject(currentLookTarget);
            }

            if (tileTracker != null && PlayerTransform != null)
            {
                FieldInfo playerTransformField = typeof(TileTracker).GetField("playerTransform", BindingFlags.Instance | BindingFlags.NonPublic);
                if (playerTransformField != null && playerTransformField.GetValue(tileTracker) == null)
                {
                    tileTracker.Initialize(Logger, PlayerTransform);
                    Logger.LogInfo("TileTracker initialized with player transform");
                }
            }

            bool isShipLanded = StartOfRound.Instance != null && StartOfRound.Instance.shipHasLanded;
            string currentPlanetName = !isShipLanded ? string.Empty : StartOfRound.Instance.currentLevel?.PlanetName ?? string.Empty;

            if (StartOfRound.Instance != null && StartOfRound.Instance.currentLevel == null && isShipLanded)
            {
                currentPlanetName = "Company Building";
            }

            await Task.Run(delegate
            {
                navMenu.UpdateCategoriesVisibility(isShipLanded, currentPlanetName);
                CheckReachedTrackedObject();
                CheckMainMenuActivation();
                CheckQuickMenuActivation();
            });

            // Enhanced turning system with arrow keys and snap turning
            if (GameNetworkManager.Instance?.localPlayerController != null && PlayerTransform != null && CameraTransform != null)
            {
                // Regular turning with bracket keys (existing functionality)
                if (Keyboard.current[Key.LeftBracket].isPressed)
                {
                    SafeRotate(-turnSpeed * Time.deltaTime);
                }
                if (Keyboard.current[Key.RightBracket].isPressed)
                {
                    SafeRotate(turnSpeed * Time.deltaTime);
                }

                // Regular turning with arrow keys
                if (Keyboard.current[Key.LeftArrow].isPressed && !Keyboard.current[Key.LeftShift].isPressed && !Keyboard.current[Key.RightShift].isPressed)
                {
                    SafeRotate(-turnSpeed * Time.deltaTime);
                }
                if (Keyboard.current[Key.RightArrow].isPressed && !Keyboard.current[Key.LeftShift].isPressed && !Keyboard.current[Key.RightShift].isPressed)
                {
                    SafeRotate(turnSpeed * Time.deltaTime);
                }

                // Snap turning with shift+arrow keys
                if ((Keyboard.current[Key.LeftShift].isPressed || Keyboard.current[Key.RightShift].isPressed))
                {
                    if (Keyboard.current[Key.LeftArrow].wasPressedThisFrame)
                    {
                        SnapTurn(-45f);
                    }
                    if (Keyboard.current[Key.RightArrow].wasPressedThisFrame)
                    {
                        SnapTurn(45f);
                    }
                }
            }
        }

        // Safe rotation method to prevent "Look rotation viewing vector is zero" errors
        private void SafeRotate(float yawDegrees)
        {
            if (PlayerTransform == null) return;

            try
            {
                PlayerTransform.Rotate(0f, yawDegrees, 0f);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in SafeRotate: {ex.Message}");
            }
        }

        // New method for snap turning in 45-degree increments
        private void SnapTurn(float degrees)
        {
            if (PlayerTransform == null) return;

            try
            {
                // Get current rotation angle
                Vector3 forward = PlayerTransform.forward;

                // Make sure the forward vector is not zero
                if (forward.magnitude < 0.001f)
                {
                    Debug.LogWarning("Forward vector is too small, using default forward");
                    forward = Vector3.forward; // Use default forward if the vector is too small
                }

                float currentAngle = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                if (currentAngle < 0f) currentAngle += 360f;

                // Calculate new angle with the turn
                float newAngle = currentAngle + degrees;

                // Snap to the nearest 45-degree cardinal direction
                newAngle = Mathf.Round(newAngle / 45f) * 45f;
                if (newAngle >= 360f) newAngle -= 360f;
                if (newAngle < 0f) newAngle += 360f;

                // Apply the rotation safely
                Vector3 newForward = new Vector3(
                    Mathf.Sin(newAngle * Mathf.Deg2Rad),
                    0f,
                    Mathf.Cos(newAngle * Mathf.Deg2Rad)
                );

                if (newForward.magnitude > 0.001f)
                {
                    // Use Quaternion.LookRotation which is safer when vectors are normalized
                    PlayerTransform.rotation = Quaternion.LookRotation(newForward, Vector3.up);
                }
                else
                {
                    Debug.LogWarning("Calculated forward vector is too small, skipping rotation");
                }

                // Announce just the cardinal direction (not the angle)
                string compassDirection = GetCompassDirection(newAngle);
                Utilities.SpeakText(compassDirection);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in SnapTurn: {ex.Message}");
            }
        }

        private void AnnounceCurrentRoom()
        {
            if (tileTracker != null)
            {
                string currentTileName = tileTracker.GetCurrentTileName();
                string pathType = tileTracker.IsOnMainPath() ? "main path" : "branch";
                Utilities.SpeakText("You are in " + currentTileName + ", " + pathType);
            }
            else
            {
                Utilities.SpeakText("Room information not available");
            }
        }

        public string GetCurrentRoomName()
        {
            if (tileTracker != null)
            {
                return tileTracker.GetCurrentTileName();
            }
            return "Unknown";
        }

        private void DrawElevatorRay()
        {
            MineshaftElevatorController elevatorController = FindObjectOfType<MineshaftElevatorController>();
            if (elevatorController == null)
            {
                Utilities.SpeakText("Elevator not found.");
                return;
            }

            Transform elevatorInsidePoint = elevatorController.elevatorInsidePoint;
            if (elevatorInsidePoint == null)
            {
                Utilities.SpeakText("Elevator entry point not found.");
                return;
            }

            Transform camTransform = CameraTransform;
            if (camTransform == null)
            {
                Utilities.SpeakText("Main camera not found.");
                return;
            }

            Vector3 position = camTransform.position;
            Vector3 forward = camTransform.forward;
            float maxDistance = 100f;

            Ray ray = new Ray(position, forward);
            RaycastHit[] hits = Physics.RaycastAll(ray, maxDistance);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            Vector3 hitPoint = position + forward * maxDistance;
            int hitCounter = 0;

            foreach (RaycastHit hit in hits)
            {
                string objectName = hit.collider.gameObject.name.ToLower();
                if (!objectName.Contains("player") && !objectName.Contains("camera"))
                {
                    hitCounter++;
                    if (hitCounter >= 5)
                    {
                        hitPoint = hit.point;
                        break;
                    }
                }
            }

            GameObject lineObj = new GameObject("ElevatorRayLine");
            LineRenderer lineRenderer = lineObj.AddComponent<LineRenderer>();
            lineRenderer.positionCount = 2;
            lineRenderer.SetPosition(0, elevatorInsidePoint.position);
            lineRenderer.SetPosition(1, hitPoint);
            lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            lineRenderer.startColor = Color.green;
            lineRenderer.endColor = Color.green;
            lineRenderer.startWidth = 0.1f;
            lineRenderer.endWidth = 0.1f;

            Destroy(lineObj, 5f);

            Vector3 offset = hitPoint - elevatorInsidePoint.position;
            string message = $"Relative position from elevator base: X={offset.x:F2}, Y={offset.y:F2}, Z={offset.z:F2}";
            Utilities.SpeakText(message);
            Debug.Log(message);
        }

        private void CheckKeybinds()
        {
            foreach (KeyValuePair<string, ConfigEntry<Key>> keybindEntry in keybindConfigEntries)
            {
                if (Keyboard.current[keybindEntry.Value.Value].wasPressedThisFrame)
                {
                    if (registeredActions.TryGetValue(keybindEntry.Key, out Action action))
                    {
                        action();
                    }
                }
                else if (Keyboard.current[keybindEntry.Value.Value].wasReleasedThisFrame)
                {
                    if (keybindEntry.Key == "LeftClickHold")
                    {
                        SimulateMouseClick(0, false);
                    }
                    else if (keybindEntry.Key == "RightClickHold")
                    {
                        SimulateMouseClick(1, false);
                    }
                }
            }
        }

        private void SimulateMouseClick(int buttonIndex, bool isPressed)
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                return;
            }

            MouseState state;
            switch (buttonIndex)
            {
                case 0:
                    if (isPressed)
                    {
                        state = default;
                        InputSystem.QueueStateEvent(mouse, state.WithButton(MouseButton.Left, true), -1.0);
                    }
                    else
                    {
                        state = default;
                        InputSystem.QueueStateEvent(mouse, state.WithButton(MouseButton.Left, false), -1.0);
                    }
                    break;
                case 1:
                    if (isPressed)
                    {
                        state = default;
                        InputSystem.QueueStateEvent(mouse, state.WithButton(MouseButton.Right, true), -1.0);
                    }
                    else
                    {
                        state = default;
                        InputSystem.QueueStateEvent(mouse, state.WithButton(MouseButton.Right, false), -1.0);
                    }
                    break;
            }
        }

        private void FocusPreviousUIElement()
        {
            if (uiAccessibilityManager != null)
            {
                uiAccessibilityManager.NavigateToPreviousElement();
            }
            else if (previouslyFocusedUIElements.Count > 0)
            {
                isNavigatingWithPrevKey = true;
                GameObject previousElement = previouslyFocusedUIElements[previouslyFocusedUIElements.Count - 1];
                previouslyFocusedUIElements.RemoveAt(previouslyFocusedUIElements.Count - 1);
                if (previousElement != null && previousElement.activeInHierarchy)
                {
                    EventSystem.current.SetSelectedGameObject(previousElement);
                }
                isNavigatingWithPrevKey = false;
            }
        }

        public void SetUINavigation(string uiElementPath, string direction, string connectedUIElementPath)
        {
            if (uiAccessibilityManager != null)
            {
                uiAccessibilityManager.SetNavigation(uiElementPath, direction, connectedUIElementPath);
                return;
            }

            if (!customUINavigation.ContainsKey(uiElementPath))
            {
                customUINavigation[uiElementPath] = new Dictionary<string, string>();
            }
            customUINavigation[uiElementPath][direction] = connectedUIElementPath;
        }

        private void SetupCustomUINavigation()
        {
            foreach (KeyValuePair<string, Dictionary<string, string>> item in customUINavigation)
            {
                string elementPath = item.Key;
                Dictionary<string, string> navigationDirs = item.Value;

                GameObject uiElement = GameObject.Find(elementPath);
                if (uiElement == null)
                    continue;

                Selectable selectable = uiElement.GetComponent<Selectable>();
                if (selectable == null)
                    continue;

                Navigation navigation = selectable.navigation;

                foreach (KeyValuePair<string, string> navItem in navigationDirs)
                {
                    string direction = navItem.Key;
                    string targetPath = navItem.Value;

                    GameObject targetElement = GameObject.Find(targetPath);
                    if (targetElement == null)
                        continue;

                    Selectable targetSelectable = targetElement.GetComponent<Selectable>();
                    if (targetSelectable == null)
                        continue;

                    switch (direction)
                    {
                        case "Up":
                            navigation.selectOnUp = targetSelectable;
                            break;
                        case "Down":
                            navigation.selectOnDown = targetSelectable;
                            break;
                        case "Left":
                            navigation.selectOnLeft = targetSelectable;
                            break;
                        case "Right":
                            navigation.selectOnRight = targetSelectable;
                            break;
                    }
                }

                selectable.navigation = navigation;
            }
        }

        private void CheckMainMenuActivation()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (activeScene.name == "MainMenu")
            {
                if (!mainMenuActivated)
                {
                    mainMenuActivated = true;
                    if (uiAccessibilityManager != null)
                    {
                        uiAccessibilityManager.ApplyCustomNavigation();
                    }
                    else
                    {
                        SetupCustomUINavigation();
                    }
                }
            }
            else
            {
                mainMenuActivated = false;
            }
        }

        private void CheckQuickMenuActivation()
        {
            GameObject quickMenu = GameObject.Find("QuickMenu");
            if (quickMenu != null && quickMenu.activeSelf)
            {
                if (!quickMenuActivated)
                {
                    quickMenuActivated = true;
                    if (uiAccessibilityManager != null)
                    {
                        uiAccessibilityManager.ApplyCustomNavigation();
                    }
                    else
                    {
                        SetupCustomUINavigation();
                    }
                }
            }
            else
            {
                quickMenuActivated = false;
            }
        }

        private void CheckReachedTrackedObject()
        {
            try
            {
                if (currentLookTarget == null || PlayerTransform == null)
                {
                    return;
                }

                float distance = Vector3.Distance(PlayerTransform.position, currentLookTarget.transform.position);
                float stoppingDistance = pathfinder != null ? pathfinder.stoppingRadius : DEFAULT_STOPPING_RADIUS;

                if (distance <= stoppingDistance)
                {
                    if (!hasPlayedReachedSound)
                    {
                        string objectName = navMenu != null ?
                            navMenu.GetDisplayNameForObject(currentLookTarget) : "Unknown";

                        if (objectName == "Unknown Object" || objectName == "Unknown" || string.IsNullOrEmpty(objectName))
                        {
                            GrabbableObject grabbable = currentLookTarget.GetComponent<GrabbableObject>();
                            objectName = grabbable != null && grabbable.itemProperties != null && !string.IsNullOrEmpty(grabbable.itemProperties.itemName) ?
                                grabbable.itemProperties.itemName : GetFriendlyObjectName(currentLookTarget.name);
                        }

                        // Play the loaded audio clip directly instead of loading it again
                        if (reachedPositionSound != null)
                        {
                            // Create a temporary audio source on the current look target
                            GameObject tempAudio = new GameObject("TempReachedPositionAudio");
                            tempAudio.transform.position = currentLookTarget.transform.position;

                            AudioSource audioSource = tempAudio.AddComponent<AudioSource>();
                            AudioSystemBypass.ConfigureAudioSourceForBypass(audioSource, 0.7f);
                            audioSource.clip = reachedPositionSound;
                            audioSource.spatialBlend = 1f;
                            audioSource.minDistance = 0f;
                            audioSource.maxDistance = 5f;
                            audioSource.Play();

                            // Destroy after playing
                            Destroy(tempAudio, reachedPositionSound.length + 0.1f);

                            Debug.Log($"Playing reached sound for {objectName}");
                        }
                        else
                        {
                            Debug.LogWarning("Reached position sound not loaded");
                        }

                        Utilities.SpeakText("Reached " + objectName);
                        hasPlayedReachedSound = true;
                    }
                }
                else
                {
                    hasPlayedReachedSound = false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("Error in CheckReachedTrackedObject: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        private string GetFriendlyObjectName(string objectName)
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
                case "PlacementBlocker (5)":
                    return "Charging Station";
                default:
                    return objectName.Replace("(Clone)", "").Replace("Script", "");
            }
        }

        private void RegisterKeybinds()
        {
            RegisterKeybind("NavigateToLookingObject", Key.P, PathfindToSelected);
            RegisterKeybind("StopLookingAndPathfinding", Key.O, StopLookingAndPathfinding);
            RegisterKeybind("FocusPreviousUIElement", Key.Tab, FocusPreviousUIElement);
            RegisterKeybind("LeftClickHold", Key.Semicolon, delegate
            {
                SimulateMouseClick(0, true);
            });
            RegisterKeybind("RightClickHold", Key.Quote, delegate
            {
                SimulateMouseClick(1, true);
            });
            RegisterKeybind("ToggleNorthSound", Key.N, ToggleNorthSound);
            RegisterKeybind("SpeakPlayerDirection", Key.L, SpeakPlayerDirection);
            RegisterKeybind("DrawElevatorRay", Key.K, DrawElevatorRay);
            RegisterKeybind("AnnounceCurrentRoom", Key.R, AnnounceCurrentRoom);
            RegisterKeybind("ToggleAimAssist", Key.G, ToggleAimAssist);

            foreach (string key in registeredActions.Keys)
            {
                ConfigEntry<Key> configEntry = Config.Bind("Keybinds", key,
                    keybindConfigEntries.ContainsKey(key) ? keybindConfigEntries[key].Value : Key.None,
                    "Keybind for " + key);
                keybindConfigEntries[key] = configEntry;
            }
        }

        public void RegisterKeybind(string keybindName, Key defaultKey, Action action)
        {
            if (!registeredActions.ContainsKey(keybindName))
            {
                registeredActions[keybindName] = action;
                keybindConfigEntries[keybindName] = Config.Bind("Keybinds", keybindName, defaultKey, "Keybind for " + keybindName);
            }
        }

        public static void SetUISpeech(string gameObjectPath, List<Func<string>> textProviders)
        {
            if (Instance?.uiAccessibilityManager != null)
            {
                Instance.uiAccessibilityManager.SetCustomText(gameObjectPath, textProviders);
            }
            else
            {
                overriddenTexts[gameObjectPath] = textProviders;
            }
        }

        public void StopLookingAndPathfinding()
        {
            if (currentLookTarget != null)
            {
                string objectName = navMenu?.GetDisplayNameForObject(currentLookTarget) ?? "Unknown Object";
                Utilities.SpeakText("Stopped looking at " + objectName);
                currentLookTarget = null;
            }

            if (pathfinder != null && pathfinder.IsPathfinding)
            {
                pathfinder.StopPathfinding();
                Utilities.SpeakText("Stopped pathfinding");
            }
        }

        private void PathfindToSelected()
        {
            Debug.Log("PathfindToSelected() method called.");
            if (PlayerTransform != null && currentLookTarget != null)
            {
                if (pathfinder == null)
                {
                    pathfinder = PlayerTransform.gameObject.GetComponent<Pathfinder>();
                    if (pathfinder == null)
                    {
                        Debug.Log("Adding Pathfinder component to the player.");
                        pathfinder = PlayerTransform.gameObject.AddComponent<Pathfinder>();
                    }
                }

                if (pathfinder.IsPathfinding)
                {
                    pathfinder.TogglePathfindingMode();
                    return;
                }

                Debug.Log("Attempting to pathfind to " + currentLookTarget.name);
                pathfinder.NavigateTo(currentLookTarget);
            }
            else
            {
                if (PlayerTransform == null)
                {
                    Debug.Log("Pathfinder initialization failed: PlayerTransform is null.");
                }

                if (currentLookTarget == null)
                {
                    Debug.Log("Pathfinding not initiated: No object selected or object not found.");
                    Utilities.SpeakText("Object not found. Make sure you land the ship before attempting to pathfind.");
                }
            }
        }

        private void ToggleNorthSound()
        {
            northSoundManager.ToggleNorthSound();
            string toggleState = northSoundManager.isEnabled ? "enabled" : "disabled";
            Utilities.SpeakText("North Sound " + toggleState);
        }

        private void ToggleAimAssist()
        {
            if (navigationAimAssist != null)
            {
                Utilities.SpeakText(navigationAimAssist.ToggleEnabled() ? "Aim assist enabled." : "Aim assist disabled.");
                navigationAimAssist.enabled = true;
            }
        }

        private void SpeakPlayerDirection()
        {
            if (PlayerTransform != null)
            {
                Vector3 forward = PlayerTransform.forward;
                float angle = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                if (angle < 0f)
                {
                    angle += 360f;
                }

                string compassDirection = GetCompassDirection(angle);
                int roundedAngle = Mathf.RoundToInt(angle);
                string message = $"Facing {compassDirection} at {roundedAngle} degrees";
                Utilities.SpeakText(message);
            }
            else
            {
                Utilities.SpeakText("Player position not available");
            }
        }

        private string GetCompassDirection(float angle)
        {
            string[] directions = new string[8] { "North", "Northeast", "East", "Southeast", "South", "Southwest", "West", "Northwest" };
            int index = Mathf.RoundToInt(angle / 45f) % 8;
            return directions[index];
        }

        private IEnumerator LoadAudioClip(string resourcePath, System.Action<AudioClip> onLoaded)
        {
            string modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string fullPath = Path.Combine(modDirectory, resourcePath);
            string fileURL = "file://" + fullPath;

            UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(fileURL, AudioType.WAV);
            yield return request.SendWebRequest();

            try
            {
                if (request.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        onLoaded(clip);
                        Debug.Log($"Successfully loaded audio clip: {resourcePath}");
                    }
                    else
                    {
                        Debug.LogError($"Failed to load audio clip content: {resourcePath}");
                    }
                }
                else
                {
                    Debug.LogError($"Error loading audio clip {resourcePath}: {request.error}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception loading audio clip {resourcePath}: {ex.Message}");
            }
            finally
            {
                request.Dispose();
            }
        }

        public IEnumerator PlayAudioClipCoroutine(string audioFilePath, GameObject targetGameObject, float minDistance, float maxDistance)
        {
            if (targetGameObject == null)
            {
                Debug.LogError("Target GameObject is null. Cannot play audio clip.");
                yield break;
            }

            string modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string fullPath = Path.Combine(modDirectory, audioFilePath);
            string fileURL = "file://" + fullPath;

            UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip(fileURL, AudioType.WAV);
            yield return uwr.SendWebRequest();

            try
            {
                if (uwr.result == UnityWebRequest.Result.ConnectionError ||
                    uwr.result == UnityWebRequest.Result.ProtocolError)
                {
                    Debug.LogError("Error loading audio clip: " + uwr.error);
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(uwr);
                if (clip != null)
                {
                    AudioSource audioSource = targetGameObject.GetComponent<AudioSource>();
                    if (audioSource == null)
                    {
                        audioSource = targetGameObject.AddComponent<AudioSource>();
                    }

                    audioSource.clip = clip;
                    audioSource.spatialBlend = 1f;
                    audioSource.rolloffMode = AudioRolloffMode.Linear;
                    audioSource.minDistance = minDistance;
                    audioSource.maxDistance = maxDistance;
                    audioSource.Play();
                    Debug.Log("Playing audio clip: " + audioFilePath);
                }
                else
                {
                    Debug.LogError("Failed to load audio clip: " + audioFilePath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("Exception processing audio clip: " + ex.Message);
            }
        }

        public NavigationAimAssist GetNavigationAimAssist()
        {
            return navigationAimAssist;
        }

        private void OnDestroy()
        {
            SpeechSynthesizer.Cleanup();
            if (navigationAimAssist != null)
            {
                Destroy(navigationAimAssist);
            }
            if (uiAccessibilityManager != null)
            {
                Destroy(uiAccessibilityManager);
            }
        }
    }
}