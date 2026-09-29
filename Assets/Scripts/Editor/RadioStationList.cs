using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Reads the station list that the radio is built from.
    ///
    /// The list lives in a plain text file rather than in code because the URLs are the one thing that has
    /// to be supplied by hand - Udon cannot construct a VRCUrl at runtime, so every station has to be baked
    /// into the scene at build time, and a text file makes that a paste rather than a code change.
    ///
    /// Format:
    ///     Station Name | https://...
    ///         12:34 | Track title          (optional, indented, for the now-playing readout)
    /// </summary>
    public static class RadioStationList
    {
        public const string Path = "Assets/ExportedAssets/RadioStations.txt";

        public class Station
        {
            public string Name;
            public string Url;

            /// <summary> Track start times in seconds, with titles, in file order. </summary>
            public List<float> TrackStarts = new List<float>();
            public List<string> TrackTitles = new List<string>();
        }

        public static List<Station> Read()
        {
            var stations = new List<Station>();

            if (!File.Exists(Path))
            {
                Debug.LogWarning($"No radio station list at {Path} - the world will have no radio");
                return stations;
            }

            Station current = null;

            foreach (string rawLine in File.ReadAllLines(Path))
            {
                string line = rawLine.TrimEnd();

                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#"))
                    continue;

                bool indented = line.StartsWith(" ") || line.StartsWith("\t");
                string body = line.Trim();

                int bar = body.IndexOf('|');
                if (bar < 0)
                    continue;

                string left = body.Substring(0, bar).Trim();
                string right = body.Substring(bar + 1).Trim();

                // an indented line under a station is one of its tracks
                if (indented && current != null)
                {
                    float seconds = ParseTimecode(left);
                    if (seconds >= 0f && right.Length > 0)
                    {
                        current.TrackStarts.Add(seconds);
                        current.TrackTitles.Add(right);
                    }

                    continue;
                }

                // a station with no URL yet is a placeholder, not a station
                if (left.Length == 0 || right.Length == 0)
                    continue;

                current = new Station { Name = left, Url = right };
                stations.Add(current);
            }

            int tracks = 0;
            foreach (var station in stations)
                tracks += station.TrackStarts.Count;

            Debug.Log($"Radio stations read: {stations.Count} stations, {tracks} track timecodes");

            return stations;
        }

        /// <summary> "mm:ss" or "hh:mm:ss" to seconds, or -1 if it is not a timecode. </summary>
        private static float ParseTimecode(string text)
        {
            string[] parts = text.Split(':');

            if (parts.Length < 2 || parts.Length > 3)
                return -1f;

            float total = 0f;

            foreach (string part in parts)
            {
                if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    return -1f;

                total = total * 60f + v;
            }

            return total;
        }
    }
}
