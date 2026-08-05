using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityA11y.Core;

namespace UnityA11y.AccessKitInterop
{
    /// <summary>
    /// Owns the AccessKit Windows adapter and the game window's WndProc subclass.
    ///
    /// Why not accesskit_windows_subclassing_adapter_new: the header states it
    /// panics if the window is already visible, and it must be created before the
    /// window is first shown. A mod loads long after Unity has created and shown
    /// its window, so we subclass manually and forward WM_GETOBJECT to
    /// accesskit_windows_adapter_handle_wm_getobject ourselves.
    ///
    /// Threading: the WndProc and the activation handler run on the window
    /// thread, which is Unity's main thread. The action handler may be called
    /// from an arbitrary RPC thread, so requests are queued and drained from
    /// Update().
    /// </summary>
    public sealed class AccessKitBridge : IDisposable
    {
        private const int GWLP_WNDPROC = -4;
        private const uint WM_GETOBJECT = 0x003D;
        private const uint WM_ACTIVATE = 0x0006;
        private const uint WM_DESTROY = 0x0002;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern void SetLastError(uint code);

        [DllImport("user32.dll")]
        private static extern void NotifyWinEvent(uint eventId, IntPtr hwnd, int idObject, int idChild);

        private const uint EVENT_OBJECT_FOCUS = 0x8005;
        private const int OBJID_CLIENT = -4;
        private const int CHILDID_SELF = 0;

        // Static callbacks: IL2CPP's reverse P/Invoke requires static methods, and
        // a single bridge instance is all we ever need.
        private static AccessKitBridge _instance;

        private IntPtr _hwnd;
        private IntPtr _adapter;
        private IntPtr _prevWndProc;

        // These delegates MUST outlive every native call that can reach them.
        // Letting one be collected produces an access violation minutes later,
        // in an unrelated place, which is a miserable bug to chase.
        private WndProcDelegate _wndProcKeepAlive;
        private static readonly AK.ActivationHandler _activationKeepAlive = OnActivation;
        private static readonly AK.ActionHandler _actionKeepAlive = OnAction;
        private static readonly AK.TreeUpdateFactory _updateFactoryKeepAlive = OnUpdateFactory;

        private readonly TreeDiffer _differ = new TreeDiffer();
        // A plain Queue behind a lock rather than ConcurrentQueue: the latter is
        // absent from the .NET 3.5 Mono profile that older Unity games still use,
        // and the action rate here is a handful per second at most.
        private readonly Queue<PendingAction> _actions = new Queue<PendingAction>();
        private readonly object _actionLock = new object();
        private static uint _ownPid;

        public bool IsRunning { get; private set; }
        public Action<string> Log = _ => { };

        public string ToolkitName
        {
            get { return _differ.ToolkitName; }
            set { _differ.ToolkitName = value; }
        }

        /// <summary>Diagnostics: what the last update actually carried. Log these
        /// when a screen feels sluggish; a full update every frame means some
        /// source is reporting a structural change it should not be.</summary>
        public int LastUpdateNodeCount => _differ.LastNodeCount;
        public bool LastUpdateWasFull => _differ.LastWasFull;

        /// <summary>
        /// Counters for the one question that matters when a screen reader stays silent:
        /// how far down the chain does it get?
        ///
        ///   WndProcMessages == 0     the subclass is not receiving anything at all
        ///   WmGetObject     == 0     no assistive technology has queried this window
        ///   Activations     == 0     it queried, but the adapter never handed back a tree
        ///   UpdateFactory   == 0     it activated, but nothing was ever published to it
        ///
        /// Without these the failure is indistinguishable from "the tree is wrong", which
        /// sends you rewriting node content when the messages were never arriving.
        /// </summary>
        public long WndProcMessages { get; private set; }
        public long WmGetObject { get; private set; }
        public long Activations { get; private set; }
        public long UpdateFactoryCalls { get; private set; }
        public string LastGetObjectOutcome { get; private set; }

