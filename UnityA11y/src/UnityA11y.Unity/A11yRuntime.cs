using System;
using System.Collections.Generic;
using UnityEngine;
using UnityA11y.AccessKitInterop;
using UnityA11y.Core;

namespace UnityA11y.Unity
{
    /// <summary>
    /// The driver. Owns the AccessKit bridge, the registered UI sources and the
    /// navigation controller, and pumps them from Update().
    ///
    /// Threading invariant that everything else depends on: the snapshot is only
    /// ever mutated from Update(), and AccessKit's activation handler and update
    /// factory only ever run on the window thread, which is the same thread,
    /// between Update calls. Nothing here is safe to call from a worker.
    /// </summary>
    public sealed class A11yRuntime : MonoBehaviour
    {
        /// <summary>Reserved node ids. Sources must not use these.</summary>
        public const ulong RootId = 1;
        public const ulong AnnouncerId = 2;

        public static A11yRuntime Instance { get; private set; }

        private readonly List<IUiSource> _sources = new List<IUiSource>();
        private readonly AccessSnapshot _snapshot = new AccessSnapshot();
        private readonly AccessNode _refreshScratch = new AccessNode();
        private AccessNode _announcer;
        private AccessKitBridge _bridge;
        private IFocusOwner _nav;
        private IUiSource _active;
        private int _lastRingIndex;
        private float _nextPollAt;
        private int _announceCounter;

        /// <summary>Safety-net rebuild for sources whose IsDirty is imperfect.</summary>
        public float PollInterval = 0.5f;

        /// <summary>
        /// Whether to poll the focused node every frame for value changes. This is
        /// what makes a slider announce while you hold the arrow key. Cheap: one
        /// node, not the tree.
        /// </summary>
        public bool RefreshFocusedNode = true;

        public Action<string> Log = msg => Debug.Log("[UnityA11y] " + msg);

        public IFocusOwner Focus => _nav;

        /// <summary>The built-in controller, or null when the host owns navigation.</summary>
        public NavigationController Navigation => _nav as NavigationController;

        public IUiSource ActiveSource => _active;
        public bool BridgeRunning => _bridge != null && _bridge.IsRunning;

        /// <summary>The adapter, for diagnostics. Null before Initialize.</summary>
        public AccessKitBridge Bridge => _bridge;

        /// <summary>
        /// Start the runtime with the built-in navigation controller, which TAKES OVER
        /// the keyboard: it suppresses the host's navigation and polls keys itself.
        /// Registers the generic uGUI scanner.
        /// </summary>
        public static A11yRuntime Create(INavigationSuppressor suppressor = null)
        {
            return Create(null, suppressor, registerUGuiSource: true);
        }

        /// <summary>
        /// Start the runtime as a pure provider, leaving navigation to the host.
        ///
        /// Pass an <see cref="ExternalFocusOwner"/> and, normally,
        /// <paramref name="registerUGuiSource"/> = false: a host with its own authored
        /// reading order does not want the generic scanner's geometric guess competing
        /// with it.
        ///
        /// Nothing is suppressed and no keys are polled in this mode, so the host's
        /// navigation behaves exactly as it did before this library was loaded.
        /// </summary>
        public static A11yRuntime Create(IFocusOwner focusOwner,
                                         INavigationSuppressor suppressor,
                                         bool registerUGuiSource)
        {
            if (Instance != null) return Instance;
            var go = new GameObject("UnityA11yRuntime");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            var runtime = go.AddComponent<A11yRuntime>();
            runtime.Initialize(focusOwner, suppressor, registerUGuiSource);
            return runtime;
        }

        private void Initialize(IFocusOwner focusOwner, INavigationSuppressor suppressor,
                                bool registerUGuiSource)
        {
            Instance = this;

            _bridge = new AccessKitBridge { Log = m => Log(m) };
            if (!_bridge.Start())
            {
                Log("AccessKit bridge failed to start. Navigation still works, " +
                    "but nothing will be announced.");
            }

            if (focusOwner != null)
            {
                // Host-owned navigation. Engage() is still called so a custom owner can
                // do its own setup, but the built-in suppressor is never constructed -
                // nothing here touches EventSystem.sendNavigationEvents.
                _nav = focusOwner;
            }
            else
            {
                var controller = new NavigationController(suppressor);
                controller.RepeatRequested += RepeatFocused;
                _nav = controller;
            }

            _nav.FocusChanged += OnFocusChanged;
            _nav.Engage();

            if (registerUGuiSource) Register(new UGuiSource());
        }

