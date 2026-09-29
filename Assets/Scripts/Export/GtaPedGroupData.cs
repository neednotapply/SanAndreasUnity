using UnityEngine;

namespace SanAndreasUnity.Export
{
    /// <summary>
    /// GTA's pedestrian groups, from pedgrp.dat.
    ///
    /// Each group is a set of ped models the game spawns together - construction workers and mechanics in
    /// one, business people in another, clubbers in a third - with separate variants for Los Santos, San
    /// Fierro and Las Venturas. Drawing a crowd from one group produces a street that looks like it belongs
    /// somewhere, rather than a uniform shuffle of every model in the game including mission characters.
    ///
    /// Members are stored flat with a start index and count per group, since Unity does not serialise
    /// jagged arrays.
    /// </summary>
    public class GtaPedGroupData : ScriptableObject
    {
        public string[] groupNames;

        /// <summary>
        /// Which city each group belongs to: 0 = Los Santos (the unsuffixed default), 1 = San Fierro,
        /// 2 = Las Venturas.
        ///
        /// pedgrp.dat repeats each group with "(SF)" and "(VEGAS)" suffixes, which is how the three cities
        /// end up with visibly different crowds - San Fierro's business people are not Los Santos'.
        /// </summary>
        public int[] groupCities;

        /// <summary> Index into <see cref="memberModels"/> where each group starts. </summary>
        public int[] groupStarts;

        public int[] groupCounts;

        /// <summary> All group members, concatenated. </summary>
        public string[] memberModels;

        public int GroupCount => groupNames != null ? groupNames.Length : 0;
    }
}
