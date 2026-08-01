using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityA11y.Core;

namespace UnityA11y.Unity
{
    /// <summary>
    /// Zero-configuration fallback source. Walks the active Canvases and turns
    /// uGUI Selectables into accessible nodes. Enough to make a large share of
    /// Unity games navigable with no game-specific code, and a useful base class
    /// for games that need refinement.
    ///
    /// Games with bespoke UI should implement IUiSource directly and register at
    /// a higher priority.
    /// </summary>
    public class UGuiSource : IUiSource
    {
        public virtual string Name => "uGUI";
        public virtual int Priority => 0;

        protected const ulong RootId = 1;
        // Ids 1 and 2 are reserved by A11yRuntime for the root and the
        // live-region announcer, so group nodes start well clear of them.
        protected const ulong FirstGroupId = 16;

        private readonly List<ulong> _focusRing = new List<ulong>();
        private readonly Dictionary<ulong, Selectable> _selectableById = new Dictionary<ulong, Selectable>();
        private readonly Dictionary<ulong, int> _setSizeById = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> _setIndexById = new Dictionary<ulong, int>();
        private readonly List<Selectable> _scratch = new List<Selectable>();

        private int _lastSignature;
        private bool _dirty = true;

        public IReadOnlyList<ulong> FocusRing => _focusRing;

        public virtual bool IsActive => EventSystem.current != null;

        /// <summary>
        /// Structural dirty check only. Value changes are handled by
        /// TryRefreshNode, which is O(1) instead of O(controls).
        /// </summary>
        public virtual bool IsDirty
        {
            get
            {
                if (_dirty) return true;
                int sig = ComputeSignature();
                if (sig != _lastSignature) { _lastSignature = sig; return true; }
                return false;
            }
        }

        public void Invalidate() => _dirty = true;

        protected virtual int ComputeSignature()
        {
            unchecked
            {
                int hash = 17;
                var all = SelectableCompat.All();
                for (int i = 0; i < all.Count; i++)
                {
                    var s = all[i];
                    if (s == null) continue;
                    hash = hash * 31 + s.GetInstanceID();
                    hash = hash * 31 + (s.IsInteractable() ? 1 : 0);
                    hash = hash * 31 + (s.gameObject.activeInHierarchy ? 1 : 0);
                }
                return hash;
            }
        }

        // ---- Tree construction ------------------------------------------------

        public virtual void BuildSnapshot(AccessSnapshot into)
        {
            _dirty = false;
            into.Clear();
            _focusRing.Clear();
            _selectableById.Clear();
            _setSizeById.Clear();
            _setIndexById.Clear();

            var root = new AccessNode(RootId, AccessRole.Window)
            {
                Label = Application.productName,
                Focusable = false,
            };
            into.Add(root);
            into.RootId = RootId;

            CollectSelectables(_scratch);
            SortInReadingOrder(_scratch);

            // Bucket by group key, preserving reading order within each group.
            var groupOrder = new List<object>();
            var groups = new Dictionary<object, List<Selectable>>();
            for (int i = 0; i < _scratch.Count; i++)
            {
                var sel = _scratch[i];
                object key = GroupKeyFor(sel) ?? this;
                List<Selectable> bucket;
                if (!groups.TryGetValue(key, out bucket))
                {
                    bucket = new List<Selectable>();
                    groups[key] = bucket;
                    groupOrder.Add(key);
                }
                bucket.Add(sel);
            }

            // A single group adds a container announcement with no information in
            // it, so flatten in that case and parent controls straight to the root.
            bool useGroups = groupOrder.Count > 1;
            ulong nextGroupId = FirstGroupId;

            for (int g = 0; g < groupOrder.Count; g++)
            {
                var key = groupOrder[g];
                var bucket = groups[key];
                AccessNode parent = root;

                if (useGroups)
                {
                    var groupNode = new AccessNode(nextGroupId++, AccessRole.Group)
                    {
                        Label = GroupLabelFor(key),
                        Focusable = false,
                        SizeOfSet = bucket.Count,
                    };
                    into.Add(groupNode);
                    root.Children.Add(groupNode.Id);
                    parent = groupNode;
                }

                for (int i = 0; i < bucket.Count; i++)
                {
                    var sel = bucket[i];
                    AccessNode node;
                    try
                    {
                        node = BuildNodeFor(sel, i + 1, bucket.Count);
                    }
                    catch (Exception)
                    {
                        // One malformed control must not take down the whole tree.
                        continue;
                    }
                    if (node == null) continue;

                    into.Add(node);
                    parent.Children.Add(node.Id);
                    _selectableById[node.Id] = sel;
                    _setIndexById[node.Id] = i + 1;
                    _setSizeById[node.Id] = bucket.Count;
                    if (node.Focusable && !node.Disabled) _focusRing.Add(node.Id);
                }
            }

            if (into.FocusId == 0 || !into.Contains(into.FocusId))
                into.FocusId = _focusRing.Count > 0 ? _focusRing[0] : RootId;
        }

