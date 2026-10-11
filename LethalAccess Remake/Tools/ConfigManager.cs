using BepInEx.Configuration;
using System.Collections.Generic;
using UnityEngine;

namespace LethalAccess
{
    /// <summary>
    /// Centralized configuration manager for LethalAccess settings
    /// </summary>
    public static class ConfigManager
    {
        private static ConfigFile _config;

        // Audio Settings
        public static ConfigEntry<float> MasterVolume { get; private set; }
        public static ConfigEntry<float> NavigationSoundVolume { get; private set; }
        public static ConfigEntry<float> NorthSoundInterval { get; private set; }

        // Movement Settings
        public static ConfigEntry<float> TurnSpeed { get; private set; }
        public static ConfigEntry<float> SnapTurnAngle { get; private set; }

        // Navigation Settings
        public static ConfigEntry<float> PathfindingStoppingRadius { get; private set; }
        public static ConfigEntry<bool> EnableVisualMarkers { get; private set; }
        public static ConfigEntry<float> ScanRadius { get; private set; }
        public static ConfigEntry<bool> AutoPathfindingSprint { get; private set; }

        // Game Flow Settings
        public static ConfigEntry<bool> SkipIntroScreens { get; private set; }
        public static ConfigEntry<bool> DisableColdOpenEvents { get; private set; }
        public static ConfigEntry<bool> FastShipLanding { get; private set; }
        public static ConfigEntry<float> ShipLandingSpeed { get; private set; }

        // UI Settings
        public static ConfigEntry<bool> EnableUIAnnouncements { get; private set; }
        public static ConfigEntry<bool> UseAccessibilityTree { get; private set; }
        public static ConfigEntry<bool> VerticalNavigationOnly { get; private set; }
        public static ConfigEntry<bool> TabMovesBySection { get; private set; }
        public static ConfigEntry<string> SectionPrefixMode { get; private set; }

        // Accessibility Settings
        public static ConfigEntry<bool> EnableAudioCues { get; private set; }

        // Performance Settings
        public static ConfigEntry<int> MaxObjectsToScan { get; private set; }
        // Removed: ObjectScanInterval. Scans are triggered by the category keybinds, not polled,
        // so nothing could ever have read it. It was a dead control in the settings menu.

        public static void Initialize(ConfigFile config)
        {
            _config = config;

            // Audio Settings
            MasterVolume = config.Bind("Audio", "MasterVolume", 1.0f,
                new ConfigDescription("Master volume for all LethalAccess sounds", new AcceptableValueRange<float>(0f, 1f)));

            NavigationSoundVolume = config.Bind("Audio", "NavigationSoundVolume", 0.8f,
                new ConfigDescription("Volume for navigation audio cues", new AcceptableValueRange<float>(0f, 1f)));

            NorthSoundInterval = config.Bind("Audio", "NorthSoundInterval", 1.5f,
                new ConfigDescription("Interval between north sound plays in seconds", new AcceptableValueRange<float>(0.5f, 5f)));

            // Movement Settings
            TurnSpeed = config.Bind("Movement", "TurnSpeed", 90f,
                new ConfigDescription("Turning speed in degrees per second", new AcceptableValueRange<float>(30f, 360f)));

            SnapTurnAngle = config.Bind("Movement", "SnapTurnAngle", 45f,
                new ConfigDescription("Angle for snap turning in degrees", new AcceptableValueRange<float>(15f, 90f)));

            // Navigation Settings
            PathfindingStoppingRadius = config.Bind("Navigation", "PathfindingStoppingRadius", 2.2f,
                new ConfigDescription("Distance from target to stop pathfinding", new AcceptableValueRange<float>(0.5f, 10f)));

            EnableVisualMarkers = config.Bind("Navigation", "EnableVisualMarkers", true,
                "Enable visual markers for navigation points");

            ScanRadius = config.Bind("Navigation", "ScanRadius", 80f,
                new ConfigDescription("Radius to scan for objects", new AcceptableValueRange<float>(20f, 200f)));

            AutoPathfindingSprint = config.Bind("Navigation", "AutoPathfindingSprint", true,
                "Automatic pathfinding runs instead of walking. It uses stamina like holding the sprint " +
                "key, and slows to a walk when you are exhausted.");

            // Game Flow Settings
            SkipIntroScreens = config.Bind("GameFlow", "SkipIntroScreens", true,
                "After choosing Online or LAN, go straight to the main menu: no boot-up screen and no " +
                "main menu opening animation.");

            DisableColdOpenEvents = config.Bind("GameFlow", "DisableColdOpenEvents", true,
                "Never play the cold open cinematics the game sometimes shows when it starts (on the " +
                "seventh launch, and the boot-up error scene).");

            FastShipLanding = config.Bind("GameFlow", "FastShipLanding", true,
                "When you are the host, the ship lands and takes off faster. Other players see the " +
                "normal speed.");

            ShipLandingSpeed = config.Bind("GameFlow", "ShipLandingSpeed", 3f,
                new ConfigDescription("How many times faster the ship lands and takes off when Fast Ship Landing is on",
                    new AcceptableValueRange<float>(1f, 6f)));

            // UI Settings
            EnableUIAnnouncements = config.Bind("UI", "EnableUIAnnouncements", true,
                "Enable UI element announcements");

            VerticalNavigationOnly = config.Bind("UI", "VerticalNavigationOnly", true,
                "Move focus with up and down only. Left and right stop moving focus so they can adjust the " +
                "focused control instead; sliders keep their value adjustment either way.");

            TabMovesBySection = config.Bind("UI", "TabMovesBySection", true,
                "Ctrl+Tab jumps to the next section of the current screen and Ctrl+Shift+Tab to the " +
                "previous one, announcing the section name on arrival. Turn this off to get the " +
                "focus-history walk on Ctrl+Tab instead. Tab on its own always moves one control.");

            UseAccessibilityTree = config.Bind("UI", "UseAccessibilityTree", true,
                "Publish menus as a real accessibility tree so your screen reader reads them with " +
                "its own wording, verbosity and braille, instead of this mod composing sentences " +
                "and pushing them at it. Needs accesskit.dll next to the game executable; if it is " +
                "missing or fails to attach, the mod falls back to speaking menus itself. " +
                "Gameplay speech is unaffected either way.");

            SectionPrefixMode = config.Bind("UI", "SectionPrefixMode", "OnChange",
                "How much context each announcement carries: \"Graphics, Terrain / Grass Detail, " +
                "selected: High, dropdown, 1 of 3\". OnChange = only on the first control you focus " +
                "after entering a different section or screen, so walking one section does not repeat " +
                "its name on every line. Always = on every announcement. Off = no context at all.");

            // Accessibility Settings
            EnableAudioCues = config.Bind("Accessibility", "EnableAudioCues", true,
                "Enable audio cues for navigation and interaction");

            // Performance Settings
            MaxObjectsToScan = config.Bind("Performance", "MaxObjectsToScan", 100,
                new ConfigDescription("Maximum number of objects to scan at once", new AcceptableValueRange<int>(50, 500)));
        }

