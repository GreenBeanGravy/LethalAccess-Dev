using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace LethalAccess
{
    /// <summary>
    /// Consolidated speech synthesis utilities for LethalAccess
    /// </summary>
    public static class SpeechUtils
    {
        // Dictionary of text replacements for specific object names
        private static readonly Dictionary<string, string> textReplacements = new Dictionary<string, string>
        {
            { "EntranceTeleportA", "Enter Factory" },
            { "EntranceTeleportA(Clone)", "Exit Factory" },
            { "EntranceTeleportB", "Enter Fire Escape" },
            { "EntranceTeleportB(Clone)", "Exit Fire Escape" },
            { "TerminalScript", "Terminal" },
            { "StartGameLever", "Start Ship Lever" },
            { "ShipInside", "Inside of Ship" },
            { "StorageCloset", "Storage Closet" },
            { "Bunkbeds", "Bunk Beds" },
            { "LightSwitch", "Light Switch" },
            { "ItemShip", "Item Ship" },
            { "RedButton", "Teleporter Button" },
            { "BellDinger", "Sell Bell" },
            { "ItemCounter", "Item Counter" },
            { "PlacementBlocker (5)", "Charging Station" }
        };

        // Speech rate limiting
        private static DateTime lastSpeechTime = DateTime.MinValue;
        private static readonly TimeSpan speechCooldown = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// Primary speech function with text processing and rate limiting
        /// </summary>
        public static async void SpeakText(string text)
        {
            // Check rate limiting
            if ((DateTime.Now - lastSpeechTime) < speechCooldown)
            {
                return;
            }

            // Check if speech is enabled
            if (!ConfigManager.EnableAudioCues.Value)
            {
                return;
            }

            // Apply text replacements and processing
            text = ProcessTextForSpeech(text);

            // Update last speech time
            lastSpeechTime = DateTime.Now;

            // Speak asynchronously
            await Task.Run(() =>
            {
                try
                {
                    SpeechSynthesizer.SpeakText(text);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error in speech synthesis: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Speak text immediately without rate limiting (for critical announcements)
        /// </summary>
        public static async void SpeakTextImmediate(string text)
        {
            if (!ConfigManager.EnableAudioCues.Value)
            {
                return;
            }

            text = ProcessTextForSpeech(text);

            await Task.Run(() =>
            {
                try
                {
                    SpeechSynthesizer.SpeakText(text);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error in immediate speech synthesis: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Speak text with custom volume (if supported by speech system)
        /// </summary>
        public static void SpeakTextWithVolume(string text, float volumeMultiplier = 1f)
        {
            // Note: Volume adjustment would need to be implemented in the speech synthesizer
            // For now, we'll just use the standard speak function
            SpeakText(text);
        }

        /// <summary>
        /// Process text for speech by applying replacements and formatting
        /// </summary>
        private static string ProcessTextForSpeech(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // Apply text replacements
            string result = text;
            foreach (var replacement in textReplacements)
            {
                result = result.Replace(replacement.Key, replacement.Value);
            }

            // Clean up common formatting issues
            result = CleanTextForSpeech(result);

            return result;
        }

        /// <summary>
        /// Clean text for better speech synthesis
        /// </summary>
        private static string CleanTextForSpeech(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // Remove or replace problematic characters
            text = text.Replace("\n", " ");
            text = text.Replace("\r", " ");
            text = text.Replace("\t", " ");

            // Replace multiple spaces with single space
            while (text.Contains("  "))
            {
                text = text.Replace("  ", " ");
            }

            // Remove special characters that might cause issues
            text = text.Replace("&", "and");
            text = text.Replace("@", "at");
            text = text.Replace("#", "number");
            text = text.Replace("$", "dollars");
            text = text.Replace("%", "percent");

            // Clean up punctuation for better flow
            text = text.Replace("...", ".");
            text = text.Replace("!!", "!");
            text = text.Replace("??", "?");

            return text.Trim();
        }

        /// <summary>
        /// Announce UI element with enhanced formatting
        /// </summary>
        public static void AnnounceUIElement(GameObject element)
        {
            if (element == null)
            {
                return;
            }

            string announcement = UIUtils.GetUIElementDisplayText(element);

            // Add positional information if useful
            string position = GetUIElementPosition(element);
            if (!string.IsNullOrEmpty(position))
            {
                announcement += $", {position}";
            }

            SpeakText(announcement);
        }

        /// <summary>
        /// Get positional information for UI element
        /// </summary>
        private static string GetUIElementPosition(GameObject element)
        {
            if (element == null || element.transform.parent == null)
            {
                return string.Empty;
            }

            // Count siblings of same type to provide position context
            Transform parent = element.transform.parent;
            var siblings = new List<Transform>();

            string elementType = GetUIElementType(element);

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform sibling = parent.GetChild(i);
                if (sibling.gameObject.activeInHierarchy &&
                    GetUIElementType(sibling.gameObject) == elementType)
                {
                    siblings.Add(sibling);
                }
            }

            if (siblings.Count > 1)
            {
                int index = siblings.IndexOf(element.transform);
                if (index >= 0)
                {
                    return $"{index + 1} of {siblings.Count}";
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Get the type of UI element for grouping
        /// </summary>
        private static string GetUIElementType(GameObject element)
        {
            if (element.GetComponent<UnityEngine.UI.Button>() != null)
                return "button";
            if (element.GetComponent<UnityEngine.UI.Toggle>() != null)
                return "toggle";
            if (element.GetComponent<UnityEngine.UI.Slider>() != null)
                return "slider";
            if (element.GetComponent<UnityEngine.UI.Dropdown>() != null)
                return "dropdown";
            if (element.GetComponent<TMPro.TMP_Dropdown>() != null)
                return "dropdown";
            if (element.GetComponent<UnityEngine.UI.InputField>() != null)
                return "inputfield";
            if (element.GetComponent<TMPro.TMP_InputField>() != null)
                return "inputfield";

            return "element";
        }

        /// <summary>
        /// Announce direction/orientation information
        /// </summary>
        public static void AnnounceDirection(Vector3 direction, bool includeVertical = false)
        {
            string directionText = GetDirectionText(direction, includeVertical);
            SpeakText(directionText);
        }

        /// <summary>
        /// Convert direction vector to speech-friendly text
        /// </summary>
        public static string GetDirectionText(Vector3 direction, bool includeVertical = false)
        {
            // Normalize the direction
            direction = direction.normalized;

            // Get horizontal angle
            float angle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360f;

            // Convert to cardinal directions
            string[] directions = { "North", "Northeast", "East", "Southeast", "South", "Southwest", "West", "Northwest" };
            int directionIndex = Mathf.RoundToInt(angle / 45f) % 8;
            string horizontalDirection = directions[directionIndex];

            if (!includeVertical)
            {
                return horizontalDirection;
            }

            // Add vertical component if requested
            string verticalDirection = "";
            if (direction.y > 0.3f)
            {
                verticalDirection = " above";
            }
            else if (direction.y < -0.3f)
            {
                verticalDirection = " below";
            }

            return horizontalDirection + verticalDirection;
        }

        /// <summary>
        /// Announce distance with appropriate units
        /// </summary>
        public static void AnnounceDistance(float distance)
        {
            string distanceText = GetDistanceText(distance);
            SpeakText(distanceText);
        }

        /// <summary>
        /// Convert distance to speech-friendly text
        /// </summary>
        public static string GetDistanceText(float distance)
        {
            if (distance < 1f)
            {
                return "very close";
            }
            else if (distance < 2f)
            {
                return "close";
            }
            else if (distance < 5f)
            {
                return $"{distance:F1} meters away";
            }
            else if (distance < 20f)
            {
                return $"{Mathf.RoundToInt(distance)} meters away";
            }
            else
            {
                return "far away";
            }
        }

        /// <summary>
        /// Queue multiple speech items with delays
        /// </summary>
        public static async void SpeakSequence(List<string> texts, float delayBetweenItems = 0.5f)
        {
            foreach (string text in texts)
            {
                SpeakTextImmediate(text);
                await Task.Delay(TimeSpan.FromSeconds(delayBetweenItems));
            }
        }

        /// <summary>
        /// Add a custom text replacement
        /// </summary>
        public static void AddTextReplacement(string original, string replacement)
        {
            textReplacements[original] = replacement;
        }

        /// <summary>
        /// Remove a text replacement
        /// </summary>
        public static void RemoveTextReplacement(string original)
        {
            textReplacements.Remove(original);
        }

        /// <summary>
        /// Get all current text replacements
        /// </summary>
        public static Dictionary<string, string> GetTextReplacements()
        {
            return new Dictionary<string, string>(textReplacements);
        }
    }
}