using UnityEngine;
using System;
using GreenBean.LethalSpeechOutput;

namespace Green.LethalAccessPlugin
{
    public static class SpeechSynthesizer
    {
        static SpeechSynthesizer()
        {
            UnityMainThreadDispatcher.Instance().Enqueue(() =>
            {
                Debug.Log("SpeechSynthesizer initialized.");
            });
        }

        public static void SpeakText(string text)
        {
            try
            {
                LethalSpeechOutput.SpeakText(text);
                UnityMainThreadDispatcher.Instance().Enqueue(() => Debug.Log($"Spoken text using LethalSpeechOutput: '{text}'"));
            }
            catch (Exception ex)
            {
                UnityMainThreadDispatcher.Instance().Enqueue(() =>
                {
                    Debug.LogError($"Error speaking text: {ex.Message}");
                    Debug.LogError($"Stack trace: {ex.StackTrace}");
                });
            }
        }

        public static void Cleanup()
        {
            UnityMainThreadDispatcher.Instance().Enqueue(() => Debug.Log("SpeechSynthesizer cleanup completed."));
        }
    }
}
