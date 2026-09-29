using UdonSharp;
using UnityEngine;
using UnityEngine.AI;
using VRC.SDKBase;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Keeps a fixed pool of NPCs active around players and recycles ones that drift too far.
    ///
    /// VRChat worlds should not instantiate NPCs on demand - dynamic instantiation is expensive and
    /// awkward to keep in sync. Instead every NPC is pre-placed and this pool activates, repositions and
    /// deactivates them, so the object count is constant and known at build time.
    ///
    /// Only the owner decides placement, so all clients see the same NPCs in the same places.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class GtaNpcPool : UdonSharpBehaviour
    {
        [Header("References")]
        public GtaPathNetworkData pathNetwork;

        [Tooltip("Pre-placed NPC objects. Each should carry a GtaPedAI.")]
        public GameObject[] pooledNpcs;

        [Header("City population")]
        [Tooltip("Which city each pooled ped belongs to: 0 = Los Santos, 1 = San Fierro, 2 = Las Venturas, " +
            "-1 = anywhere. Peds are only placed in their own city.")]
        public int[] npcCities;

        [Tooltip("City region bounds from map.zon, as min/max pairs.")]
        public Vector3[] cityMins;
        public Vector3[] cityMaxs;

        [Tooltip("City index for each region, matching npcCities.")]
        public int[] cityIndices;

        [Header("District population (popcycle)")]
        [Tooltip("Which popcycle ped group each pooled ped belongs to, or -1 if unknown.")]
        public int[] npcGroups;

        [Tooltip("Zone bounds, for finding which district a position is in.")]
        public Vector3[] zoneMins;
        public Vector3[] zoneMaxs;

        [Tooltip("Population category per zone, or -1. Indexes popcycle.")]
        public int[] zoneTypes;

        [Tooltip("Zone volume, used to pick the smallest containing district.")]
        public float[] zoneVolumes;

        [Tooltip("popcycle percentages, indexed [((type * 12 + slot) * 2 + dayKind) * 18 + group].")]
        public int[] groupPercent;

        [Tooltip("Supplies the hour of day. Without it the midday population is used throughout.")]
        public GtaDayNightCycle dayNight;

        [Header("Placement")]
        [Tooltip("NPCs are spawned no closer than this to a player, so they don't pop in on top of them.")]
        public float minSpawnDistance = 12f;

        [Tooltip("NPCs are spawned within this distance of a player.")]
        public float maxSpawnDistance = 55f;

        [Tooltip("Beyond this distance from every player an NPC is recycled.")]
        public float despawnDistance = 90f;

        [Tooltip("Seconds between placement passes. Kept low-frequency - this is not per-frame work.")]
        public float updateInterval = 1.5f;

        [Tooltip("Lift applied when placing a ped, matching the agent's baseOffset. GTA ped pivots sit at " +
            "the pelvis, so dropping one straight onto the nav mesh buries it to the knees.")]
        public float groundOffset = 1.01f;

        private float _timeSinceUpdate = 0f;

        void Start()
        {
            // start with everything parked; the first pass places them
            if (pooledNpcs == null)
                return;

            for (int i = 0; i < pooledNpcs.Length; i++)
            {
                if (pooledNpcs[i] != null)
                    pooledNpcs[i].SetActive(false);
            }
        }

        void Update()
        {
            if (!Networking.IsOwner(gameObject))
                return;

            if (pooledNpcs == null || pathNetwork == null)
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

            for (int i = 0; i < pooledNpcs.Length; i++)
            {
                GameObject npc = pooledNpcs[i];
                if (npc == null)
                    continue;

                if (!npc.activeSelf)
                {
                    TryPlaceNpc(npc, i, players);
                    continue;
                }

                // a ped killed by a player is left where it fell until its body times out, rather than
                // being teleported away mid-corpse
                var ai = npc.GetComponent<GtaPedAI>();
                if (ai != null && ai.IsDead)
                    continue;

                // recycle anything everyone has walked away from
                float nearest = NearestPlayerDistance(npc.transform.position, players);
                if (nearest > despawnDistance)
                    npc.SetActive(false);
            }
        }

        private void TryPlaceNpc(GameObject npc, int index, VRCPlayerApi[] players)
        {
            // pick a player to place near
            VRCPlayerApi anchor = players[Random.Range(0, players.Length)];
            if (anchor == null || !anchor.IsValid())
                return;

            Vector3 anchorPosition = anchor.GetPosition();

            // Only populate a city with its own people.
            //
            // pedgrp.dat gives each group a San Fierro and a Las Venturas variant, which is how the three
            // cities end up looking different from one another. Placing a Los Santos crowd in San Fierro
            // would throw that away, so a ped is only used where it belongs. Peds tagged -1 fit anywhere,
            // which covers the countryside between the cities.
            int city = FindCity(anchorPosition);
            int npcCity = npcCities != null && index < npcCities.Length ? npcCities[index] : -1;

            if (npcCity >= 0 && city >= 0 && npcCity != city)
                return;

            // Populate each district with the people who belong in it.
            //
            // popcycle.dat gives, for every kind of district and every two-hour slot, what proportion of
            // the crowd comes from each ped group - office workers in Commerce, beachgoers on the sand,
            // the poor and the criminal in Ganton. A ped whose group has no share here is simply not
            // placed, which is what makes one neighbourhood read differently from the next.
            if (!IsWantedHere(index, anchorPosition))
                return;

            // choose a random point in the spawn ring around them
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(minSpawnDistance, maxSpawnDistance);

            Vector3 candidate = anchorPosition + new Vector3(
                Mathf.Cos(angle) * distance,
                0f,
                Mathf.Sin(angle) * distance);

            // snap onto the ped path network so NPCs start somewhere walkable
            int node = pathNetwork.FindNearestNode(candidate, true, 1);
            if (node < 0)
                return;

            Vector3 nodePosition = pathNetwork.GetNodePosition(node);

            // don't place right on top of somebody
            if (NearestPlayerDistance(nodePosition, players) < minSpawnDistance)
                return;

            // and make sure it's actually on the NavMesh, or the agent will never move
            NavMeshHit navHit;
            if (!NavMesh.SamplePosition(nodePosition, out navHit, 4f, NavMesh.AllAreas))
                return;

            // lift by the same amount as the agent's baseOffset, or the ped is placed pelvis-on-ground
            npc.transform.position = navHit.position + Vector3.up * groundOffset;
            npc.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            npc.SetActive(true);
        }

        /// <summary>
        /// Whether this ped's group belongs in the district at the current hour.
        /// </summary>
        private bool IsWantedHere(int index, Vector3 position)
        {
            if (npcGroups == null || groupPercent == null || index >= npcGroups.Length)
                return true;

            int group = npcGroups[index];
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

        /// <summary> City index for a position, or -1 when it is outside every city. </summary>
        public int FindCity(Vector3 position)
        {
            if (cityMins == null || cityMaxs == null || cityIndices == null)
                return -1;

            for (int i = 0; i < cityMins.Length; i++)
            {
                Vector3 min = cityMins[i];
                Vector3 max = cityMaxs[i];

                if (position.x < min.x || position.x > max.x)
                    continue;
                if (position.z < min.z || position.z > max.z)
                    continue;

                return cityIndices[i];
            }

            return -1;
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

            // no valid players - treat as infinitely far so callers recycle rather than spawn
            if (nearest < 0f)
                return float.MaxValue;

            return nearest;
        }
    }
}
