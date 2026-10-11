using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LethalAccess
{
    /// <summary>
    /// The four rules every utterance in this mod obeys:
    ///
    ///   1. Never announce from a storage or plumbing method. Announce at the user-intent call
    ///      site. ("Settings saved." came from SaveSettingsToPrefs, which the game also calls
    ///      while WIPING settings and once at boot - so it fired at times the user did nothing.)
    ///   2. Every utterance must be attributable to an action the user just took, or to a screen
    ///      change. If you cannot name the action, do not speak.
    ///   3. State changes RE-ANNOUNCE THE FOCUSED CONTROL. They do not emit free-floating
    ///      sentences: "Master volume, slider, 60 percent" tells you what changed and what it is
    ///      now; "Volume changed" tells you neither.
    ///   4. A screen change announces the screen title, then the newly focused control.
    ///
    /// Everything speaks through SpeechUtils.SpeakText. The mod had two front doors -
    /// Utilities.SpeakText (no config gate, no normalisation) and SpeechUtils.SpeakText (gated on
    /// EnableAudioCues, normalises % to "percent" and $ to "dollars", collapses whitespace) - so
    /// the same string was spoken differently depending on which layer raised it. SpeechUtils is
    /// the survivor because it is the one that normalises.
    /// </summary>
    public static class AnnouncementPolicy
    {
        // A suppression scope that is never disposed would silence the mod permanently, which for
        // a blind user is the worst failure this file can have. Every legitimate scope wraps one
        // synchronous call, so anything still held after this long is a bug: release it and say
        // so rather than going quiet forever.
        private const float MaxSuppressionSeconds = 2f;

        private static int suppressDepth;
        private static float suppressStartedAt;

        /// <summary>
        /// Barge-in. READING this installs SpeechUtils's implementation if nothing has assigned one
        /// yet, so the hook can never be observed null merely because of static-initialisation
        /// order.
        ///
        /// It used to be a plain field, assigned from SpeechUtils's static constructor - and Speak
        /// below reads it BEFORE it first touches SpeechUtils. An explicit static constructor makes
        /// a type non-beforefieldinit, so its initializer does not run until the first static
        /// access: on the FIRST interrupt of every session the field was still null, the interrupt
        /// was silently downgraded to an ordinary queued utterance, and the log said "no
        /// BargeInHook is installed". The first interrupt in the codebase is the keybind rebind
        /// prompt - the one announcement that most needs to cut through. Reordering the two lines
        /// inside Speak would have fixed that single call site and left the same trap for the next
        /// reader, so the guarantee lives on the property, where every reader gets it.
        ///
        /// TODO: drop the hook and call the queue directly once SpeechQueue grows a public
        /// "drop everything pending" entry point. SpeechUtils.CancelQueued reaches its private
        /// backlog reflectively until then.
        /// </summary>
        public static Action BargeInHook
        {
            get
            {
                if (!bargeInHookResolved)
                {
                    // Set BEFORE the call: InstallBargeInHook assigns through the setter below, so
                    // the flag has to be true already or that write could re-enter this branch.
                    // A direct method call (rather than RuntimeHelpers.RunClassConstructor) is
                    // deliberate - if SpeechUtils ever loses the installer this stops compiling
                    // instead of quietly becoming a no-op and restoring the original bug.
                    bargeInHookResolved = true;
                    SpeechUtils.InstallBargeInHook();
                }

                return bargeInHook;
            }
            set
            {
                // An explicit assignment wins and is never overwritten by the lazy install above.
                bargeInHookResolved = true;
                bargeInHook = value;
            }
        }

        private static Action bargeInHook;
        private static bool bargeInHookResolved;

        private static bool warnedNoBargeIn;

        /// <summary>
        /// True while an intentional suppression scope is open. Reading this force-releases a
        /// scope that has been held implausibly long (see MaxSuppressionSeconds).
        /// </summary>
        public static bool Suppressed
        {
            get
            {
                if (suppressDepth <= 0)
                {
                    return false;
                }

                if (Time.unscaledTime - suppressStartedAt > MaxSuppressionSeconds)
                {
                    Debug.LogError("[LethalAccess] An announcement-suppression scope was held for more than " +
                                   MaxSuppressionSeconds + " s and has been force-released. Something opened " +
                                   "AnnouncementPolicy.Suppress() without disposing it; speech was silent until now.");
                    suppressDepth = 0;
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Silence announcements for the duration of a call the user did not initiate - the
        /// classic case being the settings write the game performs at boot inside
        /// PreInitSceneScript.ChooseLaunchOption. Nests.
        ///
        /// Always use it as `using (AnnouncementPolicy.Suppress()) { ... }`.
        /// </summary>
        public static IDisposable Suppress()
        {
            if (suppressDepth <= 0)
            {
                suppressDepth = 0;
                suppressStartedAt = Time.unscaledTime;
            }
            suppressDepth++;
            return new SuppressScope();
        }

        private sealed class SuppressScope : IDisposable
        {
            private bool released;

            public void Dispose()
            {
                // Disposing twice must not unbalance the count for the scope still above us.
                if (released)
                {
                    return;
                }
                released = true;

                suppressDepth--;
                if (suppressDepth < 0)
                {
                    Debug.LogError("[LethalAccess] Announcement suppression was released more times than it was " +
                                   "taken; the depth counter has been clamped to zero.");
                    suppressDepth = 0;
                }
            }
        }

        public static void Speak(string text)
        {
            Speak(text, false);
        }

        public static void Speak(string text, bool interrupt)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                return;
            }

            if (Suppressed)
            {
                return;
            }

            if (interrupt)
            {
                Action hook = BargeInHook;
                if (hook != null)
                {
                    hook();
                }
                else if (!warnedNoBargeIn)
                {
                    warnedNoBargeIn = true;
                    Debug.Log("[LethalAccess] Barge-in was requested but no BargeInHook is installed; this " +
                              "utterance is queued behind whatever is already speaking.");
                }
            }

            SpeechUtils.SpeakText(text);
        }

        /// <summary>
        /// Speak a line that replaces anything still queued from the SAME control.
        ///
        /// This is the correct tool for a repeated state re-announcement (a held arrow on a
        /// slider). The wrong tool - which this replaced - is Speak(text, interrupt: true), whose
        /// hook flushes the ENTIRE backlog and so destroys unrelated lines that happened to be
        /// queued while the slider was moving.
        /// </summary>
        public static void SpeakSuperseding(string text, int ownerInstanceId)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                return;
            }

            if (Suppressed)
            {
                return;
            }

            SpeechUtils.SpeakSuperseding(text, ownerInstanceId);
        }

        /// <summary>
        /// Rule 4. Speaks the screen title, then hands focus to the control the screen opens on;
        /// that control is announced by UIAccessibilityManager on the next Update, and the speech
        /// queue is FIFO, so the two arrive in that order.
        ///
        /// Seeding happens even while suppressed - suppression silences speech, it must never
        /// leave the keyboard without a focus.
        /// </summary>
        /// <summary>
        /// True once the accessibility tree is published and a screen reader is driving it.
        ///
        /// Only UI PROPERTY ASSEMBLY is muted by this - the focused control and the screen
        /// title, which the screen reader now composes itself from the tree. Gameplay
        /// speech (item names, hazards, "Settings saved.", pathfinding) keeps going through
        /// Tolk, because none of it is attached to a focusable control and there is no tree
        /// node for a screen reader to have spoken it from.
        ///
        /// Set from LACore only when the AccessKit bridge actually attached. If it did not,
        /// this stays false and the mod speaks exactly as it always did - the failure mode
        /// is the old behaviour, never silence.
        /// </summary>
        public static bool UiTreeOwnsSpeech;

        /// <summary>
        /// Whether the tree actually publishes a given control. Supplied by LACore once the
        /// bridge is up.
        ///
        /// The tree covers registered screens and nothing else, so treating
        /// <see cref="UiTreeOwnsSpeech"/> as a blanket switch made every control the tree does
        /// NOT publish completely silent - dropdown option lists, the belt bag, the server list,
        /// this mod's own menu - where before they were announced. A control that no one speaks
        /// for is the one failure a blind user cannot diagnose, so coverage is asked per control
        /// rather than assumed.
        /// </summary>
        public static Func<GameObject, bool> UiTreeCovers;

        /// <summary>True only when a screen reader is going to speak this exact control.</summary>
        public static bool UiTreeSpeaksFor(GameObject go)
        {
            return UiTreeOwnsSpeech && go != null && UiTreeCovers != null && UiTreeCovers(go);
        }

        public static void AnnounceScreen(string title, Transform focus)
        {
            AnnounceScreen(title, null, focus);
        }

        /// <summary>
        /// As above, with the screen's arrival instruction spoken straight after the title. The
        /// tree carries it as the screen's description instead, so on that path it is not spoken
        /// here either.
        /// </summary>
        public static void AnnounceScreen(string title, string hint, Transform focus)
        {
            // Focus is still seeded below: the tree reports where focus IS, so something
            // still has to put it there. Only the spoken title is the screen reader's job.
            if (UiTreeSpeaksFor(focus != null ? focus.gameObject : null))
            {
                if (focus != null)
                {
                    FocusManager.Seed(focus);
                }
                return;
            }

            if (!string.IsNullOrEmpty(title))
            {
                Speak(string.IsNullOrEmpty(hint) ? title : title + ". " + hint);

                // Announcements now open with their section or screen name. On a single-section
                // screen that prefix IS this title, so tell the announcer to drop it exactly once
                // rather than saying it twice in a row.
                UIAccessibilityManager.NoteScreenTitleAnnounced(title);

                // The seed below IS a focus change, and SpeechUtils barges in on focus changes -
                // so without this the title just queued would be flushed by the second half of the
                // very announcement it belongs to, and rule 4 would lose its first line. This is
                // the one case where something queued is NOT the tail of a control the user has
                // already left. One-shot and time-bounded on the SpeechUtils side.
                SpeechUtils.PreserveQueuedThroughNextFocusChange();
            }

            if (focus != null)
            {
                FocusManager.Seed(focus);
            }
        }

        /// <summary>
        /// Rule 3. After a state change, say the focused control again - label, type, new value
        /// and position - by clearing the announcer's "already said that" memo rather than
        /// composing a second, differently-worded sentence here. One announcer, one format.
        /// </summary>
        public static void ReAnnounceFocused()
        {
            if (Suppressed)
            {
                return;
            }

            // Routed through the announcer's coalescer rather than clearing the memo directly.
            // A continuous control (slider, scrollbar) can raise this ~10 times a second from a
            // held arrow key, and clearing the memo each time queues a full ~2 s utterance per
            // step - an unbounded backlog, because the speech queue deliberately drops nothing.
            // RequestReAnnounce makes its own null-focus check, so the guard that used to live
            // here would be redundant.
            UIAccessibilityManager.RequestReAnnounce();
        }
    }
}
