using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Captures GTA's enex markers as teleport destinations.
    ///
    /// Both exterior and interior enexes are exported. Interiors were originally skipped on the assumption
    /// that their geometry was not part of the export - that turned out to be wrong, as the world covers
    /// every cell and the interior models are all present. Rather than guess again, the scene builder drops
    /// any destination with no world geometry near it, so the list can never offer a teleport into nothing.
    /// </summary>
    public static class TeleportDataExporter
    {
        private const string OutputPath = "Assets/ExportedAssets/TeleportDestinations.asset";

        public static void Export()
        {
            var enexes = Importing.Items.Item.Enexes;

            if (enexes == null || enexes.Count == 0)
                throw new Exception("No enexes loaded - game data must be loaded first");

            var names = new List<string>();
            var positions = new List<Vector3>();
            var headings = new List<float>();
            var interiorList = new List<int>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var enex in enexes)
            {
                string name = (enex.Name ?? string.Empty).Trim();
                if (name.Length == 0)
                    continue;

                // enex names repeat across the map - keep the first of each so the list reads as a set of
                // places rather than the same shop name a dozen times
                if (!seen.Add(name))
                    continue;

                names.Add(name);
                positions.Add(enex.ExitPos);
                headings.Add(enex.ExitAngle);
                interiorList.Add(enex.TargetInterior);
            }

            var asset = ScriptableObject.CreateInstance<Export.GtaTeleportData>();
            asset.names = names.ToArray();
            asset.positions = positions.ToArray();
            asset.headings = headings.ToArray();
            asset.interiors = interiorList.ToArray();

            if (AssetDatabase.LoadAssetAtPath<Export.GtaTeleportData>(OutputPath) != null)
                AssetDatabase.DeleteAsset(OutputPath);

            AssetDatabase.CreateAsset(asset, OutputPath);
            AssetDatabase.SaveAssets();

            int interiors = interiorList.Count(_ => _ != 0);

            Debug.Log($"Teleport destinations exported: {names.Count} of {enexes.Count} enexes " +
                $"({interiors} interior, {names.Count - interiors} exterior) -> {OutputPath}");
        }
    }
}
