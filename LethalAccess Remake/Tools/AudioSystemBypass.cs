using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace LethalAccess
{

    public static class AudioSystemBypass
    {
        public static void ConfigureAudioSourceForBypass(AudioSource audioSource, float volume = 1f)
        {
            if (audioSource == null)
                return;

            audioSource.bypassEffects = true;
            audioSource.bypassListenerEffects = true;
            audioSource.bypassReverbZones = true;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.volume = volume;
            audioSource.spatialBlend = 1f;
            audioSource.priority = 0;
            audioSource.spread = 0f;
            audioSource.dopplerLevel = 0f;
        }

        public static float CalculateVolumeBasedOnDistance(float distance, float minDistance, float maxDistance, float baseVolume)
        {
            if (distance <= minDistance)
            {
                return baseVolume;
            }

            if (distance >= maxDistance)
            {
                return 0f;
            }

            float normalizedDistance = (distance - minDistance) / (maxDistance - minDistance);
            return baseVolume * (1f - normalizedDistance);
        }
    }
}