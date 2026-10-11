using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LethalAccess
{
    /// <summary>
    /// One control on a screen, in the order a screen-reader user should meet it.
    ///
    /// Resolution precedence is Direct, then Resolver, then RelativePath. Resolver is
    /// deliberately exclusive: if a Resolver is supplied and returns null the entry counts as
    /// absent rather than silently falling back to a path, because a resolver that has started
    /// returning null is a bug we want to see in the log, not paper over.
    /// </summary>
    public sealed class UIEntry
    {
        /// <summary>Path relative to the screen root. Resolved with the inactive-aware walker.</summary>
        public string RelativePath;

        /// <summary>For runtime clones and component-held references. Receives the screen root.</summary>
        public Func<Transform, Transform> Resolver;

        /// <summary>Pre-resolved instance; wins over Resolver and RelativePath.</summary>
        public Transform Direct;

        /// <summary>Authored label; wins over anything scraped off the object.</summary>
        public string Label;

        /// <summary>"button", "slider", "toggle", "dropdown", "text", ... Validated against components at resolve time.</summary>
        public string Type;

        /// <summary>Live value/state suffix, may be null.</summary>
        public Func<string> State;

        /// <summary>May be absent or inactive without a warning.</summary>
        public bool Optional;

        /// <summary>Slider/dropdown/deliberate exception: left and right keep working on this control.</summary>
        public bool KeepsHorizontal;

        /// <summary>
        /// Short, live extra the screen reader reads after the control ("Press H for help",
        /// "Also bound to Walk left"). May return null.
        /// </summary>
        public Func<string> Description;

        /// <summary>
        /// Longer explanation spoken on demand (the H key on screens that support it). May
        /// return null.
        /// </summary>
        public Func<string> Help;

        /// <summary>
        /// For a control that shows and hides other controls (a collapsible section header): true
        /// while they are shown. The tree publishes it as the expanded/collapsed state a screen
        /// reader names itself, and leaves State to the spoken fallback, so the word is not said
        /// twice. Null for everything else.
        /// </summary>
        public Func<bool> Expanded;

        public UIEntry()
        {
        }

        public UIEntry(string relativePath, string label, string type)
        {
            RelativePath = relativePath;
            Label = label;
            Type = type;
        }
    }

    /// <summary>
    /// A named run of entries. Tab moves between sections; the flat up/down order is the
    /// concatenation of the sections, never a second list, so Tab can never reach something
    /// the arrow keys cannot.
    /// </summary>
    public sealed class UISection
    {
        public string Name;
        public List<UIEntry> Entries;

        /// <summary>
        /// Walk this section in the order it appears ON SCREEN - top to bottom, then left to right
        /// for controls on the same row - rather than in the order the entries are written.
        ///
        /// Needed wherever a section is a LOGICAL grouping laid out across columns. The settings
        /// panel is the case that forced it: its Display section is authored
        /// Brightness, Fullscreen, Framerate, PixelRes, whose on-screen heights are 710, 892, 1085,
        /// 783 - so pressing Down moved the highlight UP the screen twice, and a sighted person
        /// watching a blind user navigate sees the arrows working backwards.
        ///
        /// Off by default, because authored order is right wherever it disagrees with geometry on
        /// purpose: the news popup's heading/body/Continue, and the settings Actions section, where
        /// Back and Reset sit one pixel apart on different columns and the destructive one must
        /// stay last no matter which happens to measure lower.
        /// </summary>
        public bool OrderByScreenPosition;

        /// <summary>
        /// Sections sharing a non-null PositionGroup are counted as one set for "n of m", even
        /// when other sections sit between them.
        ///
        /// For lists whose members each own a run of controls that appears directly under them -
        /// the keybinds screen's collapsible categories. Each header has to be its own section so
        /// its controls can follow it in the arrow-key order, yet the headers are one list to the
        /// user: "LethalAccess, 2 of 2", not "1 of 1" twice.
        /// </summary>
        public string PositionGroup;

        public UISection(string name, params UIEntry[] entries)
        {
            Name = name;
            Entries = new List<UIEntry>(entries ?? new UIEntry[0]);
        }
    }

    /// <summary>
    /// A screen: how to find its root, what is on it and in what order, where focus starts,
    /// and what to call it.
    /// </summary>
    public sealed class UIScreenDescriptor
    {
        public string Id;

        /// <summary>Resolve from a component reference, never GameObject.Find (active-only).</summary>
        public Func<Transform> ResolveRoot;

        public List<UISection> Sections = new List<UISection>();

        /// <summary>Null means "first visible entry".</summary>
        public Func<Transform> FocusOnOpen;

        public Func<string> Title;

        /// <summary>
        /// Optional. A short instruction read once on arrival, after the title ("Press H for help
        /// on the focused control."). Published as the screen's description rather than folded
        /// into the title, so it is not repeated wherever the title is used as context.
        /// </summary>
        public Func<string> Hint;

        /// <summary>
        /// Optional. Runs before Escape falls back to <see cref="EscapeTarget"/>. Return true when
        /// the press was used up - closing something smaller than the screen, such as a section
        /// the user expanded - and Escape must not also leave the screen.
        /// </summary>
        public Func<bool> OnEscape;

        /// <summary>
        /// Confine focus to this screen for as long as it is shown - a modal.
        ///
        /// While a trap is up, nothing outside it can take focus: another screen becoming visible
        /// registers silently underneath instead of announcing over the top, arrows and Tab stop at
        /// the trap's edges rather than walking out, and any seed aimed outside is redirected back
        /// in. The trap lifts when the screen hides, and focus then returns to whatever is beneath.
        ///
        /// This exists because a blind user cannot see that a modal is covering the screen. Without
        /// it the news pop-up left focus on the main menu behind it: the arrows read out buttons
        /// that were not reachable and the pop-up itself was never announced at all.
        /// </summary>
        public bool TrapsFocus;

        /// <summary>
        /// Optional. With <see cref="TrapsFocus"/>, confines focus to this one section rather than
        /// to the whole screen. Null traps the whole screen.
        /// </summary>
        public string TrapSection;

        /// <summary>
        /// Optional. What Escape activates on this screen, when the default - the first visible
        /// entry whose label begins with "Back" - is wrong or absent. A modal whose dismiss
        /// control is worded differently ("Continue", "Discard changes") names it here.
        /// </summary>
        public Func<Transform> EscapeTarget;

        /// <summary>
        /// A dialog: something that opened ON TOP of what the user was doing and has to be dealt
        /// with. Confirmations, pop-ups, error screens.
        ///
        /// It changes one thing - how a screen reader names this screen on arrival. An ordinary
        /// screen is a pane, whose name is read with no role after it; a dialog is announced as
        /// one, and a screen reader reads a dialog's body text before the focused control so the
        /// question arrives before the buttons. Calling the main menu a dialog is just wrong, and
        /// it costs the word its meaning when a real one appears.
        ///
        /// A screen that traps focus is a dialog by definition, so it does not also need this.
        /// </summary>
        public bool Modal;

        /// <summary>
        /// Seed focus when this screen appears, but let something else say its name.
        ///
        /// For screens whose content is still changing at the instant the root becomes visible, so
        /// the title read on arrival would be the wrong one. The pre-init scene is the case: its
        /// container switches on with the gamma panel up and then advances panels in the same
        /// frame, so announcing on show said "SET-UP. Adjust screen brightness..." about a panel
        /// the player never sees.
        ///
        /// Whatever sets this MUST announce the screen itself on every path that shows it, or the
        /// screen becomes silent.
        /// </summary>
        public bool SuppressAnnounceOnShow;

        /// <summary>
        /// Other registered screens whose controls belong to this one in the published tree.
        ///
        /// Keyboard order is untouched - a companion keeps its own up/down walk, which is the
        /// reason it exists as a separate screen (see KeybindsAccessibility.ExtrasScreenId). But
        /// the tree only publishes the ACTIVE screen, so a control on a companion was focusable
        /// and absent from the tree: the screen reader had nothing to read and fell back to the
        /// window's name. Companions are appended after this screen's own sections.
        /// </summary>
        public List<string> CompanionScreens;
    }

    /// <summary>
    /// Added to a registered screen root so the registry learns about show/hide without a
    /// Harmony patch per panel.
    ///
    /// AddComponent works on inactive GameObjects, and OnEnable fires when the panel is
    /// activated by ANY means - including the serialized GameObject.SetActive UnityEvents that
    /// switch NewsPanel, KeybindsPanel, LobbyHostSettings, PleaseConfirmChangesPanel and
    /// ConfirmKickPlayerPanel, none of which route through a patchable C# method. That is the
    /// whole reason this component exists.
    ///
    /// Note the asymmetry: adding this to an ALREADY-ACTIVE GameObject makes Unity run OnEnable
    /// immediately, so attaching to a visible panel announces it right away. Adding it to an
    /// inactive one defers until the panel is switched on, which is the common case.
    /// </summary>
    public sealed class ScreenActivationNotifier : MonoBehaviour
    {
        public string ScreenId;

        private void OnEnable()
        {
            if (!string.IsNullOrEmpty(ScreenId))
            {
                UIScreenRegistry.ScreenShown(ScreenId);
            }
        }

        private void OnDisable()
        {
            // Also fires on destroy and on scene unload; ScreenHidden tolerates both.
            if (!string.IsNullOrEmpty(ScreenId))
            {
                UIScreenRegistry.ScreenHidden(ScreenId);
            }
        }
    }

    /// <summary>
    /// The authored screen model. Everything about order, position ("n of m") and section
    /// membership comes from here, so that no part of the mod ever infers order from
    /// transform.GetSiblingIndex() or from geometry - sibling order is draw order in this game
    /// and does not track visual order (the news popup is heading=8, Continue=9, body=10 while
    /// the visual order is heading, body, Continue).
    /// </summary>
    public static class UIScreenRegistry
    {
        private sealed class Resolved
        {
            public UIEntry Entry;
            public Transform T;
            public int SectionIndex;
            public string SectionName;
            public int FlatIndex;
            public int InstanceId;
            public ScreenState Owner;

            // What "n of m" counts by: SectionIndex, or the first section of its PositionGroup.
            public int PositionKey;
        }

        private sealed class ScreenState
        {
            public UIScreenDescriptor Descriptor;
            public readonly List<Resolved> Flat = new List<Resolved>();
            public int LastShownFrame = -1;
            public bool HasValidated;

            // Shown, but its content is still changing - see SuppressAnnounceOnShow. Controls on it
            // stay silent until whoever owns the screen calls MarkSettled.
            public bool AwaitingSettle;
        }

        private static readonly Dictionary<string, ScreenState> screens =
            new Dictionary<string, ScreenState>();

        // Instance id -> entry. This is the arrow-key hot path: one dictionary lookup, no sweep.
        private static readonly Dictionary<int, Resolved> claimed = new Dictionary<int, Resolved>();

        // Show order, so hiding a sub-panel falls back to whatever was underneath it.
        private static readonly List<string> shownStack = new List<string>();

        private static string activeScreenId;

        // ---- Focus trap ------------------------------------------------------------------
        // Which screen currently owns focus exclusively, and optionally which section of it.
        // trapSectionIndex is -1 for "the whole screen".
        private static string trapScreenId;
        private static int trapSectionIndex = -1;

        /// <summary>The screen focus is currently confined to, or null when focus is free.</summary>
        public static string TrappedScreenId
        {
            get { return trapScreenId; }
        }

        /// <summary>
        /// Confine focus to <paramref name="screenId"/>, optionally to one section of it.
        ///
        /// Normally set from <see cref="UIScreenDescriptor.TrapsFocus"/> when the screen is shown.
        /// Call directly only for a trap that does not correspond to a whole screen becoming
        /// visible; release it with <see cref="ReleaseFocusTrap"/> or the keyboard stays confined.
        /// </summary>
        public static void TrapFocus(string screenId, string sectionName)
        {
            ScreenState s;
            if (string.IsNullOrEmpty(screenId) || !screens.TryGetValue(screenId, out s))
            {
                Debug.LogError("[LethalAccess] TrapFocus('" + screenId + "'): no such screen is registered, " +
                               "so focus was NOT confined. A modal will be navigable straight through.");
                return;
            }

            trapScreenId = screenId;
            trapSectionIndex = -1;

            if (!string.IsNullOrEmpty(sectionName))
            {
                List<UISection> sections = s.Descriptor.Sections;
                for (int i = 0; sections != null && i < sections.Count; i++)
                {
                    if (sections[i] != null && sections[i].Name == sectionName)
                    {
                        trapSectionIndex = i;
                        break;
                    }
                }

                if (trapSectionIndex < 0)
                {
                    // Confining to the whole screen is the safe direction to fail: too much reach,
                    // never too little. Silently trapping an empty section would strand the user.
                    Debug.LogError("[LethalAccess] TrapFocus('" + screenId + "', section '" + sectionName +
                                   "'): that screen has no section by that name. Focus is confined to the " +
                                   "whole screen instead.");
                }
            }
        }

        /// <summary>
        /// This screen's content has stopped changing: its controls may be announced again.
        ///
        /// Call immediately BEFORE announcing a screen that set
        /// <see cref="UIScreenDescriptor.SuppressAnnounceOnShow"/>, so the announcement itself and
        /// the focus seed that follows it are not swallowed by the mute.
        /// </summary>
        public static void MarkSettled(string screenId)
        {
            ScreenState s;
            if (!string.IsNullOrEmpty(screenId) && screens.TryGetValue(screenId, out s))
            {
                s.AwaitingSettle = false;
            }
        }

        /// <summary>
        /// True when this control belongs to a screen that is shown but not yet settled, so
        /// describing it now would read a half-built screen out to the user.
        /// </summary>
        public static bool IsAwaitingSettle(GameObject go)
        {
            Resolved r;
            if (go == null || !TryGetResolved(go, out r) || r.Owner == null)
            {
                return false;
            }
            return r.Owner.AwaitingSettle;
        }

        /// <summary>Let focus leave the trapped screen again. Safe to call when nothing is trapped.</summary>
        public static void ReleaseFocusTrap()
        {
            trapScreenId = null;
            trapSectionIndex = -1;
        }

        /// <summary>
        /// True when focus is confined and <paramref name="go"/> is outside the confinement, so a
        /// caller about to focus it should not.
        /// </summary>
        public static bool TrapRejects(GameObject go)
        {
            if (string.IsNullOrEmpty(trapScreenId) || go == null)
            {
                return false;
            }

            Resolved r;
            if (!TryGetResolved(go, out r) || r.Owner == null)
            {
                // Not described by any screen. While a modal is up, that is by definition something
                // behind it.
                return true;
            }

            if (r.Owner.Descriptor == null || r.Owner.Descriptor.Id != trapScreenId)
            {
                return true;
            }

            return trapSectionIndex >= 0 && r.SectionIndex != trapSectionIndex;
        }

        /// <summary>
        /// Where focus belongs inside the trap right now, or null when nothing is trapped or the
        /// trapped screen has no visible entry left.
        /// </summary>
        public static Transform TrapFocusTarget()
        {
            if (string.IsNullOrEmpty(trapScreenId))
            {
                return null;
            }

            ScreenState s;
            if (!screens.TryGetValue(trapScreenId, out s))
            {
                return null;
            }

            for (int i = 0; i < s.Flat.Count; i++)
            {
                if (trapSectionIndex >= 0 && s.Flat[i].SectionIndex != trapSectionIndex)
                {
                    continue;
                }
                if (IsVisible(s.Flat[i]))
                {
                    return s.Flat[i].T;
                }
            }
            return null;
        }

        /// <summary>True while a trap is up and this screen is not the one holding it.</summary>
        private static bool TrappedElsewhere(string id)
        {
            if (string.IsNullOrEmpty(trapScreenId) || trapScreenId == id)
            {
                return false;
            }

            // A trap whose screen has gone invisible without a hide notification would otherwise
            // deadlock the keyboard, so verify the trapped screen is still really on screen.
            ScreenState s;
            if (!screens.TryGetValue(trapScreenId, out s) || s.Descriptor.ResolveRoot == null)
            {
                ReleaseFocusTrap();
                return false;
            }

            Transform root = s.Descriptor.ResolveRoot();
            if (root == null || !UIUtils.IsUIElementVisible(root.gameObject))
            {
                ReleaseFocusTrap();
                return false;
            }

            return true;
        }

        /// <summary>The most recently shown registered screen that is still visible, or null.</summary>
        public static string ActiveScreenId
        {
            get { return activeScreenId; }
        }

        #region Registration

        public static void Register(UIScreenDescriptor d)
        {
            if (d == null)
            {
                Debug.LogError("[LethalAccess] UIScreenRegistry.Register called with a null descriptor.");
                return;
            }
            if (string.IsNullOrEmpty(d.Id))
            {
                Debug.LogError("[LethalAccess] UIScreenRegistry.Register: descriptor has no Id.");
                return;
            }
            if (d.ResolveRoot == null)
            {
                Debug.LogError("[LethalAccess] UIScreenRegistry.Register('" + d.Id + "'): ResolveRoot is null. " +
                               "A screen must know how to find its own root from a component reference.");
                return;
            }
            if (d.Sections == null || d.Sections.Count == 0)
            {
                Debug.LogError("[LethalAccess] UIScreenRegistry.Register('" + d.Id + "'): no sections. " +
                               "A screen with no authored entries would silently fall back to geometry.");
                return;
            }

            if (screens.ContainsKey(d.Id))
            {
                Debug.Log("[LethalAccess] UIScreenRegistry: replacing existing registration for '" + d.Id + "'.");
                DropClaims(screens[d.Id]);
            }

            screens[d.Id] = new ScreenState { Descriptor = d };

            // Attach now if the root already exists. If it does not, the owner is expected to
            // call AttachActivationNotifier once it does (or to call ScreenShown itself).
            Transform root = d.ResolveRoot();
            if (root != null)
            {
                AttachActivationNotifier(d.Id, root.gameObject);
            }
            else
            {
                Debug.Log("[LethalAccess] UIScreenRegistry: '" + d.Id + "' registered but its root does not exist yet; " +
                          "call AttachActivationNotifier when it does.");
            }
        }

        public static void Unregister(string id)
        {
            ScreenState s;
            if (string.IsNullOrEmpty(id) || !screens.TryGetValue(id, out s))
            {
                return;
            }

            Transform root = s.Descriptor.ResolveRoot();
            if (root != null)
            {
                ScreenActivationNotifier notifier = root.GetComponent<ScreenActivationNotifier>();
                if (notifier != null && notifier.ScreenId == id)
                {
                    // Otherwise the orphaned notifier keeps calling ScreenShown for a dead id.
                    UnityEngine.Object.Destroy(notifier);
                }
            }

            DropClaims(s);
            screens.Remove(id);
            shownStack.Remove(id);
            if (activeScreenId == id)
            {
                activeScreenId = TopVisibleInStack();
            }
        }

        /// <summary>
        /// Add (or retarget) the show/hide notifier on a screen root. Safe on inactive roots.
        /// </summary>
        public static void AttachActivationNotifier(string screenId, GameObject root)
        {
            if (string.IsNullOrEmpty(screenId))
            {
                Debug.LogError("[LethalAccess] AttachActivationNotifier called with no screen id.");
                return;
            }
            if (root == null)
            {
                Debug.LogError("[LethalAccess] AttachActivationNotifier('" + screenId + "') called with a null root.");
                return;
            }
            if (!screens.ContainsKey(screenId))
            {
                Debug.LogError("[LethalAccess] AttachActivationNotifier('" + screenId + "'): no screen with that id is " +
                               "registered. Register the descriptor first.");
                return;
            }

            ScreenActivationNotifier notifier = root.GetComponent<ScreenActivationNotifier>();
            if (notifier != null)
            {
                if (notifier.ScreenId != screenId)
                {
                    Debug.LogError("[LethalAccess] Root '" + root.name + "' already notifies for screen '" +
                                   notifier.ScreenId + "'; refusing to retarget it to '" + screenId +
                                   "'. Two screens cannot share one root.");
                }
                return;
            }

            // AddComponent on an ACTIVE GameObject runs OnEnable synchronously, INSIDE the
            // AddComponent call - i.e. before the next line can assign ScreenId. OnEnable therefore
            // saw an empty id, took its no-op branch, and the screen was never shown.
            //
            // This was a live defect, not a hypothetical: the settings panel registered correctly
            // (its descriptor was in `screens`) but ActiveScreenId stayed empty and not one of its
            // controls was ever claimed, so every announcement fell through to the old sibling
            // heuristic - "Back, button, 1 of 8", the exact defect the descriptor exists to fix.
            // The pre-init screen was unaffected only because its root is INACTIVE at registration,
            // which defers OnEnable until the id is already set.
            //
            // Assigning the id first is not possible (the component does not exist yet), so the
            // activation is driven explicitly here instead. ScreenShown de-duplicates within a
            // frame, so if OnEnable does fire correctly later nothing is announced twice.
            notifier = root.AddComponent<ScreenActivationNotifier>();
            notifier.ScreenId = screenId;

            if (root.activeInHierarchy)
            {
                ScreenShown(screenId);
            }
        }

        /// <summary>
        /// Force the next query to re-resolve this screen. Call after anything that destroys and
        /// rebuilds a screen's controls without hiding the screen (keybind "Set to defaults",
        /// lobby list refresh, dropdown option clones). The alternative would be a per-frame
        /// sweep, which this whole design exists to avoid.
        /// </summary>
        public static void Invalidate(string id)
        {
            ScreenState s;
            if (string.IsNullOrEmpty(id) || !screens.TryGetValue(id, out s))
            {
                return;
            }
            Resolve(s);
        }

        #endregion

        #region Show / hide

        public static void ScreenShown(string id)
        {
            ScreenState s;
            if (!screens.TryGetValue(id, out s))
            {
                Debug.LogWarning("[LethalAccess] ScreenShown('" + id + "'): no screen with that id is registered.");
                return;
            }

            // A panel can be announced twice in one frame - the notifier's OnEnable plus a
            // Harmony postfix on whatever opened it. Announcing the screen twice is worse than
            // missing the second call, and a genuine hide/show pair clears this stamp.
            if (s.LastShownFrame == Time.frameCount)
            {
                return;
            }
            s.LastShownFrame = Time.frameCount;

            Resolve(s);

            shownStack.Remove(id);
            shownStack.Add(id);

            // A screen appearing UNDERNEATH a modal is registered and stacked, so it is ready the
            // moment the modal closes - but it must not announce itself or take focus. This is not
            // hypothetical: MenuManager.Start activates NewsPanel and is also postfixed by the
            // main-menu registration, so both land on one frame. Without this the main menu won on
            // arrival order, the pop-up was never announced, and the arrows read out main-menu
            // buttons the modal was covering.
            if (TrappedElsewhere(id))
            {
                Debug.Log("[LethalAccess] Screen '" + id + "' became visible while '" + trapScreenId +
                          "' holds focus; registered underneath without announcing.");
                return;
            }

            activeScreenId = id;

            FocusManager.NoteScreenVisible(id);

            if (s.Descriptor.TrapsFocus)
            {
                TrapFocus(id, s.Descriptor.TrapSection);
            }

            Transform focus = ResolveFocusTarget(id);
            if (focus == null)
            {
                Debug.LogWarning("[LethalAccess] Screen '" + id + "' became visible but has no visible entry to focus. " +
                                 "Either the descriptor's paths are stale or the panel is empty.");
            }

            if (s.Descriptor.SuppressAnnounceOnShow)
            {
                // Neither announced NOR seeded, and its controls are muted until it settles.
                //
                // Seeding here is not harmless: "the first visible entry" is computed against the
                // unsettled screen, so focus landed on the gamma panel's Confirm. Muting matters
                // just as much, because the GAME selects a control of its own here
                // (SelectUIForGamepad.OnEnable) and the announcer would describe it - carrying the
                // unsettled screen's title as its context prefix, which is the exact line this flag
                // exists to suppress. MarkSettled lifts both.
                s.AwaitingSettle = true;
                return;
            }

            s.AwaitingSettle = false;

            string title = s.Descriptor.Title != null ? s.Descriptor.Title() : null;
            AnnouncementPolicy.AnnounceScreen(title, ScreenHint(id), focus);
        }

        public static void ScreenHidden(string id)
        {
            ScreenState s;
            if (!screens.TryGetValue(id, out s))
            {
                return;
            }

            // Some panels are toggled off and straight back on inside one frame; clearing the
            // stamp keeps the re-show from being swallowed by the duplicate guard above.
            s.LastShownFrame = -1;

            shownStack.Remove(id);

            bool releasedTrap = false;
            if (trapScreenId == id)
            {
                ReleaseFocusTrap();
                releasedTrap = true;
            }

            if (activeScreenId == id)
            {
                activeScreenId = TopVisibleInStack();
            }

            if (!releasedTrap)
            {
                // Deliberately does NOT re-announce whatever is underneath. The game usually
                // leaves the EventSystem pointing at a destroyed object here, and FocusManager's
                // watchdog re-seeds and announces it - one recovery path, not two competing ones.
                return;
            }

            // A trap is the exception. The screen underneath was deliberately kept silent while the
            // modal was up, so it has never been announced and focus was never seeded into it -
            // there is nothing for the watchdog to recover TO. Hand focus back explicitly.
            string beneath = activeScreenId;
            if (string.IsNullOrEmpty(beneath))
            {
                return;
            }

            ScreenState under;
            if (!screens.TryGetValue(beneath, out under))
            {
                return;
            }

            Transform focus = ResolveFocusTarget(beneath);
            if (focus == null)
            {
                Debug.LogWarning("[LethalAccess] '" + id + "' released focus but '" + beneath +
                                 "' underneath has no visible entry to hand it to. FocusManager's " +
                                 "watchdog is the only remaining recovery.");
                return;
            }

            string title = under.Descriptor.Title != null ? under.Descriptor.Title() : null;
            AnnouncementPolicy.AnnounceScreen(title, ScreenHint(beneath), focus);
        }

        private static string TopVisibleInStack()
        {
            for (int i = shownStack.Count - 1; i >= 0; i--)
            {
                ScreenState s;
                if (!screens.TryGetValue(shownStack[i], out s))
                {
                    continue;
                }
                Transform root = s.Descriptor.ResolveRoot();
                if (root != null && UIUtils.IsUIElementVisible(root.gameObject))
                {
                    return shownStack[i];
                }
            }
            return null;
        }

        /// <summary>
        /// Where focus belongs on this screen right now: FocusOnOpen if it resolves to something
        /// visible, otherwise the first visible entry. Returns null if the screen itself is not
        /// visible. Used by ScreenShown and by FocusManager's focus-loss watchdog, so both take
        /// exactly the same decision.
        /// </summary>
        public static Transform ResolveFocusTarget(string screenId)
        {
            ScreenState s;
            if (string.IsNullOrEmpty(screenId) || !screens.TryGetValue(screenId, out s))
            {
                return null;
            }

            Transform root = s.Descriptor.ResolveRoot();
            if (root == null || !UIUtils.IsUIElementVisible(root.gameObject))
            {
                return null;
            }

            if (s.Descriptor.FocusOnOpen != null)
            {
                Transform authored = s.Descriptor.FocusOnOpen();
                if (authored != null && UIUtils.IsUIElementVisible(authored.gameObject))
                {
                    return authored;
                }
                if (authored != null)
                {
                    Debug.Log("[LethalAccess] Screen '" + screenId + "': FocusOnOpen resolved to '" + authored.name +
                              "' but it is not visible; falling back to the first visible entry.");
                }
            }

            for (int i = 0; i < s.Flat.Count; i++)
            {
                if (IsVisible(s.Flat[i]))
                {
                    return s.Flat[i].T;
                }
            }
            return null;
        }

        #endregion

        #region Queries

        /// <summary>
        /// Position of this control WITHIN ITS SECTION, counting only currently-visible entries.
        /// index is 1-BASED, so it can be announced as "{index} of {count}" with no arithmetic
        /// at the call site. Returns false when the object is not claimed, or is itself not
        /// visible - announcing a position we cannot stand behind is worse than announcing none.
        /// </summary>
        /// <summary>
        /// One visible control on the active screen, flattened for an external consumer.
        ///
        /// Exists so an accessibility-tree provider can publish exactly the model the
        /// announcer speaks from, rather than re-deriving order and position by its own
        /// rules and drifting out of step with it.
        /// </summary>
        public struct UIEntryView
        {
            public Transform Target;
            public UIEntry Entry;
            public string SectionName;
            public int SectionIndex;

            /// <summary>1-based within the section, counting only visible entries.</summary>
            public int IndexInSection;

            /// <summary>Visible entries in this section.</summary>
            public int SectionCount;
        }

        /// <summary>
        /// The active screen's visible controls, in authored order, with the SAME
        /// visibility test and per-section numbering <see cref="TryGetPosition"/> uses.
        ///
        /// While a focus trap is up this reports the TRAPPED screen instead of the
        /// nominally active one, and only the trapped section when the trap is scoped to
        /// one. That is what carries modal semantics into a published tree: a screen
        /// reader exploring the tree must not be able to reach what the keyboard cannot.
        /// </summary>
        /// <returns>False when no screen is active or it has no visible entry.</returns>
        public static bool TryEnumerateActiveScreen(List<UIEntryView> into, out string screenId, out string title)
        {
            screenId = null;
            title = null;
            if (into == null)
            {
                return false;
            }
            into.Clear();

            string id = !string.IsNullOrEmpty(trapScreenId) ? trapScreenId : ActiveScreenId;
            ScreenState s;
            if (string.IsNullOrEmpty(id) || !screens.TryGetValue(id, out s))
            {
                return false;
            }

            bool sectionScoped = id == trapScreenId && trapSectionIndex >= 0;

            // Two passes so SectionCount is known before any view is emitted; a consumer
            // that needs "n of m" cannot use a count that is still being accumulated.
            //
            // Counted by PositionKey, and reported as the view's SectionIndex: sections of one
            // PositionGroup are one set, and when they are adjacent they are one group as well.
            Dictionary<int, int> counts = new Dictionary<int, int>();
            for (int i = 0; i < s.Flat.Count; i++)
            {
                Resolved e = s.Flat[i];
                if (sectionScoped && e.SectionIndex != trapSectionIndex) continue;
                if (!IsVisible(e)) continue;
                int c;
                counts[e.PositionKey] = counts.TryGetValue(e.PositionKey, out c) ? c + 1 : 1;
            }

            Dictionary<int, int> seen = new Dictionary<int, int>();
            for (int i = 0; i < s.Flat.Count; i++)
            {
                Resolved e = s.Flat[i];
                if (sectionScoped && e.SectionIndex != trapSectionIndex) continue;
                if (!IsVisible(e)) continue;

                int n;
                n = seen.TryGetValue(e.PositionKey, out n) ? n + 1 : 1;
                seen[e.PositionKey] = n;

                UIEntryView v = new UIEntryView();
                v.Target = e.T;
                v.Entry = e.Entry;
                v.SectionName = e.SectionName;
                v.SectionIndex = e.PositionKey;
                v.IndexInSection = n;
                v.SectionCount = counts[e.PositionKey];
                into.Add(v);
            }

            // Companion screens publish after the screen's own sections, with section indices
            // offset far enough that they can never collide with one of its own. Not under a
            // section-scoped trap: a trapped section is, by definition, all there is to reach.
            List<string> companions = s.Descriptor.CompanionScreens;
            if (companions != null && !sectionScoped)
            {
                for (int k = 0; k < companions.Count; k++)
                {
                    ScreenState c;
                    if (!screens.TryGetValue(companions[k], out c)) continue;

                    int offset = (k + 1) * 1000;
                    Dictionary<int, int> cCounts = new Dictionary<int, int>();
                    for (int i = 0; i < c.Flat.Count; i++)
                    {
                        if (!IsVisible(c.Flat[i])) continue;
                        int idx = c.Flat[i].PositionKey + offset;
                        int cc;
                        cCounts[idx] = cCounts.TryGetValue(idx, out cc) ? cc + 1 : 1;
                    }

                    Dictionary<int, int> cSeen = new Dictionary<int, int>();
                    for (int i = 0; i < c.Flat.Count; i++)
                    {
                        Resolved e = c.Flat[i];
                        if (!IsVisible(e)) continue;

                        int idx = e.PositionKey + offset;
                        int n;
                        n = cSeen.TryGetValue(idx, out n) ? n + 1 : 1;
                        cSeen[idx] = n;

                        UIEntryView v = new UIEntryView();
                        v.Target = e.T;
                        v.Entry = e.Entry;
                        v.SectionName = e.SectionName;
                        v.SectionIndex = idx;
                        v.IndexInSection = n;
                        v.SectionCount = cCounts[idx];
                        into.Add(v);
                    }
                }
            }

            if (into.Count == 0)
            {
                return false;
            }

            screenId = id;
            title = s.Descriptor.Title != null ? s.Descriptor.Title() : null;
            return true;
        }

        /// <summary>
        /// Whether this screen should be announced as a dialog. True when it says so, and when it
        /// traps focus - a screen nothing can be focused outside of IS a modal, whatever it called
        /// itself.
        /// </summary>
        public static bool IsModalScreen(string screenId)
        {
            ScreenState s;
            if (string.IsNullOrEmpty(screenId) || !screens.TryGetValue(screenId, out s) ||
                s.Descriptor == null)
            {
                return false;
            }
            return s.Descriptor.Modal || s.Descriptor.TrapsFocus;
        }

        /// <summary>The screen's arrival instruction, or null.</summary>
        public static string ScreenHint(string screenId)
        {
            ScreenState s;
            if (string.IsNullOrEmpty(screenId) || !screens.TryGetValue(screenId, out s) ||
                s.Descriptor == null || s.Descriptor.Hint == null)
            {
                return null;
            }
            return s.Descriptor.Hint();
        }

        /// <summary>
        /// Give the screen first refusal of an Escape press. True when its OnEscape used the press
        /// up, in which case the caller must do nothing else with it.
        /// </summary>
        public static bool TryHandleEscape(string screenId)
        {
            ScreenState s;
            if (string.IsNullOrEmpty(screenId) || !screens.TryGetValue(screenId, out s) ||
                s.Descriptor == null || s.Descriptor.OnEscape == null)
            {
                return false;
            }
            return s.Descriptor.OnEscape();
        }

        /// <summary>
        /// The control Escape should activate on this screen: the authored EscapeTarget when one
        /// is set, otherwise the first visible entry whose label begins with "Back". Null when
        /// the screen has neither, which callers must treat as "Escape does nothing here".
        /// </summary>
        public static Transform FindBackControl(string screenId)
        {
            ScreenState s;
            if (string.IsNullOrEmpty(screenId) || !screens.TryGetValue(screenId, out s))
            {
                return null;
            }

            if (s.Descriptor != null && s.Descriptor.EscapeTarget != null)
            {
                return s.Descriptor.EscapeTarget();
            }

            for (int i = 0; i < s.Flat.Count; i++)
            {
                Resolved e = s.Flat[i];
                string label = e.Entry != null ? e.Entry.Label : null;
                if (!string.IsNullOrEmpty(label) &&
                    label.StartsWith("Back", StringComparison.OrdinalIgnoreCase) &&
                    IsVisible(e))
                {
                    return e.T;
                }
            }
            return null;
        }

        /// <summary>How many sections currently have at least one visible entry.</summary>
        public static int VisibleSectionCount(string screenId)
        {
            ScreenState s;
            if (string.IsNullOrEmpty(screenId) || !screens.TryGetValue(screenId, out s))
            {
                return 0;
            }
            HashSet<int> live = new HashSet<int>();
            for (int i = 0; i < s.Flat.Count; i++)
            {
                if (IsVisible(s.Flat[i])) live.Add(s.Flat[i].PositionKey);
            }
            return live.Count;
        }

        public static bool TryGetPosition(GameObject go, out int index, out int count, out string sectionName)
        {
            index = 0;
            count = 0;
            sectionName = null;

            Resolved r;
            if (!TryGetResolved(go, out r))
            {
                return false;
            }

            List<Resolved> flat = r.Owner.Flat;
            int visible = 0;
            int mine = 0;
            for (int i = 0; i < flat.Count; i++)
            {
                Resolved e = flat[i];
                if (e.PositionKey != r.PositionKey || !IsVisible(e))
                {
                    continue;
                }
                visible++;
                if (e == r)
                {
                    mine = visible;
                }
            }

            if (mine == 0)
            {
                return false;
            }

            index = mine;
            count = visible;
            sectionName = r.SectionName;
            return true;
        }

        /// <summary>
        /// The context word an announcement is prefixed with: "Audio, Microphone, on, button, 1 of 4".
        ///
        /// Section name when the screen has more than one section, screen title when it has one.
        /// A single-section screen's section name is an internal grouping label ("Main", "Buttons")
        /// that means nothing spoken aloud, whereas its title is what the user calls the screen -
        /// which is why the main menu reads "Main Menu, Host…" and the settings panel reads
        /// "Audio, Microphone…" rather than both using the same field.
        ///
        /// Returns false for anything this registry does not claim, so legacy path-registered
        /// elements fall back to the active screen's title rather than inventing a context.
        /// </summary>
        public static bool TryGetAnnouncementPrefix(GameObject go, out string prefix)
        {
            prefix = null;

            Resolved r;
            if (!TryGetResolved(go, out r))
            {
                return false;
            }

            ScreenState owner = r.Owner;

            if (owner.Descriptor.Sections != null && owner.Descriptor.Sections.Count > 1 &&
                !string.IsNullOrEmpty(r.SectionName))
            {
                prefix = r.SectionName;
                return true;
            }

            if (owner.Descriptor.Title != null)
            {
                prefix = owner.Descriptor.Title();
            }

            if (string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(r.SectionName))
            {
                // A screen with one unnamed-ish section and no title is a registration bug, but a
                // section name is still better context than silence.
                prefix = r.SectionName;
            }

            return !string.IsNullOrEmpty(prefix);
        }

        /// <summary>
        /// Title of the screen currently shown, for elements this registry does not claim.
        /// </summary>
        public static string ActiveScreenTitle()
        {
            ScreenState s;
            if (string.IsNullOrEmpty(ActiveScreenId) || !screens.TryGetValue(ActiveScreenId, out s))
            {
                return null;
            }

            return s.Descriptor.Title != null ? s.Descriptor.Title() : null;
        }

        public static bool TryGetEntry(GameObject go, out UIEntry entry)
        {
            Resolved r;
            if (!TryGetResolved(go, out r))
            {
                entry = null;
                return false;
            }
            entry = r.Entry;
            return true;
        }

        /// <summary>
        /// The next authored neighbour in flat order: +1 = down, -1 = up. Skips entries that are
        /// not currently visible. Does NOT wrap - the top and bottom of a screen are dead ends,
        /// which is what a screen-reader user expects from arrow keys.
        /// </summary>
        public static Transform NextInFlatOrder(GameObject from, int delta)
        {
            Resolved r;
            if (delta == 0 || !TryGetResolved(from, out r))
            {
                return null;
            }

            // Under a trap the arrows must not walk out of it. Returning null stops the move
            // exactly the way the top and bottom of an authored order already do, so the keypress
            // is absorbed rather than jumping somewhere the user cannot see they have gone.
            bool trapped = !string.IsNullOrEmpty(trapScreenId);
            if (trapped && TrapRejects(from))
            {
                return null;
            }

            List<Resolved> flat = r.Owner.Flat;
            int step = delta > 0 ? 1 : -1;
            int remaining = Mathf.Abs(delta);
            int i = r.FlatIndex;

            // Walks from the entry's position in the FULL list, so this still works when the
            // focused control has itself become invisible.
            while (remaining > 0)
            {
                i += step;
                if (i < 0 || i >= flat.Count)
                {
                    return null;
                }
                if (trapped && trapSectionIndex >= 0 && flat[i].SectionIndex != trapSectionIndex)
                {
                    // Section-scoped trap: the section's first and last entries are the ends of the
                    // world, even though the screen has more below and above them.
                    return null;
                }
                if (IsVisible(flat[i]))
                {
                    remaining--;
                }
            }
            return flat[i].T;
        }

        /// <summary>
        /// First visible entry of the next/previous section that has any visible entries.
        /// Wraps, because Tab is a cycling key and every arrival announces its section name, so
        /// a wrap is audible rather than disorienting. Returns null when the screen has only one
        /// section with visible entries - there is nowhere else to go.
        /// </summary>
        public static Transform NextSection(GameObject from, int delta)
        {
            Resolved r;
            if (delta == 0 || !TryGetResolved(from, out r))
            {
                return null;
            }

            if (!string.IsNullOrEmpty(trapScreenId))
            {
                // Tab is a way out of a section, so under a section-scoped trap it must do nothing.
                // A whole-screen trap still allows Tab between that screen's own sections.
                if (trapSectionIndex >= 0 || TrapRejects(from))
                {
                    return null;
                }
            }

            List<Resolved> flat = r.Owner.Flat;

            // Section indices that currently have at least one visible entry, in authored order.
            List<int> live = new List<int>();
            for (int i = 0; i < flat.Count; i++)
            {
                if (!IsVisible(flat[i]))
                {
                    continue;
                }
                int si = flat[i].SectionIndex;
                if (!live.Contains(si))
                {
                    live.Add(si);
                }
            }

            if (live.Count <= 1)
            {
                return null;
            }

            int here = live.IndexOf(r.SectionIndex);
            if (here < 0)
            {
                // Focused entry is invisible; treat the nearest following live section as "here".
                here = 0;
                for (int i = 0; i < live.Count; i++)
                {
                    if (live[i] >= r.SectionIndex)
                    {
                        here = i;
                        break;
                    }
                }
            }

            int target = here + (delta > 0 ? 1 : -1) * Mathf.Abs(delta);
            target = ((target % live.Count) + live.Count) % live.Count;
            int targetSection = live[target];

            for (int i = 0; i < flat.Count; i++)
            {
                if (flat[i].SectionIndex == targetSection && IsVisible(flat[i]))
                {
                    return flat[i].T;
                }
            }
            return null;
        }

        /// <summary>
        /// Every currently-visible entry of a screen, in authored flat order. Pass null for the
        /// active screen. Allocates - not for the arrow-key path.
        /// </summary>
        public static List<Transform> VisibleFlatOrder(string screenId)
        {
            List<Transform> result = new List<Transform>();

            string id = string.IsNullOrEmpty(screenId) ? activeScreenId : screenId;
            ScreenState s;
            if (string.IsNullOrEmpty(id) || !screens.TryGetValue(id, out s))
            {
                return result;
            }

            for (int i = 0; i < s.Flat.Count; i++)
            {
                if (IsVisible(s.Flat[i]))
                {
                    result.Add(s.Flat[i].T);
                }
            }
            return result;
        }

        /// <summary>Arrow-key hot path: does this control keep left/right?</summary>
        public static bool KeepsHorizontal(GameObject go)
        {
            Resolved r;
            return TryGetResolved(go, out r) && r.Entry.KeepsHorizontal;
        }

        /// <summary>Arrow-key hot path: is this control part of an authored screen order?</summary>
        public static bool ClaimsSelectable(GameObject go)
        {
            Resolved r;
            return TryGetResolved(go, out r);
        }

        private static bool TryGetResolved(GameObject go, out Resolved r)
        {
            r = null;
            if (go == null)
            {
                return false;
            }
            return claimed.TryGetValue(go.GetInstanceID(), out r);
        }

        private static bool IsVisible(Resolved r)
        {
            return r.T != null && UIUtils.IsUIElementVisible(r.T.gameObject);
        }

        #endregion

        #region Resolution

        private static void DropClaims(ScreenState s)
        {
            for (int i = 0; i < s.Flat.Count; i++)
            {
                Resolved r;
                if (claimed.TryGetValue(s.Flat[i].InstanceId, out r) && r.Owner == s)
                {
                    claimed.Remove(s.Flat[i].InstanceId);
                }
            }
            s.Flat.Clear();
        }

        private static void Resolve(ScreenState s)
        {
            DropClaims(s);

            UIScreenDescriptor d = s.Descriptor;
            Transform root = d.ResolveRoot();
            if (root == null)
            {
                Debug.LogWarning("[LethalAccess] Screen '" + d.Id + "': root did not resolve; no entries are claimed.");
                return;
            }

            bool validate = !s.HasValidated;
            s.HasValidated = true;

            int flatIndex = 0;
            var sectionItems = new List<Resolved>();
            Dictionary<string, int> firstOfGroup = null;
            for (int si = 0; si < d.Sections.Count; si++)
            {
                UISection section = d.Sections[si];
                if (section == null || section.Entries == null)
                {
                    Debug.LogError("[LethalAccess] Screen '" + d.Id + "' has a null section at index " + si + ".");
                    continue;
                }

                int positionKey = si;
                if (!string.IsNullOrEmpty(section.PositionGroup))
                {
                    if (firstOfGroup == null)
                    {
                        firstOfGroup = new Dictionary<string, int>(StringComparer.Ordinal);
                    }
                    int first;
                    if (firstOfGroup.TryGetValue(section.PositionGroup, out first))
                    {
                        positionKey = first;
                    }
                    else
                    {
                        firstOfGroup[section.PositionGroup] = si;
                    }
                }

                for (int ei = 0; ei < section.Entries.Count; ei++)
                {
                    UIEntry entry = section.Entries[ei];
                    if (entry == null)
                    {
                        Debug.LogError("[LethalAccess] Screen '" + d.Id + "', section '" + section.Name +
                                       "' has a null entry at index " + ei + ".");
                        continue;
                    }

                    Transform t = ResolveEntry(entry, root);
                    if (t == null)
                    {
                        if (!entry.Optional)
                        {
                            // Absent is a real error - the entry can never be reached. This
                            // is distinct from "resolved but inactive", reported below.
                            Debug.LogError("[LethalAccess] Screen '" + d.Id + "', section '" + section.Name +
                                           "': entry '" + DescribeEntry(entry) + "' did not resolve. It is " +
                                           "unreachable and its label, type and position are inactive.");
                        }
                        continue;
                    }

                    // Every claimed entry must be focusable, or the authored order would have
                    // holes that arrow keys dead-end on. Headings and body copy legitimately have
                    // no Selectable of their own, so one is injected (inert: no transition, no
                    // target graphic).
                    if (t.GetComponent<Selectable>() == null)
                    {
                        FocusInjection.MakeFocusable(t.gameObject);
                    }

                    Resolved r = new Resolved
                    {
                        Entry = entry,
                        T = t,
                        SectionIndex = si,
                        SectionName = section.Name,
                        InstanceId = t.gameObject.GetInstanceID(),
                        Owner = s,
                        PositionKey = positionKey
                    };

                    Resolved clash;
                    if (claimed.TryGetValue(r.InstanceId, out clash))
                    {
                        if (clash.Owner != s)
                        {
                            Debug.LogError("[LethalAccess] '" + t.name + "' is claimed by both screen '" +
                                           clash.Owner.Descriptor.Id + "' and screen '" + d.Id +
                                           "'. The later registration wins; position announcements for the " +
                                           "other screen will be wrong.");
                        }
                        else
                        {
                            // Two entries of one screen resolved to the same object: the earlier
                            // one can never be focused or counted, so the authored order silently
                            // loses a step.
                            Debug.LogError("[LethalAccess] Screen '" + d.Id + "': entries '" +
                                           DescribeEntry(clash.Entry) + "' and '" + DescribeEntry(entry) +
                                           "' both resolve to '" + t.name + "'. Only the later one is reachable.");
                        }
                    }
                    claimed[r.InstanceId] = r;
                    sectionItems.Add(r);

                    if (validate)
                    {
                        ValidateResolvedEntry(d.Id, section.Name, entry, t);
                    }
                }

                if (section.OrderByScreenPosition)
                {
                    SortByScreenPosition(d.Id, section.Name, sectionItems);
                }

                // FlatIndex is assigned AFTER any reorder, so up/down, "n of m" and Tab all agree
                // on one order. Assigning it at construction time was what made the sort silently
                // partial.
                for (int k = 0; k < sectionItems.Count; k++)
                {
                    sectionItems[k].FlatIndex = flatIndex++;
                    s.Flat.Add(sectionItems[k]);
                }
                sectionItems.Clear();
            }
        }

        /// <summary>Rows within this many screen pixels count as the same row and read left to right.</summary>
        private const float SameRowPixels = 20f;

        private static void SortByScreenPosition(string screenId, string sectionName, List<Resolved> items)
        {
            if (items.Count < 2)
            {
                return;
            }

            var y = new Dictionary<Resolved, Vector2>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                Vector2 c;
                if (!TryGetScreenCenter(items[i].T, out c))
                {
                    // One unmeasurable entry makes the whole order arbitrary, so authored order is
                    // kept rather than half-sorted - and it is said out loud, because a section that
                    // silently ignores its own ordering flag is very hard to notice.
                    Debug.LogWarning("[LethalAccess] Screen '" + screenId + "', section '" + sectionName +
                                     "': '" + items[i].T.name + "' has no RectTransform, so the section " +
                                     "cannot be ordered by screen position. Authored order kept.");
                    return;
                }
                y[items[i]] = c;
            }

            items.Sort(delegate (Resolved a, Resolved b)
            {
                Vector2 ca = y[a];
                Vector2 cb = y[b];

                // Screen space puts Y=0 at the BOTTOM, so "higher on screen" is a LARGER y and the
                // comparison is deliberately reversed.
                if (Mathf.Abs(ca.y - cb.y) > SameRowPixels)
                {
                    return cb.y.CompareTo(ca.y);
                }
                return ca.x.CompareTo(cb.x);
            });
        }

        private static bool TryGetScreenCenter(Transform t, out Vector2 center)
        {
            center = Vector2.zero;

            RectTransform rt = t as RectTransform;
            if (rt == null)
            {
                return false;
            }

            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);

            // World corners on a Screen Space Overlay canvas already ARE screen coordinates; any
            // other render mode has to go through the canvas camera.
            Canvas canvas = t.GetComponentInParent<Canvas>();
            Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                ? canvas.worldCamera
                : null;

            Vector3 a = cam == null ? corners[0] : cam.WorldToScreenPoint(corners[0]);
            Vector3 b = cam == null ? corners[2] : cam.WorldToScreenPoint(corners[2]);

            center = new Vector2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f);
            return true;
        }

        private static Transform ResolveEntry(UIEntry e, Transform root)
        {
            if (e.Direct != null)
            {
                return e.Direct;
            }
            if (e.Resolver != null)
            {
                return e.Resolver(root);
            }
            if (!string.IsNullOrEmpty(e.RelativePath))
            {
                // Inactive-aware: Transform.Find traverses inactive children, GameObject.Find
                // does not, which is why every previous declarative registration for a sub-panel
                // silently resolved to nothing.
                return UIUtils.FindByPathIncludingInactive(root, e.RelativePath);
            }
            return null;
        }

        private static string DescribeEntry(UIEntry e)
        {
            if (!string.IsNullOrEmpty(e.Label))
            {
                return e.Label;
            }
            if (!string.IsNullOrEmpty(e.RelativePath))
            {
                return e.RelativePath;
            }
            return e.Resolver != null ? "<resolver>" : "<empty entry>";
        }

        /// <summary>
        /// First-show validation. "Absent" is reported by the caller; here we report the
        /// two quieter failures: resolved-but-inactive, and a declared Type with no matching
        /// component - the latter is what let "checkbox"/"toggle" be registered on plain Buttons
        /// and announced as something the control is not.
        /// </summary>
        private static void ValidateResolvedEntry(string screenId, string sectionName, UIEntry entry, Transform t)
        {
            if (!entry.Optional && !t.gameObject.activeInHierarchy)
            {
                Debug.Log("[LethalAccess] Screen '" + screenId + "', section '" + sectionName + "': entry '" +
                          DescribeEntry(entry) + "' resolved but is inactive. Fine if the panel is closed; " +
                          "a bug if it should be on screen.");
            }

            if (string.IsNullOrEmpty(entry.Type))
            {
                return;
            }

            GameObject go = t.gameObject;
            bool ok;
            switch (entry.Type.ToLowerInvariant())
            {
                case "button":
                    ok = go.GetComponent<Button>() != null;
                    break;
                case "slider":
                    ok = go.GetComponent<Slider>() != null;
                    break;
                case "toggle":
                case "checkbox":
                    ok = go.GetComponent<Toggle>() != null;
                    break;
                case "dropdown":
                    ok = go.GetComponent<Dropdown>() != null || go.GetComponent<TMP_Dropdown>() != null;
                    break;
                case "input field":
                    ok = go.GetComponent<InputField>() != null || go.GetComponent<TMP_InputField>() != null;
                    break;
                case "scroll area":
                    ok = go.GetComponent<ScrollRect>() != null;
                    break;
                default:
                    // Free-form types ("heading", "text", "row", ...) are labels, not claims
                    // about components, so there is nothing to check.
                    return;
            }

            if (!ok)
            {
                Debug.LogError("[LethalAccess] Screen '" + screenId + "', section '" + sectionName + "': entry '" +
                               DescribeEntry(entry) + "' declares type '" + entry.Type + "' but '" + go.name +
                               "' has no matching component. It will be announced as something it is not.");
            }
        }

        #endregion
    }
}
