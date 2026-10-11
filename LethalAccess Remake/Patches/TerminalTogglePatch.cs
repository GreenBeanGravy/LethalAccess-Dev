using HarmonyLib;
using UnityEngine;

namespace LethalAccess.Patches
{
    /// <summary>
    /// Terminal open/close state, and the greeting.
    ///
    /// The greeting used to be a POSTFIX on BeginUsingTerminal. BeginUsingTerminal calls
    /// LoadNewNode inside its own body, so by the time a postfix ran the whole welcome page had
    /// already been queued ahead of it: "Terminal opened!" arrived last, after the page it was
    /// supposed to introduce. SpeechQueue is strict FIFO with no barge-in, so ordering here is
    /// purely a question of which side of the method body we speak from. It is a prefix now.
    ///
    /// The class is no longer nested inside a PlayerControllerB patch container: it never patched
    /// anything on PlayerControllerB, and the outer [HarmonyPatch(typeof(PlayerControllerB))]
    /// only made the real target harder to see.
    /// </summary>
    [HarmonyPatch(typeof(Terminal))]
    public static class TerminalTogglePatch
    {
        public static bool IsTerminalActive { get; private set; }

        /// <summary>
        /// The terminal the local player is currently using, for the terminal-local hotkeys.
        /// Null whenever IsTerminalActive is false.
        /// </summary>
        public static Terminal ActiveTerminal { get; private set; }

        private static bool announcedHotkeyHint;

        [HarmonyPatch(nameof(Terminal.BeginUsingTerminal))]
        [HarmonyPrefix]
        public static void PrefixBeginUsingTerminal(Terminal __instance)
        {
            IsTerminalActive = true;
            ActiveTerminal = __instance;

            // Mod hotkeys are dispatched from LACore.CheckKeybinds on every keystroke, which would
            // fire them while the user types a command. They stay off for the session.
            LACore.enableCustomKeybinds = false;

            TerminalAccessPatch.InstallInputFieldLabel(__instance);
            TerminalAccessPatch.IsolateTerminalSelectables(__instance);
            TerminalAccessInput.Ensure();

            AnnouncementPolicy.Speak("Terminal opened.");

            if (!announcedHotkeyHint)
            {
                // Once per session: the two terminal-local keys are undiscoverable otherwise,
                // and repeating this on every open would be read out before every page.
                announcedHotkeyHint = true;
                AnnouncementPolicy.Speak("F1 repeats the page. F2 reads credits.");
            }
        }

        [HarmonyPatch(nameof(Terminal.QuitTerminal))]
        [HarmonyPostfix]
        public static void PostfixQuitTerminal(Terminal __instance)
        {
            IsTerminalActive = false;
            ActiveTerminal = null;
            LACore.enableCustomKeybinds = true;

            TerminalAccessPatch.RestoreTerminalSelectables(__instance);

            AnnouncementPolicy.Speak("Terminal closed.");
        }
    }
}
