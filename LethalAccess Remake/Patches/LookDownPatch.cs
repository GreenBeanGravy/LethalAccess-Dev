using GameNetcodeStuff;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;

namespace LethalAccess
{
    public static class LookDownPatchConfig
    {
        public static float MaxAngle = 90f;
    }

    /// <summary>
    /// Both patched methods clamp the camera pitch with Mathf.Clamp(cameraUp, -80f, limit).
    /// A positive local X euler angle is downward pitch, so the upper bound is the look-down
    /// limit and that is the constant being raised to MaxAngle.
    ///
    /// The limits differ per method in the current game build:
    ///   CalculateSmoothLookingInput  -> Mathf.Clamp(cameraUp, -80f, 60f)
    ///   CalculateNormalLookingInput  -> Mathf.Clamp(cameraUp, -80f, 80f)
    ///
    /// The normal-look patch previously searched for 60f, which does not occur in that method
    /// at all, so it matched nothing and returned the instruction stream untouched - a silent
    /// no-op with no error. Any future mismatch now logs an explicit error instead.
    /// </summary>
    internal static class LookDownTranspilerCore
    {
        internal static IEnumerable<CodeInstruction> ReplaceFloat(
            IEnumerable<CodeInstruction> instructions, float expected, string methodName)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_R4 && (float)list[i].operand == expected)
                {
                    list[i].operand = LookDownPatchConfig.MaxAngle;
                    return list;
                }
            }

            Debug.LogError(
                $"[LethalAccess] LookDownPatch: no 'ldc.r4 {expected}' found in " +
                $"PlayerControllerB.{methodName}. The look-down limit was NOT raised - the game's " +
                "pitch clamp has changed and this transpiler needs updating.");

            return list;
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "CalculateSmoothLookingInput")]
    internal class AdjustSmoothLookingPatcher
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return LookDownTranspilerCore.ReplaceFloat(instructions, 60f, "CalculateSmoothLookingInput");
        }
    }

    [HarmonyPatch(typeof(PlayerControllerB), "CalculateNormalLookingInput")]
    internal class AdjustNormalLookingPatcher
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return LookDownTranspilerCore.ReplaceFloat(instructions, 80f, "CalculateNormalLookingInput");
        }
    }
}
