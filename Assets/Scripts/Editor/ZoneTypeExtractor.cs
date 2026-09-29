using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UGameCore.Utilities;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Recovers each zone's population category - BUSINESS, GANGLAND, BEACH and the rest.
    ///
    /// This is the piece that makes popcycle.dat usable, and it is in none of the data files. info.zon
    /// reports every zone as type 0, and CTheZones::InitZonesPopulationSettings zeroes the whole array at
    /// startup. The categories are assigned at runtime by the mission script, through opcode 0767
    /// (set_zone), so the table lives inside main.scm as compiled bytecode.
    ///
    /// Scanning for that opcode recovers it. Each call is the opcode word followed by a string parameter
    /// holding the zone's internal code and an integer parameter holding the category, and the twenty
    /// categories are in the same order as popcycle.dat's twenty blocks.
    ///
    /// The zone type enumeration and opcode number are documented by the GTA modding community's opcode
    /// references and by the gta-reversed reimplementation; the values themselves are read out of the
    /// player's own game files here rather than transcribed.
    /// </summary>
    public static class ZoneTypeExtractor
    {
        /// <summary> eZonePopulationType, in the same order as popcycle.dat's blocks. </summary>
        public static readonly string[] TypeNames =
        {
            "BUSINESS", "DESERT", "ENTERTAINMENT", "COUNTRYSIDE", "RESIDENTIAL_RICH",
            "RESIDENTIAL_AVERAGE", "RESIDENTIAL_POOR", "GANGLAND", "BEACH", "SHOPPING",
            "PARK", "INDUSTRY", "ENTERTAINMENT_BUSY", "SHOPPING_BUSY", "SHOPPING_POSH",
            "RESIDENTIAL_RICH_SECLUDED", "AIRPORT", "GOLF_CLUB", "OUT_OF_TOWN_FACTORY",
            "AIRPORT_RUNWAY",
        };

        private const int MaxType = 19;

        /// <summary> SCM parameter type bytes. </summary>
        private const byte ParamInt32 = 0x01;
        private const byte ParamInt8 = 0x04;
        private const byte ParamInt16 = 0x05;
        private const byte ParamString8 = 0x09;
        private const byte ParamStringVar = 0x0E;

        /// <summary>
        /// Zone code (upper case) to population type, read out of main.scm.
        /// </summary>
        public static Dictionary<string, int> ExtractZoneTypes()
        {
            var result = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

            string path = ResolveGameFile("data/script/main.scm");

            if (path == null)
            {
                Debug.LogWarning("main.scm not found under the game directory - zone categories unavailable");
                return result;
            }

            byte[] data = File.ReadAllBytes(path);

            var validCode = new Regex("^[A-Za-z0-9_]{2,8}$");

            for (int i = 0; i + 12 < data.Length; i++)
            {
                // opcode 0767, little endian
                if (data[i] != 0x67 || data[i + 1] != 0x07)
                    continue;

                int p = i + 2;

                string code = ReadStringParam(data, ref p);
                if (code == null || !validCode.IsMatch(code))
                    continue;

                if (!TryReadIntParam(data, p, out int type))
                    continue;

                if (type < 0 || type > MaxType)
                    continue;

                // a zone can be set more than once; the later call is what stands
                result[code.ToUpperInvariant()] = type;
            }

            Debug.Log($"Zone categories recovered from main.scm: {result.Count} zones");

            return result;
        }

        private static string ReadStringParam(byte[] data, ref int p)
        {
            if (p >= data.Length)
                return null;

            byte kind = data[p];
            int start;
            int length;

            if (kind == ParamString8)
            {
                start = p + 1;
                length = 8;
            }
            else if (kind == ParamStringVar)
            {
                if (p + 1 >= data.Length)
                    return null;

                length = data[p + 1];
                start = p + 2;
            }
            else
            {
                return null;
            }

            if (start + length > data.Length)
                return null;

            // fixed-width names are null padded
            int end = start;
            while (end < start + length && data[end] != 0)
                end++;

            p = start + length;

            return System.Text.Encoding.ASCII.GetString(data, start, end - start);
        }

        private static bool TryReadIntParam(byte[] data, int p, out int value)
        {
            value = 0;

            if (p >= data.Length)
                return false;

            switch (data[p])
            {
                case ParamInt8:
                    if (p + 1 >= data.Length) return false;
                    value = data[p + 1];
                    return true;

                case ParamInt16:
                    if (p + 2 >= data.Length) return false;
                    value = System.BitConverter.ToInt16(data, p + 1);
                    return true;

                case ParamInt32:
                    if (p + 4 >= data.Length) return false;
                    value = System.BitConverter.ToInt32(data, p + 1);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Zone codes and bounds straight from info.zon.
        ///
        /// Needed because the category table is keyed by the internal code, while the zone list carries
        /// display names. Bounds are what link the two.
        /// </summary>
        public static List<(string code, Vector3 min, Vector3 max)> ReadZoneBounds()
        {
            var zones = new List<(string, Vector3, Vector3)>();

            string path = ResolveGameFile("data/info.zon");

            if (path == null)
            {
                Debug.LogWarning("info.zon not found under the game directory");
                return zones;
            }

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();

                if (line.Length == 0 || line == "zone" || line == "end" || line.StartsWith("#"))
                    continue;

                string[] parts = line.Split(',');
                if (parts.Length < 9)
                    continue;

                string code = parts[0].Trim();

                float x1 = Parse(parts[2]), y1 = Parse(parts[3]), z1 = Parse(parts[4]);
                float x2 = Parse(parts[5]), y2 = Parse(parts[6]), z2 = Parse(parts[7]);

                // GTA is Z-up; the exported zone list is already in Unity's axes
                var a = new Vector3(x1, z1, y1);
                var b = new Vector3(x2, z2, y2);

                zones.Add((code, Vector3.Min(a, b), Vector3.Max(a, b)));
            }

            return zones;
        }

        /// <summary>
        /// Locates a file inside the game directory.
        ///
        /// ArchiveManager's case-sensitive lookup only knows about files it has indexed, and throws for
        /// anything else - main.scm lives in data/script, which is not indexed. So the plain path is tried
        /// first and the lookup is only a fallback for case-sensitive filesystems.
        /// </summary>
        private static string ResolveGameFile(string relativePath)
        {
            string direct = Path.Combine(Config.GetPath("game_dir"), relativePath);

            if (File.Exists(direct))
                return direct;

            try
            {
                string resolved = Importing.Archive.ArchiveManager.PathToCaseSensitivePath(direct);
                if (File.Exists(resolved))
                    return resolved;
            }
            catch (System.Exception)
            {
                // not indexed; the direct path was the only chance
            }

            return null;
        }

        private static float Parse(string token)
        {
            return float.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? v
                : 0f;
        }
    }
}
