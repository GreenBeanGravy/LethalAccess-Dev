using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

namespace LethalAccess
{
    /// <summary>
    /// The announcer: it watches what the EventSystem has focused and says what it is.
    ///
    /// Three sources of truth feed it, most specific first:
    ///   1. Instance registrations (<see cref="RegisterObject"/>): keyed on GameObject instance
    ///      id, so runtime clones that all share one hierarchy path can each describe themselves.
    ///   2. <see cref="UIScreenRegistry"/>, the authored screen model: label, type, live state
    ///      and the "n of m" position within a named section.
    ///   3. The legacy path map (<see cref="RegisterElement"/> / <see cref="CreateGroup"/>) plus
    ///      component sniffing, for everything nothing has claimed yet.
    /// </summary>
    public class UIAccessibilityManager : MonoBehaviour
    {
        // Singleton pattern
        public static UIAccessibilityManager Instance { get; private set; }

        // UI element tracking
        private List<GameObject> previouslyFocusedUIElements = new List<GameObject>(10);

        // FocusManager reaches this field by name (by reflection) when the public
        // ClearLastAnnounced() below is unavailable. Do not rename either without updating
        // FocusManager.ProbeAnnouncer.
        private GameObject lastAnnouncedObject;

        // Set by a history jump, consumed by the NEXT observed selection change. It cannot be
        // cleared synchronously: the selection change it refers to is only observed on a later
        // Update, which is exactly why the old bool (set and cleared inside one call) never
        // suppressed anything and Tab ping-ponged between two controls forever.
        //
        // FocusManager.Seed normally clears lastAnnouncedObject, which already prevents the push;
        // this flag is what keeps the behaviour correct when it cannot; Seed reaches that memo
        // reflectively and logs an error if the field is ever renamed, and the degraded path must
        // not silently bring the ping-pong back.
        private bool suppressNextHistoryPush;

        // UI element organization
        private Dictionary<string, UIElementInfo> registeredElements = new Dictionary<string, UIElementInfo>();
        private Dictionary<string, UIGroupInfo> uiGroups = new Dictionary<string, UIGroupInfo>();

        // Instance-keyed registration. One dictionary key per CONTROL rather than per path,
        // which is the only way 40 keybind rows sharing
        // ".../RemapKeysContainer/RemapKeySlot(Clone)/Button" (and 20 lobby rows, 15 belt-bag
        // slots, 4 player-list slots, dropdown option clones) can describe themselves.
        private readonly Dictionary<int, UIElementInfo> instanceElements = new Dictionary<int, UIElementInfo>();

        // screenId -> instance ids, so ClearScreen does not have to walk the whole map.
        private readonly Dictionary<string, List<int>> instancesByScreen = new Dictionary<string, List<int>>();

        // Custom text providers (migrated from original implementation)
        private Dictionary<string, List<Func<string>>> customTextProviders = new Dictionary<string, List<Func<string>>>();

        // Custom navigation connections
        private Dictionary<string, Dictionary<string, string>> customUINavigation = new Dictionary<string, Dictionary<string, string>>();

        // One log line per offender, not one per focus change: a declared-type mismatch and an
        // unclaimed control are both permanent conditions, so repeating them every time the user
        // arrows past would bury everything else in the log.
        private readonly HashSet<string> reportedTypeMismatches = new HashSet<string>();
        private readonly HashSet<string> reportedUnclaimed = new HashSet<string>();

        // Reference to logger
        private BepInEx.Logging.ManualLogSource Logger;

        // Initialize
        public void Initialize(BepInEx.Logging.ManualLogSource logger)
        {
            Instance = this;
            Logger = logger;
            LogInfo("UIAccessibilityManager initialized");

            // Migrate any existing UI speech overrides from the static dictionary
            if (LACore.overriddenTexts != null && LACore.overriddenTexts.Count > 0)
            {
                foreach (var kvp in LACore.overriddenTexts)
                {
                    customTextProviders[kvp.Key] = kvp.Value;
                }
                LogInfo($"Migrated {LACore.overriddenTexts.Count} existing UI speech overrides");
            }
        }

        public void Update()
        {
            // Deliberately ahead of the EnableUIAnnouncements gate. Tick() is the mod's ONLY
            // focus-loss recovery: Unity has none of its own (EventSystem.m_FirstSelected is
            // (0,0) in every scene in this game). Turning announcements off must not also leave
            // the keyboard dead when a panel destroys the focused control.
            FocusManager.Tick();

            HandleEscapeAsBack();

            // UI/EnableUIAnnouncements was bound in config and listed in the settings menu but
            // never read by anything; toggling it off had no effect.
            if (!ConfigManager.EnableUIAnnouncements.Value)
            {
                return;
            }

            // Must run BEFORE the announcement pass: when the window closes this clears the
            // "already said that" memo, and HandleUIElementAnnouncement then composes the
            // announcement from the control's live state in the SAME frame, so the value spoken is
            // the value the control holds now and never one from an earlier step.
            PumpCoalescedReAnnounce();

            // Check for UI element changes
            HandleUIElementAnnouncement();
        }

        #region Coalesced state re-announcement (3.2)

        // Holding an arrow on a slider used to queue one full announcement per value step. Each
        // step calls IngamePlayerSettings.SetOption, which re-announced the focused control, and
        // an announcement is around two seconds of speech against a repeat rate of about ten per
        // second. SpeechQueue deliberately drops nothing, so the backlog grew without bound and
        // the screen reader ended up minutes behind, describing values the user had passed long
        // ago.
        //
        // What this does is COALESCE, not drop: repeated state re-announcements of ONE control
        // collapse into a single pending request, and the request is emitted with whatever value
        // the control holds at emission time. The final request of a held arrow is never
        // discarded (it is emitted as soon as the window closes), so the value the user lands on
        // is always the value they hear. No distinct utterance is lost anywhere in here.

        /// <summary>
        /// Minimum gap between two coalesced re-announcements of the same control. Short enough
        /// that a single arrow press still feels immediate (the first press of a run is emitted on
        /// the very next frame, because the window has long since expired), long enough that a
        /// held arrow cannot outrun speech.
        /// </summary>
        private const float ReAnnounceCoalesceSeconds = 0.4f;

        private GameObject pendingReAnnounceTarget;

        // The window belongs to a CONTROL, not to the manager. Without the owner, arriving on a
        // second slider within 0.4 s of the last step of the first one would hold that slider's
        // first press back for the remainder of a window it had nothing to do with.
        private GameObject coalesceWindowOwner;
        private float lastCoalescedReAnnounceAt;

        /// <summary>
        /// Instance id of the control whose next announcement should SUPERSEDE its own still-queued
        /// lines, or 0 for an ordinary announcement. Not a bool and not a global barge-in: flushing
        /// the whole queue here destroyed unrelated speech raised while a slider was moving.
        /// </summary>
        private int supersedeTagForNextAnnouncement;

        /// <summary>
        /// Rule 3: say the focused control again, with its new value.
        ///
        /// AnnouncementPolicy.ReAnnounceFocused routes through here, which gives every caller the
        /// coalescing for free and changes nothing for discrete controls.
        ///
        /// Silent when nothing is focused (rule 3 forbids a free-floating sentence) and when an
        /// announcement-suppression scope is open, which matches ReAnnounceFocused exactly.
        /// </summary>
        public static void RequestReAnnounce()
        {
            EventSystem es = EventSystem.current;
            GameObject focused = es != null ? es.currentSelectedGameObject : null;
            if (focused == null)
            {
                return;
            }

            RequestReAnnounce(focused);
        }

