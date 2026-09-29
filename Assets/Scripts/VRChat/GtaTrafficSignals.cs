using UdonSharp;
using UnityEngine;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Shows the right lens on the traffic signals in one streaming cell.
    ///
    /// GTA traffic lights are a single mesh wearing six glow sprites - red, amber and green for each of the
    /// two faces - and the game lights only the one that currently applies. Exporting the model brings all
    /// six across switched on, which is why every signal appeared to show red and green at once.
    ///
    /// The phase is not computed here. <see cref="GtaTrafficLightController"/> already runs the city-wide
    /// cycle that the traffic AI obeys, so this reads from it rather than keeping its own clock: a second
    /// timer would drift out of step and show green to a player while cars sat waiting at a red the
    /// simulation still believed in.
    ///
    /// Signals are split into two groups by which way they face, matching the controller's two directions,
    /// so signals across a junction from one another change together while the cross street holds red.
    ///
    /// One of these per cell rather than one per signal: San Andreas has thousands of them, and they all
    /// answer to the same cycle.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaTrafficSignals : UdonSharpBehaviour
    {
        [Header("Cycle")]
        [Tooltip("The city-wide phase the traffic AI also obeys.")]
        public GtaTrafficLightController cycle;

        [Header("Direction 0 - signals facing roughly north/south")]
        public GameObject[] aRed;
        public GameObject[] aAmber;
        public GameObject[] aGreen;

        [Header("Direction 1 - signals facing roughly east/west")]
        public GameObject[] bRed;
        public GameObject[] bAmber;
        public GameObject[] bGreen;

        // what is currently shown, so the arrays are only written when the phase actually changes
        private int _shown = -1;

        void OnEnable()
        {
            // force a write on the first tick after the cell loads
            _shown = -1;
            Apply();
        }

        void Update()
        {
            Apply();
        }

        private void Apply()
        {
            if (cycle == null)
                return;

            int green = cycle.CurrentGreenDirection;
            bool amber = cycle.IsYellow;

            // one number covering both which direction has priority and whether it is about to lose it
            int state = green * 2 + (amber ? 1 : 0);

            if (state == _shown)
                return;

            _shown = state;

            bool aHasPriority = green == 0;

            Show(aGreen, aHasPriority && !amber);
            Show(aAmber, aHasPriority && amber);
            Show(aRed, !aHasPriority);

            Show(bGreen, !aHasPriority && !amber);
            Show(bAmber, !aHasPriority && amber);
            Show(bRed, aHasPriority);
        }

        private void Show(GameObject[] lamps, bool on)
        {
            if (lamps == null)
                return;

            for (int i = 0; i < lamps.Length; i++)
            {
                if (lamps[i] != null && lamps[i].activeSelf != on)
                    lamps[i].SetActive(on);
            }
        }
    }
}
