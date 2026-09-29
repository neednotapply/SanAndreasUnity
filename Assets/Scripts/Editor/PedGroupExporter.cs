using System.Collections.Generic;
using System.IO;
using System.Linq;
using UGameCore.Utilities;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Captures pedgrp.dat - which pedestrian models the game spawns together.
    ///
    /// Lines look like:
    ///     HMOGAR, WMYCON, WMYCONB, WMYMECH, WMYSGRD    # POPCYCLE_GROUP_WORKERS
    ///
    /// The trailing comment names the group; the list before it is the members. Groups repeat with (SF) and
    /// (VEGAS) suffixes for the other two cities, which is how San Fierro's crowd differs from Los Santos'.
    /// </summary>
    public static class PedGroupExporter
    {
        private const string OutputPath = "Assets/ExportedAssets/PedGroups.asset";

        public static void Export()
        {
            string path = Importing.Archive.ArchiveManager.PathToCaseSensitivePath(
                Path.Combine(Config.GetPath("game_dir"), "data/pedgrp.dat"));

            if (!File.Exists(path))
            {
                Debug.LogError($"pedgrp.dat not found at {path}");
                return;
            }

            var groupNames = new List<string>();
            var groupStarts = new List<int>();
            var groupCounts = new List<int>();
            var groupCities = new List<int>();
            var members = new List<string>();

            int unnamed = 0;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();

                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                // split the members from the trailing "# GROUP_NAME" comment
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

                // "(SF)" / "(VEGAS)" suffixes mark the San Fierro and Las Venturas variants
                int city = 0;
                if (name.IndexOf("(SF)", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    city = 1;
                else if (name.IndexOf("(VEGAS)", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    city = 2;

                groupNames.Add(name);
                groupCities.Add(city);
                groupStarts.Add(members.Count);
                groupCounts.Add(models.Length);
                members.AddRange(models);
            }

            var asset = ScriptableObject.CreateInstance<SanAndreasUnity.Export.GtaPedGroupData>();
            asset.groupNames = groupNames.ToArray();
            asset.groupStarts = groupStarts.ToArray();
            asset.groupCounts = groupCounts.ToArray();
            asset.groupCities = groupCities.ToArray();
            asset.memberModels = members.ToArray();

            if (AssetDatabase.LoadAssetAtPath<SanAndreasUnity.Export.GtaPedGroupData>(OutputPath) != null)
                AssetDatabase.DeleteAsset(OutputPath);

            AssetDatabase.CreateAsset(asset, OutputPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"  city split: {groupCities.Count(_ => _ == 0)} Los Santos, " +
                $"{groupCities.Count(_ => _ == 1)} San Fierro, {groupCities.Count(_ => _ == 2)} Las Venturas");

            Debug.Log($"Ped groups exported: {groupNames.Count} groups, {members.Count} entries " +
                $"({members.Distinct(System.StringComparer.OrdinalIgnoreCase).Count()} distinct models) " +
                $"-> {OutputPath}");

            if (groupNames.Count > 0)
            {
                Debug.Log($"  e.g. '{groupNames[0]}': " +
                    $"{string.Join(", ", members.Take(groupCounts[0]))}");
            }
        }
    }
}