        /// <summary>
        /// The same thing for a named control. Continuous controls (anything whose value changes
        /// by repeat or by drag: Slider, Scrollbar) are coalesced; everything else re-announces
        /// immediately, so a dropdown, a sprite-toggle or a mic button still speaks once per
        /// press with no added latency.
        /// </summary>
        public static void RequestReAnnounce(GameObject control)
        {
            if (control == null)
            {
                Debug.LogError("[LethalAccess] RequestReAnnounce was called with a null control, so the state " +
                               "change it reports will not be announced. Pass the control whose value changed.");
                return;
            }

            if (AnnouncementPolicy.Suppressed)
            {
                return;
            }

            UIAccessibilityManager mgr = Instance;
            if (mgr == null || !IsContinuousControl(control))
            {
                FocusManager.ClearLastAnnounced();
                return;
            }

            mgr.QueueCoalescedReAnnounce(control);
        }

        /// <summary>
        /// Record that <paramref name="control"/> wants re-announcing. Later requests replace
        /// earlier ones rather than stacking, which is the whole point: there is only ever one
        /// pending re-announcement, and it is satisfied with live state.
        /// </summary>
        public void QueueCoalescedReAnnounce(GameObject control)
        {
            if (control == null)
            {
                Debug.LogError("[LethalAccess] QueueCoalescedReAnnounce was called with a null control; the " +
                               "state change it reports will not be announced.");
                return;
            }

            pendingReAnnounceTarget = control;
        }

        /// <summary>
        /// Does this control change value continuously? Slider.OnMove and Scrollbar.OnMove both
        /// call Set() on every repeat of a held arrow, and both are dragged with the mouse.
        /// </summary>
        private static bool IsContinuousControl(GameObject control)
        {
            return control.GetComponent<Slider>() != null || control.GetComponent<Scrollbar>() != null;
        }

        private void PumpCoalescedReAnnounce()
        {
            if (pendingReAnnounceTarget == null)
            {
                // Also the destroyed-target case: Unity's == reports a destroyed object as null.
                // Release the reference so a rebuilt panel cannot be described by a dead one.
                pendingReAnnounceTarget = null;
                return;
            }

            EventSystem es = EventSystem.current;
            GameObject selected = es != null ? es.currentSelectedGameObject : null;

            if (selected != pendingReAnnounceTarget)
            {
                // Focus moved with a request still pending. The value the user LANDED ON has not
                // been spoken yet, so dropping this outright loses the one number that mattered:
                // the final position of a slider they nudged and immediately arrowed away from.
                // Emit it, tagged, so it is superseded rather than stacked, and let the new
                // control's own announcement follow it on this same frame.
                EmitPendingValueForDepartedControl(pendingReAnnounceTarget);
                pendingReAnnounceTarget = null;
                coalesceWindowOwner = null;
                return;
            }

            float now = Time.unscaledTime;
            if (coalesceWindowOwner == selected &&
                now - lastCoalescedReAnnounceAt < ReAnnounceCoalesceSeconds)
            {
                // Inside the window. The request STAYS pending (nothing is discarded), so it goes
                // out as soon as the window closes, carrying the value at that moment. This is the
                // only branch that delays anything, and the delay is bounded by the window.
                return;
            }

            pendingReAnnounceTarget = null;
            coalesceWindowOwner = selected;
            lastCoalescedReAnnounceAt = now;

            // Supersede on the way out. Coalescing the REQUESTS alone is not enough: a 0.4 s
            // emission interval still outruns a ~2 s utterance, so a long hold would still leave a
            // queue of superseded numbers to sit through.
            //
            // This used to be a global barge-in (AnnouncementPolicy.Speak(..., interrupt: true)),
            // which was wrong: that flushes the ENTIRE backlog, so an unrelated "Took flashlight."
            // or a health warning raised while the slider was moving was destroyed before it was
            // ever spoken. Tagging with the control's instance id drops only the stale value of
            // THIS control, which is what coalescing actually means.
            supersedeTagForNextAnnouncement = selected.GetInstanceID();

            // The announcement itself is left to HandleUIElementAnnouncement so there is exactly
            // one place that composes an announcement and one format for it.
            lastAnnouncedObject = null;
        }

        #endregion

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
        /// Describe one specific GameObject, not a path.
        ///
        /// <paramref name="index"/> is 1-BASED and <paramref name="count"/> is the size of the run
        /// it belongs to; pass count &lt;= 0 to announce no position at all. <paramref name="text"/>
        /// is read on every focus, so it must return live state (the item in a belt-bag slot, the
        /// key currently bound to a row) rather than a value captured at registration time.
        ///
        /// count &lt;= 0 really does mean SILENT on position, not "fall back to a guess":
        /// <see cref="GetElementIndex"/> stops at the instance registration either way, so none of
        /// the later sources (lobby-row counting, the legacy group map, the sibling heuristic)
        /// can supply one behind your back.
        ///
        /// The one thing that still outranks it is an authored
        /// <see cref="UIScreenRegistry"/> position, which GetElementIndex consults first. That is
        /// the opposite of the precedence GetElementText and GetElementType use, where an instance
        /// registration is the most specific source. Left as-is rather than reordered because
        /// changing it would silently renumber every control that both a descriptor and a driver
        /// claim, and there is no way to check that from here.
        /// TODO: reorder GetElementIndex to match once the screens that register by instance can
        /// be walked in-game and their announced positions compared before and after.
        /// </summary>
        public void RegisterObject(GameObject go, Func<string> text, string type, string screenId, int index, int count)
        {
            if (go == null)
            {
                Debug.LogError("[LethalAccess] RegisterObject called with a null GameObject; nothing was registered.");
                return;
            }

            int id = go.GetInstanceID();

            UIElementInfo info;
            if (!instanceElements.TryGetValue(id, out info))
            {
                info = new UIElementInfo();
                instanceElements[id] = info;
            }
            else if (!string.IsNullOrEmpty(info.ScreenId) && info.ScreenId != screenId)
            {
                // Two drivers describing one control means one of them is silently overwritten,
                // and whichever loses stops announcing its own positions correctly.
                Debug.LogError("[LethalAccess] '" + go.name + "' was registered by screen '" + info.ScreenId +
                               "' and is now being re-registered by '" + screenId + "'. The later one wins.");
                RemoveFromScreenIndex(info.ScreenId, id);
            }

            info.Target = go;
            info.Path = Utilities.GetGameObjectPath(go);
            info.TextProvider = text;
            info.ElementType = string.IsNullOrEmpty(type) ? "Unknown" : type;
            info.ScreenId = screenId;
            info.Index = index;
            info.Count = count;

            if (!string.IsNullOrEmpty(screenId))
            {
                List<int> ids;
                if (!instancesByScreen.TryGetValue(screenId, out ids))
                {
                    ids = new List<int>();
                    instancesByScreen[screenId] = ids;
                }
                if (!ids.Contains(id))
                {
                    ids.Add(id);
                }
            }

            // A declared type that no component backs announces the control as something it
            // is not ("checkbox" on a plain Button). Say so at registration, where the caller can
            // still be pointed at, rather than only at announce time.
            if (!string.IsNullOrEmpty(type) && !DeclaredTypeMatchesComponents(go, type))
            {
                Debug.LogError("[LethalAccess] RegisterObject('" + info.Path + "') declares type '" + type +
                               "' but the object has no matching component. The announcement will use the " +
                               "component-derived type instead.");
            }
        }

