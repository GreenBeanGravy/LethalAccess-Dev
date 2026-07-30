using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

namespace LethalAccess
{
    /// <summary>
    /// Base class for creating accessible menu systems with proper input management
    /// </summary>
    public abstract class AccessibleMenuSystem : MonoBehaviour
    {
        protected bool isMenuOpen = false;
        protected int currentItemIndex = 0;
        protected List<IMenuEntry> menuEntries = new List<IMenuEntry>();

        // Input management
        private InputAction[] navigationActions;
        private EventSystem originalEventSystem;
        private bool wasEventSystemEnabled;
        private bool wasCustomKeybindsEnabled;
        private bool wasUIAccessibilityEnabled;

        // What the game had focused before this menu took over. Blocking input used to null the
        // selection without recording it, so closing the menu left the EventSystem pointing at
        // nothing: re-enabling an EventSystem does not re-apply firstSelectedGameObject (that only
        // happens in EventSystem.Start), and the one thing that would re-select in the pause menu
        // is SelectUIForGamepad.OnEnable on MainButtons/Resume, which needs the whole QuickMenu to
        // be re-activated. The result was a dead keyboard after every use of this class.
        private GameObject selectionBeforeBlock;

        // The game's own Escape. See SuppressGameEscape.
        private InputAction gameOpenMenuAction;
        private bool gameOpenMenuWasEnabled;

        // Modal sub-states (a confirmation prompt, a "press a key now" rebind) suspend navigation.
        // Counted so a nested modal cannot resume early.
        private int navigationSuspendDepth;

        // Escape key handling to prevent pass-through. Per-instance: as static state this was
        // shared by every menu in the process, so closing one menu could swallow the next menu's
        // first Escape - or leave it armed for a menu that never saw the keypress at all.
        private bool escapeWasUsedToCloseMenu = false;

        // Menu properties
        protected virtual string MenuTitle => "Menu";
        protected virtual bool ShouldAnnounceFirstItem => true;
        protected virtual bool BlockAllInput => true;

        public interface IMenuEntry
        {
            string GetDisplayText();
            bool CanInteract();
            void OnSelect();
            void OnModify(int direction); // -1 for left/down, 1 for right/up
            bool HasModification();
        }

        public class SimpleMenuEntry : IMenuEntry
        {
            public string Text { get; set; }
            public Action OnSelectAction { get; set; }
            public Action<int> OnModifyAction { get; set; }
            public Func<bool> CanInteractFunc { get; set; }

            public SimpleMenuEntry(string text, Action onSelect = null, Action<int> onModify = null, Func<bool> canInteract = null)
            {
                Text = text;
                OnSelectAction = onSelect;
                OnModifyAction = onModify;
                CanInteractFunc = canInteract ?? (() => true);
            }

            public string GetDisplayText() => Text;
            public bool CanInteract() => CanInteractFunc();
            public void OnSelect() => OnSelectAction?.Invoke();
            public void OnModify(int direction) => OnModifyAction?.Invoke(direction);
            public bool HasModification() => OnModifyAction != null;
        }

        public class CategoryMenuEntry : IMenuEntry
        {
            public string CategoryName { get; set; }
            public List<IMenuEntry> Items { get; set; } = new List<IMenuEntry>();
            public int CurrentItemIndex { get; set; } = 0;

            public CategoryMenuEntry(string categoryName)
            {
                CategoryName = categoryName;
            }

            public string GetDisplayText()
            {
                if (Items.Count == 0)
                    return $"{CategoryName} (empty)";

                // Deliberately no "x of y" here. AnnounceCurrentItem appends the entry's own
                // "i of n", and two bare positions back to back in one utterance
                // ("Audio: Master Volume 50%, 2 of 3, 1 of 12") is heard as a stuttering bug
                // rather than as two different scopes.
                var currentItem = Items[CurrentItemIndex];
                return $"{CategoryName}: {currentItem.GetDisplayText()}";
            }

            public bool CanInteract() => Items.Count > 0 && Items[CurrentItemIndex].CanInteract();

            public void OnSelect()
            {
                if (Items.Count > 0)
                    Items[CurrentItemIndex].OnSelect();
            }

            public void OnModify(int direction)
            {
                if (Items.Count == 0) return;

                // First try to modify the current item
                if (Items[CurrentItemIndex].HasModification())
                {
                    Items[CurrentItemIndex].OnModify(direction);
                }
                else
                {
                    // Navigate within category
                    if (direction > 0)
                    {
                        CurrentItemIndex = (CurrentItemIndex + 1) % Items.Count;
                    }
                    else
                    {
                        CurrentItemIndex = (CurrentItemIndex - 1 + Items.Count) % Items.Count;
                    }
                }
            }

