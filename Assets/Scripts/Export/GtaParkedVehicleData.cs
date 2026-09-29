using UnityEngine;

namespace SanAndreasUnity.Export
{
    /// <summary>
    /// Cars the game parks around the map, from the IPL "cars" sections.
    ///
    /// These are not traffic. They are specific models at specific kerbs, often with colours the designer
    /// chose - the beaten-up sedan outside a Ganton house, the row of cars at a Las Venturas casino. They
    /// are a large part of why the streets read as inhabited.
    ///
    /// Flat parallel arrays, because Udon cannot see custom types.
    /// </summary>
    public class GtaParkedVehicleData : ScriptableObject
    {
        public string[] modelNames;
        public Vector3[] positions;

        /// <summary> Heading in degrees. </summary>
        public float[] angles;

        /// <summary> Palette indices, or -1 to leave the model's own colours. </summary>
        public int[] primaryColors;
        public int[] secondaryColors;

        /// <summary>
        /// True where the game guarantees a car. The rest are candidate spaces that are filled against the
        /// district's vehicle budget, which is why a street is lined with cars rather than solid with them.
        /// </summary>
        public bool[] forceSpawn;

        /// <summary> True where the placement asked for a random vehicle rather than a specific model. </summary>
        public bool[] wasRandom;

        public int Count => modelNames != null ? modelNames.Length : 0;
    }
}
