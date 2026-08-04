using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityA11y.Core;

namespace LethalAccess
{
    /// <summary>
    /// Publishes <see cref="UIScreenRegistry"/> as an accessibility tree, so a screen
    /// reader speaks the mod's authored screens with its own vocabulary, verbosity and
    /// braille rather than hearing sentences this mod composed.
    ///
    /// The division of labour, which is the whole reason this class is small:
    ///   - the SCREEN MODEL (what the controls are, their order, their sections, their
    ///     live state) already exists and does not change here;
    ///   - the WORDING stops being ours. Roles, states, "n of m", which parts to suppress
    ///     as redundant - all of that becomes the screen reader's job;
    ///   - NAVIGATION also does not change. The Selectable finder patches still own the
    ///     arrow keys, and this class only reports where focus already is.
    ///
    /// So the mod keeps the two things it got right and stops guessing at the one thing
    /// a screen reader is authoritative about.
    /// </summary>
    public sealed class LethalAccessSource : IUiSource
    {
        // Reserved by A11yRuntime for the tree root and the live-region announcer.
        private const ulong RootId = 1;
        private const ulong AnnouncerId = 2;

        // One Dialog node per screen, keyed by screen id so it keeps the same identity while the
        // user moves around inside a screen and takes a NEW one when the screen changes. That
        // identity change is the whole mechanism: a screen reader announces an ancestor it has
        // newly entered, and re-reads nothing whose identity held still.
        private const ulong FirstDialogId = 4;
        private const ulong MaxDialogId = 255;

        // Group nodes. Kept well clear of the reserved and dialog ids, and of StableId's output,
        // which is biased above 1024.
        private const ulong FirstGroupId = 256;
        private const ulong MaxGroupId = 1023;

        private readonly Dictionary<string, ulong> _dialogIdByScreen = new Dictionary<string, ulong>();
        private ulong _nextDialogId = FirstDialogId;

        private readonly List<UIScreenRegistry.UIEntryView> _views =
            new List<UIScreenRegistry.UIEntryView>();
        private readonly List<ulong> _focusRing = new List<ulong>();
        private readonly Dictionary<ulong, Transform> _targetById = new Dictionary<ulong, Transform>();
        private readonly Dictionary<ulong, UIScreenRegistry.UIEntryView> _viewById =
            new Dictionary<ulong, UIScreenRegistry.UIEntryView>();

        private string _screenId;
        private string _structureKey = "";
        private bool _dirty = true;

        public string Name { get { return "LethalAccess screens"; } }

        /// <summary>Above the generic uGUI scanner, which knows nothing of the authored order.</summary>
        public int Priority { get { return 100; } }

        /// <summary>
        /// Only while the registry actually claims a screen. Everything else - gameplay,
        /// unregistered panels - is left to whatever else is registered, or to nothing.
        /// </summary>
        public bool IsActive
        {
            get { return !string.IsNullOrEmpty(UIScreenRegistry.ActiveScreenId); }
        }

        public IReadOnlyList<ulong> FocusRing { get { return _focusRing; } }

        /// <summary>
        /// Structural changes only. A label or value changing is handled by
        /// TryRefreshNode without rebuilding, which is what keeps a slider drag from
        /// resending the whole tree every frame.
        /// </summary>
        public bool IsDirty
        {
            get
            {
                if (_dirty) return true;
                return ComputeStructureKey() != _structureKey;
            }
        }

        public void Invalidate() { _dirty = true; }

