using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LethalAccess.Patches
{
    // Patch for HUDManager to track the visibility of the clock
    [HarmonyPatch(typeof(HUDManager), nameof(HUDManager.SetClockVisible))]
    public static class HUDManagerSetClockVisiblePatch
    {
        public static bool IsClockVisible { get; private set; }

        static void Postfix(bool visible)
        {
            IsClockVisible = visible;
        }
    }

    // Patch for TimeOfDay to speak time when 'T' key is pressed
    [HarmonyPatch(typeof(TimeOfDay))]
    public class TimeOfDayPatch : MonoBehaviour
    {
        private const string SpeakTimeKeybindName = "SpeakTimeKey";
        private const Key SpeakTimeDefaultKey = Key.T;

        public void Initialize()
        {
            Debug.Log("TimeOfDayPatch: Initializing input actions.");
            LACore.Instance.RegisterKeybind(SpeakTimeKeybindName, SpeakTimeDefaultKey, SpeakTimeAction);
            Debug.Log("TimeOfDayPatch: Input actions are registered.");

            var harmony = new Harmony("green.lethalaccess.timeofdaypatch");
            harmony.PatchAll(typeof(TimeOfDayPatch));
        }

        private void SpeakTimeAction()
        {
            TimeOfDay timeOfDay = TimeOfDay.Instance;
            if (timeOfDay != null)
            {
                if (HUDManagerSetClockVisiblePatch.IsClockVisible)
                {
                    SpeakCurrentTime(timeOfDay);
                }
                else
                {
                    Utilities.SpeakText("You must be outside to check the time.");
                }
            }
        }

        private static void SpeakCurrentTime(TimeOfDay timeOfDay)
        {
            int totalMinutes = (int)(timeOfDay.currentDayTime % timeOfDay.lengthOfHours);
            int hour = timeOfDay.hour + 6; // Adjust to start at 6 AM instead of 1 AM
            int minutes = totalMinutes % 60;
            // Adjust for 24-hour cycle
            if (hour >= 24) hour -= 24;
            string amPm = hour >= 12 ? "PM" : "AM";
            hour = hour % 12;
            hour = hour == 0 ? 12 : hour; // Convert 0 to 12 for 12-hour clock

            Utilities.SpeakText($"{hour}:{minutes:D2} {amPm}");
        }
    }
}