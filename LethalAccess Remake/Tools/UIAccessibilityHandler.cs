using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

namespace Green.LethalAccessPlugin
{
    /// <summary>
    /// Handles UI accessibility features for LethalAccess
    /// </summary>
    public class UIAccessibilityManager : MonoBehaviour
    {
        // Singleton pattern
        public static UIAccessibilityManager Instance { get; private set; }

        // UI element tracking
        private List<GameObject> previouslyFocusedUIElements = new List<GameObject>(10);
        private GameObject lastAnnouncedObject;
        private bool isNavigatingWithPrevKey = false;

        // UI element organization
        private Dictionary<string, UIElementInfo> registeredElements = new Dictionary<string, UIElementInfo>();
        private Dictionary<string, UIGroupInfo> uiGroups = new Dictionary<string, UIGroupInfo>();

        // Custom navigation connections
        private Dictionary<string, Dictionary<string, string>> customUINavigation = new Dictionary<string, Dictionary<string, string>>();

        // Custom text providers (migrated from original implementation)
        private Dictionary<string, List<Func<string>>> customTextProviders = new Dictionary<string, List<Func<string>>>();

        // Reference to logger
        private BepInEx.Logging.ManualLogSource Logger;

        // Initialize
        public void Initialize(BepInEx.Logging.ManualLogSource logger)
        {
            Instance = this;
            Logger = logger;
            LogInfo("UIAccessibilityManager initialized");

            // Migrate any existing UI speech overrides from the static dictionary
            if (LethalAccess.LethalAccessPlugin.overriddenTexts != null && LethalAccess.LethalAccessPlugin.overriddenTexts.Count > 0)
            {
                foreach (var kvp in LethalAccess.LethalAccessPlugin.overriddenTexts)
                {
                    customTextProviders[kvp.Key] = kvp.Value;
                }
                LogInfo($"Migrated {LethalAccess.LethalAccessPlugin.overriddenTexts.Count} existing UI speech overrides");
            }
        }

        public void Update()
        {
            // Check for UI element changes
            HandleUIElementAnnouncement();
        }

        #region Element Registration

        /// <summary>
        /// Register a UI element with accessibility information
        /// </summary>
        public void RegisterElement(string path, string displayName = null, string elementType = null)
        {
            string finalDisplayName = displayName ?? GetNameFromPath(path);

            if (registeredElements.ContainsKey(path))
            {
                // Update existing registration
                registeredElements[path].DisplayName = finalDisplayName;
                if (!string.IsNullOrEmpty(elementType))
                {
                    registeredElements[path].ElementType = elementType;
                }
                LogInfo($"Updated UI element: {path}");
            }
            else
            {
                // New registration
                registeredElements[path] = new UIElementInfo
                {
                    Path = path,
                    DisplayName = finalDisplayName,
                    ElementType = elementType ?? "Unknown"
                };
                LogInfo($"Registered UI element: {path}");
            }
        }

        /// <summary>
        /// Create a group of related UI elements
        /// </summary>
        public void CreateGroup(string groupName, params string[] elementPaths)
        {
            // Register or update the group
            if (uiGroups.ContainsKey(groupName))
            {
                uiGroups[groupName].ElementPaths.Clear();
                uiGroups[groupName].ElementPaths.AddRange(elementPaths);
                LogInfo($"Updated UI group: {groupName}");
            }
            else
            {
                uiGroups[groupName] = new UIGroupInfo
                {
                    Name = groupName,
                    ElementPaths = new List<string>(elementPaths)
                };
                LogInfo($"Created UI group: {groupName}");
            }

            // Register any elements not already registered
            for (int i = 0; i < elementPaths.Length; i++)
            {
                string path = elementPaths[i];

                // Auto-register if not already registered
                if (!registeredElements.ContainsKey(path))
                {
                    RegisterElement(path);
                }

                // Update group information
                registeredElements[path].GroupName = groupName;
                registeredElements[path].GroupIndex = i;
            }
        }

