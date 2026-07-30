using UnityEngine;
using UnityEngine.UI;

namespace LethalAccess
{
    /// <summary>
    /// Makes non-interactive UI - headings, prompts, body copy, status lines - reachable by the
    /// keyboard. This is the general mechanism behind every "the text on this screen cannot be
    /// read because nothing can focus it" defect.
    /// </summary>
    public static class FocusInjection
    {
        /// <summary>
        /// Give a GameObject a Selectable that the EventSystem can focus but that is visually
        /// inert.
        ///
        /// The ordering below is mandatory. Selectable.Awake runs INSIDE AddComponent and does
        /// `if (m_TargetGraphic == null) m_TargetGraphic = GetComponent&lt;Graphic&gt;()`.
        /// TextMeshProUGUI IS a Graphic, so a Selectable added to a text object adopts that text
        /// as its target graphic and colour-tints it on focus. targetGraphic must therefore be
        /// cleared AFTER AddComponent, never before, and never by passing it to a constructor
        /// (there isn't one).
        /// </summary>
        public static Selectable MakeFocusable(GameObject go)
        {
            if (go == null)
            {
                Debug.LogError("[LethalAccess] FocusInjection.MakeFocusable called with a null GameObject.");
                return null;
            }

            // Explicit null test rather than ??: GetComponent returns a real null here, but the
            // Unity-overloaded == is what correctly reports a destroyed component as absent.
            Selectable s = go.GetComponent<Selectable>();
            bool injected = false;
            if (s == null)
            {
                s = go.AddComponent<Selectable>();
                injected = true;
            }

            s.transition = Selectable.Transition.None;
            s.targetGraphic = null;
            s.interactable = true;

            if (injected)
            {
                // Coverage of injected focus points is worth being able to see in the log; this
                // runs once per object per screen resolution, not per frame.
                Debug.Log("[LethalAccess] Injected a focusable Selectable on '" + go.name + "'.");
            }

            return s;
        }
    }
}
