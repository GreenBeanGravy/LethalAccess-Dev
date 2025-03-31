using GameNetcodeStuff;
using HarmonyLib;

namespace Green.LethalAccessPlugin.Patches
{
    [HarmonyPatch(typeof(PlayerControllerB))]
    public static class TerminalTogglePatch
    {
        public static bool IsTerminalActive { get; private set; } = false;

        [HarmonyPatch(typeof(Terminal))]
        public static class TerminalOpenClosePatch
        {
            [HarmonyPatch(nameof(Terminal.BeginUsingTerminal))]
            [HarmonyPostfix]
            public static void PostfixBeginUsingTerminal()
            {
                Utilities.SpeakText("Terminal opened!");
                IsTerminalActive = true;
                LethalAccess.LethalAccessPlugin.enableCustomKeybinds = !IsTerminalActive;
            }

            [HarmonyPatch(nameof(Terminal.QuitTerminal))]
            [HarmonyPostfix]
            public static void PostfixQuitTerminal()
            {
                Utilities.SpeakText("Terminal closed.");
                IsTerminalActive = false;
                LethalAccess.LethalAccessPlugin.enableCustomKeybinds = !IsTerminalActive;
            }
        }
    }
}