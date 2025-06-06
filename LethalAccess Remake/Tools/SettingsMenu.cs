using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using BepInEx.Configuration;

namespace LethalAccess
{
    /// <summary>
    /// Simple, reliable settings menu for LethalAccess
    /// </summary>
    public class SettingsMenu : MonoBehaviour
    {
        private bool isMenuOpen = false;
        private List<MenuCategory> categories = new List<MenuCategory>();
        private int currentCategoryIndex = 0;
        private int currentItemIndex = 0;
        private string lastAnnouncedCategory = "";

        // Rebinding state
        private bool isWaitingForKey = false;
        private ConfigEntry<Key> currentKeybindEntry = null;

        // Input actions
        private InputAction[] navigationActions;
        private EventSystem originalEventSystem;
        private bool wasEventSystemEnabled;

        public class MenuItem
        {
            public string Name { get; set; }
            public string CurrentValue { get; set; }
            public Action OnSelect { get; set; }
            public Action<int> OnModify { get; set; } // 1 for right, -1 for left
            public bool IsKeybind { get; set; }

            public MenuItem(string name, string value, Action onSelect = null, Action<int> onModify = null, bool isKeybind = false)
            {
                Name = name;
                CurrentValue = value;
                OnSelect = onSelect;
                OnModify = onModify;
                IsKeybind = isKeybind;
            }
        }

        public class MenuCategory
        {
            public string Name { get; set; }
            public List<MenuItem> Items { get; set; } = new List<MenuItem>();

            public MenuCategory(string name)
            {
                Name = name;
            }
        }

        public void Initialize()
        {
            SetupInputActions();
            BuildMenu();
            Debug.Log("SettingsMenu initialized with simple approach");
        }

        private void SetupInputActions()
        {
            navigationActions = new InputAction[]
            {
                new InputAction("Up", InputActionType.Button, "<Keyboard>/upArrow"),
                new InputAction("Down", InputActionType.Button, "<Keyboard>/downArrow"),
                new InputAction("Left", InputActionType.Button, "<Keyboard>/leftArrow"),
                new InputAction("Right", InputActionType.Button, "<Keyboard>/rightArrow"),
                new InputAction("Select", InputActionType.Button, "<Keyboard>/enter"),
                new InputAction("Back", InputActionType.Button, "<Keyboard>/escape"),
                new InputAction("Toggle", InputActionType.Button, "<Keyboard>/f10")
            };

            // Enable and bind callbacks
            navigationActions[0].performed += _ => NavigateUp();
            navigationActions[1].performed += _ => NavigateDown();
            navigationActions[2].performed += _ => NavigateLeft();
            navigationActions[3].performed += _ => NavigateRight();
            navigationActions[4].performed += _ => OnSelectPressed();
            navigationActions[5].performed += _ => OnBackPressed();
            navigationActions[6].performed += _ => ToggleMenu();

            foreach (var action in navigationActions)
            {
                action.Enable();
            }
        }