        public void Register(IUiSource source)
        {
            if (source == null || _sources.Contains(source)) return;
            _sources.Add(source);
            _sources.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            Log($"Registered UI source '{source.Name}' at priority {source.Priority}.");
        }

        public void Unregister(IUiSource source)
        {
            _sources.Remove(source);
            if (_active == source) _active = null;
        }

        /// <summary>Force a full rebuild on the next frame.</summary>
        public void Invalidate() => _nextPollAt = 0f;

        /// <summary>
        /// Retry attaching to the game window. Returns true once attached.
        ///
        /// Window discovery can legitimately fail on the first attempt: a mod loader runs
        /// plugin entry points during the first scene load, and GetActiveWindow returns
        /// nothing until the window is shown and the process has pumped a message. That is
        /// not a permanent error, so the host is expected to keep calling this for a while
        /// rather than concluding accessibility is unavailable.
        /// </summary>
        public bool TryAttachBridge()
        {
            if (_bridge == null) return false;
            if (_bridge.IsRunning) return true;
            return _bridge.Start();
        }

        // ---- Announcements ------------------------------------------------------

        /// <summary>
        /// Speak something that is not attached to the focused control: a quest
        /// update, a combat message, a validation error.
        ///
        /// Implemented as a UIA live region, which NVDA maps to aria-live
        /// politeness (see UIAHandler.UIALiveSettingtoNVDAAriaLivePoliteness).
        /// Assertive interrupts; polite waits for the current utterance.
        ///
        /// Speech is a strictly serial channel. Rate-limit calls to this
        /// aggressively or you will make the game unusable.
        /// </summary>
        public void Announce(string message, bool assertive = false)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (!EnsureAnnouncer())
            {
                // No tree yet, so there is no root to parent the announcer to.
                // Adding it anyway would orphan the node, and an orphaned node in
                // a TreeUpdate panics accesskit_consumer, which aborts the process.
                Log("Announce called before the first tree was built; dropped: " + message);
                return;
            }

            // Two identical consecutive messages produce the same content hash and
            // so would not be re-announced at all. Alternating a trailing space
            // forces a change. A trailing space is never spoken, unlike the
            // zero-width space you might reach for first, which some symbol levels
            // do read out.
            _announceCounter++;
            string payload = (_announceCounter & 1) == 0 ? message : message + " ";

            _announcer.Value = payload;
            _announcer.Live = assertive ? LiveSetting.Assertive : LiveSetting.Polite;
            _bridge.Push(_snapshot);
        }

        private bool EnsureAnnouncer()
        {
            if (_announcer != null && _snapshot.Contains(AnnouncerId)) return true;

            var root = _snapshot.RootId != 0 ? _snapshot.Find(_snapshot.RootId) : null;
            if (root == null) return false;

            _announcer = new AccessNode(AnnouncerId, AccessRole.Label)
            {
                Focusable = false,
                Live = LiveSetting.Polite,
            };
            _snapshot.Add(_announcer);
            if (!root.Children.Contains(AnnouncerId))
                root.Children.Add(AnnouncerId);
            return true;
        }

        /// <summary>Re-announce the focused control without moving focus.</summary>
        public void RepeatFocused()
        {
            if (_nav.CurrentId == 0) return;
            var node = _snapshot.Find(_nav.CurrentId);
            if (node == null) return;

            var parts = new List<string>(4);
            if (!string.IsNullOrEmpty(node.Label)) parts.Add(node.Label);
            if (!string.IsNullOrEmpty(node.Value)) parts.Add(node.Value);
            if (node.PositionInSet.HasValue && node.SizeOfSet.HasValue)
                parts.Add(node.PositionInSet.Value + " of " + node.SizeOfSet.Value);

            Announce(string.Join(", ", parts.ToArray()), assertive: true);
        }

        // ---- Main loop ----------------------------------------------------------

        private void Update()
        {
            var chosen = SelectSource();
            if (chosen != _active)
            {
                _active = chosen;
                _lastRingIndex = 0;
                Rebuild();
            }
            else if (_active != null && (_active.IsDirty || Time.unscaledTime >= _nextPollAt))
            {
                Rebuild();
            }

            if (_active == null) return;

            _nav.Tick(_active);
            DrainActions();

            bool changed = false;

            if (RefreshFocusedNode && _nav.CurrentId != 0)
                changed |= RefreshFocused();

            if (_snapshot.FocusId != _nav.CurrentId && _nav.CurrentId != 0 &&
                _snapshot.Contains(_nav.CurrentId))
            {
                _snapshot.FocusId = _nav.CurrentId;
                changed = true;
            }

            // Push unconditionally when something moved; the differ discards
            // no-op pushes without crossing FFI, so this is cheap.
            if (changed) _bridge.Push(_snapshot);
        }

