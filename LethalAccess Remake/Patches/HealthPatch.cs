using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LethalAccess.Patches
{
    public class PlayerHealthPatch : MonoBehaviour
    {
        private const string SpeakHealthKeybindName = "SpeakHealthKey";
        private const Key SpeakHealthDefaultKey = Key.H;

        public void Initialize()
        {
            Debug.Log("PlayerHealthPatch: Initializing input actions.");
            LACore.Instance.RegisterKeybind(SpeakHealthKeybindName, SpeakHealthDefaultKey, SpeakPlayerHealth);
            Debug.Log("PlayerHealthPatch: Input actions are registered.");

            // HUDManagerUpdateHealthUIPatch is already applied by LACore.Awake's
            // PatchAll(Assembly.GetExecutingAssembly()). Patching it again under a second
            // Harmony id made the postfix run twice per UpdateHealthUI call.
        }

        private void SpeakPlayerHealth()
        {
            int playerHealth = HUDManagerUpdateHealthUIPatch.LastUpdatedHealth;
            Debug.Log("[PlayerHealthPatch] Speaking health: " + playerHealth);
            Utilities.SpeakText(playerHealth + " HP");
        }
    }

    [HarmonyPatch(typeof(HUDManager), "UpdateHealthUI")]
    public class HUDManagerUpdateHealthUIPatch
    {
        public static int LastUpdatedHealth { get; private set; } = 100; // Default to full health

        static void Postfix(int health)
        {
            // This method is called after UpdateHealthUI in HUDManager
            LastUpdatedHealth = health;
            Debug.Log($"[LethalAccess] Player health updated: {health}");
        }
    }
}