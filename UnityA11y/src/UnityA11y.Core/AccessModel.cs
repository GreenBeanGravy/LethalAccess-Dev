using System;
using System.Collections.Generic;

namespace UnityA11y.Core
{
    /// <summary>
    /// Subset of AccessKit's role enum. Ordinals MUST match accesskit.h exactly;
    /// they cross FFI as a raw uint8. Verified against AccessKit/accesskit-c
    /// include/accesskit.h.
    ///
    /// The comment after each entry is the NVDA role the Windows adapter's UIA
    /// control type maps to, verified against NVDA's
    /// UIAHandler.UIAControlTypesToNVDARoles. That mapping is what actually
    /// determines the word the user hears.
    /// </summary>
    public enum AccessRole : byte
    {
        Unknown = 0,              // unknown  (suppressed on focus)
        TextRun = 1,              // the characters of a text field; child of an input node
        Cell = 2,                 // cell     (suppressed on focus)
        Label = 3,                // static text (suppressed on focus)
        Image = 4,                // graphic
        Link = 5,                 // link
        Row = 6,
        ListItem = 7,             // list item (suppressed on focus)
        TreeItem = 9,             // tree view item (suppressed on focus)
        ListBoxOption = 10,       // list item
        MenuItem = 11,            // menu item (suppressed on focus)
        GenericContainer = 14,    // filtered out of the tree entirely
        CheckBox = 15,            // check box
        RadioButton = 16,         // radio button
        TextInput = 17,           // edit
        Button = 18,              // button
        Pane = 20,                // pane (suppressed on focus)
        RowHeader = 21,           // row header
        ColumnHeader = 22,        // column header
        List = 24,                // list
        Table = 25,               // table
        Switch = 29,              // check box, spoken with on/off
        Menu = 30,                // menu
        MultilineTextInput = 31,  // edit, multi line
        PasswordInput = 40,       // edit, protected
        Alert = 44,               // alert
        ComboBox = 56,            // combo box
        Dialog = 66,              // dialog
        Grid = 76,                // table
        GridCell = 77,            // cell
        Group = 78,               // grouping
        ListBox = 87,             // list
        MenuBar = 93,             // menu bar
        ProgressIndicator = 101,  // progress bar
        ScrollBar = 107,          // scroll bar
        ScrollView = 108,         // pane
        Slider = 113,             // slider
        SpinButton = 114,         // spin button
        Status = 116,             // status bar
        Tab = 120,                // tab
        TabList = 121,            // tab control
        TabPanel = 122,           // pane
        Tree = 129,               // tree view
        Window = 133,             // window  (required for the root node)
    }

    public enum Toggled : byte { False = 0, True = 1, Mixed = 2 }

    /// <summary>Maps to UIA LiveSetting, which NVDA reads as aria-live politeness.</summary>
    public enum LiveSetting : byte { Off = 0, Polite = 1, Assertive = 2 }

    public enum Orientation : byte { Horizontal = 0, Vertical = 1 }

    /// <summary>Ordinals match accesskit_text_direction.</summary>
    public enum TextDirection : byte
    {
        LeftToRight = 0,
        RightToLeft = 1,
        TopToBottom = 2,
        BottomToTop = 3,
    }

    /// <summary>Actions an assistive technology can request. Ordinals match accesskit.h.</summary>
    public enum AccessAction : byte
    {
        Click = 0,
        Focus = 1,
        Blur = 2,
        Collapse = 3,
        Expand = 4,
        Decrement = 6,
        Increment = 7,
        ScrollIntoView = 15,
        SetValue = 20,
    }

    /// <summary>
    /// A single accessible element. Plain data, no engine dependency, so the
    /// uGUI scanner and hand-written game adapters produce the same type.
    /// </summary>
    public sealed class AccessNode
    {
        /// <summary>
        /// Must be stable across frames for the same logical element. Unstable ids
        /// make NVDA treat every frame as a structural change.
        /// </summary>
        public ulong Id;

        public AccessRole Role = AccessRole.Unknown;

        public string Label;
        public string Description;
        public string Value;
        public string Placeholder;
        public string KeyboardShortcut;

        public bool Disabled;
        public bool Selected;
        public bool? Expanded;
        public Toggled? Toggled;
        public bool Focusable = true;
        public LiveSetting Live = LiveSetting.Off;
        public Orientation? Orientation;

        public double? NumericValue;
        public double? MinNumericValue;
        public double? MaxNumericValue;
        public double? NumericValueStep;

