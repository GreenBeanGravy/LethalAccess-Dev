using System.Runtime.CompilerServices;
using BepInEx;
using LethalCompanyInputUtils.Api;
using UnityEngine.InputSystem;

namespace LethalAccess
{
    /// <summary>
    /// The only code in the mod that names an InputUtils type. Reached solely through
    /// ModKeybinds.Initialize after it has confirmed InputUtils is loaded; on an install without it
    /// these methods are never compiled, so the missing assembly is never looked for.
    /// </summary>
    internal static class InputUtilsKeybinds
    {
        // Held for the lifetime of the game: InputUtils keeps its own reference, and treats a second
        // instance of the same actions class as a bug.
        private static LethalAccessInputActions actions;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Register(BepInPlugin plugin)
        {
            if (actions != null)
            {
                return;
            }

            actions = new LethalAccessInputActions(plugin);

            for (int i = 0; i < ModKeybinds.Definitions.Count; i++)
            {
                ModKeybinds.Definition d = ModKeybinds.Definitions[i];
                d.Action = actions.Asset.FindAction(d.Id);
            }
        }
    }

    /// <summary>
    /// LethalAccess's keys as an InputUtils action map. InputUtils groups the remap screen by the
    /// owning plugin's name, so these appear under "LethalAccess", one row per key, with a keyboard
    /// and a gamepad column like every other row.
    /// </summary>
    internal sealed class LethalAccessInputActions : LcInputActions
    {
        internal LethalAccessInputActions(BepInPlugin plugin) : base(plugin)
        {
        }

        // Part of the file InputUtils saves the binds to (BepInEx/config/controls/<guid>.Keybinds.json).
        protected override string MapName
        {
            get { return "Keybinds"; }
        }

        // Called from the base constructor, so it may use only static state.
        public override void CreateInputActions(in InputActionMapBuilder builder)
        {
            for (int i = 0; i < ModKeybinds.Definitions.Count; i++)
            {
                ModKeybinds.Definition d = ModKeybinds.Definitions[i];
                if (!d.InTable)
                {
                    continue;
                }

                builder.NewActionBinding()
                    .WithActionId(d.Id)
                    .WithActionType(InputActionType.Button)
                    .WithBindingName(d.Name)
                    .WithKbmPath(d.StartingPath)
                    .WithGamepadPathUnbound()
                    .Finish();
            }
        }
    }
}
