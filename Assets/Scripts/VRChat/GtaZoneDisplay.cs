using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Names the district the player is standing in, the way the game does when you cross into a new one.
    ///
    /// Zones nest - a neighbourhood inside a city inside the state - so a position is typically inside
    /// several at once and the smallest containing zone is the one worth naming. That is the same rule the
    /// game uses; without it every street in Los Santos would just read "Los Santos".
    ///
    /// Purely local: it describes where the local player is, so there is nothing to sync.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaZoneDisplay : UdonSharpBehaviour
    {
        [Header("Zone data")]
        public string[] zoneNames;
        public Vector3[] zoneMins;
        public Vector3[] zoneMaxs;
        public float[] zoneVolumes;

        [Header("Display")]
        public Text zoneText;

        [Tooltip("Shown when the player is outside every named zone.")]
        public string defaultZoneName = "San Andreas";

        [Tooltip("Seconds between checks. Crossing a district is not a per-frame event.")]
        public float checkInterval = 0.5f;

        private string _currentZone = "";

        void Start()
        {
            SendCustomEventDelayedSeconds(nameof(CheckZone), checkInterval);
        }

        /// <summary> Re-checks the district and re-schedules itself. </summary>
        public void CheckZone()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;

            if (localPlayer != null && localPlayer.IsValid())
            {
                string zone = FindZoneName(localPlayer.GetPosition());

                if (zone != _currentZone)
                {
                    _currentZone = zone;

                    if (zoneText != null)
                        zoneText.text = zone;
                }
            }

            SendCustomEventDelayedSeconds(nameof(CheckZone), checkInterval);
        }

        /// <summary> The smallest named zone containing the position. </summary>
        public string FindZoneName(Vector3 position)
        {
            if (zoneNames == null || zoneMins == null || zoneMaxs == null)
                return defaultZoneName;

            float smallest = float.MaxValue;
            int best = -1;

            for (int i = 0; i < zoneNames.Length; i++)
            {
                Vector3 min = zoneMins[i];
                Vector3 max = zoneMaxs[i];

                if (position.x < min.x || position.x > max.x)
                    continue;
                if (position.y < min.y || position.y > max.y)
                    continue;
                if (position.z < min.z || position.z > max.z)
                    continue;

                float volume = zoneVolumes != null && i < zoneVolumes.Length ? zoneVolumes[i] : 0f;

                if (best < 0 || volume < smallest)
                {
                    smallest = volume;
                    best = i;
                }
            }

            return best >= 0 ? zoneNames[best] : defaultZoneName;
        }

        /// <summary> Current district name, for other behaviours to read. </summary>
        public string CurrentZone => _currentZone;
    }
}
