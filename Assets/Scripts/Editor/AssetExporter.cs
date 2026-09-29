using SanAndreasUnity.Behaviours;
using SanAndreasUnity.Behaviours.World;
using UGameCore.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    public class AssetExporter
    {
        private const string DefaultFolderName = "ExportedAssets";
        private string m_selectedFolder = "Assets/" + DefaultFolderName;
        public string SelectedFolder => m_selectedFolder;

        string ModelsPath => m_selectedFolder + "/Models";
        string CollisionModelsPath => m_selectedFolder + "/CollisionModels";
        string MaterialsPath => m_selectedFolder + "/Materials";
        string TexturesPath => m_selectedFolder + "/Textures";
        string PrefabsPath => m_selectedFolder + "/Prefabs";
        string AnimationsPath => m_selectedFolder + "/Animations";
        string AvatarsPath => m_selectedFolder + "/Avatars";
        string AudioPath => m_selectedFolder + "/Audio";
        string MapObjectPrefabsPath => m_selectedFolder + "/Prefabs/MapObjects";

        /// <summary>
        /// Turn repeated map objects into instances of one shared prefab per model.
        ///
        /// Without this, every crate/lamppost/wall in the city is serialized in full inside the world
        /// prefab - its GameObject, Transform, and every component - even though thousands of them are
        /// identical apart from position. As instances, each occurrence stores only a prefab reference
        /// plus its transform.
        /// </summary>
        public bool DeduplicateMapObjects { get => m_deduplicateMapObjects; set => m_deduplicateMapObjects = value; }
        private bool m_deduplicateMapObjects = true;
        string AnimatorControllersPath => m_selectedFolder + "/AnimatorControllers";

        /// <summary> Float parameter that drives the ped locomotion blend tree. Set this from Udon. </summary>
        public const string SpeedParameterName = "Speed";

        // driven by the ped behaviours; the state machine for them is built in AddPedReactionStates
        public const string DeadParameterName = "Dead";
        public const string JackedParameterName = "Jacked";
        public const string ReseatParameterName = "Reseat";

        private bool m_isSilentMode = false;
        public bool IsSilentMode { get => m_isSilentMode; set => m_isSilentMode = value; }

        private CoroutineInfo m_coroutineInfo;
        public bool FinishedSuccessfully { get; private set; } = false;

        public bool IsRunning => CoroutineManager.IsRunning(m_coroutineInfo);

        private int m_numNewlyExportedAssets = 0;
        private int m_numAlreadyExportedAssets = 0;

        public enum ExportType
        {
            None = 0,
            FromSelection,
            FromLoadedWorld,
            FromGameFiles,
            VehiclesFromGameFiles,
            PedsFromGameFiles,
            WeaponsFromGameFiles,
            AnimationsFromGameFiles,
            AudioFromGameFiles,
        }

        private ExportType m_exportType;

        private bool m_exportFromSelection => m_exportType == ExportType.FromSelection;
        private bool IsExportingFromLoadedWorld => m_exportType == ExportType.FromLoadedWorld;
        private bool IsExportingFromGameFiles => m_exportType == ExportType.FromGameFiles;
        private bool IsExportingVehicles => m_exportType == ExportType.VehiclesFromGameFiles;
        private bool IsExportingPeds => m_exportType == ExportType.PedsFromGameFiles;
        private bool IsExportingWeapons => m_exportType == ExportType.WeaponsFromGameFiles;
        private bool IsExportingAudio => m_exportType == ExportType.AudioFromGameFiles;

        /// <summary> Gameplay sound effects. In-memory PCM, so these save cleanly as assets. </summary>
        public bool ExportSfxAudio { get => m_exportSfxAudio; set => m_exportSfxAudio = value; }
        private bool m_exportSfxAudio = true;

        /// <summary>
        /// Radio/music streams. These are long Ogg Vorbis tracks that have to be fully decoded to
        /// uncompressed PCM to become assets, so they are enormous - easily multiple GB across the game.
        /// </summary>
        public bool ExportStreamAudio { get => m_exportStreamAudio; set => m_exportStreamAudio = value; }
        private bool m_exportStreamAudio = false;
        private bool IsExportingModelsFromGameFiles =>
            this.IsExportingVehicles || this.IsExportingPeds || this.IsExportingWeapons;
        private bool IsExportingAnimations => m_exportType == ExportType.AnimationsFromGameFiles;

        /// <summary>
        /// The importer creates legacy clips (for the built-in <see cref="UnityEngine.Animation"/> component).
        /// Mecanim/Animator - which is what VRChat uses - refuses legacy clips, so clear the flag on export.
        /// </summary>
        public bool MakeAnimationsMecanimCompatible { get => m_makeAnimationsMecanimCompatible; set => m_makeAnimationsMecanimCompatible = value; }
        private bool m_makeAnimationsMecanimCompatible = true;

        /// <summary> Build an Avatar + Animator for exported peds, so animations can actually play on them. </summary>
        public bool CreateAvatarAndAnimator { get => m_createAvatarAndAnimator; set => m_createAvatarAndAnimator = value; }
        private bool m_createAvatarAndAnimator = true;

        private readonly Dictionary<string, RuntimeAnimatorController> m_locomotionControllers =
            new Dictionary<string, RuntimeAnimatorController>();
        private bool m_warnedAboutMissingClips = false;

        public bool ExportRenderMeshes { get => m_exportRenderMeshes; set => m_exportRenderMeshes = value; }
        public bool ExportMaterials { get => m_exportMaterials; set => m_exportMaterials = value; }
        public bool ExportTextures { get => m_exportTextures; set => m_exportTextures = value; }
        public bool ExportCollisionMeshes { get => m_exportCollisionMeshes; set => m_exportCollisionMeshes = value; }
        public bool ExportPrefabs { get => m_exportPrefabs; set => m_exportPrefabs = value; }
        
        private bool m_exportRenderMeshes = true;
        private bool m_exportMaterials = true;
        private bool m_exportTextures = true;
        private bool m_exportCollisionMeshes = true;
        private bool m_exportPrefabs = false;

        private struct SaveAssetAction
        {
            public UnityEngine.Object asset;
            public string path;
            public Action<UnityEngine.Object> assignAsset;
        }

        private readonly List<SaveAssetAction> m_saveAssetActions = new List<SaveAssetAction>();



        void ChangeFolder()
        {
            string newFolder = EditorUtility.SaveFolderPanel(
                "Select folder where to export files",
                m_selectedFolder,
                "");
            if (string.IsNullOrWhiteSpace(newFolder))
            {
                return;
            }

            newFolder = FileUtil.GetProjectRelativePath(newFolder);
            if (string.IsNullOrWhiteSpace(newFolder))
            {
                DisplayMessage("Folder must be inside project.");
            }

            m_selectedFolder = newFolder;
        }

        public void Export(ExportType exportType)
        {
            if (this.IsRunning)
                return;

            this.FinishedSuccessfully = false;
            m_exportType = exportType;

            IEnumerator coroutine;
            if (this.IsExportingAudio)
                coroutine = this.ExportAudioCoroutine();
            else if (this.IsExportingAnimations)
                coroutine = this.ExportAnimationsCoroutine();
            else if (this.IsExportingModelsFromGameFiles)
                coroutine = this.ExportModelsCoroutine();
            else
                coroutine = this.ExportCoroutine();

            m_coroutineInfo = CoroutineManager.Start(coroutine, this.Cleanup, ex => this.Cleanup());
        }

        void Cleanup()
        {
            EditorUtility.ClearProgressBar();
        }

        IEnumerator ExportCoroutine()
        {
            yield return null;

            m_numNewlyExportedAssets = 0;
            m_numAlreadyExportedAssets = 0;

            if (string.IsNullOrWhiteSpace(m_selectedFolder))
            {
                DisplayMessage("Select a folder first.");
                yield break;
            }

            if (m_exportFromSelection)
            {
                if (Selection.transforms.Length == 0)
                {
                    DisplayMessage("No object selected.");
                    yield break;
                }
            }

            var cell = Cell.Instance;
            if (null == cell && this.IsExportingFromLoadedWorld)
            {
                DisplayMessage($"{nameof(Cell)} script not found in scene. Make sure that you started the game with the correct scene.");
                yield break;
            }

            if (this.IsExportingFromGameFiles)
            {
                if (!F.IsAppInEditMode)
                {
                    DisplayMessage("This type of export can only run in edit-mode.");
                    yield break;
                }

                Importing.LoadingThread.Singleton.BackgroundJobRunner.EnsureBackgroundThreadStarted();
                
                if (!Loader.HasLoaded)
                {
                    DisplayMessage("Game data must be loaded first.");
                    yield break;
                }

                cell = Cell.Instance;
                if (cell != null)
                {
                    if (!AskDialog(true, $"Found existing {nameof(Cell)} script in scene. Would you like to use this game object for creating world objects ?\r\n\r\nIf it is part of a prefab, the prefab will be unpacked.", "Ok", "Cancel"))
                        yield break;

                    if (PrefabUtility.IsPartOfPrefabInstance(cell.gameObject))
                    {
                        PrefabUtility.UnpackPrefabInstance(PrefabUtility.GetNearestPrefabInstanceRoot(cell.gameObject), PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
                        EditorUtilityEx.MarkActiveSceneAsDirty();
                    }
                }

                if (null == cell)
                {
                    GameObject worldPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(EditorCore.PrefabsPath + "/World.prefab");

                    GameObject worldObject = (GameObject)PrefabUtility.InstantiatePrefab(worldPrefab);
                    EditorUtilityEx.MarkActiveSceneAsDirty();

                    cell = Cell.Instance;
                    if (null == cell)
                        throw new Exception("Failed to create world object");
                }
            }

            EditorUtility.DisplayProgressBar("", "Gathering info...", 0f);

            Transform[] objectsToExport = Array.Empty<Transform>();

            if (m_exportFromSelection)
                objectsToExport = Selection.transforms
                    .Where(_ => _.gameObject.activeInHierarchy)
                    .Where(_ => _.GetComponent<MapObject>() != null || _.GetComponent<Water>() != null)
                    .ToArray();
            else if (this.IsExportingFromLoadedWorld)
                objectsToExport = cell.transform
                    .GetFirstLevelChildren()
                    .AppendIf(cell.Water != null, cell.Water.GetTransformOrNull())
                    .Where(_ => _.gameObject.activeInHierarchy)
                    .ToArray();
            else if (this.IsExportingFromGameFiles)
            {
                cell.ignoreLodObjectsWhenInitializing = true;

                EditorUtilityEx.MarkActiveSceneAsDirty();

                EditorUtility.DisplayProgressBar("", "Initializing world...", 0f);
                cell.InitAll();

                yield return null;

                objectsToExport = cell.gameObject.GetFirstLevelChildrenSingleComponent<MapObject>()
                    .Select(_ => _.transform)
                    .AppendIf(cell.Water != null, cell.Water.GetTransformOrNull())
                    .ToArray();
            }

            EditorUtility.ClearProgressBar();

            if (0 == objectsToExport.Length)
            {
                DisplayMessage("No suitable objects to export.");
                yield break;
            }

            if (!AskDialog(
                true,
                $"Found {objectsToExport.Length} objects to export.\r\nProceed ?",
                "Ok",
                "Cancel"))
            {
                yield break;
            }

            if (EditorApplication.isPlaying)
                EditorApplication.isPaused = true;

            var stopwatch = Stopwatch.StartNew();

            EditorUtility.DisplayProgressBar("", "Creating folders...", 0f);

            this.CreateFolders();

            if (this.IsExportingFromGameFiles)
            {
                EditorUtility.DisplayProgressBar("", "Preparing...", 0f);

                // disable automatic light baking, otherwise Editor will be very slow after assets are loaded and will fill the whole memory
                Lightmapping.giWorkflowMode = Lightmapping.GIWorkflowMode.OnDemand;
                
                // disable GI - it makes Editor very slow after the assets are loaded
                Lightmapping.bakedGI = false;
                Lightmapping.realtimeGI = false;

                DayTimeManager.Singleton.SetTime(13, 0, true); // to make TOBJ objects visible

                EditorUtility.ClearProgressBar();
                yield return null;
                yield return null; // let the Editor update after changing day-time, who knows what all is changed

                Importing.LoadingThread.Singleton.maxTimePerFrameMs = 500;
            }

            EditorUtilityEx.MarkActiveSceneAsDirty();

            int nextIndexToTriggerLoad = 0;
            var isCanceledRef = new Ref<bool>();
            var etaMeasurer = new ETAMeasurer(0f);
            
            for (int i = 0; i < objectsToExport.Length; i++)
            {
                Transform currentObject = objectsToExport[i];

                if (this.IsExportingFromGameFiles && nextIndexToTriggerLoad == i)
                {
                    // loading of objects is done asyncly, so first we need to trigger load, then wait for it to complete

                    int nextNextIndex = Mathf.Min(i + 100, objectsToExport.Length);

                    for (int triggerLoadIndex = i; triggerLoadIndex < nextNextIndex; triggerLoadIndex++)
                    {
                        Transform triggerLoadObject = objectsToExport[triggerLoadIndex];

                        if (EditorUtils.DisplayPausableProgressBar("", $"Triggering async load ({triggerLoadIndex + 1}/{objectsToExport.Length}), ETA {etaMeasurer.ETA} ... {triggerLoadObject.name}", i / (float)objectsToExport.Length))
                            yield break;

                        var mapObject = triggerLoadObject.GetComponent<MapObject>();
                        if (mapObject != null)
                        {
                            mapObject.UnShow();
                            mapObject.Show(1f);
                        }
                    }

                    nextIndexToTriggerLoad = nextNextIndex;

                    // wait for completion of jobs

                    foreach (var item in WaitForCompletionOfLoadingJobs(
                        $"\r\nETA {etaMeasurer.ETA}, objects processed {i}/{objectsToExport.Length}",
                        i / (float)objectsToExport.Length,
                        nextNextIndex / (float)objectsToExport.Length,
                        4,
                        isCanceledRef))
                        yield return item;

                    if (isCanceledRef.value)
                        yield break;

                }

                if (EditorUtils.DisplayPausableProgressBar("", $"Creating assets ({i + 1}/{objectsToExport.Length}), ETA {etaMeasurer.ETA} ... {currentObject.name}", i / (float)objectsToExport.Length))
                    yield break;

                currentObject.gameObject.SetActive(true); // enable it so it can be seen when Editor un-freezes
                
                this.ExportAssets(currentObject.gameObject);

                if ((i % 200 == 0) || i == objectsToExport.Length - 1)
                {
                    this.ProcessSavedAssetActions();
                    etaMeasurer.UpdateETA(i / (float)objectsToExport.Length);
                }

            }

            if (m_exportPrefabs)
            {
                EditorUtility.DisplayProgressBar("", "Creating prefabs...", 1f);

                if (this.IsExportingFromLoadedWorld)
                    PrefabUtility.SaveAsPrefabAsset(cell.gameObject, $"{PrefabsPath}/ExportedWorld.prefab");
                else if (m_exportFromSelection)
                {
                    foreach (var obj in objectsToExport)
                    {
                        PrefabUtility.SaveAsPrefabAsset(obj.gameObject, $"{PrefabsPath}/{obj.gameObject.name}.prefab");
                    }
                }
                else if (this.IsExportingFromGameFiles)
                {
                    if (m_deduplicateMapObjects)
                        this.DeduplicateWorldObjects(objectsToExport);

                    PrefabUtility.SaveAsPrefabAsset(cell.transform.root.gameObject, $"{PrefabsPath}/ExportedWorldFromGameFiles.prefab");
                }
            }

            EditorUtility.DisplayProgressBar("", "Refreshing asset database...", 1f);
            AssetDatabase.Refresh();

            EditorUtility.ClearProgressBar();
            string displayText = $"number of newly exported asssets {m_numNewlyExportedAssets}, number of already exported assets {m_numAlreadyExportedAssets}, time elapsed {stopwatch.Elapsed}";
            UnityEngine.Debug.Log($"Exporting of assets finished, {displayText}");
            DisplayMessage($"Finished ! \r\n\r\n{displayText}");

            this.FinishedSuccessfully = true;
        }

        /// <summary>
        /// Finds previously exported map object prefabs whose meshes no longer exist.
        ///
        /// A prefab pointing at deleted meshes still loads perfectly well - it simply draws nothing - so an
        /// export that reuses one reports success and leaves an invisible city behind. That has to be
        /// checked, not assumed.
        ///
        /// They are identified here and overwritten in place by the loop below - never deleted. Deleting
        /// them turns every reference the world hierarchy still holds into a "Missing Prefab", after which
        /// each subsequent SaveAsPrefabAssetAndConnect logs an identifier-uniqueness violation with a full
        /// stack trace. That produced a ten-gigabyte log and rebuilt four hundred prefabs in five hours.
        /// SaveAsPrefabAssetAndConnect overwrites an existing path perfectly well, so there is nothing to
        /// gain by removing it first.
        /// </summary>
        private HashSet<string> FindStaleMapObjectPrefabs()
        {
            var stale = new HashSet<string>();

            if (!Directory.Exists(MapObjectPrefabsPath))
                return stale;

            string[] paths = Directory.GetFiles(MapObjectPrefabsPath, "*.prefab");
            if (paths.Length == 0)
                return stale;
            var stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < paths.Length; i++)
            {
                if (i % 500 == 0)
                {
                    EditorUtility.DisplayProgressBar("",
                        $"Checking exported map object prefabs ({i}/{paths.Length}), {stale.Count} stale...",
                        i / (float)paths.Length);
                }

                string path = paths[i].Replace(Path.DirectorySeparatorChar, '/');

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (null == prefab)
                    continue;

                if (!MapObjectPrefabIsUsable(prefab))
                    stale.Add(path);
            }

            EditorUtility.ClearProgressBar();

            UnityEngine.Debug.Log($"Map object prefabs: {stale.Count} of {paths.Length} point at meshes " +
                $"that no longer exist and will be overwritten (checked in " +
                $"{stopwatch.Elapsed.TotalSeconds:F0}s)");

            return stale;
        }

        /// <summary>
        /// Whether a previously exported map object prefab still points at meshes that exist.
        ///
        /// Checked rather than assumed, because a prefab whose meshes were deleted still loads perfectly
        /// well - it just draws nothing, which is indistinguishable from a working export until someone
        /// walks around the world and finds it missing.
        /// </summary>
        private static bool MapObjectPrefabIsUsable(GameObject prefab)
        {
            var filters = prefab.GetComponentsInChildren<MeshFilter>(true);

            if (filters.Length == 0)
                return true;   // genuinely geometry-free (collision-only markers, effects)

            foreach (var filter in filters)
            {
                if (null == filter.sharedMesh)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Exports vehicles or peds from game files. Unlike static map objects, these are normally only
        /// created at runtime and discarded, so nothing about them ever reaches the asset database.
        /// We build each model directly from its geometry (bypassing the gameplay Vehicle/Ped components,
        /// which drag in physics and networking that don't work in edit-mode), export its assets, and
        /// save a prefab.
        /// </summary>
        IEnumerator ExportModelsCoroutine()
        {
            yield return null;

            m_numNewlyExportedAssets = 0;
            m_numAlreadyExportedAssets = 0;

            if (string.IsNullOrWhiteSpace(m_selectedFolder))
            {
                DisplayMessage("Select a folder first.");
                yield break;
            }

            if (!F.IsAppInEditMode)
            {
                DisplayMessage("This type of export can only run in edit-mode.");
                yield break;
            }

            if (!Loader.HasLoaded)
            {
                DisplayMessage("Game data must be loaded first.");
                yield break;
            }

            bool isVehicles = this.IsExportingVehicles;
            bool isPeds = this.IsExportingPeds;
            string kindName = isVehicles ? "vehicles" : (isPeds ? "peds" : "weapons");

            // the exporter instance is reused by the window, so don't carry state between runs
            m_locomotionControllers.Clear();
            m_warnedAboutMissingClips = false;

            // gather definitions
            (int id, string modelName, string txdName, string animGroup, string subFolder)[] modelsToExport;

            if (isVehicles)
                // Group vehicles by their declared type. Without this everything lands in one folder and
                // the traffic system happily spawns planes and boats on city streets.
                modelsToExport = Importing.Items.Item.GetDefinitions<Importing.Items.Definitions.VehicleDef>()
                    .Select(def => (id: def.Id, modelName: def.ModelName, txdName: def.TextureDictionaryName,
                        animGroup: (string)null, subFolder: "Vehicles/" + def.VehicleType))
                    .ToArray();
            else if (isPeds)
                modelsToExport = Behaviours.Ped.SpawnablePedDefs
                    .Select(def => (id: def.Id, modelName: def.ModelName, txdName: def.TextureDictionaryName,
                        animGroup: def.AnimGroupName, subFolder: "Peds"))
                    .ToArray();
            else
                modelsToExport = Importing.Items.Item.GetDefinitions<Importing.Items.Definitions.WeaponDef>()
                    .Where(def => !string.IsNullOrWhiteSpace(def.ModelName))
                    .Select(def => (id: def.Id, modelName: def.ModelName, txdName: def.TextureDictionaryName,
                        animGroup: (string)null, subFolder: "Weapons"))
                    .ToArray();

            // the same model can be referenced by multiple definitions - only export it once
            modelsToExport = modelsToExport.DistinctBy(_ => _.modelName.ToLowerInvariant()).ToArray();

            if (0 == modelsToExport.Length)
            {
                DisplayMessage($"No {kindName} found to export.");
                yield break;
            }

            if (!AskDialog(
                true,
                $"Found {modelsToExport.Length} {kindName} to export.\r\nProceed ?",
                "Ok",
                "Cancel"))
            {
                yield break;
            }

            var stopwatch = Stopwatch.StartNew();

            EditorUtility.DisplayProgressBar("", "Creating folders...", 0f);
            this.CreateFolders();

            // per-model subfolders are created on demand, since vehicles split by type

            var etaMeasurer = new ETAMeasurer(0f);
            int numFailed = 0;

            for (int i = 0; i < modelsToExport.Length; i++)
            {
                var model = modelsToExport[i];

                if (EditorUtils.DisplayPausableProgressBar(
                    "",
                    $"Exporting {kindName} ({i + 1}/{modelsToExport.Length}), ETA {etaMeasurer.ETA} ... {model.modelName}",
                    i / (float)modelsToExport.Length))
                    yield break;

                GameObject go = null;

                try
                {
                    go = new GameObject(model.modelName);

                    var geometryParts = isVehicles
                        ? Importing.Conversion.Geometry.Load(
                            model.modelName,
                            Importing.Conversion.TextureDictionary.Load(model.txdName),
                            Importing.Conversion.TextureDictionary.Load("vehicle"),
                            Importing.Conversion.TextureDictionary.Load("misc"))
                        : Importing.Conversion.Geometry.Load(model.modelName, model.txdName);

                    var frames = geometryParts.AttachFrames(
                        go.transform,
                        isVehicles
                            ? Importing.Conversion.MaterialFlags.Vehicle
                            : Importing.Conversion.MaterialFlags.Default);

                    // A vehicle model carries a single master "wheel" mesh plus empty wheel_*_dummy
                    // positions; the game clones the master into each dummy when spawning the car.
                    // Exporting the raw frames leaves those dummies empty, so cars render with no wheels.
                    if (isVehicles)
                        // must run first: it centres the wheel master, so the clones below land correctly
                        RebaseVehicleMeshes(go);
                        AttachVehicleWheels(go);

                    // only peds have a skeleton that animations bind to - weapons and vehicles must not
                    // get the ped frame-name normalization or an avatar
                    if (isPeds)
                    {
                        NormalizePedFrameNames(frames);

                        if (m_createAvatarAndAnimator)
                            this.SetupPedAnimator(go, model.animGroup);
                    }

                    this.ExportAssets(go);
                    this.ProcessSavedAssetActions();

                    // Must run AFTER the assets are processed: by this point the renderers reference the
                    // persisted material assets. Tinting the pre-save runtime objects would be discarded,
                    // because ProcessSavedAssetActions reuses an already-exported material rather than
                    // overwriting it.
                    if (isVehicles)
                        BakeVehicleColors(go, model.modelName);

                    if (m_exportPrefabs)
                    {
                        string modelPrefabsPath = $"{PrefabsPath}/{model.subFolder}";
                        if (!Directory.Exists(modelPrefabsPath))
                            Directory.CreateDirectory(modelPrefabsPath);

                        PrefabUtility.SaveAsPrefabAsset(go, $"{modelPrefabsPath}/{model.modelName}.prefab");
                    }
                }
                catch (Exception ex)
                {
                    // a single bad model shouldn't abort the whole export
                    numFailed++;
                    UnityEngine.Debug.LogError($"Failed to export {model.modelName} (id {model.id}): {ex}");
                }
                finally
                {
                    if (go != null)
                        UnityEngine.Object.DestroyImmediate(go);
                }

                etaMeasurer.UpdateETA(i / (float)modelsToExport.Length);

                // give the Editor a chance to breathe, otherwise it freezes for the whole export
                if (i % 20 == 0)
                    yield return null;
            }

            // handling.cfg is only readable while the game data is loaded, so capture it here rather than
            // trying to recover it when the scene is assembled
            if (isVehicles)
                ExportVehicleHandling(modelsToExport.Select(_ => _.modelName).ToArray());

            EditorUtility.DisplayProgressBar("", "Refreshing asset database...", 1f);
            AssetDatabase.Refresh();

            EditorUtility.ClearProgressBar();
            string displayText = $"exported {modelsToExport.Length - numFailed}/{modelsToExport.Length} {kindName}, " +
                $"failed {numFailed}, number of newly exported assets {m_numNewlyExportedAssets}, " +
                $"number of already exported assets {m_numAlreadyExportedAssets}, time elapsed {stopwatch.Elapsed}";
            UnityEngine.Debug.Log($"Exporting of {kindName} finished, {displayText}");
            DisplayMessage($"Finished ! \r\n\r\n{displayText}");

            this.FinishedSuccessfully = true;
        }

        /// <summary>
        /// Exports every animation clip from the game's .ifp packages as an AnimationClip asset.
        /// Clips are built against a reference ped skeleton, because animation curves are bound to
        /// frame paths - all ped models share the same (normalized) skeleton naming, which is why one
        /// reference model is enough for all of them.
        /// </summary>
        IEnumerator ExportAnimationsCoroutine()
        {
            yield return null;

            m_numNewlyExportedAssets = 0;
            m_numAlreadyExportedAssets = 0;

            if (string.IsNullOrWhiteSpace(m_selectedFolder))
            {
                DisplayMessage("Select a folder first.");
                yield break;
            }

            if (!F.IsAppInEditMode)
            {
                DisplayMessage("This type of export can only run in edit-mode.");
                yield break;
            }

            if (!Loader.HasLoaded)
            {
                DisplayMessage("Game data must be loaded first.");
                yield break;
            }

            // we need a skeleton to bind the animation curves against
            var referencePedDef = Behaviours.Ped.SpawnablePedDefs.FirstOrDefault();
            if (null == referencePedDef)
            {
                DisplayMessage("No ped definitions found - can not build a reference skeleton for animations.");
                yield break;
            }

            var ifpFileNames = Importing.Archive.ArchiveManager.GetFileNamesWithExtension(".ifp")
                .Select(Path.GetFileNameWithoutExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (0 == ifpFileNames.Length)
            {
                DisplayMessage("No .ifp animation packages found.");
                yield break;
            }

            if (!AskDialog(
                true,
                $"Found {ifpFileNames.Length} animation packages to export.\r\nProceed ?",
                "Ok",
                "Cancel"))
            {
                yield break;
            }

            var stopwatch = Stopwatch.StartNew();

            EditorUtility.DisplayProgressBar("", "Creating folders...", 0f);
            this.CreateFolders();

            GameObject referenceGo = null;
            int numExportedClips = 0;
            int numFailed = 0;

            try
            {
                referenceGo = new GameObject("AnimationReferenceSkeleton");

                var referenceFrames = Importing.Conversion.Geometry
                    .Load(referencePedDef.ModelName, referencePedDef.TextureDictionaryName)
                    .AttachFrames(referenceGo.transform, Importing.Conversion.MaterialFlags.Default);

                NormalizePedFrameNames(referenceFrames);

                for (int i = 0; i < ifpFileNames.Length; i++)
                {
                    string ifpName = ifpFileNames[i];

                    if (EditorUtility.DisplayCancelableProgressBar(
                        "",
                        $"Exporting animations ({i + 1}/{ifpFileNames.Length}) ... {ifpName}",
                        i / (float)ifpFileNames.Length))
                        break;

                    string packageFolder = $"{AnimationsPath}/{ifpName}";

                    Importing.Conversion.Animation.Package package;
                    try
                    {
                        package = Importing.Conversion.Animation.LoadPackageOnly(ifpName);
                    }
                    catch (Exception ex)
                    {
                        numFailed++;
                        UnityEngine.Debug.LogError($"Failed to load animation package {ifpName}: {ex}");
                        continue;
                    }

                    if (!Directory.Exists(packageFolder))
                        Directory.CreateDirectory(packageFolder);

                    foreach (var sourceClip in package.AnimPackage.Clips)
                    {
                        try
                        {
                            var animation = Importing.Conversion.Animation.Load(
                                ifpName, sourceClip.Name, referenceFrames);

                            UnityEngine.AnimationClip clip = animation.Clip;
                            if (null == clip)
                                continue;

                            if (m_makeAnimationsMecanimCompatible)
                                clip.legacy = false;

                            string clipPath = $"{packageFolder}/{MakeFileNameSafe(sourceClip.Name)}.anim";

                            if (AssetDatabase.Contains(clip) || AssetExistsAtPath(clipPath))
                                continue;

                            AssetDatabase.CreateAsset(clip, clipPath);
                            m_numNewlyExportedAssets++;
                            numExportedClips++;
                        }
                        catch (Exception ex)
                        {
                            numFailed++;
                            UnityEngine.Debug.LogError(
                                $"Failed to export animation {ifpName}/{sourceClip.Name}: {ex}");
                        }
                    }
                }
            }
            finally
            {
                if (referenceGo != null)
                    UnityEngine.Object.DestroyImmediate(referenceGo);
            }

            EditorUtility.DisplayProgressBar("", "Refreshing asset database...", 1f);
            AssetDatabase.Refresh();

            EditorUtility.ClearProgressBar();
            string displayText = $"exported {numExportedClips} animation clips from {ifpFileNames.Length} packages, " +
                $"failed {numFailed}, already exported {m_numAlreadyExportedAssets}, time elapsed {stopwatch.Elapsed}";
            UnityEngine.Debug.Log($"Exporting of animations finished, {displayText}");
            DisplayMessage($"Finished ! \r\n\r\n{displayText}");

            this.FinishedSuccessfully = true;
        }

        /// <summary>
        /// Builds a generic Avatar from the ped's skeleton and adds an Animator that uses it. Mecanim
        /// (and therefore VRChat) can only play clips through an Animator with a valid Avatar - without
        /// this, exported peds are static meshes that no animation can drive.
        /// </summary>
        private void SetupPedAnimator(GameObject go, string animGroupName)
        {
            // the importer names the skeleton root "Root" (see PedModel), and root motion is bound to it
            Avatar avatar = AvatarBuilder.BuildGenericAvatar(go, "Root");

            if (null == avatar || !avatar.isValid)
            {
                UnityEngine.Debug.LogWarning($"Could not build a valid avatar for ped {go.name} - skipping Animator setup.");
                return;
            }

            avatar.name = go.name;

            string avatarPath = $"{AvatarsPath}/{go.name}.asset";
            var existingAvatar = AssetDatabase.LoadAssetAtPath<Avatar>(avatarPath);
            if (existingAvatar != null)
            {
                UnityEngine.Object.DestroyImmediate(avatar);
                avatar = existingAvatar;
                m_numAlreadyExportedAssets++;
            }
            else
            {
                AssetDatabase.CreateAsset(avatar, avatarPath);
                m_numNewlyExportedAssets++;
            }

            // written out explicitly rather than via GetOrAddComponent: the VRChat SDK ships its own
            // extension method of that name, which shadows UGameCore's and marks it obsolete
            var animator = go.GetComponent<Animator>();
            if (null == animator)
                animator = go.AddComponent<Animator>();

            animator.avatar = avatar;
            animator.applyRootMotion = false; // NPC movement is driven by script, not by the clip
            animator.runtimeAnimatorController = this.GetOrCreateLocomotionController(animGroupName);
        }

        /// <summary>
        /// Peds don't all share the same animations - each ped definition names an anim group (man, woman, ...)
        /// which selects a different set of clips. So we build one controller per anim group: accurate, and far
        /// smaller than one controller per ped.
        /// Returns null when the needed clips haven't been exported yet.
        /// </summary>
        string PedAnimationFolder => m_selectedFolder + "/Animations/ped";

        /// <summary>
        /// Adds the states a ped needs beyond walking: being killed, and being pulled out of a car.
        ///
        /// The behaviours were already driving "Dead", "Jacked" and "Reseat", but nothing ever created
        /// them, so every attempt logged "Parameter does not exist" - five hundred of them in one session -
        /// and the ped stayed in whatever state it happened to be in. That is why a ped thrown out of a car
        /// kept playing its fall animation after being put back: there was no transition to leave by.
        ///
        /// All three clips ship with the game; only the state machine was missing.
        /// </summary>
        private void AddPedReactionStates(AnimatorController controller, AnimatorState locomotion)
        {
            var machine = controller.layers[0].stateMachine;

            controller.AddParameter(DeadParameterName, AnimatorControllerParameterType.Bool);
            controller.AddParameter(JackedParameterName, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(ReseatParameterName, AnimatorControllerParameterType.Trigger);

            var deathClip = LoadPedClip("FALL_collapse");
            var jackedClip = LoadPedClip("CAR_LjackedLHS");

            if (deathClip != null)
            {
                var dead = machine.AddState("Dead");
                dead.motion = deathClip;

                // The clip must not loop - a ped that keeps collapsing over and over reads as a glitch
                // rather than as a corpse - so it is held on its last frame instead.
                dead.speed = 1f;

                var toDead = machine.AddAnyStateTransition(dead);
                toDead.AddCondition(AnimatorConditionMode.If, 0f, DeadParameterName);
                toDead.duration = 0.1f;
                toDead.canTransitionToSelf = false;

                // peds are pooled and reused, so there has to be a way back out of death
                var revive = dead.AddTransition(locomotion);
                revive.AddCondition(AnimatorConditionMode.IfNot, 0f, DeadParameterName);
                revive.duration = 0.25f;
            }

            if (jackedClip != null)
            {
                var jacked = machine.AddState("Jacked");
                jacked.motion = jackedClip;

                var toJacked = machine.AddAnyStateTransition(jacked);
                toJacked.AddCondition(AnimatorConditionMode.If, 0f, JackedParameterName);
                toJacked.duration = 0.05f;
                toJacked.canTransitionToSelf = false;

                // explicit return, used when the ped is put back in the car
                var back = jacked.AddTransition(locomotion);
                back.AddCondition(AnimatorConditionMode.If, 0f, ReseatParameterName);
                back.duration = 0.2f;

                // and a natural one, so a ped left on the road gets up by itself rather than lying there
                var settle = jacked.AddTransition(locomotion);
                settle.hasExitTime = true;
                settle.exitTime = 0.95f;
                settle.duration = 0.3f;
            }
        }

        /// <summary> Loads one clip from the shared ped animation group. </summary>
        private UnityEngine.AnimationClip LoadPedClip(string clipName)
        {
            return AssetDatabase.LoadAssetAtPath<UnityEngine.AnimationClip>(
                $"{PedAnimationFolder}/{clipName}.anim");
        }

        private RuntimeAnimatorController GetOrCreateLocomotionController(string animGroupName)
        {
            if (string.IsNullOrWhiteSpace(animGroupName))
                animGroupName = "default";

            animGroupName = animGroupName.ToLowerInvariant();

            if (m_locomotionControllers.TryGetValue(animGroupName, out var cached))
                return cached;

            string controllerPath = $"{AnimatorControllersPath}/{MakeFileNameSafe(animGroupName)}.controller";

            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (existing != null)
            {
                m_locomotionControllers.Add(animGroupName, existing);
                return existing;
            }

            var walkCycle = Importing.Animation.AnimationGroup.Get(
                animGroupName, Importing.Animation.AnimGroup.WalkCycle);

            if (null == walkCycle)
            {
                m_locomotionControllers.Add(animGroupName, null);
                return null;
            }

            // thresholds are the blend positions along the Speed parameter
            var motions = new[]
            {
                (clip: LoadExportedClip(walkCycle, Importing.Animation.AnimIndex.Idle), threshold: 0f),
                (clip: LoadExportedClip(walkCycle, Importing.Animation.AnimIndex.Walk), threshold: 0.5f),
                (clip: LoadExportedClip(walkCycle, Importing.Animation.AnimIndex.Run), threshold: 1f),
            }.Where(_ => _.clip != null).ToArray();

            if (0 == motions.Length)
            {
                if (!m_warnedAboutMissingClips)
                {
                    m_warnedAboutMissingClips = true;
                    UnityEngine.Debug.LogWarning(
                        "No exported animation clips found, so peds get an Avatar and Animator but no controller. " +
                        "Run \"Export animations from game files\" first, then export peds again.");
                }

                m_locomotionControllers.Add(animGroupName, null);
                return null;
            }

            var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            controller.AddParameter(SpeedParameterName, AnimatorControllerParameterType.Float);

            // a 1D blend tree means Udon only has to drive a single float to move between idle/walk/run
            AnimatorState state = controller.CreateBlendTreeInController("Locomotion", out BlendTree blendTree, 0);
            blendTree.blendType = BlendTreeType.Simple1D;
            blendTree.blendParameter = SpeedParameterName;
            blendTree.useAutomaticThresholds = false;

            foreach (var motion in motions)
                blendTree.AddChild(motion.clip, motion.threshold);

            controller.layers[0].stateMachine.defaultState = state;

            AddPedReactionStates(controller, state);

            m_numNewlyExportedAssets++;
            m_locomotionControllers.Add(animGroupName, controller);

            return controller;
        }

        private UnityEngine.AnimationClip LoadExportedClip(
            Importing.Animation.AnimationGroup animGroup, Importing.Animation.AnimIndex animIndex)
        {
            string clipName;
            try
            {
                clipName = animGroup[animIndex];
            }
            catch (Exception)
            {
                // the group may not define this index
                return null;
            }

            if (string.IsNullOrWhiteSpace(clipName))
                return null;

            string path = $"{AnimationsPath}/{animGroup.FileName}/{MakeFileNameSafe(clipName)}.anim";
            return AssetDatabase.LoadAssetAtPath<UnityEngine.AnimationClip>(path);
        }

        /// <summary>
        /// Exports the game's audio as AudioClip assets.
        ///
        /// SFX are stored as in-memory PCM already, so they convert directly. Streams (radio/music) are Ogg
        /// Vorbis and are normally played through a streaming AudioClip backed by a runtime decode callback -
        /// that holds no data and cannot be saved, so they have to be fully decoded here instead. That makes
        /// them very large, which is why they are opt-in.
        /// </summary>
        IEnumerator ExportAudioCoroutine()
        {
            yield return null;

            m_numNewlyExportedAssets = 0;
            m_numAlreadyExportedAssets = 0;

            if (!F.IsAppInEditMode)
            {
                DisplayMessage("This type of export can only run in edit-mode.");
                yield break;
            }

            if (!Loader.HasLoaded)
            {
                DisplayMessage("Game data must be loaded first.");
                yield break;
            }

            var audioFiles = Behaviours.Audio.AudioManager.AudioFiles;
            if (null == audioFiles)
            {
                DisplayMessage("Audio files are not loaded.");
                yield break;
            }

            var stopwatch = Stopwatch.StartNew();

            EditorUtility.DisplayProgressBar("", "Creating folders...", 0f);
            this.CreateFolders();

            int numExported = 0;
            int numFailed = 0;

            // ---- SFX ----
            if (m_exportSfxAudio)
            {
                var sfxFiles = audioFiles.SFXAudioFiles;

                for (int fileIndex = 0; fileIndex < sfxFiles.Length; fileIndex++)
                {
                    var sfxFile = sfxFiles[fileIndex];
                    if (null == sfxFile)
                        continue;

                    string folder = $"{AudioPath}/SFX/{MakeFileNameSafe(sfxFile.Name)}";
                    if (!Directory.Exists(folder))
                        Directory.CreateDirectory(folder);

                    if (EditorUtility.DisplayCancelableProgressBar(
                        "",
                        $"Exporting SFX ({fileIndex + 1}/{sfxFiles.Length}) ... {sfxFile.Name}",
                        fileIndex / (float)sfxFiles.Length))
                        break;

                    for (uint bank = 0; bank < sfxFile.NumBanks; bank++)
                    {
                        if (!sfxFile.IsBankAvailable(bank))
                            continue;

                        int numClips = sfxFile.GetNumAudioClipsFromBank(bank);

                        for (uint clipIndex = 0; clipIndex < numClips; clipIndex++)
                        {
                            if (!sfxFile.IsAudioClipAvailableFromBank(bank, clipIndex))
                                continue;

                            string path = $"{folder}/{bank}_{clipIndex}.asset";
                            if (AssetExistsAtPath(path))
                                continue;

                            AudioClip clip = Behaviours.Audio.AudioManager.CreateAudioClipFromSfx(
                                sfxFile.Name, (int)bank, (int)clipIndex);

                            if (null == clip)
                            {
                                numFailed++;
                                continue;
                            }

                            AssetDatabase.CreateAsset(clip, path);
                            m_numNewlyExportedAssets++;
                            numExported++;
                        }
                    }

                    yield return null;
                }
            }

            // ---- streams (radio / music) ----
            if (m_exportStreamAudio)
            {
                var streamFiles = audioFiles.StreamsAudioFiles;

                for (int fileIndex = 0; fileIndex < streamFiles.Length; fileIndex++)
                {
                    var streamFile = streamFiles[fileIndex];
                    if (null == streamFile)
                        continue;

                    string folder = $"{AudioPath}/Streams/{MakeFileNameSafe(streamFile.Name)}";
                    if (!Directory.Exists(folder))
                        Directory.CreateDirectory(folder);

                    if (EditorUtility.DisplayCancelableProgressBar(
                        "",
                        $"Exporting streams ({fileIndex + 1}/{streamFiles.Length}) ... {streamFile.Name}",
                        fileIndex / (float)streamFiles.Length))
                        break;

                    for (uint bank = 0; bank < streamFile.NumBanks; bank++)
                    {
                        if (!streamFile.IsBankAvailable(bank))
                            continue;

                        string path = $"{folder}/{bank}.asset";
                        if (AssetExistsAtPath(path))
                            continue;

                        AudioClip clip = DecodeStreamToClip(
                            audioFiles, streamFile.Name, bank, $"{streamFile.Name}_{bank}");

                        if (null == clip)
                        {
                            numFailed++;
                            continue;
                        }

                        AssetDatabase.CreateAsset(clip, path);
                        m_numNewlyExportedAssets++;
                        numExported++;
                    }

                    yield return null;
                }
            }

            EditorUtility.DisplayProgressBar("", "Refreshing asset database...", 1f);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.ClearProgressBar();

            string displayText = $"exported {numExported} audio clips, failed {numFailed}, " +
                $"already exported {m_numAlreadyExportedAssets}, time elapsed {stopwatch.Elapsed}";
            UnityEngine.Debug.Log($"Exporting of audio finished, {displayText}");
            DisplayMessage($"Finished ! \r\n\r\n{displayText}");

            this.FinishedSuccessfully = true;
        }

        /// <summary>
        /// Fully decodes an Ogg Vorbis stream into a non-streaming AudioClip. The runtime uses a streaming
        /// clip with a decode callback, which holds no sample data and so cannot be saved as an asset.
        /// </summary>
        private static AudioClip DecodeStreamToClip(
            GTAAudioSharp.GTAAudioFiles audioFiles, string fileName, uint bankIndex, string clipName)
        {
            try
            {
                Stream stream = audioFiles.OpenStreamsAudioStreamByName(fileName, bankIndex);
                if (null == stream)
                    return null;

                using (var reader = new NVorbis.VorbisReader(stream, true))
                {
                    int lengthSamples = (int)reader.TotalSamples;
                    int channels = reader.Channels;

                    if (lengthSamples <= 0 || channels <= 0)
                        return null;

                    var data = new float[lengthSamples * channels];
                    reader.ReadSamples(data, 0, data.Length);

                    var clip = AudioClip.Create(clipName, lengthSamples, channels, reader.SampleRate, false);
                    clip.SetData(data, 0);
                    return clip;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"Failed to decode audio stream {fileName} bank {bankIndex}: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Collapses repeated map objects into instances of a shared prefab per model.
        ///
        /// The first occurrence of each model becomes the prefab asset (and is connected to it); every later
        /// occurrence is replaced by an instance carrying only its transform. This is what stops thousands of
        /// identical crates each serializing their full component set into the world prefab.
        /// </summary>
        private void DeduplicateWorldObjects(Transform[] objects)
        {
            if (!Directory.Exists(MapObjectPrefabsPath))
                Directory.CreateDirectory(MapObjectPrefabsPath);

            HashSet<string> stalePrefabs = FindStaleMapObjectPrefabs();
            int numStaleRebuilt = 0;

            var prefabByModel = new Dictionary<string, GameObject>();
            int numPrefabsCreated = 0;
            int numInstanced = 0;
            int numFailed = 0;

            var stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < objects.Length; i++)
            {
                Transform obj = objects[i];

                // entries go null as we destroy the originals we've replaced
                if (null == obj)
                    continue;

                if (i % 500 == 0)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(
                        "",
                        $"Deduplicating map objects ({i}/{objects.Length}), {numPrefabsCreated} prefabs, {numInstanced} instances...",
                        i / (float)objects.Length))
                        break;
                }

                string modelName = obj.gameObject.name;

                try
                {
                    if (!prefabByModel.TryGetValue(modelName, out GameObject prefab))
                    {
                        string path = $"{MapObjectPrefabsPath}/{MakeFileNameSafe(modelName)}.prefab";

                        // A prefab whose meshes no longer resolve is not reused - it is written over by
                        // the branch below, which replaces it in place.
                        var existing = stalePrefabs.Contains(path)
                            ? null
                            : AssetDatabase.LoadAssetAtPath<GameObject>(path);

                        if (existing == null && stalePrefabs.Contains(path))
                            numStaleRebuilt++;

                        if (existing != null)
                        {
                            prefabByModel[modelName] = existing;
                        }
                        else
                        {
                            // the first occurrence becomes the shared prefab, and stays in the world as a
                            // connected instance of it - so it needs no further work
                            prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(
                                obj.gameObject, path, InteractionMode.AutomatedAction);

                            prefabByModel[modelName] = prefab;
                            numPrefabsCreated++;
                            continue;
                        }

                        prefab = prefabByModel[modelName];
                    }

                    if (null == prefab)
                        continue;

                    // replace this occurrence with a lightweight instance of the shared prefab
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, obj.parent);
                    instance.transform.SetPositionAndRotation(obj.position, obj.rotation);
                    instance.transform.localScale = obj.localScale;
                    instance.name = modelName;
                    instance.SetActive(obj.gameObject.activeSelf);

                    UnityEngine.Object.DestroyImmediate(obj.gameObject);
                    objects[i] = null;

                    numInstanced++;
                }
                catch (Exception ex)
                {
                    numFailed++;
                    UnityEngine.Debug.LogError($"Failed to deduplicate map object {modelName}: {ex}");
                }
            }

            EditorUtility.ClearProgressBar();

            UnityEngine.Debug.Log(
                $"Map object deduplication finished: {numPrefabsCreated} unique prefabs created, " +
                $"{numStaleRebuilt} stale prefabs rebuilt (their meshes had been removed), " +
                $"{numInstanced} objects replaced with instances, {numFailed} failed, " +
                $"time elapsed {stopwatch.Elapsed}");
        }

        /// <summary>
        /// Writes a vehicle's paint job directly onto its material assets.
        ///
        /// The game keeps a palette index (_CarColorIndex) per material and supplies the actual colour at
        /// runtime through a MaterialPropertyBlock. Udon has no MaterialPropertyBlock, so that indirection
        /// cannot survive - the colour has to be resolved here, at export time, while the car colour tables
        /// from the game data are still loaded.
        ///
        /// Materials are written per model slot, so tinting one vehicle cannot bleed into another.
        /// </summary>
        private void BakeVehicleColors(GameObject go, string modelName)
        {
            var defaults = Importing.Vehicles.CarColors.GetCarDefaults(modelName);
            if (null == defaults || defaults.Count == 0)
                return;

            // pick one of the model's legitimate factory colour sets
            Color32[] paint = Importing.Vehicles.CarColors.FromIndices(
                defaults[UnityEngine.Random.Range(0, defaults.Count)]);

            if (null == paint || paint.Length == 0)
                return;

            // index 0 is untinted; 1-4 are the paint job; 5-8 are lights, left white here
            var white = new Color32(255, 255, 255, 255);
            var palette = new Color32[]
            {
                white,
                paint.Length > 0 ? paint[0] : white,
                paint.Length > 1 ? paint[1] : white,
                paint.Length > 2 ? paint[2] : white,
                paint.Length > 3 ? paint[3] : white,
                white, white, white, white,
            };

            int colorPropertyId = Shader.PropertyToID("_CarColor");

            foreach (var renderer in go.GetComponentsInChildren<MeshRenderer>())
            {
                var materials = renderer.sharedMaterials;

                for (int i = 0; i < materials.Length; i++)
                {
                    Material material = materials[i];
                    if (null == material || !material.HasProperty(Importing.Conversion.Geometry.CarColorIndexId))
                        continue;

                    int colorIndex = material.GetInt(Importing.Conversion.Geometry.CarColorIndexId);
                    if (colorIndex < 0 || colorIndex >= palette.Length)
                        continue;

                    if (material.HasProperty(colorPropertyId))
                    {
                        material.SetColor(colorPropertyId, palette[colorIndex]);
                        // the material is a persisted asset by now, so the change needs flushing to disk
                        EditorUtility.SetDirty(material);
                    }
                }
            }
        }

        /// <summary>
        /// Captures each vehicle's handling.cfg entry into a data asset.
        ///
        /// Written as parallel primitive arrays rather than a list of objects, because the scene builder
        /// feeds these straight onto Udon behaviours and Udon cannot see custom types.
        /// </summary>
        /// <summary>
        /// Rewrites VehicleHandling.asset from the currently exported vehicle prefabs, without touching the
        /// models themselves.
        ///
        /// Handling and animation-group data come from the loaded game files, not from the meshes, so a
        /// change to what we record about a vehicle does not justify re-exporting every vehicle model.
        /// </summary>
        public void ExportVehicleDataOnly()
        {
            string folder = $"{PrefabsPath}/Vehicles";

            var modelNames = new List<string>();

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                modelNames.Add(System.IO.Path.GetFileNameWithoutExtension(path));
            }

            if (modelNames.Count == 0)
            {
                throw new Exception(
                    $"No exported vehicle prefabs found under '{folder}'. Refusing to overwrite " +
                    "VehicleHandling.asset with an empty one - check the export folder.");
            }

            UnityEngine.Debug.Log($"Refreshing vehicle data for {modelNames.Count} exported vehicles");

            ExportVehicleHandling(modelNames.ToArray());

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private void ExportVehicleHandling(string[] modelNames)
        {
            var names = new List<string>();
            var mass = new List<float>();
            var maxSpeed = new List<float>();
            var engineAccel = new List<float>();
            var brakeDecel = new List<float>();
            var steeringLock = new List<float>();
            var traction = new List<float>();
            var drag = new List<float>();
            var animGroups = new List<string>();
            var vehicleTypes = new List<string>();
            var compRules = new List<int>();

            var defs = Importing.Items.Item.GetDefinitions<Importing.Items.Definitions.VehicleDef>().ToArray();

            foreach (string modelName in modelNames)
            {
                var def = defs.FirstOrDefault(d =>
                    string.Equals(d.ModelName, modelName, StringComparison.OrdinalIgnoreCase));

                if (null == def || string.IsNullOrWhiteSpace(def.HandlingName))
                    continue;

                Importing.Vehicles.Handling.Car handling = null;
                try
                {
                    handling = Importing.Vehicles.Handling.Get<Importing.Vehicles.Handling.Car>(def.HandlingName);
                }
                catch (Exception)
                {
                    // some vehicles reference handling entries that don't resolve
                }

                if (null == handling)
                    continue;

                names.Add(modelName);
                mass.Add(handling.Mass);
                // stored raw; converted to m/s when applied, so the units can be corrected without
                // re-running the whole vehicle export
                maxSpeed.Add(handling.TransmissionMaxVel);
                engineAccel.Add(handling.TransmissionEngineAccel);
                brakeDecel.Add(handling.BrakeDecel);
                steeringLock.Add(handling.SteeringLock);
                traction.Add(handling.TractionMult);
                drag.Add(handling.Drag);

                // selects which in-vehicle animation set a ped uses for this vehicle; parsed by the
                // importer but never consumed anywhere, so it has to be carried out explicitly
                animGroups.Add(def.AnimsName ?? string.Empty);
                vehicleTypes.Add(def.VehicleType.ToString());

                // which optional parts this model may wear, chosen per spawned vehicle rather than baked
                compRules.Add(def.CompRules.Value);
            }

            var asset = ScriptableObject.CreateInstance<Export.GtaVehicleHandlingData>();
            asset.modelNames = names.ToArray();
            asset.mass = mass.ToArray();
            asset.maxSpeed = maxSpeed.ToArray();
            asset.engineAccel = engineAccel.ToArray();
            asset.brakeDecel = brakeDecel.ToArray();
            asset.steeringLock = steeringLock.ToArray();
            asset.traction = traction.ToArray();
            asset.drag = drag.ToArray();
            asset.animGroups = animGroups.ToArray();
            asset.vehicleTypes = vehicleTypes.ToArray();
            asset.compRules = compRules.ToArray();

            string path = $"{m_selectedFolder}/VehicleHandling.asset";

            if (AssetExistsAtPath(path))
                AssetDatabase.DeleteAsset(path);

            AssetDatabase.CreateAsset(asset, path);
            m_numNewlyExportedAssets++;

            UnityEngine.Debug.Log($"Vehicle handling exported: {names.Count} entries to {path}, " +
                $"anim groups: {string.Join(", ", animGroups.Distinct().OrderBy(_ => _))}");
        }

        /// <summary>
        /// Clones the model's master wheel mesh into each wheel position.
        ///
        /// GTA vehicle models store one "wheel" mesh and a set of empty "wheel_*_dummy" frames marking
        /// where wheels belong. The game clones the master into each dummy at spawn time, so a straight
        /// export of the frames produces a car with no wheels on it.
        /// </summary>
        /// <summary>
        /// Moves vehicle part meshes into their own frame's space.
        ///
        /// GTA vehicle geometry is stored in model space - a door's vertices already sit where the door
        /// belongs on the car - but each part hangs under a *_dummy frame that carries that same offset. The
        /// offset is therefore applied twice and every panel ends up displaced by its own position, which is
        /// what makes exported cars look like they have exploded.
        ///
        /// Rebasing the vertices leaves the hierarchy intact, so door frames and wheel dummies still mean
        /// what they should. It runs before the wheels are cloned, so the master wheel is already centred by
        /// the time copies are placed into the dummies - otherwise every copy carries the master's corner
        /// offset and the wheels scatter.
        /// </summary>
        /// <summary> Set by -logRebaseDecisions:1 to dump the per-part convention test. </summary>
        private static readonly bool s_logRebaseDecisions =
            System.Environment.GetCommandLineArgs().Any(a => a == "-logRebaseDecisions:1");

        private void RebaseVehicleMeshes(GameObject go)
        {
            // Decide the convention once for the whole vehicle, then apply it to every part.
            //
            // GTA stores a model's vertices either in model space - a door's mesh already sits where the
            // door belongs, so the frame offset would be applied twice - or already frame-local. Which of
            // the two is a property of how the model was authored, so it is the same for every part of a
            // given vehicle.
            //
            // Deciding per part, as this used to, breaks on any panel whose mesh is legitimately off-centre
            // within its own frame. A bonnet hinges at its back edge and extends forward, so its centre sits
            // well away from its origin; the per-part test read that as model space and moved the bonnet
            // backwards onto the windscreen, which is what left cars looking like their hood was missing.
            // Every other part of the same car voted the other way, and by a much wider margin.
            var done = new HashSet<Mesh>();
            var candidates = new List<(MeshFilter filter, Mesh mesh, Vector3 offset)>();

            int votesModelSpace = 0;
            int votesFrameLocal = 0;

            foreach (var meshFilter in go.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = meshFilter.sharedMesh;
                if (null == mesh || !done.Add(mesh))
                    continue;

                Vector3 offset = go.transform.InverseTransformPoint(meshFilter.transform.position);

                // A part sitting at the origin cannot distinguish the two conventions, and rebasing it by
                // a near-zero offset would do nothing anyway - so it neither votes nor moves.
                if (offset.magnitude < 0.25f)
                    continue;

                candidates.Add((meshFilter, mesh, offset));

                Vector3 centre = mesh.bounds.center;

                if (Vector3.Distance(centre, offset) < centre.magnitude)
                    votesModelSpace++;
                else
                    votesFrameLocal++;

                if (s_logRebaseDecisions)
                {
                    UnityEngine.Debug.Log($"REBASE {go.name}/{meshFilter.name} " +
                        $"offset=({offset.x:F2},{offset.y:F2},{offset.z:F2}) " +
                        $"centre=({centre.x:F2},{centre.y:F2},{centre.z:F2}) " +
                        $"asModel={Vector3.Distance(centre, offset):F3} asLocal={centre.magnitude:F3}");
                }
            }

            bool rebaseThisVehicle = votesModelSpace > votesFrameLocal;

            if (!rebaseThisVehicle)
            {
                if (s_logRebaseDecisions || votesModelSpace > 0)
                {
                    UnityEngine.Debug.Log($"{go.name}: already frame-local " +
                        $"({votesFrameLocal} parts agree, {votesModelSpace} disagree) - left alone");
                }

                return;
            }

            foreach (var candidate in candidates)
            {
                var vertices = candidate.mesh.vertices;
                for (int i = 0; i < vertices.Length; i++)
                    vertices[i] -= candidate.offset;

                candidate.mesh.vertices = vertices;
                candidate.mesh.RecalculateBounds();
            }

            UnityEngine.Debug.Log($"{go.name}: rebased {candidates.Count} part meshes into frame space " +
                $"({votesModelSpace} parts agree, {votesFrameLocal} disagree)");
        }

        private void AttachVehicleWheels(GameObject go)
        {
            Transform master = null;
            var dummies = new List<Transform>();

            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            {
                if (null == master && t.name == "wheel")
                    master = t;

                if (t.name.StartsWith("wheel_") && t.name.EndsWith("_dummy"))
                    dummies.Add(t);
            }

            if (null == master || dummies.Count == 0)
                return;

            // Centre the master wheel on its own geometry before any copies are made.
            //
            // RebaseVehicleMeshes cannot do this one: it rebases by a part's transform offset, and the
            // master wheel's transform sits at the origin while its mesh sits out at a corner. So it is
            // skipped there and stays in model space, and every copy dropped into a dummy carries that
            // corner offset - which puts all four wheels in a heap beside the car instead of under it.
            var masterFilter = master.GetComponent<MeshFilter>();
            if (masterFilter != null && masterFilter.sharedMesh != null)
            {
                Mesh wheelMesh = masterFilter.sharedMesh;
                Vector3 centre = wheelMesh.bounds.center;

                if (centre.sqrMagnitude > 0.000001f)
                {
                    var vertices = wheelMesh.vertices;
                    for (int i = 0; i < vertices.Length; i++)
                        vertices[i] -= centre;

                    wheelMesh.vertices = vertices;
                    wheelMesh.RecalculateBounds();

                    UnityEngine.Debug.Log($"{go.name}: centred wheel mesh (was offset by {centre})");
                }
            }

            int attached = 0;

            foreach (Transform dummy in dummies)
            {
                // some models already ship a wheel under the dummy - use it rather than adding a second
                Transform existing = null;
                for (int i = 0; i < dummy.childCount; i++)
                {
                    if (dummy.GetChild(i).name == "wheel")
                    {
                        existing = dummy.GetChild(i);
                        break;
                    }
                }

                if (existing != null)
                {
                    existing.gameObject.SetActive(true);
                    continue;
                }

                var copy = UnityEngine.Object.Instantiate(master.gameObject, dummy, false);
                copy.name = "wheel";
                copy.transform.localPosition = Vector3.zero;
                copy.transform.localRotation = Quaternion.identity;
                copy.SetActive(true);
                attached++;
            }

            // the master sits wherever the artist left it - hide it unless it happens to be a real wheel
            bool masterIsPlaced = master.parent != null
                && master.parent.name.StartsWith("wheel_")
                && master.parent.name.EndsWith("_dummy");

            if (!masterIsPlaced && attached > 0)
                master.gameObject.SetActive(false);
        }

        private static string MakeFileNameSafe(string fileName)
        {
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(invalidChar, '_');
            return fileName;
        }

        /// <summary>
        /// Mirrors what <see cref="Behaviours.PedModel"/> does after attaching frames: some ped models have
        /// white spaces in frame names and some don't, so names are stripped to make every model share the
        /// same skeleton naming. Animation curves are bound to these paths, so exported ped prefabs must be
        /// normalized the same way or exported animations won't bind to them.
        /// </summary>
        private static void NormalizePedFrameNames(FrameContainer frames)
        {
            foreach (var frame in frames)
            {
                frame.Name = frame.Name.Replace(" ", "");
                frame.gameObject.name = frame.Name;
            }
        }

        private static IEnumerable WaitForCompletionOfLoadingJobs(
            string textSuffix,
            float startPerc,
            float endPerc,
            int numIterations,
            Ref<bool> isCanceledRef)
        {
            if (numIterations < 1)
                throw new ArgumentOutOfRangeException(nameof(numIterations));

            isCanceledRef.value = false;

            float diffPerc = endPerc - startPerc;

            for (int i = 0; i < numIterations; i++)
            {
                long initialNumPendingJobs = Importing.LoadingThread.Singleton.BackgroundJobRunner.GetNumPendingJobs();
                long numPendingJobs = initialNumPendingJobs;

                do
                {
                    long numJobsProcessed = initialNumPendingJobs - numPendingJobs;

                    float currentPerc = startPerc + diffPerc * (0 == initialNumPendingJobs ? 0f : numJobsProcessed / (float)initialNumPendingJobs);
                    if (EditorUtils.DisplayPausableProgressBar("", $"Waiting for async jobs to finish ({numJobsProcessed}/{initialNumPendingJobs})...{textSuffix}", currentPerc))
                    {
                        isCanceledRef.value = true;
                        yield break;
                    }

                    Importing.LoadingThread.Singleton.UpdateJobs();

                    System.Threading.Thread.Sleep(5); // don't interact with background thread too often, and also reduce CPU usage
                    
                    numPendingJobs = Importing.LoadingThread.Singleton.BackgroundJobRunner.GetNumPendingJobs();
                    initialNumPendingJobs = Math.Max(initialNumPendingJobs, numPendingJobs);

                } while (numPendingJobs > 0);
            }

        }

        void CreateFolders()
        {
            if (!Directory.Exists(m_selectedFolder))
                Directory.CreateDirectory(m_selectedFolder);
            
            string[] folders = new string[]
            {
                ModelsPath,
                MaterialsPath,
                TexturesPath,
                PrefabsPath,
                CollisionModelsPath,
                MapObjectPrefabsPath,
                AnimationsPath,
                AvatarsPath,
                AnimatorControllersPath,
                AudioPath,
            };

            foreach (string folder in folders)
            {
                if (!Directory.Exists(folder))
                    Directory.CreateDirectory(folder);
            }
        }

        private void RegisterSaveAssetAction(UnityEngine.Object asset, string path, Action<UnityEngine.Object> assignAsset)
        {
            if (asset == null && !path.IsNullOrWhiteSpace())
            {
                UnityEngine.Debug.LogError($"RegisterSaveAssetAction(): asset is null, path: {path}");
                return;
            }

            m_saveAssetActions.Add(new SaveAssetAction
            {
                asset = asset,
                path = path,
                assignAsset = assignAsset,
            });
        }

        private void ProcessSavedAssetActions()
        {
            var writeActions = new List<SaveAssetAction>();

            // first read-only access
            foreach (var action in m_saveAssetActions)
            {
                if (action.path.IsNullOrWhiteSpace())
                    continue;
                if (AssetDatabase.Contains(action.asset))
                    continue;
                if (AssetExistsAtPath(action.path))
                {
                    action.assignAsset(AssetDatabase.LoadMainAssetAtPath(action.path));
                    continue;
                }

                writeActions.Add(action);
            }

            // now write access
            if (writeActions.Count > 0)
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var action in writeActions.DistinctBy(a => a.asset))
                    {
                        AssetDatabase.CreateAsset(action.asset, action.path);
                        //action.assignAsset();
                        m_numNewlyExportedAssets++;
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
            }

            // now callbacks
            foreach (var action in m_saveAssetActions)
            {
                if (action.path.IsNullOrWhiteSpace())
                    action.assignAsset(null);
            }

            m_saveAssetActions.Clear();
        }

        public void ExportAssets(GameObject go)
        {
            string assetName = go.name;

            if (m_exportRenderMeshes)
            {
                var meshFilters = go.GetComponentsInChildren<MeshFilter>();

                for (int i = 0; i < meshFilters.Length; i++)
                {
                    MeshFilter meshFilter = meshFilters[i];
                    string indexPath = meshFilters.Length == 1 ? "" : "-" + i;
                    RegisterSaveAssetAction(meshFilter.sharedMesh, $"{ModelsPath}/{assetName}{indexPath}.asset", (obj) => meshFilter.sharedMesh = (Mesh)obj);
                    //meshFilter.sharedMesh = (Mesh)CreateAssetIfNotExists(meshFilter.sharedMesh, $"{ModelsPath}/{assetName}{indexPath}.asset");
                }
            }

            var meshRenderers = go.GetComponentsInChildren<MeshRenderer>();

            for (int i = 0; i < meshRenderers.Length; i++)
            {
                ExportMeshRenderer(go, meshRenderers[i], meshRenderers.Length == 1 ? (int?)null : i);
            }

            // skinned meshes are used by peds - their mesh lives on the renderer itself, not on a MeshFilter
            var skinnedMeshRenderers = go.GetComponentsInChildren<SkinnedMeshRenderer>();

            for (int i = 0; i < skinnedMeshRenderers.Length; i++)
            {
                var skinnedMeshRenderer = skinnedMeshRenderers[i];

                if (m_exportRenderMeshes)
                {
                    string indexPath = skinnedMeshRenderers.Length == 1 ? "" : "-" + i;
                    // note the distinct suffix - a model can have both a MeshFilter and a skinned mesh,
                    // and identical paths would make one silently overwrite the other
                    RegisterSaveAssetAction(
                        skinnedMeshRenderer.sharedMesh,
                        $"{ModelsPath}/{assetName}{indexPath}-skinned.asset",
                        obj => skinnedMeshRenderer.sharedMesh = (Mesh)obj);
                }

                ExportMeshRenderer(go, skinnedMeshRenderer, skinnedMeshRenderers.Length == 1 ? (int?)null : i);
            }

            if (m_exportCollisionMeshes)
            {
                var meshColliders = go.GetComponentsInChildren<MeshCollider>();

                for (int i = 0; i < meshColliders.Length; i++)
                {
                    int tempColliderIndex = i;
                    string indexPath = meshColliders.Length == 1 ? "" : "-" + i;
                    RegisterSaveAssetAction(meshColliders[i].sharedMesh, $"{CollisionModelsPath}/{assetName}{indexPath}.asset", obj => meshColliders[tempColliderIndex].sharedMesh = (Mesh)obj);
                    //meshColliders[i].sharedMesh = (Mesh)CreateAssetIfNotExists(meshColliders[i].sharedMesh, $"{CollisionModelsPath}/{assetName}{indexPath}.asset");
                }
            }

        }

        // accepts any Renderer, so it handles both MeshRenderer (static geometry, vehicles)
        // and SkinnedMeshRenderer (peds)
        public void ExportMeshRenderer(GameObject rootGo, Renderer meshRenderer, int? index)
        {
            if (!m_exportTextures && !m_exportMaterials)
                return;

            string indexPath = index.HasValue ? "-" + index.Value : "";
            // distinguish skinned renderers, so their materials/textures can't collide with those
            // of a MeshRenderer sitting on the same object
            string kindSuffix = meshRenderer is SkinnedMeshRenderer ? "-skinned" : "";
            string assetName = rootGo.name + indexPath + kindSuffix;

            var mats = meshRenderer.sharedMaterials.ToArray();

            for (int i = 0; i < mats.Length; i++)
            {
                if (null == mats[i])
                    continue;

                if (m_exportTextures)
                {
                    int tempTexIndex = i;
                    var tex = mats[i].mainTexture;
                    if (tex != null && tex != Texture2D.whiteTexture) // sometimes materials will have white texture assigned, and Unity will crash if we attempt to create asset from it
                        RegisterSaveAssetAction(tex, $"{TexturesPath}/{assetName}-{i}.asset", obj => mats[tempTexIndex].mainTexture = (Texture)obj);
                        //mats[i].mainTexture = (Texture)CreateAssetIfNotExists(tex, $"{TexturesPath}/{assetName}-{i}.asset");
                }

                int tempMatIndex = i;
                if (m_exportMaterials)
                    RegisterSaveAssetAction(mats[i], $"{MaterialsPath}/{assetName}-{i}.mat", obj => mats[tempMatIndex] = (Material)obj);
                    //mats[i] = (Material)CreateAssetIfNotExists(mats[i], $"{MaterialsPath}/{assetName}-{i}.mat");
            }

            RegisterSaveAssetAction(null, "", obj => meshRenderer.sharedMaterials = mats);
            //meshRenderer.sharedMaterials = mats;
        }

        private UnityEngine.Object CreateAssetIfNotExists(UnityEngine.Object asset, string path)
        {
            if (AssetDatabase.Contains(asset))
                return asset;

            if (AssetExistsAtPath(path))
            {
                return AssetDatabase.LoadMainAssetAtPath(path);
            }

            AssetDatabase.CreateAsset(asset, path);

            m_numNewlyExportedAssets++;

            return asset;
        }

        private bool AssetExistsAtPath(string path)
        {
            if (File.Exists(Path.Combine(Directory.GetParent(Application.dataPath).FullName, path)))
            {
                m_numAlreadyExportedAssets++;
                return true;
            }
            return false;
        }

        private void DisplayMessage(string message)
        {
            if (m_isSilentMode)
                UnityEngine.Debug.Log(message);
            else
                EditorUtility.DisplayDialog("", message, "Ok");
        }

        private bool AskDialog(bool defaultValue, string message, string ok, string cancel)
        {
            if (m_isSilentMode)
                return defaultValue;
            return EditorUtility.DisplayDialog("", message, ok, cancel);
        }

    }
}
