using System;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityA11y.AccessKitInterop
{
    /// <summary>
    /// Raw bindings to accesskit.dll (an accesskit-c release build).
    /// Signatures verified against AccessKit/accesskit-c include/accesskit.h.
    ///
    /// Ownership rules that will bite if ignored:
    ///  - accesskit_tree_update_push_node TAKES OWNERSHIP of the node. Do not free it.
    ///  - accesskit_tree_update_set_tree TAKES OWNERSHIP of the tree. Do not free it.
    ///  - The tree_update returned from an activation handler or update factory is
    ///    transferred to AccessKit. Do not free it, and NEVER return null.
    ///  - queued_events must be raised exactly once; raising also frees it.
    ///  - action_request ownership transfers to the callback; free it there.
    ///
    /// AccessKit is Rust and will ABORT THE PROCESS on a panic across FFI. The
    /// documented panic conditions we can actually trigger are:
    ///  - a TreeUpdate containing a node that is neither already in the tree nor a
    ///    child of another node in the same update (orphaned node),
    ///  - a focus id that is not present in the resulting tree.
    /// TreeDiffer is written to make both impossible.
    /// </summary>
    internal static class AK
    {
        internal const string DLL = "accesskit";

        // ---- Callback types -------------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr TreeUpdateFactory(IntPtr userdata);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr ActivationHandler(IntPtr userdata);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void ActionHandler(IntPtr request, IntPtr userdata);

        // ---- Structs --------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct OptLResult
        {
            [MarshalAs(UnmanagedType.I1)] public bool HasValue;
            public IntPtr Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public double X0, Y0, X1, Y1;
        }

        /// <summary>
        /// accesskit_text_position. `character_index` is size_t, so UIntPtr; it is 8 bytes in
        /// this process and an int field would misalign everything after it.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct TextPosition
        {
            public ulong Node;
            public UIntPtr CharacterIndex;
        }

        /// <summary>accesskit_text_selection. Passed BY VALUE, matching the header.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct TextSelection
        {
            public TextPosition Anchor;
            public TextPosition Focus;
        }

        /// <summary>
        /// Byte offsets inside accesskit_action_request, measured by compiling
        /// accesskit.h with gcc and printing offsetof rather than hand-computing
        /// alignment. All enums in the header are uint8_t.
        ///
        ///   action           0    (u8)
        ///   target_tree      1    (16 bytes, alignment 1)
        ///   target_node     24    (u64)
        ///   data.has_value  32    (bool)
        ///   data.value.tag  40    (u8)
        ///   data.value.*    48    (union payload: double / char* / i32)
        ///   sizeof          80
        /// </summary>
        internal static class ActionRequestLayout
        {
            public const int Action = 0;
            public const int TargetNode = 24;
            public const int DataHasValue = 32;
            public const int DataTag = 40;
            public const int DataPayload = 48;

            public const byte TagCustomAction = 0;
            public const byte TagValue = 1;
            public const byte TagNumericValue = 2;
        }

        // ---- Node -----------------------------------------------------------

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr accesskit_node_new(byte role);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_free(IntPtr node);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_label_with_length(IntPtr node, byte[] value, UIntPtr length);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_description_with_length(IntPtr node, byte[] value, UIntPtr length);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_value_with_length(IntPtr node, byte[] value, UIntPtr length);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_placeholder_with_length(IntPtr node, byte[] value, UIntPtr length);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_keyboard_shortcut_with_length(IntPtr node, byte[] value, UIntPtr length);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_push_child(IntPtr node, ulong child);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_add_action(IntPtr node, byte action);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_disabled(IntPtr node);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_selected(IntPtr node, [MarshalAs(UnmanagedType.I1)] bool value);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_expanded(IntPtr node, [MarshalAs(UnmanagedType.I1)] bool value);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_toggled(IntPtr node, byte value);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_live(IntPtr node, byte value);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_orientation(IntPtr node, byte value);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_numeric_value(IntPtr node, double value);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_min_numeric_value(IntPtr node, double value);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_max_numeric_value(IntPtr node, double value);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_numeric_value_step(IntPtr node, double value);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_position_in_set(IntPtr node, UIntPtr value);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_size_of_set(IntPtr node, UIntPtr value);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_level(IntPtr node, UIntPtr value);

        // Text. The slice setters take (length, values) and AccessKit copies immediately, so
        // the managed array only has to survive the call.
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_character_lengths(
            IntPtr node, UIntPtr length, byte[] values);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_word_starts(
            IntPtr node, UIntPtr length, byte[] values);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_text_direction(IntPtr node, byte value);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_text_selection(IntPtr node, TextSelection value);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_node_set_bounds(IntPtr node, Rect bounds);

        // ---- Tree -----------------------------------------------------------

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr accesskit_tree_new(ulong root);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_tree_set_toolkit_name_with_length(IntPtr tree, byte[] name, UIntPtr length);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_tree_set_toolkit_version_with_length(IntPtr tree, byte[] version, UIntPtr length);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr accesskit_tree_update_with_capacity_and_focus(UIntPtr capacity, ulong focus);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_tree_update_push_node(IntPtr update, ulong id, IntPtr node);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_tree_update_set_tree(IntPtr update, IntPtr tree);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_tree_update_free(IntPtr update);

        // ---- Windows adapter ------------------------------------------------

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr accesskit_windows_adapter_new(
            IntPtr hwnd,
            [MarshalAs(UnmanagedType.I1)] bool isWindowFocused,
            ActionHandler actionHandler,
            IntPtr actionHandlerUserdata);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_windows_adapter_free(IntPtr adapter);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr accesskit_windows_adapter_update_if_active(
            IntPtr adapter, TreeUpdateFactory factory, IntPtr userdata);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr accesskit_windows_adapter_update_window_focus_state(
            IntPtr adapter, [MarshalAs(UnmanagedType.I1)] bool isFocused);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern OptLResult accesskit_windows_adapter_handle_wm_getobject(
            IntPtr adapter, IntPtr wparam, IntPtr lparam,
            ActivationHandler activationHandler, IntPtr activationUserdata);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_windows_queued_events_raise(IntPtr events);

        // ---- Misc -----------------------------------------------------------

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_action_request_free(IntPtr request);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void accesskit_string_free(IntPtr s);

        // ---- String helpers --------------------------------------------------

        /// <summary>
        /// AccessKit copies strings on the far side, so the managed array can be
        /// collected as soon as the call returns. We always use the _with_length
        /// variants: a CharSet.Ansi marshaller would mangle any non-ASCII text,
        /// which matters the moment a game ships in German or Japanese.
        ///
        /// These wrappers exist so the length is never passed as a separate
        /// argument evaluated after an out-parameter, which is correct in C# but
        /// far too subtle to leave lying around.
        /// </summary>
        internal static void SetLabel(IntPtr node, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            var b = Encoding.UTF8.GetBytes(s);
            accesskit_node_set_label_with_length(node, b, (UIntPtr)b.Length);
        }

        internal static void SetDescription(IntPtr node, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            var b = Encoding.UTF8.GetBytes(s);
            accesskit_node_set_description_with_length(node, b, (UIntPtr)b.Length);
        }

        internal static void SetValue(IntPtr node, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            var b = Encoding.UTF8.GetBytes(s);
            accesskit_node_set_value_with_length(node, b, (UIntPtr)b.Length);
        }

        internal static void SetPlaceholder(IntPtr node, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            var b = Encoding.UTF8.GetBytes(s);
            accesskit_node_set_placeholder_with_length(node, b, (UIntPtr)b.Length);
        }

        internal static void SetShortcut(IntPtr node, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            var b = Encoding.UTF8.GetBytes(s);
            accesskit_node_set_keyboard_shortcut_with_length(node, b, (UIntPtr)b.Length);
        }

        internal static void SetToolkit(IntPtr tree, string name, string version)
        {
            if (!string.IsNullOrEmpty(name))
            {
                var b = Encoding.UTF8.GetBytes(name);
                accesskit_tree_set_toolkit_name_with_length(tree, b, (UIntPtr)b.Length);
            }
            if (!string.IsNullOrEmpty(version))
            {
                var b = Encoding.UTF8.GetBytes(version);
                accesskit_tree_set_toolkit_version_with_length(tree, b, (UIntPtr)b.Length);
            }
        }

        internal static string ReadUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            if (len == 0) return string.Empty;
            var buf = new byte[len];
            Marshal.Copy(ptr, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }
    }
}
