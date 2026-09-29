using UnityEngine;

namespace SanAndreasUnity.Export
{
    /// <summary>
    /// San Andreas' three city regions, from map.zon.
    ///
    /// map.zon is the file the game loads before any IPL, and it divides the state into Los Santos, San
    /// Fierro and Las Venturas. Anything outside all three is countryside or desert.
    ///
    /// This is what makes a city-aware population possible: the popcycle zone categories (BUSINESS,
    /// GANGLAND and the rest) are not present in any data file, but which city you are standing in is.
    /// </summary>
    public class GtaCityRegionData : ScriptableObject
    {
        public string[] names;
        public Vector3[] mins;
        public Vector3[] maxs;

        /// <summary> 1 = Los Santos, 2 = San Fierro, 3 = Las Venturas. </summary>
        public int[] islands;

        public int Count => names != null ? names.Length : 0;
    }
}
