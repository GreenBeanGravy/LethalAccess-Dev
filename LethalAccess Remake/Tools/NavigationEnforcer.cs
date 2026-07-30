using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace LethalAccess
{
    /// <summary>
    /// Targeted rewrites of a Selectable's authored Navigation.
    ///
    /// The general "up/down only" rule is enforced by SelectableNavigationPatch, which is
    /// non-destructive and needs no invalidation story. This class is for the cases where the mod
    /// owns the objects anyway - Selectables it injected, runtime clones it built - and writing
    /// the chain directly is simpler than describing it.
    ///
    /// Every method here writes back through the `navigation` property: Navigation is a struct,
    /// so `s.navigation.selectOnUp = x` does not compile and mutating a local copy without
    /// assigning it back is a no-op that looks like it worked.
    /// </summary>
    public static class NavigationEnforcer
    {
        /// <summary>
        /// Wire an ordered list into an explicit up/down chain with no horizontal escape.
        /// Nulls in the list are skipped (a caller building a chain from resolved paths will have
        /// holes when a control is absent).
        /// </summary>
        public static void BuildChain(IList<Selectable> chain, bool wrap)
        {
            if (chain == null)
            {
                Debug.LogError("[LethalAccess] NavigationEnforcer.BuildChain called with a null chain.");
                return;
            }

            List<Selectable> live = new List<Selectable>(chain.Count);
            for (int i = 0; i < chain.Count; i++)
            {
                if (chain[i] != null)
                {
                    live.Add(chain[i]);
                }
            }

            if (live.Count == 0)
            {
                Debug.LogWarning("[LethalAccess] NavigationEnforcer.BuildChain: every element was null; " +
                                 "no navigation was written.");
                return;
            }

            for (int i = 0; i < live.Count; i++)
            {
                Selectable s = live[i];
                Navigation nav = s.navigation;
                nav.mode = Navigation.Mode.Explicit;
                nav.selectOnLeft = null;
                nav.selectOnRight = null;
                nav.selectOnUp = i > 0 ? live[i - 1] : (wrap ? live[live.Count - 1] : null);
                nav.selectOnDown = i < live.Count - 1 ? live[i + 1] : (wrap ? live[0] : null);
                s.navigation = nav;
            }
        }

        /// <summary>Convenience overload; does not wrap.</summary>
        public static void BuildChain(params Selectable[] chain)
        {
            BuildChain(chain, false);
        }

        /// <summary>
        /// Keep the authored up/down neighbours, remove the horizontal ones.
        ///
        /// Switching to Explicit freezes whatever up/down were authored. If the scene authored
        /// them as Automatic (geometry) they are null, and this would kill vertical movement
        /// entirely - unless the registry claims the control, in which case
        /// SelectableNavigationPatch supplies up/down from the authored screen order and the null
        /// fields are never read. That is the only case where an empty explicit chain is safe, so
        /// anything else gets a warning.
        /// </summary>
        public static void MakeVerticalOnly(Selectable s)
        {
            if (s == null)
            {
                Debug.LogError("[LethalAccess] NavigationEnforcer.MakeVerticalOnly called with a null Selectable.");
                return;
            }

            Navigation nav = s.navigation;

            if (nav.selectOnUp == null && nav.selectOnDown == null &&
                !UIScreenRegistry.ClaimsSelectable(s.gameObject))
            {
                Debug.LogWarning("[LethalAccess] MakeVerticalOnly('" + s.name + "'): it has no explicit up/down " +
                                 "neighbours and no screen descriptor claims it, so up and down will now do " +
                                 "nothing. Register it on a screen or use BuildChain instead.");
            }

            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnLeft = null;
            nav.selectOnRight = null;
            s.navigation = nav;
        }

        /// <summary>
        /// Take a control out of navigation entirely. It can still be focused deliberately (by
        /// FocusManager.Seed or a mouse click); it just cannot be arrowed to, and arrowing off it
        /// goes nowhere.
        /// </summary>
        public static void Isolate(Selectable s)
        {
            if (s == null)
            {
                Debug.LogError("[LethalAccess] NavigationEnforcer.Isolate called with a null Selectable.");
                return;
            }

            Navigation nav = s.navigation;
            nav.mode = Navigation.Mode.None;
            nav.selectOnUp = null;
            nav.selectOnDown = null;
            nav.selectOnLeft = null;
            nav.selectOnRight = null;
            s.navigation = nav;
        }
    }
}
