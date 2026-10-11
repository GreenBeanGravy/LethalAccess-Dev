using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace LethalAccess
{
    /// <summary>
    /// Every key LethalAccess acts on, in one table.
    ///
    /// With LethalCompanyInputUtils installed the table becomes an InputUtils action map, so the
    /// mod's keys appear in the game's own Remap Controls screen as a "LethalAccess" section - the
    /// way every other mod's keys do - and are rebound, reset, saved per profile and conflict-checked
    /// there. Without InputUtils each key is the BepInEx config entry it always was.
    ///
    /// The table is static because InputUtils builds a mod's whole action map once, when the mod
    /// constructs its actions object in Awake. Several keys are registered much later than that
    /// (TileTracker's, when the player spawns), so they could not be discovered from
    /// RegisterKeybind calls in time.
    ///
    /// Keys are read off the bound CONTROLS, not the action's phase. InputUtils switches every
    /// mod's actions off while the remap screen is open, and the menu navigation key still has to
    /// work there.
    /// </summary>
    public static class ModKeybinds
    {
        public const string InputUtilsGuid = "com.rune580.LethalCompanyInputUtils";

        /// <summary>The Tab key. A config key name, so it outlives the behaviour it once described.</summary>
        public const string UiNavigation = "FocusPreviousUIElement";
        public const string TurnLeft = "TurnLeft";
        public const string TurnRight = "TurnRight";

        public sealed class Definition
        {
            /// <summary>Config key and InputUtils action id. Never rename: it is what users' saved binds are keyed by.</summary>
            public readonly string Id;

            /// <summary>What a person calls it. The row label on the remap screen.</summary>
            public readonly string Name;

            public readonly Key DefaultKey;

            /// <summary>False for a key registered at runtime that is not in the table.</summary>
            public readonly bool InTable;

            internal ConfigEntry<Key> Config;

            /// <summary>The InputUtils action, or null when the config entry is the binding.</summary>
            internal InputAction Action;

            internal Definition(string id, string name, Key defaultKey, bool inTable)
            {
                Id = id;
                Name = name;
                DefaultKey = defaultKey;
                InTable = inTable;
            }

            /// <summary>
            /// The keyboard path the action starts on. The config value rather than the shipped
            /// default, so whatever a user had already set in the config file or the F10 menu
            /// carries over as their starting point in the remap screen.
            /// </summary>
            internal string StartingPath
            {
                get { return KeyToPath(Config != null ? Config.Value : DefaultKey); }
            }
        }

        // Display order on the remap screen: menu first, then moving around, then information,
        // then the rest.
        private static readonly List<Definition> definitions = new List<Definition>
        {
            new Definition(UiNavigation, "Menu navigation", Key.Tab, true),
            new Definition("MoveToNextItem", "Next item", Key.RightBracket, true),
            new Definition("MoveToPreviousItem", "Previous item", Key.LeftBracket, true),
            new Definition("MoveToNextCategory", "Next category", Key.Equals, true),
            new Definition("MoveToPreviousCategory", "Previous category", Key.Minus, true),
            new Definition("RefreshCurrentCategory", "Refresh category", Key.Semicolon, true),
            new Definition("NavigateToLookingObject", "Go to selected item", Key.P, true),
            new Definition("StopLookingAndPathfinding", "Stop looking and pathfinding", Key.O, true),
            new Definition("InstantNavigation1", "Quick navigation 1", Key.Digit5, true),
            new Definition("InstantNavigation2", "Quick navigation 2", Key.Digit6, true),
            new Definition("InstantNavigation3", "Quick navigation 3", Key.Digit7, true),
            new Definition("InstantNavigation4", "Quick navigation 4", Key.Digit8, true),
            new Definition("InstantNavigation5", "Quick navigation 5", Key.Digit9, true),
            new Definition("InstantNavigation6", "Quick navigation 6", Key.Digit0, true),
            new Definition(TurnLeft, "Turn left", Key.LeftArrow, true),
            new Definition(TurnRight, "Turn right", Key.RightArrow, true),
            new Definition("SpeakPlayerDirection", "Speak direction", Key.L, true),
            new Definition("AnnounceCurrentRoom", "Announce current room", Key.R, true),
            new Definition("AnnounceNearbyNavPoints", "Announce nearby navigation points", Key.U, true),
            new Definition("SpeakTimeKey", "Speak time", Key.T, true),
            new Definition("SpeakHealthKey", "Speak health", Key.H, true),
            new Definition("SpeakProfitQuotaKey", "Speak profit quota", Key.K, true),
            new Definition("SpeakControlTipKey", "Speak control tips", Key.Quote, true),
            new Definition("ToggleNorthSound", "Toggle north sound", Key.N, true),
            new Definition("CycleNavigationMode", "Cycle navigation mode", Key.M, true),
            new Definition("ToggleVisuals", "Toggle visual markers", Key.Backspace, true),
            new Definition("LeftClickHold", "Left click, hold", Key.LeftAlt, true),
            new Definition("RightClickHold", "Right click, hold", Key.RightAlt, true),
        };

        private static readonly Dictionary<string, Definition> byId = BuildIndex();

        private static ConfigFile config;

        private static Dictionary<string, Definition> BuildIndex()
        {
            Dictionary<string, Definition> index = new Dictionary<string, Definition>(StringComparer.Ordinal);
            for (int i = 0; i < definitions.Count; i++)
            {
                index[definitions[i].Id] = definitions[i];
            }
            return index;
        }

        public static IList<Definition> Definitions
        {
            get { return definitions; }
        }

        /// <summary>True when the keys live in InputUtils and the remap screen is where they are changed.</summary>
        public static bool HostedByInputUtils { get; private set; }

        /// <summary>
        /// Bind every config entry, then hand the table to InputUtils if it is loaded. Called once
        /// from LACore.Awake, before anything registers an action. InputUtils must have woken first,
        /// which LACore's soft BepInDependency guarantees.
        /// </summary>
        public static void Initialize(ConfigFile configFile, BepInPlugin plugin)
        {
            config = configFile;
            for (int i = 0; i < definitions.Count; i++)
            {
                Definition d = definitions[i];
                d.Config = configFile.Bind("Keybinds", d.Id, d.DefaultKey, "Keybind for " + d.Name);
            }

            if (!Chainloader.PluginInfos.ContainsKey(InputUtilsGuid))
            {
                Debug.Log("[LethalAccess] LethalCompanyInputUtils is not installed; LethalAccess keys are read " +
                          "from the config file and changed in the F10 menu.");
                return;
            }

            try
            {
                InputUtilsKeybinds.Register(plugin);
                HostedByInputUtils = true;
                Debug.Log("[LethalAccess] " + definitions.Count + " LethalAccess keys registered with InputUtils; " +
                          "they appear under 'LethalAccess' on the game's Remap Controls screen.");
            }
            catch (Exception e)
            {
                // The config entries are already bound, so every key still works - it just cannot be
                // changed from the remap screen. Never let this take Awake down with it.
                for (int i = 0; i < definitions.Count; i++)
                {
                    definitions[i].Action = null;
                }
                HostedByInputUtils = false;
                Debug.LogError("[LethalAccess] Registering LethalAccess keys with InputUtils failed; they keep " +
                               "working from the config file but are not on the remap screen. " + e);
            }
        }

        /// <summary>
        /// A key some part of the mod registers that the table does not know. It still works, from
        /// the config file, but it is not on the remap screen until it is added to the table.
        /// </summary>
        internal static Definition EnsureDefinition(string id, Key defaultKey)
        {
            Definition d;
            if (byId.TryGetValue(id, out d))
            {
                return d;
            }

            d = new Definition(id, FriendlyFromId(id), defaultKey, false);
            if (config != null)
            {
                d.Config = config.Bind("Keybinds", id, defaultKey, "Keybind for " + d.Name);
            }
            definitions.Add(d);
            byId[id] = d;
            Debug.LogWarning("[LethalAccess] Keybind '" + id + "' is not in ModKeybinds' table, so it works from the " +
                             "config file only and does not appear on the remap screen. Add it to the table.");
            return d;
        }

        public static bool TryGet(string id, out Definition definition)
        {
            return byId.TryGetValue(id, out definition);
        }

        public static string NameOf(string id)
        {
            Definition d;
            return byId.TryGetValue(id, out d) ? d.Name : FriendlyFromId(id);
        }

        /// <summary>Did any control bound to this key go down this frame.</summary>
        public static bool WasPressedThisFrame(string id)
        {
            Definition d;
            if (!byId.TryGetValue(id, out d))
            {
                return false;
            }

            if (d.Action != null)
            {
                var controls = d.Action.controls;
                for (int i = 0; i < controls.Count; i++)
                {
                    ButtonControl button = controls[i] as ButtonControl;
                    if (button != null && button.wasPressedThisFrame)
                    {
                        return true;
                    }
                }
                return false;
            }

            KeyControl key = ConfigKeyControl(d);
            return key != null && key.wasPressedThisFrame;
        }

        /// <summary>Is any control bound to this key held right now.</summary>
        public static bool IsPressed(string id)
        {
            Definition d;
            if (!byId.TryGetValue(id, out d))
            {
                return false;
            }

            if (d.Action != null)
            {
                var controls = d.Action.controls;
                for (int i = 0; i < controls.Count; i++)
                {
                    ButtonControl button = controls[i] as ButtonControl;
                    if (button != null && button.isPressed)
                    {
                        return true;
                    }
                }
                return false;
            }

            KeyControl key = ConfigKeyControl(d);
            return key != null && key.isPressed;
        }

        /// <summary>
        /// The bound key as a person says it ("Left Bracket", "Tab"), or "not bound". Every binding
        /// the action has, keyboard first, joined with "or".
        /// </summary>
        public static string DescribeBinding(string id)
        {
            Definition d;
            if (!byId.TryGetValue(id, out d))
            {
                return "not bound";
            }

            if (d.Action == null)
            {
                Key k = d.Config != null ? d.Config.Value : d.DefaultKey;
                return k == Key.None ? "not bound" : KeyDisplayName(k);
            }

            List<string> parts = new List<string>(2);
            var bindings = d.Action.bindings;
            for (int i = 0; i < bindings.Count; i++)
            {
                string path = bindings[i].effectivePath;
                if (string.IsNullOrEmpty(path) || path.IndexOf("Not-Bound", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }
                string human = SpokenKeyName(InputControlPath.ToHumanReadableString(path, InputControlPath.HumanReadableStringOptions.OmitDevice));
                if (!string.IsNullOrEmpty(human) && !parts.Contains(human))
                {
                    parts.Add(human);
                }
            }
            return parts.Count == 0 ? "not bound" : string.Join(" or ", parts.ToArray());
        }

        /// <summary>
        /// The mod's own uses of a keyboard key, for the keybinds screen's conflict warning when the
        /// mod's keys are NOT on that screen. When they are, they are rows like any other and the
        /// screen finds the conflict itself.
        /// </summary>
        public static List<string> UsesOfKey(Key key)
        {
            List<string> uses = new List<string>();
            if (key == Key.None)
            {
                return uses;
            }

            for (int i = 0; i < definitions.Count; i++)
            {
                Definition d = definitions[i];
                if (d.Action != null)
                {
                    continue;
                }
                Key bound = d.Config != null ? d.Config.Value : d.DefaultKey;
                if (bound == key)
                {
                    uses.Add(d.Name + " (LethalAccess)");
                }
            }
            return uses;
        }

        private static KeyControl ConfigKeyControl(Definition d)
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || d.Config == null)
            {
                return null;
            }

            // Keyboard's indexer throws for None and returns null for IMESelected; neither can be held.
            Key key = d.Config.Value;
            if (key == Key.None || key == Key.IMESelected)
            {
                return null;
            }
            return keyboard[key];
        }

        /// <summary>"&lt;Keyboard&gt;/leftBracket" for Key.LeftBracket; the InputUtils "not bound" path for None.</summary>
        internal static string KeyToPath(Key key)
        {
            if (key == Key.None || key == Key.IMESelected)
            {
                return "<InputUtils-Kbm-Not-Bound>";
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                KeyControl control = keyboard[key];
                if (control != null)
                {
                    return "<Keyboard>/" + control.name;
                }
            }

            // No keyboard yet: the control names are the enum names in camel case, except digits.
            string name = key.ToString();
            if (name.StartsWith("Digit", StringComparison.Ordinal) && name.Length == 6)
            {
                return "<Keyboard>/" + name.Substring(5);
            }
            if (name.StartsWith("OEM", StringComparison.Ordinal))
            {
                return "<Keyboard>/oem" + name.Substring(3);
            }
            return "<Keyboard>/" + char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        internal static string KeyDisplayName(Key key)
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && key != Key.None && key != Key.IMESelected)
            {
                KeyControl control = keyboard[key];
                if (control != null && !string.IsNullOrEmpty(control.displayName))
                {
                    return SpokenKeyName(control.displayName);
                }
            }
            return key.ToString();
        }

        /// <summary>
        /// A key name as it should be heard. The Input System names punctuation keys by their
        /// character ("]", "'"), and a screen reader at its default punctuation level reads a lone
        /// symbol as nothing at all, so the binding sounded empty. Mouse wheel directions come back
        /// as "Scroll/Down".
        /// </summary>
        internal static string SpokenKeyName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }
            string trimmed = name.Trim();
            if (trimmed.Length == 1)
            {
                switch (trimmed[0])
                {
                    case '[': return "Left bracket";
                    case ']': return "Right bracket";
                    case ';': return "Semicolon";
                    case '\'': return "Quote";
                    case '/': return "Slash";
                    case '\\': return "Backslash";
                    case '=': return "Equals";
                    case '-': return "Minus";
                    case ',': return "Comma";
                    case '.': return "Period";
                    case '`': return "Backquote";
                }
                return trimmed;
            }
            if (trimmed.StartsWith("Scroll/", StringComparison.OrdinalIgnoreCase))
            {
                return "Scroll " + trimmed.Substring(7).ToLowerInvariant();
            }
            return trimmed;
        }

        private static string FriendlyFromId(string id)
        {
            string name = id;
            if (name.EndsWith("Key", StringComparison.Ordinal) && name.Length > 3)
            {
                name = name.Substring(0, name.Length - 3);
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                {
                    sb.Append(' ');
                    sb.Append(char.ToLowerInvariant(name[i]));
                    continue;
                }
                sb.Append(name[i]);
            }
            return sb.ToString();
        }
    }
}
