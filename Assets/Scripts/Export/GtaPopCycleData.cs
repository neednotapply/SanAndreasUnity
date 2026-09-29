using UnityEngine;

namespace SanAndreasUnity.Export
{
    /// <summary>
    /// GTA's population model, from popcycle.dat.
    ///
    /// For every zone category and every two-hour slot, this gives how many pedestrians and vehicles the
    /// area supports and what mix of people they should be. It is why Ganton has gang members and Commerce
    /// has office workers - the game does not scatter a uniform crowd, it populates each district according
    /// to what that district is.
    ///
    /// Indexed by zone category, two-hour slot, weekday/weekend and ped group - see Index(). Flat arrays,
    /// because Udon cannot see custom types or multidimensional ones.
    /// </summary>
    public class GtaPopCycleData : ScriptableObject
    {
        public const int ZoneTypeCount = 20;
        public const int SlotCount = 12;
        public const int GroupCount = 18;

        /// <summary> Weekday (0) and weekend (1). The file carries both and they differ meaningfully. </summary>
        public const int DayKindCount = 2;

        /// <summary> Ped group names in column order, matching pedgrp.dat's groups. </summary>
        public string[] groupNames;

        /// <summary> Percentage of the crowd drawn from each group. Indexed as described above. </summary>
        public int[] groupPercent;

        /// <summary> Maximum pedestrians for [zoneType * SlotCount + slot]. </summary>
        public int[] maxPeds;

        /// <summary> Maximum vehicles for [zoneType * SlotCount + slot]. </summary>
        public int[] maxCars;

        /// <summary> Index into groupPercent. </summary>
        public static int Index(int zoneType, int slot, int dayKind, int group)
        {
            return ((zoneType * SlotCount + slot) * DayKindCount + dayKind) * GroupCount + group;
        }

        /// <summary> Index into maxPeds and maxCars. </summary>
        public static int DensityIndex(int zoneType, int slot, int dayKind)
        {
            return (zoneType * SlotCount + slot) * DayKindCount + dayKind;
        }
    }
}