            public bool HasModification() => true; // Categories can always be navigated
        }

        protected virtual void Awake()
        {
            SetupInputActions();
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
                new InputAction("Back", InputActionType.Button, "<Keyboard>/escape")
            };

            navigationActions[0].performed += _ => NavigateUp();
            navigationActions[1].performed += _ => NavigateDown();
            navigationActions[2].performed += _ => NavigateLeft();
            navigationActions[3].performed += _ => NavigateRight();
            navigationActions[4].performed += _ => OnSelectPressed();
            navigationActions[5].performed += _ => OnBackPressed();
        }

        public virtual void OpenMenu()
        {
            if (isMenuOpen) return;

            isMenuOpen = true;
            currentItemIndex = 0;

            if (BlockAllInput)
            {
                BlockAllOtherInput();
            }

            SuppressGameEscape();

            // A modal that was abandoned by a destroyed coroutine would otherwise keep navigation
            // suspended for the whole next session.
            navigationSuspendDepth = 0;
            EnableNavigationActions();
            BuildMenuEntries();

            SpeechUtils.SpeakText($"{MenuTitle} opened");

            // Ensure we have items before announcing
            if (ShouldAnnounceFirstItem && menuEntries.Count > 0)
            {
                // Small delay to ensure speech doesn't overlap
                StartCoroutine(DelayedAnnounceFirstItem());
            }

            OnMenuOpened();
        }

        private System.Collections.IEnumerator DelayedAnnounceFirstItem()
        {
            yield return new WaitForSeconds(0.3f); // Short delay to prevent overlap
            if (isMenuOpen && menuEntries.Count > 0)
            {
                AnnounceCurrentItem();
            }
        }

        public virtual void CloseMenu()
        {
            if (!isMenuOpen) return;

            isMenuOpen = false;

            // Mark that escape was used to close menu to prevent pass-through
            escapeWasUsedToCloseMenu = true;

            // The suspend counter is deliberately NOT cleared here. A modal coroutine that is
            // still running holds one count and will release it on its next frame; zeroing the
            // counter under it would turn that release into a spurious "resumed more times than
            // suspended" error. OpenMenu resets it instead, which also covers a coroutine that
            // was abandoned outright.
            DisableNavigationActions();
            RestoreGameEscape();

            if (BlockAllInput)
            {
                UnblockAllOtherInput();
            }

            SpeechUtils.SpeakText($"{MenuTitle} closed");
            OnMenuClosed();
        }

        protected virtual void OnMenuOpened() { }
        protected virtual void OnMenuClosed() { }

        protected abstract void BuildMenuEntries();

        /// <summary>
        /// The pause menu's Escape is a separate, still-live binding: PlayerControllerB subscribes
        /// to the project-wide "OpenMenu" action, so one Escape both closes this menu AND toggles
        /// the QuickMenu underneath it. Take that action out for the menu's lifetime.
        /// </summary>
        private void SuppressGameEscape()
        {
            gameOpenMenuAction = null;
            gameOpenMenuWasEnabled = false;

            InputActionAsset actions = InputSystem.actions;
            if (actions == null)
            {
                Debug.LogWarning("[LethalAccess] There is no project-wide input action asset, so the game's own " +
                                 "Escape cannot be suppressed while '" + MenuTitle + "' is open; Escape may also " +
                                 "toggle the pause menu underneath it.");
                return;
            }

            InputAction action = actions.FindAction("OpenMenu");
            if (action == null)
            {
                Debug.LogWarning("[LethalAccess] No 'OpenMenu' action was found in the project-wide input asset, " +
                                 "so the game's own Escape cannot be suppressed while '" + MenuTitle +
                                 "' is open; Escape may also toggle the pause menu underneath it.");
                return;
            }

            gameOpenMenuAction = action;
            gameOpenMenuWasEnabled = action.enabled;
            if (gameOpenMenuWasEnabled)
            {
                action.Disable();
            }
        }

        /// <summary>
        /// Symmetric to SuppressGameEscape. Leaving this undone would make the pause menu
        /// permanently unopenable, so it is also run from OnDestroy.
        /// </summary>
        private void RestoreGameEscape()
        {
            if (gameOpenMenuAction == null)
            {
                return;
            }

            if (gameOpenMenuWasEnabled)
            {
                gameOpenMenuAction.Enable();
            }
            gameOpenMenuAction = null;
            gameOpenMenuWasEnabled = false;
        }

