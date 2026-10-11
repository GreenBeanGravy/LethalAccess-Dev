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

        // Reset-to-defaults confirmation state, so a second request cannot stack a second
        // coroutine reading the same keypress.
        private bool isResetConfirmationPending = false;

        public void Initialize()
        {
            // F10 used to be bound TWICE to this same toggle: this always-enabled InputAction and
            // a LACore keybind polled from LACore.Update. Opening worked by luck (the action fires
            // before Update, and OpenMenu clears enableCustomKeybinds first) but closing did not,
            // because CloseMenu restores that flag before LACore.Update runs in the same frame, so
            // CheckKeybinds re-toggled and the menu reopened immediately.
            //
            // This half is the one that survives: it works while enableCustomKeybinds is false,
            // which is exactly the state this menu puts the game into. The LACore "OpenSettingsMenu"
            // keybind registration is deleted in Plugin.cs.
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

            // Removed: "Object Scan Interval". Nothing read that setting - scans are triggered by
            // the category keybinds rather than polled - so the control did nothing.

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
            IList<ModKeybinds.Definition> keybinds = ModKeybinds.Definitions;
            for (int i = 0; i < keybinds.Count; i++)
            {
                ModKeybinds.Definition keybind = keybinds[i];
                string id = keybind.Id;
                AddMenuItem(() => $"{keybind.Name}: {ModKeybinds.DescribeBinding(id)}",
                    onSelect: () => StartKeybindRebind(keybind));
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
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                // No keyboard device is present, so there is nothing to read this frame. Waiting
                // is the correct behaviour; throwing an NRE out of Update every frame is not.
                return;
            }

            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                CancelKeybindRebind();
                return;
            }

            foreach (Key key in System.Enum.GetValues(typeof(Key)))
            {
                if (IsInvalidRebindKey(key)) continue;

                // Deliberately unguarded. Every value that reaches here is a real, non-null
                // KeyControl on this Input System version (see IsInvalidRebindKey), so an
                // exception out of this indexer means the skip list has gone stale and must be
                // seen. The try/catch that used to sit here caught ArgumentOutOfRangeException
                // and continued, which could only ever have hidden that regression: it would
                // have reached the user as "rebinding ignores some keys" with nothing in the log.
                if (keyboard[key].wasPressedThisFrame)
                {
                    SetKeybind(key);
                    return;
                }
            }
        }

        /// <summary>
        /// Keys that must not be offered as a rebind target. Two unrelated reasons, grouped
        /// together only because both mean "skip" - they are NOT interchangeable.
        /// </summary>
        private bool IsInvalidRebindKey(Key key)
        {
            // GROUP 1 - not addressable through Keyboard's indexer. Load-bearing: dropping either
            // of these reintroduces a per-frame throw out of Update while the rebind prompt is up.
            //
            // Keyboard.get_Item is "num = (int)key - 1; if (num < 0 || num >= m_Keys.Length)
            // throw new ArgumentOutOfRangeException", and m_Keys is a KeyControl[]. Verified
            // against the running game rather than assumed: m_Keys.Length is 123 == (int)Key.F24,
            // and indexing every value of Key reports exactly two bad entries and no others -
            //   None (0)          throws ArgumentOutOfRangeException, since (int)key - 1 == -1.
            //   IMESelected (111) returns NULL. The device's IMESelected child is a ButtonControl
            //                     ("Button:/Keyboard/IMESelected"), not a KeyControl, so
            //                     m_Keys[110] was never filled in. That is an NRE, not an
            //                     ArgumentOutOfRangeException, so the old catch never covered it.
            // This enum carries no Count/Begin/End range markers to skip. Its only other oddity
            // is duplicate-valued aliases (AltGr == RightAlt; LeftWindows/LeftApple/LeftCommand
            // == LeftMeta, and the Right- equivalents), which are real bindable controls and
            // merely make the loop visit those values more than once - harmless.
            //
            // If a future Unity/Input System upgrade extends Key past m_Keys.Length, or adds
            // another non-KeyControl slot, this list is wrong and the loop above will throw
            // loudly. That is intended: a visible failure beats rebinding silently going deaf.
            if (key == Key.None || key == Key.IMESelected)
            {
                return true;
            }

            // GROUP 2 - policy, not safety. These all index fine; they are reserved because the
            // menu itself reads them (Escape cancels this prompt, Enter and Space select, the
            // arrows move the selection, F10 toggles the menu), so binding an action to one of
            // them would leave the menu unusable.
            return key == Key.Escape ||
                   key == Key.Enter ||
                   key == Key.Space ||
                   key == Key.UpArrow ||
                   key == Key.DownArrow ||
                   key == Key.LeftArrow ||
                   key == Key.RightArrow ||
                   key == Key.F10;
        }

        private void StartKeybindRebind(ModKeybinds.Definition keybind)
        {
            // With InputUtils the key lives in its action map, and a config write here would be
            // overruled by the binds InputUtils saves. One place to change a key, not two that
            // disagree.
            if (keybind.Action != null)
            {
                SpeechUtils.SpeakText(keybind.Name + " is " + ModKeybinds.DescribeBinding(keybind.Id) +
                                      ". LethalAccess keys are changed on the game's Remap Controls screen, " +
                                      "under LethalAccess: Settings, then Change keybinds.");
                return;
            }

            if (keybind.Config == null)
            {
                SpeechUtils.SpeakText(keybind.Name + " cannot be changed here.");
                return;
            }

            isWaitingForKey = true;
            currentKeybindEntry = keybind.Config;

            // "Press a key" is modal: with the navigation actions still live, the arrow keys move
            // the menu selection underneath the prompt (they are rejected as bindings, so the user
            // hears a different entry announced and the rebind is still waiting), and Escape is
            // read both here and by the Back action.
            SuspendNavigationActions();

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
                ResumeNavigationActions();

                string keyName = GetKeyDisplayName(newKey);
                SpeechUtils.SpeakText($"Successfully rebound to {keyName}");
                currentKeybindEntry = null;
                RefreshCurrentItem();
            }
        }

        private string CheckKeybindConflict(Key newKey)
        {
            IList<ModKeybinds.Definition> keybinds = ModKeybinds.Definitions;
            for (int i = 0; i < keybinds.Count; i++)
            {
                ModKeybinds.Definition other = keybinds[i];
                if (other.Config != null && other.Config != currentKeybindEntry && other.Config.Value == newKey)
                {
                    return other.Name;
                }
            }
            return null;
        }

        private void CancelKeybindRebind()
        {
            // Keyed on isWaitingForKey, not on currentKeybindEntry: those two are set together,
            // but if they ever diverged the old guard left the menu permanently modal - waiting
            // for a key, with navigation suspended and no way out.
            if (!isWaitingForKey)
            {
                return;
            }

            isWaitingForKey = false;
            currentKeybindEntry = null;
            ResumeNavigationActions();
            SpeechUtils.SpeakText("Cancelled rebinding");
            RefreshCurrentItem();
        }

        private void ResetAllSettings()
        {
            if (isResetConfirmationPending)
            {
                // Nothing should be able to get here now that navigation is suspended for the
                // duration, but stacking a second coroutine on the same prompt would mean two
                // readers of the same Enter and two resets.
                return;
            }

            isResetConfirmationPending = true;
            SpeechUtils.SpeakText("Are you sure you want to reset all settings to default? Press Enter to confirm, or Escape to cancel.");
            StartCoroutine(WaitForResetConfirmation());
        }

        private System.Collections.IEnumerator WaitForResetConfirmation()
        {
            // StartCoroutine runs the body synchronously up to the first yield, and the Input
            // System processes events before MonoBehaviour updates in the same frame - so without
            // this yield the loop below reads the very Enter that ASKED for the confirmation and
            // wipes every setting with no chance to cancel. This has to stay the first statement.
            yield return null;

            // Enter is also bound to the Select action, which would re-invoke whatever entry is
            // focused ("Reset All Settings") as the user confirms. Escape is likewise read both
            // here and by the Back action, which would close the whole menu behind the prompt.
            SuspendNavigationActions();

            bool waitingForConfirmation = true;

            while (waitingForConfirmation)
            {
                if (!isMenuOpen)
                {
                    // The menu went away underneath the prompt; abandon it rather than resetting
                    // settings for a user who is no longer looking at the question.
                    SpeechUtils.SpeakText("Reset cancelled");
                    break;
                }

                Keyboard keyboard = Keyboard.current;
                if (keyboard == null)
                {
                    yield return null;
                    continue;
                }

                if (keyboard.enterKey.wasPressedThisFrame)
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
                else if (keyboard.escapeKey.wasPressedThisFrame)
                {
                    SpeechUtils.SpeakText("Reset cancelled");
                    RefreshCurrentItem();
                    waitingForConfirmation = false;
                }

                if (waitingForConfirmation)
                {
                    yield return null;
                }
            }

            isResetConfirmationPending = false;
            ResumeNavigationActions();
        }

        // Helper methods
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