using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    public class AssetExporterWindow : EditorWindowBase
    {
        private Vector2 m_scrollViewPos = Vector2.zero;
        private readonly AssetExporter m_assetExporter = new AssetExporter();



        public AssetExporterWindow()
        {
            this.titleContent = new GUIContent("Asset exporter");
            this.minSize = new Vector2(400, 200);
            this.position = new Rect(this.position.center, new Vector2(400, 500));
        }

        [MenuItem(EditorCore.MenuName + "/" + "Asset exporter")]
        static void Init()
        {
            var window = GetWindow<AssetExporterWindow>();
            window.Show();
        }

        void OnGUI()
        {
            m_scrollViewPos = EditorGUILayout.BeginScrollView(m_scrollViewPos);

            EditorGUILayout.HelpBox(
                "This tool can export assets from game into Unity project.\n" +
                "Later you can use these assets inside Unity Editor like any other asset. " +
                "It will store them in a separate folder, and will only export those objects that were not already exported. This means that you can cancel the process, and when you start it next time, it will skip already exported assets.",
                MessageType.Info,
                true);

            GUILayout.Space(30);

            EditorGUILayout.LabelField("Folder where assets are placed: " + m_assetExporter.SelectedFolder);

            m_assetExporter.ExportRenderMeshes = EditorGUILayout.Toggle("Export render meshes", m_assetExporter.ExportRenderMeshes);
            m_assetExporter.ExportMaterials = EditorGUILayout.Toggle("Export materials", m_assetExporter.ExportMaterials);
            m_assetExporter.ExportTextures = EditorGUILayout.Toggle("Export textures", m_assetExporter.ExportTextures);
            m_assetExporter.ExportCollisionMeshes = EditorGUILayout.Toggle("Export collision meshes", m_assetExporter.ExportCollisionMeshes);
            m_assetExporter.ExportPrefabs = EditorGUILayout.Toggle("Export prefabs", m_assetExporter.ExportPrefabs);

            GUILayout.Space(30);

            if (GUILayout.Button("Export from game files"))
                m_assetExporter.Export(AssetExporter.ExportType.FromGameFiles);

            if (GUILayout.Button("Export from world"))
                m_assetExporter.Export(AssetExporter.ExportType.FromLoadedWorld);

            if (GUILayout.Button("Export from selection"))
                m_assetExporter.Export(AssetExporter.ExportType.FromSelection);

            GUILayout.Space(20);

            EditorGUILayout.HelpBox(
                "Vehicles and peds are normally only created at runtime and discarded, so they never " +
                "become project assets. These buttons build every model from the game files and export " +
                "it. Enable \"Export prefabs\" above to also get a prefab per model.\n\n" +
                "Export animations BEFORE peds - peds get an Animator wired to a locomotion blend tree " +
                "built from the exported clips, and that step is skipped if the clips don't exist yet.",
                MessageType.Info,
                true);

            m_assetExporter.CreateAvatarAndAnimator = EditorGUILayout.Toggle(
                "Create avatar + animator", m_assetExporter.CreateAvatarAndAnimator);

            if (GUILayout.Button("Export vehicles from game files"))
                m_assetExporter.Export(AssetExporter.ExportType.VehiclesFromGameFiles);

            if (GUILayout.Button("Export peds from game files"))
                m_assetExporter.Export(AssetExporter.ExportType.PedsFromGameFiles);

            if (GUILayout.Button("Export weapons from game files"))
                m_assetExporter.Export(AssetExporter.ExportType.WeaponsFromGameFiles);

            GUILayout.Space(20);

            m_assetExporter.MakeAnimationsMecanimCompatible = EditorGUILayout.Toggle(
                "Mecanim-compatible anims", m_assetExporter.MakeAnimationsMecanimCompatible);

            EditorGUILayout.HelpBox(
                "Animations are imported as legacy clips (for the built-in Animation component). " +
                "Mecanim/Animator - which VRChat uses - rejects legacy clips, so leave the toggle above " +
                "enabled when exporting for VRChat. Disable it only if you want clips for the legacy " +
                "Animation component used by this project at runtime.",
                MessageType.Info,
                true);

            if (GUILayout.Button("Export animations from game files"))
                m_assetExporter.Export(AssetExporter.ExportType.AnimationsFromGameFiles);

            GUILayout.Space(20);

            EditorGUILayout.HelpBox(
                "The path node network (roads, ped paths, lanes, traffic lights) is parsed from " +
                "nodes0..63.dat on every load and never persisted. Export it so traffic and ped AI can " +
                "run without the importer. Links are pre-resolved to array indices so Udon can walk the " +
                "graph without any lookups.",
                MessageType.Info,
                true);

            if (GUILayout.Button("Export path node network"))
                ExportPathNetwork();

            GUILayout.Space(20);

            m_assetExporter.ExportSfxAudio = EditorGUILayout.Toggle(
                "Export SFX audio", m_assetExporter.ExportSfxAudio);
            m_assetExporter.ExportStreamAudio = EditorGUILayout.Toggle(
                "Export stream audio", m_assetExporter.ExportStreamAudio);

            EditorGUILayout.HelpBox(
                "SFX are stored as PCM and convert directly. Stream audio (radio/music) is Ogg Vorbis " +
                "played through a streaming clip that holds no data, so exporting it requires decoding " +
                "every track to uncompressed PCM - that is easily multiple GB and will not fit in a " +
                "VRChat world. Leave it off unless you specifically need it.",
                MessageType.Warning,
                true);

            if (GUILayout.Button("Export audio from game files"))
                m_assetExporter.Export(AssetExporter.ExportType.AudioFromGameFiles);

            EditorGUILayout.EndScrollView();
        }

        void ExportPathNetwork()
        {
            if (!Behaviours.Loader.HasLoaded)
            {
                EditorUtility.DisplayDialog("", "Game data must be loaded first.", "Ok");
                return;
            }

            try
            {
                string path = m_assetExporter.SelectedFolder + "/PathNetwork.asset";
                var network = PathNetworkExporter.ExportToAsset(path);

                string message = $"Exported path network to {path}\n\n" +
                    $"nodes: {network.NodeCount}\nlinks: {network.LinkCount}\nnav nodes: {network.NavNodeCount}";

                Debug.Log(message);
                EditorUtility.DisplayDialog("", message, "Ok");
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("", "Failed to export path network:\n\n" + ex.Message, "Ok");
            }
        }
    }
}