        public bool Start()
        {
            if (IsRunning) return true;
            _instance = this;
            _ownPid = (uint)Process.GetCurrentProcess().Id;

            _hwnd = FindGameWindow();
            if (_hwnd == IntPtr.Zero)
            {
                Log("Could not locate the game window; accessibility disabled.");
                return false;
            }

            try
            {
                _adapter = AK.accesskit_windows_adapter_new(_hwnd, true, _actionKeepAlive, IntPtr.Zero);
            }
            catch (DllNotFoundException)
            {
                Log("accesskit.dll not found. Place the x64 build next to the game executable.");
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                Log("accesskit.dll is present but has an incompatible API: " + ex.Message);
                return false;
            }

            if (_adapter == IntPtr.Zero)
            {
                Log("accesskit_windows_adapter_new returned null.");
                return false;
            }

            _wndProcKeepAlive = WndProc;
            SetLastError(0);
            var newProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive);
            _prevWndProc = SetWindowProc(_hwnd, newProc);
            if (_prevWndProc == IntPtr.Zero)
            {
                Log("Failed to subclass the window procedure (error " +
                    Marshal.GetLastWin32Error() + ").");
                AK.accesskit_windows_adapter_free(_adapter);
                _adapter = IntPtr.Zero;
                return false;
            }

            IsRunning = true;
            Log($"AccessKit adapter attached to HWND 0x{_hwnd.ToInt64():X}.");

            // The window was shown, focused and inspected long before this adapter existed, so
            // any screen reader already asked WM_GETOBJECT, got nothing, and cached that answer.
            // Nothing in UIA re-asks on its own - which is why the tree only appeared after an
            // alt-tab away and back, the one act that re-fires the focus chain. This synthesises
            // that nudge: a focus WinEvent on the client object makes a screen reader re-query
            // the window it believes it already knows, and the query now reaches the adapter.
            // Only when the game is actually foreground; announcing focus for a background
            // window would yank the reader's attention to a window the user is not in.
            if (GetForegroundWindow() == _hwnd)
            {
                NotifyWinEvent(EVENT_OBJECT_FOCUS, _hwnd, OBJID_CLIENT, CHILDID_SELF);
            }
            return true;
        }

        /// <summary>
        /// Publish a snapshot. Cheap in the common case: if nothing changed since
        /// the previous frame we do not cross FFI at all, and if no assistive
        /// technology is listening AccessKit never calls the factory.
        ///
        /// The snapshot must not be mutated after this returns; A11yRuntime
        /// double-buffers for exactly that reason.
        /// </summary>
        public void Push(AccessSnapshot snapshot)
        {
            if (!IsRunning || snapshot == null) return;
            if (!_differ.Prepare(snapshot)) return;

            var events = AK.accesskit_windows_adapter_update_if_active(
                _adapter, _updateFactoryKeepAlive, IntPtr.Zero);
            if (events != IntPtr.Zero)
                AK.accesskit_windows_queued_events_raise(events);
        }

        public bool TryDequeueAction(out PendingAction action)
        {
            lock (_actionLock)
            {
                if (_actions.Count == 0) { action = default(PendingAction); return false; }
                action = _actions.Dequeue();
                return true;
            }
        }

        // ---- Native callbacks ------------------------------------------------

        private static IntPtr OnActivation(IntPtr userdata)
        {
            // An assistive technology just connected and has nothing, so it needs
            // the whole tree regardless of what changed.
            var self = _instance;
            if (self == null) return IntPtr.Zero;
            self.Activations++;
            try
            {
                self._differ.Reset();
                return self._differ.Materialize(forceFull: true);
            }
            catch (Exception ex)
            {
                self.Log("Activation handler threw: " + ex);
                // Returning null is a contract violation, so fall back to the
                // degenerate-but-valid tree rather than aborting the process.
                return self._differ.Materialize(forceFull: true);
            }
        }

        private static IntPtr OnUpdateFactory(IntPtr userdata)
        {
            var self = _instance;
            if (self == null) return IntPtr.Zero;
            self.UpdateFactoryCalls++;
            try
            {
                var update = self._differ.Materialize(forceFull: false);
                return update;
            }
            catch (Exception ex)
            {
                self.Log("Update factory threw: " + ex);
                return self._differ.Materialize(forceFull: true);
            }
        }

