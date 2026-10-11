using HarmonyLib;
using UnityEngine;

namespace LethalAccess
{
    /// <summary>
    /// Hooks the three main-menu modals up to <see cref="NewsPanelAccessibility"/>.
    ///
    /// MenuManager.Start is the right seam, and the timing is the whole point:
    ///   - It is where the news pop-up is shown and where the game parks focus on its dismiss
    ///     button, via GetComponentInChildren&lt;Button&gt;(): the only Button under NewsPanel is
    ///     Panel/ResponseButton. A postfix runs after that, so our re-focus onto the heading
    ///     overwrites the game's choice in the same frame.
    ///   - Unity runs every Start before any Update, and UIAccessibilityManager announces from
    ///     Update. It therefore sees the heading as the first non-null selection and the Continue
    ///     button is never announced at all; there is no double-speak to suppress.
    ///
    /// Start is private, so it is patched by string name; nameof would not compile.
    ///
    /// No cleanup hook is needed. The Continue button's serialized onClick deactivates NewsPanel
    /// (persistent call #3), which drops the injected Selectables out of Selectable.s_Selectables
    /// and fires the registry's hide path, and call #4 (SetSelectedUIToNextNotificationPanel) moves
    /// focus on by itself.
    /// </summary>
    [HarmonyPatch(typeof(MenuManager), "Start")]
    internal static class NewsPanelPatch
    {
        [HarmonyPostfix]
        public static void Postfix(MenuManager __instance)
        {
            // MenuManager.Start returns on its first line in the init scenes,
            // so none of these panels were touched and none of them are meaningful there.
            if (__instance.isInitScene)
            {
                return;
            }

            // Order matters when more than one modal is up at once (news pop-up plus an
            // incompatible-save-file notification is a real combination). The panel applied LAST
            // is the one focus ends on, so this follows the game's own precedence in
            // MenuManager.SetSelectedUIToNextNotificationPanel: LAN warning beats menu
            // notification beats everything else.

            GameObject news = __instance.NewsPanel;
            if (news == null)
            {
                Debug.LogError("[LethalAccess] MenuManager.NewsPanel is not wired, so the news pop-up cannot " +
                               "be made accessible if the game ever shows it.");
            }
            else if (news.activeSelf)
            {
                NewsPanelAccessibility.ApplyNewsPanel(news);
            }

            // Registered even when hidden, unlike the other two: this is the one panel the game
            // activates LATER at runtime (every DisplayMenuNotification call: failed joins,
            // disconnects, kicks), and registering now puts the activation notifier on it so those
            // later appearances announce themselves. Nothing is claimed until it is actually shown.
            GameObject notification = __instance.menuNotification;
            if (notification == null)
            {
                Debug.LogError("[LethalAccess] MenuManager.menuNotification is not wired; menu notifications " +
                               "will not be navigable.");
            }
            else
            {
                NewsPanelAccessibility.ApplyMenuNotification(notification);
            }

            // Only ever activated on the LAN launch path (MenuManager.Start), and the game itself
            // only dereferences it when disableSteam is set, so a null here in a normal Steam
            // session is not evidence of anything wrong and is deliberately not logged.
            GameObject lanWarning = __instance.lanWarningContainer;
            if (lanWarning != null && lanWarning.activeSelf)
            {
                NewsPanelAccessibility.ApplyLanWarning(lanWarning);
            }
        }
    }
}
