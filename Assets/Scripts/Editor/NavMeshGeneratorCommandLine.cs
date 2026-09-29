using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.Linq;
using UnityEditor;
using UGameCore.Utilities;
using UnityEngine.AI;
using SanAndreasUnity.Behaviours.World;
using SanAndreasUnity.Behaviours;
using UnityEditor.SceneManagement;

namespace SanAndreasUnity.Editor
{
    public static class NavMeshGeneratorCommandLine
    {
        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();

            // we need to check this before starting coroutine, or otherwise Editor may exit
            if (args.Contains("-quit"))
            {
                throw new ArgumentException("Nav mesh generation from command line can not be used with '-quit' argument. " +
                    "Remove the argument, and Editor will be closed when nav mesh generation is finished.");
            }

            CoroutineManager.Start(RunCoroutine(), null, OnFinishWithError);
        }

        private static IEnumerator RunCoroutine()
        {
            yield return null;

            Debug.Log("Started nav mesh generation ...");

            // skip loading models and textures
            Config.SetString("loadStaticRenderModels", false.ToString());
            Config.SetString("dontLoadTextures", true.ToString());

            // open startup scene
            EditorSceneManager.OpenScene(EditorBuildSettings.scenes[0].path, OpenSceneMode.Single);
            yield return null;
            yield return null;

            // load game data

            Loader.StartLoading();

            while (Loader.IsLoading)
                yield return null;

            if (!Loader.HasLoaded)
                throw new Exception("Loader did not finish successfully");

            // use AssetExporter to load game collision

            var assetExporter = new AssetExporter
            {
                ExportPrefabs = false,
                ExportRenderMeshes = false,
                ExportTextures = false,
                ExportMaterials = false,
                ExportCollisionMeshes = false,
                IsSilentMode = true
            };

            assetExporter.Export(AssetExporter.ExportType.FromGameFiles);

            while (assetExporter.IsRunning)
            {
                yield return null;
            }

            if (!assetExporter.FinishedSuccessfully)
                throw new Exception("Asset exporter did not finish successfully");

            yield return null;

            // we need to create collision for water
            Cell.Singleton.Water.CreateCollisionObjects = true;
            Cell.Singleton.Water.Initialize(Cell.Singleton.WorldSize * Vector2.one);

            yield return null;

            // if specified, disable objects out of given radius
            if (CmdLineUtils.TryGetUshortArgument("navMeshGenerationObjectsIncludeRadius", out ushort objectsRadius))
                DisableObjectsOutOfRadius(objectsRadius);

            // now fire up NavMeshGenerator

            var navMeshGenerator = new NavMeshGenerator(null);
            navMeshGenerator.LogProgressPeriodically = true;

            var navMeshBuildSettings = NavMesh.GetSettingsByID(0);
            navMeshBuildSettings.maxJobWorkers = CmdLineUtils.TryGetUshortArgument("navMeshGenerationMaxJobWorkers", out ushort maxJobWorkers) ? maxJobWorkers : (uint)2;

            // Unity's default voxel size (agentRadius/3, ~0.17) over GTA's 6km x 6km map is what produces a
            // ~141 MB nav mesh - far too large for a VRChat world. Coarser voxels shrink it dramatically and
            // are still plenty for NPCs walking streets. All of it is overridable from the command line.
            navMeshBuildSettings.agentRadius = GetFloatArgOrDefault("navMeshAgentRadius", 0.5f);
            navMeshBuildSettings.agentHeight = GetFloatArgOrDefault("navMeshAgentHeight", 2f);
            navMeshBuildSettings.agentSlope = GetFloatArgOrDefault("navMeshAgentSlope", 45f);
            navMeshBuildSettings.agentClimb = GetFloatArgOrDefault("navMeshAgentClimb", 0.75f);
            navMeshBuildSettings.voxelSize = GetFloatArgOrDefault("navMeshVoxelSize", 0.35f);
            navMeshBuildSettings.tileSize = CmdLineUtils.GetUshortArgumentOrDefault("navMeshTileSize", 256);
            // culls tiny disconnected islands, which are pure size cost and useless to NPCs
            navMeshBuildSettings.minRegionArea = GetFloatArgOrDefault("navMeshMinRegionArea", 4f);

            Debug.Log($"Nav mesh settings: voxelSize {navMeshBuildSettings.voxelSize}, " +
                $"tileSize {navMeshBuildSettings.tileSize}, agentRadius {navMeshBuildSettings.agentRadius}, " +
                $"agentHeight {navMeshBuildSettings.agentHeight}, agentClimb {navMeshBuildSettings.agentClimb}, " +
                $"minRegionArea {navMeshBuildSettings.minRegionArea}, maxJobWorkers {navMeshBuildSettings.maxJobWorkers}");

            navMeshGenerator.Generate(navMeshBuildSettings, true);

            while (navMeshGenerator.IsRunning)
            {
                yield return null;
            }

            if (!navMeshGenerator.FinishedSuccessfully)
                throw new Exception("Nav mesh generator did not finish successfully");

            yield return null;

            string outputPath = CmdLineUtils.GetStringArgumentOrDefault(
                "navMeshOutputPath", "Assets/GeneratedNavMeshFromCommandLine.asset");

            navMeshGenerator.SaveNavMesh(outputPath);

            yield return null;

            // the resulting size is the whole point of this exercise, so report it
            string fullPath = System.IO.Path.Combine(
                System.IO.Directory.GetParent(Application.dataPath).FullName, outputPath);
            if (System.IO.File.Exists(fullPath))
            {
                long bytes = new System.IO.FileInfo(fullPath).Length;
                Debug.Log($"Nav mesh saved to {outputPath} - size {bytes / (1024f * 1024f):F2} MB");
            }

            Debug.Log("Finished generation of nav mesh from command line");

            yield return null;
            yield return null;

            EditorApplication.Exit(0);
        }

        private static float GetFloatArgOrDefault(string argName, float defaultValue)
        {
            if (!CmdLineUtils.TryGetStringArgument(argName, out string value))
                return defaultValue;

            if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                return parsed;

            Debug.LogWarning($"Could not parse '{value}' for {argName}, using default {defaultValue}");
            return defaultValue;
        }

        static void OnFinishWithError(Exception exception)
        {
            EditorApplication.Exit(1);
        }

        static void DisableObjectsOutOfRadius(ushort radius)
        {
            Cell.Singleton.gameObject.GetFirstLevelChildrenSingleComponent<MapObject>().ForEach(mapObject =>
            {
                if (mapObject.transform.Distance(Vector3.zero) > radius)
                    mapObject.gameObject.SetActive(false);
            });
        }
    }
}