        /// <summary>
        /// Identity of the current structure: which screen, and which controls are
        /// visible in which sections. Deliberately excludes labels and states, because
        /// those changing must NOT trigger a full rebuild.
        /// </summary>
        private string ComputeStructureKey()
        {
            string screenId, title;
            if (!UIScreenRegistry.TryEnumerateActiveScreen(_views, out screenId, out title))
            {
                return "";
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder(screenId, 128);
            for (int i = 0; i < _views.Count; i++)
            {
                sb.Append('|').Append(_views[i].SectionIndex).Append(':');
                Transform t = _views[i].Target;
                sb.Append(t != null ? t.GetInstanceID() : 0);
            }
            return sb.ToString();
        }

        // ---- Tree construction --------------------------------------------------

        public void BuildSnapshot(AccessSnapshot into)
        {
            _dirty = false;
            into.Clear();
            _focusRing.Clear();
            _targetById.Clear();
            _viewById.Clear();

            string screenId, title;
            bool have = UIScreenRegistry.TryEnumerateActiveScreen(_views, out screenId, out title);
            _screenId = screenId;
            _structureKey = ComputeStructureKey();

            // The root is the game window and is named for the application, never for the current
            // screen. A screen reader reads a window's name when the application is switched to,
            // not when focus moves inside it, so a title put here is read once at startup and
            // never again - which is what happened to every screen title the mod published.
            AccessNode root = new AccessNode(RootId, AccessRole.Window);
            root.Label = Application.productName;
            root.Focusable = false;
            into.Add(root);
            into.RootId = RootId;

            if (!have)
            {
                into.FocusId = RootId;
                return;
            }

            // The screen itself, carrying the title. Pane rather than Dialog unless the screen
            // really is one: a screen reader suppresses a pane's role and speaks only its name,
            // which is the announcement a title needs to be, whereas "Main menu, dialog" is both
            // untrue and the reason the word stops meaning anything when a real dialog appears.
            AccessNode screenNode = new AccessNode(
                DialogIdFor(screenId),
                UIScreenRegistry.IsModalScreen(screenId) ? AccessRole.Dialog : AccessRole.Pane);
            screenNode.Label = title;
            screenNode.Focusable = false;
            into.Add(screenNode);
            root.Children.Add(screenNode.Id);

            // A screen with one section gets no group node. Its section name is an
            // internal grouping label ("Main", "Buttons") that means nothing aloud, and
            // an unnamed container makes a screen reader announce a level of nesting
            // that carries no information.
            int liveSections = UIScreenRegistry.VisibleSectionCount(screenId);
            bool useGroups = liveSections > 1;

            ulong nextGroupId = FirstGroupId;
            int currentSection = int.MinValue;
            AccessNode parent = screenNode;

            for (int i = 0; i < _views.Count; i++)
            {
                UIScreenRegistry.UIEntryView v = _views[i];
                if (v.Target == null) continue;

                if (useGroups && v.SectionIndex != currentSection)
                {
                    currentSection = v.SectionIndex;
                    if (nextGroupId > MaxGroupId)
                    {
                        Debug.LogError("[LethalAccess] Screen '" + screenId + "' has more sections than the " +
                                       "reserved group-id range allows; the remainder are parented to the screen " +
                                       "and will announce without their section name.");
                        parent = screenNode;
                    }
                    else
                    {
                        AccessNode group = new AccessNode(nextGroupId++, AccessRole.Group);
                        group.Label = v.SectionName;
                        group.Focusable = false;
                        group.SizeOfSet = v.SectionCount;
                        into.Add(group);
                        screenNode.Children.Add(group.Id);
                        parent = group;
                    }
                }

                ulong id = StableId(v.Target.gameObject);
                if (into.Contains(id))
                {
                    // Two entries resolving to one object would make the tree ambiguous and
                    // the second silently replace the first. Say so rather than hide it.
                    Debug.LogWarning("[LethalAccess] Screen '" + screenId + "' claims '" + v.Target.name +
                                     "' more than once; only the first is published.");
                    continue;
                }

                AccessNode node = new AccessNode(id, AccessRole.Unknown);
                PopulateNode(v, node);
                into.Add(node);
                parent.Children.Add(id);

                _targetById[id] = v.Target;
                _viewById[id] = v;
                if (!node.Disabled) _focusRing.Add(id);
            }

            ulong focus = CurrentFocusId();
            into.FocusId = into.Contains(focus)
                ? focus
                : (_focusRing.Count > 0 ? _focusRing[0] : RootId);
        }

        /// <summary>
        /// A stable id for a screen's Dialog node: same screen, same id for the process lifetime,
        /// so navigating within a screen never re-announces its title and arriving at a different
        /// screen always does.
        /// </summary>
        private ulong DialogIdFor(string screenId)
        {
            ulong id;
            if (_dialogIdByScreen.TryGetValue(screenId, out id)) return id;

            if (_nextDialogId > MaxDialogId)
            {
                // Reusing an id would make a genuinely new screen look like one already visited,
                // and its title would go unspoken. Reuse the last slot and say so.
                Debug.LogError("[LethalAccess] More than " + (MaxDialogId - FirstDialogId + 1) +
                               " screens have been published; '" + screenId + "' shares a node id with " +
                               "another screen and its title may not be announced on arrival.");
                return MaxDialogId;
            }

            id = _nextDialogId++;
            _dialogIdByScreen[screenId] = id;
            return id;
        }

        /// <summary>
        /// Fill everything mutable. Shared by the full build and the per-frame refresh so
        /// the two cannot drift.
        /// </summary>
        private void PopulateNode(UIScreenRegistry.UIEntryView v, AccessNode node)
        {
            UIEntry entry = v.Entry;
            string state = SafeState(entry, v.Target);

            node.Role = RoleFor(entry, state);
            node.Label = !string.IsNullOrEmpty(entry != null ? entry.Label : null)
                ? entry.Label
                : Utilities.GetTextFromComponent(v.Target.gameObject);
            node.PositionInSet = v.IndexInSection;
            node.SizeOfSet = v.SectionCount;
            node.Disabled = !IsInteractable(v.Target);
            node.AddAction(AccessAction.Focus);

            ApplyState(node, state);

            // An authored state is a deliberate override and always wins; this only fills the
            // silence where no delegate was written, which was most controls.
            if (string.IsNullOrEmpty(state)) ApplyComponentState(node, v.Target);

            if (node.Role == AccessRole.Slider) ApplyRange(node, v.Target);
        }

        /// <summary>
        /// Read the control's state off its own components, by role.
        ///
        /// Every state a control had was previously reachable ONLY through a hand-written State
        /// delegate on its UIEntry, and four entries in the whole mod have one. So a checkbox
        /// published no toggle state, a dropdown published no selection, and an edit field
        /// published no text - which a screen reader cannot compensate for: with no toggle state
        /// there is nothing to say on focus and, worse, nothing to CHANGE, so toggling a checkbox
        /// was silent.
        ///
        /// Driven by the authored role rather than by whatever components happen to be attached,
        /// because a role is a promise about which keys work and must not follow the scene.
        /// </summary>
        private static void ApplyComponentState(AccessNode node, Transform target)
        {
            GameObject go = target.gameObject;

            switch (node.Role)
            {
                case AccessRole.CheckBox:
                case AccessRole.Switch:
                {
                    UnityEngine.UI.Toggle toggle = target.GetComponent<UnityEngine.UI.Toggle>();
                    if (toggle != null)
                    {
                        node.Toggled = toggle.isOn ? Toggled.True : Toggled.False;
                        return;
                    }

                    // The settings panel's toggles are plain Buttons whose only indication is a
                    // swapped Image sprite, so the state lives in IngamePlayerSettings.
                    bool on;
                    if (UIAccessibilityManager.TryGetSettingsToggle(go, out on))
                    {
                        node.Toggled = on ? Toggled.True : Toggled.False;
                        return;
                    }

                    // Published as a checkbox with no state at all: a screen reader says "check box"
                    // and stops, and the user has no way to tell on from off. Name the control.
                    Debug.LogWarning("[LethalAccess] '" + go.name + "' is registered as a checkbox but has no " +
                                     "Toggle and no known SettingsOption, so it is published with no state.");
                    return;
                }

                case AccessRole.ComboBox:
                {
                    TMPro.TMP_Dropdown dropdown = target.GetComponent<TMPro.TMP_Dropdown>();
                    if (dropdown == null)
                    {
                        Debug.LogWarning("[LethalAccess] '" + go.name + "' is registered as a dropdown but has no " +
                                         "TMP_Dropdown, so its selected option is published with no value.");
                        return;
                    }

                    node.Expanded = dropdown.IsExpanded;
                    if (dropdown.value >= 0 && dropdown.value < dropdown.options.Count)
                    {
                        node.Value = dropdown.options[dropdown.value].text;
                    }
                    return;
                }

                case AccessRole.TextInput:
                {
                    TMPro.TMP_InputField field = target.GetComponent<TMPro.TMP_InputField>();
                    if (field == null) return;

                    node.Value = field.text;
                    if (field.placeholder != null)
                    {
                        TMPro.TMP_Text hint = field.placeholder.GetComponent<TMPro.TMP_Text>();
                        if (hint != null) node.Placeholder = hint.text;
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// Publish a Slider as a numeric range rather than as text.
        ///
        /// A screen reader given min/max/value normalises it itself - NVDA reports a slider's
        /// RangeValue as a percentage of its range - and it fires a value-change announcement
        /// that speaks the number ALONE while the user holds an arrow key, instead of repeating
        /// the name and role on every step. A scraped string would get neither.
        ///
        /// Left/right still reach the Slider: it returns null from FindSelectableOnLeft/Right on
        /// a horizontal axis, and Slider.OnMove steps the value whenever the finder returned null.
        /// </summary>
        private static void ApplyRange(AccessNode node, Transform target)
        {
            UnityEngine.UI.Slider slider = target.GetComponent<UnityEngine.UI.Slider>();
            if (slider == null)
            {
                // Authored as a slider but carrying no Slider component: the path is wrong, or the
                // scene changed. Publishing it as a valueless slider tells the user a value exists
                // and then never says one, so say which control is at fault.
                Debug.LogWarning("[LethalAccess] '" + target.name + "' is registered as a slider but has no " +
                                 "UnityEngine.UI.Slider, so it is published with no value.");
                return;
            }

            node.NumericValue = slider.value;
            node.MinNumericValue = slider.minValue;
            node.MaxNumericValue = slider.maxValue;

            // The step the game itself moves by, so a reader that offers its own increment command
            // lands on the same values arrow keys produce (UnityEngine.UI/Slider.OnMove).
            node.NumericValueStep = slider.wholeNumbers ? 1.0 : (slider.maxValue - slider.minValue) * 0.1;
            node.Orientation = slider.direction == UnityEngine.UI.Slider.Direction.BottomToTop ||
                               slider.direction == UnityEngine.UI.Slider.Direction.TopToBottom
                ? UnityA11y.Core.Orientation.Vertical
                : UnityA11y.Core.Orientation.Horizontal;
        }

        /// <summary>
        /// Map the authored type, plus the state text, onto a role.
        ///
        /// This is where the mod stops inventing wording. A control the mod used to
        /// announce as "Invert Y-Axis, off, button" is a CheckBox with Toggled = False,
        /// and the screen reader renders that in the user's own configured vocabulary -
        /// which is also why the on/off text must NOT also go in Value: CheckBox is one of
        /// the roles whose value is suppressed precisely to stop "off  not checked".
        /// </summary>
        private static AccessRole RoleFor(UIEntry entry, string state)
        {
            string type = entry != null ? entry.Type : null;
            if (string.IsNullOrEmpty(type)) return AccessRole.Button;

            // Spaces removed before matching. The authored vocabulary and the one the legacy
            // announcer sniffs are the same words spelled differently - "input field" is what
            // four host-menu entries are registered as and what SniffElementType returns, and it
            // matched nothing here, so every edit field on that screen published as a button.
            switch (type.Replace(" ", "").ToLowerInvariant())
            {
                case "slider": return AccessRole.Slider;
                case "dropdown": return AccessRole.ComboBox;
                case "checkbox":
                case "toggle": return AccessRole.CheckBox;
                case "input":
                case "inputfield": return AccessRole.TextInput;
                case "radiobutton": return AccessRole.RadioButton;
                case "scrollbar": return AccessRole.ScrollBar;
                case "scrollarea": return AccessRole.ScrollView;
                case "heading":
                case "text":
                case "label": return AccessRole.Label;
                case "tab": return AccessRole.Tab;

                // SniffElementType's fallback for "something focusable we could not identify".
                // Publishing it as a button would promise Enter/Space works.
                case "uielement": return AccessRole.Unknown;
            }

            // An authored "button" whose live state reads as on/off is really a toggle.
            // Six controls on the settings panel are exactly this: plain Buttons whose
            // only indication is a sprite swap, which is why the mod had to read their
            // state out of unsavedSettings in the first place.
            if (type == "button" && LooksBoolean(state)) return AccessRole.CheckBox;

            return AccessRole.Button;
        }

        private static bool LooksBoolean(string state)
        {
            if (string.IsNullOrEmpty(state)) return false;
            string s = state.Trim().ToLowerInvariant();
            return s == "on" || s == "off" || s == "enabled" || s == "disabled";
        }

        /// <summary>
        /// Route the state text to the right property. A boolean lands on Toggled, never
        /// on Value; anything else is a Value.
        /// </summary>
        private static void ApplyState(AccessNode node, string state)
        {
            if (string.IsNullOrEmpty(state)) return;

            if (node.Role == AccessRole.CheckBox || node.Role == AccessRole.Switch)
            {
                string s = state.Trim().ToLowerInvariant();
                node.Toggled = (s == "on" || s == "enabled" || s == "checked")
                    ? Toggled.True
                    : Toggled.False;
                return;
            }

            node.Value = state;
        }

        /// <summary>
        /// A state delegate is game code and can throw. One bad control must not cost the
        /// whole tree, and it must not fail silently either.
        /// </summary>
        private static string SafeState(UIEntry entry, Transform target)
        {
            if (entry == null || entry.State == null) return null;
            try
            {
                return entry.State();
            }
            catch (Exception ex)
            {
                Debug.LogError("[LethalAccess] State delegate for '" +
                               (target != null ? target.name : "<destroyed>") +
                               "' threw, so it is published with no value: " + ex.Message);
                return null;
            }
        }

        private static bool IsInteractable(Transform t)
        {
            if (t == null) return false;
            UnityEngine.UI.Selectable sel = t.GetComponent<UnityEngine.UI.Selectable>();
            return sel == null || sel.IsInteractable();
        }

        public bool TryRefreshNode(ulong id, AccessNode into)
        {
            UIScreenRegistry.UIEntryView v;
            if (!_viewById.TryGetValue(id, out v) || v.Target == null) return false;

            into.Reset(id, AccessRole.Unknown);
            PopulateNode(v, into);
            return true;
        }

        // ---- Focus --------------------------------------------------------------

        /// <summary>
        /// Where focus IS, read from the EventSystem - not where this class thinks it
        /// should be. The finder patches own movement; this only observes.
        /// </summary>
        public ulong CurrentFocusId()
        {
            EventSystem es = EventSystem.current;
            GameObject go = es != null ? es.currentSelectedGameObject : null;
            if (go == null) return 0;
            ulong id = StableId(go);
            return _targetById.ContainsKey(id) ? id : 0;
        }

        /// <summary>
        /// A focus request from the assistive technology - NVDA's "move focus to navigator
        /// object". Routed through FocusManager so it goes through the same choke point,
        /// and the same focus trap, as every other focus change in the mod.
        /// </summary>
        public void RequestFocus(ulong id)
        {
            Transform t;
            if (!_targetById.TryGetValue(id, out t) || t == null) return;
            FocusManager.Seed(t);
        }

        public void OnFocusChanged(ulong nodeId) { }

        public bool PerformAction(PendingAction action)
        {
            Transform t;
            if (!_targetById.TryGetValue(action.TargetNode, out t) || t == null) return false;

            switch (action.Action)
            {
                case AccessAction.Focus:
                    RequestFocus(action.TargetNode);
                    return true;

                case AccessAction.Click:
                    return Activate(t);
            }

            // Increment/Decrement deliberately unhandled: left and right already adjust the
            // focused control through the game's own Selectable.OnMove, and duplicating that
            // here would step twice per press.
            return false;
        }

        private static bool Activate(Transform t)
        {
            EventSystem es = EventSystem.current;
            if (es == null) return false;
            BaseEventData data = new BaseEventData(es);
            if (ExecuteEvents.Execute(t.gameObject, data, ExecuteEvents.submitHandler)) return true;

            PointerEventData pointer = new PointerEventData(es);
            pointer.button = PointerEventData.InputButton.Left;
            return ExecuteEvents.Execute(t.gameObject, pointer, ExecuteEvents.pointerClickHandler);
        }

        /// <summary>
        /// Stable for a GameObject's lifetime, which is what the tree differ needs. Biased
        /// above the reserved root/announcer/group range.
        /// </summary>
        public static ulong StableId(GameObject go)
        {
            unchecked
            {
                ulong id = (ulong)(uint)go.GetInstanceID();
                return id <= MaxGroupId ? id + 1024 : id;
            }
        }
    }
}
