using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Captures popcycle.dat - how many people each kind of district supports, and who they are.
    ///
    /// The file is twenty blocks, one per zone category in the order of eZonePopulationType, and each block
    /// holds twelve weekday rows followed by twelve weekend rows at two-hour intervals. A row is:
    ///
    ///     #Peds #Cars Dealers Gang Cops Other  then eighteen ped-group percentages
    ///
    /// Both sets are kept. The weekend rows are not decoration - a business district is far emptier on a
    /// Sunday than a Tuesday - and the world derives a day of the week from the same clock that drives the
    /// time of day, so the distinction is observable.
    /// </summary>
    public static class PopCycleExporter
    {
        private const string OutputPath = "Assets/ExportedAssets/PopCycle.asset";

        /// <summary> #Peds #Cars Dealers Gang Cops Other, before the group percentages. </summary>
        private const int HeaderColumns = 6;

        /// <summary> Group columns in file order. These match pedgrp.dat's POPCYCLE_GROUP_* names. </summary>
        private static readonly string[] GroupColumns =
        {
            "WORKERS", "BUSINESS", "CLUBBERS", "FARMERS", "BEACHFOLK", "PARKFOLK",
            "CASUAL_RICH", "CASUAL_AVERAGE", "CASUAL_POOR", "PROZZIES", "CRIMINALS", "GOLFERS",
            "SERVANTS", "AIRCREW", "ENTERTAINERS", "OUT_OF_TOWN_FACTORY", "DESERTFOLK",
            "AIRCREW_RUNWAY",
        };

        public static void Export()
        {
            string path = ResolveGameFile("data/popcycle.dat");

            if (path == null)
            {
                Debug.LogError("popcycle.dat not found under the game directory");
                return;
            }

            int types = SanAndreasUnity.Export.GtaPopCycleData.ZoneTypeCount;
            int slots = SanAndreasUnity.Export.GtaPopCycleData.SlotCount;
            int groups = SanAndreasUnity.Export.GtaPopCycleData.GroupCount;

            int dayKinds = SanAndreasUnity.Export.GtaPopCycleData.DayKindCount;

            var percent = new int[types * slots * dayKinds * groups];
            var maxPeds = new int[types * slots * dayKinds];
            var maxCars = new int[types * slots * dayKinds];

            // Block and section are read from the file's own labels rather than counted.
            //
            // Counting separator lines does not work: each category is wrapped in several rows of slashes,
            // so a naive count reported sixty categories instead of twenty and threw every row into the
            // wrong block. The file names each category ("// GANGLAND") and labels its two halves
            // ("// Weekday", "// Weekend"), which is unambiguous.
            int block = -1;
            int row = 0;
            int dayKind = -1;
            int rowsRead = 0;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();

                if (line.Length == 0)
                    continue;

                if (line.StartsWith("//"))
                {
                    string body = line.TrimStart('/').Trim();

                    if (body.Length == 0)
                        continue;

                    string firstWord = body.Split(new[] { ' ', '	', '-' },
                        System.StringSplitOptions.RemoveEmptyEntries)[0];

                    if (firstWord.Equals("Weekday", System.StringComparison.OrdinalIgnoreCase))
                    {
                        dayKind = 0;
                        row = 0;
                        continue;
                    }

                    if (firstWord.Equals("Weekend", System.StringComparison.OrdinalIgnoreCase))
                    {
                        dayKind = 1;
                        row = 0;
                        continue;
                    }

                    int named = System.Array.FindIndex(
                        ZoneTypeExtractor.TypeNames,
                        _ => _.Equals(firstWord, System.StringComparison.OrdinalIgnoreCase));

                    if (named >= 0)
                    {
                        block = named;
                        row = 0;
                        dayKind = -1;
                    }

                    continue;
                }

                if (block < 0 || block >= types || dayKind < 0 || row >= slots)
                    continue;

                string[] tokens = line.Split(
                    new[] { ' ', '	' }, System.StringSplitOptions.RemoveEmptyEntries);

                if (tokens.Length < HeaderColumns + groups)
                    continue;

                int density = SanAndreasUnity.Export.GtaPopCycleData.DensityIndex(block, row, dayKind);

                maxPeds[density] = ParseInt(tokens[0]);
                maxCars[density] = ParseInt(tokens[1]);

                for (int g = 0; g < groups; g++)
                {
                    percent[SanAndreasUnity.Export.GtaPopCycleData.Index(block, row, dayKind, g)] =
                        ParseInt(tokens[HeaderColumns + g]);
                }

                rowsRead++;
                row++;
            }

            var asset = ScriptableObject.CreateInstance<SanAndreasUnity.Export.GtaPopCycleData>();
            asset.groupNames = GroupColumns;
            asset.groupPercent = percent;
            asset.maxPeds = maxPeds;
            asset.maxCars = maxCars;

            if (AssetDatabase.LoadAssetAtPath<SanAndreasUnity.Export.GtaPopCycleData>(OutputPath) != null)
                AssetDatabase.DeleteAsset(OutputPath);

            AssetDatabase.CreateAsset(asset, OutputPath);
            AssetDatabase.SaveAssets();

            int typesWithData = 0;
            for (int t = 0; t < types; t++)
            {
                if (maxPeds[SanAndreasUnity.Export.GtaPopCycleData.DensityIndex(t, 6, 0)] > 0)
                    typesWithData++;
            }

            Debug.Log($"Pop cycle exported: {typesWithData}/{types} categories populated, " +
                $"{rowsRead} rows (expected {types * slots * dayKinds}) -> {OutputPath}");

            // worked examples, so a misparse shows up rather than passing silently
            LogExample(asset, 7, 0, 0, "GANGLAND midnight weekday");
            LogExample(asset, 0, 6, 0, "BUSINESS noon weekday");
            LogExample(asset, 0, 6, 1, "BUSINESS noon weekend");
            LogExample(asset, 8, 6, 1, "BEACH noon weekend");
        }

        private static void LogExample(
            SanAndreasUnity.Export.GtaPopCycleData data, int type, int slot, int dayKind, string label)
        {
            var parts = new List<string>();

            for (int g = 0; g < SanAndreasUnity.Export.GtaPopCycleData.GroupCount; g++)
            {
                int value = data.groupPercent[
                    SanAndreasUnity.Export.GtaPopCycleData.Index(type, slot, dayKind, g)];
                if (value > 0)
                    parts.Add($"{data.groupNames[g]} {value}%");
            }

            int density = data.maxPeds[
                SanAndreasUnity.Export.GtaPopCycleData.DensityIndex(type, slot, dayKind)];

            Debug.Log($"  {label}: {density} peds - {string.Join(", ", parts)}");
        }

        private static int ParseInt(string token)
        {
            return int.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v
                : 0;
        }

        private static string ResolveGameFile(string relativePath)
        {
            string direct = Path.Combine(UGameCore.Utilities.Config.GetPath("game_dir"), relativePath);
            return File.Exists(direct) ? direct : null;
        }
    }
}