        private void BlockAllOtherInput()
        {
            // Store original states
            wasCustomKeybindsEnabled = LACore.enableCustomKeybinds;

            if (UIAccessibilityManager.Instance != null)
            {
                wasUIAccessibilityEnabled = UIAccessibilityManager.Instance.enabled;
                UIAccessibilityManager.Instance.enabled = false;
            }

            originalEventSystem = EventSystem.current;
            selectionBeforeBlock = null;
            if (originalEventSystem != null)
            {
                wasEventSystemEnabled = originalEventSystem.enabled;
                selectionBeforeBlock = originalEventSystem.currentSelectedGameObject;
                originalEventSystem.enabled = false;
                originalEventSystem.SetSelectedGameObject(null);
            }

            // Disable LACore custom keybinds
            LACore.enableCustomKeybinds = false;

            Debug.Log("AccessibleMenuSystem: Blocked all other input");
        }

        private void UnblockAllOtherInput()
        {
            // Restore original states
            LACore.enableCustomKeybinds = wasCustomKeybindsEnabled;

            if (UIAccessibilityManager.Instance != null)
            {
                UIAccessibilityManager.Instance.enabled = wasUIAccessibilityEnabled;
            }

            if (originalEventSystem != null)
            {
                originalEventSystem.enabled = wasEventSystemEnabled;
            }

            RestoreSelection();

            Debug.Log("AccessibleMenuSystem: Unblocked all other input");
        }

        /// <summary>
        /// Put focus back where it was. Order matters: the EventSystem and the announcer are
        /// re-enabled first, otherwise the selection is set but never spoken.
        /// </summary>
        private void RestoreSelection()
        {
            GameObject previous = selectionBeforeBlock;
            selectionBeforeBlock = null;

            if (previous != null && UIUtils.IsUIElementVisible(previous))
            {
                FocusManager.Seed(previous);
                return;
            }

            // The screen changed while this menu was up (or the control was destroyed). Ask the
            // screen model where focus belongs now rather than waiting out FocusManager's 0.25 s
            // watchdog on a dead keyboard.
            string screenId = UIScreenRegistry.ActiveScreenId;
            if (!string.IsNullOrEmpty(screenId))
            {
                Transform target = UIScreenRegistry.ResolveFocusTarget(screenId);
                if (target != null)
                {
                    FocusManager.Seed(target);
                    return;
                }
            }

            if (previous == null)
            {
                // Nothing was focused when the menu opened either, so there is nothing to put
                // back. Gameplay screens are legitimately in this state.
                return;
            }

            Debug.LogWarning("[LethalAccess] '" + MenuTitle + "' closed but the control that had focus " +
                             "beforehand is gone and no registered screen offered a replacement; focus recovery " +
                             "is left to FocusManager's watchdog.");
        }

        private void EnableNavigationActions()
        {
            foreach (var action in navigationActions)
            {
                action.Enable();
            }
        }

        private void DisableNavigationActions()
        {
            foreach (var action in navigationActions)
            {
                action.Disable();
            }
        }

        /// <summary>
        /// Take arrows/Enter/Escape out of service while a modal sub-state owns the keyboard.
        /// Without this the keypress that ANSWERS a prompt is also delivered to the Select action,
        /// which re-invokes whatever entry is focused - so confirming a destructive action
        /// re-triggers it.
        /// </summary>
        protected void SuspendNavigationActions()
        {
            navigationSuspendDepth++;
            if (navigationSuspendDepth == 1)
            {
                DisableNavigationActions();
            }
        }

        protected void ResumeNavigationActions()
        {
            if (navigationSuspendDepth <= 0)
            {
                Debug.LogError("[LethalAccess] ResumeNavigationActions was called more times than " +
                               "SuspendNavigationActions on '" + MenuTitle + "'; the counter has been clamped " +
                               "to zero and navigation re-synchronised from the menu's open state.");
                navigationSuspendDepth = 0;
            }
            else
            {
                navigationSuspendDepth--;
            }

            if (navigationSuspendDepth == 0 && isMenuOpen)
            {
                EnableNavigationActions();
            }
        }

        protected virtual void NavigateUp()
        {
            if (!isMenuOpen || menuEntries.Count == 0) return;

            currentItemIndex = (currentItemIndex - 1 + menuEntries.Count) % menuEntries.Count;
            AnnounceCurrentItem();
        }

