using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UGameCore.Utilities;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Captures map.zon - the boundaries of Los Santos, San Fierro and Las Venturas.
    ///
    /// Entries look like:
    ///     LA01, 3, 480.0, -3000.0, -500.0, 3000.0, -850.0, 500.0, 1, UNUSED
    ///
    /// which is name, type, min x/y/z, max x/y/z, island, label. GTA's coordinates are Z-up, so the y and z
    /// components swap on the way into Unity - the same conversion the rest of the importer does.
    /// </summary>
    public static class CityRegionExporter
    {
        private const string OutputPath = "Assets/ExportedAssets/CityRegions.asset";

        public static void Export()
        {
            string path = Importing.Archive.ArchiveManager.PathToCaseSensitivePath(
                Path.Combine(Config.GetPath("game_dir"), "data/map.zon"));

            if (!File.Exists(path))
            {
                Debug.LogError($"map.zon not found at {path}");
                return;
            }

            var names = new List<string>();
            var mins = new List<Vector3>();
            var maxs = new List<Vector3>();
            var islands = new List<int>();

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();

                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    continue;

                if (line == "zone" || line == "end")
                    continue;

                string[] parts = line.Split(',');
                if (parts.Length < 9)
                    continue;

                string name = parts[0].Trim();

                float x1 = ParseFloat(parts[2]);
                float y1 = ParseFloat(parts[3]);
                float z1 = ParseFloat(parts[4]);
                float x2 = ParseFloat(parts[5]);
                float y2 = ParseFloat(parts[6]);
                float z2 = ParseFloat(parts[7]);

                int island = (int)ParseFloat(parts[8]);

                // GTA is Z-up: its y is Unity's z, its z is Unity's y
                var a = new Vector3(x1, z1, y1);
                var b = new Vector3(x2, z2, y2);

                names.Add(name);
                mins.Add(Vector3.Min(a, b));
                maxs.Add(Vector3.Max(a, b));
                islands.Add(island);
            }

            var asset = ScriptableObject.CreateInstance<SanAndreasUnity.Export.GtaCityRegionData>();
            asset.names = names.ToArray();
            asset.mins = mins.ToArray();
            asset.maxs = maxs.ToArray();
            asset.islands = islands.ToArray();

            if (AssetDatabase.LoadAssetAtPath<SanAndreasUnity.Export.GtaCityRegionData>(OutputPath) != null)
                AssetDatabase.DeleteAsset(OutputPath);

            AssetDatabase.CreateAsset(asset, OutputPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"City regions exported: {names.Count} regions -> {OutputPath}");

            for (int i = 0; i < names.Count; i++)
                Debug.Log($"  {names[i]} (island {islands[i]}): {mins[i]} .. {maxs[i]}");
        }

        private static float ParseFloat(string token)
        {
            return float.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? v
                : 0f;
        }
    }
}
