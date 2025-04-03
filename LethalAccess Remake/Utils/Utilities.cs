using BepInEx.Logging;
using GameNetcodeStuff;
using LethalAccess;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using static IngamePlayerSettings;

namespace LethalAccess
{
    internal class Utilities
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
    { "ItemCounter", "Item Counter" }
    // Add any other replacements here
};

        public static string GetTextFromGameObject(string gameObjectPath)
        {
            GameObject gameObject = GameObject.Find(gameObjectPath);
            if (gameObject != null)
            {
                return GetTextFromComponent(gameObject);
            }
            return string.Empty;
        }

        public static string GetTextFromComponent(GameObject gameObject)
        {
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
                        string lobbyName = Utilities.GetTextFromComponent(lobbySlot.LobbyName.gameObject);
                        string playerCount = Utilities.GetTextFromComponent(lobbySlot.playerCount.gameObject);
                        return $"Lobby: {lobbyName}, Players: {playerCount}";
                    }
                }
            }
            return string.Empty;
        }

        public static string RemoveSpecialCharacters(string input)
        {
            string specialCharacters = "[]{}<>()";
            foreach (char c in specialCharacters)
            {
                input = input.Replace(c.ToString(), string.Empty);
            }
            return input;
        }

        public static string GetGameObjectPath(GameObject gameObject)
        {
            string path = gameObject.name;
            Transform parent = gameObject.transform.parent;

            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

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

        public static void LookAtObject(GameObject targetObject)
        {
            if (targetObject != null && LACore.CameraTransform != null)
            {
                float maxDistance = 15f;
                float distanceToTarget = Vector3.Distance(LACore.CameraTransform.position, targetObject.transform.position);

                if (distanceToTarget <= maxDistance)
                {
                    PlayerControllerB playerControllerB = LACore.PlayerTransform.GetComponent<PlayerControllerB>();

                    if (playerControllerB != null)
                    {
                        Vector3 targetDirection = targetObject.transform.position - LACore.CameraTransform.position;
                        Vector3 relativeDirection = LACore.CameraTransform.InverseTransformDirection(targetDirection);

                        // Increase the rotation speed by multiplying the input
                        float rotationSpeedMultiplier = 5f; // Adjust this value to change the rotation speed
                        Vector2 adjustedInput = new Vector2(relativeDirection.x, relativeDirection.y) * rotationSpeedMultiplier;

                        var method = typeof(PlayerControllerB).GetMethod("CalculateNormalLookingInput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        method?.Invoke(playerControllerB, new object[] { adjustedInput });
                    }
                }
            }
        }

        // Updated SpeakText method with text replacements
        public static async void SpeakText(string text)
        {
            // Apply text replacements
            text = ApplyTextReplacements(text);

            await Task.Run(() =>
            {
                SpeechSynthesizer.SpeakText(text);
            });
        }

        // Process text replacements
        private static string ApplyTextReplacements(string text)
        {
            string result = text;

            foreach (var replacement in textReplacements)
            {
                // Replace exact matches
                result = result.Replace(replacement.Key, replacement.Value);
            }

            return result;
        }

        public static void LogUIElementInfo(GameObject uiElement, ManualLogSource logger)
        {
            string elementName = uiElement.name;
            string gameObjectPath = GetGameObjectPath(uiElement);


            logger.LogInfo($"UI Element: {elementName}, GameObject Path: {gameObjectPath}");
        }

        public static async Task<AudioClip> LoadClip(string relativePath)
        {
            AudioClip clip = null;
            string modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string fullPath = Path.Combine(modDirectory, relativePath);
            string fileURL = "file://" + fullPath;

            try
            {
                using (UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip(fileURL, AudioType.WAV))
                {
                    var operation = uwr.SendWebRequest();

                    while (!operation.isDone)
                    {
                        await Task.Yield(); // Yield control back to the main thread
                    }

                    if (uwr.result == UnityWebRequest.Result.Success)
                    {
                        clip = DownloadHandlerAudioClip.GetContent(uwr);
                        Debug.Log($"Successfully loaded audio clip: {relativePath}");
                    }
                    else
                    {
                        Debug.LogError($"Failed to load audio clip: {uwr.error}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error loading audio clip: {ex.Message}");
            }

            return clip;
        }
    }
}