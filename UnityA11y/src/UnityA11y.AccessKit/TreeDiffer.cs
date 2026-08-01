using System;
using System.Collections.Generic;
using UnityA11y.Core;

namespace UnityA11y.AccessKitInterop
{
    /// <summary>
    /// Decides what to send AccessKit, and materialises it.
    ///
    /// Why this exists (and why v1 pushing whole trees was wrong):
    /// AccessKit's TreeUpdate is documented as "only meant to bring the tree from
    /// a specific previous state into its next state", and "an update should only
    /// include nodes that are new or changed. AccessKit platform adapters will
    /// avoid raising extraneous events for nodes that have not changed since the
    /// previous update, but there is still a cost in processing these nodes and
    /// replacing the previous instances."
    ///
    /// So pushing everything is CORRECT but wasteful: the adapter diffs, and the
    /// user hears the right thing either way, but every push allocates and
    /// replaces every node. On a 200-control screen at 60fps that is real work
    /// for nothing.
    ///
    /// The dangerous direction is the other one. Sending a node whose parent is
    /// neither already in the tree nor in the same update makes accesskit_consumer
    /// panic ("TreeUpdate includes N nodes which are neither in the current tree
    /// nor a child of another node from the update"), and a Rust panic across FFI
    /// aborts the process. Godot shipped that crash. So the rule here is:
    ///
    ///   structural change  -> send the whole tree (orphans impossible)
    ///   content-only change -> send just the changed nodes (parents unchanged,
    ///                          so every node is already in the tree)
    ///
    /// Anything ambiguous falls back to a full send.
    /// </summary>
    internal sealed class TreeDiffer
    {
        private struct Published
        {
            public int Content;
            public int Children;
        }

        // What the assistive technology currently has.
        private Dictionary<ulong, Published> _published;
        private ulong _publishedRoot;
        private ulong _publishedFocus;

        // What we last examined, so an unchanged frame costs no FFI at all.
        private Dictionary<ulong, Published> _lastSeen;
        private ulong _lastSeenFocus;

        private readonly List<AccessNode> _delta = new List<AccessNode>();
        private bool _pendingFull;
        private AccessSnapshot _pending;

        /// <summary>Diagnostics for the most recent materialised update.</summary>
        public int LastNodeCount { get; private set; }
        public bool LastWasFull { get; private set; }

        public string ToolkitName = "UnityA11y";
        public string ToolkitVersion = "0.2.0";

        /// <summary>
        /// Returns true if anything at all changed since the previous frame.
        /// When false, the caller should skip the native call entirely.
        /// </summary>
        public bool Prepare(AccessSnapshot snap)
        {
            if (snap == null || snap.Nodes.Count == 0) return false;

            _pending = snap;
            ulong focus = ResolveFocus(snap);

            bool changed = false;
            if (_lastSeen == null || _lastSeenFocus != focus || _lastSeen.Count != snap.Nodes.Count)
            {
                changed = true;
            }
            else
            {
                for (int i = 0; i < snap.Nodes.Count; i++)
                {
                    var n = snap.Nodes[i];
                    Published prev;
                    if (!_lastSeen.TryGetValue(n.Id, out prev) ||
                        prev.Content != n.ContentHash() ||
                        prev.Children != n.ChildrenHash())
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed) return false;

            // Record what we have seen so the next unchanged frame is free.
            _lastSeen = Capture(snap);
            _lastSeenFocus = focus;
            return true;
        }

        /// <summary>
        /// Build the update to hand to AccessKit. Called from the update factory,
        /// which means AccessKit has already decided an AT is listening.
        /// Ownership of the returned pointer transfers to AccessKit.
        /// </summary>
        public IntPtr Materialize(bool forceFull)
        {
            var snap = _pending;
            if (snap == null || snap.Nodes.Count == 0)
                return MaterializeEmpty();

            ulong focus = ResolveFocus(snap);
            _pendingFull = forceFull || RequiresFullUpdate(snap);

            _delta.Clear();
            if (_pendingFull)
            {
                _delta.AddRange(snap.Nodes);
            }
            else
            {
                for (int i = 0; i < snap.Nodes.Count; i++)
                {
                    var n = snap.Nodes[i];
                    Published prev;
                    if (!_published.TryGetValue(n.Id, out prev) || prev.Content != n.ContentHash())
                        _delta.Add(n);
                }
            }

            var update = AK.accesskit_tree_update_with_capacity_and_focus((UIntPtr)_delta.Count, focus);

            for (int i = 0; i < _delta.Count; i++)
            {
                var n = _delta[i];
                AK.accesskit_tree_update_push_node(update, n.Id, NodeBuilder.Build(n));
            }

            if (_pendingFull)
            {
                var tree = AK.accesskit_tree_new(snap.RootId);
                AK.SetToolkit(tree, ToolkitName, ToolkitVersion);
                AK.accesskit_tree_update_set_tree(update, tree);
            }

            LastNodeCount = _delta.Count;
            LastWasFull = _pendingFull;

            _published = Capture(snap);
            _publishedRoot = snap.RootId;
            _publishedFocus = focus;
            return update;
        }

        /// <summary>
        /// A full update is required whenever the set of node ids changed, any
        /// node's child list changed, or the root changed. All of those can
        /// orphan a node in a partial update, which would abort the process.
        /// </summary>
        private bool RequiresFullUpdate(AccessSnapshot snap)
        {
            if (_published == null) return true;
            if (_publishedRoot != snap.RootId) return true;
            if (_published.Count != snap.Nodes.Count) return true;

            for (int i = 0; i < snap.Nodes.Count; i++)
            {
                var n = snap.Nodes[i];
                Published prev;
                if (!_published.TryGetValue(n.Id, out prev)) return true;
                if (prev.Children != n.ChildrenHash()) return true;
            }
            return false;
        }

        /// <summary>
        /// Focus must point at a node that exists in the resulting tree, or
        /// AccessKit panics. Fall back to the root, which always exists.
        /// </summary>
        private static ulong ResolveFocus(AccessSnapshot snap)
        {
            if (snap.FocusId != 0 && snap.Contains(snap.FocusId)) return snap.FocusId;
            return snap.RootId;
        }

        private static Dictionary<ulong, Published> Capture(AccessSnapshot snap)
        {
            var map = new Dictionary<ulong, Published>(snap.Nodes.Count);
            for (int i = 0; i < snap.Nodes.Count; i++)
            {
                var n = snap.Nodes[i];
                map[n.Id] = new Published { Content = n.ContentHash(), Children = n.ChildrenHash() };
            }
            return map;
        }

        /// <summary>
        /// The factory contract forbids returning null, so the degenerate case is
        /// a valid single-node tree rather than nothing.
        /// </summary>
        private IntPtr MaterializeEmpty()
        {
            var update = AK.accesskit_tree_update_with_capacity_and_focus((UIntPtr)1, 1);
            var root = AK.accesskit_node_new((byte)AccessRole.Window);
            AK.accesskit_tree_update_push_node(update, 1, root);
            var tree = AK.accesskit_tree_new(1);
            AK.SetToolkit(tree, ToolkitName, ToolkitVersion);
            AK.accesskit_tree_update_set_tree(update, tree);

            _published = null;
            _publishedRoot = 1;
            return update;
        }

        /// <summary>Forget everything; the next update will be a full one.</summary>
        public void Reset()
        {
            _published = null;
            _lastSeen = null;
            _publishedRoot = 0;
            _publishedFocus = 0;
            _lastSeenFocus = 0;
        }
    }

