using UnityEngine;
using System.Collections;
using BepInEx.Configuration;

namespace Green.LethalAccessPlugin
{
    public class NorthSoundManager : MonoBehaviour
    {
        private AudioSource audioSource;
        public bool isEnabled = false;
        private float playInterval = 1.5f; // Default value
        private float volume = 0.15f;
        private float normalFrequency = 440f;
        private float behindFrequency = 220f; // 50% deeper tone

        // New configuration entry in the "Values" category
        private static ConfigEntry<float> configPlayInterval;

        void Start()
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.spatialize = true;
            audioSource.spatialBlend = 1f;
            audioSource.volume = volume;
            audioSource.loop = false;
            audioSource.playOnAwake = false;
            audioSource.dopplerLevel = 0f;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.maxDistance = 1000f;

            // Initialize the configuration entry in the "Values" category
            configPlayInterval = LethalAccess.LethalAccessPlugin.Instance.Config.Bind("Values", "NorthSoundPlayInterval", 1.5f, "The delay in seconds between North sound plays");

            // Set the playInterval to the configured value
            playInterval = configPlayInterval.Value;
        }

        void Update()
        {
            if (isEnabled)
            {
                Vector3 northDirection = Vector3.forward;
                transform.position = LethalAccess.LethalAccessPlugin.PlayerTransform.position + northDirection * 10f;
                transform.LookAt(LethalAccess.LethalAccessPlugin.PlayerTransform);
            }
        }

        public void ToggleNorthSound()
        {
            isEnabled = !isEnabled;
            if (isEnabled)
            {
                StartCoroutine(PlayNorthSoundRoutine());
            }
            else
            {
                StopAllCoroutines();
                audioSource.Stop();
            }
        }

        private IEnumerator PlayNorthSoundRoutine()
        {
            while (isEnabled)
            {
                bool isBehindPlayer = IsSoundBehindPlayer();
                audioSource.clip = GenerateNorthSound(isBehindPlayer);
                audioSource.Play();
                yield return new WaitForSeconds(playInterval);
            }
        }

        private AudioClip GenerateNorthSound(bool isBehindPlayer)
        {
            int sampleRate = 44100;
            float frequency = isBehindPlayer ? behindFrequency : normalFrequency;
            float duration = 0.2f;
            int sampleCount = Mathf.CeilToInt(sampleRate * duration);
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleRate;
                samples[i] = Mathf.Sin(2f * Mathf.PI * frequency * t);
            }

            AudioClip clip = AudioClip.Create("NorthSound", sampleCount, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private bool IsSoundBehindPlayer()
        {
            Vector3 playerForward = LethalAccess.LethalAccessPlugin.PlayerTransform.forward;
            Vector3 toSound = transform.position - LethalAccess.LethalAccessPlugin.PlayerTransform.position;
            float dotProduct = Vector3.Dot(playerForward, toSound.normalized);
            return dotProduct < 0; // If dot product is negative, sound is behind the player
        }

        // Method to update the play interval
        public void UpdatePlayInterval(float newInterval)
        {
            playInterval = newInterval;
            configPlayInterval.Value = newInterval;
        }
    }
}