        /// <summary>
        /// Forget one instance registration. Safe to call for an object that was never registered.
        /// </summary>
        public void UnregisterObject(GameObject go)
        {
            if (go == null)
            {
                return;
            }

            int id = go.GetInstanceID();
            UIElementInfo info;
            if (!instanceElements.TryGetValue(id, out info))
            {
                return;
            }

            RemoveFromScreenIndex(info.ScreenId, id);
            instanceElements.Remove(id);
        }

        /// <summary>
        /// Drop every instance registration belonging to a screen. Call this when the screen's
        /// controls are destroyed (keybind rows on "Set to defaults", lobby rows on refresh),
        /// otherwise the map keeps dead instance ids, and Unity reuses instance ids, so a stale
        /// entry can eventually be handed to an unrelated new object.
        /// </summary>
        public void ClearScreen(string screenId)
        {
            if (string.IsNullOrEmpty(screenId))
            {
                return;
            }

            List<int> ids;
            if (!instancesByScreen.TryGetValue(screenId, out ids))
            {
                return;
            }

            for (int i = 0; i < ids.Count; i++)
            {
                instanceElements.Remove(ids[i]);
            }
            instancesByScreen.Remove(screenId);
            LogInfo($"Cleared {ids.Count} instance registration(s) for screen '{screenId}'");
        }

        private void RemoveFromScreenIndex(string screenId, int instanceId)
        {
            List<int> ids;
            if (string.IsNullOrEmpty(screenId) || !instancesByScreen.TryGetValue(screenId, out ids))
            {
                return;
            }
            ids.Remove(instanceId);
            if (ids.Count == 0)
            {
                instancesByScreen.Remove(screenId);
            }
        }

        private bool TryGetInstanceInfo(GameObject element, out UIElementInfo info)
        {
            info = null;
            if (element == null)
            {
                return false;
            }

            int id = element.GetInstanceID();
            if (!instanceElements.TryGetValue(id, out info))
            {
                return false;
            }

            // Unity reuses instance ids after a destroy, so a surviving entry whose target has
            // gone is not just useless, it is a wrong answer waiting to be given to a new object.
            if (info.Target == null)
            {
                RemoveFromScreenIndex(info.ScreenId, id);
                instanceElements.Remove(id);
                info = null;
                return false;
            }

            return true;
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
        /// Forget what was last announced, so re-selecting the same GameObject speaks again.
        /// FocusManager prefers this over reflecting at the private field; keep it public.
        /// </summary>
        public void ClearLastAnnounced()
        {
            lastAnnouncedObject = null;
        }

        /// <summary>
        /// Handle announcing the current UI element
        /// </summary>
        public void HandleUIElementAnnouncement()
        {
            EventSystem es = EventSystem.current;
            GameObject selectedObject = es != null ? es.currentSelectedGameObject : null;

            if (selectedObject == null)
            {
                // Without this, coming back to the SAME control after a focus wipe is
                // silent (the announcer still thinks it already said it) and a value change can
                // never re-announce.
                lastAnnouncedObject = null;

                // The selection can only vanish between the pump and here if the EventSystem
                // itself went away mid-frame, but a supersede tag left armed would attach to
                // whatever is announced next, which is not the utterance it was meant to replace.
                supersedeTagForNextAnnouncement = 0;

                // Losing focus entirely ends the current context, so the next control re-states
                // where it is even in OnChange mode. Without this, coming back to a screen after a
                // focus wipe would announce a control with no context at all.
                lastContextPrefix = null;
                return;
            }

            // A screen that is on screen but still rearranging itself mutes its own controls until
            // it settles. Without this the pre-init scene described the gamma panel's Confirm
            // (selected by the game's own SelectUIForGamepad), one frame before the panel it belongs
            // to was replaced. Deliberately does NOT touch lastAnnouncedObject: the control must
            // still be announceable the moment the screen settles.
            if (UIScreenRegistry.IsAwaitingSettle(selectedObject))
            {
                return;
            }

            if (selectedObject == lastAnnouncedObject)
            {
                return;
            }

            // SpeechUtils.NoteFocusChanged was written for exactly this spot and nothing called
            // it, so barge-in never fired on a focus change and everything queued about the
            // control the user just left still had to be spoken before they heard the one they
            // moved to. It compares instance ids itself, so a coalesced re-announcement of the
            // SAME control does not cut off its own utterance; only a genuine move does.
            //
            // Deliberately not called on the null branch above: losing focus entirely is the
            // watchdog's business, and cancelling there would cut off the announcement of the
            // control FocusManager.Tick is about to re-seed.
            SpeechUtils.NoteFocusChanged(selectedObject);

            // The back-trail is maintained here, where selection changes are actually
            // observed, and never inside AnnounceUIElement. An announcer that also mutates
            // history is what made the suppression flag unusable.
            if (suppressNextHistoryPush)
            {
                suppressNextHistoryPush = false;
            }
            else if (lastAnnouncedObject != null)
            {
                PushHistory(lastAnnouncedObject);
            }

            // Set BEFORE announcing: if a text provider or state delegate throws, the exception
            // must surface once and stop, not re-fire on every frame forever because the
            // "already said it" memo was never updated.
            lastAnnouncedObject = selectedObject;

            // Consumed here rather than read inside AnnounceUIElement so that it is cleared even
            // if the announcement throws; a stuck tag would attach to an unrelated later utterance.
            int supersedeTag = supersedeTagForNextAnnouncement;
            supersedeTagForNextAnnouncement = 0;

            AnnounceUIElement(selectedObject, supersedeTag);
        }

        /// <summary>
        /// Escape activates the current screen's Back control, so leaving a menu is one key
        /// instead of a walk to the bottom of it, the Windows convention.
        ///
        /// Menu scenes only: in gameplay the game owns Escape (the pause menu, and
        /// `PlayerControllerB` routing in-game settings to the confirm-changes modal), and
        /// firing as well would back out of two things with one press. In the menu scenes the
        /// game ignores Escape entirely (Assembly-CSharp has no ICancelHandler at all), so
        /// claiming it collides with nothing.
        /// </summary>
        private void HandleEscapeAsBack()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null || !kb.escapeKey.wasPressedThisFrame)
            {
                return;
            }

            // Gameplay: the game's Escape handling exists precisely there.
            if (StartOfRound.Instance != null)
            {
                return;
            }

            EventSystem es = EventSystem.current;
            GameObject current = es != null ? es.currentSelectedGameObject : null;

            // Unclaimed selection covers the states Escape must not touch that live outside the
            // registry: an expanded dropdown's option list, and any panel the mod does not model.
            if (current == null || !UIScreenRegistry.ClaimsSelectable(current))
            {
                return;
            }

            // The same press may already have closed that list: TMP_Dropdown handles Cancel
            // first and selects the dropdown again, so focus looks claimed. Its list stays alive
            // (IsExpanded) through the fade-out, which is what tells this press apart.
            TMP_Dropdown dropdown = current.GetComponent<TMP_Dropdown>();
            if (dropdown != null && dropdown.IsExpanded)
            {
                return;
            }

            // A focused text field owns Escape: TMP cancels the edit and restores the original
            // text. Leaving the field is what that press means; leaving the screen is the next one.
            TMP_InputField field = current.GetComponent<TMP_InputField>();
            if (field != null && field.isFocused)
            {
                return;
            }

