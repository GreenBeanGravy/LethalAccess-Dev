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
        // ------------------------------------------------------------------
        // Label resolution
        //
        // Everything below is the single implementation and UIUtils forwards to it. Do not copy any
        // of it back there: with two copies a fix lands in one and is invisible through the other.
        // ------------------------------------------------------------------

        /// <summary>
        /// The label text for a UI object.
        ///
        /// Order: the first text component in its own subtree that actually says something, then
        /// (for sliders and buttons only) a label on a sibling, then the GameObject name.
        ///
        /// "Actually says something" is the important part. Returning the first TextMeshProUGUI
        /// unconditionally produces three separate wrong announcements:
        ///   - "Rebind Button" for every keybind row that shows a controller glyph, because
        ///     InputUtils blanks bindLabel whenever it draws a sprite instead
        ///     (RebindButton.UpdateState: bindLabel.SetText("", true)).
        ///   - "SectionEntry" for every keybinds section, because the first TMP in that prefab is
        ///     SectionEntry.indicator: a ">" that RemoveSpecialCharacters reduces to nothing, and
        ///     whose Behaviour is disabled unless that section is the selected one.
        ///   - the GameObject name for every slider, because a Slider's subtree is Background /
        ///     Fill Area / Handle Slide Area and holds no text at all; the label is a sibling
        ///     ("Gamma/Brightness:" lives at BrightnessSetting/Text (1), beside the Slider).
        /// </summary>
        public static string GetTextFromComponent(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return string.Empty;
            }

            string text;
            if (TryGetUsableText(gameObject.transform, out text))
            {
                return text;
            }

            // Restricted to Slider and Button on purpose. A control that carries its own text
            // (a toggle, a dropdown, the mic button reading "ENABLED") must never be relabelled by
            // whatever happens to sit next to it, and a container holding several buttons would
            // otherwise hand every one of them its neighbour's label.
            if (gameObject.GetComponent<UnityEngine.UI.Slider>() != null ||
                gameObject.GetComponent<UnityEngine.UI.Button>() != null)
            {
                string sibling = GetSiblingLabel(gameObject.transform);
                if (!string.IsNullOrEmpty(sibling))
                {
                    return sibling;
                }
            }

            return gameObject.name;
        }

        /// <summary>
        /// Find the label that belongs to <paramref name="element"/> but is authored beside it
        /// rather than inside it. This is where the real name of most settings lives:
        /// "Gamma/Brightness:", "Master volume:", "Look sensitivity:", "Voice volume",
        /// "With detected mods", "With challenge moon", and every keybind row's action name.
        ///
        /// Siblings that are (or contain) a Selectable are skipped; their text is that control's
        /// own label. Public so screen descriptors can use it as a Label/State provider.
        /// </summary>
        public static string GetSiblingLabel(Transform element)
        {
            if (element == null || element.parent == null)
            {
                return string.Empty;
            }

            Transform parent = element.parent;
            string text;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform sibling = parent.GetChild(i);
                if (sibling == element)
                {
                    continue;
                }

                // A sibling that is (or contains) a control owns its text; taking it would
                // announce the wrong control's name (MicSettings holds ChooseDevice, SpeakerButton
                // and TalkMode side by side, and several settings rows wrap their button one level
                // down). Reading a neighbour's label is worse than falling back to the object
                // name: the name is merely unhelpful, a neighbour's label is wrong.
                //
                // Note the interaction with FocusInjection.MakeFocusable: a heading or body TMP
                // that has been made focusable becomes a Selectable and will be skipped here. That
                // is intended: anything the user can focus in its own right should not also be
                // read out as some other control's name.
                if (sibling.GetComponentInChildren<UnityEngine.UI.Selectable>(true) != null)
                {
                    continue;
                }

                if (TryGetUsableText(sibling, out text))
                {
                    return text;
                }
            }

            // Some rows put the label on the container itself instead of in a child.
            UnityEngine.UI.Selectable parentSelectable = parent.GetComponent<UnityEngine.UI.Selectable>();
            if (parentSelectable == null)
            {
                TMPro.TextMeshProUGUI ownTmp = parent.GetComponent<TMPro.TextMeshProUGUI>();
                if (ownTmp != null && IsUsableLabel(ownTmp.text))
                {
                    return ownTmp.text;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// The caption of a dropdown: what the control IS, not what it is currently set to.
        ///
        /// A TMP_Dropdown's first child TMP is "Label", which holds the CURRENT VALUE. Announcing
        /// that as the name is why every dropdown on the settings panel said its value twice and
        /// never its own name. The caption is the sibling child named "Label2".
        ///
        /// TODO(unverified): the "Label2" name has not been confirmed against a loaded settings
        /// panel. If it is wrong, the sibling fallback below still supplies the row's label and
        /// nothing announces a value as a name, so this fails quietly rather than audibly. Confirm
        /// by inspecting the children of Canvas/MenuContainer/SettingsPanel/FullscreenMode.
        /// </summary>
        public static string GetDropdownCaption(Transform dropdown)
        {
            if (dropdown == null)
            {
                return string.Empty;
            }

            Transform caption = dropdown.Find("Label2");
            if (caption != null)
            {
                string text;
                if (TryGetUsableText(caption, out text))
                {
                    return text;
                }
            }

            // Not every dropdown carries its own caption; several are labelled by a sibling in the
            // row container, exactly like the sliders.
            return GetSiblingLabel(dropdown);
        }

        /// <summary>
        /// First text component in <paramref name="root"/>'s subtree whose text survives
        /// <see cref="RemoveSpecialCharacters"/>, preferring one that is actually being drawn.
        ///
        /// The second pass matters because labels are read while their panel is still inactive
        /// (screen descriptors resolve entries before the panel is shown), and because
        /// SectionEntry.SetIndicator toggles Behaviour.enabled, which leaves activeInHierarchy
        /// true.
        /// </summary>
        private static bool TryGetUsableText(Transform root, out string text)
        {
            text = string.Empty;
            if (root == null)
            {
                return false;
            }

            // Hierarchy order, self first: deterministic, unlike the old mix of an active-only
            // GetComponentInChildren followed by a manual recursion over inactive children.
            TMPro.TextMeshProUGUI[] tmps = root.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantDrawn = pass == 0;
                for (int i = 0; i < tmps.Length; i++)
                {
                    TMPro.TextMeshProUGUI tmp = tmps[i];
                    if (tmp == null || tmp.isActiveAndEnabled != wantDrawn)
                    {
                        continue;
                    }
                    if (!IsUsableLabel(tmp.text))
                    {
                        continue;
                    }
                    text = tmp.text;
                    return true;
                }
            }

            UnityEngine.UI.Text[] legacy = root.GetComponentsInChildren<UnityEngine.UI.Text>(true);
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantDrawn = pass == 0;
                for (int i = 0; i < legacy.Length; i++)
                {
                    UnityEngine.UI.Text label = legacy[i];
                    if (label == null || label.isActiveAndEnabled != wantDrawn)
                    {
                        continue;
                    }
                    if (!IsUsableLabel(label.text))
                    {
                        continue;
                    }
                    text = label.text;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when this string would still say something after the announcer cleans it. The
        /// cleaned form is what decides, not the raw text: a ">" indicator is non-empty but
        /// RemoveSpecialCharacters reduces it to nothing, and announcing it means announcing the
        /// GameObject name instead of the label two children further down.
        /// </summary>
        private static bool IsUsableLabel(string raw)
        {
            return !string.IsNullOrEmpty(raw) && RemoveSpecialCharacters(raw).Length > 0;
        }

        /// <summary>
        /// Strip the bracket characters the game's scene text is decorated with, and trim.
        ///
        /// The trim is not cosmetic: the menu labels are authored as "> Host", "> Back" and so on,
        /// and without it every one of them is announced with a leading space, which some screen
        /// readers render as a pause and which made "Back" and " Back" compare as different
        /// strings anywhere a label was matched.
        /// </summary>
        public static string RemoveSpecialCharacters(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            string specialCharacters = "[]{}<>()";
            foreach (char c in specialCharacters)
            {
                input = input.Replace(c.ToString(), string.Empty);
            }
            return input.Trim();
        }

        public static string GetGameObjectPath(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return string.Empty;
            }

            string path = gameObject.name;
            Transform parent = gameObject.transform.parent;

            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        // ------------------------------------------------------------------
        // Camera
        // ------------------------------------------------------------------

        // PlayerControllerB.CalculateNormalLookingInput(Vector2) is private, so it has to be
        // reached reflectively. Resolving the MethodInfo and calling Invoke(new object[]{...})
        // happened on every frame a look target was set: a reflective lookup, a reflective
        // dispatch, a boxed Vector2 and a one-element object[] per frame. Bound once to an open
        // delegate instead; the call site is then a normal delegate invoke with no allocation.
        private static Action<PlayerControllerB, Vector2> calculateNormalLookingInput;
        private static bool calculateNormalLookingInputResolved;

        private static Action<PlayerControllerB, Vector2> GetCalculateNormalLookingInput()
        {
            if (calculateNormalLookingInputResolved)
            {
                return calculateNormalLookingInput;
            }
            calculateNormalLookingInputResolved = true;

            var method = typeof(PlayerControllerB).GetMethod(
                "CalculateNormalLookingInput",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (method == null)
            {
                Debug.LogError("[LethalAccess] PlayerControllerB.CalculateNormalLookingInput not found; " +
                               "look-at-object will not aim the camera. The game's method has been renamed.");
                return null;
            }

            calculateNormalLookingInput =
                (Action<PlayerControllerB, Vector2>)Delegate.CreateDelegate(
                    typeof(Action<PlayerControllerB, Vector2>), method);

            return calculateNormalLookingInput;
        }

        public static void LookAtObject(GameObject targetObject)
        {
            if (targetObject != null)
            {
                LookAtPoint(targetObject.transform.position);
            }
        }

        /// <summary>One frame's turn of the camera toward point, through the game's own look input so the
        /// rotation syncs to other players. Call every frame until it is reached.</summary>
        public static void LookAtPoint(Vector3 point)
        {
            if (LACore.CameraTransform != null)
            {
                float maxDistance = 15f;
                float distanceToTarget = Vector3.Distance(LACore.CameraTransform.position, point);

                if (distanceToTarget <= maxDistance)
                {
                    Transform playerTransform = LACore.PlayerTransform;
                    if (playerTransform == null)
                    {
                        return;
                    }

                    PlayerControllerB playerControllerB = playerTransform.GetComponent<PlayerControllerB>();

                    if (playerControllerB != null)
                    {
                        Vector3 targetDirection = point - LACore.CameraTransform.position;
                        Vector3 relativeDirection = LACore.CameraTransform.InverseTransformDirection(targetDirection);

                        // Increase the rotation speed by multiplying the input
                        float rotationSpeedMultiplier = 5f; // Adjust this value to change the rotation speed
                        Vector2 adjustedInput = new Vector2(relativeDirection.x, relativeDirection.y) * rotationSpeedMultiplier;

                        GetCalculateNormalLookingInput()?.Invoke(playerControllerB, adjustedInput);
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Speech
        // ------------------------------------------------------------------

        /// <summary>
        /// One front door.
        ///
        /// This used to reach SpeechSynthesizer directly with its own private copy of the
        /// text-replacement table and no config gate, while SpeechUtils.SpeakText applied a larger
        /// table, normalised "%" to "percent" and "$" to "dollars", collapsed whitespace, and
        /// honoured EnableAudioCues. The same string was therefore spoken two different ways
        /// depending on which layer raised it, and turning audio cues off silenced the mod's own
        /// menus while leaving ~150 game announcements audible.
        ///
        /// Consequence worth knowing: EnableAudioCues = false now silences these callers too.
        /// That is the intended meaning of the setting; nothing else in the mod distinguishes
        /// "cue" from "announcement".
        /// </summary>
        public static void SpeakText(string text)
        {
            SpeechUtils.SpeakText(text);
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