        /// <summary>
        /// Set custom text provider for a UI element
        /// </summary>
        public void SetCustomText(string path, List<Func<string>> textProviders)
        {
            customTextProviders[path] = textProviders;
            LogInfo($"Set custom text for UI element: {path}");

            // Auto-register if not already registered
            if (!registeredElements.ContainsKey(path))
            {
                RegisterElement(path);
            }
        }

        /// <summary>
        /// Set custom navigation connection between UI elements
        /// </summary>
        public void SetNavigation(string sourcePath, string direction, string targetPath)
        {
            if (!customUINavigation.ContainsKey(sourcePath))
            {
                customUINavigation[sourcePath] = new Dictionary<string, string>();
            }

            customUINavigation[sourcePath][direction] = targetPath;
            LogInfo($"Set custom navigation: {sourcePath} -> {direction} -> {targetPath}");

            // Auto-register if not already registered
            if (!registeredElements.ContainsKey(sourcePath))
            {
                RegisterElement(sourcePath);
            }

            if (!registeredElements.ContainsKey(targetPath))
            {
                RegisterElement(targetPath);
            }
        }

        #endregion

        #region UI Announcement and Navigation

        /// <summary>
        /// Handle announcing the current UI element
        /// </summary>
        public void HandleUIElementAnnouncement()
        {
            if (EventSystem.current?.currentSelectedGameObject != null)
            {
                GameObject selectedObject = EventSystem.current.currentSelectedGameObject;
                if (selectedObject != lastAnnouncedObject)
                {
                    AnnounceUIElement(selectedObject);
                    lastAnnouncedObject = selectedObject;
                }
            }
        }

        /// <summary>
        /// Navigate to the previously focused UI element
        /// </summary>
        public void NavigateToPreviousElement()
        {
            if (previouslyFocusedUIElements.Count > 0)
            {
                isNavigatingWithPrevKey = true;

                int lastIndex = previouslyFocusedUIElements.Count - 1;
                GameObject previousUIElement = previouslyFocusedUIElements[lastIndex];
                previouslyFocusedUIElements.RemoveAt(lastIndex);

                if (previousUIElement != null && previousUIElement.activeInHierarchy)
                {
                    EventSystem.current.SetSelectedGameObject(previousUIElement);
                }

                isNavigatingWithPrevKey = false;
            }
        }

        /// <summary>
        /// Apply all custom navigation connections
        /// </summary>
        public void ApplyCustomNavigation()
        {
            foreach (var entry in customUINavigation)
            {
                string sourcePath = entry.Key;
                Dictionary<string, string> directions = entry.Value;

                GameObject sourceObj = GameObject.Find(sourcePath);
                if (sourceObj == null) continue;

                Selectable selectable = sourceObj.GetComponent<Selectable>();
                if (selectable == null) continue;

                Navigation nav = selectable.navigation;
                nav.mode = Navigation.Mode.Explicit;

                foreach (var directionEntry in directions)
                {
                    string direction = directionEntry.Key.ToLower();
                    string targetPath = directionEntry.Value;

                    GameObject targetObj = GameObject.Find(targetPath);
                    if (targetObj == null) continue;

                    Selectable targetSelectable = targetObj.GetComponent<Selectable>();
                    if (targetSelectable == null) continue;

                    switch (direction)
                    {
                        case "up":
                            nav.selectOnUp = targetSelectable;
                            break;
                        case "down":
                            nav.selectOnDown = targetSelectable;
                            break;
                        case "left":
                            nav.selectOnLeft = targetSelectable;
                            break;
                        case "right":
                            nav.selectOnRight = targetSelectable;
                            break;
                    }
                }

                selectable.navigation = nav;
                LogInfo($"Applied navigation for {sourcePath}");
            }
        }

