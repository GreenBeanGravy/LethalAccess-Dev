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

        // UI Settings
        public static ConfigEntry<bool> EnableUIAnnouncements { get; private set; }

        // Accessibility Settings
        public static ConfigEntry<bool> EnableAudioCues { get; private set; }

        // Performance Settings
        public static ConfigEntry<int> MaxObjectsToScan { get; private set; }
        public static ConfigEntry<float> ObjectScanInterval { get; private set; }

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

            // UI Settings
            EnableUIAnnouncements = config.Bind("UI", "EnableUIAnnouncements", true,
                "Enable UI element announcements");

            // Accessibility Settings
            EnableAudioCues = config.Bind("Accessibility", "EnableAudioCues", true,
                "Enable audio cues for navigation and interaction");

            // Performance Settings
            MaxObjectsToScan = config.Bind("Performance", "MaxObjectsToScan", 100,
                new ConfigDescription("Maximum number of objects to scan at once", new AcceptableValueRange<int>(50, 500)));

            ObjectScanInterval = config.Bind("Performance", "ObjectScanInterval", 0.1f,
                new ConfigDescription("Interval between object scans in seconds", new AcceptableValueRange<float>(0.05f, 1f)));
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
                ("Scan Radius", ScanRadius)
            };

            categories["UI"] = new List<(string, ConfigEntryBase)>
            {
                ("Enable UI Announcements", EnableUIAnnouncements)
            };

            categories["Accessibility"] = new List<(string, ConfigEntryBase)>
            {
                ("Enable Audio Cues", EnableAudioCues)
            };

            categories["Performance"] = new List<(string, ConfigEntryBase)>
            {
                ("Max Objects To Scan", MaxObjectsToScan),
                ("Object Scan Interval", ObjectScanInterval)
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
            EnableUIAnnouncements.Value = (bool)EnableUIAnnouncements.DefaultValue;
            EnableAudioCues.Value = (bool)EnableAudioCues.DefaultValue;
            MaxObjectsToScan.Value = (int)MaxObjectsToScan.DefaultValue;
            ObjectScanInterval.Value = (float)ObjectScanInterval.DefaultValue;

            SaveConfig();
        }
    }
}