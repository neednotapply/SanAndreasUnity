using UdonSharp;
using UnityEngine;

namespace SanAndreasUnity.VRChat
{
    /// <summary>
    /// Runtime holder for GTA's path node graph, baked in from the exported GtaPathNetwork asset.
    ///
    /// Udon cannot see custom types (so a ScriptableObject can't be referenced directly), has no
    /// Dictionary, and no generics - hence the flat parallel arrays and the pre-resolved link indices.
    /// Walking the graph here is pure array indexing.
    ///
    /// This behaviour holds data and answers queries; it has no per-frame cost and never syncs.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GtaPathNetworkData : UdonSharpBehaviour
    {
        [Header("Nodes")]
        public Vector3[] nodePositions;
        public bool[] nodeIsPedNode;
        public float[] nodePathWidth;
        public bool[] nodeIsWater;
        public bool[] nodeEmergencyOnly;
        public int[] nodeSpawnProbability;

        [Header("Links (pre-resolved to node indices)")]
        public int[] nodeLinkStart;
        public int[] nodeLinkCount;
        public int[] linkTargetNodeIndex;
        public float[] linkLength;

        [Header("Traffic lights")]
        /// <summary>
        /// Which light phase governs each node: -1 for no traffic light, otherwise 0 or 1 for the two
        /// opposing directions of a junction. Derived from the nav node data at bake time so the runtime
        /// can test a node directly instead of searching nav nodes.
        /// </summary>
        public int[] nodeTrafficLightDirection;

        [Header("Area index")]
        public int[] areaNodeStart;
        public int[] areaNodeCount;
        public float areaSize = 750f;
        public float areaGridMin = -3000f;

        // UdonSharp does not support static fields, so grid dimension is a const
        private const int AreaGridDimension = 8;

        public int NodeCount
        {
            get
            {
                if (nodePositions == null)
                    return 0;
                return nodePositions.Length;
            }
        }

        /// <summary> Number of outgoing links a node has. </summary>
        public int GetLinkCount(int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= NodeCount)
                return 0;
            return nodeLinkCount[nodeIndex];
        }

        /// <summary>
        /// Node index that link <paramref name="linkOrdinal"/> of <paramref name="nodeIndex"/> points at,
        /// or -1 when out of range or unresolved.
        /// </summary>
        public int GetLinkedNode(int nodeIndex, int linkOrdinal)
        {
            if (nodeIndex < 0 || nodeIndex >= NodeCount)
                return -1;

            if (linkOrdinal < 0 || linkOrdinal >= nodeLinkCount[nodeIndex])
                return -1;

            int linkIndex = nodeLinkStart[nodeIndex] + linkOrdinal;
            if (linkIndex < 0 || linkIndex >= linkTargetNodeIndex.Length)
                return -1;

            return linkTargetNodeIndex[linkIndex];
        }

        public Vector3 GetNodePosition(int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= NodeCount)
                return Vector3.zero;
            return nodePositions[nodeIndex];
        }

        /// <summary> Area id (0..63) containing a world position, or -1 when outside the map. </summary>
        public int GetAreaIdFromPosition(Vector3 position)
        {
            int x = GetAreaAxisIndex(position.x);
            int y = GetAreaAxisIndex(position.z);

            if (x < 0 || y < 0)
                return -1;

            return y * AreaGridDimension + x;
        }

        private int GetAreaAxisIndex(float value)
        {
            int index = (int)Mathf.Floor((value - areaGridMin) / areaSize);

            if (index < 0 || index >= AreaGridDimension)
                return -1;

            return index;
        }

        /// <summary>
        /// Nearest node to a position, restricted to ped or vehicle nodes.
        ///
        /// Only the containing area and its immediate neighbours are scanned. A full scan would be tens of
        /// thousands of nodes per call, which Udon cannot afford at runtime.
        /// Returns -1 when nothing was found (increase <paramref name="areaRadius"/> to widen the search).
        /// </summary>
        public int FindNearestNode(Vector3 position, bool wantPedNode, int areaRadius)
        {
            int centerX = GetAreaAxisIndex(position.x);
            int centerY = GetAreaAxisIndex(position.z);

            if (centerX < 0 || centerY < 0)
                return -1;

            if (areaRadius < 0)
                areaRadius = 0;

            int bestIndex = -1;
            float bestSqrDistance = 0f;

            for (int offsetY = -areaRadius; offsetY <= areaRadius; offsetY++)
            {
                int areaY = centerY + offsetY;
                if (areaY < 0 || areaY >= AreaGridDimension)
                    continue;

                for (int offsetX = -areaRadius; offsetX <= areaRadius; offsetX++)
                {
                    int areaX = centerX + offsetX;
                    if (areaX < 0 || areaX >= AreaGridDimension)
                        continue;

                    int areaId = areaY * AreaGridDimension + areaX;
                    int start = areaNodeStart[areaId];
                    int count = areaNodeCount[areaId];

                    for (int i = 0; i < count; i++)
                    {
                        int nodeIndex = start + i;

                        if (nodeIsPedNode[nodeIndex] != wantPedNode)
                            continue;

                        float sqrDistance = (nodePositions[nodeIndex] - position).sqrMagnitude;

                        if (bestIndex < 0 || sqrDistance < bestSqrDistance)
                        {
                            bestIndex = nodeIndex;
                            bestSqrDistance = sqrDistance;
                        }
                    }
                }
            }

            return bestIndex;
        }

        /// <summary>
        /// Picks a random link target from a node, avoiding <paramref name="previousNodeIndex"/> so wanderers
        /// don't immediately double back. Falls back to going back when there is no other option (dead end).
        /// Returns -1 when the node has no usable links.
        /// </summary>
        public int PickWanderTarget(int nodeIndex, int previousNodeIndex)
        {
            int count = GetLinkCount(nodeIndex);
            if (count <= 0)
                return -1;

            // count candidates that aren't where we came from and aren't unresolved/water
            int numCandidates = 0;
            for (int i = 0; i < count; i++)
            {
                int candidate = GetLinkedNode(nodeIndex, i);
                if (candidate < 0 || candidate == previousNodeIndex)
                    continue;
                if (nodeIsWater[candidate])
                    continue;
                numCandidates++;
            }

            if (numCandidates == 0)
            {
                // dead end - going back is better than stopping
                for (int i = 0; i < count; i++)
                {
                    int candidate = GetLinkedNode(nodeIndex, i);
                    if (candidate >= 0)
                        return candidate;
                }
                return -1;
            }

            int chosen = Random.Range(0, numCandidates);

            for (int i = 0; i < count; i++)
            {
                int candidate = GetLinkedNode(nodeIndex, i);
                if (candidate < 0 || candidate == previousNodeIndex)
                    continue;
                if (nodeIsWater[candidate])
                    continue;

                if (chosen == 0)
                    return candidate;
                chosen--;
            }

            return -1;
        }
    }
}
