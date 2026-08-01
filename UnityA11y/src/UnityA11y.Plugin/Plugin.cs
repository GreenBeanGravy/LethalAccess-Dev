using System.Collections;
using BepInEx;
using UnityEngine;
using UnityA11y.Unity;

namespace UnityA11y.Plugin
{
    /// <summary>
    /// BepInEx 5 entry point. Deliberately thin: everything real lives in
    /// UnityA11y.Unity, so a MelonLoader or Doorstop front end is a ten-line file.
    /// </summary>
    [BepInPlugin(Guid, "Unity Accessibility Base", "0.2.0")]
    public class A11yPlugin : BaseUnityPlugin
    {
        public const string Guid = "dev.unitya11y.base";

        private void Awake()
        {
            StartCoroutine(StartWhenReady());
        }

        /// <summary>
        /// Deferred by a frame: during Awake the EventSystem and Canvases usually
        /// do not exist yet, and GetActiveWindow is more reliable once the game
        /// has pumped at least one message.
        /// </summary>
        private IEnumerator StartWhenReady()
        {
            yield return null;

            var runtime = A11yRuntime.Create();
            runtime.Log = msg => Logger.LogInfo(msg);

            // Unbound by default because any sensible default collides with
            // something in some game. Wire it here or read it from config.
            runtime.Navigation.Repeat = KeyCode.None;
            runtime.Navigation.ApplyKeyBindings();

            Logger.LogInfo(runtime.BridgeRunning
                ? "Unity accessibility base loaded; AccessKit attached."
                : "Unity accessibility base loaded; AccessKit NOT attached (see log above).");
        }
    }
}