        /// <summary>
        /// What counts as one group of related controls. Defaults to the Canvas,
        /// which separates screens in games that use one Canvas per menu. Override
        /// to group by panel, tab or whatever the game's structure actually is.
        /// </summary>
        protected virtual object GroupKeyFor(Selectable sel)
        {
            var canvas = sel.GetComponentInParent<Canvas>();
            return canvas != null ? canvas.rootCanvas : null;
        }

        protected virtual string GroupLabelFor(object key)
        {
            var canvas = key as Canvas;
            return canvas != null ? Humanize(canvas.gameObject.name) : null;
        }

        protected virtual void CollectSelectables(List<Selectable> results)
        {
            results.Clear();
            var all = SelectableCompat.All();
            for (int i = 0; i < all.Count; i++)
            {
                var s = all[i];
                if (s == null) continue;
                if (!s.gameObject.activeInHierarchy) continue;
                if (!IsVisible(s)) continue;
                results.Add(s);
            }
        }

        /// <summary>
        /// Filters out Selectables that exist but are not really on screen: zero
        /// alpha CanvasGroups, disabled Canvases, degenerate rects. Without this
        /// the user tabs into invisible controls belonging to closed menus, which
        /// is the most common complaint about naive uGUI scanners.
        ///
        /// Note that a non-interactable CanvasGroup is NOT treated as hidden. A
        /// greyed-out panel should be announced as unavailable, not disappear.
        /// </summary>
        protected virtual bool IsVisible(Selectable s)
        {
            var canvas = s.GetComponentInParent<Canvas>();
            if (canvas == null || !canvas.isActiveAndEnabled) return false;

            var t = s.transform;
            while (t != null)
            {
                var group = t.GetComponent<CanvasGroup>();
                if (group != null)
                {
                    if (group.alpha <= 0.01f) return false;
                    if (group.ignoreParentGroups) break;
                }
                t = t.parent;
            }

            var rt = s.transform as RectTransform;
            if (rt != null && (rt.rect.width <= 0f || rt.rect.height <= 0f)) return false;

            return true;
        }

        protected virtual bool IsEffectivelyInteractable(Selectable s)
        {
            if (!s.IsInteractable()) return false;
            var t = s.transform;
            while (t != null)
            {
                var group = t.GetComponent<CanvasGroup>();
                if (group != null)
                {
                    if (!group.interactable) return false;
                    if (group.ignoreParentGroups) break;
                }
                t = t.parent;
            }
            return true;
        }

        /// <summary>
        /// Top-to-bottom, then left-to-right, in screen space. Rows are bucketed
        /// with a tolerance so a horizontal row of buttons is not interleaved by a
        /// few pixels of vertical jitter.
        /// </summary>
        protected virtual void SortInReadingOrder(List<Selectable> list)
        {
            const float rowTolerance = 12f;
            list.Sort((a, b) =>
            {
                Vector3 pa = a.transform.position, pb = b.transform.position;
                if (Mathf.Abs(pa.y - pb.y) > rowTolerance)
                    return pb.y.CompareTo(pa.y); // higher y first: Unity screen space is bottom-up
                int byX = pa.x.CompareTo(pb.x);
                if (byX != 0) return byX;
                // Deterministic tie-break, otherwise List.Sort is unstable and the
                // reading order flickers between frames.
                return a.GetInstanceID().CompareTo(b.GetInstanceID());
            });
        }

        protected virtual AccessNode BuildNodeFor(Selectable sel, int index, int total)
        {
            var node = new AccessNode(StableId(sel.gameObject), RoleFor(sel));
            PopulateNode(sel, node, index, total);
            return node;
        }

