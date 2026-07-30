using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LethalAccess
{
    /// <summary>
    /// UI utility functions for LethalAccess.
    ///
    /// Text scraping, special-character stripping and hierarchy-path building all live in
    /// <see cref="Utilities"/>; the versions here are thin forwards. Keep them that way - as
    /// byte-identical copies a label rule could be fixed in one and silently not apply through the
    /// other, because LACore's custom text providers go through this class while the announcer
    /// goes through Utilities.
    /// </summary>
    public static class UIUtils
    {
        // ------------------------------------------------------------------
        // Path resolution that sees inactive objects
        // ------------------------------------------------------------------

        /// <summary>
        /// Resolve a '/'-separated path below <paramref name="root"/>, traversing inactive
        /// children.
        ///
        /// This exists because <c>GameObject.Find</c> is ACTIVE-ONLY. Every panel in this game
        /// ships with <c>m_IsActive = false</c> and is turned on by a serialized UnityEvent, so
        /// <c>GameObject.Find("Canvas/MenuContainer/SettingsPanel/...")</c> returns null right up
        /// until the panel is on screen - which is why both SetNavigation calls in Plugin.cs never
        /// applied and why ValidateRegisteredPaths reported every SettingsPanel and
        /// LobbyHostSettings path as broken on every launch.
        ///
        /// <c>Transform.Find</c> has no such restriction: it walks the child list directly.
        /// </summary>
        /// <returns>The resolved transform, or null if any segment is missing.</returns>
        public static Transform FindByPathIncludingInactive(Transform root, string relativePath)
        {
            if (root == null)
            {
                return null;
            }
            if (string.IsNullOrEmpty(relativePath))
            {
                return root;
            }

            Transform current = root;
            string[] segments = relativePath.Split('/');

            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];

                // Tolerate a leading, trailing or doubled slash rather than failing to resolve a
                // path that is only cosmetically malformed.
                if (segment.Length == 0)
                {
                    continue;
                }

                current = current.Find(segment);
                if (current == null)
                {
                    return null;
                }
            }

            return current;
        }

        /// <summary>
        /// Resolve an absolute scene path ("Canvas/MenuContainer/SettingsPanel/BackButton"),
        /// traversing inactive objects.
        ///
        /// Every loaded scene is searched, active scene first. That matters in this game because
        /// the menu and the in-game relay scene are loaded together at various points, and the
        /// active one is not always the one holding the UI being asked about.
        ///
        /// KNOWN GAP: the DontDestroyOnLoad pseudo-scene is not returned by SceneManager, so a
        /// path whose root object has been marked DontDestroyOnLoad will NOT resolve here. Unity
        /// exposes no API to enumerate that scene's roots. Nothing currently registered by this
        /// mod lives there (the menu roots are in MainMenu/InitSceneLaunchOptions and "Systems" is
        /// a root of the in-game relay scene), but a caller that needs it must resolve from a live
        /// component reference instead of a path.
        /// </summary>
        public static Transform FindByPathIncludingInactive(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
            {
                return null;
            }

            int slash = absolutePath.IndexOf('/');
            string rootName = slash < 0 ? absolutePath : absolutePath.Substring(0, slash);
            string remainder = slash < 0 ? string.Empty : absolutePath.Substring(slash + 1);

            Scene active = SceneManager.GetActiveScene();
            Transform hit = FindInScene(active, rootName, remainder);
            if (hit != null)
            {
                return hit;
            }

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene == active)
                {
                    continue;
                }

                hit = FindInScene(scene, rootName, remainder);
                if (hit != null)
                {
                    return hit;
                }
            }

            return null;
        }

        private static Transform FindInScene(Scene scene, string rootName, string remainder)
        {
            // GetRootGameObjects throws on a scene that is not finished loading.
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return null;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (root == null || root.name != rootName)
                {
                    continue;
                }

                // Root names are not unique - keep scanning rather than committing to the first
                // object that merely has the right name.
                Transform resolved = remainder.Length == 0
                    ? root.transform
                    : FindByPathIncludingInactive(root.transform, remainder);

                if (resolved != null)
                {
                    return resolved;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------
        // Text
        // ------------------------------------------------------------------

        /// <summary>
        /// Get text from a GameObject by absolute path. Resolves inactive objects.
        /// </summary>
        public static string GetTextFromGameObject(string gameObjectPath)
        {
            Transform target = FindByPathIncludingInactive(gameObjectPath);
            if (target != null)
            {
                return GetTextFromComponent(target.gameObject);
            }
            return string.Empty;
        }

        /// <summary>
        /// Get the label text for a GameObject. See <see cref="Utilities.GetTextFromComponent"/>
        /// for the rules (blank text is skipped, sliders and buttons fall back to a sibling label).
        /// </summary>
        public static string GetTextFromComponent(GameObject gameObject)
        {
            return Utilities.GetTextFromComponent(gameObject);
        }

        /// <summary>
        /// Strip bracket characters and trim. Single implementation in <see cref="Utilities"/>.
        /// </summary>
        public static string RemoveSpecialCharacters(string input)
        {
            return Utilities.RemoveSpecialCharacters(input);
        }

        /// <summary>
        /// Full hierarchy path of a GameObject. Single implementation in <see cref="Utilities"/>.
        /// </summary>
        public static string GetGameObjectPath(GameObject gameObject)
        {
            return Utilities.GetGameObjectPath(gameObject);
        }

        // ------------------------------------------------------------------
        // Sliders
        // ------------------------------------------------------------------

        /// <summary>
        /// Read a slider's value given the path of its handle. Used by the custom text providers
        /// registered in Plugin.cs for the master-volume and look-sensitivity rows.
        /// </summary>
        public static int GetSliderValue(string sliderHandlePath)
        {
            Transform sliderHandle = FindByPathIncludingInactive(sliderHandlePath);
            if (sliderHandle != null)
            {
                // includeInactive: the handle resolves through inactive parents now, so the parent
                // walk has to as well or this returns 0 for a panel that is not on screen yet.
                UnityEngine.UI.Slider slider = sliderHandle.GetComponentInParent<UnityEngine.UI.Slider>(true);
                if (slider != null)
                {
                    return Mathf.RoundToInt(slider.value);
                }
            }
            return 0;
        }

        // ------------------------------------------------------------------
        // Registration helpers
        // ------------------------------------------------------------------

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
        /// Check if a UI element is visible and interactable.
        /// The screen registry uses this to decide which entries count towards "n of m", so an
        /// element hidden behind a zero-alpha or non-interactable CanvasGroup must fail here.
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

        // ------------------------------------------------------------------
        // Server list
        // ------------------------------------------------------------------

        /// <summary>
        /// Describe the lobby row that owns <paramref name="selectedObject"/> (typically a
        /// JoinButton). Resolves the LobbySlot with GetComponentInParent rather than
        /// transform.parent.GetComponent: SteamLobbyManager instantiates the row prefab and takes
        /// the LobbySlot with GetComponentInChildren (SteamLobbyManager.loadLobbyListAndFilter), so the component
        /// is not guaranteed to sit exactly one level above the button.
        /// </summary>
        public static string GetLobbyInfoFromJoinButton(GameObject selectedObject)
        {
            if (selectedObject == null)
            {
                return string.Empty;
            }

            LobbySlot lobbySlot = selectedObject.GetComponentInParent<LobbySlot>(true);
            if (lobbySlot == null)
            {
                return string.Empty;
            }

            return BuildLobbyRowLabel(lobbySlot, 0, 0);
        }

        /// <summary>
        /// The spoken label for one server-list row.
        ///
        /// <paramref name="index"/> is 1-BASED and <paramref name="total"/> is the number of rows
        /// currently visible, matching UIScreenRegistry.TryGetPosition. Pass 0 for both to omit
        /// the position - do that when the announcer is already appending one, so the row is not
        /// numbered twice.
        ///
        /// Pass `i + 1` when iterating rows - the index is 1-based so it matches TryGetPosition and
        /// every other "n of m" in the mod. An index outside 1..total is treated as "no position"
        /// rather than announced, so an off-by-one shows up as a missing position on row 1 instead
        /// of as a wrong number on every row.
        /// </summary>
        public static string BuildLobbyRowLabel(LobbySlot slot, int index, int total)
        {
            if (slot == null)
            {
                return string.Empty;
            }

            // SteamLobbyManager writes LobbyName.text directly (SteamLobbyManager.loadLobbyListAndFilter), so this
            // is the real lobby name, not a scene placeholder.
            string name = slot.LobbyName != null
                ? Utilities.RemoveSpecialCharacters(slot.LobbyName.text)
                : string.Empty;

            if (string.IsNullOrEmpty(name))
            {
                // A row with no name is still joinable; saying nothing would make it unreachable.
                name = "Unnamed lobby";
            }

            string label = name;

            string players = slot.playerCount != null
                ? Utilities.RemoveSpecialCharacters(slot.playerCount.text)
                : string.Empty;

            if (!string.IsNullOrEmpty(players))
            {
                // The game formats this as "3 / 4" (SteamLobbyManager.loadLobbyListAndFilter). Spoken verbatim a
                // slash reads as punctuation or is swallowed entirely, so say the relation.
                int separator = players.IndexOf('/');
                if (separator > 0 && separator < players.Length - 1)
                {
                    string here = players.Substring(0, separator).Trim();
                    string cap = players.Substring(separator + 1).Trim();
                    if (here.Length > 0 && cap.Length > 0)
                    {
                        players = here + " of " + cap;
                    }
                }
                label += ", " + players + " players";
            }

            // LobbySlot.SetModdedIcon only ever ENABLES an icon - the Vanilla case enables neither
            // and nothing ever disables one - so "both off" and "both on" are equally
            // uninformative. Only speak a mod state when exactly one icon is showing, rather than
            // reporting a lobby as modded on the strength of a prefab default.
            // TODO(verify): open the server list and read the authored Image.enabled values on
            // LobbySlotPrefab; if Vanilla is provably "both off", this can also say "no mods".
            bool modded = slot.modStateTrueIcon != null && slot.modStateTrueIcon.enabled;
            bool unknown = slot.modStateUnknownIcon != null && slot.modStateUnknownIcon.enabled;
            if (modded && !unknown)
            {
                label += ", modded";
            }
            else if (unknown && !modded)
            {
                label += ", mods unknown";
            }

            if (total > 0 && index >= 1 && index <= total)
            {
                label += ", " + index + " of " + total;
            }

            return label;
        }
    }
}
