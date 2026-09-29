using UnityEngine;

namespace SanAndreasUnity.Export
{
    /// <summary>
    /// Per-weapon statistics from GTA's weapon.dat.
    ///
    /// What separates a pistol from an AK is entirely in this data - damage, range, clip size and fire rate -
    /// so a single weapon behaviour reads its numbers from here rather than being reimplemented per gun.
    ///
    /// Flat parallel arrays, keyed by model name, because Udon cannot see custom types.
    /// </summary>
    public class GtaWeaponData : ScriptableObject
    {
        public string[] modelNames;

        /// <summary> Weapon class from the data file, e.g. "PISTOL", "MELEE". </summary>
        public string[] weaponTypes;

        /// <summary> Damage per hit. </summary>
        public int[] damage;

        /// <summary> Effective range in metres. </summary>
        public float[] range;

        /// <summary> Rounds per clip; 0 for melee. </summary>
        public int[] clipSize;

        /// <summary> Accuracy multiplier from the data file (roughly 0.5 - 2.0). </summary>
        public float[] accuracy;

        /// <summary> True for anything that shoots. </summary>
        public bool[] isGun;

        public int Count => modelNames != null ? modelNames.Length : 0;

        /// <summary> Index for a model name, or -1. </summary>
        public int IndexOf(string modelName)
        {
            if (modelNames == null || string.IsNullOrEmpty(modelName))
                return -1;

            for (int i = 0; i < modelNames.Length; i++)
            {
                if (string.Equals(modelNames[i], modelName, System.StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }
    }
}