        /// <summary>
        /// Fills every mutable property. Shared by the full build and the cheap
        /// per-frame refresh so the two can never drift apart.
        /// </summary>
        protected virtual void PopulateNode(Selectable sel, AccessNode node, int index, int total)
        {
            var over = sel.GetComponent<A11yLabel>();

            node.Role = RoleFor(sel);
            node.Label = ExtractLabel(sel);
            node.Description = over != null && !string.IsNullOrEmpty(over.Description) ? over.Description : null;
            node.Disabled = !IsEffectivelyInteractable(sel);
            node.PositionInSet = index;
            node.SizeOfSet = total;
            node.AddAction(AccessAction.Focus);

            ApplyBounds(sel, node);

            var toggle = sel as Toggle;
            if (toggle != null)
            {
                node.Toggled = toggle.isOn ? Core.Toggled.True : Core.Toggled.False;
                // Radio buttons express state through selection in UIA as well as
                // through the toggle pattern; setting both keeps every AT happy.
                if (node.Role == AccessRole.RadioButton) node.Selected = toggle.isOn;
                node.AddAction(AccessAction.Click);
                return;
            }

            var slider = sel as Slider;
            if (slider != null)
            {
                node.NumericValue = slider.value;
                node.MinNumericValue = slider.minValue;
                node.MaxNumericValue = slider.maxValue;
                node.NumericValueStep = slider.wholeNumbers
                    ? 1.0
                    : Math.Max((slider.maxValue - slider.minValue) / 100.0, 0.0001);
                node.Value = FormatSliderValue(slider);
                node.Orientation = slider.direction == Slider.Direction.BottomToTop ||
                                   slider.direction == Slider.Direction.TopToBottom
                    ? Core.Orientation.Vertical
                    : Core.Orientation.Horizontal;
                node.AddAction(AccessAction.Increment)
                    .AddAction(AccessAction.Decrement)
                    .AddAction(AccessAction.SetValue);
                return;
            }

            var scrollbar = sel as Scrollbar;
            if (scrollbar != null)
            {
                node.NumericValue = scrollbar.value;
                node.MinNumericValue = 0;
                node.MaxNumericValue = 1;
                node.Orientation = scrollbar.direction == Scrollbar.Direction.BottomToTop ||
                                   scrollbar.direction == Scrollbar.Direction.TopToBottom
                    ? Core.Orientation.Vertical
                    : Core.Orientation.Horizontal;
                node.AddAction(AccessAction.Increment).AddAction(AccessAction.Decrement);
                return;
            }

            var dropdown = sel as Dropdown;
            if (dropdown != null)
            {
                node.Value = dropdown.value >= 0 && dropdown.value < dropdown.options.Count
                    ? dropdown.options[dropdown.value].text
                    : null;
                node.Expanded = false;
                node.SizeOfSet = total;
                node.AddAction(AccessAction.Click)
                    .AddAction(AccessAction.Expand)
                    .AddAction(AccessAction.Increment)
                    .AddAction(AccessAction.Decrement);
                return;
            }

            var input = sel as InputField;
            if (input != null)
            {
                // Never publish the contents of a password field.
                node.Value = input.contentType == InputField.ContentType.Password ? null : input.text;
                node.Placeholder = ExtractText(input.placeholder);
                node.AddAction(AccessAction.SetValue);
                return;
            }

            if (TmpSupport.TryDescribe(sel, node)) return;

            node.AddAction(AccessAction.Click);
        }

        protected virtual void ApplyBounds(Selectable sel, AccessNode node)
        {
            var rt = sel.transform as RectTransform;
            if (rt == null) return;

            var canvas = sel.GetComponentInParent<Canvas>();
            // Screen Space - Overlay uses a null camera; every other render mode
            // needs the canvas camera or the coordinates are meaningless.
            Camera cam = null;
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                cam = canvas.worldCamera ?? Camera.main;

            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            var min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
            var max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);

            // AccessKit and UIA use a top-left origin; Unity screen space is
            // bottom-left, so the Y axis has to be flipped.
            node.SetBounds(
                Mathf.Min(min.x, max.x),
                Screen.height - Mathf.Max(min.y, max.y),
                Mathf.Abs(max.x - min.x),
                Mathf.Abs(max.y - min.y));
        }