        private static void OnAction(IntPtr request, IntPtr userdata)
        {
            var self = _instance;
            try
            {
                if (self == null || request == IntPtr.Zero) return;

                byte action = Marshal.ReadByte(request, AK.ActionRequestLayout.Action);
                ulong target = unchecked((ulong)Marshal.ReadInt64(request, AK.ActionRequestLayout.TargetNode));

                var pending = new PendingAction
                {
                    Action = (AccessAction)action,
                    TargetNode = target,
                };

                bool hasData = Marshal.ReadByte(request, AK.ActionRequestLayout.DataHasValue) != 0;
                if (hasData)
                {
                    byte tag = Marshal.ReadByte(request, AK.ActionRequestLayout.DataTag);
                    if (tag == AK.ActionRequestLayout.TagNumericValue)
                    {
                        long bits = Marshal.ReadInt64(request, AK.ActionRequestLayout.DataPayload);
                        pending.NumericValue = BitConverter.Int64BitsToDouble(bits);
                        pending.HasNumericValue = true;
                    }
                    else if (tag == AK.ActionRequestLayout.TagValue)
                    {
                        var strPtr = Marshal.ReadIntPtr(request, AK.ActionRequestLayout.DataPayload);
                        pending.StringValue = AK.ReadUtf8(strPtr);
                    }
                }

                lock (self._actionLock) { self._actions.Enqueue(pending); }
            }
            catch (Exception ex)
            {
                self?.Log("Action handler threw: " + ex);
            }
            finally
            {
                // Ownership transferred to us; freeing is mandatory.
                if (request != IntPtr.Zero) AK.accesskit_action_request_free(request);
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            WndProcMessages++;
            try
            {
                if (msg == WM_GETOBJECT && _adapter != IntPtr.Zero)
                {
                    WmGetObject++;
                    var res = AK.accesskit_windows_adapter_handle_wm_getobject(
                        _adapter, wParam, lParam, _activationKeepAlive, IntPtr.Zero);
                    LastGetObjectOutcome = res.HasValue
                        ? "handled, lresult=0x" + res.Value.ToInt64().ToString("X")
                        : "declined (lParam=0x" + lParam.ToInt64().ToString("X") + ")";
                    if (res.HasValue) return res.Value;
                }
                else if (msg == WM_ACTIVATE && _adapter != IntPtr.Zero)
                {
                    bool focused = (wParam.ToInt64() & 0xFFFF) != 0;
                    var events = AK.accesskit_windows_adapter_update_window_focus_state(_adapter, focused);
                    if (events != IntPtr.Zero) AK.accesskit_windows_queued_events_raise(events);
                }
                else if (msg == WM_DESTROY)
                {
                    Detach();
                }
            }
            catch (Exception ex)
            {
                // An exception escaping a WndProc is undefined behaviour, so this
                // catch is load bearing rather than defensive style.
                Log("WndProc threw: " + ex);
            }

            return CallWindowProc(_prevWndProc, hWnd, msg, wParam, lParam);
        }

        // ---- Window discovery -------------------------------------------------

        private static IntPtr FindGameWindow()
        {
            // GetActiveWindow returns a handle only when called on the thread that
            // owns the window. From Unity's main thread that is exactly right.
            var h = GetActiveWindow();
            if (h != IntPtr.Zero && IsWindow(h)) return h;

            h = GetForegroundWindow();
            if (h != IntPtr.Zero && BelongsToThisProcess(h)) return h;

            IntPtr found = IntPtr.Zero;
            // Held in a local so the delegate cannot be collected mid-enumeration.
            EnumWindowsProc callback = (hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd) || !BelongsToThisProcess(hWnd)) return true;
                found = hWnd;
                return false;
            };
            EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return found;
        }

        private static bool BelongsToThisProcess(IntPtr hWnd)
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            return pid == _ownPid;
        }

        private static IntPtr SetWindowProc(IntPtr hWnd, IntPtr proc)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, GWLP_WNDPROC, proc)
                : new IntPtr(SetWindowLong32(hWnd, GWLP_WNDPROC, proc.ToInt32()));
        }

        private void Detach()
        {
            if (_prevWndProc != IntPtr.Zero && _hwnd != IntPtr.Zero && IsWindow(_hwnd))
            {
                SetWindowProc(_hwnd, _prevWndProc);
            }
            _prevWndProc = IntPtr.Zero;

            if (_adapter != IntPtr.Zero)
            {
                AK.accesskit_windows_adapter_free(_adapter);
                _adapter = IntPtr.Zero;
            }
            IsRunning = false;
        }

        public void Dispose()
        {
            Detach();
            if (_instance == this) _instance = null;
        }
    }
}
