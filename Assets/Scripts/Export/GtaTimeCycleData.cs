using UnityEngine;

namespace SanAndreasUnity.Export
{
    /// <summary>
    /// San Andreas' own sky and lighting, from timecyc.dat.
    ///
    /// The game does not compute its lighting - it is authored. For every weather type there are eight
    /// keyframes through the day, each giving the ambient colour, the sun's colour, the sky gradient and the
    /// fog. That authored quality is why a San Andreas dusk looks like San Andreas rather than like a
    /// generic sunset, and no amount of arithmetic reproduces it.
    ///
    /// Values are stored per weather, per keyframe, indexed as [weather * KeyframeCount + keyframe].
    /// Flat arrays, because Udon cannot see custom types.
    /// </summary>
    public class GtaTimeCycleData : ScriptableObject
    {
        public const int KeyframeCount = 8;

        /// <summary> Hour of day each keyframe applies to. </summary>
        public int[] keyframeHours;

        public string[] weatherNames;

        public Color[] ambient;
        /// <summary> The "Dir" column. Pure white at every hour, so of little use as a light colour. </summary>
        public Color[] directional;

        /// <summary>
        /// The "SunCore" column - the colour of the sun disc, and the one that actually varies: orange
        /// through the night and dawn, white by mid-morning. This is what gives dawn and dusk their warmth.
        /// </summary>
        public Color[] sunCore;
        public Color[] skyTop;
        public Color[] skyBottom;

        public float[] fogStart;
        public float[] farClip;

        public int WeatherCount => weatherNames != null ? weatherNames.Length : 0;
    }
}