        public virtual bool TryRefreshNode(ulong id, AccessNode into)
        {
            Selectable sel;
            if (!_selectableById.TryGetValue(id, out sel) || sel == null) return false;

            int index, size;
            _setIndexById.TryGetValue(id, out index);
            _setSizeById.TryGetValue(id, out size);

            into.Reset(id, RoleFor(sel));
            PopulateNode(sel, into, index, size);
            return true;
        }

        protected virtual string FormatSliderValue(Slider s)
        {
            if (s.wholeNumbers) return Mathf.RoundToInt(s.value).ToString();
            float range = s.maxValue - s.minValue;
            if (range <= 0f) return s.value.ToString("0.##");
            // Percentages read far better than raw floats.
            int pct = Mathf.RoundToInt((s.value - s.minValue) / range * 100f);
            return pct + "%";
        }

        protected virtual AccessRole RoleFor(Selectable sel)
        {
            if (sel is Button) return AccessRole.Button;
            if (sel is Scrollbar) return AccessRole.ScrollBar;
            if (sel is Slider) return AccessRole.Slider;
            if (sel is Dropdown) return AccessRole.ComboBox;

            var toggle = sel as Toggle;
            if (toggle != null)
                return toggle.group != null ? AccessRole.RadioButton : AccessRole.CheckBox;

            var input = sel as InputField;
            if (input != null)
            {
                if (input.contentType == InputField.ContentType.Password) return AccessRole.PasswordInput;
                return input.lineType == InputField.LineType.SingleLine
                    ? AccessRole.TextInput
                    : AccessRole.MultilineTextInput;
            }

            return TmpSupport.RoleFor(sel);
        }

