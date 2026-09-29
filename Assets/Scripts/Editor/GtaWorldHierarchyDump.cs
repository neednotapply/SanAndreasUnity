using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Prints the shape of the exported world prefab: how deep it nests and where the map object
    /// instances actually sit. Written because the placement reader assumed the instances were direct
    /// children of the root and read six of forty-five thousand.
    /// </summary>
    public static class GtaWorldHierarchyDump
    {
        public static void Run()
        {
            const string path = "Assets/ExportedAssets/Prefabs/ExportedWorldFromGameFiles.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);

            try
            {
                var byDepth = new SortedDictionary<int, int>();
                var instanceRootsByDepth = new SortedDictionary<int, int>();
                int total = 0, instanceRoots = 0, withSource = 0;

                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    int depth = 0;
                    for (var p = t; p != root.transform; p = p.parent) depth++;

                    total++;
                    byDepth[depth] = byDepth.TryGetValue(depth, out int a) ? a + 1 : 1;

                    if (PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject))
                    {
                        instanceRoots++;
                        instanceRootsByDepth[depth] = instanceRootsByDepth.TryGetValue(depth, out int b) ? b + 1 : 1;
                    }

                    if (PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject) != null)
                        withSource++;
                }

                Debug.Log($"WORLDDUMP root='{root.name}' directChildren={root.transform.childCount} " +
                    $"totalTransforms={total} prefabInstanceRoots={instanceRoots} withSource={withSource}");

                foreach (var kv in byDepth)
                    Debug.Log($"WORLDDUMP depth {kv.Key}: {kv.Value} transforms, " +
                        $"{(instanceRootsByDepth.TryGetValue(kv.Key, out int r) ? r : 0)} are prefab instance roots");

                int shown = 0;
                foreach (Transform child in root.transform)
                {
                    Debug.Log($"WORLDDUMP child '{child.name}' children={child.childCount} " +
                        $"pos={child.localPosition} isInstanceRoot={PrefabUtility.IsAnyPrefabInstanceRoot(child.gameObject)}");
                    if (++shown >= 8) break;
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            EditorApplication.Exit(0);
        }
    }
}
