using UnityEngine;

namespace SanAndreasUnity.Export
{
    /// <summary>
    /// Named destinations the player can teleport to, taken from GTA's enex (entrance/exit) markers.
    ///
    /// Enexes are the game's own interior transitions - shop doors, safehouses, clubs - so they double as a
    /// ready-made list of the places worth going, already named and already positioned on solid ground.
    ///
    /// Flat parallel arrays, because Udon cannot see custom types or generic collections.
    /// </summary>
    public class GtaTeleportData : ScriptableObject
    {
        public string[] names;

        /// <summary> Where the player ends up. </summary>
        public Vector3[] positions;

        /// <summary> Heading the player faces on arrival, degrees. </summary>
        public float[] headings;

        /// <summary> Interior level, 0 for the main exterior world. </summary>
        public int[] interiors;

        public int Count => names != null ? names.Length : 0;
    }
}
