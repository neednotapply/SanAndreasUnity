using SanAndreasUnity.Behaviours;
using System;
using System.Collections;
using UGameCore.Utilities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Runs the full asset export headlessly, in dependency order.
    ///
    /// Follows the same shape as <see cref="NavMeshGeneratorCommandLine"/>: the work is a coroutine driven
    /// by the Editor update loop, so the Editor must NOT be launched with -quit - it exits itself when done.
    /// </summary>
    public static class AssetExportCommandLine
    {
        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();

            if (Array.IndexOf(args, "-quit") >= 0)
            {
                throw new ArgumentException(
                    "Asset export from command line can not be used with '-quit'. " +
                    "The Editor closes itself when the export finishes.");
            }

            CoroutineManager.Start(RunCoroutine(), null, OnFinishWithError);
        }

        /// <summary>
        /// Re-runs only the static world map export.
        ///
        /// IMPORTANT: this must NOT be launched with -nographics. StaticGeometry gates render-model loading
        /// on !F.IsInHeadlessMode, and F.IsInHeadlessMode is true whenever the graphics device is Null - so
        /// under -nographics the city exports collision only, with no visual geometry at all.
        /// </summary>
        public static void RunWorldOnly()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-quit") >= 0)
                throw new ArgumentException("Can not be used with '-quit'; the Editor closes itself when done.");

            CoroutineManager.Start(RunWorldOnlyCoroutine(), null, OnFinishWithError);
        }

        /// <summary>
        /// Re-runs only the vehicle export. Used after changing how vehicles are grouped or coloured -
        /// the full export would needlessly repeat the multi-hour world stage.
        /// </summary>
        public static void RunVehiclesOnly()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-quit") >= 0)
                throw new ArgumentException("Can not be used with '-quit'; the Editor closes itself when done.");

            CoroutineManager.Start(RunVehiclesOnlyCoroutine(), null, OnFinishWithError);
        }

        /// <summary>
        /// Re-runs only the ped and weapon exports.
        ///
        /// Needed when their mesh assets have been removed: the prefabs are overwritten unconditionally,
        /// but they reference meshes that no longer exist and draw nothing until the meshes come back.
        /// </summary>
        public static void RunPedsAndWeaponsOnly()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-quit") >= 0)
                throw new ArgumentException("Can not be used with '-quit'; the Editor closes itself when done.");

            CoroutineManager.Start(RunPedsAndWeaponsOnlyCoroutine(), null, OnFinishWithError);
        }

        private static IEnumerator RunPedsAndWeaponsOnlyCoroutine()
        {
            yield return null;

            Debug.Log("=== Headless ped + weapon export starting ===");

            EditorSceneManager.OpenScene(EditorBuildSettings.scenes[0].path, OpenSceneMode.Single);
            yield return null;
            yield return null;

            Debug.Log("Loading game data ...");
            Loader.StartLoading();

            while (Loader.IsLoading)
                yield return null;

            if (!Loader.HasLoaded)
                throw new Exception("Loader did not finish successfully");

            Debug.Log("Game data loaded.");

            IEnumerator peds = RunExport("peds", AssetExporter.ExportType.PedsFromGameFiles);
            while (peds.MoveNext())
                yield return peds.Current;

            IEnumerator weapons = RunExport("weapons", AssetExporter.ExportType.WeaponsFromGameFiles);
            while (weapons.MoveNext())
                yield return weapons.Current;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("=== Headless ped + weapon export finished ===");

            yield return null;
            yield return null;

            EditorApplication.Exit(0);
        }

        /// <summary>
        /// Re-reads only the per-vehicle data (handling + animation groups) from the game files.
        ///
        /// Much cheaper than <see cref="RunVehiclesOnly"/>: it loads the game data, rewrites one asset and
        /// exits, rather than re-exporting every vehicle model.
        /// </summary>
        public static void RunVehicleDataOnly()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-quit") >= 0)
                throw new ArgumentException("Can not be used with '-quit'; the Editor closes itself when done.");

            CoroutineManager.Start(RunVehicleDataOnlyCoroutine(), null, OnFinishWithError);
        }

        /// <summary>
        /// Exports the game-data tables the VRChat world needs (weapons, teleport destinations).
        /// </summary>
        public static void RunVRChatData()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-quit") >= 0)
                throw new ArgumentException("Can not be used with '-quit'; the Editor closes itself when done.");

            CoroutineManager.Start(RunVRChatDataCoroutine(), null, OnFinishWithError);
        }

        /// <summary>
        /// Adds lamp glow to street light models from their 2dfx data. Needs the game data loaded, since
        /// 2dfx lives inside the model files rather than in anything already exported.
        /// </summary>
        public static void RunStreetLights()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-quit") >= 0)
                throw new ArgumentException("Can not be used with '-quit'; the Editor closes itself when done.");

            CoroutineManager.Start(RunStreetLightsCoroutine(), null, OnFinishWithError);
        }

        private static IEnumerator RunStreetLightsCoroutine()
        {
            yield return null;

            Debug.Log("=== Street light export starting ===");

            EditorSceneManager.OpenScene(EditorBuildSettings.scenes[0].path, OpenSceneMode.Single);
            yield return null;
            yield return null;

            Debug.Log("Loading game data ...");
            Loader.StartLoading();

            while (Loader.IsLoading)
                yield return null;

            if (!Loader.HasLoaded)
                throw new Exception("Loader did not finish successfully");

            StreetLightExporter.Export();

            Debug.Log("=== Street light export finished ===");

            yield return null;
            yield return null;

            EditorApplication.Exit(0);
        }

        /// <summary>
        /// Writes the map in both candidate tile orders, so the correct one can be identified by eye.
        /// </summary>
        public static void RunMapOrderCheck()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-quit") >= 0)
                throw new ArgumentException("Can not be used with '-quit'; the Editor closes itself when done.");

            CoroutineManager.Start(RunMapOrderCheckCoroutine(), null, OnFinishWithError);
        }

        private static IEnumerator RunMapOrderCheckCoroutine()
        {
            yield return null;

            EditorSceneManager.OpenScene(EditorBuildSettings.scenes[0].path, OpenSceneMode.Single);
            yield return null;
            yield return null;

            Debug.Log("Loading game data ...");
            Loader.StartLoading();

            while (Loader.IsLoading)
                yield return null;

            if (!Loader.HasLoaded)
                throw new Exception("Loader did not finish successfully");

            MapTextureExporter.ExportBothOrders();

            yield return null;
            yield return null;

            EditorApplication.Exit(0);
        }

        private static IEnumerator RunVRChatDataCoroutine()
        {
            yield return null;

            Debug.Log("=== VRChat data export starting ===");

            EditorSceneManager.OpenScene(EditorBuildSettings.scenes[0].path, OpenSceneMode.Single);
            yield return null;
            yield return null;

            Debug.Log("Loading game data ...");
            Loader.StartLoading();

            while (Loader.IsLoading)
                yield return null;

            if (!Loader.HasLoaded)
                throw new Exception("Loader did not finish successfully");

            VRChatDataExporter.ExportAll();

            Debug.Log("=== VRChat data export finished ===");

            yield return null;
            yield return null;

            EditorApplication.Exit(0);
        }

        private static IEnumerator RunVehicleDataOnlyCoroutine()
        {
            yield return null;

            Debug.Log("=== Vehicle data refresh starting ===");

            EditorSceneManager.OpenScene(EditorBuildSettings.scenes[0].path, OpenSceneMode.Single);
            yield return null;
            yield return null;

            Debug.Log("Loading game data ...");
            Loader.StartLoading();

            while (Loader.IsLoading)
                yield return null;

            if (!Loader.HasLoaded)
                throw new Exception("Loader did not finish successfully");

            Debug.Log("Game data loaded.");

            new AssetExporter { IsSilentMode = true }.ExportVehicleDataOnly();

            Debug.Log("=== Vehicle data refresh finished ===");

            yield return null;
            yield return null;

            EditorApplication.Exit(0);
        }

        private static IEnumerator RunVehiclesOnlyCoroutine()
        {
            yield return null;

            Debug.Log("=== Headless vehicle export starting ===");

            EditorSceneManager.OpenScene(EditorBuildSettings.scenes[0].path, OpenSceneMode.Single);
            yield return null;
            yield return null;

            Debug.Log("Loading game data ...");
            Loader.StartLoading();

            while (Loader.IsLoading)
                yield return null;

            if (!Loader.HasLoaded)
                throw new Exception("Loader did not finish successfully");

            Debug.Log("Game data loaded.");

            IEnumerator vehicles = RunExport("vehicles", AssetExporter.ExportType.VehiclesFromGameFiles);
            while (vehicles.MoveNext())
                yield return vehicles.Current;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("=== Headless vehicle export finished ===");

            yield return null;
            yield return null;

            EditorApplication.Exit(0);
        }

        private static IEnumerator RunWorldOnlyCoroutine()
        {
            yield return null;

            Debug.Log("=== Headless world export starting ===");

            if (UnityEngine.SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                throw new Exception(
                    "Running with a Null graphics device (-nographics). Static geometry would export " +
                    "collision only, with no render meshes. Re-launch without -nographics.");
            }

            EditorSceneManager.OpenScene(EditorBuildSettings.scenes[0].path, OpenSceneMode.Single);
            yield return null;
            yield return null;

            Debug.Log("Loading game data ...");
            Loader.StartLoading();

            while (Loader.IsLoading)
                yield return null;

            if (!Loader.HasLoaded)
                throw new Exception("Loader did not finish successfully");

            Debug.Log("Game data loaded.");

            IEnumerator worldCoroutine = RunExport("static world map", AssetExporter.ExportType.FromGameFiles);
            while (worldCoroutine.MoveNext())
                yield return worldCoroutine.Current;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("=== Headless world export finished ===");

            yield return null;
            yield return null;

            EditorApplication.Exit(0);
        }

        private static IEnumerator RunCoroutine()
        {
            yield return null;

            Debug.Log("=== Headless asset export starting ===");

            // the loader singletons live on the startup scene's GameManager prefab
            EditorSceneManager.OpenScene(EditorBuildSettings.scenes[0].path, OpenSceneMode.Single);
            yield return null;
            yield return null;

            Debug.Log("Loading game data ...");
            Loader.StartLoading();

            while (Loader.IsLoading)
                yield return null;

            if (!Loader.HasLoaded)
                throw new Exception("Loader did not finish successfully");

            Debug.Log("Game data loaded.");

            // Audio: SFX only by default - stream/radio tracks decode to uncompressed PCM and are
            // enormous. Pass -exportStreamAudio to include them.
            bool exportStreams = Array.IndexOf(
                Environment.GetCommandLineArgs(), "-exportStreamAudio") >= 0;

            // Order matters: animations must precede peds, because ped export builds Animator
            // controllers from the exported clips and silently skips that step if they don't exist.
            var stages = new (string label, AssetExporter.ExportType type, bool streams)[]
            {
                ("animations", AssetExporter.ExportType.AnimationsFromGameFiles, false),
                ("peds", AssetExporter.ExportType.PedsFromGameFiles, false),
                ("vehicles", AssetExporter.ExportType.VehiclesFromGameFiles, false),
                ("weapons", AssetExporter.ExportType.WeaponsFromGameFiles, false),
                (exportStreams ? "audio (SFX + streams)" : "audio (SFX)",
                    AssetExporter.ExportType.AudioFromGameFiles, exportStreams),
            };

            foreach (var stage in stages)
            {
                // CoroutineRunner ignores the yielded value, so a nested IEnumerator is never iterated -
                // "yield return RunExport(...)" silently does nothing. Drive it by hand instead.
                IEnumerator stageCoroutine = RunExport(stage.label, stage.type, stage.streams);
                while (stageCoroutine.MoveNext())
                    yield return stageCoroutine.Current;
            }

            // Path node network (traffic + ped AI data)
            Debug.Log("--- exporting path network ---");
            try
            {
                var network = PathNetworkExporter.ExportToAsset("Assets/ExportedAssets/PathNetwork.asset");
                Debug.Log($"Path network exported: nodes {network.NodeCount}, links {network.LinkCount}, " +
                    $"nav nodes {network.NavNodeCount}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Path network export failed: {ex}");
            }

            yield return null;

            // The static world itself - every map object, its meshes, materials, textures and collision,
            // plus a world prefab. By far the heaviest step (it is the whole city), so it runs last: if it
            // runs out of memory, everything above is already on disk.
            IEnumerator worldCoroutine = RunExport("static world map", AssetExporter.ExportType.FromGameFiles);
            while (worldCoroutine.MoveNext())
                yield return worldCoroutine.Current;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("=== Headless asset export finished ===");

            yield return null;
            yield return null;

            EditorApplication.Exit(0);
        }

        private static IEnumerator RunExport(
            string label, AssetExporter.ExportType exportType, bool exportStreamAudio = false)
        {
            Debug.Log($"--- exporting {label} ---");

            var exporter = new AssetExporter
            {
                IsSilentMode = true,
                ExportPrefabs = true,
                ExportStreamAudio = exportStreamAudio,
            };

            exporter.Export(exportType);

            while (exporter.IsRunning)
                yield return null;

            if (exporter.FinishedSuccessfully)
                Debug.Log($"--- {label}: finished successfully ---");
            else
                Debug.LogError($"--- {label}: did NOT finish successfully ---");
        }

        static void OnFinishWithError(Exception exception)
        {
            Debug.LogError($"Headless asset export failed: {exception}");
            EditorApplication.Exit(1);
        }
    }
}