    internal static class NodeBuilder
    {
        public static IntPtr Build(AccessNode n)
        {
            var node = AK.accesskit_node_new((byte)n.Role);

            AK.SetLabel(node, n.Label);

            // NVDA drops a description identical to the name anyway; filtering
            // here keeps other assistive technologies consistent with it.
            if (!string.IsNullOrEmpty(n.Description) && n.Description != n.Label)
                AK.SetDescription(node, n.Description);

            AK.SetValue(node, n.Value);
            AK.SetPlaceholder(node, n.Placeholder);
            AK.SetShortcut(node, n.KeyboardShortcut);

            if (n.Disabled) AK.accesskit_node_set_disabled(node);
            if (n.Selected) AK.accesskit_node_set_selected(node, true);
            if (n.Expanded.HasValue) AK.accesskit_node_set_expanded(node, n.Expanded.Value);
            if (n.Toggled.HasValue) AK.accesskit_node_set_toggled(node, (byte)n.Toggled.Value);
            if (n.Live != LiveSetting.Off) AK.accesskit_node_set_live(node, (byte)n.Live);
            if (n.Orientation.HasValue) AK.accesskit_node_set_orientation(node, (byte)n.Orientation.Value);

            if (n.NumericValue.HasValue) AK.accesskit_node_set_numeric_value(node, n.NumericValue.Value);
            if (n.MinNumericValue.HasValue) AK.accesskit_node_set_min_numeric_value(node, n.MinNumericValue.Value);
            if (n.MaxNumericValue.HasValue) AK.accesskit_node_set_max_numeric_value(node, n.MaxNumericValue.Value);
            if (n.NumericValueStep.HasValue) AK.accesskit_node_set_numeric_value_step(node, n.NumericValueStep.Value);

            if (n.PositionInSet.HasValue) AK.accesskit_node_set_position_in_set(node, (UIntPtr)n.PositionInSet.Value);
            if (n.SizeOfSet.HasValue) AK.accesskit_node_set_size_of_set(node, (UIntPtr)n.SizeOfSet.Value);
            if (n.Level.HasValue) AK.accesskit_node_set_level(node, (UIntPtr)n.Level.Value);

            if (n.HasBounds)
            {
                AK.accesskit_node_set_bounds(node, new AK.Rect
                {
                    X0 = n.X,
                    Y0 = n.Y,
                    X1 = n.X + n.Width,
                    Y1 = n.Y + n.Height,
                });
            }

            for (int i = 0; i < n.Children.Count; i++)
                AK.accesskit_node_push_child(node, n.Children[i]);

            for (int i = 0; i < n.Actions.Count; i++)
                AK.accesskit_node_add_action(node, (byte)n.Actions[i]);

            return node;
        }
    }
}
