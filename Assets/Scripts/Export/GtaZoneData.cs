using UnityEngine;

namespace SanAndreasUnity.Export
{
    /// <summary>
    /// San Andreas' named districts - Ganton, Idlewood, Las Venturas and the rest.
    ///
    /// Zones nest: a neighbourhood sits inside a city which sits inside the state, so a point is usually
    /// inside several at once and the smallest one is the answer. Volumes are exported alongside the bounds
    /// so that comparison can be made without recomputing it per lookup.
    ///
    /// Flat parallel arrays, because Udon cannot see custom types.
    /// </summary>
    public class GtaZoneData : ScriptableObject
    {
        public string[] names;
        public Vector3[] mins;
        public Vector3[] maxs;

        /// <summary> Precomputed volume, used to pick the smallest containing zone. </summary>
        public float[] volumes;

        /// <summary>
        /// Population category per zone, from eZonePopulationType: 0 BUSINESS, 7 GANGLAND, 8 BEACH and so
        /// on, or -1 where it could not be recovered. This is what indexes popcycle.dat.
        /// </summary>
        public int[] zoneTypes;

        /// <summary> Internal zone code from info.zon, e.g. "SUNMA". </summary>
        public string[] zoneCodes;

        public int Count => names != null ? names.Length : 0;
    }
}
