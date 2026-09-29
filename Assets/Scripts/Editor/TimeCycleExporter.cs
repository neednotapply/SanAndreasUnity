using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UGameCore.Utilities;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Captures timecyc.dat - the game's authored sky and lighting.
    ///
    /// Each weather block holds eight lines, one per time of day, and each line is 51 numbers describing
    /// ambient light, sun colour, the sky gradient, cloud tints, water colour and fog. Only the values that
    /// can actually drive a VRChat scene are kept; the rest describe effects the original renderer had and
    /// Unity does not.
    /// </summary>
    public static class TimeCycleExporter
    {
        private const string OutputPath = "Assets/ExportedAssets/TimeCycle.asset";

        /// <summary> The hours timecyc's eight keyframes correspond to. </summary>
        private static readonly int[] KeyframeHours = { 0, 5, 6, 7, 12, 19, 20, 22 };

        // token offsets within a timecyc line
        private const int AmbientOffset = 0;
        private const int DirectionalOffset = 6;
        private const int SkyTopOffset = 9;
        private const int SkyBottomOffset = 12;
        private const int SunCoreOffset = 15;
        private const int FarClipOffset = 27;
        private const int FogStartOffset = 28;

        public static void Export()
        {
            // no config entry points at timecyc, so it is located relative to the game directory
            string path = Importing.Archive.ArchiveManager.PathToCaseSensitivePath(
                Path.Combine(Config.GetPath("game_dir"), "data/timecyc.dat"));

            if (!File.Exists(path))
            {
                Debug.LogError($"timecyc.dat not found at {path}");
                return;
            }

            var weatherNames = new List<string>();
            var ambient = new List<Color>();
            var directional = new List<Color>();
            var skyTop = new List<Color>();
            var skyBottom = new List<Color>();
            var sunCore = new List<Color>();
            var fogStart = new List<float>();
            var farClip = new List<float>();

            string currentWeather = null;
            int keyframesRead = 0;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();

                if (line.Length == 0)
                    continue;

                if (line.StartsWith("////////////"))
                {
                    // a new weather block: "//////////// EXTRASUNNY_LA"
                    currentWeather = line.TrimStart('/').Trim();
                    keyframesRead = 0;
                    continue;
                }

                if (line.StartsWith("//"))
                    continue;

                string[] tokens = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);

                // a full keyframe is 51 numbers; anything shorter is a stray or malformed line
                if (tokens.Length < 30 || currentWeather == null)
                    continue;

                if (keyframesRead == 0)
                    weatherNames.Add(currentWeather);

                ambient.Add(ReadColor(tokens, AmbientOffset));
                directional.Add(ReadColor(tokens, DirectionalOffset));
                skyTop.Add(ReadColor(tokens, SkyTopOffset));
                skyBottom.Add(ReadColor(tokens, SkyBottomOffset));
                sunCore.Add(ReadColor(tokens, SunCoreOffset));

                farClip.Add(ReadFloat(tokens, FarClipOffset, 800f));
                fogStart.Add(ReadFloat(tokens, FogStartOffset, 100f));

                keyframesRead++;
            }

            int expected = weatherNames.Count * SanAndreasUnity.Export.GtaTimeCycleData.KeyframeCount;

            if (ambient.Count != expected)
            {
                Debug.LogWarning($"timecyc: expected {expected} keyframes for {weatherNames.Count} " +
                    $"weather types but read {ambient.Count} - the file layout may differ from the usual one");
            }

            var asset = ScriptableObject.CreateInstance<SanAndreasUnity.Export.GtaTimeCycleData>();
            asset.keyframeHours = KeyframeHours;
            asset.weatherNames = weatherNames.ToArray();
            asset.ambient = ambient.ToArray();
            asset.directional = directional.ToArray();
            asset.skyTop = skyTop.ToArray();
            asset.skyBottom = skyBottom.ToArray();
            asset.sunCore = sunCore.ToArray();
            asset.fogStart = fogStart.ToArray();
            asset.farClip = farClip.ToArray();

            if (AssetDatabase.LoadAssetAtPath<SanAndreasUnity.Export.GtaTimeCycleData>(OutputPath) != null)
                AssetDatabase.DeleteAsset(OutputPath);

            AssetDatabase.CreateAsset(asset, OutputPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"Time cycle exported: {weatherNames.Count} weather types, {ambient.Count} keyframes " +
                $"-> {OutputPath}");

            if (weatherNames.Count > 0)
            {
                Debug.Log($"  first weather '{weatherNames[0]}': midnight ambient {ambient[0]}, " +
                    $"midday ambient {ambient[4]}, midday sky {skyTop[4]}");
            }
        }

        /// <summary> Three consecutive 0-255 components as a colour. </summary>
        private static Color ReadColor(string[] tokens, int offset)
        {
            return new Color(
                ReadFloat(tokens, offset + 0, 0f) / 255f,
                ReadFloat(tokens, offset + 1, 0f) / 255f,
                ReadFloat(tokens, offset + 2, 0f) / 255f);
        }

        private static float ReadFloat(string[] tokens, int index, float fallback)
        {
            if (index < 0 || index >= tokens.Length)
                return fallback;

            return float.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? v
                : fallback;
        }
    }
}