        private void BuildMenu()
        {
            categories.Clear();

            // Audio Settings
            var audioCategory = new MenuCategory("Audio");
            audioCategory.Items.Add(new MenuItem("Master Volume", GetVolumeText(ConfigManager.MasterVolume.Value),
                onModify: delta => ModifyVolume(ConfigManager.MasterVolume, delta)));
            audioCategory.Items.Add(new MenuItem("Navigation Sound Volume", GetVolumeText(ConfigManager.NavigationSoundVolume.Value),
                onModify: delta => ModifyVolume(ConfigManager.NavigationSoundVolume, delta)));
            audioCategory.Items.Add(new MenuItem("North Sound Interval", $"{ConfigManager.NorthSoundInterval.Value:F1}s",
                onModify: delta => ModifyFloat(ConfigManager.NorthSoundInterval, delta * 0.1f, 0.5f, 5f)));
            categories.Add(audioCategory);

            // Movement Settings
            var movementCategory = new MenuCategory("Movement");
            movementCategory.Items.Add(new MenuItem("Turn Speed", $"{ConfigManager.TurnSpeed.Value:F0}°/s",
                onModify: delta => ModifyFloat(ConfigManager.TurnSpeed, delta * 5f, 30f, 360f)));
            movementCategory.Items.Add(new MenuItem("Snap Turn Angle", $"{ConfigManager.SnapTurnAngle.Value:F0}°",
                onModify: delta => ModifyFloat(ConfigManager.SnapTurnAngle, delta * 5f, 15f, 90f)));
            categories.Add(movementCategory);

            // Navigation Settings
            var navigationCategory = new MenuCategory("Navigation");
            navigationCategory.Items.Add(new MenuItem("Pathfinding Stopping Radius", $"{ConfigManager.PathfindingStoppingRadius.Value:F1}m",
                onModify: delta => ModifyFloat(ConfigManager.PathfindingStoppingRadius, delta * 0.5f, 0.5f, 10f)));
            navigationCategory.Items.Add(new MenuItem("Enable Visual Markers", ConfigManager.EnableVisualMarkers.Value ? "enabled" : "disabled",
                onSelect: () => ToggleBool(ConfigManager.EnableVisualMarkers)));
            navigationCategory.Items.Add(new MenuItem("Scan Radius", $"{ConfigManager.ScanRadius.Value:F0}m",
                onModify: delta => ModifyFloat(ConfigManager.ScanRadius, delta * 5f, 20f, 200f)));
            categories.Add(navigationCategory);

            // UI Settings
            var uiCategory = new MenuCategory("UI");
            uiCategory.Items.Add(new MenuItem("Enable UI Announcements", ConfigManager.EnableUIAnnouncements.Value ? "enabled" : "disabled",
                onSelect: () => ToggleBool(ConfigManager.EnableUIAnnouncements)));
            categories.Add(uiCategory);

            // Accessibility Settings
            var accessibilityCategory = new MenuCategory("Accessibility");
            accessibilityCategory.Items.Add(new MenuItem("Enable Audio Cues", ConfigManager.EnableAudioCues.Value ? "enabled" : "disabled",
                onSelect: () => ToggleBool(ConfigManager.EnableAudioCues)));
            categories.Add(accessibilityCategory);

            // Performance Settings
            var performanceCategory = new MenuCategory("Performance");
            performanceCategory.Items.Add(new MenuItem("Max Objects To Scan", ConfigManager.MaxObjectsToScan.Value.ToString(),
                onModify: delta => ModifyInt(ConfigManager.MaxObjectsToScan, delta * 5, 50, 500)));
            performanceCategory.Items.Add(new MenuItem("Object Scan Interval", $"{ConfigManager.ObjectScanInterval.Value:F2}s",
                onModify: delta => ModifyFloat(ConfigManager.ObjectScanInterval, delta * 0.01f, 0.05f, 1f)));
            categories.Add(performanceCategory);

            // Keybinds
            BuildKeybindsCategory();
        }

        private void BuildKeybindsCategory()
        {
            var keybindsCategory = new MenuCategory("Keybinds");

            // Try to get keybinds from LACore
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
                            string keyName = GetKeyDisplayName(entry.Value);

                            keybindsCategory.Items.Add(new MenuItem(friendlyName, keyName,
                                onSelect: () => StartKeybindRebind(entry), isKeybind: true));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to load keybinds: {ex.Message}");
            }

            categories.Add(keybindsCategory);
        }

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
                {Key.F1, "F1"}, {Key.F2, "F2"}, {Key.F3, "F3"}, {Key.F4, "F4"},
                {Key.F5, "F5"}, {Key.F6, "F6"}, {Key.F7, "F7"}, {Key.F8, "F8"},
                {Key.F9, "F9"}, {Key.F10, "F10"}, {Key.F11, "F11"}, {Key.F12, "F12"},
                {Key.Digit0, "0"}, {Key.Digit1, "1"}, {Key.Digit2, "2"}, {Key.Digit3, "3"},
                {Key.Digit4, "4"}, {Key.Digit5, "5"}, {Key.Digit6, "6"}, {Key.Digit7, "7"},
                {Key.Digit8, "8"}, {Key.Digit9, "9"},
                {Key.Tab, "Tab"}, {Key.CapsLock, "Caps Lock"}, {Key.LeftShift, "Left Shift"},
                {Key.RightShift, "Right Shift"}, {Key.LeftCtrl, "Left Control"}, {Key.RightCtrl, "Right Control"},
                {Key.LeftAlt, "Left Alt"}, {Key.RightAlt, "Right Alt"}, {Key.Period, "Period"},
                {Key.Comma, "Comma"}, {Key.Slash, "Slash"}, {Key.Backslash, "Backslash"},
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

        public void ToggleMenu()
        {
            if (isWaitingForKey) return;

            isMenuOpen = !isMenuOpen;
            if (isMenuOpen)
            {
                OpenMenu();
            }
            else
            {
                CloseMenu();
            }
        }

        private void OpenMenu()
        {
            DisableOtherSystems();
            BuildMenu(); // Refresh values
            currentCategoryIndex = 0;
            currentItemIndex = 0;
            lastAnnouncedCategory = "";
            SpeechUtils.SpeakText("Settings menu opened");
            AnnounceCurrentItem();
        }

        private void CloseMenu()
        {
            if (isWaitingForKey)
            {
                CancelKeybindRebind();
            }
            EnableOtherSystems();
            ConfigManager.SaveConfig();
            SpeechUtils.SpeakText("Settings menu closed. Changes saved.");
        }

