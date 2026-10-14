using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace LethalAccess.Patches
{
    /// <summary>
    /// Everything the terminal says, and the three separate reasons it said the welcome page
    /// three times over.
    ///
    /// The three readings of the welcome page came from three independent places:
    ///   1. the LoadNewNode postfix speaking all of Terminal.currentText,
    ///   2. selectTextFieldDelayed selecting screenText one second later, at which point the
    ///      announcer read the input field's own text (the whole buffer) AND appended
    ///      ", text: " + the buffer again,
    ///   3. TerminalTogglePatch's greeting, which was a POSTFIX on BeginUsingTerminal and so
    ///      queued behind the page the method body had already spoken.
    ///
    /// (1) is fixed here by speaking only the delta. (3) is fixed in TerminalTogglePatch by
    /// making the greeting a prefix. (2) is reduced to a single short "Terminal command input,
    /// input field" by registering a custom text provider for the field. See
    /// InstallInputFieldLabel. Full suppression would need a check inside the announcer's private
    /// AnnounceUIElement; the provider route uses only its public API and needs no such change.
    /// </summary>
    [HarmonyPatch(typeof(Terminal))]
    public static class TerminalAccessPatch
    {
        /// <summary>
        /// Shortest run of characters we will accept as proof that the tail of the previous page
        /// is the head of the new buffer. Too short and an incidental "\n\n" match would swallow
        /// real content; too long and a short page never matches and gets re-read. 16 is well
        /// above the length of any punctuation run the terminal emits.
        /// </summary>
        private const int MinOverlapChars = 16;

        /// <summary>Whole text of the page currently on screen, for the "repeat page" key.</summary>
        public static string LastPageText { get; private set; }

        /// <summary>
        /// Set by the LoadNewNode postfix, read by the OnSubmit postfix. If OnSubmit ran without
        /// LoadNewNode running inside it, ParsePlayerSentence returned null and the game produced
        /// no output at all, and the user heard nothing.
        /// </summary>
        private static bool loadedNodeDuringSubmit;

        // Terminal.broadcastedCodeThisFrame is private. It is the only way to distinguish "you
        // typed a door code and it worked" (which produces no node, deliberately) from "the
        // terminal did not understand you".
        private static readonly AccessTools.FieldRef<Terminal, bool> BroadcastedCodeRef =
            AccessTools.FieldRefAccess<Terminal, bool>("broadcastedCodeThisFrame");

        #region Page speech

        [HarmonyPatch(nameof(Terminal.LoadNewNode))]
        [HarmonyPrefix]
        public static void PrefixLoadNewNode(Terminal __instance, ref string __state)
        {
            // Captured BEFORE the method overwrites it; this is the page the user has heard.
            __state = __instance.currentText;
        }

        [HarmonyPatch(nameof(Terminal.LoadNewNode))]
        [HarmonyPostfix]
        public static void PostfixLoadNewNode(Terminal __instance, TerminalNode node, string __state)
        {
            loadedNodeDuringSubmit = true;

            string full = __instance.currentText;
            if (string.IsNullOrEmpty(full))
            {
                return;
            }

            LastPageText = full;

            // clearPreviousText wipes the buffer, so everything in it is new.
            string toSpeak = (node == null || node.clearPreviousText)
                ? full
                : SpeakableDelta(__state, full);

            if (string.IsNullOrWhiteSpace(toSpeak))
            {
                // The overlap ate everything, which means the node appended nothing visible.
                // Falling back to the node's own text is the safe direction: a repeated sentence
                // is recoverable, a command that answers with silence is not.
                toSpeak = node != null ? node.displayText : null;
            }

            if (!string.IsNullOrWhiteSpace(toSpeak))
            {
                AnnouncementPolicy.Speak(toSpeak.Trim());
            }
        }

        /// <summary>
        /// The part of <paramref name="full"/> the user has not heard yet.
        ///
        /// When clearPreviousText is false, LoadNewNode builds "\n\n" + previous + "\n\n" + new
        /// and then keeps only the LAST 250 characters. So whatever survives of the old page is a
        /// suffix of ("\n\n" + previous) sitting at the START of the new buffer. The longest
        /// prefix of the new buffer that is a suffix of that string is therefore exactly the
        /// already-heard region, and everything after it is new. This is what stops up to 250
        /// characters being re-read on every appending page.
        ///
        /// Returns the whole buffer when no overlap of at least MinOverlapChars is found: if the
        /// two pages genuinely share nothing, repeating is correct, and if TextPostProcess has
        /// rewritten the old region beyond recognition then repeating is still better than
        /// guessing a cut point and losing content.
        /// </summary>
        private static string SpeakableDelta(string previous, string full)
        {
            if (string.IsNullOrEmpty(full))
            {
                return string.Empty;
            }
            if (string.IsNullOrEmpty(previous))
            {
                return full;
            }

            string haystack = "\n\n" + previous;
            int max = Mathf.Min(full.Length, haystack.Length);

            for (int len = max; len >= MinOverlapChars; len--)
            {
                if (string.CompareOrdinal(haystack, haystack.Length - len, full, 0, len) == 0)
                {
                    return full.Substring(len);
                }
            }

            return full;
        }

        #endregion

        #region Parse failure reporting

        // Public only because it appears in the signature of the public Harmony patch methods
        // below; C# refuses a public method whose parameter type is less accessible.
        public struct SubmitState
        {
            public int TextAdded;
            public string Typed;
            public TerminalNode Node;
        }

        [HarmonyPatch(nameof(Terminal.OnSubmit))]
        [HarmonyPrefix]
        public static void PrefixOnSubmit(Terminal __instance, ref SubmitState __state)
        {
            loadedNodeDuringSubmit = false;

            __state = default(SubmitState);
            __state.TextAdded = __instance.textAdded;
            __state.Node = __instance.currentNode;

            // Same slice ParsePlayerSentence takes. Read here because OnSubmit truncates the
            // buffer and zeroes textAdded on the failure path, so by the postfix it is gone.
            if (__instance.textAdded > 0 &&
                __instance.screenText != null &&
                __instance.screenText.text != null &&
                __instance.screenText.text.Length >= __instance.textAdded)
            {
                string t = __instance.screenText.text;
                __state.Typed = t.Substring(t.Length - __instance.textAdded).Trim();
            }
        }

        [HarmonyPatch(nameof(Terminal.OnSubmit))]
        [HarmonyPostfix]
        public static void PostfixOnSubmit(Terminal __instance, SubmitState __state)
        {
            if (!__instance.terminalInUse)
            {
                return;
            }

            // Enter on an empty line: the game only re-focuses the field. Nothing happened, so
            // announcing anything here would be an utterance with no user action behind it.
            if (__state.TextAdded == 0)
            {
                return;
            }

            // A node loaded, so the LoadNewNode postfix has already read the result.
            if (loadedNodeDuringSubmit)
            {
                return;
            }

            // Door and turret codes deliberately produce no node; they broadcast and play an
            // effect. Without this branch every code would be reported as "not recognised".
            if (BroadcastedCodeRef(__instance))
            {
                AnnouncementPolicy.Speak("Code transmitted.");
                return;
            }

            string typed = string.IsNullOrEmpty(__state.Typed) ? "that" : __state.Typed;

            // Override options are the confirm/deny prompts of every purchase, and the whole set
            // of valid answers is on the node, so say what they are instead of just "no".
            if (__state.Node != null && __state.Node.overrideOptions)
            {
                string options = ListOverrideWords(__state.Node);
                if (!string.IsNullOrEmpty(options))
                {
                    AnnouncementPolicy.Speak("Not an option here: " + typed + ". Valid answers: " + options + ".");
                    return;
                }

                AnnouncementPolicy.Speak("Not an option here: " + typed + ".");
                return;
            }

            AnnouncementPolicy.Speak("Command not recognised: " + typed + ".");
        }

        private static string ListOverrideWords(TerminalNode node)
        {
            if (node.terminalOptions == null)
            {
                return null;
            }

            List<string> words = new List<string>(node.terminalOptions.Length);
            for (int i = 0; i < node.terminalOptions.Length; i++)
            {
                CompatibleNoun option = node.terminalOptions[i];
                if (option == null || option.noun == null || string.IsNullOrEmpty(option.noun.word))
                {
                    continue;
                }
                if (!words.Contains(option.noun.word))
                {
                    words.Add(option.noun.word);
                }
            }

            return words.Count == 0 ? null : string.Join(", ", words.ToArray());
        }

        #endregion

        #region Input-field label and navigation lockdown

        /// <summary>
        /// The Terminal these two latches were set for, NOT a bool.
        ///
        /// Both used to be process-wide bools, and nothing clears them on scene teardown:
        /// Terminal.QuitTerminal is reached only from PressESC (guarded on terminalInUse), from
        /// BeginUsingTerminal's own catch, and from StartOfRound.playersFiredClientRpc, so every
        /// other way a round can end (disconnect, host quit, any return to the menu) destroys the
        /// Terminal with the latches still set. The next round's Terminal was then never isolated
        /// and never labelled, and quitting THAT one wrote the PREVIOUS Terminal's stashed
        /// Navigation onto it.
        ///
        /// Comparison is ReferenceEquals, never Unity's ==, for two reasons. Unity's operator
        /// answers "equal" for ANY two destroyed objects (CompareBaseObjects returns true as soon
        /// as both sides fail the native-alive test), so a destroyed stash could match a different
        /// destroyed Terminal; and a `stashedFor != null` style check would report "nothing is
        /// stashed" the instant the scene unloaded, which is exactly the leak being fixed here.
        /// ReferenceEquals asks the only question that matters ("is this the same CLR object")
        /// and is unaffected by native liveness. The separate "is this object usable" question is
        /// still asked with Unity's == below, because a destroyed component must not be written to.
        ///
        /// Keying on the instance also closes a second hole for free: because a restore can now
        /// only ever run against the very Terminal the stash was taken from, terminal.screenText
        /// and terminal.scrollBarVertical cannot have changed between stash and restore, so the
        /// restore can never write an uninitialised Navigation (whose mode is None) onto a control
        /// that was absent when the stash was taken.
        /// </summary>
        private static Terminal labelInstalledFor;
        private static Terminal stashedFor;
        private static Navigation stashedInputNavigation;
        private static Navigation stashedScrollNavigation;

        /// <summary>
        /// Give the command field a short label of its own.
        ///
        /// Without this, the announcer falls back to GetTextFromComponent (the whole screen
        /// buffer) and then EnhanceTextWithState's input-field branch appends ", text: " plus the
        /// same buffer, so focusing the field reads the entire page twice in one utterance. A
        /// custom text provider is consulted before either of those, so one short sentence wins.
        ///
        /// The path is taken from the live object rather than written down, because the terminal
        /// screen's hierarchy is not something this file can verify.
        /// </summary>
        public static void InstallInputFieldLabel(Terminal terminal)
        {
            // Unity ==: a destroyed Terminal reports null and must not be dereferenced.
            if (terminal == null)
            {
                return;
            }

            // ReferenceEquals: see the note on the latch fields. This is "have we already
            // registered a provider for THIS Terminal", not "has one ever been registered".
            if (ReferenceEquals(labelInstalledFor, terminal))
            {
                return;
            }

            if (terminal.screenText == null)
            {
                Debug.LogError("[LethalAccess] Terminal has no screenText input field, so no command-field " +
                               "label could be registered; focusing it will read the whole page twice.");
                return;
            }

            string path = Utilities.GetGameObjectPath(terminal.screenText.gameObject);
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("[LethalAccess] Terminal: could not resolve a hierarchy path for the command " +
                               "input field, so focusing it will still read the whole page twice.");
                // Deliberately NOT latched: a failed attempt must not disable every later one.
                return;
            }

            List<Func<string>> providers = new List<Func<string>>();
            providers.Add(new Func<string>(() => "Terminal command"));
            LACore.SetUISpeech(path, providers);

            // Latched only on success, and only for this instance. LACore.SetUISpeech is keyed by
            // hierarchy path and is never cleared, so re-registering for a Terminal whose path is
            // unchanged is an idempotent dictionary write; re-registering matters when the new
            // Terminal sits at a different path, which is precisely the case the old bool lost.
            labelInstalledFor = terminal;
        }

        /// <summary>
        /// Take the two terminal Selectables out of navigation while the terminal is open.
        ///
        /// THE ORIGINAL REASON GIVEN HERE WAS WRONG. It claimed "an arrow key that escapes the
        /// field lands on the scrollbar". Arrow keys do NOT escape a focused TMP_InputField under
        /// InputSystemUIInputModule: live-tested on the host menu's ServerNameField, where two Left
        /// presses and a Down press all left focus on the field with isFocused still true. The
        /// field is focused for the whole session (BeginUsingTerminal calls ActivateInputField
        /// immediately and Select a second later), so that escape route does not exist.
        ///
        /// The isolation is kept for a different and still-valid reason, which is in the Unity
        /// source rather than inferred: Selectable.FindSelectable skips every candidate whose
        /// navigation.mode is None (decompiled UnityEngine.UI/Selectable.cs, the
        /// `selectable2.navigation.mode == Navigation.Mode.None` continue at line 452), and the
        /// array it walks (s_Selectables) is a single process-wide list of every enabled
        /// Selectable, not a per-canvas one. Mode.None is therefore the only thing that keeps the
        /// terminal's scrollbar from being an arrow TARGET, and stops it being a silent focus stop
        /// if anything selects it: a Scrollbar announces nothing, and landing on one costs the user
        /// the caret with no indication that focus moved. That risk is not hypothetical during the
        /// open sequence, because selectTextFieldDelayed yields for a full second before calling
        /// screenText.Select(), leaving a window in which the field does not hold focus.
        ///
        /// Observed in a hosted ship 2026-10-14 (LAN, fresh save): before the terminal had ever
        /// been opened, the input field and the vertical scrollbar both already had
        /// navigation.mode == None, so the scene ships them that way and this isolation changes
        /// nothing in this build. With the terminal open and 80 lines of text, the scrollbar goes
        /// active (AutoHideAndExpandViewport), joins the Selectable list, and none of the field's
        /// four FindSelectableOn* calls returned it or anything else; focus stayed on the field.
        /// Only a mouse click can select the scrollbar. The pair is kept because it costs nothing
        /// and protects against a game update that authors Automatic navigation there.
        ///
        /// The authored Navigation is stashed and put back on quit, because these are scene
        /// objects that outlive the terminal session.
        /// </summary>
        public static void IsolateTerminalSelectables(Terminal terminal)
        {
            // Unity ==: a destroyed Terminal reports null and must not be dereferenced.
            if (terminal == null)
            {
                return;
            }

            // ReferenceEquals: see the note on the latch fields. A Terminal from a previous round
            // that was destroyed with its scene still sitting in stashedFor must NOT suppress the
            // stash for this one, or this terminal is never isolated and quitting it later writes
            // the dead terminal's Navigation onto live controls.
            if (ReferenceEquals(stashedFor, terminal))
            {
                return;
            }

            TMP_InputField field = terminal.screenText;
            Scrollbar bar = terminal.scrollBarVertical;

            if (field == null && bar == null)
            {
                Debug.LogError("[LethalAccess] Terminal has neither a screenText input field nor a vertical " +
                               "scrollbar; the terminal scrollbar cannot be taken out of arrow navigation, so " +
                               "focus may land on it unannounced.");
                return;
            }

            if (field != null)
            {
                stashedInputNavigation = field.navigation;
                NavigationEnforcer.Isolate(field);
            }
            if (bar != null)
            {
                stashedScrollNavigation = bar.navigation;
                NavigationEnforcer.Isolate(bar);
            }

            stashedFor = terminal;
        }

        public static void RestoreTerminalSelectables(Terminal terminal)
        {
            // ReferenceEquals both times. The first call rejects a genuinely absent argument
            // without consulting native liveness; the second is the identity test that stops this
            // method writing one Terminal's stashed Navigation onto a different Terminal's
            // controls, which is what happened when the latch was a process-wide bool.
            if (ReferenceEquals(terminal, null) || !ReferenceEquals(stashedFor, terminal))
            {
                return;
            }

            stashedFor = null;

            // Unity == from here on: if the Terminal is mid-teardown its components report null and
            // writing to them would throw. Clearing stashedFor above already happened, so a
            // destroyed terminal releases the latch instead of leaking it into the next round.
            if (terminal.screenText != null)
            {
                terminal.screenText.navigation = stashedInputNavigation;
            }
            if (terminal.scrollBarVertical != null)
            {
                terminal.scrollBarVertical.navigation = stashedScrollNavigation;
            }

            // A Navigation holds selectOnUp/Down/Left/Right Selectable references. Dropping them
            // keeps a finished round's scene objects from being reachable from a static field.
            stashedInputNavigation = default(Navigation);
            stashedScrollNavigation = default(Navigation);
        }

        #endregion
    }

    /// <summary>
    /// The terminal's own two hotkeys.
    ///
    /// LACore.CheckKeybinds is switched off for the whole time the terminal is open (otherwise
    /// every mod hotkey would fire while the user types a command), so a re-read has to come from
    /// somewhere else. This component runs only while TerminalTogglePatch.IsTerminalActive and
    /// reads the keyboard directly.
    ///
    /// F1 and F2 are hard-coded rather than registered with LACore.RegisterKeybind, because
    /// registered binds are dispatched from CheckKeybinds, the very thing that is disabled here.
    /// They are function keys specifically so that they cannot end up inside the command the user
    /// is typing. Making them rebindable needs terminal-scoped entries in ConfigManager, which
    /// would have to be dispatched from this component rather than from CheckKeybinds.
    /// </summary>
    public class TerminalAccessInput : MonoBehaviour
    {
        private static TerminalAccessInput instance;

        public static void Ensure()
        {
            if (instance != null)
            {
                return;
            }

            GameObject host = LACore.Instance != null ? LACore.Instance.gameObject : null;
            if (host == null)
            {
                // Nothing has initialised the mod yet, which should be impossible by the time a
                // terminal exists; say so rather than silently losing the hotkeys.
                Debug.LogError("[LethalAccess] Terminal hotkeys could not be installed: LACore.Instance is null, " +
                               "so there is no component host. Repeat-page and credits keys are unavailable.");
                return;
            }

            instance = host.GetComponent<TerminalAccessInput>();
            if (instance == null)
            {
                instance = host.AddComponent<TerminalAccessInput>();
            }
        }

        private void Update()
        {
            if (!TerminalTogglePatch.IsTerminalActive)
            {
                return;
            }

            Keyboard kb = Keyboard.current;
            if (kb == null)
            {
                return;
            }

            if (kb.f1Key.wasPressedThisFrame)
            {
                string page = TerminalAccessPatch.LastPageText;
                if (string.IsNullOrEmpty(page))
                {
                    AnnouncementPolicy.Speak("No terminal page to repeat.");
                }
                else
                {
                    AnnouncementPolicy.Speak(page.Trim());
                }
            }

            if (kb.f2Key.wasPressedThisFrame)
            {
                Terminal terminal = TerminalTogglePatch.ActiveTerminal;
                if (terminal == null || terminal.topRightText == null)
                {
                    // topRightText is where the game writes "$<credits>"; without it we would be
                    // inventing a number, which is worse than admitting we cannot read it.
                    AnnouncementPolicy.Speak("Credit display unavailable.");
                    return;
                }

                string credits = terminal.topRightText.text;
                AnnouncementPolicy.Speak(string.IsNullOrEmpty(credits)
                    ? "Credit display is blank."
                    : "Credits: " + credits);
            }
        }
    }
}