        /// <summary>
        /// Label resolution, in priority order: an explicit override component,
        /// the control's own text children, a nearby non-interactive sibling, then
        /// the GameObject name as a last resort.
        /// </summary>
        protected virtual string ExtractLabel(Selectable sel)
        {
            var over = sel.GetComponent<A11yLabel>();
            if (over != null && !string.IsNullOrEmpty(over.Label)) return over.Label;

            string text = FindTextInChildren(sel.transform);
            if (!string.IsNullOrEmpty(text)) return text;

            // Settings rows usually put the label beside the control rather than
            // inside it, so check siblings before giving up.
            var parent = sel.transform.parent;
            if (parent != null)
            {
                for (int i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    if (child == sel.transform) continue;
                    if (child.GetComponent<Selectable>() != null) continue;
                    text = FindTextInChildren(child);
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }

            return Humanize(sel.gameObject.name);
        }

        protected static string FindTextInChildren(Transform root)
        {
            var legacy = root.GetComponentsInChildren<Text>(false);
            for (int i = 0; i < legacy.Length; i++)
            {
                var t = Clean(legacy[i].text);
                if (!string.IsNullOrEmpty(t)) return t;
            }
            return TmpSupport.FindText(root);
        }

        protected static string ExtractText(Graphic g)
        {
            var t = g as Text;
            if (t != null) return Clean(t.text);
            return g != null ? TmpSupport.GetText(g) : null;
        }

        /// <summary>Strips Unity rich-text markup, which a screen reader would spell out.</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.IndexOf('<') < 0) return s.Trim();
            var sb = new System.Text.StringBuilder(s.Length);
            bool inTag = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>"MasterVolumeSlider" -> "Master Volume Slider". Last-resort labelling.</summary>
        protected static string Humanize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new System.Text.StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '_' || c == '-') { sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Instance ids are stable for a GameObject's lifetime, which is what the
        /// tree diff needs. Ids 0 and 1 are reserved for "none" and the root, and
        /// low ids are reserved for group nodes.
        /// </summary>
        public static ulong StableId(GameObject go)
        {
            unchecked
            {
                ulong id = (ulong)(uint)go.GetInstanceID();
                return id < 1024 ? id + 1024 : id;
            }
        }

        // ---- Interaction --------------------------------------------------------

        public virtual void OnFocusChanged(ulong nodeId)
        {
            Selectable sel;
            if (!_selectableById.TryGetValue(nodeId, out sel) || sel == null) return;
            var es = EventSystem.current;
            if (es != null && es.currentSelectedGameObject != sel.gameObject)
                es.SetSelectedGameObject(sel.gameObject);
        }

        public virtual bool PerformAction(PendingAction action)
        {
            Selectable sel;
            if (!_selectableById.TryGetValue(action.TargetNode, out sel) || sel == null) return false;

            switch (action.Action)
            {
                case AccessAction.Focus:
                    OnFocusChanged(action.TargetNode);
                    return true;

                case AccessAction.Click:
                case AccessAction.Expand:
                    return Activate(sel);

                case AccessAction.Increment:
                    return Nudge(sel, +1);

                case AccessAction.Decrement:
                    return Nudge(sel, -1);

                case AccessAction.SetValue:
                    return SetValue(sel, action);
            }
            return false;
        }

        protected virtual bool SetValue(Selectable sel, PendingAction action)
        {
            if (action.HasNumericValue)
            {
                var slider = sel as Slider;
                if (slider != null)
                {
                    slider.value = Mathf.Clamp((float)action.NumericValue, slider.minValue, slider.maxValue);
                    return true;
                }
                var scrollbar = sel as Scrollbar;
                if (scrollbar != null)
                {
                    scrollbar.value = Mathf.Clamp01((float)action.NumericValue);
                    return true;
                }
            }

            if (action.StringValue != null)
            {
                var input = sel as InputField;
                if (input != null && input.contentType != InputField.ContentType.Password)
                {
                    input.text = action.StringValue;
                    return true;
                }
            }
            return false;
        }

        public virtual bool Activate(Selectable sel)
        {
            if (sel == null || !IsEffectivelyInteractable(sel)) return false;

            var toggle = sel as Toggle;
            if (toggle != null) { toggle.isOn = !toggle.isOn; return true; }

            var dropdown = sel as Dropdown;
            if (dropdown != null) { dropdown.Show(); return true; }

            // Route through the EventSystem rather than calling onClick directly:
            // games commonly attach their own ISubmitHandler for sounds and state.
            var es = EventSystem.current;
            if (es == null) return false;
            var data = new BaseEventData(es);
            if (ExecuteEvents.Execute(sel.gameObject, data, ExecuteEvents.submitHandler)) return true;

            // Some games only wire up pointer clicks, so fall back to those.
            var pointer = new PointerEventData(es) { button = PointerEventData.InputButton.Left };
            return ExecuteEvents.Execute(sel.gameObject, pointer, ExecuteEvents.pointerClickHandler);
        }

        public virtual bool Nudge(Selectable sel, int direction)
        {
            var slider = sel as Slider;
            if (slider != null)
            {
                float step = slider.wholeNumbers ? 1f : (slider.maxValue - slider.minValue) / 20f;
                slider.value = Mathf.Clamp(slider.value + step * direction, slider.minValue, slider.maxValue);
                return true;
            }

            var scrollbar = sel as Scrollbar;
            if (scrollbar != null)
            {
                scrollbar.value = Mathf.Clamp01(scrollbar.value + 0.1f * direction);
                return true;
            }

            var dropdown = sel as Dropdown;
            if (dropdown != null && dropdown.options.Count > 0)
            {
                int next = dropdown.value + direction;
                if (next < 0) next = dropdown.options.Count - 1;
                if (next >= dropdown.options.Count) next = 0;
                dropdown.value = next;
                return true;
            }

            return TmpSupport.TryNudge(sel, direction);
        }

        public Selectable Resolve(ulong nodeId)
        {
            Selectable sel;
            return _selectableById.TryGetValue(nodeId, out sel) ? sel : null;
        }
    }

    /// <summary>
    /// Drop this on a GameObject to override what the scanner would guess.
    /// </summary>
    public class A11yLabel : MonoBehaviour
    {
        public string Label;
        public string Description;
    }

    /// <summary>
    /// Selectable.allSelectablesArray only exists from Unity 2019.3. Older games
    /// expose the List-based allSelectables instead, and calling the missing one
    /// throws MissingMethodException at JIT time, which is not catchable at the
    /// call site in a useful way. So resolve it once by reflection.
    /// </summary>
    internal static class SelectableCompat
    {
        private static bool _probed;
        private static PropertyInfo _arrayProp;
        private static PropertyInfo _listProp;
        private static readonly List<Selectable> _buffer = new List<Selectable>(256);

