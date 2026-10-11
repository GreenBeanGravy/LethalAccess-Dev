using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityA11y.Core;

namespace UnityA11y.Unity
{
    /// <summary>
    /// Contract for suppressing whatever the game normally does with navigation
    /// keys. uGUI games are covered by the default implementation; games with a
    /// bespoke menu controller need a Harmony patch driven by HarmonySuppressor.
    /// </summary>
    public interface INavigationSuppressor
    {
        void Engage();
        void Release();
        /// <summary>Re-assert every frame. Games love turning navigation back on.</summary>
        void Tick();
    }

    /// <summary>
    /// Turns off the EventSystem's own navigation dispatch. This is the cleanest
    /// lever in uGUI: Move, Submit and Cancel stop being sent, but selection
    /// visuals still work because we drive selection through SetSelectedGameObject.
    /// </summary>
    public sealed class EventSystemSuppressor : INavigationSuppressor
    {
        private bool _engaged;

        public void Engage()
        {
            _engaged = true;
            Tick();
        }

        public void Tick()
        {
            if (!_engaged) return;
            var es = EventSystem.current;
            if (es != null && es.sendNavigationEvents) es.sendNavigationEvents = false;
        }

        public void Release()
        {
            _engaged = false;
            var es = EventSystem.current;
            if (es != null) es.sendNavigationEvents = true;
        }
    }

    /// <summary>
    /// Template for games that read input directly instead of going through the
    /// EventSystem. Patch the game's own navigation method with a Harmony prefix
    /// that returns false while <see cref="Active"/> is set.
    ///
    /// <code>
    /// [HarmonyPatch(typeof(GameMenuController), "HandleInput")]
    /// static bool Prefix() => !HarmonySuppressor.Active;
    /// </code>
    /// </summary>
    public static class HarmonySuppressor
    {
        public static volatile bool Active;
    }

    /// <summary>
    /// Who decides which control is focused.
    ///
    /// A screen reader never moves focus (it only observes it), so something in
    /// the process must. That is either this library (<see cref="NavigationController"/>,
    /// which takes the keyboard over) or the host game (<see cref="ExternalFocusOwner"/>,
    /// which leaves the game's own navigation alone and just reports what it picked).
    ///
    /// The split exists because taking the keyboard over is not always the right
    /// call. A game that already routes navigation through its own authored order
    /// loses that order the moment this library starts polling keys itself.
    /// </summary>
    public interface IFocusOwner
    {
        ulong CurrentId { get; }
        event Action<ulong> FocusChanged;

        void SetFocus(ulong id);
        void Reconcile(IReadOnlyList<ulong> ring, int previousIndex);
        int IndexOf(IReadOnlyList<ulong> ring);

        /// <summary>Called once per frame from the runtime's Update.</summary>
        void Tick(IUiSource source);

        void Engage();
        void Release();
    }

    /// <summary>
    /// Focus owner for hosts that keep their own navigation. Reports the host's
    /// current selection and forwards focus requests back to it; polls no keys,
    /// suppresses nothing, and never touches the EventSystem.
    ///
    /// This is what makes the library usable as a pure PROVIDER: the game keeps
    /// whatever navigation it already had (including any authored reading order
    /// the generic scanner knows nothing about), and this library restricts itself
    /// to publishing a tree and relaying the assistive technology's focus requests.
    /// </summary>
    public sealed class ExternalFocusOwner : IFocusOwner
    {
        private readonly Func<ulong> _read;
        private readonly Action<ulong> _request;
        private ulong _last;

        /// <param name="readCurrentId">
        /// Returns the node id of the host's currently focused control, or 0.
        /// </param>
        /// <param name="requestFocus">
        /// Asks the host to move focus to a node id. Called when the assistive
        /// technology issues a Focus action: NVDA's "move focus to navigator
        /// object". The host may decline; nothing here assumes it succeeded.
        /// </param>
        public ExternalFocusOwner(Func<ulong> readCurrentId, Action<ulong> requestFocus)
        {
            if (readCurrentId == null) throw new ArgumentNullException("readCurrentId");
            _read = readCurrentId;
            _request = requestFocus;
        }

        public ulong CurrentId { get { return _read(); } }

        public event Action<ulong> FocusChanged;

        public void SetFocus(ulong id)
        {
            if (_request != null) _request(id);
        }

        /// <summary>
        /// No-op: the host owns the ring, so there is no cursor of ours to repair.
        /// Whatever it focuses next is the answer.
        /// </summary>
        public void Reconcile(IReadOnlyList<ulong> ring, int previousIndex) { }

        public int IndexOf(IReadOnlyList<ulong> ring)
        {
            if (ring == null) return -1;
            ulong id = CurrentId;
            for (int i = 0; i < ring.Count; i++)
                if (ring[i] == id) return i;
            return -1;
        }

        /// <summary>
        /// Only watches for the host's selection changing, so the runtime can
        /// republish focus. Deliberately reads no input and suppresses nothing.
        /// </summary>
        public void Tick(IUiSource source)
        {
            ulong now = CurrentId;
            if (now == _last) return;
            _last = now;
            var handler = FocusChanged;
            if (handler != null) handler(now);
        }

        public void Engage() { }
        public void Release() { }
    }

