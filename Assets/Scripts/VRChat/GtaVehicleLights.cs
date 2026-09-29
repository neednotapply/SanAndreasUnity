using UdonSharp;
using UnityEngine;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Switches a vehicle's headlights and tail lights on after dark.
    ///
    /// The lamps are emissive quads rather than real lights. A world with dozens of active vehicles would
    /// need a hundred realtime lights to do this properly, which VRChat cannot afford - and for headlights
    /// seen from outside the car, a glowing lamp reads exactly the same as a lit one at a fraction of the
    /// cost.
    ///
    /// Positions come from the model's own headlights/taillights dummy frames, so each vehicle's lamps sit
    /// where its designer put them.
    ///
    /// There is deliberately no Update() here. Every vehicle in the world carries one of these, and Udon
    /// pays a real per-frame cost for each behaviour that ticks - fifty-odd of them waking every frame
    /// merely to count to five seconds is waste. Instead each one re-checks on a delayed event, which costs
    /// nothing between checks, and they are staggered so they do not all wake on the same frame.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaVehicleLights : UdonSharpBehaviour
    {
        [Header("References")]
        public GtaDayNightCycle dayNight;

        [Tooltip("Lamp objects, hidden during the day.")]
        public GameObject[] lamps;

        [Header("Timing")]
        [Tooltip("Hour after which lights come on.")]
        public float duskHour = 19f;

        [Tooltip("Hour at which they go off again.")]
        public float dawnHour = 6.5f;

        [Tooltip("Seconds between checks. Dusk does not need to be noticed instantly.")]
        public float checkInterval = 5f;

        private bool _lit = false;

        void Start()
        {
            Apply(ShouldBeLit());

            // stagger the first check, so every vehicle in the world does not re-evaluate on one frame
            SendCustomEventDelayedSeconds(nameof(CheckLights), Random.Range(0f, checkInterval));
        }

        /// <summary> Re-checks the time and re-schedules itself. </summary>
        public void CheckLights()
        {
            bool wanted = ShouldBeLit();
            if (wanted != _lit)
                Apply(wanted);

            SendCustomEventDelayedSeconds(nameof(CheckLights), checkInterval);
        }

        private bool ShouldBeLit()
        {
            if (dayNight == null)
                return false;

            float hour = dayNight.CurrentHour;

            // the lit window wraps past midnight, so it is two ranges rather than one
            return hour >= duskHour || hour < dawnHour;
        }

        private void Apply(bool lit)
        {
            _lit = lit;

            if (lamps == null)
                return;

            for (int i = 0; i < lamps.Length; i++)
            {
                if (lamps[i] != null)
                    lamps[i].SetActive(lit);
            }
        }
    }
}
