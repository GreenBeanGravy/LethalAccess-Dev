using BepInEx;
using HarmonyLib;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace LethalAccess
{
    [BepInPlugin("LethalAccess.NavMesh", "NavMesh", "1.0.0")]
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
                    // ORDER MATTERS. This used to destroy every vanilla AI node FIRST and then
                    // instantiate the replacement. Green.CompNavMesh.bundle was built against an
                    // older Unity, and on the current build its prefab loses its MonoBehaviours:
                    //   "The referenced script on this Behaviour (Game Object 'CompanyNavSurface') is missing!"
                    //   "The referenced script (NodeHelper) on this Behaviour is missing!"
                    // so the old order deleted working enemy-AI nodes - for every player in the
                    // lobby, not just the mod user - and installed a non-functional replacement.
                    //
                    // Now: build and validate the replacement first, and only touch the vanilla
                    // nodes once it is known good. If it is not, leave vanilla alone and say so.
                    if (navPrefab == null)
                    {
                        Debug.LogError("[LethalAccess] Company navigation prefab was not loaded; " +
                                       "leaving the game's own AI nodes intact.");
                        return;
                    }

                    Debug.Log("Instantiating Company Navigation!");
                    Transform navTransform = UnityEngine.Object.Instantiate(navPrefab, __instance.transform.parent, true).transform;
                    navTransform.position = Vector3.zero;
                    navTransform.rotation = Quaternion.identity;
                    navTransform.localScale = Vector3.one;

                    // The prefab must supply an outside-node container and an inside-node
                    // container, each with children. If its scripts failed to load, or its shape
                    // has changed, this is where we find out - before anything is destroyed.
                    bool prefabUsable =
                        navTransform.childCount >= 2 &&
                        navTransform.GetChild(0).childCount > 0 &&
                        navTransform.GetChild(1).childCount > 0;

                    if (!prefabUsable)
                    {
                        Debug.LogError("[LethalAccess] Company navigation prefab did not instantiate " +
                                       $"usable node containers (childCount={navTransform.childCount}). " +
                                       "The asset bundle needs rebuilding against this Unity version. " +
                                       "Leaving the game's own AI nodes intact.");
                        UnityEngine.Object.Destroy(navTransform.gameObject);
                        return;
                    }

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