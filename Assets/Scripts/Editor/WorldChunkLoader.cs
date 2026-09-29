using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Loads part of the exported world without instantiating the whole thing.
    ///
    /// The world prefab holds ~45k map objects as prefab instances. Instantiating it in one call crashes
    /// the Editor in batch mode, and loading the entire city only to delete most of it is wasteful. Instead
    /// we read the placements out of the prefab through the AssetDatabase - each instance is just a source
    /// prefab plus a transform, thanks to the deduplication pass - and instantiate only what we want.
    /// </summary>
    public static class WorldChunkLoader
    {
        public struct Placement
        {
            public string guid;
            public Vector3 position;
            public Quaternion rotation;
        }

        /// <summary>
        /// Streams the world prefab and returns every map object placement. Streaming matters: the file is
        /// ~128 MB and reading it whole is needlessly heavy.
        /// </summary>
        /// <summary>
        /// Reads every map object placement out of the exported world prefab.
        ///
        /// This used to parse the prefab's YAML directly. That broke the moment the project switched to
        /// binary asset serialization: the parser silently found no instances and the world came out empty,
        /// with nothing to indicate why. Going through the AssetDatabase works whatever the serialization
        /// mode is, and cannot drift out of step with Unity's file format again.
        ///
        /// The prefab is opened rather than instantiated - PrefabUtility.LoadPrefabContents gives a real
        /// hierarchy to walk without adding 45k objects to the open scene, which is what crashed the Editor
        /// in batch mode when this was done the obvious way.
        /// </summary>
        public static List<Placement> ParsePlacements(string prefabPath)
        {
            var placements = new List<Placement>();

            if (null == AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath))
            {
                Debug.LogError($"World prefab not found at {prefabPath}");
                return placements;
            }

            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);

            try
            {
                int missingSource = 0;

                // The instances are not direct children of the root: they sit under a "Cell" object beside
                // the water, the spawn markers and the camera. Reading only the root's children found six
                // of forty-five thousand and produced an empty world with no error, so this walks the whole
                // hierarchy and takes every prefab instance root - which is exactly the set the old YAML
                // reader found, and the count is checked against that below.
                foreach (Transform child in contents.GetComponentsInChildren<Transform>(true))
                {
                    if (child == contents.transform)
                        continue;

                    if (!PrefabUtility.IsAnyPrefabInstanceRoot(child.gameObject))
                        continue;

                    // each one is an instance of one map object prefab
                    GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(child.gameObject);

                    if (null == source)
                    {
                        missingSource++;
                        continue;
                    }

                    string sourcePath = AssetDatabase.GetAssetPath(source);
                    string guid = AssetDatabase.AssetPathToGUID(sourcePath);

                    if (string.IsNullOrEmpty(guid))
                    {
                        missingSource++;
                        continue;
                    }

                    placements.Add(new Placement
                    {
                        guid = guid,
                        position = child.position,
                        rotation = child.rotation,
                    });
                }

                if (missingSource > 0)
                {
                    Debug.LogWarning($"{missingSource} world objects had no resolvable source prefab " +
                        "and were skipped");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            if (placements.Count < 1000)
            {
                Debug.LogError(
                    $"Only {placements.Count} placements read from {prefabPath}. The full world has about " +
                    "45,000, so this is almost certainly a reader fault rather than a small world.");
            }

            return placements;
        }

        private static void EmitPlacement(
            List<Placement> placements, string guid, Dictionary<string, float> values)
        {
            if (string.IsNullOrEmpty(guid))
                return;

            // a placement with no position override sits at the prefab's own origin, which for map
            // objects means it wasn't a real placement - skip it
            if (!values.ContainsKey("m_LocalPosition.x"))
                return;

            placements.Add(new Placement
            {
                guid = guid,
                position = new Vector3(
                    GetOrDefault(values, "m_LocalPosition.x", 0f),
                    GetOrDefault(values, "m_LocalPosition.y", 0f),
                    GetOrDefault(values, "m_LocalPosition.z", 0f)),
                rotation = new Quaternion(
                    GetOrDefault(values, "m_LocalRotation.x", 0f),
                    GetOrDefault(values, "m_LocalRotation.y", 0f),
                    GetOrDefault(values, "m_LocalRotation.z", 0f),
                    GetOrDefault(values, "m_LocalRotation.w", 1f)),
            });
        }

        private static float GetOrDefault(Dictionary<string, float> values, string key, float defaultValue)
        {
            return values.TryGetValue(key, out float v) ? v : defaultValue;
        }

        /// <summary>
        /// Builds the whole city, grouped into spatial cells that can be streamed at runtime.
        ///
        /// Every object is instantiated once here, at build time, because Udon cannot instantiate at
        /// runtime. Grouping under cell roots is what makes streaming affordable: the runtime toggles a few
        /// hundred parents instead of 45,000 individual objects.
        ///
        /// Returns the cell roots and their centres, in matching order.
        /// </summary>
        public static void BuildStreamingCells(
            List<Placement> placements,
            float cellSize,
            Transform parent,
            out List<GameObject> cellRoots,
            out List<Vector3> cellCenters)
        {
            var cells = new Dictionary<Vector2Int, List<Placement>>();

            // bucket placements by grid coordinate
            for (int i = 0; i < placements.Count; i++)
            {
                Placement p = placements[i];
                var key = new Vector2Int(
                    Mathf.FloorToInt(p.position.x / cellSize),
                    Mathf.FloorToInt(p.position.z / cellSize));

                if (!cells.TryGetValue(key, out List<Placement> list))
                {
                    list = new List<Placement>();
                    cells[key] = list;
                }

                list.Add(p);
            }

            Debug.Log($"Grouped {placements.Count} placements into {cells.Count} cells of {cellSize}m");

            cellRoots = new List<GameObject>(cells.Count);
            cellCenters = new List<Vector3>(cells.Count);

            var prefabCache = new Dictionary<string, GameObject>();
            int built = 0;
            int missing = 0;
            int cellIndex = 0;

            foreach (var kvp in cells)
            {
                Vector2Int key = kvp.Key;
                List<Placement> list = kvp.Value;

                var cellGo = new GameObject($"Cell_{key.x}_{key.y}");
                cellGo.transform.SetParent(parent);

                // centre from actual contents, not the grid square - cells at the map edge are lopsided
                // and a grid-square centre would misjudge their distance
                Vector3 sum = Vector3.zero;

                for (int i = 0; i < list.Count; i++)
                {
                    Placement p = list[i];

                    if (!prefabCache.TryGetValue(p.guid, out GameObject prefab))
                    {
                        string path = AssetDatabase.GUIDToAssetPath(p.guid);
                        prefab = string.IsNullOrEmpty(path)
                            ? null
                            : AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        prefabCache[p.guid] = prefab;
                    }

                    if (null == prefab)
                    {
                        missing++;
                        continue;
                    }

                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, cellGo.transform);
                    instance.transform.SetPositionAndRotation(p.position, p.rotation);

                    sum += p.position;
                    built++;
                }

                cellGo.transform.position = Vector3.zero; // children are already in world space
                cellRoots.Add(cellGo);
                cellCenters.Add(list.Count > 0 ? sum / list.Count : Vector3.zero);

                cellIndex++;
                if (cellIndex % 50 == 0)
                    Debug.Log($"  built {cellIndex}/{cells.Count} cells, {built} objects so far");
            }

            if (missing > 0)
                Debug.LogWarning($"{missing} placements referenced prefabs that could not be loaded");

            Debug.Log($"Streaming world built: {built} objects across {cellRoots.Count} cells");
        }

        /// <summary>
        /// Instantiates every placement within <paramref name="radius"/> of <paramref name="center"/>.
        /// Prefab assets are cached per GUID so a model used a thousand times is only loaded once.
        /// </summary>
        public static int InstantiateWithinRadius(
            List<Placement> placements, Vector3 center, float radius, Transform parent)
        {
            var prefabCache = new Dictionary<string, GameObject>();
            float sqrRadius = radius * radius;
            int instantiated = 0;
            int missing = 0;

            for (int i = 0; i < placements.Count; i++)
            {
                Placement p = placements[i];

                if ((p.position - center).sqrMagnitude > sqrRadius)
                    continue;

                if (!prefabCache.TryGetValue(p.guid, out GameObject prefab))
                {
                    string path = AssetDatabase.GUIDToAssetPath(p.guid);
                    prefab = string.IsNullOrEmpty(path)
                        ? null
                        : AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    prefabCache[p.guid] = prefab;
                }

                if (null == prefab)
                {
                    missing++;
                    continue;
                }

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
                instance.transform.SetPositionAndRotation(p.position, p.rotation);
                instantiated++;
            }

            if (missing > 0)
                Debug.LogWarning($"{missing} placements referenced prefabs that could not be loaded");

            return instantiated;
        }
    }
}