            // Mid-rebind, Escape belongs to the rebind operation.
            IngamePlayerSettings settings = IngamePlayerSettings.Instance;
            if (settings != null && settings.rebindingOperation != null &&
                settings.rebindingOperation.started)
            {
                return;
            }

            // The screen may have something smaller to close first (the keybinds screen's open
            // category). If it used the press, leaving the screen is the NEXT Escape.
            if (UIScreenRegistry.TryHandleEscape(UIScreenRegistry.ActiveScreenId))
            {
                return;
            }

            Transform back = UIScreenRegistry.FindBackControl(UIScreenRegistry.ActiveScreenId);
            if (back == null)
            {
                // A screen with no Back control: modals with their own dismiss wording, the main
                // menu itself. Saying nothing would read as a dead key; explain instead.
                AnnouncementPolicy.Speak("No back control on this screen.");
                return;
            }

            ExecuteEvents.Execute(back.gameObject,
                new BaseEventData(es), ExecuteEvents.submitHandler);
        }

        private void PushHistory(GameObject go)
        {
            if (previouslyFocusedUIElements.Count > 0 &&
                previouslyFocusedUIElements[previouslyFocusedUIElements.Count - 1] == go)
            {
                return;
            }

            previouslyFocusedUIElements.Add(go);
            if (previouslyFocusedUIElements.Count > 10)
            {
                previouslyFocusedUIElements.RemoveAt(0);
            }
        }

        /// <summary>
        /// The Tab key.
        ///
        /// A shim kept for its name only: LACore registers Key.Tab under "FocusPreviousUIElement",
        /// which dispatches here. Renaming that registration would silently drop whatever key the
        /// user had rebound it to, so the name outlives the behaviour it described.
        ///
        /// See <see cref="HandleTabNavigation"/> for what the key actually does.
        /// </summary>
        public void NavigateToPreviousElement()
        {
            HandleTabNavigation();
        }

        /// <summary>
        /// Tab moves between CONTROLS, Ctrl+Tab between SECTIONS, the Windows convention a
        /// screen-reader user already has, rather than one this mod invented.
        ///
        /// Tab used to jump by section and Ctrl+Tab walked the focus history. The history walk
        /// survives as the fallback on screens the registry does not claim, where there is no
        /// section model to move through and Tab has nothing better to do.
        ///
        /// Arrow keys are unchanged: up/down still move between controls, and left/right still
        /// reach the focused control itself, which is what adjusts a slider.
        /// </summary>
        public void HandleTabNavigation()
        {
            Keyboard kb = Keyboard.current;
            bool ctrl = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
            bool shift = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);

            EventSystem es = EventSystem.current;
            GameObject current = es != null ? es.currentSelectedGameObject : null;

            if (current == null || !UIScreenRegistry.ClaimsSelectable(current))
            {
                NavigateToPreviousElementInHistory();
                return;
            }

            if (!ctrl)
            {
                MoveWithinScreen(current, shift ? -1 : 1);
                return;
            }

            if (!ConfigManager.TabMovesBySection.Value)
            {
                NavigateToPreviousElementInHistory();
                return;
            }

            Transform target = UIScreenRegistry.NextSection(current, shift ? -1 : 1);
            if (target == null)
            {
                // NextSection returns null when only one section currently has visible entries.
                // Saying so is attributable to the keypress; silence would read as a dead key.
                AnnouncementPolicy.Speak("No other sections on this screen.");
                return;
            }

            // The section name comes from the TARGET, and is spoken before the control: the
            // speech queue is FIFO and Seed makes the announcer speak the control on the next
            // Update, giving "Actions section. Confirm changes, button, 1 of 3."
            int index;
            int count;
            string sectionName;
            bool prefixCarriesIt = ConfigManager.SectionPrefixMode != null &&
                                   !string.Equals(ConfigManager.SectionPrefixMode.Value, "Off",
                                                  StringComparison.OrdinalIgnoreCase);

            if (!prefixCarriesIt &&
                UIScreenRegistry.TryGetPosition(target.gameObject, out index, out count, out sectionName) &&
                !string.IsNullOrEmpty(sectionName))
            {
                // Skipped when announcements already open with their section, because the control
                // itself is about to say "Actions, Confirm changes, button, 1 of 3", a separate
                // "Actions section." in front of that is the same word twice in a row.
                AnnouncementPolicy.Speak(sectionName + " section.");

                // Same two-part shape as rule 4, and the same hazard: the Seed below is a focus
                // change, and the announcer now barges in on focus changes, so without this the
                // section title just queued would be flushed by the second half of the very
                // announcement it belongs to. One-shot and time-bounded on the SpeechUtils side;
                // taken only when a title was actually queued, so a section jump with no name
                // still gets its ordinary barge-in.
                SpeechUtils.PreserveQueuedThroughNextFocusChange();
            }