        /// <summary>
        /// Announce a UI element with all its information
        /// </summary>
        private void AnnounceUIElement(GameObject element)
        {
            try
            {
                // Track previous elements for back navigation
                if (!isNavigatingWithPrevKey && lastAnnouncedObject != null)
                {
                    previouslyFocusedUIElements.Add(lastAnnouncedObject);
                    if (previouslyFocusedUIElements.Count > 10)
                    {
                        previouslyFocusedUIElements.RemoveAt(0);
                    }
                }

                // Get element path
                string path = Utilities.GetGameObjectPath(element);

                // Get element type first, as it affects how we read the text
                string elementType = GetElementType(element, path);

                // Build announcement with text and index info
                string elementText = GetElementText(element, path, elementType);
                string indexInfo = GetElementIndex(element, path);

                // Full announcement format depends on the element type
                string announcement;

                // Don't repeat the element type if it's already in the text
                // For example, if elementText is "Value: 50%, slider", we don't need to say "slider" again
                if (elementText.ToLower().Contains(elementType.ToLower()))
                {
                    announcement = $"{elementText}{indexInfo}";
                }
                else
                {
                    announcement = $"{elementText}, {elementType}{indexInfo}";
                }

                // Log and speak the element info
                LogUIElementInfo(element, announcement);
                Utilities.SpeakText(announcement);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error announcing UI element: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the text for a UI element (using custom providers if available)
        /// </summary>
        private string GetElementText(GameObject element, string path, string elementType)
        {
            // Use custom text provider if available
            if (customTextProviders.TryGetValue(path, out var providers))
            {
                string customText = string.Join(" ", providers.Select(p => p()));

                // Remove redundant "Slider:" text if it will be followed by ", slider"
                if (elementType == "slider" && customText.Contains("Slider:"))
                {
                    customText = customText.Replace("Slider:", "").Trim();
                }

                if (!string.IsNullOrEmpty(customText))
                {
                    return Utilities.RemoveSpecialCharacters(customText);
                }
            }

            // Use registered display name if available
            if (registeredElements.TryGetValue(path, out var info) &&
                !string.IsNullOrEmpty(info.DisplayName))
            {
                string displayName = info.DisplayName;

                // Enhance with state information based on element type
                return EnhanceTextWithState(element, displayName, elementType);
            }

            // Fall back to getting text from the component
            string baseText = Utilities.RemoveSpecialCharacters(Utilities.GetTextFromComponent(element));

            // Enhance with state information
            return EnhanceTextWithState(element, baseText, elementType);
        }

        /// <summary>
        /// Enhance element text with state information based on element type
        /// </summary>
        private string EnhanceTextWithState(GameObject element, string baseText, string elementType)
        {
            // If base text is empty or just whitespace, replace with element name
            if (string.IsNullOrWhiteSpace(baseText))
            {
                baseText = element.name.Replace("(Clone)", "").Trim();
            }

            switch (elementType)
            {
                case "toggle":
                case "checkbox":
                    // Add toggle/checkbox state (on/off)
                    Toggle toggle = element.GetComponent<Toggle>();
                    if (toggle != null)
                    {
                        string state = toggle.isOn ? "checked" : "unchecked";
                        return $"{baseText}, {state}";
                    }
                    break;

                case "dropdown":
                    // Add selected option
                    TMP_Dropdown tmpDropdown = element.GetComponent<TMP_Dropdown>();
                    if (tmpDropdown != null && tmpDropdown.options.Count > 0)
                    {
                        int selectedIndex = tmpDropdown.value;
                        if (selectedIndex >= 0 && selectedIndex < tmpDropdown.options.Count)
                        {
                            string selectedOption = tmpDropdown.options[selectedIndex].text;
                            return $"{baseText}, selected: {selectedOption}";
                        }
                    }
                    else
                    {
                        Dropdown dropdown = element.GetComponent<Dropdown>();
                        if (dropdown != null && dropdown.options.Count > 0)
                        {
                            int selectedIndex = dropdown.value;
                            if (selectedIndex >= 0 && selectedIndex < dropdown.options.Count)
                            {
                                string selectedOption = dropdown.options[selectedIndex].text;
                                return $"{baseText}, selected: {selectedOption}";
                            }
                        }
                    }
                    break;

                case "slider":
                    // Remove redundant "Slider:" text if present
                    if (baseText.Contains("Slider:"))
                    {
                        baseText = baseText.Replace("Slider:", "").Trim();
                    }

                    // Try to get value if not already in the text
                    if (!baseText.Contains("%") && !baseText.Contains("value"))
                    {
                        Slider slider = element.GetComponent<Slider>();
                        if (slider != null)
                        {
                            // Format value as percentage or raw value based on slider range
                            if (slider.minValue == 0 && slider.maxValue == 1)
                            {
                                int percentage = Mathf.RoundToInt(slider.value * 100);
                                baseText = $"{baseText} {percentage}%";
                            }
                            else
                            {
                                baseText = $"{baseText} {slider.value}";
                            }
                        }
                    }
                    break;

                case "input field":
                    // Add current text and placeholder
                    TMP_InputField tmpInputField = element.GetComponent<TMP_InputField>();
                    if (tmpInputField != null)
                    {
                        string currentText = tmpInputField.text;
                        if (string.IsNullOrEmpty(currentText) && tmpInputField.placeholder != null)
                        {
                            TextMeshProUGUI placeholderText = tmpInputField.placeholder.GetComponent<TextMeshProUGUI>();
                            if (placeholderText != null)
                            {
                                return $"{baseText}, placeholder: {placeholderText.text}";
                            }
                        }
                        else if (!string.IsNullOrEmpty(currentText))
                        {
                            return $"{baseText}, text: {currentText}";
                        }
                    }
                    else
                    {
                        InputField inputField = element.GetComponent<InputField>();
                        if (inputField != null)
                        {
                            string currentText = inputField.text;
                            if (string.IsNullOrEmpty(currentText) && inputField.placeholder != null)
                            {
                                Text placeholderText = inputField.placeholder.GetComponent<Text>();
                                if (placeholderText != null)
                                {
                                    return $"{baseText}, placeholder: {placeholderText.text}";
                                }
                            }
                            else if (!string.IsNullOrEmpty(currentText))
                            {
                                return $"{baseText}, text: {currentText}";
                            }
                        }
                    }
                    break;

                case "button":
                    // Check for button with toggle-like behavior (image changes to indicate state)
                    Image buttonImage = element.GetComponent<Image>();
                    if (buttonImage != null)
                    {
                        // Special handling for microphone toggle and similar buttons that change their sprite
                        if (baseText.Contains("Microphone Toggle"))
                        {
                            // Try to determine state from parent name or other indicators
                            if (element.transform.parent != null)
                            {
                                // Check if parent name contains state indicators
                                string parentName = element.transform.parent.name.ToLower();
                                if (parentName.Contains("on") || parentName.Contains("enabled"))
                                {
                                    return $"{baseText}, enabled";
                                }
                                else if (parentName.Contains("off") || parentName.Contains("disabled"))
                                {
                                    return $"{baseText}, disabled";
                                }
                            }

                            // Check sprite name for state indicators
                            if (buttonImage.sprite != null)
                            {
                                string spriteName = buttonImage.sprite.name.ToLower();
                                if (spriteName.Contains("on") || spriteName.Contains("enabled"))
                                {
                                    return $"{baseText}, enabled";
                                }
                                else if (spriteName.Contains("off") || spriteName.Contains("disabled"))
                                {
                                    return $"{baseText}, disabled";
                                }
                            }

                            // Check color for indicators (green often means on, red often means off)
                            Color buttonColor = buttonImage.color;
                            if (buttonColor.g > buttonColor.r * 1.5f && buttonColor.g > buttonColor.b * 1.5f)
                            {
                                return $"{baseText}, enabled";
                            }
                            else if (buttonColor.r > buttonColor.g * 1.5f && buttonColor.r > buttonColor.b * 1.5f)
                            {
                                return $"{baseText}, disabled";
                            }
                        }
                    }
                    break;
            }

            return baseText;
        }

        /// <summary>
        /// Get the type of UI element
        /// </summary>
        private string GetElementType(GameObject element, string path)
        {
            // Use registered type if available
            if (registeredElements.TryGetValue(path, out var info) &&
                info.ElementType != "Unknown")
            {
                return info.ElementType;
            }

            // Detect type based on components
            if (element.GetComponent<Button>() != null)
                return "button";

            Toggle toggle = element.GetComponent<Toggle>();
            if (toggle != null)
            {
                // Check if it's specifically a checkbox
                // Some games distinguish checkboxes from other toggle controls
                if (toggle.graphic != null)
                {
                    string graphicName = toggle.graphic.name.ToLower();
                    if (graphicName.Contains("check") || graphicName.Contains("box"))
                        return "checkbox";

                    // Also check parent/child names for checkbox indicators
                    if (toggle.transform.name.ToLower().Contains("check") ||
                        (toggle.transform.parent != null && toggle.transform.parent.name.ToLower().Contains("check")))
                        return "checkbox";
                }
                return "toggle";
            }

            if (element.GetComponent<Slider>() != null)
                return "slider";

            if (element.GetComponent<Dropdown>() != null || element.GetComponent<TMP_Dropdown>() != null)
                return "dropdown";

            if (element.GetComponent<InputField>() != null || element.GetComponent<TMP_InputField>() != null)
                return "input field";

            if (element.GetComponent<ScrollRect>() != null)
                return "scroll area";

            if (element.GetComponent<TextMeshProUGUI>() != null || element.GetComponent<Text>() != null)
                return "text";

            // More specific UI elements
            if (element.name.ToLower().Contains("checkbox") ||
                (element.transform.parent != null && element.transform.parent.name.ToLower().Contains("checkbox")))
                return "checkbox";

            if (element.name.ToLower().Contains("radio") ||
                (element.transform.parent != null && element.transform.parent.name.ToLower().Contains("radio")))
                return "radio button";

            if (element.name.ToLower().Contains("tab") ||
                (element.transform.parent != null && element.transform.parent.name.ToLower().Contains("tab")))
                return "tab";

            return "ui element";
        }

        /// <summary>
        /// Get the index information for a UI element (e.g., "1 of 5")
        /// </summary>
        private string GetElementIndex(GameObject element, string path)
        {
            // Check if this element is part of a registered group
            if (registeredElements.TryGetValue(path, out var info) &&
                !string.IsNullOrEmpty(info.GroupName))
            {
                if (uiGroups.TryGetValue(info.GroupName, out var group))
                {
                    return $", {info.GroupIndex + 1} of {group.ElementPaths.Count}";
                }
            }

            // If not in a registered group, try to find siblings of same type
            Transform parent = element.transform.parent;
            if (parent != null && parent.childCount > 1)
            {
                // Get component types to match
                string elementType = GetElementType(element, path);
                Type buttonType = typeof(Button);
                Type sliderType = typeof(Slider);
                Type toggleType = typeof(Toggle);
                Type dropdownType = typeof(Dropdown);
                Type tmpDropdownType = typeof(TMP_Dropdown);
                Type checkboxType = toggleType; // Checkboxes are toggle components

                // Determine which component type to look for
                Type targetType = null;
                switch (elementType)
                {
                    case "button": targetType = buttonType; break;
                    case "slider": targetType = sliderType; break;
                    case "toggle":
                    case "checkbox": targetType = toggleType; break;
                    case "dropdown":
                        // Check both dropdown types
                        List<Transform> siblings = new List<Transform>();

                        for (int i = 0; i < parent.childCount; i++)
                        {
                            Transform child = parent.GetChild(i);
                            if (child.gameObject.activeInHierarchy &&
                                (child.GetComponent(dropdownType) != null || child.GetComponent(tmpDropdownType) != null))
                            {
                                siblings.Add(child);
                            }
                        }

                        if (siblings.Count > 1)
                        {
                            int index = siblings.IndexOf(element.transform);
                            if (index >= 0)
                            {
                                return $", {index + 1} of {siblings.Count}";
                            }
                        }
                        return "";
                }

                if (targetType != null)
                {
                    // Count active siblings with the same component
                    List<Transform> siblingsOfSameType = new List<Transform>();
                    for (int i = 0; i < parent.childCount; i++)
                    {
                        Transform child = parent.GetChild(i);
                        if (child.gameObject.activeInHierarchy &&
                            child.GetComponent(targetType) != null)
                        {
                            siblingsOfSameType.Add(child);
                        }
                    }

                    if (siblingsOfSameType.Count > 1)
                    {
                        int index = siblingsOfSameType.IndexOf(element.transform);
                        if (index >= 0)
                        {
                            return $", {index + 1} of {siblingsOfSameType.Count}";
                        }
                    }
                }

                // Look for siblings with similar names, useful for custom UI elements
                if (targetType == null)
                {
                    string elementBaseName = GetBaseElementName(element.name);
                    if (!string.IsNullOrEmpty(elementBaseName))
                    {
                        List<Transform> similarlyNamedSiblings = new List<Transform>();

                        for (int i = 0; i < parent.childCount; i++)
                        {
                            Transform child = parent.GetChild(i);
                            if (child.gameObject.activeInHierarchy &&
                                GetBaseElementName(child.name) == elementBaseName)
                            {
                                similarlyNamedSiblings.Add(child);
                            }
                        }

                        if (similarlyNamedSiblings.Count > 1)
                        {
                            int index = similarlyNamedSiblings.IndexOf(element.transform);
                            if (index >= 0)
                            {
                                return $", {index + 1} of {similarlyNamedSiblings.Count}";
                            }
                        }
                    }
                }
            }

            return ""; // No index info if not in a group
        }

        /// <summary>
        /// Get the base element name without numbers (for grouping related elements)
        /// </summary>
        private string GetBaseElementName(string fullName)
        {
            // Remove clone suffix
            string name = fullName.Replace("(Clone)", "").Trim();

            // Remove trailing numbers and spaces
            return System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(\d+\)$|\s*\d+$", "");
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Extract a display name from a UI path
        /// </summary>
        private string GetNameFromPath(string path)
        {
            string[] parts = path.Split('/');
            if (parts.Length > 0)
            {
                string name = parts[parts.Length - 1];

                // Clean up the name
                name = name.Replace("(Clone)", "").Trim();
                name = System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");

                // Remove trailing numbers (e.g., "Button (1)" becomes "Button")
                name = System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(\d+\)$", "");

                return name;
            }
            return path;
        }

        /// <summary>
        /// Log UI element information
        /// </summary>
        private void LogUIElementInfo(GameObject element, string announcement)
        {
            if (Logger != null)
            {
                Logger.LogInfo($"UI Element: {element.name}, Path: {Utilities.GetGameObjectPath(element)}, Announced: {announcement}");
            }
        }

        /// <summary>
        /// Log information message
        /// </summary>
        private void LogInfo(string message)
        {
            if (Logger != null)
            {
                Logger.LogInfo($"[UIAccessibility] {message}");
            }
            else
            {
                Debug.Log($"[UIAccessibility] {message}");
            }
        }

        #endregion

        #region Helper Classes

        /// <summary>
        /// Information about a registered UI element
        /// </summary>
        private class UIElementInfo
        {
            public string Path;
            public string DisplayName;
            public string ElementType = "Unknown";
            public string GroupName;
            public int GroupIndex;
        }

        /// <summary>
        /// Information about a UI group
        /// </summary>
        private class UIGroupInfo
        {
            public string Name;
            public List<string> ElementPaths = new List<string>();
        }

        #endregion
    }
}