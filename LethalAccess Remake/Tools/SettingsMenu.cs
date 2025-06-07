using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using BepInEx.Configuration;

namespace LethalAccess
{
    /// <summary>
    /// Settings menu using the AccessibleMenuSystem backend
    /// </summary>
    public class SettingsMenu : AccessibleMenuSystem
    {
        protected override string MenuTitle => "Settings menu";

        // Rebinding state
        private bool isWaitingForKey = false;
        private ConfigEntry<Key> currentKeybindEntry = null;
        private InputAction toggleAction;

        public void Initialize()
        {
            // Create F10 toggle action that's always enabled
            toggleAction = new InputAction("ToggleSettings", InputActionType.Button, "<Keyboard>/f10");
            toggleAction.performed += _ => ToggleMenu();
            toggleAction.Enable();

            Debug.Log("SettingsMenu initialized with AccessibleMenuSystem");
        }

        public void ToggleMenu()
        {
            if (isWaitingForKey) return;

            if (isMenuOpen)
            {
                CloseMenu();
            }
            else
            {
                OpenMenu();
            }
        }

        protected override void BuildMenuEntries()
        {
            ClearMenuEntries();

            // Audio Settings
            AddMenuItem(() => $"Master Volume: {GetVolumeText(ConfigManager.MasterVolume.Value)}",
                onModify: delta => ModifyVolume(ConfigManager.MasterVolume, delta));

            AddMenuItem(() => $"Navigation Sound Volume: {GetVolumeText(ConfigManager.NavigationSoundVolume.Value)}",
                onModify: delta => ModifyVolume(ConfigManager.NavigationSoundVolume, delta));

            AddMenuItem(() => $"North Sound Interval: {ConfigManager.NorthSoundInterval.Value:F1}s",
                onModify: delta => ModifyFloat(ConfigManager.NorthSoundInterval, delta * 0.1f, 0.5f, 5f));

            // Movement Settings
            AddMenuItem(() => $"Turn Speed: {ConfigManager.TurnSpeed.Value:F0}°/s",
                onModify: delta => ModifyFloat(ConfigManager.TurnSpeed, delta * 5f, 30f, 360f));

            AddMenuItem(() => $"Snap Turn Angle: {ConfigManager.SnapTurnAngle.Value:F0}°",
                onModify: delta => ModifyFloat(ConfigManager.SnapTurnAngle, delta * 5f, 15f, 90f));

            // Navigation Settings
            AddMenuItem(() => $"Pathfinding Stopping Radius: {ConfigManager.PathfindingStoppingRadius.Value:F1}m",
                onModify: delta => ModifyFloat(ConfigManager.PathfindingStoppingRadius, delta * 0.5f, 0.5f, 10f));

            AddMenuItem(() => $"Enable Visual Markers: {(ConfigManager.EnableVisualMarkers.Value ? "enabled" : "disabled")}",
                onSelect: () => ToggleBool(ConfigManager.EnableVisualMarkers));

            AddMenuItem(() => $"Scan Radius: {ConfigManager.ScanRadius.Value:F0}m",
                onModify: delta => ModifyFloat(ConfigManager.ScanRadius, delta * 5f, 20f, 200f));

            // UI Settings
            AddMenuItem(() => $"Enable UI Announcements: {(ConfigManager.EnableUIAnnouncements.Value ? "enabled" : "disabled")}",
                onSelect: () => ToggleBool(ConfigManager.EnableUIAnnouncements));

            // Accessibility Settings
            AddMenuItem(() => $"Enable Audio Cues: {(ConfigManager.EnableAudioCues.Value ? "enabled" : "disabled")}",
                onSelect: () => ToggleBool(ConfigManager.EnableAudioCues));

            // Performance Settings
            AddMenuItem(() => $"Max Objects To Scan: {ConfigManager.MaxObjectsToScan.Value}",
                onModify: delta => ModifyInt(ConfigManager.MaxObjectsToScan, delta * 5, 50, 500));

            AddMenuItem(() => $"Object Scan Interval: {ConfigManager.ObjectScanInterval.Value:F2}s",
                onModify: delta => ModifyFloat(ConfigManager.ObjectScanInterval, delta * 0.01f, 0.05f, 1f));

            // Keybinds
            AddKeybindEntries();

            // Settings Actions
            AddMenuItem("Reset All Settings: Press Enter to Reset",
                onSelect: ResetAllSettings);

            Debug.Log($"Built {menuEntries.Count} menu entries");
        }

