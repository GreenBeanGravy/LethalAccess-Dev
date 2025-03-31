using HarmonyLib;

namespace Green.LethalAccessPlugin.Patches
{
    [HarmonyPatch(typeof(StartOfRound))]
    public class SwitchLevelPatch
    {
        private static string lastMoon;

        [HarmonyPostfix]
        [HarmonyPatch(nameof(StartOfRound.ChangeLevel))]
        public static void AnnounceMoonChange(StartOfRound __instance, int levelID)
        {
            if (lastMoon != __instance.currentLevel.PlanetName)
            {
                lastMoon = __instance.currentLevel.PlanetName;
                Utilities.SpeakText("Navigated to " + __instance.currentLevel.PlanetName);

                // Weather alert logic
                if (__instance.currentLevel.currentWeather != LevelWeatherType.None)
                {
                    string weatherAlert = __instance.currentLevel.PlanetName + " is currently experiencing " + __instance.currentLevel.currentWeather.ToString();
                    Utilities.SpeakText(weatherAlert);
                }
            }
        }

        // Function to speak the current moon
        public static void SpeakCurrentMoon(StartOfRound instance)
        {
            if (instance != null && instance.currentLevel != null)
            {
                Utilities.SpeakText("Current moon: " + instance.currentLevel.PlanetName);
            }
            else
            {
                Utilities.SpeakText("Current moon information is not available.");
            }
        }
    }
}