    /// <summary>
    /// Owns the user's position in the focus ring and translates key presses into
    /// focus movement and control manipulation.
    ///
    /// This runs whether or not a screen reader is attached, so keyboard behaviour
    /// is identical either way. That matters: a sighted QA tester can reproduce
    /// exactly what a blind player experiences.
    ///
    /// Takes the keyboard over: it suppresses the host's navigation and polls keys
    /// itself. Hosts that already have navigation worth keeping want
    /// <see cref="ExternalFocusOwner"/> instead.
    /// </summary>
    public sealed class NavigationController : IFocusOwner
    {
        public KeyCode Next = KeyCode.DownArrow;
        public KeyCode Previous = KeyCode.UpArrow;
        public KeyCode Decrease = KeyCode.LeftArrow;
        public KeyCode Increase = KeyCode.RightArrow;
        public KeyCode Activate = KeyCode.Return;
        public KeyCode ActivateAlt = KeyCode.KeypadEnter;
        public KeyCode Back = KeyCode.Escape;
        public KeyCode First = KeyCode.Home;
        public KeyCode Last = KeyCode.End;

        /// <summary>
        /// Re-announces the focused control. Unbound by default because any
        /// sensible default collides with something in some game; set it from
        /// your plugin or expose it in config.
        /// </summary>
        public KeyCode Repeat = KeyCode.None;

        public float RepeatDelay = 0.4f;
        public float RepeatInterval = 0.06f;

        private readonly INavigationSuppressor _suppressor;
        private KeyCode[] _watched;
        private KeyCode _heldKey = KeyCode.None;
        private float _nextRepeatAt;

        public ulong CurrentId { get; private set; }
        public event Action<ulong> FocusChanged;
        public event Action BackPressed;
        public event Action RepeatRequested;

        public NavigationController(INavigationSuppressor suppressor)
        {
            _suppressor = suppressor ?? new EventSystemSuppressor();
            ApplyKeyBindings();
        }

        /// <summary>Call after changing any of the KeyCode fields.</summary>
        public void ApplyKeyBindings()
        {
            _watched = new[]
            {
                Next, Previous, Decrease, Increase,
                Activate, ActivateAlt, Back, First, Last, Repeat,
            };
        }

        public void Engage()
        {
            _suppressor.Engage();
            HarmonySuppressor.Active = true;
        }

        public void Release()
        {
            _suppressor.Release();
            HarmonySuppressor.Active = false;
        }

        /// <summary>
        /// Keeps the cursor valid across tree rebuilds. If the focused node
        /// vanished (menu closed, list refiltered) we fall back to the nearest
        /// surviving index rather than jumping to the top, which is far less
        /// disorienting mid-task.
        /// </summary>
        public void Reconcile(IReadOnlyList<ulong> ring, int previousIndex)
        {
            if (ring == null || ring.Count == 0) { CurrentId = 0; return; }
            if (CurrentId != 0)
            {
                for (int i = 0; i < ring.Count; i++)
                    if (ring[i] == CurrentId) return;
            }
            int idx = Mathf.Clamp(previousIndex, 0, ring.Count - 1);
            SetFocus(ring[idx]);
        }

        public int IndexOf(IReadOnlyList<ulong> ring)
        {
            if (ring == null) return -1;
            for (int i = 0; i < ring.Count; i++)
                if (ring[i] == CurrentId) return i;
            return -1;
        }

        public void Tick(IUiSource source)
        {
            _suppressor.Tick();
            if (source == null) return;

            var ring = source.FocusRing;
            if (ring == null || ring.Count == 0) return;

            KeyCode pressed = PollKey();
            if (pressed == KeyCode.None) return;

            int index = IndexOf(ring);
            if (index < 0) index = 0;

            if (pressed == Next)
            {
                SetFocus(ring[(index + 1) % ring.Count]);
            }
            else if (pressed == Previous)
            {
                SetFocus(ring[(index - 1 + ring.Count) % ring.Count]);
            }
            else if (pressed == First)
            {
                SetFocus(ring[0]);
            }
            else if (pressed == Last)
            {
                SetFocus(ring[ring.Count - 1]);
            }
            else if (pressed == Increase || pressed == Decrease)
            {
                int dir = pressed == Increase ? 1 : -1;
                bool adjusted = source.PerformAction(new PendingAction
                {
                    Action = dir > 0 ? AccessAction.Increment : AccessAction.Decrement,
                    TargetNode = CurrentId,
                });
                if (!adjusted)
                {
                    // Not an adjustable control, so horizontal means movement.
                    SetFocus(ring[(index + dir + ring.Count) % ring.Count]);
                }
            }
            else if (pressed == Activate || pressed == ActivateAlt)
            {
                source.PerformAction(new PendingAction
                {
                    Action = AccessAction.Click,
                    TargetNode = CurrentId,
                });
            }
            else if (pressed == Back)
            {
                BackPressed?.Invoke();
            }
            else if (pressed == Repeat)
            {
                RepeatRequested?.Invoke();
            }
        }

        public void SetFocus(ulong id)
        {
            if (id == CurrentId) return;
            CurrentId = id;
            FocusChanged?.Invoke(id);
        }

        /// <summary>
        /// Edge detection plus key repeat. Only one navigation key is considered
        /// held at a time, which avoids diagonal drift when a player rests a hand
        /// on the arrow cluster.
        /// </summary>
        private KeyCode PollKey()
        {
            var watched = _watched;
            for (int i = 0; i < watched.Length; i++)
            {
                var k = watched[i];
                if (k == KeyCode.None) continue;
                if (Input.GetKeyDown(k))
                {
                    _heldKey = k;
                    _nextRepeatAt = Time.unscaledTime + RepeatDelay;
                    return k;
                }
            }

            if (_heldKey != KeyCode.None)
            {
                if (!Input.GetKey(_heldKey))
                {
                    _heldKey = KeyCode.None;
                }
                else if (IsRepeatable(_heldKey) && Time.unscaledTime >= _nextRepeatAt)
                {
                    _nextRepeatAt = Time.unscaledTime + RepeatInterval;
                    return _heldKey;
                }
            }

            return KeyCode.None;
        }

        private bool IsRepeatable(KeyCode k) =>
            k == Next || k == Previous || k == Increase || k == Decrease;
    }
}
