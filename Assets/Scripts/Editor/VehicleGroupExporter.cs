using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Captures cargrp.dat - which vehicles belong with which kind of population.
    ///
    /// Lines look like:
    ///     bravura, taxi, cabbie, premier, ...    # POPCYCLE_GROUP_BUSINESS
    ///
    /// The group names match pedgrp.dat's, which is the whole point: a district already resolves to a
    /// population mix through popcycle, and that same mix picks the vehicles parked and driving there.
    /// </summary>
    public static class VehicleGroupExporter
    {
        private const string OutputPath = "Assets/ExportedAssets/VehicleGroups.asset";

        public static void Export()
        {
            string path = Path.Combine(UGameCore.Utilities.Config.GetPath("game_dir"), "data/cargrp.dat");

            if (!File.Exists(path))
            {
                Debug.LogError("cargrp.dat not found under the game directory");
                return;
            }

            var groupNames = new List<string>();
            var groupStarts = new List<int>();
            var groupCounts = new List<int>();
            var members = new List<string>();

            int unnamed = 0;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();

                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                string name;
                string body;

                int hash = line.IndexOf('#');
                if (hash >= 0)
                {
                    body = line.Substring(0, hash);
                    name = line.Substring(hash + 1).Trim();
                }
                else
                {
                    body = line;
                    name = $"GROUP_{unnamed++}";
                }

                var models = body
                    .Split(',')
                    .Select(_ => _.Trim())
                    .Where(_ => _.Length > 0)
                    .ToArray();

                if (models.Length == 0)
                    continue;

                groupNames.Add(name);
                groupStarts.Add(members.Count);
                groupCounts.Add(models.Length);
                members.AddRange(models);
            }

            var asset = ScriptableObject.CreateInstance<SanAndreasUnity.Export.GtaVehicleGroupData>();
            asset.groupNames = groupNames.ToArray();
            asset.groupStarts = groupStarts.ToArray();
            asset.groupCounts = groupCounts.ToArray();
            asset.memberModels = members.ToArray();

            if (AssetDatabase.LoadAssetAtPath<SanAndreasUnity.Export.GtaVehicleGroupData>(OutputPath) != null)
                AssetDatabase.DeleteAsset(OutputPath);

            AssetDatabase.CreateAsset(asset, OutputPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"Vehicle groups exported: {groupNames.Count} groups, {members.Count} entries " +
                $"({members.Distinct(System.StringComparer.OrdinalIgnoreCase).Count()} distinct) " +
                $"-> {OutputPath}");

            if (groupNames.Count > 1)
                Debug.Log($"  e.g. '{groupNames[1]}': {string.Join(", ", members.Skip(groupStarts[1]).Take(groupCounts[1]))}");
        }
    }
}
