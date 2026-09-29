using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Reports which material and texture files belong to vehicles, and whether anything that is not a
    /// vehicle depends on them.
    ///
    /// Vehicle materials are named "model-rendererIndex-materialIndex" and reused by path when they exist,
    /// so they went stale when wheel cloning shifted the renderer order. Regenerating them means deleting
    /// them, and Materials/ and Textures/ are shared with every other exported category - a bulk delete from
    /// a shared folder already cost most of a day once. This lists exactly what would go, and whether
    /// anything else still points at it, before anything is deleted.
    /// </summary>
    public static class GtaVehicleAssetDependencyCheck
    {
        public static void Run()
        {
            string outPath = GetArg("depOut", "scratchpad/vehicle_assets.txt");

            string[] vehiclePrefabs = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ExportedAssets/Prefabs/Vehicles" })
                .Select(AssetDatabase.GUIDToAssetPath).ToArray();

            var vehicleNames = new HashSet<string>(
                vehiclePrefabs.Select(p => System.IO.Path.GetFileNameWithoutExtension(p)),
                System.StringComparer.OrdinalIgnoreCase);

            Debug.Log($"DEPCHECK vehicle prefabs={vehiclePrefabs.Length} names={vehicleNames.Count}");

            // "name-N-N" - the exporter's per-renderer naming
            var pattern = new Regex(@"^(?<model>.+)-\d+-\d+$");

            var candidates = new List<string>();
            foreach (string folder in new[] { "Assets/ExportedAssets/Materials", "Assets/ExportedAssets/Textures" })
            {
                if (!AssetDatabase.IsValidFolder(folder))
                    continue;

                foreach (string guid in AssetDatabase.FindAssets("", new[] { folder }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (AssetDatabase.IsValidFolder(path))
                        continue;

                    var m = pattern.Match(System.IO.Path.GetFileNameWithoutExtension(path));
                    if (m.Success && vehicleNames.Contains(m.Groups["model"].Value))
                        candidates.Add(path);
                }
            }

            var byFolder = candidates.GroupBy(p => System.IO.Path.GetDirectoryName(p).Replace(System.IO.Path.DirectorySeparatorChar, '/'))
                .ToDictionary(g => g.Key, g => g.Count());
            foreach (var kv in byFolder)
                Debug.Log($"DEPCHECK candidates in {kv.Key}: {kv.Value}");

            var candidateSet = new HashSet<string>(candidates);

            // does anything that is NOT a vehicle depend on them?
            var nonVehicle = new List<string>();
            foreach (string folder in new[] { "Assets/ExportedAssets/Prefabs/MapObjects", "Assets/ExportedAssets/Prefabs/Peds",
                         "Assets/ExportedAssets/Prefabs/Weapons", "Assets/ExportedAssets/Prefabs/Pickups" })
            {
                if (!AssetDatabase.IsValidFolder(folder))
                    continue;
                nonVehicle.AddRange(AssetDatabase.FindAssets("t:Prefab", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath));
            }

            // also every other prefab directly under Prefabs/ that is not in the Vehicles subfolder
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/ExportedAssets/Prefabs" }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (!p.Contains("/Prefabs/Vehicles/") && !nonVehicle.Contains(p))
                    nonVehicle.Add(p);
            }

            int referenced = 0;
            var examples = new List<string>();
            var referencedSet = new HashSet<string>();

            foreach (string prefab in nonVehicle)
            {
                foreach (string dep in AssetDatabase.GetDependencies(prefab, true))
                {
                    if (candidateSet.Contains(dep) && referencedSet.Add(dep))
                    {
                        referenced++;
                        if (examples.Count < 15)
                            examples.Add($"{dep}  <-  {prefab}");
                    }
                }
            }

            Debug.Log($"DEPCHECK non-vehicle prefabs scanned={nonVehicle.Count}");
            Debug.Log($"DEPCHECK candidate vehicle assets={candidates.Count}");
            Debug.Log($"DEPCHECK candidates ALSO used by non-vehicles={referenced}");
            foreach (string e in examples)
                Debug.Log($"DEPCHECK shared: {e}");

            System.IO.File.WriteAllLines(outPath, candidates.Except(referencedSet));
            System.IO.File.WriteAllLines(outPath.Replace(".txt", "_shared.txt"), referencedSet);
            Debug.Log($"DEPCHECK safe list written: {candidates.Count - referenced} paths -> {outPath}");

            EditorApplication.Exit(0);
        }

        private static string GetArg(string name, string fallback)
        {
            string search = "-" + name + ":";
            string arg = System.Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith(search));
            return arg == null ? fallback : arg.Substring(search.Length);
        }
    }
}
