using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;

namespace LethalAccess
{
    /// <summary>
    /// Consolidated audio utility functions for LethalAccess
    /// </summary>
    public static class AudioUtils
    {
        /// <summary>
        /// Load an audio clip from the embedded resources
        /// </summary>
        public static IEnumerator LoadAudioClip(string resourcePath, System.Action<AudioClip> onLoaded)
        {
            string modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string fullPath = Path.Combine(modDirectory, resourcePath);
            string fileURL = "file://" + fullPath;

            UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(fileURL, AudioType.WAV);
            yield return request.SendWebRequest();

            try
            {
                if (request.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        onLoaded(clip);
                        Debug.Log($"Successfully loaded audio clip: {resourcePath}");
                    }
                    else
                    {
                        Debug.LogError($"Failed to load audio clip content: {resourcePath}");
                    }
                }
                else
                {
                    Debug.LogError($"Error loading audio clip {resourcePath}: {request.error}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception loading audio clip {resourcePath}: {ex.Message}");
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Load and play an audio clip at a specific location
        /// </summary>
        public static IEnumerator LoadAndPlayAudioClip(string audioFilePath, GameObject targetGameObject, float minDistance, float maxDistance)
        {
            if (targetGameObject == null)
            {
                Debug.LogError("Target GameObject is null. Cannot play audio clip.");
                yield break;
            }

            string modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string fullPath = Path.Combine(modDirectory, audioFilePath);
            string fileURL = "file://" + fullPath;

            UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip(fileURL, AudioType.WAV);
            yield return uwr.SendWebRequest();

            try
            {
                if (uwr.result == UnityWebRequest.Result.ConnectionError ||
                    uwr.result == UnityWebRequest.Result.ProtocolError)
                {
                    Debug.LogError("Error loading audio clip: " + uwr.error);
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(uwr);
                if (clip != null)
                {
                    AudioSource audioSource = targetGameObject.GetComponent<AudioSource>();
                    if (audioSource == null)
                    {
                        audioSource = targetGameObject.AddComponent<AudioSource>();
                    }

                    audioSource.clip = clip;
                    audioSource.spatialBlend = 1f;
                    audioSource.rolloffMode = AudioRolloffMode.Linear;
                    audioSource.minDistance = minDistance;
                    audioSource.maxDistance = maxDistance;
                    audioSource.volume = ConfigManager.NavigationSoundVolume.Value;
                    audioSource.Play();
                    Debug.Log("Playing audio clip: " + audioFilePath);
                }
                else
                {
                    Debug.LogError("Failed to load audio clip: " + audioFilePath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("Exception processing audio clip: " + ex.Message);
            }
        }

        /// <summary>
        /// Generate a simple tone audio clip
        /// </summary>
        public static AudioClip GenerateTone(string name, float frequency, float duration, float volume = 0.5f)
        {
            int sampleRate = 44100;
            int sampleCount = Mathf.RoundToInt(sampleRate * duration);
            AudioClip clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float normalizedTime = i / (float)sampleCount;
                float envelope = Mathf.Sin(normalizedTime * Mathf.PI); // Fade in and out
                samples[i] = Mathf.Sin(2 * Mathf.PI * frequency * i / sampleRate) * envelope * volume;
            }

            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Generate a dual-tone audio clip (useful for confirmations)
        /// </summary>
        public static AudioClip GenerateDualTone(string name, float freq1, float freq2, float toneDuration, float pauseDuration, float volume = 0.5f)
        {
            int sampleRate = 44100;
            float totalDuration = toneDuration * 2 + pauseDuration;
            int sampleCount = Mathf.RoundToInt(sampleRate * totalDuration);
            AudioClip clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);
            float[] samples = new float[sampleCount];

            int index = 0;

            // First tone
            for (int i = 0; i < Mathf.RoundToInt(sampleRate * toneDuration) && index < sampleCount; i++)
            {
                float normalizedTime = i / (sampleRate * toneDuration);
                float envelope = Mathf.Sin(normalizedTime * Mathf.PI);
                samples[index++] = Mathf.Sin(2 * Mathf.PI * freq1 * i / sampleRate) * envelope * volume;
            }

            // Pause
            index = Mathf.Min(index + Mathf.RoundToInt(sampleRate * pauseDuration), sampleCount);

            // Second tone
            for (int i = 0; i < Mathf.RoundToInt(sampleRate * toneDuration) && index < sampleCount; i++)
            {
                float normalizedTime = i / (sampleRate * toneDuration);
                float envelope = Mathf.Sin(normalizedTime * Mathf.PI);
                samples[index++] = Mathf.Sin(2 * Mathf.PI * freq2 * i / sampleRate) * envelope * volume;
            }

            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Generate a sweep tone (frequency changes over time)
        /// </summary>
        public static AudioClip GenerateSweepTone(string name, float startFreq, float endFreq, float duration, float volume = 0.5f)
        {
            int sampleRate = 44100;
            int sampleCount = Mathf.RoundToInt(sampleRate * duration);
            AudioClip clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleCount;
                float frequency = Mathf.Lerp(startFreq, endFreq, t);
                float envelope = t * (1 - t) * 4; // Bell curve envelope
                samples[i] = Mathf.Sin(2 * Mathf.PI * frequency * i / sampleRate) * envelope * volume;
            }

            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Generate a click sound (useful for UI feedback)
        /// </summary>
        public static AudioClip GenerateClickSound(string name = "ClickSound", float volume = 0.6f)
        {
            int sampleRate = 44100;
            float duration = 0.05f;
            float frequency = 2000f;

            AudioClip clip = AudioClip.Create(name, (int)(sampleRate * duration), 1, sampleRate, false);

            float[] samples = new float[(int)(sampleRate * duration)];
            for (int i = 0; i < samples.Length; i++)
            {
                float t = (float)i / samples.Length;
                float envelope = Mathf.Exp(-t * 10); // Sharp attack, quick decay
                samples[i] = Mathf.Sin(2 * Mathf.PI * frequency * t) * envelope * volume;
            }

            clip.SetData(samples, 0);
            return clip;
        }

        /// <summary>
        /// Play a one-shot audio clip with proper configuration
        /// </summary>
        public static void PlayOneShotAudio(Vector3 position, AudioClip clip, float volume = 1f, bool is3D = true)
        {
            if (clip == null) return;

            GameObject tempAudio = new GameObject("OneShotAudio");
            tempAudio.transform.position = position;

            AudioSource audioSource = tempAudio.AddComponent<AudioSource>();
            AudioSystemBypass.ConfigureAudioSourceForBypass(audioSource, volume * ConfigManager.MasterVolume.Value);

            audioSource.clip = clip;
            audioSource.spatialBlend = is3D ? 1f : 0f;
            audioSource.Play();

            // Destroy after playing
            MonoBehaviour.Destroy(tempAudio, clip.length + 0.1f);
        }

        /// <summary>
        /// Create an audio source with standard LethalAccess configuration
        /// </summary>
        public static AudioSource CreateConfiguredAudioSource(GameObject target, float volume = 1f, bool is3D = true)
        {
            AudioSource audioSource = target.GetComponent<AudioSource>();
            if (audioSource == null)
            {
                audioSource = target.AddComponent<AudioSource>();
            }

            AudioSystemBypass.ConfigureAudioSourceForBypass(audioSource, volume * ConfigManager.MasterVolume.Value);
            audioSource.spatialBlend = is3D ? 1f : 0f;
            audioSource.playOnAwake = false;

            return audioSource;
        }

        /// <summary>
        /// Generate navigation audio cues based on type
        /// </summary>
        public static AudioClip GenerateNavigationCue(NavigationCueType cueType)
        {
            switch (cueType)
            {
                case NavigationCueType.ItemPickup:
                    return GenerateDualTone("ItemPickupTone", 544.5f, 647.5f, 0.1f, 0.028f, 0.35f);

                case NavigationCueType.TwoHandedItem:
                    return GenerateDualTone("TwoHandedTone", 272.25f, 323.75f, 0.1f, 0.028f, 0.35f);

                case NavigationCueType.ReachedDestination:
                    return GenerateClickSound("ReachedDestination", 0.8f);

                case NavigationCueType.OnTarget:
                    return GenerateTone("OnTarget", 880f, 0.15f, 0.7f);

                case NavigationCueType.AlmostOnTarget:
                    return GenerateTone("AlmostOnTarget", 660f, 0.15f, 0.7f);

                case NavigationCueType.OffTarget:
                    return GenerateSweepTone("OffTarget", 440f, 220f, 0.3f, 0.5f);

                case NavigationCueType.Completion:
                    return GenerateDualTone("Completion", 660f, 785f, 0.15f, 0.05f, 0.5f);

                default:
                    return GenerateClickSound();
            }
        }
    }

    /// <summary>
    /// Types of navigation audio cues
    /// </summary>
    public enum NavigationCueType
    {
        ItemPickup,
        TwoHandedItem,
        ReachedDestination,
        OnTarget,
        AlmostOnTarget,
        OffTarget,
        Completion
    }
}