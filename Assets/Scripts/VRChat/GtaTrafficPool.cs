using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Keeps traffic on the roads around players, using the same fixed-pool approach as the NPC pool.
    ///
    /// Vehicles are placed on VEHICLE path nodes rather than sampled onto the nav mesh - the nav mesh is
    /// for pedestrians and would happily drop a car onto a pavement or into a plaza. Road nodes are what
    /// keep traffic on streets.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class GtaTrafficPool : UdonSharpBehaviour
    {
        [Header("References")]
        public GtaPathNetworkData pathNetwork;

        [Tooltip("Pre-placed vehicles. Each should carry a GtaTrafficVehicle.")]
        public GameObject[] pooledVehicles;

        [Header("District traffic (popcycle + cargrp)")]
        [Tooltip("Which popcycle group each pooled vehicle's model belongs to, or -1 if unknown.")]
        public int[] vehicleGroups;

        [Tooltip("District bounds, smallest containing one wins.")]
        public Vector3[] zoneMins;
        public Vector3[] zoneMaxs;
        public float[] zoneVolumes;

        [Tooltip("Population category per zone, or -1. Indexes popcycle.")]
        public int[] zoneTypes;

        [Tooltip("popcycle percentages, indexed [((type * 12 + slot) * 2 + dayKind) * 18 + group].")]
        public int[] groupPercent;

        [Tooltip("Supplies the hour and the kind of day. Optional; midday on a weekday without it.")]
        public GtaDayNightCycle dayNight;

        [Header("Placement")]
        [Tooltip("Vehicles are not spawned closer than this, so they don't pop in on top of a player.")]
        public float minSpawnDistance = 40f;

        [Tooltip("Vehicles are spawned within this distance of a player.")]
        public float maxSpawnDistance = 140f;

        [Tooltip("Beyond this distance from every player a vehicle is recycled.")]
        public float despawnDistance = 220f;

        [Tooltip("Seconds between placement passes.")]
        public float updateInterval = 2f;

        private float _timeSinceUpdate = 0f;

        void Start()
        {
            if (pooledVehicles == null)
                return;

            for (int i = 0; i < pooledVehicles.Length; i++)
            {
                if (pooledVehicles[i] != null)
                    pooledVehicles[i].SetActive(false);
            }
        }

        void Update()
        {
            // one owner decides placement so every client sees traffic in the same places
            if (!Networking.IsOwner(gameObject))
                return;

            if (pooledVehicles == null || pathNetwork == null)
                return;

            _timeSinceUpdate += Time.deltaTime;
            if (_timeSinceUpdate < updateInterval)
                return;

            _timeSinceUpdate = 0f;

            UpdatePlacement();
        }

        private void UpdatePlacement()
        {
            int playerCount = VRCPlayerApi.GetPlayerCount();
            if (playerCount <= 0)
                return;

            VRCPlayerApi[] players = new VRCPlayerApi[playerCount];
            VRCPlayerApi.GetPlayers(players);

            for (int i = 0; i < pooledVehicles.Length; i++)
            {
                GameObject vehicle = pooledVehicles[i];
                if (vehicle == null)
                    continue;

                if (!vehicle.activeSelf)
                {
                    TryPlaceVehicle(vehicle, i, players);
                    continue;
                }

                if (NearestPlayerDistance(vehicle.transform.position, players) > despawnDistance)
                    vehicle.SetActive(false);
            }
        }

        private void TryPlaceVehicle(GameObject vehicle, int index, VRCPlayerApi[] players)
        {
            VRCPlayerApi anchor = players[Random.Range(0, players.Length)];
            if (anchor == null || !anchor.IsValid())
                return;

            Vector3 anchorPosition = anchor.GetPosition();

            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(minSpawnDistance, maxSpawnDistance);

            Vector3 candidate = anchorPosition + new Vector3(
                Mathf.Cos(angle) * distance,
                0f,
                Mathf.Sin(angle) * distance);

            // false = vehicle nodes, so traffic starts on a road rather than wherever is walkable
            int node = pathNetwork.FindNearestNode(candidate, false, 1);
            if (node < 0)
                return;

            if (pathNetwork.nodeIsWater[node])
                return;

            Vector3 nodePosition = pathNetwork.GetNodePosition(node);

            if (NearestPlayerDistance(nodePosition, players) < minSpawnDistance)
                return;

            // Does this vehicle belong on this road?
            //
            // Traffic used to be drawn round-robin from every model in the game, so a sports car was as
            // likely to appear on a dirt track in the desert as on a Los Santos boulevard. popcycle says
            // what mix of population a district carries and cargrp says which vehicles go with each part
            // of that mix, which together are the game's own answer to the question.
            if (!BelongsHere(index, nodePosition))
                return;

            // aim along a link so the car starts pointing down the road instead of across it
            Vector3 forward = Vector3.forward;
            int linkCount = pathNetwork.GetLinkCount(node);
            for (int i = 0; i < linkCount; i++)
            {
                int next = pathNetwork.GetLinkedNode(node, i);
                if (next < 0)
                    continue;

                Vector3 delta = pathNetwork.GetNodePosition(next) - nodePosition;
                delta.y = 0f;
                if (delta.sqrMagnitude > 0.01f)
                {
                    forward = delta.normalized;
                    break;
                }
            }

            // lift slightly so the body isn't sunk into the road surface
            vehicle.transform.position = nodePosition + Vector3.up * 0.6f;
            vehicle.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            vehicle.SetActive(true);
        }

        /// <summary> Whether this vehicle's group belongs in the district at the current hour. </summary>
        private bool BelongsHere(int index, Vector3 position)
        {
            if (vehicleGroups == null || groupPercent == null || index >= vehicleGroups.Length)
                return true;

            int group = vehicleGroups[index];
            if (group < 0)
                return true;

            int zoneType = FindZoneType(position);
            if (zoneType < 0)
                return true;   // outside every named district - no rule to apply

            // midday on a weekday, used when there is no clock to ask
            int slot = 6;
            int dayKind = 0;

            if (dayNight != null)
            {
                slot = Mathf.Clamp((int)(dayNight.CurrentHour * 0.5f), 0, 11);
                dayKind = dayNight.DayKind;
            }

            int i = ((zoneType * 12 + slot) * 2 + dayKind) * 18 + group;
            if (i < 0 || i >= groupPercent.Length)
                return true;

            return groupPercent[i] > 0;
        }

        /// <summary> Population category of the smallest district containing a position, or -1. </summary>
        public int FindZoneType(Vector3 position)
        {
            if (zoneMins == null || zoneMaxs == null || zoneTypes == null)
                return -1;

            float smallest = 0f;
            int best = -1;

            for (int i = 0; i < zoneMins.Length; i++)
            {
                Vector3 min = zoneMins[i];
                Vector3 max = zoneMaxs[i];

                if (position.x < min.x || position.x > max.x)
                    continue;
                if (position.y < min.y || position.y > max.y)
                    continue;
                if (position.z < min.z || position.z > max.z)
                    continue;

                if (i >= zoneTypes.Length || zoneTypes[i] < 0)
                    continue;

                // districts nest, so the smallest one containing the point is the meaningful one
                float volume = zoneVolumes != null && i < zoneVolumes.Length ? zoneVolumes[i] : 0f;

                if (best < 0 || volume < smallest)
                {
                    smallest = volume;
                    best = i;
                }
            }

            return best >= 0 ? zoneTypes[best] : -1;
        }

        private float NearestPlayerDistance(Vector3 position, VRCPlayerApi[] players)
        {
            float nearest = -1f;

            for (int i = 0; i < players.Length; i++)
            {
                VRCPlayerApi player = players[i];
                if (player == null || !player.IsValid())
                    continue;

                float distance = Vector3.Distance(position, player.GetPosition());

                if (nearest < 0f || distance < nearest)
                    nearest = distance;
            }

            if (nearest < 0f)
                return float.MaxValue;

            return nearest;
        }
    }
}