        /// <summary>
        /// Get all configuration entries for the settings menu
        /// </summary>
        public static Dictionary<string, List<(string name, ConfigEntryBase entry)>> GetAllConfigEntries()
        {
            var categories = new Dictionary<string, List<(string, ConfigEntryBase)>>();

            categories["Audio"] = new List<(string, ConfigEntryBase)>
            {
                ("Master Volume", MasterVolume),
                ("Navigation Sound Volume", NavigationSoundVolume),
                ("North Sound Interval", NorthSoundInterval)
            };

            categories["Movement"] = new List<(string, ConfigEntryBase)>
            {
                ("Turn Speed", TurnSpeed),
                ("Snap Turn Angle", SnapTurnAngle)
            };

            categories["Navigation"] = new List<(string, ConfigEntryBase)>
            {
                ("Pathfinding Stopping Radius", PathfindingStoppingRadius),
                ("Enable Visual Markers", EnableVisualMarkers),
                ("Scan Radius", ScanRadius),
                ("Auto Pathfinding Sprint", AutoPathfindingSprint)
            };

            categories["Game Flow"] = new List<(string, ConfigEntryBase)>
            {
                ("Skip Intro Screens", SkipIntroScreens),
                ("Disable Cold Open Events", DisableColdOpenEvents),
                ("Fast Ship Landing", FastShipLanding),
                ("Ship Landing Speed", ShipLandingSpeed)
            };

            categories["UI"] = new List<(string, ConfigEntryBase)>
            {
                ("Enable UI Announcements", EnableUIAnnouncements),
                ("Vertical Navigation Only", VerticalNavigationOnly),
                ("Tab Moves By Section", TabMovesBySection),
                ("Section Prefix Mode", SectionPrefixMode)
            };

            categories["Accessibility"] = new List<(string, ConfigEntryBase)>
            {
                ("Enable Audio Cues", EnableAudioCues)
            };

            categories["Performance"] = new List<(string, ConfigEntryBase)>
            {
                ("Max Objects To Scan", MaxObjectsToScan)
            };

            return categories;
        }

        /// <summary>
        /// Save all configuration changes
        /// </summary>
        public static void SaveConfig()
        {
            _config?.Save();
        }

        /// <summary>
        /// Reset all settings to default values
        /// </summary>
        public static void ResetToDefaults()
        {
            MasterVolume.Value = (float)MasterVolume.DefaultValue;
            NavigationSoundVolume.Value = (float)NavigationSoundVolume.DefaultValue;
            NorthSoundInterval.Value = (float)NorthSoundInterval.DefaultValue;
            TurnSpeed.Value = (float)TurnSpeed.DefaultValue;
            SnapTurnAngle.Value = (float)SnapTurnAngle.DefaultValue;
            PathfindingStoppingRadius.Value = (float)PathfindingStoppingRadius.DefaultValue;
            EnableVisualMarkers.Value = (bool)EnableVisualMarkers.DefaultValue;
            ScanRadius.Value = (float)ScanRadius.DefaultValue;
            AutoPathfindingSprint.Value = (bool)AutoPathfindingSprint.DefaultValue;
            SkipIntroScreens.Value = (bool)SkipIntroScreens.DefaultValue;
            DisableColdOpenEvents.Value = (bool)DisableColdOpenEvents.DefaultValue;
            FastShipLanding.Value = (bool)FastShipLanding.DefaultValue;
            ShipLandingSpeed.Value = (float)ShipLandingSpeed.DefaultValue;
            EnableUIAnnouncements.Value = (bool)EnableUIAnnouncements.DefaultValue;
            VerticalNavigationOnly.Value = (bool)VerticalNavigationOnly.DefaultValue;
            TabMovesBySection.Value = (bool)TabMovesBySection.DefaultValue;
            SectionPrefixMode.Value = (string)SectionPrefixMode.DefaultValue;
            EnableAudioCues.Value = (bool)EnableAudioCues.DefaultValue;
            MaxObjectsToScan.Value = (int)MaxObjectsToScan.DefaultValue;

            SaveConfig();
        }
    }
}
