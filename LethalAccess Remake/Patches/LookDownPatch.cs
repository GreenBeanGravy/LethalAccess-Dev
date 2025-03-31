using GameNetcodeStuff;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;

namespace Green.LethalAccessPlugin
{
    public static class LookDownPatchConfig
    {
        public static float MaxAngle = 90f;
    }

    [HarmonyPatch(typeof(PlayerControllerB), "CalculateSmoothLookingInput")]
    internal class AdjustSmoothLookingPatcher
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_R4 && (float)list[i].operand == 60f)
                {
                    list[i].operand = LookDownPatchConfig.MaxAngle;
                    break;
                }
            }
            return list.AsEnumerable();
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "CalculateNormalLookingInput")]
    internal class AdjustNormalLookingPatcher
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_R4 && (float)list[i].operand == 60f)
                {
                    list[i].operand = LookDownPatchConfig.MaxAngle;
                    break;
                }
            }
            return list.AsEnumerable();
        }
    }
}