        private IUiSource SelectSource()
        {
            for (int i = 0; i < _sources.Count; i++)
                if (_sources[i].IsActive) return _sources[i];
            return null;
        }

        /// <summary>
        /// Pull the focused control's current values without rebuilding the tree.
        /// This is the difference between a slider that announces as you drag it
        /// and one that announces half a second later.
        /// </summary>
        private bool RefreshFocused()
        {
            var live = _snapshot.Find(_nav.CurrentId);
            if (live == null) return false;

            if (!_active.TryRefreshNode(_nav.CurrentId, _refreshScratch)) return false;
            if (_refreshScratch.ContentHash() == live.ContentHash()) return false;

            // Copy in place: the node object is referenced by the snapshot and by
            // the differ's pending list, so it must not be replaced wholesale.
            live.Role = _refreshScratch.Role;
            live.Label = _refreshScratch.Label;
            live.Description = _refreshScratch.Description;
            live.Value = _refreshScratch.Value;
            live.Placeholder = _refreshScratch.Placeholder;
            live.KeyboardShortcut = _refreshScratch.KeyboardShortcut;
            live.Disabled = _refreshScratch.Disabled;
            live.Selected = _refreshScratch.Selected;
            live.Expanded = _refreshScratch.Expanded;
            live.Toggled = _refreshScratch.Toggled;
            live.Orientation = _refreshScratch.Orientation;
            live.NumericValue = _refreshScratch.NumericValue;
            live.MinNumericValue = _refreshScratch.MinNumericValue;
            live.MaxNumericValue = _refreshScratch.MaxNumericValue;
            live.NumericValueStep = _refreshScratch.NumericValueStep;
            live.PositionInSet = _refreshScratch.PositionInSet;
            live.SizeOfSet = _refreshScratch.SizeOfSet;
            live.Level = _refreshScratch.Level;
            live.HasBounds = _refreshScratch.HasBounds;
            live.X = _refreshScratch.X;
            live.Y = _refreshScratch.Y;
            live.Width = _refreshScratch.Width;
            live.Height = _refreshScratch.Height;
            live.Actions.Clear();
            live.Actions.AddRange(_refreshScratch.Actions);
            // Children are deliberately NOT copied: a change there is structural
            // and must go through a full rebuild, or the diff can orphan a node.

            return true;
        }

        private void Rebuild()
        {
            _nextPollAt = Time.unscaledTime + PollInterval;
            if (_active == null) return;

            _lastRingIndex = Math.Max(_nav.IndexOf(_active.FocusRing), 0);

            try
            {
                _snapshot.FocusId = _nav.CurrentId;
                _active.BuildSnapshot(_snapshot);
            }
            catch (Exception ex)
            {
                Log("BuildSnapshot threw, keeping the previous tree: " + ex);
                return;
            }

            // The source cleared the snapshot, so the announcer has to be re-added.
            if (_announcer != null)
            {
                _announcer = null;
                EnsureAnnouncer();
            }

            _nav.Reconcile(_active.FocusRing, _lastRingIndex);
            if (_nav.CurrentId != 0 && _snapshot.Contains(_nav.CurrentId))
                _snapshot.FocusId = _nav.CurrentId;

            _bridge.Push(_snapshot);
        }

        private void OnFocusChanged(ulong id)
        {
            _active?.OnFocusChanged(id);
            if (_snapshot.Contains(id))
            {
                _snapshot.FocusId = id;
                _bridge.Push(_snapshot);
            }
        }

        /// <summary>
        /// Actions arrive from NVDA on an RPC thread and are queued. Unity API
        /// calls are only legal on the main thread, so they are drained here.
        /// </summary>
        private void DrainActions()
        {
            PendingAction action;
            int guard = 0;
            while (_bridge.TryDequeueAction(out action) && guard++ < 32)
            {
                if (action.Action == AccessAction.Focus)
                {
                    _nav.SetFocus(action.TargetNode);
                    continue;
                }

                try
                {
                    _active.PerformAction(action);
                }
                catch (Exception ex)
                {
                    Log("PerformAction threw: " + ex);
                }
            }
        }

        private void OnDestroy()
        {
            _nav?.Release();
            _bridge?.Dispose();
            if (Instance == this) Instance = null;
        }
    }
}
