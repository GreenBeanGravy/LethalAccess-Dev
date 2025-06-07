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

        // Escape key handling to prevent pass-through
        private static bool escapeWasUsedToCloseMenu = false;
        private static float escapeReleaseTime = 0f;

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

                var currentItem = Items[CurrentItemIndex];
                return $"{CategoryName}: {currentItem.GetDisplayText()}, {CurrentItemIndex + 1} of {Items.Count}";
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
            escapeReleaseTime = Time.unscaledTime;

            DisableNavigationActions();

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
            if (originalEventSystem != null)
            {
                wasEventSystemEnabled = originalEventSystem.enabled;
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

            Debug.Log("AccessibleMenuSystem: Unblocked all other input");
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
                // If escape key is currently not pressed, allow future presses
                if (!Keyboard.current.escapeKey.isPressed)
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
            if (escapeWasUsedToCloseMenu && !Keyboard.current.escapeKey.isPressed)
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