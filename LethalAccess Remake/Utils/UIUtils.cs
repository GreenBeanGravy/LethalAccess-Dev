using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace LethalAccess
{
    /// <summary>
    /// Consolidated UI utility functions for LethalAccess
    /// </summary>
    public static class UIUtils
    {
        /// <summary>
        /// Get text from a GameObject by path
        /// </summary>
        public static string GetTextFromGameObject(string gameObjectPath)
        {
            GameObject gameObject = GameObject.Find(gameObjectPath);
            if (gameObject != null)
            {
                return GetTextFromComponent(gameObject);
            }
            return string.Empty;
        }

        /// <summary>
        /// Get text from a GameObject's text components
        /// </summary>
        public static string GetTextFromComponent(GameObject gameObject)
        {
            if (gameObject == null) return string.Empty;

            // Check if the component has a TextMeshProUGUI component
            TMPro.TextMeshProUGUI tmpTextComponent = gameObject.GetComponentInChildren<TMPro.TextMeshProUGUI>();
            if (tmpTextComponent != null)
            {
                return tmpTextComponent.text;
            }

            // If no TextMeshProUGUI component, check for a Text component
            UnityEngine.UI.Text textComponent = gameObject.GetComponent<UnityEngine.UI.Text>();
            if (textComponent != null)
            {
                return textComponent.text;
            }

            // If no text component found, recursively check child objects
            foreach (Transform child in gameObject.transform)
            {
                string childText = GetTextFromComponent(child.gameObject);
                if (!string.IsNullOrEmpty(childText))
                {
                    return childText;
                }
            }

            // If no text component found, use the name of the GameObject
            return gameObject.name;
        }

        /// <summary>
        /// Get lobby information from a join button
        /// </summary>
        public static string GetLobbyInfoFromJoinButton(GameObject selectedObject)
        {
            if (selectedObject != null)
            {
                Transform lobbyListItemTransform = selectedObject.transform.parent;
                if (lobbyListItemTransform != null)
                {
                    LobbySlot lobbySlot = lobbyListItemTransform.GetComponent<LobbySlot>();
                    if (lobbySlot != null)
                    {
                        string lobbyName = GetTextFromComponent(lobbySlot.LobbyName.gameObject);
                        string playerCount = GetTextFromComponent(lobbySlot.playerCount.gameObject);
                        return $"Lobby: {lobbyName}, Players: {playerCount}";
                    }
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// Remove special characters from text
        /// </summary>
        public static string RemoveSpecialCharacters(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            string specialCharacters = "[]{}<>()";
            foreach (char c in specialCharacters)
            {
                input = input.Replace(c.ToString(), string.Empty);
            }
            return input;
        }

        /// <summary>
        /// Get the full path of a GameObject in the hierarchy
        /// </summary>
        public static string GetGameObjectPath(GameObject gameObject)
        {
            if (gameObject == null) return string.Empty;

            string path = gameObject.name;
            Transform parent = gameObject.transform.parent;

            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        /// <summary>
        /// Get slider value as percentage
        /// </summary>
        public static float GetSliderValuePercentage(string sliderHandlePath)
        {
            GameObject sliderHandle = GameObject.Find(sliderHandlePath);
            if (sliderHandle != null)
            {
                Vector3 localPosition = sliderHandle.transform.localPosition;
                float percentage = (localPosition.x + 70f) / 140f * 100f;
                return Mathf.Clamp(percentage, 0f, 100f);
            }
            return 0f;
        }

        /// <summary>
        /// Get slider value as integer
        /// </summary>
        public static int GetSliderValue(string sliderHandlePath)
        {
            GameObject sliderHandle = GameObject.Find(sliderHandlePath);
            if (sliderHandle != null)
            {
                UnityEngine.UI.Slider slider = sliderHandle.GetComponentInParent<UnityEngine.UI.Slider>();
                if (slider != null)
                {
                    return Mathf.RoundToInt(slider.value);
                }
            }
            return 0;
        }

        /// <summary>
        /// Set custom text for UI accessibility manager
        /// </summary>
        public static void SetCustomUIText(UIAccessibilityManager manager, string path, Func<string> textProvider)
        {
            if (manager != null)
            {
                manager.SetCustomText(path, new List<Func<string>> { textProvider });
            }
        }

        /// <summary>
        /// Check if a UI element is visible and interactable
        /// </summary>
        public static bool IsUIElementVisible(GameObject uiElement)
        {
            if (uiElement == null) return false;

            // Check if the object is active
            if (!uiElement.activeInHierarchy) return false;

            // Check if it has a CanvasGroup that might be blocking interaction
            CanvasGroup canvasGroup = uiElement.GetComponent<CanvasGroup>();
            if (canvasGroup != null && (canvasGroup.alpha <= 0 || !canvasGroup.interactable))
            {
                return false;
            }

            // Check parent CanvasGroups
            Transform parent = uiElement.transform.parent;
            while (parent != null)
            {
                CanvasGroup parentCanvasGroup = parent.GetComponent<CanvasGroup>();
                if (parentCanvasGroup != null && (parentCanvasGroup.alpha <= 0 || !parentCanvasGroup.interactable))
                {
                    return false;
                }
                parent = parent.parent;
            }

            return true;
        }

        /// <summary>
        /// Get the display text for different UI element types
        /// </summary>
        public static string GetUIElementDisplayText(GameObject element)
        {
            if (element == null) return string.Empty;

            // Button
            Button button = element.GetComponent<Button>();
            if (button != null)
            {
                string buttonText = GetTextFromComponent(element);
                return string.IsNullOrEmpty(buttonText) ? element.name : buttonText;
            }

            // Toggle
            Toggle toggle = element.GetComponent<Toggle>();
            if (toggle != null)
            {
                string toggleText = GetTextFromComponent(element);
                string state = toggle.isOn ? "checked" : "unchecked";
                return $"{toggleText}, {state}";
            }

            // Slider
            Slider slider = element.GetComponent<Slider>();
            if (slider != null)
            {
                string sliderText = GetTextFromComponent(element);
                string value = slider.wholeNumbers ?
                    slider.value.ToString("F0") :
                    slider.value.ToString("F1");
                return $"{sliderText}: {value}";
            }

            // Dropdown
            Dropdown dropdown = element.GetComponent<Dropdown>();
            if (dropdown != null)
            {
                string dropdownText = GetTextFromComponent(element);
                if (dropdown.options.Count > 0 && dropdown.value >= 0 && dropdown.value < dropdown.options.Count)
                {
                    string selectedOption = dropdown.options[dropdown.value].text;
                    return $"{dropdownText}: {selectedOption}";
                }
                return dropdownText;
            }

            // TMP Dropdown
            TMP_Dropdown tmpDropdown = element.GetComponent<TMP_Dropdown>();
            if (tmpDropdown != null)
            {
                string dropdownText = GetTextFromComponent(element);
                if (tmpDropdown.options.Count > 0 && tmpDropdown.value >= 0 && tmpDropdown.value < tmpDropdown.options.Count)
                {
                    string selectedOption = tmpDropdown.options[tmpDropdown.value].text;
                    return $"{dropdownText}: {selectedOption}";
                }
                return dropdownText;
            }

            // Input Field
            InputField inputField = element.GetComponent<InputField>();
            if (inputField != null)
            {
                string label = GetTextFromComponent(element.transform.parent?.gameObject);
                string currentText = string.IsNullOrEmpty(inputField.text) ? "empty" : inputField.text;
                return $"{label}: {currentText}";
            }

            // TMP Input Field
            TMP_InputField tmpInputField = element.GetComponent<TMP_InputField>();
            if (tmpInputField != null)
            {
                string label = GetTextFromComponent(element.transform.parent?.gameObject);
                string currentText = string.IsNullOrEmpty(tmpInputField.text) ? "empty" : tmpInputField.text;
                return $"{label}: {currentText}";
            }

            // Default to text content or name
            string elementText = GetTextFromComponent(element);
            return string.IsNullOrEmpty(elementText) ? element.name : elementText;
        }

        /// <summary>
        /// Find UI elements by type in a parent container
        /// </summary>
        public static List<T> FindUIElementsOfType<T>(Transform parent) where T : Component
        {
            List<T> elements = new List<T>();
            if (parent == null) return elements;

            T[] foundElements = parent.GetComponentsInChildren<T>(true);
            elements.AddRange(foundElements);

            return elements;
        }

        /// <summary>
        /// Check if a UI element can be interacted with
        /// </summary>
        public static bool CanInteractWithElement(GameObject element)
        {
            if (!IsUIElementVisible(element)) return false;

            // Check for Selectable components
            Selectable selectable = element.GetComponent<Selectable>();
            if (selectable != null)
            {
                return selectable.IsInteractable();
            }

            // If no Selectable, assume it can be interacted with if visible
            return true;
        }
    }
}