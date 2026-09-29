using UnityEngine;

namespace SanAndreasUnity.Export
{
    /// <summary>
    /// A flattened, self-contained snapshot of GTA's path node network (the road/ped graph read from
    /// nodes0..63.dat), suitable for driving traffic and ped AI without the importer.
    ///
    /// The layout is deliberately "dumb": parallel arrays of primitives rather than arrays of structs,
    /// and links pre-resolved to global node indices. That is what makes it usable from Udon, which has
    /// no Dictionary and cannot see custom types - at runtime, walking from a node to its neighbours is
    /// pure array indexing with no lookups.
    ///
    /// Node <c>i</c> owns the links in <c>[nodeLinkStart[i], nodeLinkStart[i] + nodeLinkCount[i])</c>.
    /// </summary>
    public class GtaPathNetwork : ScriptableObject
    {
        [Header("Nodes")]
        public Vector3[] nodePositions;

        /// <summary> Original area (0..63) the node came from - kept for debugging/round-tripping. </summary>
        public int[] nodeAreaId;

        /// <summary> Original per-area node id - kept for debugging/round-tripping. </summary>
        public int[] nodeLocalId;

        public float[] nodePathWidth;
        public int[] nodeType;

        /// <summary> False for vehicle (road) nodes, true for ped nodes. </summary>
        public bool[] nodeIsPedNode;

        [Header("Node flags")]
        /// <summary> 0 = Full, 1 = High, 2 = Medium, 3 = Low. </summary>
        public int[] nodeTrafficLevel;
        public bool[] nodeRoadBlocks;
        public bool[] nodeIsWater;
        public bool[] nodeEmergencyOnly;
        public bool[] nodeIsHighway;
        public int[] nodeSpawnProbability;
        public bool[] nodeParking;

        [Header("Links (pre-resolved to global node indices)")]
        public int[] nodeLinkStart;
        public int[] nodeLinkCount;

        /// <summary> Index into the node arrays of the link's target. -1 when it could not be resolved. </summary>
        public int[] linkTargetNodeIndex;
        public float[] linkLength;

        [Header("Area index (spatial acceleration)")]
        /// <summary>
        /// Nodes are stored grouped by GTA's own 8x8 area grid (750m per area), so a spatial query only
        /// has to scan the relevant area plus its neighbours instead of every node in the map. Indexed by
        /// area id (0..63), where area id = areaY * 8 + areaX.
        /// </summary>
        public int[] areaNodeStart;
        public int[] areaNodeCount;

        /// <summary> Size of one area in world units. </summary>
        public float areaSize = 750f;

        /// <summary> World-space minimum corner of the area grid (GTA's map spans -3000..3000). </summary>
        public float areaGridMin = -3000f;

        public const int AreaGridDimension = 8;

        [Header("Nav nodes (lanes, traffic lights, crossings)")]
        public Vector2[] navNodePositions;

        /// <summary> Index into the node arrays, or -1 when unresolved. </summary>
        public int[] navNodeTargetNodeIndex;
        public Vector2[] navNodeDirections;
        public int[] navNodeWidth;
        public int[] navNodeNumLeftLanes;
        public int[] navNodeNumRightLanes;
        public int[] navNodeTrafficLightDirection;
        public int[] navNodeTrafficLightBehavior;
        public bool[] navNodeIsTrainCrossing;

        public int NodeCount => nodePositions != null ? nodePositions.Length : 0;
        public int LinkCount => linkTargetNodeIndex != null ? linkTargetNodeIndex.Length : 0;
        public int NavNodeCount => navNodePositions != null ? navNodePositions.Length : 0;
    }
}
