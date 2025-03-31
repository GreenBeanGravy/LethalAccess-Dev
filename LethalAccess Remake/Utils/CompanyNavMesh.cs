using BepInEx;
using HarmonyLib;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Green.LethalAccessPlugin
{
    [BepInPlugin("Green.LethalAccess.NavMesh", "NavMesh", "1.0.0")]
    public class CompanyNavMesh : BaseUnityPlugin
    {
        private static GameObject navPrefab;

        private void Awake()
        {
            AssetBundle assetBundle = AssetBundle.LoadFromStream(ResourceUtils.Get(".bundle"));
            if (assetBundle != null)
            {
                navPrefab = assetBundle.LoadAsset<GameObject>("CompanyNavSurface.prefab");
                assetBundle.Unload(false);
            }
            else
            {
                Debug.LogError("Failed to load asset bundle.");
            }

            Harmony.CreateAndPatchAll(typeof(DepositItemsDeskPatch));
        }

        [HarmonyPatch(typeof(DepositItemsDesk), "Start")]
        internal class DepositItemsDeskPatch
        {
            [HarmonyPostfix]
            internal static void StartPatch(DepositItemsDesk __instance)
            {
                try
                {
                    GameObject[] outsideAINodes = GameObject.FindGameObjectsWithTag("OutsideAINode");
                    foreach (GameObject node in outsideAINodes)
                    {
                        UnityEngine.Object.Destroy(node);
                    }

                    GameObject[] insideAINodes = GameObject.FindGameObjectsWithTag("AINode");
                    foreach (GameObject node in insideAINodes)
                    {
                        UnityEngine.Object.Destroy(node);
                    }

                    Debug.Log("Instantiating Company Navigation!");
                    Transform navTransform = UnityEngine.Object.Instantiate(navPrefab, __instance.transform.parent, true).transform;
                    navTransform.position = Vector3.zero;
                    navTransform.rotation = Quaternion.identity;
                    navTransform.localScale = Vector3.one;

                    Transform outsideNodesTransform = navTransform.GetChild(0);
                    RoundManager.Instance.outsideAINodes = new GameObject[outsideNodesTransform.childCount];
                    for (int i = 0; i < outsideNodesTransform.childCount; i++)
                    {
                        RoundManager.Instance.outsideAINodes[i] = outsideNodesTransform.GetChild(i).gameObject;
                    }

                    Transform insideNodesTransform = navTransform.GetChild(1);
                    RoundManager.Instance.insideAINodes = new GameObject[insideNodesTransform.childCount];
                    for (int i = 0; i < insideNodesTransform.childCount; i++)
                    {
                        RoundManager.Instance.insideAINodes[i] = insideNodesTransform.GetChild(i).gameObject;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }
    }

    public static class ResourceUtils
    {
        public static Stream Get(string name)
        {
            Assembly executingAssembly = Assembly.GetExecutingAssembly();
            string[] manifestResourceNames = executingAssembly.GetManifestResourceNames();
            if (manifestResourceNames.Length == 0)
            {
                throw new FileNotFoundException("Assembly does not contain any resource stream names.");
            }
            string resourceName = manifestResourceNames.FirstOrDefault(n => n.EndsWith(name));
            if (string.IsNullOrEmpty(resourceName))
            {
                throw new FileNotFoundException($"Assembly does not contain a resource stream ending with '{name}'");
            }
            return executingAssembly.GetManifestResourceStream(resourceName);
        }
    }
}