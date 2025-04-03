using System;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

namespace LethalAccess.Patches
{
    [HarmonyPatch(typeof(PlayerControllerB))]
    public static class ItemAction
    {
        private static DateTime lastSpokenTime = DateTime.MinValue;
        private static AudioSource audioSource;
        private static AudioSource twoHandedAudioSource;

        private static void SpeakWithCooldown(string text)
        {
            if ((DateTime.Now - lastSpokenTime).TotalSeconds > 0.05)
            {
                Utilities.SpeakText(text);
                lastSpokenTime = DateTime.Now;
            }
        }

        public static event Action OnItemHeld;

        private static void HandleItemPickedUp(bool isTwoHanded)
        {
            if (isTwoHanded)
            {
                PlayTwoHandedSound();
            }
            else
            {
                if (audioSource == null)
                {
                    GameObject obj = new GameObject("ItemPickupAudioSource");
                    audioSource = obj.AddComponent<AudioSource>();
                    audioSource.clip = GenerateItemPickupTone();
                    audioSource.volume = 0.35f; // 30% lower volume
                }

                audioSource.Play();
            }
            LACore.currentLookTarget = null;
        }

        private static AudioClip GenerateItemPickupTone()
        {
            int sampleRate = 44100;
            float toneDuration = 0.1f;
            float pauseDuration = 0.028125f; // 0.05f * 0.75f * 0.75f
            int sampleLength = Mathf.RoundToInt(sampleRate * ((toneDuration * 2) + pauseDuration));
            AudioClip toneClip = AudioClip.Create("ItemPickupTone", sampleLength, 1, sampleRate, false);
            float[] samples = new float[sampleLength];

            // Set frequency values
            float frequency1 = 544.5f; // Approximately 880 * 0.75 * 1.1 * 0.75
            float frequency2 = 647.5f; // Approximately 1046.5 * 0.75 * 1.1 * 0.75

            int index = 0;

            // First tone
            for (int i = 0; i < Mathf.RoundToInt(sampleRate * toneDuration) && index < sampleLength; i++)
            {
                samples[index++] = Mathf.Sin(2 * Mathf.PI * frequency1 * i / sampleRate);
            }

            // Short pause
            index = Mathf.Min(index + Mathf.RoundToInt(sampleRate * pauseDuration), sampleLength);

            // Second tone
            for (int i = 0; i < Mathf.RoundToInt(sampleRate * toneDuration) && index < sampleLength; i++)
            {
                samples[index++] = Mathf.Sin(2 * Mathf.PI * frequency2 * i / sampleRate);
            }

            toneClip.SetData(samples, 0);
            return toneClip;
        }

        private static void PlayTwoHandedSound()
        {
            if (twoHandedAudioSource == null)
            {
                GameObject obj = new GameObject("TwoHandedAudioSource");
                twoHandedAudioSource = obj.AddComponent<AudioSource>();
                twoHandedAudioSource.clip = GenerateTwoHandedTone();
                twoHandedAudioSource.volume = 0.35f; // Same volume as item pickup tone
            }

            twoHandedAudioSource.Play();
        }

        private static AudioClip GenerateTwoHandedTone()
        {
            int sampleRate = 44100;
            float toneDuration = 0.1f;
            float pauseDuration = 0.028125f; // 0.05f * 0.75f * 0.75f
            int sampleLength = Mathf.RoundToInt(sampleRate * ((toneDuration * 2) + pauseDuration));
            AudioClip toneClip = AudioClip.Create("TwoHandedTone", sampleLength, 1, sampleRate, false);
            float[] samples = new float[sampleLength];

            // Set frequency values
            float frequency1 = 272.25f; // Half of 544.5f
            float frequency2 = 323.75f; // Half of 647.5f

            int index = 0;

            // First tone
            for (int i = 0; i < Mathf.RoundToInt(sampleRate * toneDuration) && index < sampleLength; i++)
            {
                samples[index++] = Mathf.Sin(2 * Mathf.PI * frequency1 * i / sampleRate);
            }

            // Short pause
            index = Mathf.Min(index + Mathf.RoundToInt(sampleRate * pauseDuration), sampleLength);

            // Second tone
            for (int i = 0; i < Mathf.RoundToInt(sampleRate * toneDuration) && index < sampleLength; i++)
            {
                samples[index++] = Mathf.Sin(2 * Mathf.PI * frequency2 * i / sampleRate);
            }

            toneClip.SetData(samples, 0);
            return toneClip;
        }

        [HarmonyPatch("SwitchToItemSlot"), HarmonyPostfix]
        public static void SwitchToItemSlotPostfix(PlayerControllerB __instance, int slot)
        {
            if (__instance.IsOwner && __instance.ItemSlots[slot] != null)
            {
                GrabbableObject item = __instance.ItemSlots[slot];
                string itemName = item.itemProperties.itemName;
                int scrapValue = item.scrapValue;

                // Remove the item from the NavMenu
                LACore.Instance.navMenu.RemoveItem(item.gameObject.name, "Items");

                string actionType = "held";
                if (item.isPocketed)
                {
                    actionType = "pocketed";
                }
                else if (item.deactivated)
                {
                    actionType = "deactivated";
                }

                if (item.itemProperties.twoHanded)
                {
                    SpeakWithCooldown($"{actionType} {itemName}, two-handed object, worth ${scrapValue}, cannot switch items until this item is dropped");
                }
                else
                {
                    SpeakWithCooldown($"{actionType} {itemName}, worth ${scrapValue}");
                }

                HandleItemPickedUp(item.itemProperties.twoHanded);
                OnItemHeld?.Invoke();

                // Refresh the menu if the held/pocketed/deactivated item was the currently selected item
                if (LACore.Instance.navMenu.currentIndices.categoryIndex == LACore.Instance.navMenu.categories.IndexOf("Items") &&
                    LACore.Instance.navMenu.menuItems["Items"].Count > 0 &&
                    LACore.Instance.navMenu.menuItems["Items"][LACore.Instance.navMenu.currentIndices.itemIndex] == item.gameObject.name)
                {
                    Utilities.SpeakText($"{itemName} removed from item list as it is now {actionType}.");
                    LACore.Instance.navMenu.RefreshMenu();
                }
            }
        }

        [HarmonyPatch("SetObjectAsNoLongerHeld"), HarmonyPostfix]
        public static void SetObjectAsNoLongerHeldPostfix(PlayerControllerB __instance, GrabbableObject dropObject)
        {
            if (__instance.IsOwner && dropObject != null)
            {
                string itemName = dropObject.itemProperties.itemName;
                SpeakWithCooldown("Dropped " + itemName);
            }
        }
    }
}