        /// <summary>
        /// 1-based. Drives NVDA's "n of m".
        ///
        /// AccessKit's own position_in_set is 0-based, so TreeDiffer subtracts one on the way
        /// out. Do not pre-subtract here: the internal announcer reads this field directly.
        /// </summary>
        public int? PositionInSet;

        /// <summary>
        /// Set on BOTH the item and its container. AccessKit documents this as a
        /// container property, but NVDA reads UIA SizeOfSet from the item itself
        /// (see UIA._get_positionInfo), so setting only one of the two risks
        /// losing the "of m" half of the announcement depending on how the
        /// adapter resolves it.
        /// </summary>
        public int? SizeOfSet;

        /// <summary>1-based tree depth. Drives NVDA's "level n".</summary>
        public int? Level;

        // ---- Text ---------------------------------------------------------------
        // A text field is published as an input node with one TextRun child holding the
        // characters, and the caret/selection on the input node pointing into that run.
        // Together they are what makes a UIA TextPattern exist, which is the only way a
        // screen reader can read a field back, move a review cursor through it, or say
        // which character the caret is on. A plain Value gives none of that - it is read
        // once on focus and is silent for every keystroke afterwards.

        /// <summary>
        /// TextRun only. One entry per character, holding that character's length in UTF-8
        /// BYTES. Must sum to the UTF-8 byte length of <see cref="Value"/>: AccessKit slices
        /// the string by these and a disagreement is a panic across FFI, which aborts the
        /// process rather than throwing.
        /// </summary>
        public byte[] CharacterLengths;

        /// <summary>
        /// TextRun only, optional. Character indices where words begin, ascending, for
        /// ctrl+arrow word movement. Indices are bytes in AccessKit, so a run longer than
        /// 255 characters cannot describe its later words and simply stops listing them.
        /// </summary>
        public byte[] WordStarts;

        /// <summary>TextRun only. Absent means the reader assumes left-to-right.</summary>
        public TextDirection? Direction;

        /// <summary>
        /// Input node only. The caret is the degenerate case where anchor equals focus.
        /// Both point at a TextRun id and a character index within it.
        /// </summary>
        public bool HasTextSelection;
        public ulong SelectionAnchorNode;
        public int SelectionAnchorIndex;
        public ulong SelectionFocusNode;
        public int SelectionFocusIndex;

        public double X, Y, Width, Height;
        public bool HasBounds;

        public readonly List<ulong> Children = new List<ulong>();
        public readonly List<AccessAction> Actions = new List<AccessAction>();

        public AccessNode() { }

        public AccessNode(ulong id, AccessRole role)
        {
            Id = id;
            Role = role;
        }

        public AccessNode AddAction(AccessAction a)
        {
            if (!Actions.Contains(a)) Actions.Add(a);
            return this;
        }

        public AccessNode SetBounds(double x, double y, double w, double h)
        {
            X = x; Y = y; Width = w; Height = h; HasBounds = true;
            return this;
        }

        public void Reset(ulong id, AccessRole role)
        {
            Id = id; Role = role;
            Label = Description = Value = Placeholder = KeyboardShortcut = null;
            Disabled = Selected = false;
            Expanded = null; Toggled = null;
            Focusable = true;
            Live = LiveSetting.Off;
            Orientation = null;
            NumericValue = MinNumericValue = MaxNumericValue = NumericValueStep = null;
            PositionInSet = SizeOfSet = Level = null;
            CharacterLengths = WordStarts = null;
            Direction = null;
            HasTextSelection = false;
            SelectionAnchorNode = SelectionFocusNode = 0;
            SelectionAnchorIndex = SelectionFocusIndex = 0;
            HasBounds = false;
            X = Y = Width = Height = 0;
            Children.Clear();
            Actions.Clear();
        }

