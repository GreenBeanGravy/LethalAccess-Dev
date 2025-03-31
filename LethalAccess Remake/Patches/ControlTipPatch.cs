using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

namespace Green.LethalAccessPlugin.Patches
{
    public class ControlTipAccessPatch : MonoBehaviour
    {
        private const string SpeakControlTipKeybindName = "SpeakControlTipKey";
        private const Key SpeakControlTipDefaultKey = Key.Quote;

        public void Initialize()
        {
            Debug.Log("ControlTipPatch: Initializing input actions.");
            LethalAccess.LethalAccessPlugin.Instance.RegisterKeybind(SpeakControlTipKeybindName, SpeakControlTipDefaultKey, SpeakControlTip);
            Debug.Log("ControlTipPatch: Input actions are registered.");
        }

        private void SpeakControlTip()
        {
            // Concatenate text from ControlTip1, ControlTip2, and ControlTip3
            string combinedText = "";

            for (int i = 1; i <= 3; i++)
            {
                var controlTipTextObject = GameObject.Find($"Systems/UI/Canvas/IngamePlayerHUD/TopRightCorner/ControlTip{i}");
                if (controlTipTextObject != null)
                {
                    var tmpTextComponent = controlTipTextObject.GetComponent<TextMeshProUGUI>();
                    if (tmpTextComponent != null)
                    {
                        combinedText += tmpTextComponent.text + ", "; // Add a space between control tips
                    }
                    else
                    {
                        Debug.LogError($"[ControlTipPatch] TMP Text component not found on ControlTip{i} GameObject.");
                    }
                }
                else
                {
                    Debug.LogError($"[ControlTipPatch] ControlTip{i} GameObject not found.");
                }
            }

            if (!string.IsNullOrEmpty(combinedText))
            {
                Debug.Log("[ControlTipPatch] Speaking control tips: " + combinedText);
                Utilities.SpeakText(combinedText);
            }
        }
    }
}