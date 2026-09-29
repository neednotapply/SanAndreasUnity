using UdonSharp;
using UnityEngine;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Decides which parking spaces in this cell currently hold a car.
    ///
    /// The game does not bake this. Parked cars appear when an area streams in, filled against how busy the
    /// district is at that moment, so the same street is not identically parked every time you see it - and
    /// a business district that is lined with cars at midday is half empty at three in the morning.
    ///
    /// Baking the choice at build time loses both of those: the same spaces would sit empty forever. So the
    /// decision is made here, each time the cell loads, from the district's vehicle budget for the current
    /// hour. Udon cannot create objects, so every space is pre-placed and this only chooses which are shown.
    ///
    /// Spaces the game guarantees are not passed to this behaviour at all - they are simply always on.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaParkedCarSpaces : UdonSharpBehaviour
    {
        [Header("Spaces")]
        [Tooltip("Candidate cars in this cell. Guaranteed ones are not listed here.")]
        public GameObject[] candidates;

        [Header("District")]
        [Tooltip("Population category of this cell, or -1 if unknown.")]
        public int zoneType = -1;

        [Tooltip("Vehicle budget per district and hour, indexed [(type * 12 + slot) * 2 + dayKind].")]
        public int[] maxCars;

        [Tooltip("Busiest district's budget, used to scale the rest.")]
        public int busiestBudget = 1;

        public GtaDayNightCycle dayNight;

        [Header("Fill")]
        [Tooltip("Lowest proportion of spaces ever filled, so quiet districts still have some cars.")]
        [Range(0f, 1f)] public float minimumFill = 0.15f;

        void OnEnable()
        {
            Refill();
        }

        /// <summary>
        /// Chooses which spaces hold a car right now.
        /// </summary>
        public void Refill()
        {
            if (candidates == null || candidates.Length == 0)
                return;

            float fill = ComputeFill();

            // A seed that moves with the hour, so a street re-parks over the course of a day rather than
            // flickering every time the cell reloads. Mixing in the object's position keeps neighbouring
            // cells from filling and emptying in lockstep.
            int hourSeed = dayNight != null ? (int)dayNight.CurrentHour : 12;
            int placeSeed = Mathf.RoundToInt(transform.position.x + transform.position.z);

            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i] == null)
                    continue;

                int roll = Mathf.Abs((i * 73 + hourSeed * 31 + placeSeed) % 100);

                candidates[i].SetActive(roll < fill * 100f);
            }
        }

        /// <summary> Proportion of spaces to fill, from the district's budget at this hour. </summary>
        private float ComputeFill()
        {
            if (maxCars == null || zoneType < 0 || busiestBudget <= 0)
                return 1f;

            int slot = 6;
            int dayKind = 0;

            if (dayNight != null)
            {
                slot = Mathf.Clamp((int)(dayNight.CurrentHour * 0.5f), 0, 11);
                dayKind = dayNight.DayKind;
            }

            int index = (zoneType * 12 + slot) * 2 + dayKind;

            if (index < 0 || index >= maxCars.Length)
                return 1f;

            float fill = maxCars[index] / (float)busiestBudget;

            return Mathf.Clamp(fill, minimumFill, 1f);
        }
    }
}
