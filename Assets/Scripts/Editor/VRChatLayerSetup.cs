using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Brings the project's layers and collision matrix in line with VRChat's, and assigns exported
    /// content to the layers VRChat expects.
    ///
    /// This matters more than it looks. SanAndreasUnity defines its own layers (Vehicle, World, PedBone,
    /// VehicleMesh...) in slots 8-14, which is exactly where VRChat reserves Interactive, Player,
    /// PlayerLocal, Environment, UiMenu, Pickup and PickupNoEnvironment. Handing VRChat a car on the layer
    /// it treats as "Player", or world geometry on the layer it treats as "PlayerLocal", produces physics
    /// and interaction behaviour that looks like broken game logic but is really a layer mismatch.
    ///
    /// Applying the SDK's layer setup renumbers custom layers, so anything already placed has to be
    /// reassigned afterwards - which is what AssignSceneLayers does.
    /// </summary>
    public static class VRChatLayerSetup
    {
        // VRChat layer names, resolved by name rather than index so this stays correct if VRChat shifts them
        public const string LayerDefault = "Default";
        public const string LayerEnvironment = "Environment";
        public const string LayerInteractive = "Interactive";
        public const string LayerWalkthrough = "Walkthrough";

        [MenuItem(EditorCore.MenuName + "/" + "VRChat/Set up layers and collision matrix")]
        public static void SetupFromMenu()
        {
            bool changed = EnsureLayersAndCollisionMatrix();

            EditorUtility.DisplayDialog(
                "",
                changed
                    ? "Layers and collision matrix updated to match VRChat.\n\n" +
                      "Custom layers were renumbered, so rebuild the scene to reassign objects."
                    : "Layers and collision matrix already match VRChat.",
                "Ok");
        }

        /// <summary> Applies the SDK's layer + collision setup if needed. Returns true if anything changed. </summary>
        public static bool EnsureLayersAndCollisionMatrix()
        {
            bool changed = false;

            if (!UpdateLayers.AreLayersSetup())
            {
                Debug.Log("VRChat layers are not set up - applying SDK layer configuration");
                UpdateLayers.SetupEditorLayers();
                changed = true;
            }

            if (!UpdateLayers.IsCollisionLayerMatrixSetup())
            {
                Debug.Log("VRChat collision matrix is not set up - applying SDK collision configuration");
                UpdateLayers.SetupCollisionLayerMatrix();
                changed = true;
            }

            Debug.Log($"VRChat layers setup: {UpdateLayers.AreLayersSetup()}, " +
                $"collision matrix setup: {UpdateLayers.IsCollisionLayerMatrixSetup()}");

            return changed;
        }

        /// <summary>
        /// Puts a GameObject and everything under it on a named layer. Missing layers are reported rather
        /// than silently skipped, since a wrong layer is invisible until physics misbehaves.
        /// </summary>
        public static void SetLayerRecursive(GameObject go, string layerName)
        {
            int layer = LayerMask.NameToLayer(layerName);
            if (layer < 0)
            {
                Debug.LogWarning($"Layer '{layerName}' does not exist - run the VRChat layer setup first");
                return;
            }

            SetLayerRecursive(go, layer);
        }

        public static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;

            foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = layer;
        }
    }
}