        public static List<Selectable> All()
        {
            if (!_probed)
            {
                _probed = true;
                var t = typeof(Selectable);
                _arrayProp = t.GetProperty("allSelectablesArray", BindingFlags.Public | BindingFlags.Static);
                _listProp = t.GetProperty("allSelectables", BindingFlags.Public | BindingFlags.Static);
            }

            _buffer.Clear();
            if (_arrayProp != null)
            {
                var arr = _arrayProp.GetValue(null, null) as Selectable[];
                if (arr != null) _buffer.AddRange(arr);
            }
            else if (_listProp != null)
            {
                var list = _listProp.GetValue(null, null) as IList<Selectable>;
                if (list != null)
                    for (int i = 0; i < list.Count; i++) _buffer.Add(list[i]);
            }
            return _buffer;
        }
    }

    /// <summary>
    /// Reflection shim for TextMeshPro. TMP ships in most but not all games, and
    /// hard-referencing it would make this assembly fail to load where it is absent.
    /// </summary>
    internal static class TmpSupport
    {
        private static bool _probed;
        private static Type _tmpText, _tmpInputField, _tmpDropdown;
        private static PropertyInfo _textProp;

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (_tmpText == null) _tmpText = asm.GetType("TMPro.TMP_Text");
                    if (_tmpInputField == null) _tmpInputField = asm.GetType("TMPro.TMP_InputField");
                    if (_tmpDropdown == null) _tmpDropdown = asm.GetType("TMPro.TMP_Dropdown");
                }
                catch (ReflectionTypeLoadException) { }
            }
            if (_tmpText != null) _textProp = _tmpText.GetProperty("text");
        }

        internal static string FindText(Transform root)
        {
            Probe();
            if (_tmpText == null || _textProp == null) return null;
            var comps = root.GetComponentsInChildren(_tmpText, false);
            for (int i = 0; i < comps.Length; i++)
            {
                var s = UGuiSource.Clean((string)_textProp.GetValue(comps[i], null));
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return null;
        }

        internal static string GetText(Component c)
        {
            Probe();
            if (_tmpText == null || _textProp == null || !_tmpText.IsInstanceOfType(c)) return null;
            return UGuiSource.Clean((string)_textProp.GetValue(c, null));
        }

        internal static AccessRole RoleFor(Selectable sel)
        {
            Probe();
            if (_tmpInputField != null && _tmpInputField.IsInstanceOfType(sel)) return AccessRole.TextInput;
            if (_tmpDropdown != null && _tmpDropdown.IsInstanceOfType(sel)) return AccessRole.ComboBox;
            return AccessRole.Button;
        }

        internal static bool TryDescribe(Selectable sel, AccessNode node)
        {
            Probe();
            if (_tmpDropdown != null && _tmpDropdown.IsInstanceOfType(sel))
            {
                var caption = _tmpDropdown.GetProperty("captionText")?.GetValue(sel, null) as Component;
                node.Value = caption != null ? GetText(caption) : null;
                node.Expanded = false;
                node.AddAction(AccessAction.Click)
                    .AddAction(AccessAction.Expand)
                    .AddAction(AccessAction.Increment)
                    .AddAction(AccessAction.Decrement);
                return true;
            }

            if (_tmpInputField != null && _tmpInputField.IsInstanceOfType(sel))
            {
                var contentType = _tmpInputField.GetProperty("contentType")?.GetValue(sel, null);
                bool isPassword = contentType != null && contentType.ToString() == "Password";
                node.Value = isPassword ? null : _tmpInputField.GetProperty("text")?.GetValue(sel, null) as string;
                if (isPassword) node.Role = AccessRole.PasswordInput;
                node.AddAction(AccessAction.SetValue);
                return true;
            }

            return false;
        }

        internal static bool TryNudge(Selectable sel, int direction)
        {
            Probe();
            if (_tmpDropdown == null || !_tmpDropdown.IsInstanceOfType(sel)) return false;

            var valueProp = _tmpDropdown.GetProperty("value");
            var optionsProp = _tmpDropdown.GetProperty("options");
            if (valueProp == null || optionsProp == null) return false;

            var options = optionsProp.GetValue(sel, null) as System.Collections.IList;
            if (options == null || options.Count == 0) return false;

            int current = (int)valueProp.GetValue(sel, null);
            int next = current + direction;
            if (next < 0) next = options.Count - 1;
            if (next >= options.Count) next = 0;
            valueProp.SetValue(sel, next, null);
            return true;
        }
    }
}
