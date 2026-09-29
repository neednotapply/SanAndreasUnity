using SanAndreasUnity.Export;
using SanAndreasUnity.VRChat;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Copies an exported <see cref="GtaPathNetwork"/> asset into a <see cref="GtaPathNetworkData"/>
    /// component in the scene.
    ///
    /// This step exists because Udon cannot reference custom ScriptableObject types - the data has to live
    /// in serialized fields on an UdonSharpBehaviour instead.
    /// </summary>
    public static class PathNetworkBaker
    {
        [MenuItem(EditorCore.MenuName + "/" + "Bake path network into selected Udon behaviour")]
        static void BakeIntoSelection()
        {
            var target = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<GtaPathNetworkData>()
                : null;

            if (null == target)
            {
                EditorUtility.DisplayDialog(
                    "",
                    $"Select a GameObject with a {nameof(GtaPathNetworkData)} component first.",
                    "Ok");
                return;
            }

            string assetPath = EditorUtility.OpenFilePanel("Select exported path network asset", "Assets", "asset");
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            assetPath = FileUtil.GetProjectRelativePath(assetPath);
            var network = AssetDatabase.LoadAssetAtPath<GtaPathNetwork>(assetPath);

            if (null == network)
            {
                EditorUtility.DisplayDialog("", "That asset is not a path network.", "Ok");
                return;
            }

            Bake(network, target);

            EditorUtility.DisplayDialog(
                "",
                $"Baked path network into {target.name}.\n\n" +
                $"nodes: {network.NodeCount}\nlinks: {network.LinkCount}",
                "Ok");
        }

        public static void Bake(GtaPathNetwork network, GtaPathNetworkData target)
        {
            Undo.RecordObject(target, "Bake path network");

            target.nodePositions = network.nodePositions;
            target.nodeIsPedNode = network.nodeIsPedNode;
            target.nodePathWidth = network.nodePathWidth;
            target.nodeIsWater = network.nodeIsWater;
            target.nodeEmergencyOnly = network.nodeEmergencyOnly;
            target.nodeSpawnProbability = network.nodeSpawnProbability;

            target.nodeLinkStart = network.nodeLinkStart;
            target.nodeLinkCount = network.nodeLinkCount;
            target.linkTargetNodeIndex = network.linkTargetNodeIndex;
            target.linkLength = network.linkLength;

            target.areaNodeStart = network.areaNodeStart;
            target.areaNodeCount = network.areaNodeCount;
            target.areaSize = network.areaSize;
            target.areaGridMin = network.areaGridMin;

            target.nodeTrafficLightDirection = BuildTrafficLightDirections(network);

            EditorUtility.SetDirty(target);
        }

        /// <summary>
        /// Projects the nav nodes' traffic light data onto the path nodes they control.
        ///
        /// GTA stores light information on nav nodes, each of which points at a path node. Searching that
        /// relation at runtime would mean scanning ~31k nav nodes per query, so it is flattened here into
        /// one entry per path node: -1 for no light, otherwise the phase (0 or 1) that must be green for
        /// traffic to proceed through it.
        /// </summary>
        private static int[] BuildTrafficLightDirections(GtaPathNetwork network)
        {
            int nodeCount = network.NodeCount;
            var directions = new int[nodeCount];

            for (int i = 0; i < nodeCount; i++)
                directions[i] = -1;

            if (network.navNodeTargetNodeIndex == null)
                return directions;

            int assigned = 0;

            for (int i = 0; i < network.navNodeTargetNodeIndex.Length; i++)
            {
                int target = network.navNodeTargetNodeIndex[i];
                if (target < 0 || target >= nodeCount)
                    continue;

                // behaviour 0 means the nav node carries no light
                if (network.navNodeTrafficLightBehavior[i] == 0)
                    continue;

                directions[target] = network.navNodeTrafficLightDirection[i];
                assigned++;
            }

            Debug.Log($"Traffic lights: {assigned} path nodes are light-controlled");
            return directions;
        }
    }
}
