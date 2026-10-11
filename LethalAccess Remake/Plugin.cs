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
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityA11y.AccessKitInterop;
using UnityA11y.Unity;
using UnityEngine.UI;
using Key = UnityEngine.InputSystem.Key;

namespace LethalAccess
{
    [BepInPlugin(modGUID, modName, modVersion)]
    // Soft: the mod runs without InputUtils. With it, InputUtils must wake first, because
    // ModKeybinds registers LethalAccess's keys with it from this plugin's Awake.
    [BepInDependency(ModKeybinds.InputUtilsGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class LACore : BaseUnityPlugin
    {
        private const string modGUID = "Green.LethalAccess";
        private const string modName = "LethalAccess";
        private const string modVersion = "1.2.0";

        public static Transform playerTransform;
        public static Transform cameraTransform;
        private const string REACHED_POSITION_SOUND_PATH = "LethalAccessAssets/ReachedPosition.wav";
        private AudioClip reachedPositionSound;
        private static Dictionary<string, Action> registeredActions = new Dictionary<string, Action>();
        public static Dictionary<string, List<Func<string>>> overriddenTexts = new Dictionary<string, List<Func<string>>>();
        private List<GameObject> previouslyFocusedUIElements = new List<GameObject>();
        public static GameObject currentLookTarget;
        public static bool enableCustomKeybinds = true;
        private bool hasPlayedReachedSound = false;
        private bool mainMenuActivated = false;
        private bool quickMenuActivated = false;
        private ControlTipAccessPatch controlTipAccessPatch;
        private ProfitQuotaPatch profitQuotaPatch;
        private PlayerHealthPatch playerHealthPatch;
        private TimeOfDayPatch timeOfDayPatch;
        public NavMenu navMenu;
        private Pathfinder pathfinder;
        private NorthSoundManager northSoundManager;
        private TileTracker tileTracker;
        private InstantNavigationManager instantNavigationManager;

        // The accessibility-tree provider, when it attached. Held so it is not collected and
        // so its state can be inspected at runtime.
        private A11yRuntime accessibilityTree;
        private UIAccessibilityManager uiAccessibilityManager;
        private SettingsMenu settingsMenu;
        private const float MAX_DISTANCE_TO_OBJECT = 15f;
        private const float DEFAULT_STOPPING_RADIUS = 2.2f;
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

                // Every plugin lives on BepInEx's manager object, and the game destroys it on the
                // first scene load unless BepInEx.cfg sets HideManagerGameObject = true. A fresh
                // BepInEx install leaves that false, so the mod died at the launch screen and Tolk
                // was unloaded under the speech thread, crashing the game. This is the same flag
                // that setting applies.
                gameObject.hideFlags = HideFlags.HideAndDontSave;

                // Initialize configuration manager
                ConfigManager.Initialize(Config);

                // Before anything calls RegisterKeybind: the patches below register theirs from
                // their own Initialize methods.
                ModKeybinds.Initialize(Config, MetadataHelper.GetMetadata(this));

                // Start the speech worker from the main thread. Doing this lazily from a
                // thread-pool thread is what made the old static constructor a permanent-TTS-loss
                // hazard.
                SpeechSynthesizer.Initialize();

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

                Logger.LogInfo("TileTracker initialized.");
                uiAccessibilityManager = gameObject.AddComponent<UIAccessibilityManager>();
                uiAccessibilityManager.Initialize(Logger);

                // Initialize settings menu
                settingsMenu = gameObject.AddComponent<SettingsMenu>();
                settingsMenu.Initialize();

                Harmony harmony = new Harmony("LethalAccess");
                // PatchAll(assembly) already covers every [HarmonyPatch] class in this assembly,
                // including PreInitScenePatch. The extra PatchAll(typeof(PreInitScenePatch)) that
                // used to follow applied the postfix a second time, visible in the log as two
                // "Pressing Continue button in PreInitScene" lines for one event.
                harmony.PatchAll(Assembly.GetExecutingAssembly());

                // At startup rather than on the first open of the keybinds screen, where building
                // these patches used to be part of the pause before the screen appeared.
                KeybindsPanelPatch.EnsureInputUtilsPatches();
                Logger.LogInfo("LethalAccess initialized.");
                RegisterKeybinds();
                StartCoroutine(LoadReachedPositionSound());
                RegisterNavigationPoints();
                RegisterUIElements();
                instantNavigationManager = new InstantNavigationManager(this);
                StartAccessibilityTree();
            }
            else
            {
                Destroy(this);
            }
        }

        private IEnumerator LoadReachedPositionSound()
        {
            yield return StartCoroutine(AudioUtils.LoadAudioClip(REACHED_POSITION_SOUND_PATH, clip => reachedPositionSound = clip));
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
            // Every path below must name an object that actually ships in level2. Names that look
            // right and do not exist have been added here more than once: MainButtons/JoinButton
            // (it is JoinACrew), MicSettings/PushToTalk (the mic-mode control is TalkMode; the real
            // PushToTalk is under ControlsOptions, inactive, and its Selectable is on the child
            // PushToTalk/Button), MainButtons/PlayButton, SettingsPanel/TabButton(*) and
            // MenuBackground. A bad path is silent: the control simply never announces.
            //
            // Do not annotate these as "verified" without re-resolving them. Instead rely on
            // UIAccessibilityManager.ValidateRegisteredPaths(), which reports every path that fails
            // to resolve when the main menu opens, so a rename cannot go unnoticed.
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/SettingsPanel/MicSettings/SpeakerButton", "Microphone Toggle", "toggle");
            uiAccessibilityManager.RegisterElement("Systems/UI/Canvas/QuickMenu/SettingsPanel/MicSettings/SpeakerButton", "Microphone Toggle", "toggle");
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/SettingsPanel/ControlsOptions/InvertYAxis", "Invert Y-Axis", "checkbox");
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/SettingsPanel/MicSettings/ChooseDevice", "Current Input Device", "toggle");
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/SettingsPanel/ControlsOptions/LookSensitivity/Slider", "Look Sensitivity", "slider");
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/SettingsPanel/MasterVolume/Slider", "Master Volume", "slider");
            uiAccessibilityManager.RegisterElement("Systems/UI/Canvas/QuickMenu/SettingsPanel/ControlsOptions/LookSensitivity/Slider", "Look Sensitivity", "slider");
            uiAccessibilityManager.RegisterElement("Systems/UI/Canvas/QuickMenu/SettingsPanel/MasterVolume/Slider", "Master Volume", "slider");
            // Register the main-menu buttons BEFORE grouping them. CreateGroup only
            // auto-registers paths it has not seen (UIAccessibilityHandler.CreateGroup), and that
            // auto-registration passes displayName == null, which makes RegisterElement fall back
            // to GetNameFromPath, a camel-split of the GameObject name, i.e. "Host Button". The
            // scene labels are "> Host" etc., so registered and unregistered buttons on one screen
            // announced themselves in two different styles.
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/MainButtons/HostButton", "Host", "button");
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/MainButtons/JoinACrew", "Join a crew", "button");
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/MainButtons/SettingsButton", "Settings", "button");
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/MainButtons/Credits", "Credits", "button");
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/MainButtons/QuitButton", "Quit", "button");

            // Five active buttons, in scene order. The old group named JoinButton (which
            // does not exist) and omitted Credits, so group members said "of 4" while the two
            // unregistered buttons fell through to the sibling scan and said "of 5".
            uiAccessibilityManager.CreateGroup("MainMenuButtons",
                "Canvas/MenuContainer/MainButtons/HostButton",
                "Canvas/MenuContainer/MainButtons/JoinACrew",
                "Canvas/MenuContainer/MainButtons/SettingsButton",
                "Canvas/MenuContainer/MainButtons/Credits",
                "Canvas/MenuContainer/MainButtons/QuitButton");
            uiAccessibilityManager.CreateGroup("FileSelectionButtons", "Canvas/MenuContainer/LobbyHostSettings/FilesPanel/File1", "Canvas/MenuContainer/LobbyHostSettings/FilesPanel/File2", "Canvas/MenuContainer/LobbyHostSettings/FilesPanel/File3", "Canvas/MenuContainer/LobbyHostSettings/FilesPanel/ChallengeMoonButton");

            // The third member used to be MicSettings/PushToTalk, which does not exist, so
            // the group announced "of 3" while only two members could ever be reached. TalkMode is
            // the mic-mode control that actually lives under MicSettings.
            uiAccessibilityManager.CreateGroup("MicSettings", "Canvas/MenuContainer/SettingsPanel/MicSettings/SpeakerButton", "Canvas/MenuContainer/SettingsPanel/MicSettings/TalkMode", "Canvas/MenuContainer/SettingsPanel/MicSettings/ChooseDevice");

            // The real push-to-talk control, registered on its own rather than in the mic group:
            // it sits under ControlsOptions, not MicSettings, and the Selectable is on the child
            // Button; the parent PushToTalk object has none. Its parent ships inactive, so this
            // entry is expected to be absent most of the time; that is only expressible as
            // UIEntry.Optional once the settings panel gets a UIScreenDescriptor (that file is
            // owned by the settings pass), and until then ValidateRegisteredPaths will report it.
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/SettingsPanel/ControlsOptions/PushToTalk/Button", "Push to talk key", "button");

            // Set custom text for UI elements using UIUtils
            UIUtils.SetCustomUIText(uiAccessibilityManager, "Canvas/MenuContainer/SettingsPanel/MicSettings/SpeakerButton",
                () => "Microphone Toggle: " + UIUtils.GetTextFromGameObject("Canvas/MenuContainer/SettingsPanel/MicSettings/SpeakerButton"));

            UIUtils.SetCustomUIText(uiAccessibilityManager, "Systems/UI/Canvas/QuickMenu/SettingsPanel/MicSettings/SpeakerButton",
                () => "Microphone Toggle: " + UIUtils.GetTextFromGameObject("Systems/UI/Canvas/QuickMenu/SettingsPanel/MicSettings/SpeakerButton"));

            UIUtils.SetCustomUIText(uiAccessibilityManager, "Canvas/MenuContainer/SettingsPanel/ControlsOptions/LookSensitivity/Slider",
                () => string.Format("{0} {1}", UIUtils.GetTextFromGameObject("Canvas/MenuContainer/SettingsPanel/ControlsOptions/LookSensitivity/Text (1)"),
                    UIUtils.GetSliderValue("Canvas/MenuContainer/SettingsPanel/ControlsOptions/LookSensitivity/Slider/Handle Slide Area/Handle")));

            UIUtils.SetCustomUIText(uiAccessibilityManager, "Canvas/MenuContainer/SettingsPanel/MasterVolume/Slider",
                () => string.Format("{0} {1}%", UIUtils.GetTextFromGameObject("Canvas/MenuContainer/SettingsPanel/MasterVolume/Text (1)"),
                    UIUtils.GetSliderValue("Canvas/MenuContainer/SettingsPanel/MasterVolume/Slider/Handle Slide Area/Handle")));

            UIUtils.SetCustomUIText(uiAccessibilityManager, "Systems/UI/Canvas/QuickMenu/SettingsPanel/ControlsOptions/LookSensitivity/Slider",
                () => string.Format("{0} {1}", UIUtils.GetTextFromGameObject("Systems/UI/Canvas/QuickMenu/SettingsPanel/ControlsOptions/LookSensitivity/Text (1)"),
                    UIUtils.GetSliderValue("Systems/UI/Canvas/QuickMenu/SettingsPanel/ControlsOptions/LookSensitivity/Slider/Handle Slide Area/Handle")));

            UIUtils.SetCustomUIText(uiAccessibilityManager, "Systems/UI/Canvas/QuickMenu/SettingsPanel/MasterVolume/Slider",
                () => string.Format("{0} {1}%", UIUtils.GetTextFromGameObject("Systems/UI/Canvas/QuickMenu/SettingsPanel/MasterVolume/Text (1)"),
                    UIUtils.GetSliderValue("Systems/UI/Canvas/QuickMenu/SettingsPanel/MasterVolume/Slider/Handle Slide Area/Handle")));

            // Three main-menu texts that have never been announced. Each is a bare TMP with no
            // Selectable, so registering them does not by itself make them reachable; it means
            // that WHEN something focuses them (a main-menu UIScreenDescriptor, or an injected
            // Selectable) they announce their live value instead of their GameObject name.
            // The version string is read off the live TMP on purpose: the news popup's hand-authored
            // heading still says 80 while MenuManager.Awake writes v81 into VersionNum, so any
            // synthesised or hard-coded version would be wrong today and wrong again next patch.
            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/VersionNum", "Game version", "text");
            UIUtils.SetCustomUIText(uiAccessibilityManager, "Canvas/MenuContainer/VersionNum",
                () => DescribeLiveText("Canvas/MenuContainer/VersionNum", "Game version", "Game version unavailable"));

            uiAccessibilityManager.RegisterElement("Canvas/MenuContainer/LaunchedInLanModeText", "Launch mode", "text");
            UIUtils.SetCustomUIText(uiAccessibilityManager, "Canvas/MenuContainer/LaunchedInLanModeText",
                () => DescribeLiveText("Canvas/MenuContainer/LaunchedInLanModeText", "Launch mode", "Launch mode unavailable"));

            // Connection status ("Connecting to server...", "Connection failed. Try again?"). Note
            // this one hangs off Canvas directly, not MenuContainer.
            uiAccessibilityManager.RegisterElement("Canvas/LogText", "Connection status", "text");
            UIUtils.SetCustomUIText(uiAccessibilityManager, "Canvas/LogText",
                () => DescribeLiveText("Canvas/LogText", "Connection status", "No connection status"));

            uiAccessibilityManager.SetNavigation("Canvas/MenuContainer/LobbyHostSettings/FilesPanel/ChallengeMoonButton", "Up", "Canvas/MenuContainer/LobbyHostSettings/FilesPanel/File3");
            uiAccessibilityManager.SetNavigation("Canvas/MenuContainer/LobbyHostSettings/FilesPanel/File3", "Down", "Canvas/MenuContainer/LobbyHostSettings/FilesPanel/ChallengeMoonButton");
        }

        /// <summary>
        /// "&lt;label&gt;, &lt;live text&gt;" for a text-only UI object, or an explicit absence phrase.
        ///
        /// UIUtils.GetTextFromGameObject falls back to the GameObject's NAME when it finds no text
        /// component, and returns "" when the path does not resolve. Both would be announced
        /// verbatim ("VersionNum" or a bare label with nothing after it), so the empty case is
        /// turned into a sentence that says the value is missing rather than sounding like the
        /// value is blank.
        /// </summary>
        private static string DescribeLiveText(string path, string label, string whenMissing)
        {
            string live = UIUtils.GetTextFromGameObject(path);
            if (string.IsNullOrEmpty(live) || live.Trim().Length == 0)
            {
                return whenMissing;
            }
            return label + ", " + live.Trim();
        }

        // Every statement below touches the Unity API and therefore must run on the main
        // thread. This method used to be `async void` and pushed the four calls after the
        // planet-name block through Task.Run, which made GameObject.Find, SceneManager,
        // AudioSource creation and Destroy run on a thread-pool thread; they threw every
        // frame and the exceptions were swallowed. It is now a plain synchronous Update.
        private void Update()
        {
            // The RELEASE arm runs unconditionally, OUTSIDE the enableCustomKeybinds gate.
            // Two sites clear that flag while a key may still be physically held:
            // TerminalTogglePatch.PrefixBeginUsingTerminal, and AccessibleMenuSystem.BlockAllOtherInput,
            // which is the base of both SettingsMenu (F10) and AccessibleFriendsMenu, so three
            // things the user can open. While the release arm lived inside CheckKeybinds, letting go of
            // LeftAlt/RightAlt after one of those opened never reached SimulateMouseClick(_, false),
            // so Mouse.current kept a synthetic button-down state for the rest of the session.
            // The mod must always be able to undo its OWN held state; it still cannot START one
            // while blocked, because the press side stays inside the gate below.
            ReleaseHeldSimulatedClicks();

            if (enableCustomKeybinds)
            {
                CheckKeybinds();
            }

            if (currentLookTarget != null && (pathfinder == null || !pathfinder.IsPathfinding))
            {
                Utilities.LookAtObject(currentLookTarget);
            }

            if (tileTracker != null && !tileTracker.IsInitializedForPlayer && PlayerTransform != null)
            {
                tileTracker.Initialize(Logger, PlayerTransform);
                Logger.LogInfo("TileTracker initialized with player transform");
            }

            bool isShipLanded = StartOfRound.Instance != null && StartOfRound.Instance.shipHasLanded;
            string currentPlanetName = !isShipLanded ? string.Empty : StartOfRound.Instance.currentLevel?.PlanetName ?? string.Empty;

            if (StartOfRound.Instance != null && StartOfRound.Instance.currentLevel == null && isShipLanded)
            {
                currentPlanetName = "Company Building";
            }

            // Category visibility is TRACKED every frame but only ANNOUNCED during gameplay while
            // the local player is alive. Outside that, the categories are not something the user
            // can act on, and announcing at boot is how every launch used to open with "Factory
            // category is now hidden." twice over before the menu had even appeared.
            navMenu.UpdateCategoriesVisibility(isShipLanded, currentPlanetName, IsInLivingGameplay());
            CheckReachedTrackedObject();
            CheckMainMenuActivation();
            CheckQuickMenuActivation();

            // Turning with the arrow keys, including snap turns.
            //
            // This block used to sit OUTSIDE the enableCustomKeybinds gate above, so the
            // arrow keys yawed the player at the same time as they moved the selection in the
            // pause menu, and at the same time as they moved the caret in a text field. It now
            // runs only when the mod's keybinds are live (AccessibleMenuSystem clears that flag
            // while its own menus are open) and only when nothing else owns the arrow keys.
            if (enableCustomKeybinds && !IsTextInputFocused() && !IsMenuHoldingArrowKeys()
                && GameNetworkManager.Instance?.localPlayerController != null
                && PlayerTransform != null && CameraTransform != null)
            {
                float currentTurnSpeed = ConfigManager.TurnSpeed.Value;

                // Regular turning with arrow keys ONLY
                // The turn keys are rebindable (ModKeybinds); Shift is the snap-turn modifier
                // whatever they are bound to.
                bool shiftHeld = Keyboard.current != null &&
                                 (Keyboard.current[Key.LeftShift].isPressed || Keyboard.current[Key.RightShift].isPressed);

                if (ModKeybinds.IsPressed(ModKeybinds.TurnLeft) && !shiftHeld)
                {
                    SafeRotate(-currentTurnSpeed * Time.deltaTime);
                }
                if (ModKeybinds.IsPressed(ModKeybinds.TurnRight) && !shiftHeld)
                {
                    SafeRotate(currentTurnSpeed * Time.deltaTime);
                }

                // Snap turning with shift+arrow keys
                if (shiftHeld)
                {
                    // Movement/SnapTurnAngle was bound in config and shown in the settings menu
                    // but never read; the angle was hard-coded to 45 degrees here.
                    float snapAngle = ConfigManager.SnapTurnAngle.Value;

                    if (ModKeybinds.WasPressedThisFrame(ModKeybinds.TurnLeft))
                    {
                        SnapTurn(-snapAngle);
                    }
                    if (ModKeybinds.WasPressedThisFrame(ModKeybinds.TurnRight))
                    {
                        SnapTurn(snapAngle);
                    }
                }
            }
        }

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

        private void SnapTurn(float degrees)
        {
            if (PlayerTransform == null) return;

            try
            {
                Vector3 forward = PlayerTransform.forward;

                if (forward.magnitude < 0.001f)
                {
                    Debug.LogWarning("Forward vector is too small, using default forward");
                    forward = Vector3.forward;
                }

                float currentAngle = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                if (currentAngle < 0f) currentAngle += 360f;

                // Snap to multiples of the configured angle, not a hard-coded 45 degrees,
                // otherwise a configured 30 or 90 degree step was immediately rounded back
                // onto the 45 degree grid.
                float step = Mathf.Max(1f, Mathf.Abs(degrees));
                float newAngle = currentAngle + degrees;
                newAngle = Mathf.Round(newAngle / step) * step;
                if (newAngle >= 360f) newAngle -= 360f;
                if (newAngle < 0f) newAngle += 360f;

                Vector3 newForward = new Vector3(
                    Mathf.Sin(newAngle * Mathf.Deg2Rad),
                    0f,
                    Mathf.Cos(newAngle * Mathf.Deg2Rad)
                );

                if (newForward.magnitude > 0.001f)
                {
                    PlayerTransform.rotation = Quaternion.LookRotation(newForward, Vector3.up);
                }
                else
                {
                    Debug.LogWarning("Calculated forward vector is too small, skipping rotation");
                }

                string compassDirection = GetCompassDirection(newAngle);
                SpeechUtils.SpeakText(compassDirection);
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
                SpeechUtils.SpeakText("You are in " + currentTileName + ", " + pathType);
            }
            else
            {
                SpeechUtils.SpeakText("Room information not available");
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

        /// <summary>
        /// True while the EventSystem's selection is a text field that is actually accepting
        /// characters.
        ///
        /// The mod polls Keyboard.current through the new Input System while TMP delivers
        /// characters to the focused field through its own IMGUI event queue. Neither consumes the
        /// other, so every mod keybind (P, O, N, L, K, R, Tab) fired AND typed its character. That
        /// is why the lobby name "cannot be changed": Tab yanked the selection out of the field
        /// mid-edit while P and R ran pathfinding and room announcements over the top. The server
        /// tag filter and the terminal break in exactly the same way.
        /// </summary>
        /// <summary>
        /// Whether the key bound to UI navigation went down this frame. Reads the CONFIGURED key
        /// rather than Key.Tab, so rebinding it keeps the way out of a text field.
        /// </summary>
        private bool NavigationKeyPressedThisFrame()
        {
            return ModKeybinds.WasPressedThisFrame(UiNavigationKeybindName);
        }

        /// <summary>
        /// Stop the focused text field accepting characters, leaving it selected. False when it
        /// was left alone, in which case the caller must keep suppressing the keypress.
        ///
        /// Only fields on a registered screen are released, because only there is there a next
        /// control to navigate to. The terminal's field is the case this protects: it is
        /// deliberately isolated from navigation and stays active for the whole session
        /// (Terminal.BeginUsingTerminal), so ending its edit would take the keyboard away from
        /// the one thing the player is using and leave nowhere to go.
        ///
        /// Deliberately not a focus change: the caller moves focus itself straight after, and
        /// doing both here would move twice.
        /// </summary>
        private static bool TryEndTextEntry()
        {
            EventSystem eventSystem = EventSystem.current;
            GameObject selected = eventSystem == null ? null : eventSystem.currentSelectedGameObject;
            if (selected == null || !UIScreenRegistry.ClaimsSelectable(selected))
            {
                return false;
            }

            TMP_InputField tmpField = selected.GetComponent<TMP_InputField>();
            if (tmpField != null)
            {
                tmpField.DeactivateInputField();
                return true;
            }

            InputField legacyField = selected.GetComponent<InputField>();
            if (legacyField != null)
            {
                legacyField.DeactivateInputField();
                return true;
            }

            return false;
        }

        private static bool IsTextInputFocused()
        {
            EventSystem eventSystem = EventSystem.current;
            GameObject selected = eventSystem == null ? null : eventSystem.currentSelectedGameObject;
            if (selected == null)
            {
                return false;
            }

            TMP_InputField tmpField = selected.GetComponent<TMP_InputField>();
            if (tmpField != null && tmpField.isFocused)
            {
                return true;
            }

            InputField legacyField = selected.GetComponent<InputField>();
            return legacyField != null && legacyField.isFocused;
        }

        /// <summary>
        /// R is both the mod's "announce current room" and the game's build-mode rotate
        /// (ShipBuildModeManager.Update polls the "ReloadBatteries" action, default R), so placing
        /// furniture rotated the ghost and read the room name at the same time.
        ///
        /// This suppresses EVERY mod keybind while a ghost is up, not just R, so that no future
        /// bind can collide with build mode either. The cost is that pathfinding, north sound and
        /// facing-direction are
        /// inert for the few seconds build mode is open. It does NOT cost the user placement:
        /// placement is confirmed by the "BuildMode" action itself
        /// (ShipBuildModeManager.EnterBuildMode's InBuildMode branch), not by a mouse click, so
        /// the simulated LeftAlt/RightAlt clicks are not needed here.
        /// </summary>
        private static bool IsInShipBuildMode()
        {
            return ShipBuildModeManager.Instance != null && ShipBuildModeManager.Instance.InBuildMode;
        }

        /// <summary>
        /// Publish the mod's screens as a real accessibility tree, so a screen reader reads
        /// them with its own vocabulary, verbosity, braille and review cursor instead of
        /// being handed sentences this mod wrote.
        ///
        /// Navigation is deliberately NOT handed over. An ExternalFocusOwner reports where
        /// focus already is and relays focus requests back; the Selectable finder patches
        /// keep owning the arrow keys, so the authored screen order survives. The generic
        /// uGUI scanner is left unregistered for the same reason: its geometric guess would
        /// compete with that order.
        ///
        /// Falls back to speaking menus itself if the bridge does not attach. That path is
        /// the mod's previous behaviour, so a missing accesskit.dll costs braille and NVDA's
        /// own wording, never silence.
        /// </summary>
        /// <summary>
        /// Report, once, how far an assistive technology actually got into the tree.
        ///
        /// "Attached" only means the WndProc was subclassed. It does NOT mean a screen
        /// reader ever queried the window, nor that the adapter answered. Those are three
        /// separate failures with identical symptoms (silence) and telling them apart by
        /// staring at node content is hopeless, so the counters say which one it is.
        /// </summary>
        private IEnumerator ReportTreeReachability(A11yRuntime runtime)
        {
            yield return new WaitForSecondsRealtime(10f);

            AccessKitBridge bridge = runtime != null ? runtime.Bridge : null;
            if (bridge == null)
            {
                yield break;
            }

            Logger.LogInfo(string.Format(
                "Tree reachability after 10s: wndProc={0} wmGetObject={1} activations={2} " +
                "updateFactory={3} lastGetObject={4}",
                bridge.WndProcMessages, bridge.WmGetObject, bridge.Activations,
                bridge.UpdateFactoryCalls, bridge.LastGetObjectOutcome ?? "<none>"));

            if (bridge.WndProcMessages == 0)
            {
                Logger.LogWarning("The window subclass is receiving no messages at all, so the " +
                                  "subclass did not take even though it reported success.");
            }
            else if (bridge.WmGetObject == 0)
            {
                Logger.LogWarning("No assistive technology has queried this window (no WM_GETOBJECT). " +
                                  "Either none is running, or it is not inspecting this process.");
            }
            else if (bridge.Activations == 0)
            {
                Logger.LogWarning("A screen reader queried the window but the adapter never handed back " +
                                  "a tree. The WM_GETOBJECT reply is being rejected - suspect the " +
                                  "accesskit_opt_lresult return-value marshalling.");
            }
        }

        private void StartAccessibilityTree()
        {
            if (ConfigManager.UseAccessibilityTree == null || !ConfigManager.UseAccessibilityTree.Value)
            {
                Logger.LogInfo("Accessibility tree disabled by config; menus are spoken by the mod.");
                return;
            }
            StartCoroutine(StartAccessibilityTreeWhenWindowExists());
        }

        /// <summary>
        /// Attaching cannot be done from Awake. The adapter needs the game's HWND, and this
        /// runs during the first scene load: GetActiveWindow returns nothing until the
        /// window has been shown and the process has pumped a message, so the first attempt
        /// finds no window and gives up. Live-confirmed: the bridge reported no window while
        /// the very same lookup, run a few seconds later, returned a valid handle.
        ///
        /// So the runtime is created immediately (it is inert until the bridge attaches) and
        /// the attach is retried until it takes.
        /// </summary>
        private IEnumerator StartAccessibilityTreeWhenWindowExists()
        {
            const float attachTimeoutSeconds = 20f;

            A11yRuntime runtime;
            try
            {
                LethalAccessSource source = new LethalAccessSource();
                runtime = A11yRuntime.Create(
                    new ExternalFocusOwner(source.CurrentFocusId, source.RequestFocus),
                    null,
                    registerUGuiSource: false);

                runtime.Log = msg => Logger.LogInfo("[tree] " + msg);
                runtime.Register(source);
                accessibilityTree = runtime;
            }
            catch (Exception ex)
            {
                AnnouncementPolicy.UiTreeOwnsSpeech = false;
                Logger.LogError("Accessibility tree failed to start, so menus stay on the mod's own speech: " + ex);
                yield break;
            }

            float deadline = Time.unscaledTime + attachTimeoutSeconds;
            while (!runtime.BridgeRunning && Time.unscaledTime < deadline)
            {
                // Not wrapped in try/catch: TryAttachBridge already reports its own failures,
                // and swallowing an exception here would turn "accessibility is off" into a
                // silent condition, which is the one outcome a blind user cannot diagnose.
                if (runtime.TryAttachBridge()) break;
                yield return new WaitForSecondsRealtime(0.25f);
            }

            if (runtime.BridgeRunning)
            {
                // Only now does the mod stop speaking menus. Flipping this any earlier would
                // leave a blind user with no menu speech at all on the paths where the bridge
                // never attaches.
                AnnouncementPolicy.UiTreeOwnsSpeech = true;

                // Scoped to what the tree actually publishes. A registered screen has to be the
                // active one as well as claiming the control, because that is the condition
                // LethalAccessSource.IsActive builds a snapshot under.
                AnnouncementPolicy.UiTreeCovers = delegate (GameObject go)
                {
                    return !string.IsNullOrEmpty(UIScreenRegistry.ActiveScreenId) &&
                           UIScreenRegistry.ClaimsSelectable(go);
                };

                Logger.LogInfo("Accessibility tree attached; your screen reader now reads the menus. " +
                               "Gameplay speech still comes from this mod.");
                StartCoroutine(ReportTreeReachability(runtime));
            }
            else
            {
                Logger.LogWarning("Accessibility tree could not attach within " + attachTimeoutSeconds +
                                  "s. Menus will be spoken by the mod as before. Check that accesskit.dll " +
                                  "(x64) sits next to the game executable; the log line above this one " +
                                  "carries the adapter's own reason.");
            }
        }

        /// <summary>
        /// True while the local player is in a round and alive, the only time announcements about
        /// the navigation categories mean anything.
        ///
        /// StartOfRound.Instance is non-null in the main menu scene too, so it cannot carry this on
        /// its own; localPlayerController is the part that is null until a round actually starts.
        /// isPlayerDead covers the spectator path, where the categories still change underneath the
        /// player but they can no longer walk to anything.
        /// </summary>
        private static bool IsInLivingGameplay()
        {
            if (StartOfRound.Instance == null)
            {
                return false;
            }

            PlayerControllerB local = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;

            return local != null && local.isPlayerControlled && !local.isPlayerDead;
        }

        /// <summary>
        /// True only while the vanilla settings panel is genuinely waiting for the user to
        /// press the key they are binding.
        ///
        /// DO NOT simplify this to `IngamePlayerSettings.Instance.rebindingOperation != null`. That
        /// reads like the obvious guard and is a permanent lockout: Assembly-CSharp never assigns
        /// that field null. IngamePlayerSettings.RebindKey writes it and every exit path only
        /// Dispose()s it, so after the first rebind of a session it stays non-null forever and
        /// every mod keybind goes dead for good.
        ///
        /// RebindingOperation.started is the real state (a pure m_Flags read, safe after Dispose,
        /// cleared by ResetAfterMatchCompleted on both the complete and the cancel path). It is not
        /// sufficient on its own either: IngamePlayerSettings.CancelRebind calls Dispose() without
        /// Cancel(), and SettingsOption.OnDisable routes every keybind row through it when the
        /// panel closes, which leaves Flags.Started set on an operation that can never finish. So
        /// this also requires InputSystem.actions to still be disabled: RebindKey disables it and
        /// both CompleteRebind and CancelRebind re-enable it, which makes it the one signal cleared
        /// on every exit path. The conjunction fails OPEN: a stale flag costs one spurious
        /// announcement, a stuck guard would cost the user their entire keyboard.
        /// </summary>
        private static bool IsRebindInProgress()
        {
            IngamePlayerSettings settings = IngamePlayerSettings.Instance;
            if (settings == null || settings.rebindingOperation == null || !settings.rebindingOperation.started)
            {
                return false;
            }

            // Deliberately not just `!enabled`: the game disables the action asset in other places
            // too, and on its own that would suppress mod keybinds during unrelated transitions.
            InputActionAsset actions = InputSystem.actions;
            return actions != null && !actions.enabled;
        }

        /// <summary>
        /// True when a game menu owns the arrow keys, so the mod must not also yaw the player.
        /// Chat and the special (terminal/shop) menus both take raw text; the quick menu takes
        /// arrow navigation.
        /// </summary>
        private static bool IsMenuHoldingArrowKeys()
        {
            PlayerControllerB player = GameNetworkManager.Instance == null
                ? null
                : GameNetworkManager.Instance.localPlayerController;
            if (player == null)
            {
                return false;
            }

            if (player.isTypingChat || player.inSpecialMenu || player.inTerminalMenu)
            {
                return true;
            }

            return player.quickMenuManager != null && player.quickMenuManager.isMenuOpen;
        }

        private void CheckKeybinds()
        {
            // Each of these means some other consumer already owns the
            // keyboard this frame, so firing a mod action on top of it is the bug rather than a
            // missed feature. Evaluated once per frame, not once per keybind.
            //
            // This suppresses PRESSES only, and this whole method is PRESS-only now. 3.4: the
            // release arm that un-presses the simulated mouse buttons used to be the `else` half of
            // the loop below, which put it behind the enableCustomKeybinds gate at the call site,
            // so a LeftAlt/RightAlt held when the terminal or an accessible menu opened could never
            // be released. It is now ReleaseHeldSimulatedClicks(), called unconditionally from
            // Update before this method.
            bool typing = IsTextInputFocused();

            // A text field must not be a trap. Suppressing every keybind while typing also
            // suppressed the UI navigation key, so the only way out of a field was Enter, which
            // no screen-reader user has any reason to expect: Tab leaves a field in every other
            // Windows application. The field is deactivated FIRST so the keypress that leaves is
            // not also delivered to it as a character, which is the collision this suppression
            // exists to prevent; every other keybind stays suppressed.
            if (typing && NavigationKeyPressedThisFrame() && TryEndTextEntry())
            {
                typing = false;
            }

            bool suppressPresses = typing || IsInShipBuildMode() || IsRebindInProgress();

            // On the remap screen only the menu-navigation key keeps working. H there reads the
            // focused control's help, and pressing any key to bind it must not also run what the
            // mod has on that key.
            bool remapScreen = IsRemapScreenOpen();

            foreach (KeyValuePair<string, Action> registered in registeredActions)
            {
                if (!ModKeybinds.WasPressedThisFrame(registered.Key))
                {
                    continue;
                }

                if (suppressPresses || (remapScreen && registered.Key != UiNavigationKeybindName))
                {
                    continue;
                }

                registered.Value();
            }
        }

        // True while THIS mod is holding a simulated mouse button down. Written only where the
        // state event is actually queued (inside SimulateMouseClick, past its Mouse.current == null
        // guard), so the flag can never claim a press that was never sent, which is what keeps
        // ReleaseHeldSimulatedClicks from injecting mouse-up events over a real mouse the mod never
        // touched.
        private bool simulatedLeftButtonHeld;
        private bool simulatedRightButtonHeld;

        /// <summary>
        /// Queues the mouse-up for any simulated button this mod is holding, once the key that put
        /// it down is no longer down. Called every frame from Update regardless of
        /// enableCustomKeybinds. See the call site for why.
        ///
        /// The test is "the mod holds the button AND its key is not pressed" rather than
        /// wasReleasedThisFrame. That is a superset of the release edge: it still fires on the frame
        /// the key comes up, and it additionally recovers when the edge is never observed at all:
        /// an InputSystem device reset on focus loss clears the key without reporting a release, a
        /// press and release inside one frame reports both edges at once (the old `if pressed else
        /// if released` chain took only the press and stuck), and rebinding LeftClickHold while it
        /// is held moves the edge to a control nobody is watching. Each of those left the button
        /// virtually down for the rest of the session.
        ///
        /// Keys come from the same config entries CheckKeybinds reads, so a rebound
        /// LeftClickHold/RightClickHold releases on the key the user actually bound.
        /// </summary>
        private void ReleaseHeldSimulatedClicks()
        {
            if (simulatedLeftButtonHeld && !IsKeybindKeyPressed("LeftClickHold"))
            {
                SimulateMouseClick(0, false);
            }

            if (simulatedRightButtonHeld && !IsKeybindKeyPressed("RightClickHold"))
            {
                SimulateMouseClick(1, false);
            }
        }

        /// <summary>
        /// Is the key currently bound to <paramref name="keybindName"/> physically down?
        ///
        /// False when there is no keyboard, when the bind is missing, and when it is Key.None.
        /// Key.None can never be held, and Keyboard's indexer does not merely return null for it:
        /// the shipped Unity.InputSystem.dll decompiles to
        /// "int num = (int)(key - 1); if (num is out of m_Keys range) throw
        /// ArgumentOutOfRangeException", so Key.None (0) maps to index -1 and throws. Unlike
        /// CheckKeybinds this runs OUTSIDE the enableCustomKeybinds gate, where a throw would take
        /// the rest of Update (look-at tracking, the reached-target sound, main-menu and quick-menu
        /// activation) down with it every frame. So the bad values are filtered here, not indexed.
        /// </summary>
        private static bool IsKeybindKeyPressed(string keybindName)
        {
            // ModKeybinds filters Key.None and IMESelected itself, so this can never index the
            // keyboard with a value that throws.
            return ModKeybinds.IsPressed(keybindName);
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
                    // Recorded only after the event is queued, so ReleaseHeldSimulatedClicks
                    // tracks what was actually sent rather than what was attempted.
                    simulatedLeftButtonHeld = isPressed;
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
                    simulatedRightButtonHeld = isPressed;
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
                GameObject previousElement = previouslyFocusedUIElements[previouslyFocusedUIElements.Count - 1];
                previouslyFocusedUIElements.RemoveAt(previouslyFocusedUIElements.Count - 1);
                if (previousElement != null && previousElement.activeInHierarchy)
                {
                    EventSystem.current.SetSelectedGameObject(previousElement);
                }
            }
        }

        // LACore.SetUINavigation and LACore.SetupCustomUINavigation were deleted here.
        // Both were dead: nothing outside this file ever called SetUINavigation, and its
        // fallback branch was only reachable when uiAccessibilityManager was null, which it never
        // is after Awake. SetupCustomUINavigation was also broken on its own terms: it assigned
        // selectOnUp/selectOnDown but never set Navigation.mode = Explicit, and Unity ignores the
        // explicit links entirely while the mode is Automatic, so even if it had run it would have
        // done nothing. UIAccessibilityManager.SetNavigation / ApplyCustomNavigation is the single
        // remaining path, and NavigationEnforcer is the one that sets Explicit correctly.

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
                        uiAccessibilityManager.ValidateRegisteredPaths();
                        uiAccessibilityManager.ApplyCustomNavigation();
                    }
                }
            }
            else
            {
                mainMenuActivated = false;
            }
        }

        // GameObject.Find walks the whole active hierarchy and was previously called every
        // frame. The QuickMenu object is created once per gameplay scene, so cache it and
        // only re-search when the cached reference has been destroyed.
        private GameObject cachedQuickMenu;

        private void CheckQuickMenuActivation()
        {
            if (cachedQuickMenu == null)
            {
                cachedQuickMenu = GameObject.Find("QuickMenu");
            }

            GameObject quickMenu = cachedQuickMenu;
            if (quickMenu != null && quickMenu.activeSelf)
            {
                if (!quickMenuActivated)
                {
                    quickMenuActivated = true;
                    if (uiAccessibilityManager != null)
                    {
                        uiAccessibilityManager.ApplyCustomNavigation();
                    }
                }
            }
            else
            {
                quickMenuActivated = false;
            }
        }

        // Notices when the player reaches the tracked object.

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

                // For factory entrances/exits and other registered items, use a slightly larger stopping distance
                bool isRegisteredItem = NavMenu.IsRegisteredMenuItem(currentLookTarget.name);
                if (isRegisteredItem)
                {
                    stoppingDistance = Mathf.Max(stoppingDistance, 3.0f); // Ensure minimum 3.0f for registered items
                }

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

                        if (reachedPositionSound != null)
                        {
                            GameObject tempAudio = new GameObject("TempReachedPositionAudio");
                            tempAudio.transform.position = currentLookTarget.transform.position;

                            AudioSource audioSource = tempAudio.AddComponent<AudioSource>();
                            AudioSystemBypass.ConfigureAudioSourceForBypass(audioSource, 0.7f);
                            audioSource.clip = reachedPositionSound;
                            audioSource.spatialBlend = 1f;
                            audioSource.minDistance = 0f;
                            audioSource.maxDistance = 5f;
                            audioSource.Play();

                            Destroy(tempAudio, reachedPositionSound.length + 0.1f);

                            Debug.Log($"Playing reached sound for {objectName} at distance {distance:F2}m");
                        }
                        else
                        {
                            Debug.LogWarning("Reached position sound not loaded");
                        }

                        SpeechUtils.SpeakText("Reached " + objectName);
                        hasPlayedReachedSound = true;

                        Debug.Log($"Reached {objectName} (distance: {distance:F2}m, stopping distance: {stoppingDistance:F2}m)");
                    }
                }
                else
                {
                    // Reset the flag when we move away from the object
                    if (distance > stoppingDistance + 1.0f) // Add some hysteresis to prevent flickering
                    {
                        hasPlayedReachedSound = false;
                    }
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

        // The stored name of the UI navigation bind. Kept as-is because it is a config key: a
        // rename would silently discard whatever the user had rebound it to.
        private const string UiNavigationKeybindName = ModKeybinds.UiNavigation;

        private void RegisterKeybinds()
        {
            RegisterKeybind("NavigateToLookingObject", Key.P, PathfindToSelected);
            RegisterKeybind("StopLookingAndPathfinding", Key.O, StopLookingAndPathfinding);
            RegisterKeybind(UiNavigationKeybindName, Key.Tab, FocusPreviousUIElement);
            // Mouse clicks are held on LeftAlt and RightAlt.
            RegisterKeybind("LeftClickHold", Key.LeftAlt, delegate
            {
                SimulateMouseClick(0, true);
            });
            RegisterKeybind("RightClickHold", Key.RightAlt, delegate
            {
                SimulateMouseClick(1, true);
            });
            RegisterKeybind("ToggleNorthSound", Key.N, ToggleNorthSound);
            RegisterKeybind("SpeakPlayerDirection", Key.L, SpeakPlayerDirection);
            RegisterKeybind("AnnounceCurrentRoom", Key.R, AnnounceCurrentRoom);

            // F10 is NOT registered here. SettingsMenu.Initialize owns an always-enabled
            // InputAction bound to <Keyboard>/f10 that calls ToggleMenu, and having both meant the
            // menu could be opened but not closed: AccessibleMenuSystem.CloseMenu restores
            // enableCustomKeybinds before LACore.Update runs in the same frame, so CheckKeybinds
            // saw the same F10 press still flagged wasPressedThisFrame and toggled straight back
            // open. The InputAction is the surviving binding because it works while
            // enableCustomKeybinds is false, which is exactly the state the menu puts the mod in.
        }

        /// <summary>
        /// Everything THIS mod does with a physical key, by a name a person can say. Used by the
        /// keybinds screen to say when a game action has been given a key that LethalAccess also
        /// acts on, which is a real in-game conflict: both fire on the one press.
        /// </summary>
        public static List<string> DescribeModKeyUses(Key key)
        {
            // Only the keys that are NOT rows on the remap screen; ModKeybinds skips the rest.
            return ModKeybinds.UsesOfKey(key);
        }

        /// <summary>
        /// True while the remap-controls screen is open. The mod's own hotkeys must not also act
        /// on keys pressed there: a key being bound is a key press like any other, and an
        /// InputUtils rebind (unlike the vanilla one) does not set IsRebindInProgress.
        /// </summary>
        private static bool IsRemapScreenOpen()
        {
            return KeybindsAccessibility.IsPanelOpen;
        }

        public void RegisterKeybind(string keybindName, Key defaultKey, Action action)
        {
            if (!registeredActions.ContainsKey(keybindName))
            {
                registeredActions[keybindName] = action;
                // The key itself is ModKeybinds' table entry; defaultKey only matters for a name
                // the table does not know.
                ModKeybinds.EnsureDefinition(keybindName, defaultKey);
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
                SpeechUtils.SpeakText("Stopped looking at " + objectName);
                currentLookTarget = null;
            }

            if (pathfinder != null && pathfinder.IsPathfinding)
            {
                pathfinder.StopPathfinding();
                SpeechUtils.SpeakText("Stopped pathfinding");
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
                    SpeechUtils.SpeakText("Object not found. Make sure you land the ship before attempting to pathfind.");
                }
            }
        }

        private void ToggleNorthSound()
        {
            northSoundManager.ToggleNorthSound();
            string toggleState = northSoundManager.isEnabled ? "enabled" : "disabled";
            SpeechUtils.SpeakText("North Sound " + toggleState);
        }

        // OpenSettingsMenu() was deleted with the duplicate F10 keybind above. SettingsMenu
        // toggles itself from its own InputAction; this wrapper had no other caller.

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
                SpeechUtils.SpeakText(message);
            }
            else
            {
                SpeechUtils.SpeakText("Player position not available");
            }
        }

        private string GetCompassDirection(float angle)
        {
            string[] directions = new string[8] { "North", "Northeast", "East", "Southeast", "South", "Southwest", "West", "Northwest" };
            int index = Mathf.RoundToInt(angle / 45f) % 8;
            return directions[index];
        }

        public IEnumerator PlayAudioClipCoroutine(string audioFilePath, GameObject targetGameObject, float minDistance, float maxDistance)
        {
            if (targetGameObject == null)
            {
                Debug.LogError("Target GameObject is null. Cannot play audio clip.");
                yield break;
            }

            yield return StartCoroutine(AudioUtils.LoadAndPlayAudioClip(audioFilePath, targetGameObject, minDistance, maxDistance));
        }

        private void OnDestroy()
        {
            // Teardown must undo Awake symmetrically, otherwise the static state below survives
            // into the next plugin instance holding references to destroyed objects.
            StopAllCoroutines();
            SpeechSynthesizer.Cleanup();

            if (uiAccessibilityManager != null)
            {
                Destroy(uiAccessibilityManager);
            }
            if (settingsMenu != null)
            {
                Destroy(settingsMenu);
            }

            currentLookTarget = null;
            playerTransform = null;
            cameraTransform = null;
            cachedQuickMenu = null;
            registeredActions.Clear();
            overriddenTexts.Clear();

            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}