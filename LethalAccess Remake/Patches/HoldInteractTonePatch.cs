using HarmonyLib;
using LethalAccess;
using UnityEngine;

namespace LethalAccess.Patches
{
    public static class LethalAccess_InteractionPatches
    {
        private static AudioSource continuousToneSource = null;
        private static AudioSource completionToneSource = null;
        private static bool isToneInitialized = false;

        [HarmonyPatch(typeof(HUDManager), "Update")]
        public static class HUDManager_UpdatePatch
        {
            [HarmonyPostfix]
            public static void Postfix(HUDManager __instance)
            {
                if (__instance == null) return;
                InitializeTones();
                if (continuousToneSource == null) return;

                if (__instance.holdFillAmount > 0)
                {
                    float pitch = 1 + (__instance.holdFillAmount * 0.7f);
                    continuousToneSource.pitch = pitch;
                    if (!continuousToneSource.isPlaying)
                    {
                        continuousToneSource.Play();
                    }
                    continuousToneSource.volume = Mathf.Lerp(0.1f, 0.5f, __instance.holdFillAmount);
                }
                else if (__instance.holdFillAmount == 0 && continuousToneSource.isPlaying)
                {
                    continuousToneSource.Stop();
                }
            }
        }

        [HarmonyPatch(typeof(HUDManager), "HoldInteractionFill")]
        public static class HUDManager_HoldInteractionFillPatch
        {
            [HarmonyPostfix]
            public static void Postfix(HUDManager __instance, float timeToHold, float speedMultiplier, bool __result)
            {
                if (__result) // Interaction completed
                {
                    PlayCompletionTone();
                }
            }
        }

        // Pure functions of constants, so generated once for the process. Regenerating them when
        // the AudioSources are rebuilt (which happens on each player-object rebuild, i.e. every
        // round) allocates a 44100-sample float[] plus a native AudioClip each time, and nothing
        // destroys the old one.
        private static AudioClip cachedContinuousClip;
        private static AudioClip cachedCompletionClip;

        private static void InitializeTones()
        {
            if (!isToneInitialized || continuousToneSource == null || completionToneSource == null)
            {
                if (LACore.PlayerTransform != null)
                {
                    if (cachedContinuousClip == null)
                    {
                        cachedContinuousClip = GenerateContinuousToneClip(440, 1);
                    }
                    if (cachedCompletionClip == null)
                    {
                        cachedCompletionClip = GenerateCompletionToneClip();
                    }

                    // Initialize continuous tone
                    continuousToneSource = LACore.PlayerTransform.gameObject.AddComponent<AudioSource>();
                    continuousToneSource.loop = true;
                    continuousToneSource.clip = cachedContinuousClip;
                    continuousToneSource.volume = 0.3f;

                    // Initialize completion tone
                    completionToneSource = LACore.PlayerTransform.gameObject.AddComponent<AudioSource>();
                    completionToneSource.loop = false;
                    completionToneSource.clip = cachedCompletionClip;
                    completionToneSource.volume = 0.7f;
                    completionToneSource.priority = 0; // Highest priority
                    completionToneSource.bypassEffects = true;
                    completionToneSource.bypassListenerEffects = true;
                    completionToneSource.bypassReverbZones = true;

                    isToneInitialized = true;
                }
            }
        }

        private static AudioClip GenerateContinuousToneClip(float frequency, float duration)
        {
            int sampleRate = 44100;
            int sampleLength = Mathf.RoundToInt(sampleRate * duration);
            AudioClip toneClip = AudioClip.Create("ContinuousTone", sampleLength, 1, sampleRate, false);
            float[] samples = new float[sampleLength];
            for (int i = 0; i < sampleLength; i++)
            {
                samples[i] = Mathf.Sin(2 * Mathf.PI * frequency * i / sampleRate);
            }
            toneClip.SetData(samples, 0);
            return toneClip;
        }

        private static AudioClip GenerateCompletionToneClip()
        {
            int sampleRate = 44100;
            float toneDuration = 0.15f;
            float pauseDuration = 0.05f;
            int sampleLength = Mathf.RoundToInt(sampleRate * ((toneDuration * 2) + pauseDuration));
            AudioClip toneClip = AudioClip.Create("CompletionTone", sampleLength, 1, sampleRate, false);
            float[] samples = new float[sampleLength];

            float frequency1 = 660f;
            float frequency2 = 785f;

            int index = 0;

            // First tone
            for (int i = 0; i < Mathf.RoundToInt(sampleRate * toneDuration) && index < sampleLength; i++)
            {
                samples[index++] = Mathf.Sin(2 * Mathf.PI * frequency1 * i / sampleRate) * 0.5f;
            }

            // Short pause
            index = Mathf.Min(index + Mathf.RoundToInt(sampleRate * pauseDuration), sampleLength);

            // Second tone
            for (int i = 0; i < Mathf.RoundToInt(sampleRate * toneDuration) && index < sampleLength; i++)
            {
                samples[index++] = Mathf.Sin(2 * Mathf.PI * frequency2 * i / sampleRate) * 0.5f;
            }

            toneClip.SetData(samples, 0);
            return toneClip;
        }

        private static void PlayCompletionTone()
        {
            InitializeTones();
            if (completionToneSource != null && !completionToneSource.isPlaying)
            {
                completionToneSource.PlayOneShot(completionToneSource.clip, 1f);
            }
        }
    }
}