        private void DisableOtherSystems()
        {
            originalEventSystem = EventSystem.current;
            if (originalEventSystem != null)
            {
                wasEventSystemEnabled = originalEventSystem.enabled;
                originalEventSystem.enabled = false;
                originalEventSystem.SetSelectedGameObject(null);
            }

            if (UIAccessibilityManager.Instance != null)
            {
                UIAccessibilityManager.Instance.enabled = false;
            }

            LACore.enableCustomKeybinds = false;
        }

        private void EnableOtherSystems()
        {
            if (originalEventSystem != null && wasEventSystemEnabled)
            {
                originalEventSystem.enabled = true;
            }

            if (UIAccessibilityManager.Instance != null)
            {
                UIAccessibilityManager.Instance.enabled = true;
            }

            LACore.enableCustomKeybinds = true;
        }

        private void Update()
        {
            if (!isMenuOpen) return;

            if (isWaitingForKey)
            {
                HandleKeybindInput();
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
                // Skip invalid keys for rebinding
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
                    // Some Key enum values aren't valid for Keyboard input - skip them
                    continue;
                }
            }
        }

        private bool IsInvalidRebindKey(Key key)
        {
            // Keys that should not be allowed for rebinding
            return key == Key.None ||
                   key == Key.Escape ||
                   key == Key.Enter ||
                   key == Key.Space ||
                   key == Key.UpArrow ||
                   key == Key.DownArrow ||
                   key == Key.LeftArrow ||
                   key == Key.RightArrow ||
                   key == Key.F10; // Don't allow rebinding the settings menu key
        }

        private void NavigateUp()
        {
            if (!isMenuOpen || isWaitingForKey) return;

            if (currentItemIndex > 0)
            {
                currentItemIndex--;
            }
            else
            {
                // Move to previous category, last item
                currentCategoryIndex = (currentCategoryIndex - 1 + categories.Count) % categories.Count;
                currentItemIndex = Mathf.Max(0, categories[currentCategoryIndex].Items.Count - 1);
            }
            AnnounceCurrentItem();
        }

        private void NavigateDown()
        {
            if (!isMenuOpen || isWaitingForKey) return;

            var currentCategory = categories[currentCategoryIndex];
            if (currentItemIndex < currentCategory.Items.Count - 1)
            {
                currentItemIndex++;
            }
            else
            {
                // Move to next category, first item
                currentCategoryIndex = (currentCategoryIndex + 1) % categories.Count;
                currentItemIndex = 0;
            }
            AnnounceCurrentItem();
        }

        private void NavigateLeft()
        {
            if (!isMenuOpen || isWaitingForKey) return;

            var currentItem = GetCurrentItem();
            if (currentItem?.OnModify != null)
            {
                currentItem.OnModify(-1);
                RefreshCurrentItem();
                AnnounceCurrentItem();
            }
        }

        private void NavigateRight()
        {
            if (!isMenuOpen || isWaitingForKey) return;

            var currentItem = GetCurrentItem();
            if (currentItem?.OnModify != null)
            {
                currentItem.OnModify(1);
                RefreshCurrentItem();
                AnnounceCurrentItem();
            }
        }

        private void OnSelectPressed()
        {
            if (!isMenuOpen) return;

            var currentItem = GetCurrentItem();
            if (currentItem?.OnSelect != null)
            {
                currentItem.OnSelect();
                if (!currentItem.IsKeybind)
                {
                    RefreshCurrentItem();
                    AnnounceCurrentItem();
                }
            }
            else if (!currentItem.IsKeybind)
            {
                SpeechUtils.SpeakText($"Use left and right arrows to modify {currentItem.Name}");
            }
        }

        private void OnBackPressed()
        {
            if (isWaitingForKey)
            {
                CancelKeybindRebind();
            }
            else if (isMenuOpen)
            {
                CloseMenu();
            }
        }

        private MenuItem GetCurrentItem()
        {
            if (currentCategoryIndex >= categories.Count) return null;
            var category = categories[currentCategoryIndex];
            if (currentItemIndex >= category.Items.Count) return null;
            return category.Items[currentItemIndex];
        }

        private void AnnounceCurrentItem()
        {
            var category = categories[currentCategoryIndex];
            var item = GetCurrentItem();

            if (item != null)
            {
                int globalIndex = GetGlobalIndex();
                int totalItems = GetTotalItemCount();
                bool isNewCategory = category.Name != lastAnnouncedCategory;

                string announcement;
                if (isNewCategory)
                {
                    announcement = $"{category.Name} category. {item.Name}: {item.CurrentValue}, {globalIndex} of {totalItems}";
                    lastAnnouncedCategory = category.Name;
                }
                else
                {
                    announcement = $"{item.Name}: {item.CurrentValue}, {globalIndex} of {totalItems}";
                }

                SpeechUtils.SpeakText(announcement);
            }
        }

        private int GetGlobalIndex()
        {
            int index = 1;
            for (int i = 0; i < currentCategoryIndex; i++)
            {
                index += categories[i].Items.Count;
            }
            index += currentItemIndex;
            return index;
        }

        private int GetTotalItemCount()
        {
            return categories.Sum(c => c.Items.Count);
        }

        private void RefreshCurrentItem()
        {
            var item = GetCurrentItem();
            if (item == null) return;

            // Update the current value display
            if (item.Name.Contains("Volume"))
            {
                if (item.Name.Contains("Master"))
                    item.CurrentValue = GetVolumeText(ConfigManager.MasterVolume.Value);
                else if (item.Name.Contains("Navigation"))
                    item.CurrentValue = GetVolumeText(ConfigManager.NavigationSoundVolume.Value);
            }
            else if (item.Name.Contains("North Sound Interval"))
            {
                item.CurrentValue = $"{ConfigManager.NorthSoundInterval.Value:F1}s";
            }
            else if (item.Name.Contains("Turn Speed"))
            {
                item.CurrentValue = $"{ConfigManager.TurnSpeed.Value:F0}°/s";
            }
            else if (item.Name.Contains("Snap Turn Angle"))
            {
                item.CurrentValue = $"{ConfigManager.SnapTurnAngle.Value:F0}°";
            }
            else if (item.Name.Contains("Pathfinding Stopping Radius"))
            {
                item.CurrentValue = $"{ConfigManager.PathfindingStoppingRadius.Value:F1}m";
            }
            else if (item.Name.Contains("Visual Markers"))
            {
                item.CurrentValue = ConfigManager.EnableVisualMarkers.Value ? "enabled" : "disabled";
            }
            else if (item.Name.Contains("Scan Radius"))
            {
                item.CurrentValue = $"{ConfigManager.ScanRadius.Value:F0}m";
            }
            else if (item.Name.Contains("UI Announcements"))
            {
                item.CurrentValue = ConfigManager.EnableUIAnnouncements.Value ? "enabled" : "disabled";
            }
            else if (item.Name.Contains("Audio Cues"))
            {
                item.CurrentValue = ConfigManager.EnableAudioCues.Value ? "enabled" : "disabled";
            }
            else if (item.Name.Contains("Max Objects"))
            {
                item.CurrentValue = ConfigManager.MaxObjectsToScan.Value.ToString();
            }
            else if (item.Name.Contains("Object Scan Interval"))
            {
                item.CurrentValue = $"{ConfigManager.ObjectScanInterval.Value:F2}s";
            }
        }

        // Modification helpers
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

        // Keybind rebinding
        private void StartKeybindRebind(ConfigEntry<Key> entry)
        {
            isWaitingForKey = true;
            currentKeybindEntry = entry;
            var item = GetCurrentItem();
            SpeechUtils.SpeakText($"Press a key to rebind {item.Name}, or Escape to cancel");
        }

        private void SetKeybind(Key newKey)
        {
            if (currentKeybindEntry != null)
            {
                // Check for conflicts with other keybinds
                string conflictingAction = CheckKeybindConflict(newKey);
                if (!string.IsNullOrEmpty(conflictingAction))
                {
                    SpeechUtils.SpeakText($"Key {GetKeyDisplayName(newKey)} is already used for {conflictingAction}. Press Escape to cancel or try another key.");
                    return;
                }

                currentKeybindEntry.Value = newKey;
                isWaitingForKey = false;

                var item = GetCurrentItem();
                item.CurrentValue = GetKeyDisplayName(newKey);

                string keyName = GetKeyDisplayName(newKey);
                string actionName = item.Name;

                SpeechUtils.SpeakText($"Successfully bound {actionName} to {keyName}");
                currentKeybindEntry = null;
                AnnounceCurrentItem();
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
                var item = GetCurrentItem();
                string actionName = item.Name;
                string currentKey = GetKeyDisplayName(currentKeybindEntry.Value);

                isWaitingForKey = false;
                currentKeybindEntry = null;

                SpeechUtils.SpeakText($"Cancelled rebinding. {actionName} remains bound to {currentKey}");
                AnnounceCurrentItem();
            }
        }

        private void OnDestroy()
        {
            if (isMenuOpen)
            {
                EnableOtherSystems();
            }

            foreach (var action in navigationActions ?? new InputAction[0])
            {
                action?.Disable();
                action?.Dispose();
            }
        }
    }
}