        private void AddMenuItem(Func<string> textProvider, Action onSelect = null, Action<int> onModify = null, Func<bool> canInteract = null)
        {
            menuEntries.Add(new DynamicMenuEntry(textProvider, onSelect, onModify, canInteract));
        }

        private void AddMenuItem(string staticText, Action onSelect = null, Action<int> onModify = null, Func<bool> canInteract = null)
        {
            menuEntries.Add(new DynamicMenuEntry(staticText, onSelect, onModify, canInteract));
        }

        private void AddKeybindEntries()
        {
            try
            {
                var lacoreType = typeof(LACore);
                var keybindField = lacoreType.GetField("keybindConfigEntries",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

                if (keybindField != null)
                {
                    var keybindEntries = (Dictionary<string, ConfigEntry<Key>>)keybindField.GetValue(null);
                    if (keybindEntries != null)
                    {
                        foreach (var kvp in keybindEntries)
                        {
                            var entry = kvp.Value;
                            string friendlyName = GetKeybindFriendlyName(kvp.Key);

                            AddMenuItem(() => $"{friendlyName}: {GetKeyDisplayName(entry.Value)}",
                                onSelect: () => StartKeybindRebind(entry));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to load keybinds: {ex.Message}");
                AddMenuItem("Error loading keybinds", canInteract: () => false);
            }
        }

        // Custom menu entry class for dynamic text
        private class DynamicMenuEntry : IMenuEntry
        {
            private Func<string> textProvider;
            private Action onSelectAction;
            private Action<int> onModifyAction;
            private Func<bool> canInteractFunc;

            public DynamicMenuEntry(Func<string> textProvider, Action onSelect = null, Action<int> onModify = null, Func<bool> canInteract = null)
            {
                this.textProvider = textProvider;
                this.onSelectAction = onSelect;
                this.onModifyAction = onModify;
                this.canInteractFunc = canInteract ?? (() => true);
            }

            public DynamicMenuEntry(string staticText, Action onSelect = null, Action<int> onModify = null, Func<bool> canInteract = null)
                : this(() => staticText, onSelect, onModify, canInteract)
            {
            }

            public string GetDisplayText() => textProvider();
            public bool CanInteract() => canInteractFunc();
            public void OnSelect() => onSelectAction?.Invoke();
            public void OnModify(int direction) => onModifyAction?.Invoke(direction);
            public bool HasModification() => onModifyAction != null;
        }

        protected override void OnMenuClosed()
        {
            ConfigManager.SaveConfig();
        }

        protected override void Update()
        {
            base.Update();

            if (isWaitingForKey)
            {
                HandleKeybindInput();
            }
        }

        protected override void OnBackPressed()
        {
            if (isWaitingForKey)
            {
                CancelKeybindRebind();
            }
            else
            {
                base.OnBackPressed(); // Call the base implementation for escape handling
            }
        }

        private void HandleKeybindInput()
        {
            if (Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                CancelKeybindRebind();
                return;
            }

            foreach (Key key in System.Enum.GetValues(typeof(Key)))
            {
                if (IsInvalidRebindKey(key)) continue;

                try
                {
                    if (Keyboard.current[key].wasPressedThisFrame)
                    {
                        SetKeybind(key);
                        return;
                    }
                }
                catch (System.ArgumentOutOfRangeException)
                {
                    continue;
                }
            }
        }

        private bool IsInvalidRebindKey(Key key)
        {
            return key == Key.None ||
                   key == Key.Escape ||
                   key == Key.Enter ||
                   key == Key.Space ||
                   key == Key.UpArrow ||
                   key == Key.DownArrow ||
                   key == Key.LeftArrow ||
                   key == Key.RightArrow ||
                   key == Key.F10;
        }

        private void StartKeybindRebind(ConfigEntry<Key> entry)
        {
            isWaitingForKey = true;
            currentKeybindEntry = entry;
            SpeechUtils.SpeakText($"Press a key to rebind this action, or Escape to cancel");
        }

        private void SetKeybind(Key newKey)
        {
            if (currentKeybindEntry != null)
            {
                string conflictingAction = CheckKeybindConflict(newKey);
                if (!string.IsNullOrEmpty(conflictingAction))
                {
                    SpeechUtils.SpeakText($"Key {GetKeyDisplayName(newKey)} is already used for {conflictingAction}. Press Escape to cancel or try another key.");
                    return;
                }

                currentKeybindEntry.Value = newKey;
                isWaitingForKey = false;

                string keyName = GetKeyDisplayName(newKey);
                SpeechUtils.SpeakText($"Successfully rebound to {keyName}");
                currentKeybindEntry = null;
                RefreshCurrentItem();
            }
        }

        private string CheckKeybindConflict(Key newKey)
        {
            try
            {
                var lacoreType = typeof(LACore);
                var keybindField = lacoreType.GetField("keybindConfigEntries",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

                if (keybindField != null)
                {
                    var keybindEntries = (Dictionary<string, ConfigEntry<Key>>)keybindField.GetValue(null);
                    if (keybindEntries != null)
                    {
                        foreach (var kvp in keybindEntries)
                        {
                            if (kvp.Value != currentKeybindEntry && kvp.Value.Value == newKey)
                            {
                                return GetKeybindFriendlyName(kvp.Key);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error checking keybind conflicts: {ex.Message}");
            }

            return null;
        }

        private void CancelKeybindRebind()
        {
            if (currentKeybindEntry != null)
            {
                isWaitingForKey = false;
                currentKeybindEntry = null;
                SpeechUtils.SpeakText("Cancelled rebinding");
                RefreshCurrentItem();
            }
        }

        private void ResetAllSettings()
        {
            SpeechUtils.SpeakText("Are you sure you want to reset all settings to default? Press Enter to confirm, or Escape to cancel.");
            StartCoroutine(WaitForResetConfirmation());
        }

        private System.Collections.IEnumerator WaitForResetConfirmation()
        {
            bool waitingForConfirmation = true;

            while (waitingForConfirmation)
            {
                if (Keyboard.current.enterKey.wasPressedThisFrame)
                {
                    try
                    {
                        ConfigManager.ResetToDefaults();
                        BuildMenuEntries(); // Rebuild with new values
                        currentItemIndex = 0;
                        SpeechUtils.SpeakText("All settings have been reset to default values");
                        RefreshCurrentItem();
                        waitingForConfirmation = false;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error resetting settings: {ex.Message}");
                        SpeechUtils.SpeakText("Error resetting settings");
                        waitingForConfirmation = false;
                    }
                }
                else if (Keyboard.current.escapeKey.wasPressedThisFrame)
                {
                    SpeechUtils.SpeakText("Reset cancelled");
                    RefreshCurrentItem();
                    waitingForConfirmation = false;
                }

                yield return null;
            }
        }

        // Helper methods
        private string GetKeybindFriendlyName(string key)
        {
            var friendlyNames = new Dictionary<string, string>
            {
                {"NavigateToLookingObject", "Navigate to Selected Object"},
                {"StopLookingAndPathfinding", "Stop Looking and Pathfinding"},
                {"FocusPreviousUIElement", "Focus Previous UI Element"},
                {"LeftClickHold", "Left Click Hold"},
                {"RightClickHold", "Right Click Hold"},
                {"ToggleNorthSound", "Toggle North Sound"},
                {"SpeakPlayerDirection", "Speak Player Direction"},
                {"DrawElevatorRay", "Draw Elevator Ray"},
                {"AnnounceCurrentRoom", "Announce Current Room"},
                {"OpenSettingsMenu", "Open Settings Menu"},
                {"MoveToNextItem", "Move to Next Item"},
                {"MoveToPreviousItem", "Move to Previous Item"},
                {"MoveToNextCategory", "Move to Next Category"},
                {"MoveToPreviousCategory", "Move to Previous Category"},
                {"RefreshCurrentCategory", "Refresh Current Category"},
                {"InstantNavigation1", "Quick Nav 1"},
                {"InstantNavigation2", "Quick Nav 2"},
                {"InstantNavigation3", "Quick Nav 3"},
                {"InstantNavigation4", "Quick Nav 4"},
                {"InstantNavigation5", "Quick Nav 5"},
                {"InstantNavigation6", "Quick Nav 6"},
                {"SpeakProfitQuota", "Speak Profit Quota"},
                {"SpeakPlayerHealth", "Speak Player Health"},
                {"SpeakTime", "Speak Time"}
            };

            return friendlyNames.TryGetValue(key, out string friendlyName) ? friendlyName : key;
        }

        private string GetKeyDisplayName(Key key)
        {
            var keyNames = new Dictionary<Key, string>
            {
                {Key.LeftBracket, "Left Bracket"},
                {Key.RightBracket, "Right Bracket"},
                {Key.Semicolon, "Semicolon"},
                {Key.Quote, "Quote"},
                {Key.Minus, "Minus"},
                {Key.Equals, "Equals"},
                {Key.LeftAlt, "Left Alt"},
                {Key.RightAlt, "Right Alt"},
                {Key.F1, "F1"}, {Key.F2, "F2"}, {Key.F3, "F3"}, {Key.F4, "F4"},
                {Key.F5, "F5"}, {Key.F6, "F6"}, {Key.F7, "F7"}, {Key.F8, "F8"},
                {Key.F9, "F9"}, {Key.F10, "F10"}, {Key.F11, "F11"}, {Key.F12, "F12"},
                {Key.Digit0, "0"}, {Key.Digit1, "1"}, {Key.Digit2, "2"}, {Key.Digit3, "3"},
                {Key.Digit4, "4"}, {Key.Digit5, "5"}, {Key.Digit6, "6"}, {Key.Digit7, "7"},
                {Key.Digit8, "8"}, {Key.Digit9, "9"},
                {Key.Tab, "Tab"}, {Key.CapsLock, "Caps Lock"}, {Key.LeftShift, "Left Shift"},
                {Key.RightShift, "Right Shift"}, {Key.LeftCtrl, "Left Control"}, {Key.RightCtrl, "Right Control"},
                {Key.Period, "Period"}, {Key.Comma, "Comma"}, {Key.Slash, "Slash"}, {Key.Backslash, "Backslash"},
                {Key.Backquote, "Backquote"}, {Key.Insert, "Insert"}, {Key.Delete, "Delete"},
                {Key.Home, "Home"}, {Key.End, "End"}, {Key.PageUp, "Page Up"}, {Key.PageDown, "Page Down"},
                {Key.None, "None"}
            };

            return keyNames.TryGetValue(key, out string name) ? name : key.ToString();
        }

        private string GetVolumeText(float volume)
        {
            return $"{Mathf.RoundToInt(volume * 100)}%";
        }

        // Configuration modification helpers
        private void ModifyVolume(ConfigEntry<float> entry, int delta)
        {
            float newValue = Mathf.Clamp(entry.Value + (delta * 0.05f), 0f, 1f);
            entry.Value = newValue;
        }

        private void ModifyFloat(ConfigEntry<float> entry, float deltaAmount, float min, float max)
        {
            float newValue = Mathf.Clamp(entry.Value + deltaAmount, min, max);
            entry.Value = newValue;
        }

        private void ModifyInt(ConfigEntry<int> entry, int deltaAmount, int min, int max)
        {
            int newValue = Mathf.Clamp(entry.Value + deltaAmount, min, max);
            entry.Value = newValue;
        }

        private void ToggleBool(ConfigEntry<bool> entry)
        {
            entry.Value = !entry.Value;
        }

        protected override void OnDestroy()
        {
            toggleAction?.Disable();
            toggleAction?.Dispose();
            base.OnDestroy();
        }
    }
}