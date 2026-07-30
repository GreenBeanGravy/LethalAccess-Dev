using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LethalAccess
{
    /// <summary>
    /// Owns keyboard focus: seeding it when a screen opens, and getting it back when the game
    /// throws it away.
    ///
    /// Focus loss is not an edge case in Lethal Company. It happens every time a panel destroys
    /// the object that was focused ("Set to defaults" rebuilding the keybind rows, the lobby list
    /// refreshing, the join/loading transition) and every time a screen opens without selecting
    /// anything (the launch-mode screen on a first run). Unity offers no recovery of its own:
    /// EventSystem.m_FirstSelected is (0,0) in every scene in this game, so once
    /// currentSelectedGameObject goes null nothing ever puts it back and the keyboard is dead.
    /// This watchdog is the only thing that recovers it.
    /// </summary>
    public static class FocusManager
    {
        // Long enough that a normal panel switch (deactivate, activate, select, all within a
        // frame or two) never trips it; short enough that a blind user does not sit on a dead
        // keyboard wondering whether the game hung.
        private const float NullSelectionGraceSeconds = 0.25f;

        // If a recovery does not take, do not retry every frame.
        private const float RecoveryCooldownSeconds = 0.5f;

        private static float nullSince = -1f;
        private static float lastRecoveryTime = -1f;
        private static bool warnedNoRecoveryTarget;

        /// <summary>
        /// Point the EventSystem at a control and make sure it gets announced even if it was the
        /// last thing announced. Announcement itself stays with UIAccessibilityManager - this
        /// only clears the "already said that" memo so the announcer runs on the next Update.
        /// </summary>
        public static void Seed(Transform target)
        {
            if (target == null)
            {
                Debug.LogWarning("[LethalAccess] FocusManager.Seed called with no target; focus left where it was.");
                return;
            }

            if (!UIUtils.IsUIElementVisible(target.gameObject))
            {
                // Seeding an invisible object strands the keyboard exactly the way the watchdog
                // exists to prevent, so refuse loudly instead.
                Debug.LogWarning("[LethalAccess] FocusManager.Seed refused '" + target.name +
                                 "': it is not visible. Focus left where it was.");
                return;
            }

            // Every path that moves focus comes through here, which makes this the one place a
            // focus trap can be enforced without hunting down callers. Redirected rather than
            // refused: a modal that swallows the seed and leaves focus on whatever was behind it is
            // the exact failure the trap exists to prevent.
            if (UIScreenRegistry.TrapRejects(target.gameObject))
            {
                Transform inside = UIScreenRegistry.TrapFocusTarget();
                if (inside == null)
                {
                    Debug.LogError("[LethalAccess] Focus is trapped to '" + UIScreenRegistry.TrappedScreenId +
                                   "' but that screen has no visible entry, so the seed of '" + target.name +
                                   "' was dropped and the keyboard has nowhere to go.");
                    return;
                }

                Debug.Log("[LethalAccess] Seed of '" + target.name + "' redirected into trapped screen '" +
                          UIScreenRegistry.TrappedScreenId + "'.");
                target = inside;
            }

            EventSystem es = EventSystem.current;
            if (es == null)
            {
                Debug.LogError("[LethalAccess] FocusManager.Seed('" + target.name +
                               "'): there is no active EventSystem, so focus cannot be set.");
                return;
            }

            ClearLastAnnounced();
            es.SetSelectedGameObject(target.gameObject);

            // A successful seed restarts the watchdog's clock; the selection it just made is not
            // visible to Tick until the next frame.
            nullSince = -1f;
            warnedNoRecoveryTarget = false;
        }

        public static void Seed(GameObject go)
        {
            Seed(go == null ? null : go.transform);
        }

        /// <summary>
        /// Forget what was last announced, so re-selecting the same GameObject speaks again.
        /// Without this, every focus wipe followed by a return to the same control is silent, and
        /// a value change can never re-announce its control.
        /// </summary>
        public static void ClearLastAnnounced()
        {
            UIAccessibilityManager mgr = UIAccessibilityManager.Instance;
            if (mgr == null)
            {
                // Nothing has announced anything yet; there is no memo to clear.
                return;
            }

            if (!announcerProbed)
            {
                ProbeAnnouncer();
            }

            if (clearViaMethod != null)
            {
                clearViaMethod(mgr);
                return;
            }
            if (lastAnnouncedField != null)
            {
                lastAnnouncedField.SetValue(mgr, null);
                return;
            }
            // ProbeAnnouncer already logged the error once; saying it every frame would bury it.
        }

        // UIAccessibilityManager's "already announced this object" memo is private and lives in a
        // file this class does not own. Reach it once, preferring a public method if one is added
        // later, and say so loudly if neither exists rather than silently going quiet.
        private static bool announcerProbed;
        private static Action<UIAccessibilityManager> clearViaMethod;
        private static FieldInfo lastAnnouncedField;

        private static void ProbeAnnouncer()
        {
            announcerProbed = true;

            MethodInfo m = typeof(UIAccessibilityManager).GetMethod(
                "ClearLastAnnounced",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);

            if (m != null)
            {
                clearViaMethod = (Action<UIAccessibilityManager>)Delegate.CreateDelegate(
                    typeof(Action<UIAccessibilityManager>), m);
                return;
            }

            lastAnnouncedField = typeof(UIAccessibilityManager).GetField(
                "lastAnnouncedObject", BindingFlags.NonPublic | BindingFlags.Instance);

            if (lastAnnouncedField == null)
            {
                Debug.LogError("[LethalAccess] FocusManager cannot reach UIAccessibilityManager's last-announced " +
                               "memo: neither a public ClearLastAnnounced() nor a private 'lastAnnouncedObject' " +
                               "field exists. Re-focusing the same control will be silent, and value changes will " +
                               "not re-announce. Restore one of the two.");
            }
        }

        /// <summary>
        /// Called by UIScreenRegistry.ScreenShown. Resets the watchdog so a panel transition does
        /// not read as focus loss.
        /// </summary>
        public static void NoteScreenVisible(string screenId)
        {
            nullSince = -1f;
            lastRecoveryTime = -1f;
            warnedNoRecoveryTarget = false;
        }

        /// <summary>
        /// The focus-loss watchdog. MUST be called every frame from
        /// UIAccessibilityManager.Update - it is the only recovery path there is.
        ///
        /// Only ever acts while a registered screen is visible, so it can never steal focus
        /// during gameplay.
        /// </summary>
        public static void Tick()
        {
            EventSystem es = EventSystem.current;
            if (es == null)
            {
                return;
            }

            // A destroyed selection already compares equal to null here, which is exactly the
            // case ("Set to defaults" destroyed the focused row) we most need to catch.
            if (es.currentSelectedGameObject != null)
            {
                nullSince = -1f;
                warnedNoRecoveryTarget = false;
                return;
            }

            // Menus frequently run at timeScale 0 (the pause menu does), where Time.time stops
            // advancing and every timer built on it silently never fires.
            float now = Time.unscaledTime;

            if (nullSince < 0f)
            {
                nullSince = now;
                return;
            }
            if (now - nullSince < NullSelectionGraceSeconds)
            {
                return;
            }

            string screenId = UIScreenRegistry.ActiveScreenId;
            if (string.IsNullOrEmpty(screenId))
            {
                // No registered screen is up: this is gameplay, and nothing should be focused.
                return;
            }

            if (lastRecoveryTime >= 0f && now - lastRecoveryTime < RecoveryCooldownSeconds)
            {
                return;
            }
            lastRecoveryTime = now;

            Transform target = UIScreenRegistry.ResolveFocusTarget(screenId);
            if (target == null)
            {
                if (!warnedNoRecoveryTarget)
                {
                    warnedNoRecoveryTarget = true;
                    Debug.LogWarning("[LethalAccess] Focus was lost on screen '" + screenId +
                                     "' and no visible entry could be found to recover to. The keyboard is dead " +
                                     "on this screen until something else takes focus.");
                }
                return;
            }

            // Seed re-announces the control by itself (it clears the last-announced memo), so
            // recovery does not need - and must not add - an utterance of its own.
            Seed(target);
        }
    }
}