        protected virtual void NavigateDown()
        {
            if (!isMenuOpen || menuEntries.Count == 0) return;

            currentItemIndex = (currentItemIndex + 1) % menuEntries.Count;
            AnnounceCurrentItem();
        }

        protected virtual void NavigateLeft()
        {
            if (!isMenuOpen || menuEntries.Count == 0) return;

            var currentEntry = menuEntries[currentItemIndex];
            if (currentEntry.HasModification())
            {
                currentEntry.OnModify(-1);
                AnnounceCurrentItem();
            }
        }

        protected virtual void NavigateRight()
        {
            if (!isMenuOpen || menuEntries.Count == 0) return;

            var currentEntry = menuEntries[currentItemIndex];
            if (currentEntry.HasModification())
            {
                currentEntry.OnModify(1);
                AnnounceCurrentItem();
            }
        }

        protected virtual void OnSelectPressed()
        {
            if (!isMenuOpen || menuEntries.Count == 0) return;

            var currentEntry = menuEntries[currentItemIndex];
            if (currentEntry.CanInteract())
            {
                currentEntry.OnSelect();
                // Re-announce in case the selection changed something
                AnnounceCurrentItem();
            }
            else
            {
                SpeechUtils.SpeakText("This item cannot be selected");
            }
        }

        protected virtual void OnBackPressed()
        {
            if (!isMenuOpen) return;

            // Check if we should ignore this escape press (to prevent pass-through)
            if (ShouldIgnoreEscapePress())
            {
                return;
            }

            CloseMenu();
        }

        private bool ShouldIgnoreEscapePress()
        {
            // If escape was used to close a menu recently, check if it's been released
            if (escapeWasUsedToCloseMenu)
            {
                Keyboard keyboard = Keyboard.current;
                if (keyboard == null)
                {
                    // No keyboard means this press came from somewhere else entirely; there is no
                    // held key to wait out.
                    escapeWasUsedToCloseMenu = false;
                    return false;
                }

                // If escape key is currently not pressed, allow future presses
                if (!keyboard.escapeKey.isPressed)
                {
                    escapeWasUsedToCloseMenu = false;
                    return false; // Allow this press
                }
                else
                {
                    // Escape is still held down, ignore this press
                    return true;
                }
            }

            return false; // Normal escape press, allow it
        }

        protected virtual void AnnounceCurrentItem()
        {
            if (currentItemIndex >= 0 && currentItemIndex < menuEntries.Count)
            {
                var entry = menuEntries[currentItemIndex];
                string itemText = entry.GetDisplayText();
                string positionText = $", {currentItemIndex + 1} of {menuEntries.Count}";
                SpeechUtils.SpeakText(itemText + positionText);
            }
        }

        protected void RefreshCurrentItem()
        {
            if (isMenuOpen)
            {
                AnnounceCurrentItem();
            }
        }

        protected virtual void Update()
        {
            // Handle escape key state tracking
            Keyboard keyboard = Keyboard.current;
            if (escapeWasUsedToCloseMenu && (keyboard == null || !keyboard.escapeKey.isPressed))
            {
                escapeWasUsedToCloseMenu = false;
            }
        }

        protected virtual void OnDestroy()
        {
            if (isMenuOpen)
            {
                CloseMenu();
            }

            // CloseMenu already does this, but a half-constructed or already-closed instance that
            // still holds the action would leave the pause menu permanently unopenable.
            RestoreGameEscape();

            if (navigationActions != null)
            {
                foreach (var action in navigationActions)
                {
                    try
                    {
                        action?.Disable();
                        action?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error disposing navigation action: {ex.Message}");
                    }
                }
            }
        }

        // Helper methods for common menu patterns
        protected void AddSimpleItem(string text, Action onSelect = null)
        {
            menuEntries.Add(new SimpleMenuEntry(text, onSelect));
        }

        protected void AddModifiableItem(string text, Action<int> onModify, Action onSelect = null)
        {
            menuEntries.Add(new SimpleMenuEntry(text, onSelect, onModify));
        }

        protected void AddCategory(string categoryName, List<IMenuEntry> items)
        {
            var category = new CategoryMenuEntry(categoryName);
            category.Items.AddRange(items);
            menuEntries.Add(category);
        }

        protected void ClearMenuEntries()
        {
            menuEntries.Clear();
            currentItemIndex = 0;
        }
    }
}