            // Seed clears the announcer's memo, so the control we are leaving would never reach
            // the back-trail on its own; a section jump is ordinary navigation and Ctrl+Tab
            // should be able to undo it.
            PushHistory(current);
            FocusManager.Seed(target);
        }

        /// <summary>
        /// Tab / Shift+Tab: the next control in the screen's authored order.
        ///
        /// Deliberately identical to what down and up arrow do, including the dead ends: Tab is
        /// an alternative to the arrows, not a second navigation model with its own edges. The
        /// silence at the top and bottom is the same silence the arrows already produce there.
        /// </summary>
        private void MoveWithinScreen(GameObject current, int delta)
        {
            Transform target = UIScreenRegistry.NextInFlatOrder(current, delta);
            if (target == null)
            {
                return;
            }

            // Seed clears the announcer's memo, so without this the control being left never
            // reaches the back-trail and the history walk cannot undo a Tab.
            PushHistory(current);
            FocusManager.Seed(target);
        }

        /// <summary>
        /// Walk back through recently focused controls.
        ///
        /// Loops past candidates that have since been destroyed or hidden instead of consuming
        /// one and giving up, and suppresses the history push the resulting selection change
        /// would otherwise generate; without that, every jump pushed the control it just left
        /// and the key ping-ponged between two elements forever.
        /// </summary>
        public void NavigateToPreviousElementInHistory()
        {
            while (previouslyFocusedUIElements.Count > 0)
            {
                int lastIndex = previouslyFocusedUIElements.Count - 1;
                GameObject candidate = previouslyFocusedUIElements[lastIndex];
                previouslyFocusedUIElements.RemoveAt(lastIndex);

                if (candidate == null || !UIUtils.IsUIElementVisible(candidate))
                {
                    continue;
                }

                suppressNextHistoryPush = true;
                FocusManager.Seed(candidate);
                return;
            }

            AnnouncementPolicy.Speak("No previous element.");
        }

        // Which scenes the path map has already been checked in. Per-scene and not a single
        // process-wide bool: with one flag the only report ever produced was taken in the main
        // menu, where every SettingsPanel / LobbyHostSettings path is legitimately inactive.
        private readonly HashSet<string> validatedScenes = new HashSet<string>();

        /// <summary>
        /// Report every registered hierarchy path that does not resolve to a real
        /// GameObject in the current scene.
        ///
        /// Two changes from the version that produced the "4 stale paths" note:
        ///  * Resolution is inactive-aware (<see cref="UIUtils.FindByPathIncludingInactive(string)"/>).
        ///    The old active-only GameObject.Find reported every path inside SettingsPanel and
        ///    LobbyHostSettings (both of which ship inactive) as broken on every launch, which
        ///    is noise that buries the genuinely dead entries.
        ///  * ABSENT (nothing of that name exists) is an error; RESOLVED-BUT-INACTIVE is normal
        ///    and logged at info level only.
        ///
        /// Per-SCREEN validation is not done here; <see cref="UIScreenRegistry"/> validates each
        /// descriptor's entries the first time that screen is shown, which is the correct seam
        /// for anything with a screen root. This method covers only the legacy flat path map,
        /// whose keys are absolute scene paths with no screen to hang off.
        /// </summary>
        public void ValidateRegisteredPaths()
        {
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (!validatedScenes.Add(sceneName))
            {
                return;
            }

            var absent = new List<string>();
            var inactive = new List<string>();
            var notInThisScene = 0;

            foreach (string path in registeredElements.Keys)
            {
                // If the path's ROOT object is not in this scene at all, the whole hierarchy
                // belongs to a scene that is not loaded (the in-game HUD lives under "Systems",
                // the menu under "Canvas") and nothing can be concluded about the rest of the
                // path. Tested by root existence rather than by a hard-coded scene name so a
                // renamed scene cannot silently turn this into a blanket skip.
                string rootName = path.Split('/')[0];
                if (UIUtils.FindByPathIncludingInactive(rootName) == null)
                {
                    notInThisScene++;
                    continue;
                }

                Transform t = UIUtils.FindByPathIncludingInactive(path);
                if (t == null)
                {
                    absent.Add(path);
                }
                else if (!t.gameObject.activeInHierarchy)
                {
                    inactive.Add(path);
                }
            }

            if (inactive.Count > 0 || notInThisScene > 0)
            {
                LogInfo($"Scene '{sceneName}': {inactive.Count} registered UI path(s) resolved but are inactive " +
                        $"(normal for closed panels), {notInThisScene} belong to a scene that is not loaded.");
            }

            if (absent.Count == 0)
            {
                LogInfo($"All registered UI paths resolved in scene '{sceneName}'.");
                return;
            }

            Debug.LogError(
                $"[LethalAccess] {absent.Count} registered UI path(s) do not exist in scene '{sceneName}'. " +
                "Custom labels, types and position announcements are permanently inactive for these:\n  " +
                string.Join("\n  ", absent.ToArray()));
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
        /// Announce a UI element with all its information.
        ///
        /// <paramref name="supersedeTag"/> is non-zero only on the coalesced re-announcement path
        /// (3.2). It drops this control's own still-queued lines and nothing else: an earlier
        /// value of the same slider is already wrong by the time it would be spoken, whereas an
        /// unrelated "Took flashlight." queued at the same moment must survive.
        /// </summary>
        private void AnnounceUIElement(GameObject element, int supersedeTag)
        {
            // No blanket try/catch here on purpose. Text and state providers are supplied by
            // other parts of the mod; one that throws is a bug we need to see, and swallowing it
            // would leave the user with a control that is silently unlabelled forever.
            string path = Utilities.GetGameObjectPath(element);

            // Type first: it decides how the text is read.
            string elementType = GetElementType(element, path);

            string elementText = GetElementText(element, path, elementType);
            string indexInfo = GetElementIndex(element, path);

            string announcement;
            if (TextAlreadyStatesType(elementText, elementType))
            {
                announcement = $"{elementText}{indexInfo}";
            }
            else
            {
                announcement = $"{elementText}, {elementType}{indexInfo}";
            }

            string prefix = ResolveContextPrefix(element);
            if (!string.IsNullOrEmpty(prefix))
            {
                announcement = prefix + ", " + announcement;
            }

            LogUIElementInfo(element, announcement);

            // When the tree publishes THIS control, the screen reader composes and speaks it from
            // its own vocabulary and the user's own verbosity settings, and speaking here as well
            // would say it twice in two different wordings. Asked per control: the tree only
            // covers registered screens, and anything outside one still needs this path or it is
            // silent. Everything above still runs, so the log line stays comparable between them.
            if (AnnouncementPolicy.UiTreeSpeaksFor(element))
            {
                return;
            }

            // AnnouncementPolicy, not Utilities.SpeakText: it is the only path that honours a
            // suppression scope, and it normalises through SpeechUtils so "60%" is spoken as
            // "60 percent" whichever layer raised it.
            if (supersedeTag != 0)
            {
                AnnouncementPolicy.SpeakSuperseding(announcement, supersedeTag);
            }
            else
            {
                AnnouncementPolicy.Speak(announcement);
            }
        }

        /// <summary>
        /// The context word an announcement opens with: "Audio, Microphone, on, button, 1 of 4"
        /// or "Main Menu, Host, button, 1 of 5".
        ///
        /// Section name on a multi-section screen, screen title otherwise; for elements the screen
        /// registry does not claim, the active screen's title, so a legacy path-registered control
        /// still says where it is.
        ///
        /// ConfigManager.SectionPrefixMode picks between:
        ///   OnChange - the default. Spoken on the first control focused after the context changes,
        ///              then silent until it changes again. Keyed on the CONTEXT changing, never on
        ///              the position within the section: entering Graphics from below lands on
        ///              "Graphics, Indirect lighting, on, button, 3 of 3", not on a bare line
        ///              because it happens not to be entry 1.
        ///   Always   - every announcement carries it
        ///   Off      - no context at all
        /// </summary>
        private string ResolveContextPrefix(GameObject element)
        {
            string mode = ConfigManager.SectionPrefixMode != null
                ? ConfigManager.SectionPrefixMode.Value
                : "OnChange";

            if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                lastContextPrefix = null;
                return null;
            }

            string prefix;
            if (!UIScreenRegistry.TryGetAnnouncementPrefix(element, out prefix))
            {
                prefix = UIScreenRegistry.ActiveScreenTitle();
            }

            if (string.IsNullOrEmpty(prefix))
            {
                // Nothing claims this element and no screen is active. Saying nothing is correct:
                // inventing a context from the hierarchy is how "SectionEntry" got announced as a
                // label in the first place.
                lastContextPrefix = null;
                return null;
            }

            // The screen title was just spoken on its own; repeating it as this control's prefix
            // would be the same words twice in a row. One-shot: the next control still gets it.
            if (!string.IsNullOrEmpty(titleJustAnnounced) &&
                string.Equals(prefix, titleJustAnnounced, StringComparison.OrdinalIgnoreCase))
            {
                titleJustAnnounced = null;
                lastContextPrefix = prefix;
                return null;
            }
            titleJustAnnounced = null;

            if (string.Equals(mode, "OnChange", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(prefix, lastContextPrefix, StringComparison.Ordinal))
                {
                    return null;
                }
                lastContextPrefix = prefix;
                return prefix;
            }

            lastContextPrefix = prefix;
            return prefix;
        }

        /// <summary>
        /// Last context spoken, for SectionPrefixMode = OnChange. Cleared whenever the selection
        /// goes null so that re-entering a screen always re-states where you are.
        /// </summary>
        private string lastContextPrefix;

        /// <summary>
        /// One-shot: a screen title that was just spoken on its own, whose repeat as the next
        /// announcement's prefix would be the same words twice in a row.
        /// </summary>
        private string titleJustAnnounced;

        /// <summary>
        /// Called by <see cref="AnnouncementPolicy.AnnounceScreen"/> right after it speaks a screen
        /// title. On a single-section screen the title IS the prefix, so without this the user
        /// hears "Main Menu." followed immediately by "Main Menu, Host, button, 1 of 5". On a
        /// multi-section screen the two differ ("Settings." then "Audio, Microphone…") and this
        /// suppresses nothing, because the prefix will not match.
        /// </summary>
        public static void NoteScreenTitleAnnounced(string title)
        {
            if (Instance == null || string.IsNullOrEmpty(title))
            {
                return;
            }

            Instance.titleJustAnnounced = title;

            // A screen change ends the previous context, so in OnChange mode the first control on
            // the new screen always states its section. Without this, opening Settings from a screen
            // that happened to leave "Audio" as the last context would announce the first control
            // with no section at all.
            Instance.lastContextPrefix = null;
        }

        /// <summary>
        /// Speak the pending value of a control the user has just arrowed away from.
        ///
        /// Without this the final value of a slider is lost whenever focus moves inside the
        /// coalescing window, i.e. exactly when someone nudges a slider once and immediately
        /// moves on, which is the common case. Tagged, so it still supersedes that control's own
        /// stale lines rather than stacking behind them.
        /// </summary>
        private void EmitPendingValueForDepartedControl(GameObject control)
        {
            if (control == null)
            {
                return;
            }

            AnnounceUIElement(control, control.GetInstanceID());
        }

        /// <summary>
        /// Does the label already say what kind of control this is?
        ///
        /// The old test was `elementText.ToLower().Contains(elementType.ToLower())`, an unanchored
        /// substring match: "text" matched inside "context" and "texture", "tab" inside "table"
        /// and "acceptable", so any label or news paragraph containing one of those silently
        /// dropped the role from the announcement. Match whole words only.
        /// </summary>
        private static bool TextAlreadyStatesType(string elementText, string elementType)
        {
            if (string.IsNullOrEmpty(elementText) || string.IsNullOrEmpty(elementType))
            {
                return false;
            }

            return Regex.IsMatch(elementText, @"\b" + Regex.Escape(elementType) + @"\b",
                                 RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Get the text for a UI element, most specific source first.
        /// </summary>
        private string GetElementText(GameObject element, string path, string elementType)
        {
            // 1. Instance registration: the only source that can tell 40 identical clones
            //    apart. The provider is called fresh, so it reports live state.
            UIElementInfo instanceInfo;
            if (TryGetInstanceInfo(element, out instanceInfo))
            {
                if (instanceInfo.TextProvider != null)
                {
                    string live = instanceInfo.TextProvider();
                    if (!string.IsNullOrEmpty(live))
                    {
                        return Utilities.RemoveSpecialCharacters(live).Trim();
                    }
                }

                if (!string.IsNullOrEmpty(instanceInfo.DisplayName))
                {
                    return EnhanceTextWithState(element, instanceInfo.DisplayName, elementType);
                }
            }

            // 2. The authored screen model.
            UIEntry entry;
            if (UIScreenRegistry.TryGetEntry(element, out entry))
            {
                string label = !string.IsNullOrEmpty(entry.Label)
                    ? entry.Label
                    : Utilities.RemoveSpecialCharacters(Utilities.GetTextFromComponent(element)).Trim();

                if (entry.State != null)
                {
                    // An authored state provider is ground truth and beats anything sniffed off
                    // the components, so EnhanceTextWithState is deliberately skipped here.
                    string state = entry.State();
                    if (!string.IsNullOrEmpty(state))
                    {
                        return label + ", " + state.Trim().TrimStart(',').Trim();
                    }
                    return label;
                }

                return EnhanceTextWithState(element, label, elementType);
            }

            // 3. Legacy path-keyed custom providers.
            List<Func<string>> providers;
            if (customTextProviders.TryGetValue(path, out providers))
            {
                string customText = string.Join(" ", providers.Select(p => p()).ToArray());

                // Remove redundant "Slider:" text if it will be followed by ", slider"
                if (elementType == "slider" && customText.Contains("Slider:"))
                {
                    customText = customText.Replace("Slider:", "").Trim();
                }

                if (!string.IsNullOrEmpty(customText))
                {
                    return Utilities.RemoveSpecialCharacters(customText).Trim();
                }
            }

            // 4. Legacy path-keyed display name.
            UIElementInfo info;
            if (registeredElements.TryGetValue(path, out info) && !string.IsNullOrEmpty(info.DisplayName))
            {
                return EnhanceTextWithState(element, info.DisplayName, elementType);
            }

            // 5. A lobby row's data lives on the row root, not on the focused JoinButton,
            //    which is why every server in the list announced the single word "Join".
            LobbySlot slot = element.GetComponentInParent<LobbySlot>();
            if (slot != null)
            {
                string rowLabel = BuildLobbyRowLabel(slot);
                if (!string.IsNullOrEmpty(rowLabel))
                {
                    return rowLabel;
                }
            }

            // 6. Whatever the components say, translated first when it is one of InputUtils' raw
            //    tokens, which its dropdown options are until their first Update.
            string baseText = Utilities.RemoveSpecialCharacters(
                InputUtilsBridge.TranslateToken(Utilities.GetTextFromComponent(element))).Trim();
            return EnhanceTextWithState(element, baseText, elementType);
        }

        /// <summary>
        /// Name and population of one server-list row.
        ///
        /// Only the fallback path. Once ServerListAccessibility has registered the rows they arrive
        /// through RegisterObject with a live provider, and this is not reached.
        ///
        /// UIUtils.BuildLobbyRowLabel(slot, index, total) now does the same job and carries the
        /// position; this copy predates it and omits the position. Folding this into that one would
        /// change what a server row announces, so it is a deliberate follow-up rather than a
        /// drive-by edit.
        /// </summary>
        private static string BuildLobbyRowLabel(LobbySlot slot)
        {
            // Both TMP fields are public and wired in the prefab, but the challenge-moon prefab
            // is a different prefab, so neither is guaranteed. An unguarded read here would throw
            // inside an announcement and take the whole announcer down for that frame.
            string name = slot.LobbyName != null ? slot.LobbyName.text : null;
            string players = slot.playerCount != null ? slot.playerCount.text : null;

            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            string label = Utilities.RemoveSpecialCharacters(name).Trim();
            if (!string.IsNullOrEmpty(players))
            {
                label += ", " + Utilities.RemoveSpecialCharacters(players).Trim() + " players";
            }
            return label;
        }

        /// <summary>
        /// Adds the element's state to its text, based on the element type.
        /// </summary>
        private string EnhanceTextWithState(GameObject element, string baseText, string elementType)
        {
            // If base text is empty or just whitespace, replace with element name
            if (string.IsNullOrWhiteSpace(baseText))
            {
                baseText = element.name.Replace("(Clone)", "").Trim();
            }

            // The sprite-swap settings. Six controls on the settings panel (InvertYAxis,
            // ArachniphobiaMode, HeadBobbing, ToggleSprint, AdvancedLighting, InvertScreenOption)
            // are plain Buttons whose SettingsOption.textElement is null, so their entire state is
            // a swapped Image sprite with nothing readable anywhere. Checked before the type
            // switch because the component is a Button, not a Toggle.
            string settingState = GetSettingsOptionState(element, ref baseText);
            if (settingState != null)
            {
                return $"{baseText}, {settingState}";
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
            }

            return baseText;
        }

        /// <summary>
        /// "on" / "off" for the settings that are Buttons with a swapped sprite, read from the
        /// same field the game itself reads in SettingsOption.ToggleEnabledImage
        /// (SettingsOption.ToggleEnabledImage) rather than guessed from a sprite name or a button colour.
        /// Returns null when this element is not one of them.
        ///
        /// The numeric cases in the game's switch are SettingsOptionType values: MicEnabled 4,
        /// InvertYAxis 11, SpiderSafeMode 12, AdvancedLighting 14, ToggleSprint 17,
        /// HeadBobbing 18, and DontTell 19, which is the InvertScreenOption
        /// (QuickMenuManager.specialOption), whose state is IngamePlayerSettings.flipCamera and
        /// NOT part of unsavedSettings.
        /// </summary>
        private static string GetSettingsOptionState(GameObject element, ref string baseText)
        {
            bool on;
            if (!TryGetSettingsToggle(element, out on))
            {
                return null;
            }

            // The mic toggle is the one control in this family whose textElement is wired, and
            // the game writes the literal "ENABLED"/"DISABLED" into it (SettingsOption.ToggleEnabledImage).
            // Scraped as a label that is all the user hears ("ENABLED" with no idea what is
            // enabled), and appending ", on" to it would only make it "ENABLED, on".
            if (!string.IsNullOrEmpty(baseText))
            {
                string trimmed = baseText.Trim();
                if (string.Equals(trimmed, "ENABLED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(trimmed, "DISABLED", StringComparison.OrdinalIgnoreCase))
                {
                    baseText = SplitCamelCase(element.name.Replace("(Clone)", "").Trim());
                }
            }

            return on ? "on" : "off";
        }

        /// <summary>
        /// Whether one of the sprite-swap settings is on, read from the same field the game reads
        /// in SettingsOption.ToggleEnabledImage rather than guessed from a sprite name or a button
        /// colour. False when this object is not one of them.
        ///
        /// Public and shared because the accessibility tree needs the same answer as a boolean,
        /// to publish as a toggle state. Two copies of this switch would silently disagree the
        /// first time the game adds an option type.
        /// </summary>
        public static bool TryGetSettingsToggle(GameObject element, out bool on)
        {
            on = false;

            SettingsOption option = element.GetComponent<SettingsOption>();
            if (option == null && element.transform.parent != null)
            {
                // The gamma/volume style controls put the SettingsOption on the wrapper and the
                // Slider on the child. One level only; a wider search would attach an unrelated
                // ancestor's setting to a nested button.
                option = element.transform.parent.GetComponent<SettingsOption>();
            }

            if (option == null)
            {
                return false;
            }

            IngamePlayerSettings settings = IngamePlayerSettings.Instance;
            if (settings == null || settings.unsavedSettings == null)
            {
                // Only reachable before IngamePlayerSettings has initialised, i.e. before any of
                // these controls can be on screen; report nothing rather than guess a state.
                return false;
            }

            switch (option.optionType)
            {
                case SettingsOptionType.MicEnabled:
                    on = settings.unsavedSettings.micEnabled;
                    break;
                case SettingsOptionType.InvertYAxis:
                    on = settings.unsavedSettings.invertYAxis;
                    break;
                case SettingsOptionType.SpiderSafeMode:
                    on = settings.unsavedSettings.spiderSafeMode;
                    break;
                case SettingsOptionType.AdvancedLighting:
                    on = settings.unsavedSettings.advancedLightMode;
                    break;
                case SettingsOptionType.ToggleSprint:
                    on = settings.unsavedSettings.toggleSprint;
                    break;
                case SettingsOptionType.HeadBobbing:
                    on = settings.unsavedSettings.headBobbing;
                    break;
                case SettingsOptionType.DontTell:
                    on = settings.flipCamera;
                    break;
                default:
                    // Sliders, dropdowns, rebind rows and the mic-device button are also
                    // SettingsOptions; none of them has an on/off state.
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The type of a UI element.
        ///
        /// Components are sniffed FIRST and a registered type string is only ever a label
        /// override on top of that. Registering "checkbox" or "toggle" on an object that is a
        /// plain Button used to make the announcer state a control type the object does not have,
        /// and then EnhanceTextWithState looked for a Toggle component, found none, and announced
        /// no state either.
        /// </summary>
        private string GetElementType(GameObject element, string path)
        {
            string sniffed = SniffElementType(element);

            // Most specific declaration wins, same order as GetElementText.
            string declared = null;
            string source = null;

            UIElementInfo instanceInfo;
            if (TryGetInstanceInfo(element, out instanceInfo) && instanceInfo.ElementType != "Unknown")
            {
                declared = instanceInfo.ElementType;
                source = "instance registration";
            }

            if (declared == null)
            {
                UIEntry entry;
                if (UIScreenRegistry.TryGetEntry(element, out entry) && !string.IsNullOrEmpty(entry.Type))
                {
                    declared = entry.Type;
                    source = "screen descriptor";
                }
            }

            if (declared == null)
            {
                UIElementInfo info;
                if (registeredElements.TryGetValue(path, out info) && info.ElementType != "Unknown")
                {
                    declared = info.ElementType;
                    source = "path registration";
                }
            }

            if (declared == null)
            {
                return sniffed;
            }

            if (DeclaredTypeMatchesComponents(element, declared))
            {
                return declared;
            }

            if (reportedTypeMismatches.Add(path))
            {
                Debug.LogError("[LethalAccess] '" + path + "' is declared as type '" + declared + "' by its " +
                               source + ", but the object has no matching component. Announcing it as '" +
                               sniffed + "' instead.");
            }
            return sniffed;
        }

        /// <summary>
        /// What the components say this control is.
        /// </summary>
        private static string SniffElementType(GameObject element)
        {
            if (element.GetComponent<Button>() != null)
                return "button";

            Toggle toggle = element.GetComponent<Toggle>();
            if (toggle != null)
            {
                // Some games distinguish checkboxes from other toggle controls
                if (toggle.graphic != null)
                {
                    string graphicName = toggle.graphic.name.ToLower();
                    if (graphicName.Contains("check") || graphicName.Contains("box"))
                        return "checkbox";

                    if (toggle.transform.name.ToLower().Contains("check") ||
                        (toggle.transform.parent != null && toggle.transform.parent.name.ToLower().Contains("check")))
                        return "checkbox";
                }
                return "toggle";
            }

            if (element.GetComponent<Slider>() != null)
                return "slider";

            // Without this a focused scrollbar announced "Handle, ui element"; the type had no
            // branch at all and the text fell through to a child object's name.
            if (element.GetComponent<Scrollbar>() != null)
                return "scroll bar";

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
        /// Does a declared type name describe a component this object actually has?
        ///
        /// Deliberately the same rule UIScreenRegistry.ValidateResolvedEntry applies: names that
        /// claim an interactive component must be backed by it; free-form names ("heading",
        /// "row", "section") are labels, not claims, and are accepted as authored.
        /// </summary>
        private static bool DeclaredTypeMatchesComponents(GameObject go, string declaredType)
        {
            switch (declaredType.ToLowerInvariant())
            {
                case "button":
                    return go.GetComponent<Button>() != null;
                case "slider":
                    return go.GetComponent<Slider>() != null;
                case "toggle":
                case "checkbox":
                    return go.GetComponent<Toggle>() != null;
                case "dropdown":
                    return go.GetComponent<Dropdown>() != null || go.GetComponent<TMP_Dropdown>() != null;
                case "input field":
                    return go.GetComponent<InputField>() != null || go.GetComponent<TMP_InputField>() != null;
                case "scroll area":
                    return go.GetComponent<ScrollRect>() != null;
                case "scroll bar":
                    return go.GetComponent<Scrollbar>() != null;
                default:
                    return true;
            }
        }

        /// <summary>
        /// The "n of m" suffix, from the authored order wherever there is one.
        ///
        /// The sibling heuristic below survives only as the fallback for controls nothing has
        /// claimed, and it is why a standalone Confirm button never had an index: for
        /// elementType == "button" the scan is pinned to typeof(Button) under the IMMEDIATE
        /// parent, needs at least two matches, and the name-similarity fallback was gated on
        /// targetType == null so it could never run for a button.
        /// </summary>
        private string GetElementIndex(GameObject element, string path)
        {
            // 1. The authored screen model. Index is 1-based and scoped to the entry's section,
            //    counting only entries that are currently visible.
            int index;
            int count;
            string sectionName;
            if (UIScreenRegistry.TryGetPosition(element, out index, out count, out sectionName))
            {
                return $", {index} of {count}";
            }

            // 2. Instance registration, and it is TERMINAL (8.2). RegisterObject documents
            //    count <= 0 as "announce no position at all", and a driver relies on that. It only
            //    holds if this returns here: every later step, and the sibling heuristic in
            //    particular, will happily invent a position, so falling through turned a
            //    deliberate "no position" into a wrong one ("Refresh, button, 2 of 5" on a control
            //    that is not in a run of five).
            UIElementInfo instanceInfo;
            if (TryGetInstanceInfo(element, out instanceInfo))
            {
                if (instanceInfo.Count > 0)
                {
                    return $", {instanceInfo.Index} of {instanceInfo.Count}";
                }
                return "";
            }

            // 3. The meaningful ordinal for a lobby row is its position under Content, one
            //    level above the JoinButton whose only Button sibling is the inactive HostName.
            //    Counted over ROWS rather than over Content.childCount: Content also holds
            //    non-row children (serverListBlankText sits at ".../Content/ListHeader (1)"), so a
            //    raw sibling index would both offset the position and overstate the total.
            LobbySlot slot = element.GetComponentInParent<LobbySlot>();
            if (slot != null && slot.transform.parent != null)
            {
                Transform content = slot.transform.parent;
                int rowTotal = 0;
                int rowIndex = 0;
                for (int i = 0; i < content.childCount; i++)
                {
                    Transform child = content.GetChild(i);
                    if (child.GetComponent<LobbySlot>() == null)
                    {
                        continue;
                    }
                    rowTotal++;
                    if (child == slot.transform)
                    {
                        rowIndex = rowTotal;
                    }
                }

                if (rowIndex > 0)
                {
                    return $", {rowIndex} of {rowTotal}";
                }
            }

            // 4. Legacy path-keyed group.
            UIElementInfo info;
            if (registeredElements.TryGetValue(path, out info) && !string.IsNullOrEmpty(info.GroupName))
            {
                UIGroupInfo group;
                if (uiGroups.TryGetValue(info.GroupName, out group))
                {
                    return $", {info.GroupIndex + 1} of {group.ElementPaths.Count}";
                }
            }

            if (reportedUnclaimed.Add(path))
            {
                LogDebug($"No authored position for '{path}'; falling back to the sibling heuristic. " +
                         "Add it to a UIScreenDescriptor or register it by instance to fix the coverage gap.");
            }

            return GetElementIndexBySiblingHeuristic(element, path);
        }

        /// <summary>
        /// Legacy fallback: guess a position from same-typed or similarly-named siblings under the
        /// immediate parent. Wrong often enough that it is now last, but better than silence on a
        /// screen nothing has described yet.
        /// </summary>
        private string GetElementIndexBySiblingHeuristic(GameObject element, string path)
        {
            Transform parent = element.transform.parent;
            if (parent == null || parent.childCount <= 1)
            {
                return "";
            }

            string elementType = GetElementType(element, path);

            Type targetType = null;
            switch (elementType)
            {
                case "button": targetType = typeof(Button); break;
                case "slider": targetType = typeof(Slider); break;
                case "toggle":
                case "checkbox": targetType = typeof(Toggle); break;
                case "scroll bar": targetType = typeof(Scrollbar); break;
                case "dropdown":
                    // Two component types mean one type filter cannot express it.
                    List<Transform> dropdownSiblings = new List<Transform>();
                    for (int i = 0; i < parent.childCount; i++)
                    {
                        Transform child = parent.GetChild(i);
                        if (child.gameObject.activeInHierarchy &&
                            (child.GetComponent<Dropdown>() != null || child.GetComponent<TMP_Dropdown>() != null))
                        {
                            dropdownSiblings.Add(child);
                        }
                    }

                    if (dropdownSiblings.Count > 1)
                    {
                        int dropdownIndex = dropdownSiblings.IndexOf(element.transform);
                        if (dropdownIndex >= 0)
                        {
                            return $", {dropdownIndex + 1} of {dropdownSiblings.Count}";
                        }
                    }
                    break;
            }

            if (targetType != null)
            {
                List<Transform> siblingsOfSameType = new List<Transform>();
                for (int i = 0; i < parent.childCount; i++)
                {
                    Transform child = parent.GetChild(i);
                    if (child.gameObject.activeInHierarchy && child.GetComponent(targetType) != null)
                    {
                        siblingsOfSameType.Add(child);
                    }
                }

                if (siblingsOfSameType.Count > 1)
                {
                    int typeIndex = siblingsOfSameType.IndexOf(element.transform);
                    if (typeIndex >= 0)
                    {
                        return $", {typeIndex + 1} of {siblingsOfSameType.Count}";
                    }
                }
            }

            // Reached whenever the type scan produced nothing, not only when there was no type to
            // scan for. Gating this on `targetType == null` made it dead code for every button,
            // slider, toggle and dropdown in the game.
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
                    int nameIndex = similarlyNamedSiblings.IndexOf(element.transform);
                    if (nameIndex >= 0)
                    {
                        return $", {nameIndex + 1} of {similarlyNamedSiblings.Count}";
                    }
                }
            }

            return "";
        }

        /// <summary>
        /// Get the base element name without numbers (for grouping related elements)
        /// </summary>
        private string GetBaseElementName(string fullName)
        {
            // Remove clone suffix
            string name = fullName.Replace("(Clone)", "").Trim();

            // Remove trailing numbers and spaces
            return Regex.Replace(name, @"\s*\(\d+\)$|\s*\d+$", "");
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
                name = SplitCamelCase(name);

                // Remove trailing numbers (e.g., "Button (1)" becomes "Button")
                name = Regex.Replace(name, @"\s*\(\d+\)$", "");

                return name;
            }
            return path;
        }

        private static string SplitCamelCase(string name)
        {
            return Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
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

        /// <summary>
        /// Coverage gaps, not failures: loud enough to find when looking for them, quiet enough
        /// not to drown the log during normal play.
        /// </summary>
        private void LogDebug(string message)
        {
            if (Logger != null)
            {
                Logger.LogDebug($"[UIAccessibility] {message}");
            }
        }

        #endregion

        #region Helper Classes

        /// <summary>
        /// Information about a registered UI element. One instance describes either a path
        /// registration or an instance registration; the fields the other kind does not use stay
        /// at their defaults.
        /// </summary>
        private class UIElementInfo
        {
            public string Path;
            public string DisplayName;
            public string ElementType = "Unknown";
            public string GroupName;
            public int GroupIndex;

            // Instance registrations only.
            public GameObject Target;
            public Func<string> TextProvider;
            public string ScreenId;
            public int Index;
            public int Count;
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
