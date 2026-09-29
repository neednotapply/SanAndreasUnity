using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Runs the city-wide traffic light cycle.
    ///
    /// GTA alternates two opposing phases at every junction rather than timing each light individually,
    /// so a single global cycle reproduces the behaviour and costs almost nothing. Vehicles ask which
    /// phase is currently green and stop if the node they are approaching belongs to the other one.
    ///
    /// The phase is derived from network time, so every client agrees without any syncing.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaTrafficLightController : UdonSharpBehaviour
    {
        [Header("Durations (seconds) - defaults match the original game")]
        public float greenDuration = 7f;
        public float yellowDuration = 2f;

        /// <summary> Phase 0 or 1 currently allowed to move. </summary>
        public int CurrentGreenDirection => _currentGreen;

        /// <summary> True while the green phase is ending, so vehicles can begin slowing. </summary>
        public bool IsYellow => _isYellow;

        private int _currentGreen = 0;
        private bool _isYellow = false;

        void Update()
        {
            float cycle = (greenDuration + yellowDuration) * 2f;
            if (cycle <= 0.01f)
                return;

            // network time keeps every client on the same phase without syncing anything
            // Server time can come back negative before the network is up, which would put the cycle in a
            // phase that does not exist. Same guard as the day/night clock uses.
            double serverTime = Networking.GetServerTimeInSeconds();
            if (!(serverTime > 0.0) || serverTime > 1e12)
                serverTime = 0.0;

            float t = (float)(serverTime % cycle);

            float half = greenDuration + yellowDuration;

            if (t < half)
            {
                _currentGreen = 0;
                _isYellow = t >= greenDuration;
            }
            else
            {
                _currentGreen = 1;
                _isYellow = (t - half) >= greenDuration;
            }
        }

        /// <summary>
        /// Whether a vehicle may proceed through the given node. Nodes with no light always pass.
        /// </summary>
        public bool CanProceed(int trafficLightDirection)
        {
            if (trafficLightDirection < 0)
                return true;

            if (trafficLightDirection != _currentGreen)
                return false;

            // yellow still permits movement - vehicles already in the junction should clear it
            return true;
        }
    }
}
