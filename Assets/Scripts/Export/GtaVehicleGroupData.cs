using UnityEngine;

namespace SanAndreasUnity.Export
{
    /// <summary>
    /// GTA's vehicle groups, from cargrp.dat.
    ///
    /// Each group lists the vehicles that belong with a kind of population - work trucks and buses for
    /// WORKERS, executive saloons for BUSINESS, beach runabouts and bikes for BEACHFOLK. The groups are
    /// named after the same POPCYCLE_GROUP_* keys as the pedestrian groups, so a district that draws its
    /// people from one group draws its traffic and parked cars from the matching one.
    ///
    /// That shared key is what lets a parked car be chosen the way the game chooses it, instead of from a
    /// hand-filtered list of "ordinary looking" vehicles.
    ///
    /// Members are stored flat with a start index and count per group, since Unity does not serialise
    /// jagged arrays.
    /// </summary>
    public class GtaVehicleGroupData : ScriptableObject
    {
        public string[] groupNames;
        public int[] groupStarts;
        public int[] groupCounts;
        public string[] memberModels;

        public int GroupCount => groupNames != null ? groupNames.Length : 0;
    }
}