        /// <summary>
        /// Hash of everything we publish except children. Drives incremental
        /// updates: if this is unchanged, the node is not resent.
        /// </summary>
        public int ContentHash()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (int)Role;
                h = h * 31 + (Label?.GetHashCode() ?? 0);
                h = h * 31 + (Description?.GetHashCode() ?? 0);
                h = h * 31 + (Value?.GetHashCode() ?? 0);
                h = h * 31 + (Placeholder?.GetHashCode() ?? 0);
                h = h * 31 + (KeyboardShortcut?.GetHashCode() ?? 0);
                h = h * 31 + (Disabled ? 1 : 0);
                h = h * 31 + (Selected ? 2 : 0);
                h = h * 31 + (Expanded.HasValue ? (Expanded.Value ? 3 : 4) : 0);
                h = h * 31 + (Toggled.HasValue ? (int)Toggled.Value + 5 : 0);
                h = h * 31 + (int)Live;
                h = h * 31 + (Orientation.HasValue ? (int)Orientation.Value + 9 : 0);
                h = h * 31 + HashDouble(NumericValue);
                h = h * 31 + HashDouble(MinNumericValue);
                h = h * 31 + HashDouble(MaxNumericValue);
                h = h * 31 + HashDouble(NumericValueStep);
                h = h * 31 + (PositionInSet ?? 0);
                h = h * 31 + (SizeOfSet ?? 0);
                h = h * 31 + (Level ?? 0);
                // Hashed by length only. The characters themselves are in Value, already
                // hashed above, and these arrays are rebuilt from it - so a change in one is
                // a change in the other, and hashing every byte would cost a walk of the
                // whole field on every frame the caret moves.
                h = h * 31 + (CharacterLengths != null ? CharacterLengths.Length : -1);
                h = h * 31 + (WordStarts != null ? WordStarts.Length : -1);
                h = h * 31 + (Direction.HasValue ? (int)Direction.Value + 1 : 0);
                h = h * 31 + (HasTextSelection ? 1 : 0);
                h = h * 31 + SelectionAnchorIndex;
                h = h * 31 + SelectionFocusIndex;
                h = h * 31 + (int)SelectionAnchorNode;
                h = h * 31 + (int)SelectionFocusNode;
                // Bounds are quantised: sub-pixel jitter from animated layouts
                // would otherwise resend every node every frame.
                h = h * 31 + (HasBounds ? (int)X * 7 + (int)Y * 13 + (int)Width * 17 + (int)Height * 19 : 0);
                for (int i = 0; i < Actions.Count; i++) h = h * 31 + (int)Actions[i];
                return h;
            }
        }

        public int ChildrenHash()
        {
            unchecked
            {
                int h = 19;
                for (int i = 0; i < Children.Count; i++)
                    h = h * 31 + Children[i].GetHashCode();
                return h;
            }
        }

        private static int HashDouble(double? d) =>
            d.HasValue ? d.Value.GetHashCode() : 0;
    }

    /// <summary>A complete tree for one frame.</summary>
    public sealed class AccessSnapshot
    {
        public ulong RootId;
        public ulong FocusId;
        public readonly List<AccessNode> Nodes = new List<AccessNode>();
        private readonly Dictionary<ulong, AccessNode> _index = new Dictionary<ulong, AccessNode>();

        public void Clear()
        {
            Nodes.Clear();
            _index.Clear();
            RootId = 0;
        }

        public void Add(AccessNode node)
        {
            Nodes.Add(node);
            _index[node.Id] = node;
        }

        public AccessNode Find(ulong id)
        {
            AccessNode n;
            return _index.TryGetValue(id, out n) ? n : null;
        }

        public bool Contains(ulong id) => _index.ContainsKey(id);
    }

    /// <summary>An action request from NVDA, queued for the main thread.</summary>
    public struct PendingAction
    {
        public AccessAction Action;
        public ulong TargetNode;
        public string StringValue;
        public double NumericValue;
        public bool HasNumericValue;
    }

    /// <summary>
    /// The one interface a per-game adapter implements. Everything else in this
    /// library is game-independent.
    /// </summary>
    public interface IUiSource
    {
        string Name { get; }

        /// <summary>Higher wins. The built-in uGUI scanner is 0; game adapters use 100+.</summary>
        int Priority { get; }

        bool IsActive { get; }

        /// <summary>True when the tree's structure changed and needs a full rebuild.</summary>
        bool IsDirty { get; }

        void BuildSnapshot(AccessSnapshot into);

        /// <summary>
        /// Cheap per-frame refresh of one node's mutable properties, without
        /// rebuilding the tree. This is what makes slider drags and toggle
        /// presses announce immediately instead of waiting for the next poll.
        /// Return false if the node cannot be refreshed in isolation.
        /// </summary>
        bool TryRefreshNode(ulong id, AccessNode into);

        IReadOnlyList<ulong> FocusRing { get; }

        void OnFocusChanged(ulong nodeId);

        bool PerformAction(PendingAction action);
    }
}
