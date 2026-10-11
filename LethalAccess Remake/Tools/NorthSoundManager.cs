using UnityEngine;
using System.Collections;
using BepInEx.Configuration;

namespace LethalAccess
{
    public class NorthSoundManager : MonoBehaviour
    {
        private AudioSource audioSource;
        public bool isEnabled = false;
        private float volume = 0.15f;
        private float normalFrequency = 440f;
        private float behindFrequency = 220f; // 50% deeper tone

        // Both tones are generated once. The previous code built a fresh AudioClip (and a fresh
        // 8820-sample float[]) on every play, roughly 40 native AudioClips a minute, none of
        // them ever destroyed, for as long as North Sound stayed on.
        private AudioClip normalToneClip;
        private AudioClip behindToneClip;

        // Interval now comes from ConfigManager.NorthSoundInterval, which is the entry exposed in
        // the in-game settings menu. This class used to Bind its own "Values/NorthSoundPlayInterval"
        // key and read that instead, so the setting the user could actually see did nothing.
        private float PlayInterval => ConfigManager.NorthSoundInterval.Value;

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

            normalToneClip = GenerateNorthSound(isBehindPlayer: false);
            behindToneClip = GenerateNorthSound(isBehindPlayer: true);
        }

        void Update()
        {
            if (!isEnabled)
            {
                return;
            }

            // LACore.PlayerTransform is null in the main menu and between rounds. Dereferencing
            // it unguarded threw a NullReferenceException every frame once North Sound had been
            // toggled on and the player then left the round.
            Transform player = LACore.PlayerTransform;
            if (player == null)
            {
                SetEnabled(false, announce: true, reason: "North Sound disabled, player not available");
                return;
            }

            Vector3 northDirection = Vector3.forward;
            transform.position = player.position + northDirection * 10f;
            transform.LookAt(player);
        }

        private void SetEnabled(bool enabled, bool announce, string reason = null)
        {
            isEnabled = enabled;

            if (!enabled)
            {
                StopAllCoroutines();
                if (audioSource != null)
                {
                    audioSource.Stop();
                }
            }

            if (announce && !string.IsNullOrEmpty(reason))
            {
                Utilities.SpeakText(reason);
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
                if (LACore.PlayerTransform == null)
                {
                    yield break;
                }

                bool isBehindPlayer = IsSoundBehindPlayer();
                audioSource.clip = isBehindPlayer ? behindToneClip : normalToneClip;
                audioSource.Play();
                yield return new WaitForSeconds(PlayInterval);
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
            Transform player = LACore.PlayerTransform;
            if (player == null)
            {
                return false;
            }

            Vector3 playerForward = player.forward;
            Vector3 toSound = transform.position - player.position;
            float dotProduct = Vector3.Dot(playerForward, toSound.normalized);
            return dotProduct < 0; // If dot product is negative, sound is behind the player
        }

        // Method to update the play interval
        public void UpdatePlayInterval(float newInterval)
        {
            ConfigManager.NorthSoundInterval.Value = newInterval;
        }

        private void OnDestroy()
        {
            if (normalToneClip != null) Destroy(normalToneClip);
            if (behindToneClip != null) Destroy(behindToneClip);
        }
    }
}