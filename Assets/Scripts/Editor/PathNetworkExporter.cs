using SanAndreasUnity.Export;
using SanAndreasUnity.Importing.Paths;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Bakes the runtime-parsed GTA path node network into a <see cref="GtaPathNetwork"/> asset.
    ///
    /// Nothing persists this data today - it's parsed from nodes0..63.dat on every load - so it has to be
    /// exported before traffic/ped AI can run without the importer.
    /// </summary>
    public static class PathNetworkExporter
    {
        private const int NumAreas = 64;

        public static GtaPathNetwork BuildFromLoadedNodes()
        {
            var nodeFiles = NodeReader.NodeFiles;

            if (null == nodeFiles || nodeFiles.Count < NumAreas)
                throw new System.InvalidOperationException(
                    $"Path nodes are not loaded (found {(nodeFiles == null ? 0 : nodeFiles.Count)} areas, expected {NumAreas}). " +
                    "Load game data first.");

            // Global index of the first node of each area. Within an area, node ids are contiguous:
            // vehicle nodes first, then ped nodes - matching NodeFile.GetNodeById().
            var areaNodeOffset = new int[NumAreas];
            int totalNodes = 0;
            int totalLinks = 0;
            int totalNavNodes = 0;

            for (int areaId = 0; areaId < NumAreas; areaId++)
            {
                areaNodeOffset[areaId] = totalNodes;
                NodeFile area = nodeFiles[areaId];
                totalNodes += area.VehicleNodes.Count + area.PedNodes.Count;
                totalLinks += area.NodeLinks.Count;
                totalNavNodes += area.NavNodes.Count;
            }

            var network = ScriptableObject.CreateInstance<GtaPathNetwork>();

            network.nodePositions = new Vector3[totalNodes];
            network.nodeAreaId = new int[totalNodes];
            network.nodeLocalId = new int[totalNodes];
            network.nodePathWidth = new float[totalNodes];
            network.nodeType = new int[totalNodes];
            network.nodeIsPedNode = new bool[totalNodes];
            network.nodeTrafficLevel = new int[totalNodes];
            network.nodeRoadBlocks = new bool[totalNodes];
            network.nodeIsWater = new bool[totalNodes];
            network.nodeEmergencyOnly = new bool[totalNodes];
            network.nodeIsHighway = new bool[totalNodes];
            network.nodeSpawnProbability = new int[totalNodes];
            network.nodeParking = new bool[totalNodes];
            network.nodeLinkStart = new int[totalNodes];
            network.nodeLinkCount = new int[totalNodes];

            // nodes are written grouped by area, so each area is one contiguous range - that is what
            // makes cheap spatial queries possible at runtime
            network.areaNodeStart = new int[NumAreas];
            network.areaNodeCount = new int[NumAreas];
            for (int areaId = 0; areaId < NumAreas; areaId++)
            {
                NodeFile area = nodeFiles[areaId];
                network.areaNodeStart[areaId] = areaNodeOffset[areaId];
                network.areaNodeCount[areaId] = area.VehicleNodes.Count + area.PedNodes.Count;
            }

            network.linkTargetNodeIndex = new int[totalLinks];
            network.linkLength = new float[totalLinks];

            network.navNodePositions = new Vector2[totalNavNodes];
            network.navNodeTargetNodeIndex = new int[totalNavNodes];
            network.navNodeDirections = new Vector2[totalNavNodes];
            network.navNodeWidth = new int[totalNavNodes];
            network.navNodeNumLeftLanes = new int[totalNavNodes];
            network.navNodeNumRightLanes = new int[totalNavNodes];
            network.navNodeTrafficLightDirection = new int[totalNavNodes];
            network.navNodeTrafficLightBehavior = new int[totalNavNodes];
            network.navNodeIsTrainCrossing = new bool[totalNavNodes];

            // links are stored per-area and indexed by the node's BaseLinkID, so keep a per-area base
            var areaLinkOffset = new int[NumAreas];
            int linkWriteIndex = 0;
            int navWriteIndex = 0;
            int numUnresolvedLinks = 0;

            for (int areaId = 0; areaId < NumAreas; areaId++)
            {
                NodeFile area = nodeFiles[areaId];
                areaLinkOffset[areaId] = linkWriteIndex;

                for (int i = 0; i < area.NodeLinks.Count; i++)
                {
                    NodeLink link = area.NodeLinks[i];

                    int targetGlobalIndex = ResolveGlobalNodeIndex(
                        nodeFiles, areaNodeOffset, link.AreaID, link.NodeID);

                    if (targetGlobalIndex < 0)
                        numUnresolvedLinks++;

                    network.linkTargetNodeIndex[linkWriteIndex] = targetGlobalIndex;
                    network.linkLength[linkWriteIndex] = link.Length;
                    linkWriteIndex++;
                }

                for (int i = 0; i < area.NavNodes.Count; i++)
                {
                    NavNode navNode = area.NavNodes[i];

                    network.navNodePositions[navWriteIndex] = navNode.Position;
                    network.navNodeTargetNodeIndex[navWriteIndex] = ResolveGlobalNodeIndex(
                        nodeFiles, areaNodeOffset, navNode.TargetAreaID, navNode.TargetNodeID);
                    network.navNodeDirections[navWriteIndex] = navNode.Direction;
                    network.navNodeWidth[navWriteIndex] = navNode.Width;
                    network.navNodeNumLeftLanes[navWriteIndex] = navNode.NumLeftLanes;
                    network.navNodeNumRightLanes[navWriteIndex] = navNode.NumRightLanes;
                    network.navNodeTrafficLightDirection[navWriteIndex] = navNode.TrafficLightDirection;
                    network.navNodeTrafficLightBehavior[navWriteIndex] = navNode.TrafficLightBehavior;
                    network.navNodeIsTrainCrossing[navWriteIndex] = navNode.IsTrainCrossing != 0;
                    navWriteIndex++;
                }
            }

            for (int areaId = 0; areaId < NumAreas; areaId++)
            {
                NodeFile area = nodeFiles[areaId];
                int numVehicleNodes = area.VehicleNodes.Count;
                int numNodesInArea = numVehicleNodes + area.PedNodes.Count;

                for (int localId = 0; localId < numNodesInArea; localId++)
                {
                    bool isPedNode = localId >= numVehicleNodes;
                    PathNode node = isPedNode
                        ? area.PedNodes[localId - numVehicleNodes]
                        : area.VehicleNodes[localId];

                    int globalIndex = areaNodeOffset[areaId] + localId;

                    network.nodePositions[globalIndex] = node.Position;
                    network.nodeAreaId[globalIndex] = areaId;
                    network.nodeLocalId[globalIndex] = localId;
                    network.nodePathWidth[globalIndex] = node.PathWidth;
                    network.nodeType[globalIndex] = node.NodeType;
                    network.nodeIsPedNode[globalIndex] = isPedNode;

                    network.nodeTrafficLevel[globalIndex] = (int)node.Flags.TrafficLevel;
                    network.nodeRoadBlocks[globalIndex] = node.Flags.RoadBlocks;
                    network.nodeIsWater[globalIndex] = node.Flags.IsWater;
                    network.nodeEmergencyOnly[globalIndex] = node.Flags.EmergencyOnly;
                    network.nodeIsHighway[globalIndex] = node.Flags.IsHighway;
                    network.nodeSpawnProbability[globalIndex] = node.Flags.SpawnProbability;
                    network.nodeParking[globalIndex] = node.Flags.Parking;

                    // BaseLinkID is relative to the area's own link list
                    network.nodeLinkStart[globalIndex] = areaLinkOffset[areaId] + node.BaseLinkID;
                    network.nodeLinkCount[globalIndex] = node.LinkCount;
                }
            }

            if (numUnresolvedLinks > 0)
                Debug.LogWarning($"Path network export: {numUnresolvedLinks} links could not be resolved and were stored as -1.");

            return network;
        }

        private static int ResolveGlobalNodeIndex(
            IReadOnlyList<NodeFile> nodeFiles, int[] areaNodeOffset, int areaId, int localNodeId)
        {
            if (areaId < 0 || areaId >= NumAreas)
                return -1;

            NodeFile area = nodeFiles[areaId];
            int numNodesInArea = area.VehicleNodes.Count + area.PedNodes.Count;

            if (localNodeId < 0 || localNodeId >= numNodesInArea)
                return -1;

            return areaNodeOffset[areaId] + localNodeId;
        }

        public static GtaPathNetwork ExportToAsset(string assetPath)
        {
            GtaPathNetwork network = BuildFromLoadedNodes();

            string directory = System.IO.Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                System.IO.Directory.CreateDirectory(directory);

            var existing = AssetDatabase.LoadAssetAtPath<GtaPathNetwork>(assetPath);
            if (existing != null)
                AssetDatabase.DeleteAsset(assetPath);

            AssetDatabase.CreateAsset(network, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return network;
        }
    }
}
