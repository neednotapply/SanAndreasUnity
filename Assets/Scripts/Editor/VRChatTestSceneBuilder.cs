using SanAndreasUnity.Export;
using SanAndreasUnity.VRChat;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.AI.Navigation;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;
using UnityEditor.Events;
using UnityEngine.Events;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Builds a self-contained test scene from the EXPORTED assets only - no runtime importer, no GTA
    /// install. This is the first thing in the project that exercises the VRChat path end to end:
    /// exported world geometry, the baked nav mesh, the baked path network, and the Udon behaviours.
    ///
    /// The world is trimmed to a radius around a spawn point so the scene stays openable; the full city is
    /// ~45k objects and makes iteration painful.
    /// </summary>
    public static class VRChatTestSceneBuilder
    {
        private const string ExportRoot = "Assets/ExportedAssets";
        private const string WorldPrefabPath = ExportRoot + "/Prefabs/ExportedWorldFromGameFiles.prefab";
        private const string NavMeshPath = ExportRoot + "/NavMesh_Full_v035.asset";
        private const string PathNetworkPath = ExportRoot + "/PathNetwork.asset";
        private const string PedPrefabsFolder = ExportRoot + "/Prefabs/Peds";
        /// <summary>
        /// Only road cars. Vehicles are exported per type, so boats, planes and helicopters no longer end
        /// up in street traffic - they need their own placement rules (water, airports) to make sense.
        /// </summary>
        private const string VehiclePrefabsFolder = ExportRoot + "/Prefabs/Vehicles/Car";
        private const string WeaponPrefabsFolder = ExportRoot + "/Prefabs/Weapons";

        // Handed between build steps: the HUD and the player's health both need to see things created
        // earlier in the build, and threading them through every call signature is worse than this.
        private static GameObject[] s_pooledNpcs = new GameObject[0];
        private static GtaWeapon[] s_weapons = new GtaWeapon[0];
        private static GtaDayNightCycle s_dayNight;

        // Built before the clock exists, so it is wired up afterwards. Four separate systems have now been
        // caught taking a null clock this way; anything needing the time of day should be connected below
        // where s_dayNight is assigned, not where the object is created.
        private static GtaWorldStreamer s_streamer;
        private static readonly List<GtaTrafficSignals> s_signals = new List<GtaTrafficSignals>();
        private static System.Collections.Generic.List<Transform> s_spawnPoints;
        private static readonly List<GtaPlayerVehicleSeat> s_playerSeats =
            new List<GtaPlayerVehicleSeat>();
        private static GameObject[] s_cellRoots = new GameObject[0];
        private static Vector3[] s_cellCenters = new Vector3[0];
        private static float s_cellSize = 200f;
        private const string VehicleHandlingPath = ExportRoot + "/VehicleHandling.asset";
        private const string ScenePath = "Assets/Scenes/VRChatTest.unity";

        private const float DefaultRadius = 300f;
        /// <summary>
        /// Size of the NPC pool. These are not all visible at once - the pool activates them around
        /// players and recycles the rest, so this is the ceiling on concurrent peds.
        /// </summary>
        private const int DefaultPedCount = 60;

        [MenuItem(EditorCore.MenuName + "/" + "Build VRChat test scene")]
        public static void BuildFromMenu()
        {
            Build(DefaultRadius, DefaultPedCount, false);
        }

        /// <summary> Headless entry point. </summary>
        public static void BuildFromCommandLine()
        {
            float radius = ParseFloatArg("testSceneRadius", DefaultRadius);
            int pedCount = (int)ParseFloatArg("testScenePedCount", DefaultPedCount);
            bool includeWorld = ParseFloatArg("testSceneIncludeWorld", 0f) > 0f;
            bool streamWorld = ParseFloatArg("testSceneStreamWorld", 0f) > 0f;
            float cellSize = ParseFloatArg("testSceneCellSize", 200f);
            int vehicleCount = (int)ParseFloatArg("testSceneVehicleCount", 25f);
            s_pedFootOffset = ParseFloatArg("testScenePedFootOffset", s_pedFootOffset);
            float parkedRadius = ParseFloatArg("testSceneParkedRadius", 400f);

            try
            {
                Build(radius, pedCount, includeWorld, streamWorld, cellSize, vehicleCount, parkedRadius);
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Test scene build failed: {ex}");
                EditorApplication.Exit(1);
            }
        }

        public static void Build(
            float radius, int pedCount, bool includeWorld, bool streamWorld = false, float cellSize = 200f,
            int vehicleCount = 25, float parkedVehicleRadius = 400f)
        {
            Debug.Log($"=== Building VRChat test scene " +
                $"(includeWorld={includeWorld}, streamWorld={streamWorld}, cellSize={cellSize}) ===");

            // NOTE: after ADDING a new UdonSharpBehaviour, this build must be run as two separate Editor
            // invocations - once with UdonProgramAssetGenerator.GenerateAndCompile, then again to build.
            // A single session fails two ways: the editor assembly compiles before the runtime assembly has
            // picked up the new script ("CS0246: type not found"), and a program asset created during the
            // session is not registered in time ("outdated script version"). Both are ordering, not bugs.
            //
            // Every UdonSharpBehaviour needs a compiled UdonSharpProgramAsset before it can be added to a
            // GameObject - without one, UdonSharpUndo.AddComponent throws a NullReferenceException deep
            // inside RunBehaviourSetup. Newly written behaviours have no asset yet, so ensure they all
            // exist up front rather than failing late, after an expensive world build.
            // VRChat reserves layers 8-14, which is exactly where SanAndreasUnity puts Vehicle, World,
            // PedBone and friends. Objects handed to VRChat on the wrong layer get the wrong physics and
            // interaction rules, so fix the project configuration before building anything into a scene.
            VRChatLayerSetup.EnsureLayersAndCollisionMatrix();

            UdonProgramAssetGenerator.GenerateAndCompile();

            // Exported clips have no loop flag, so a Mecanim state plays once and freezes on the last
            // frame - peds change pose when the state changes, then stand still. Idempotent, so this is
            // effectively free once it has run.
            AnimationLoopFixer.SetClipsLooping();

            var pathNetwork = AssetDatabase.LoadAssetAtPath<GtaPathNetwork>(PathNetworkPath);
            if (null == pathNetwork)
                throw new Exception($"Path network not found at {PathNetworkPath}");

            var navMeshData = AssetDatabase.LoadAssetAtPath<NavMeshData>(NavMeshPath);
            if (null == navMeshData)
                throw new Exception($"Nav mesh not found at {NavMeshPath}");

            // Spawn where the data says peds can actually walk, rather than guessing a coordinate.
            Vector3 spawnPoint = FindPedSpawnPoint(pathNetwork);
            Debug.Log($"Chosen spawn point (from ped path nodes): {spawnPoint}");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // ---- world geometry (optional) --------------------------------------------------------
            // Instantiating the full world prefab (45k objects referencing 8666 sub-prefabs) crashes the
            // Editor in batch mode, and loading the whole city only to delete 99% of it is wasteful anyway.
            // The nav mesh is independent baked data, so the AI stack can be validated without geometry.
            bool worldIsStreamed = false;

            if (includeWorld || streamWorld)
            {
                var worldParent = new GameObject("World");

                Debug.Log("Parsing world placements...");
                var placements = WorldChunkLoader.ParsePlacements(WorldPrefabPath);
                Debug.Log($"Parsed {placements.Count} map object placements");

                if (streamWorld)
                {
                    // whole city, grouped into cells that GtaWorldStreamer toggles around the player
                    WorldChunkLoader.BuildStreamingCells(
                        placements,
                        cellSize,
                        worldParent.transform,
                        out var cellRoots,
                        out var cellCenters);

                    var streamerGo = new GameObject("WorldStreamer");
                    var streamer = AddUdonBehaviour<GtaWorldStreamer>(streamerGo);
                    streamer.cellRoots = cellRoots.ToArray();
                    streamer.cellCenters = cellCenters.ToArray();
                    streamer.cellLamps = GroupCellLamps(cellRoots);

                    // signals run off server time, so they need no wiring to the clock
                    GroupCellSignals(cellRoots);

                    // parked cars are parented into these, so they stream with the streets they sit on
                    s_cellRoots = streamer.cellRoots;
                    s_cellCenters = streamer.cellCenters;
                    s_cellSize = cellSize;

                    // The clock does not exist yet - the world is built first - so the streamer is kept
                    // and given its clock further down. Assigning s_dayNight here would silently store
                    // null and the street lighting would never come on.
                    s_streamer = streamer;
                    UdonSharpEditorUtility.CopyProxyToUdon(streamer);

                    // the city is what the player walks on and collides with - VRChat expects that on
                    // Environment, not on whatever layer the importer originally baked in
                    VRChatLayerSetup.SetLayerRecursive(worldParent, VRChatLayerSetup.LayerEnvironment);

                    worldIsStreamed = true;
                    Debug.Log($"World streamer wired with {cellRoots.Count} cells (layer: Environment)");
                }
                else
                {
                    int built = WorldChunkLoader.InstantiateWithinRadius(
                        placements, spawnPoint, radius, worldParent.transform);

                    Debug.Log($"World chunk built: {built} objects within {radius}m of {spawnPoint}");
                }
            }
            else
            {
                Debug.Log("World geometry skipped (pass -testSceneIncludeWorld:1 or -testSceneStreamWorld:1)");
            }

            // ---- nav mesh -------------------------------------------------------------------------
            var navGo = new GameObject("NavMesh");
            var surface = navGo.AddComponent<NavMeshSurface>();
            surface.navMeshData = navMeshData;
            surface.AddData();
            Debug.Log("Nav mesh surface added with baked data");

            // ---- path network data (Udon) ---------------------------------------------------------
            var pathGo = new GameObject("PathNetworkData");
            var pathData = AddUdonBehaviour<GtaPathNetworkData>(pathGo);
            PathNetworkBaker.Bake(pathNetwork, pathData);
            // proxy fields only reach the Udon heap when explicitly copied - without this the behaviour
            // would have null arrays at runtime
            UdonSharpEditorUtility.CopyProxyToUdon(pathData);
            Debug.Log($"Path network baked into Udon behaviour: {pathData.NodeCount} nodes");

            // ---- VRChat scene descriptor ----------------------------------------------------------
            // Without this the scene is not a VRChat world at all: ClientSim won't initialize, the VRChat
            // player controller and UI never appear, and Udon runs as plain C# proxies rather than through
            // the Udon VM - which means nothing about the actual VRChat behaviour gets tested.
            // Spawns must be limited to the geometry chunk when one is loaded - otherwise players spawn
            // over empty space, fall to the respawn plane, and respawn into empty space again.
            // A little inside the chunk edge, so you don't spawn on the boundary looking at nothing.
            // A streamed world covers the whole map, so spawns can go anywhere. A fixed chunk only has
            // ground in one place, so spawns must stay inside it or the player falls through empty space.
            var spawnTransforms = AddSceneDescriptor(
                pathNetwork,
                spawnPoint,
                spawnPoint,
                (includeWorld && !worldIsStreamed) ? radius * 0.8f : -1f);

            // ---- peds (pooled around the player) ---------------------------------------------------
            // Peds are NOT placed at fixed points. A fixed pool is activated in a ring around whoever is
            // playing and recycled once they walk away - the same approach GTA uses, and the right one for
            // VRChat where runtime instantiation is expensive and awkward to sync.
            // The clock is created before any population or vehicles: the NPC pool reads the hour to
            // decide which crowd a district should have, and headlights need it too. Anything built
            // before this point would silently get no clock at all.
            // ---- a light so the scene isn't pitch black --------------------------------------------
            var lightGo = GameObject.Find("Directional Light");
            if (null == lightGo)
            {
                lightGo = new GameObject("Directional Light");
                var l = lightGo.AddComponent<Light>();
                l.type = LightType.Directional;
                l.intensity = 1f;
            }
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // ---- day/night cycle --------------------------------------------------------------------
            // Created before any vehicles: their headlights need a reference to it, and a vehicle built
            // earlier would silently get none.
            var dayNight = AddUdonBehaviour<GtaDayNightCycle>(lightGo);
            dayNight.sun = lightGo.GetComponent<Light>();
            ApplyTimeCycle(dayNight);
            UdonSharpEditorUtility.CopyProxyToUdon(dayNight);
            s_dayNight = dayNight;

            // now that the clock exists, hand it to everything built before it
            if (s_streamer != null)
            {
                s_streamer.dayNight = dayNight;
                UdonSharpEditorUtility.CopyProxyToUdon(s_streamer);
            }
            Debug.Log("Day/night cycle wired to the directional light");

            int pooled = CreateNpcPool(pathData, pedCount);
            Debug.Log($"NPC pool created with {pooled} pooled peds");

            // ---- traffic lights ---------------------------------------------------------------------
            var lightsGo = new GameObject("TrafficLights");
            var trafficLights = AddUdonBehaviour<GtaTrafficLightController>(lightsGo);
            UdonSharpEditorUtility.CopyProxyToUdon(trafficLights);

            // The signal lenses were grouped while the world was built, before this existed. Wiring them
            // to the same cycle the traffic AI obeys is what stops the lights a player sees disagreeing
            // with the lights the cars are stopping at.
            foreach (var signals in s_signals)
            {
                if (signals == null)
                    continue;

                signals.cycle = trafficLights;
                UdonSharpEditorUtility.CopyProxyToUdon(signals);
            }

            Debug.Log($"Traffic light controller added, driving {s_signals.Count} cells of signals");


            // ---- traffic (pooled on road nodes) -----------------------------------------------------
            int vehicles = CreateTrafficPool(pathData, trafficLights, vehicleCount);
            Debug.Log($"Traffic pool created with {vehicles} pooled vehicles");

            // ---- world settings (player scale, movement) --------------------------------------------
            var settingsGo = new GameObject("WorldSettings");
            var worldSettings = AddUdonBehaviour<GtaWorldSettings>(settingsGo);
            UdonSharpEditorUtility.CopyProxyToUdon(worldSettings);
            Debug.Log("World settings added (eye height constrained for vehicle fit)");

            // ---- teleport menu ----------------------------------------------------------------------
            s_spawnPoints = spawnTransforms;

            int destinations = CreateTeleportMenu(spawnPoint);
            Debug.Log($"Teleport menu created with {destinations} destinations");

            // ---- weapon pickups ---------------------------------------------------------------------
            int spawnable = CreateVehicleSpawner(spawnPoint);
            Debug.Log($"Vehicle spawner created with {spawnable} summonable vehicles");

            int weapons = CreateWeaponPickups(spawnPoint);
            Debug.Log($"Weapon pickups placed: {weapons}");

            // ---- water ------------------------------------------------------------------------------
            int waterMeshes = CreateWater();
            Debug.Log($"Water surface built from {waterMeshes} meshes");

            // ---- parked cars ------------------------------------------------------------------------
            int parked = CreateParkedVehicles(spawnPoint, parkedVehicleRadius);
            Debug.Log($"Parked vehicles placed: {parked}");

            // ---- player state + HUD -----------------------------------------------------------------
            CreatePlayerHud(spawnPoint, dayNight);
            Debug.Log("Player health and HUD created");

            // ---- radio ------------------------------------------------------------------------------
            int stations = CreateRadio(spawnPoint);

            // after every vehicle exists, so it reaches all their seats
            WireVehicleInput();
            Debug.Log($"Radio created with {stations} stations");

            // put the default camera somewhere useful
            var cam = Camera.main;
            if (cam != null)
            {
                cam.transform.position = spawnPoint + new Vector3(0f, 15f, -25f);
                cam.transform.LookAt(spawnPoint);
                cam.farClipPlane = 1000f;
            }

            System.IO.Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);

            Debug.Log($"=== Test scene saved to {ScenePath} ===");
        }

        /// <summary>
        /// Picks a ped node that actually has links, so the wander logic has somewhere to go. Nodes are
        /// grouped by area, so this also implicitly lands somewhere with real map content.
        /// </summary>
        private static Vector3 FindPedSpawnPoint(GtaPathNetwork network)
        {
            int best = -1;
            int bestLinks = 0;

            for (int i = 0; i < network.NodeCount; i++)
            {
                if (!network.nodeIsPedNode[i])
                    continue;
                if (network.nodeIsWater[i])
                    continue;

                int links = network.nodeLinkCount[i];
                if (links > bestLinks)
                {
                    bestLinks = links;
                    best = i;
                    if (links >= 4)
                        break; // good enough - a proper junction
                }
            }

            if (best < 0)
                throw new Exception("No suitable ped node found in path network");

            return network.nodePositions[best];
        }

        private static int TrimWorldToRadius(GameObject worldInstance, Vector3 center, float radius)
        {
            // the map objects live under whichever transform has by far the most children
            Transform container = null;
            int maxChildren = 0;

            foreach (Transform t in worldInstance.GetComponentsInChildren<Transform>(true))
            {
                if (t.childCount > maxChildren)
                {
                    maxChildren = t.childCount;
                    container = t;
                }
            }

            if (null == container)
                return 0;

            // unpack so we can delete children of a prefab instance
            PrefabUtility.UnpackPrefabInstance(
                worldInstance, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);

            float sqrRadius = radius * radius;
            var toRemove = new System.Collections.Generic.List<GameObject>();

            for (int i = 0; i < container.childCount; i++)
            {
                Transform child = container.GetChild(i);
                if ((child.position - center).sqrMagnitude > sqrRadius)
                    toRemove.Add(child.gameObject);
            }

            foreach (var go in toRemove)
                UnityEngine.Object.DestroyImmediate(go);

            return toRemove.Count;
        }

        /// <summary>
        /// Lays out grabbable weapons near the spawn.
        ///
        /// They are VRCPickups rather than an inventory: picking a gun up off a rack is an interaction
        /// VRChat players already understand, and it behaves the same in VR and on desktop. Statistics come
        /// from the exported weapon.dat, so each gun differs by data rather than by its own behaviour.
        /// </summary>
        private static int CreateWeaponPickups(Vector3 spawnPoint)
        {
            var stats = AssetDatabase.LoadAssetAtPath<GtaWeaponData>("Assets/ExportedAssets/WeaponData.asset");
            if (null == stats || stats.Count == 0)
            {
                Debug.LogWarning("No weapon data exported - skipping weapon pickups");
                return 0;
            }

            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { WeaponPrefabsFolder });
            if (guids.Length == 0)
            {
                Debug.LogWarning($"No weapon prefabs under {WeaponPrefabsFolder}");
                return 0;
            }

            // A rack at every spawn point, not just the first.
            //
            // Racks are parented into the streaming cell that covers them, so only the ones near a player
            // are ever live. Without that, fifty-eight racks of twenty-eight weapons would be over sixteen
            // hundred pickups active at once, which VRChat would not survive.
            var spawnList = s_spawnPoints != null && s_spawnPoints.Count > 0
                ? s_spawnPoints
                : null;

            var root = new GameObject("Weapons");
            root.transform.position = spawnPoint;

            var created = new System.Collections.Generic.List<GtaWeapon>();
            int placed = 0;
            int racks = 0;

            int spawnCount = spawnList != null ? spawnList.Count : 1;

            for (int spawnIndex = 0; spawnIndex < spawnCount; spawnIndex++)
            {
            Vector3 rackOrigin = spawnList != null && spawnList[spawnIndex] != null
                ? spawnList[spawnIndex].position
                : spawnPoint;

            Transform rackParent = root.transform;

            if (s_cellRoots != null && s_cellRoots.Length > 0)
            {
                Transform cell = FindCellFor(rackOrigin);
                if (cell != null)
                    rackParent = cell;
            }

            racks++;
            int placedHere = 0;

            foreach (string guid in guids)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (null == prefab)
                    continue;

                int index = stats.IndexOf(prefab.name);

                // only place things weapon.dat actually describes as weapons; the folder also holds
                // ammo boxes, cameras and other props that are not guns
                if (index < 0 || !stats.isGun[index])
                    continue;

                var weapon = (GameObject)PrefabUtility.InstantiatePrefab(prefab, rackParent);

                // lay them out in a row on an imaginary rack beside this spawn
                weapon.transform.position = rackOrigin
                    + new Vector3(-3f + (placedHere % 8) * 0.85f, 1.1f, 3.5f + (placedHere / 8) * 0.9f);
                weapon.transform.rotation = Quaternion.identity;

                var body = weapon.GetComponent<Rigidbody>();
                if (null == body)
                    body = weapon.AddComponent<Rigidbody>();
                body.mass = 2f;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                // exported weapons are bare meshes, so they need a collider to be grabbable at all
                if (weapon.GetComponentInChildren<Collider>() == null)
                {
                    var box = weapon.AddComponent<BoxCollider>();
                    var renderers = weapon.GetComponentsInChildren<MeshRenderer>();
                    if (renderers.Length > 0)
                    {
                        Bounds bounds = renderers[0].bounds;
                        foreach (var r in renderers)
                            bounds.Encapsulate(r.bounds);

                        box.center = weapon.transform.InverseTransformPoint(bounds.center);
                        box.size = bounds.size.magnitude > 0.01f ? bounds.size : Vector3.one * 0.3f;
                    }
                    else
                    {
                        box.size = Vector3.one * 0.3f;
                    }
                }

                // A weapon on a rack should stay on the rack.
                //
                // With gravity on, every weapon at every spawn drops to the floor the moment its cell
                // loads. VRChat takes the body over when the weapon is grabbed and thrown, so kinematic
                // here costs nothing at the point where physics actually matters.
                body.isKinematic = true;
                body.useGravity = false;

                var pickup = weapon.AddComponent<VRC.SDK3.Components.VRCPickup>();
                pickup.pickupable = true;

                // guns are held in one hand and fired with the trigger
                pickup.orientation = VRC.SDKBase.VRC_Pickup.PickupOrientation.Gun;

                // Click to pick up and keep holding it, rather than having to hold the button down.
                // Left unset this defaults to AutoDetect, which is what made a weapon something you could
                // hold but never really wield.
                pickup.AutoHold = VRC.SDKBase.VRC_Pickup.AutoHoldMode.Yes;

                // Where the hand goes and which way the weapon points once held. Without this the model is
                // held at whatever angle its pivot happens to have, so it aims off in some other direction
                // than the one being fired in.
                // created below, once the barrel direction is known from the model
                Transform grip = null;

                // Only the first rack is position-synced.
                //
                // VRCObjectSync is the expensive part of a pickup, and syncing every weapon at every spawn
                // would be over a thousand networked objects. Unsynced weapons still pick up and fire
                // normally; other players just do not see where you left one.
                if (spawnIndex == 0)
                    weapon.AddComponent<VRC.SDK3.Components.VRCObjectSync>();

                // Fire from the front of the model, measured rather than assumed - weapon models vary
                // in length from a pistol to a rocket launcher, and a fixed offset puts the muzzle inside
                // the longer ones.
                var muzzle = new GameObject("Muzzle");
                muzzle.transform.SetParent(weapon.transform, false);

                float reach = 0.35f;
                var muzzleRenderers = weapon.GetComponentsInChildren<MeshRenderer>();
                if (muzzleRenderers.Length > 0)
                {
                    Bounds b = muzzleRenderers[0].bounds;
                    foreach (var r in muzzleRenderers)
                        b.Encapsulate(r.bounds);

                    reach = Mathf.Max(0.2f, b.extents.z + 0.05f);
                }

                muzzle.transform.localPosition = new Vector3(0f, 0f, reach);

                // The muzzle flash is part of the model and exports switched on, which is why every weapon
                // on the rack looked like it was mid-shot. It is hidden here and shown for a frame or two
                // by GtaWeapon when the weapon actually fires.
                GameObject flash = null;
                foreach (var t in weapon.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name.IndexOf("flash", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        flash = t.gameObject;
                        flash.SetActive(false);

                        // put the muzzle where the flash is - the model already says where that is
                        muzzle.transform.position = t.position;
                        break;
                    }
                }

                // Which way the weapon points, taken from the model rather than assumed.
                //
                // ExactGun is where the hand goes and which way the weapon aims from it. Left at identity,
                // every weapon is held along whatever axis its pivot happened to use - which is why they
                // came out at odd angles. The muzzle flash marks the end of the barrel, so the line from
                // the weapon's origin to it is the barrel direction, measured rather than guessed.
                Vector3 barrel = Vector3.forward;

                if (flash != null)
                {
                    Vector3 local = weapon.transform.InverseTransformPoint(flash.transform.position);
                    if (local.sqrMagnitude > 0.0001f)
                        barrel = local.normalized;
                }
                else
                {
                    // no flash (melee weapons): the longest axis of the model is its length
                    var bounds = new Bounds(Vector3.zero, Vector3.zero);
                    bool first = true;

                    foreach (var r in weapon.GetComponentsInChildren<MeshRenderer>())
                    {
                        Bounds b = r.bounds;
                        if (first) { bounds = b; first = false; }
                        else bounds.Encapsulate(b);
                    }

                    Vector3 e = bounds.extents;

                    if (e.x >= e.y && e.x >= e.z) barrel = Vector3.right;
                    else if (e.y >= e.x && e.y >= e.z) barrel = Vector3.up;
                    else barrel = Vector3.forward;
                }

                var gripGo = new GameObject("Grip");
                gripGo.transform.SetParent(weapon.transform, false);
                gripGo.transform.localPosition = Vector3.zero;

                // the grip's forward runs down the barrel, so aiming the hand aims the weapon
                gripGo.transform.localRotation = Quaternion.LookRotation(barrel, Vector3.up);

                grip = gripGo.transform;
                pickup.ExactGun = grip;

                var gta = AddUdonBehaviour<GtaWeapon>(weapon);
                gta.modelName = prefab.name;
                gta.damage = stats.damage[index];
                gta.range = stats.range[index];
                gta.clipSize = stats.clipSize[index];
                gta.accuracy = stats.accuracy[index];
                gta.isGun = stats.isGun[index];
                gta.muzzle = muzzle.transform;
                gta.muzzleFlash = flash;
                UdonSharpEditorUtility.CopyProxyToUdon(gta);
                created.Add(gta);

                placed++;
                placedHere++;
            }
            }

            // Pickup is the layer VRChat expects for held objects; on Environment they cannot be grabbed
            VRChatLayerSetup.SetLayerRecursive(root, "Pickup");

            s_weapons = created.ToArray();

            Debug.Log($"Weapon racks: {racks} racks, {placed} pickups " +
                $"({(spawnList != null ? "one per spawn, streamed with the world" : "single rack")})");

            return placed;
        }

        /// <summary>
        /// Creates the local player's health tracker and the HUD that displays it.
        ///
        /// Both are per-client and unsynced: health belongs to the player it describes, and a HUD that
        /// followed someone else's head would be worse than none at all.
        /// </summary>
        private static void CreatePlayerHud(Vector3 spawnPoint, GtaDayNightCycle dayNight)
        {
            var healthGo = new GameObject("PlayerHealth");
            healthGo.transform.position = spawnPoint;

            var respawn = new GameObject("RespawnPoint");
            respawn.transform.SetParent(healthGo.transform, false);
            respawn.transform.position = spawnPoint + Vector3.up * 0.1f;

            var health = AddUdonBehaviour<GtaPlayerHealth>(healthGo);
            health.respawnPoint = respawn.transform;
            health.pooledNpcs = s_pooledNpcs;
            UdonSharpEditorUtility.CopyProxyToUdon(health);

            // ---- the panel itself ----
            var hudGo = new GameObject("PlayerHUD");
            hudGo.transform.position = spawnPoint + Vector3.up * 1.5f;

            var canvasGo = new GameObject("Canvas");
            canvasGo.transform.SetParent(hudGo.transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var canvasRect = canvas.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(700f, 180f);
            canvasRect.localScale = Vector3.one * 0.0009f;

            AddPanelImage(canvasGo, new Color(0.05f, 0.06f, 0.08f, 0.55f), Vector2.zero,
                new Vector2(700f, 180f));

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (null == font)
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            // health bar: a filled image so it can be driven by fillAmount
            var barBackGo = new GameObject("HealthBarBack");
            barBackGo.transform.SetParent(canvasGo.transform, false);
            var barBack = barBackGo.AddComponent<Image>();
            barBack.color = new Color(0.2f, 0.05f, 0.05f, 1f);
            var barBackRect = barBackGo.GetComponent<RectTransform>();
            barBackRect.sizeDelta = new Vector2(300f, 26f);
            barBackRect.anchoredPosition = new Vector2(-90f, 34f);

            var barGo = new GameObject("HealthBar");
            barGo.transform.SetParent(barBackGo.transform, false);
            var bar = barGo.AddComponent<Image>();
            bar.color = new Color(0.85f, 0.2f, 0.2f, 1f);
            bar.type = Image.Type.Filled;
            bar.fillMethod = Image.FillMethod.Horizontal;
            bar.fillAmount = 1f;
            var barRect = barGo.GetComponent<RectTransform>();
            barRect.sizeDelta = new Vector2(300f, 26f);
            barRect.anchoredPosition = Vector2.zero;

            var healthText = AddLabel(canvasGo, "HealthText", "100", font, 26, new Vector2(110f, 34f),
                new Vector2(120f, 34f), TextAnchor.MiddleRight);

            var clockText = AddLabel(canvasGo, "ClockText", "00:00", font, 26, new Vector2(180f, 34f),
                new Vector2(120f, 34f), TextAnchor.MiddleRight);

            var weaponText = AddLabel(canvasGo, "WeaponText", "UNARMED", font, 24, new Vector2(0f, -30f),
                new Vector2(480f, 34f), TextAnchor.MiddleCenter);

            // district readout - the game names the neighbourhood as you cross into it, and on a map this
            // size knowing whether you are in Ganton or Idlewood is genuinely useful
            var zoneText = AddLabel(canvasGo, "ZoneText", "SAN ANDREAS", GetUiFont(), 22,
                new Vector2(0f, -62f), new Vector2(480f, 30f), TextAnchor.MiddleCenter);

            GtaZoneDisplay zoneDisplayBehaviour = null;

            var zoneData = AssetDatabase.LoadAssetAtPath<GtaZoneData>("Assets/ExportedAssets/ZoneData.asset");
            if (zoneData != null && zoneData.Count > 0)
            {
                var zoneDisplay = AddUdonBehaviour<GtaZoneDisplay>(hudGo);
                zoneDisplayBehaviour = zoneDisplay;
                zoneDisplay.zoneNames = zoneData.names;
                zoneDisplay.zoneMins = zoneData.mins;
                zoneDisplay.zoneMaxs = zoneData.maxs;
                zoneDisplay.zoneVolumes = zoneData.volumes;
                zoneDisplay.zoneText = zoneText;
                UdonSharpEditorUtility.CopyProxyToUdon(zoneDisplay);
            }
            else
            {
                Debug.LogWarning("No zone data exported - the district readout will stay blank");
            }

            // ---- radar ----
            // The map is one assembled texture and the radar is a window onto it, so this is just a
            // RawImage whose uvRect moves. Placed to the left of the readouts, where GTA puts it.
            var mapTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/ExportedAssets/Map/SanAndreasMap.png");

            if (mapTexture != null)
            {
                const float RadarSize = 150f;

                // Root: holds the mask and the arrow. The arrow is a sibling of the mask rather than a
                // child, because a Mask clips everything under it and the arrow must stay drawn on top.
                var radarGo = new GameObject("Radar");
                radarGo.transform.SetParent(canvasGo.transform, false);

                var radarRect = radarGo.GetComponent<RectTransform>();
                if (null == radarRect)
                    radarRect = radarGo.AddComponent<RectTransform>();

                radarRect.sizeDelta = new Vector2(RadarSize, RadarSize);
                radarRect.anchoredPosition = new Vector2(-185f, 0f);

                // Circular crop. Unity has no round mask, so this is an Image holding a generated circle
                // sprite with Mask on it - the sprite's alpha is what does the cropping.
                var maskGo = new GameObject("Crop");
                maskGo.transform.SetParent(radarGo.transform, false);

                var maskImage = maskGo.AddComponent<Image>();
                maskImage.sprite = LoadOrCreateSprite("RadarCircle", 128, false);

                var mask = maskGo.AddComponent<Mask>();
                mask.showMaskGraphic = false;

                var maskRect = maskGo.GetComponent<RectTransform>();
                maskRect.sizeDelta = new Vector2(RadarSize, RadarSize);
                maskRect.anchoredPosition = Vector2.zero;

                // The map itself, sized so the whole world spans it. GtaMiniMap slides and turns this
                // rectangle under the mask; the visible circle is whatever ends up over the centre.
                var mapGo = new GameObject("Map");
                mapGo.transform.SetParent(maskGo.transform, false);

                var mapImage = mapGo.AddComponent<RawImage>();
                mapImage.texture = mapTexture;
                mapImage.raycastTarget = false;

                var mapRect = mapGo.GetComponent<RectTransform>();
                mapRect.anchorMin = new Vector2(0.5f, 0.5f);
                mapRect.anchorMax = new Vector2(0.5f, 0.5f);
                mapRect.pivot = new Vector2(0.5f, 0.5f);

                // player arrow, centred over the crop and pointing up the screen
                var arrowGo = new GameObject("PlayerArrow");
                arrowGo.transform.SetParent(radarGo.transform, false);

                var arrowImage = arrowGo.AddComponent<Image>();
                arrowImage.sprite = LoadOrCreateSprite("RadarArrow", 64, true);
                arrowImage.color = new Color(1f, 1f, 1f, 0.95f);
                arrowImage.raycastTarget = false;

                var arrowRect = arrowGo.GetComponent<RectTransform>();
                arrowRect.sizeDelta = new Vector2(16f, 16f);
                arrowRect.anchoredPosition = Vector2.zero;

                var miniMap = AddUdonBehaviour<GtaMiniMap>(hudGo);
                miniMap.mapImage = mapImage;
                miniMap.mapRect = mapRect;
                miniMap.radarPixels = RadarSize;
                miniMap.playerArrow = arrowRect;
                miniMap.districtLabel = zoneText;
                miniMap.zoneDisplay = zoneDisplayBehaviour;
                UdonSharpEditorUtility.CopyProxyToUdon(miniMap);

                Debug.Log("Radar added to the HUD");
            }
            else
            {
                Debug.LogWarning("No map texture exported - the HUD will have no radar");
            }

            s_nowPlayingText = AddLabel(canvasGo, "NowPlaying", string.Empty, GetUiFont(), 20,
                new Vector2(0f, -86f), new Vector2(660f, 28f), TextAnchor.MiddleCenter);

            var hud = AddUdonBehaviour<GtaPlayerHud>(hudGo);
            hud.playerHealth = health;
            hud.dayNight = dayNight;
            hud.panel = canvasGo.transform;
            hud.healthText = healthText;
            hud.healthBar = bar;
            hud.clockText = clockText;
            hud.weaponText = weaponText;
            hud.weapons = s_weapons;
            UdonSharpEditorUtility.CopyProxyToUdon(hud);
        }

        private const string LampMaterialPath = ExportRoot + "/Materials/VehicleLamp.mat";
        private const string TailLampMaterialPath = ExportRoot + "/Materials/VehicleTailLamp.mat";

        /// <summary>
        /// Adds headlights and tail lights that come on after dark.
        ///
        /// GTA vehicle models carry "headlights" and "taillights" dummy frames marking where the lamps sit,
        /// so the positions come from the model rather than being guessed per vehicle.
        ///
        /// The lamps are unlit emissive quads, not real lights. Dozens of active vehicles would mean over a
        /// hundred realtime lights, which VRChat cannot afford; seen from outside the car a glowing lamp is
        /// indistinguishable from a lit one.
        /// </summary>
        private static void AddVehicleLights(GameObject vehicle, GtaDayNightCycle dayNight)
        {
            if (dayNight == null)
                return;

            var lamps = new System.Collections.Generic.List<GameObject>();

            foreach (var frame in vehicle.GetComponentsInChildren<Transform>(true))
            {
                string name = frame.name.ToLowerInvariant();

                bool isHead = name.StartsWith("headlight");
                bool isTail = name.StartsWith("taillight");

                if (!isHead && !isTail)
                    continue;

                var lamp = GameObject.CreatePrimitive(PrimitiveType.Quad);
                lamp.name = isHead ? "HeadLamp" : "TailLamp";
                lamp.transform.SetParent(frame, false);
                lamp.transform.localPosition = Vector3.zero;
                // face along the vehicle: headlights forward, tail lights back
                lamp.transform.localRotation = Quaternion.Euler(0f, isHead ? 0f : 180f, 0f);
                lamp.transform.localScale = Vector3.one * (isHead ? 0.28f : 0.22f);

                // a lamp must never block a shot or a collision
                var collider = lamp.GetComponent<Collider>();
                if (collider != null)
                    UnityEngine.Object.DestroyImmediate(collider);

                var renderer = lamp.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = GetLampMaterial(isHead);
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                lamp.SetActive(false);
                lamps.Add(lamp);
            }

            if (lamps.Count == 0)
                return;

            var lights = AddUdonBehaviour<GtaVehicleLights>(vehicle);
            lights.dayNight = dayNight;
            lights.lamps = lamps.ToArray();
            UdonSharpEditorUtility.CopyProxyToUdon(lights);
        }

        private static Material s_headLampMaterial;
        private static Material s_tailLampMaterial;

        /// <summary> Unlit lamp material, created once and reused by every vehicle. </summary>
        private static Material GetLampMaterial(bool isHead)
        {
            if (isHead && s_headLampMaterial != null)
                return s_headLampMaterial;

            if (!isHead && s_tailLampMaterial != null)
                return s_tailLampMaterial;

            string path = isHead ? LampMaterialPath : TailLampMaterialPath;

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null)
            {
                // Unlit so the lamp glows regardless of scene lighting - which is the whole point of it
                var shader = Shader.Find("Unlit/Color");
                if (shader == null)
                    shader = Shader.Find("Standard");

                existing = new Material(shader);
                existing.color = isHead
                    ? new Color(1f, 0.96f, 0.85f)
                    : new Color(0.9f, 0.15f, 0.1f);

                System.IO.Directory.CreateDirectory(ExportRoot + "/Materials");
                AssetDatabase.CreateAsset(existing, path);
            }

            if (isHead)
                s_headLampMaterial = existing;
            else
                s_tailLampMaterial = existing;

            return existing;
        }

        private const string WaterFolder = ExportRoot + "/Water";
        private const string WaterMaterialPath = ExportRoot + "/Materials/Water.mat";

        /// <summary>
        /// Lays down San Andreas' water surface.
        ///
        /// The state is an island. Without this the map simply ends at the shoreline over empty space, and
        /// the canals, rivers and pools inland are dry trenches. The meshes are pre-built from water.dat at
        /// export time, so this only has to place and skin them.
        ///
        /// Water is not collided with. Swimming would need a custom Udon controller that does not exist
        /// yet - for now the surface is something to see and to drive a boat past, not to fall into.
        /// </summary>
        private static int CreateWater()
        {
            string[] guids = AssetDatabase.FindAssets("t:Mesh", new[] { WaterFolder });
            if (guids.Length == 0)
            {
                Debug.LogWarning($"No water meshes under {WaterFolder} - skipping water");
                return 0;
            }

            var root = new GameObject("Water");
            var material = GetWaterMaterial();

            int placed = 0;

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (null == mesh)
                    continue;

                var go = new GameObject(mesh.name);
                go.transform.SetParent(root.transform, false);

                go.AddComponent<MeshFilter>().sharedMesh = mesh;

                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                // a flat sea casting shadows over the whole map costs a great deal and shows nothing
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

                placed++;
            }

            VRChatLayerSetup.SetLayerRecursive(root, VRChatLayerSetup.LayerEnvironment);

            return placed;
        }

        private static Material s_waterMaterial;

        private static Material GetWaterMaterial()
        {
            if (s_waterMaterial != null)
                return s_waterMaterial;

            var existing = AssetDatabase.LoadAssetAtPath<Material>(WaterMaterialPath);

            if (existing == null)
            {
                var shader = Shader.Find("Standard");
                existing = new Material(shader);

                // semi-transparent blue-green, glossy enough to read as water under a moving sun
                existing.SetFloat("_Mode", 3f);   // transparent
                existing.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                existing.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                existing.SetInt("_ZWrite", 0);
                existing.DisableKeyword("_ALPHATEST_ON");
                existing.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                existing.renderQueue = 3000;

                existing.color = new Color(0.12f, 0.32f, 0.42f, 0.78f);
                existing.SetFloat("_Glossiness", 0.85f);
                existing.SetFloat("_Metallic", 0.1f);

                System.IO.Directory.CreateDirectory(ExportRoot + "/Materials");
                AssetDatabase.CreateAsset(existing, WaterMaterialPath);
            }

            s_waterMaterial = existing;
            return existing;
        }

        /// <summary>
        /// Places the cars the game parks around the map.
        ///
        /// These are IPL placements - a specific model at a specific kerb - and they are much of why the
        /// streets read as lived in rather than swept clean. There are thousands across the state, so only
        /// those near the spawn are placed; scattering the full set into a scene this size would add tens of
        /// thousands of objects for cars nobody is standing near.
        ///
        /// They are static props, not drivable. Making every parked car drivable would mean a station and a
        /// physics body on each, which is a different and much heavier feature.
        /// </summary>
        /// <summary>
        /// Places the cars the game parks around the map.
        ///
        /// These are IPL placements - a specific model at a specific kerb - and they are much of why GTA's
        /// streets read as lived in rather than swept clean. There are over a thousand across the state.
        ///
        /// Each car is parented into the streaming cell that covers its position, so it loads and unloads
        /// with the streets around it. Placing them all loose in the scene would mean a thousand vehicles
        /// active at once no matter where the player stood; radius-limiting them around the spawn instead
        /// would leave the rest of the state bare, which defeats the point.
        ///
        /// They are static scenery, not drivable. Giving each one a station and a physics body would be a
        /// much heavier feature, and the summonable vehicles already cover wanting something to drive.
        /// </summary>
        private static int CreateParkedVehicles(Vector3 spawnPoint, float fallbackRadius)
        {
            var data = AssetDatabase.LoadAssetAtPath<GtaParkedVehicleData>(
                "Assets/ExportedAssets/ParkedVehicles.asset");

            if (null == data || data.Count == 0)
            {
                Debug.LogWarning("No parked vehicle data exported - skipping");
                return 0;
            }

            LoadPopulationData();

            var handlingForParked = AssetDatabase.LoadAssetAtPath<GtaVehicleHandlingData>(VehicleHandlingPath);

            bool streamed = s_cellRoots != null && s_cellRoots.Length > 0;

            // without streaming cells there is nowhere to hang them, so fall back to a radius that keeps
            // the scene openable
            var looseRoot = streamed ? null : new GameObject("ParkedVehicles");

            // resolving a prefab by name is the expensive part; the same model recurs constantly
            var prefabCache = new System.Collections.Generic.Dictionary<string, GameObject>(
                System.StringComparer.OrdinalIgnoreCase);

            int placed = 0;
            int noPrefab = 0;
            int noCell = 0;
            int guaranteedCount = 0;
            int chosenByZone = 0;
            int damagedPanels = 0;
            int snappedToGround = 0;
            int liftedOnly = 0;

            // the world colliders were just created; without this the first raycasts miss all of them
            Physics.SyncTransforms();

            // candidate cars per cell, so each cell can decide its own occupancy at load
            var candidatesByCell = new Dictionary<Transform, List<GameObject>>();
            var zoneTypeByCell = new Dictionary<Transform, int>();

            for (int i = 0; i < data.Count; i++)
            {
                Vector3 position = data.positions[i];

                if (!streamed && Vector3.Distance(position, spawnPoint) > fallbackRadius)
                    continue;

                // Seeded from the position so the same spaces are filled with the same cars on every
                // export - a rebuild that reshuffled every parked car in the state would be its own bug.
                int seed = Mathf.Abs(
                    Mathf.RoundToInt(position.x * 11.3f) ^ Mathf.RoundToInt(position.z * 7.9f));

                int zoneType = ZoneTypeAt(position);

                // Whether a space holds a car is decided at runtime, not here.
                //
                // The game fills parking when an area streams in, against how busy the district is at that
                // moment - so a street is not identically parked on every visit, and a business district
                // empties overnight. Baking the choice would freeze one arrangement forever, so every space
                // is created and GtaParkedCarSpaces decides which are shown as each cell loads.
                //
                // The model in a space is still chosen here: Udon cannot create objects, so which car sits
                // in a given space has to be fixed even though whether it is there does not.
                bool guaranteed = data.forceSpawn != null && i < data.forceSpawn.Length && data.forceSpawn[i];

                string modelName = data.modelNames[i];

                // A placement that asked for "any vehicle" gets one appropriate to the district, rather
                // than one from a hand-picked list of ordinary-looking cars.
                if (data.wasRandom != null && i < data.wasRandom.Length && data.wasRandom[i])
                {
                    string zoneChoice = PickZoneVehicle(zoneType, seed);
                    if (!string.IsNullOrEmpty(zoneChoice))
                    {
                        modelName = zoneChoice;
                        chosenByZone++;
                    }
                }

                if (!prefabCache.TryGetValue(modelName, out GameObject prefab))
                {
                    prefab = FindVehiclePrefab(modelName);
                    prefabCache[modelName] = prefab;
                }

                if (null == prefab)
                {
                    noPrefab++;
                    continue;
                }

                Transform parent = looseRoot != null ? looseRoot.transform : FindCellFor(position);

                if (null == parent)
                {
                    // a car outside every cell would never be shown, so there is no point creating it
                    noCell++;
                    continue;
                }

                var vehicle = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);

                // Lift by the average wheel height, exactly as the original does when it spawns a car:
                //   position - Vector3.up * AverageWheelHeight
                //
                // The recorded position is not where the car's origin goes. Wheel dummies sit below the
                // origin (about -0.45 m on a saloon), so placing the origin at the raw position leaves the
                // wheels half sunk into the road. Parked cars are kinematic, so nothing ever pushes them
                // back up - which is what made every parked vehicle look sunken. This is the same kind of
                // error as the ped foot offset, and the original already shows the correct rule.
                vehicle.transform.position = PlaceOnGround(vehicle, position, out bool snapped);
                vehicle.transform.rotation = Quaternion.Euler(0f, data.angles[i], 0f);

                if (snapped) snappedToGround++; else liftedOnly++;

                // seeded from where the car is parked, so a street carries a mix of variants
                SelectVehicleExtras(vehicle, prefab.name, seed);

                AddVehicleCollider(vehicle);

                // Parked cars can be stolen, which is most of the point of a parked car.
                //
                // The body starts kinematic so hundreds of them can load without settling or launching out
                // of the kerb; the seat wakes the one being taken. They stream with their cell, so only the
                // few dozen near a player are ever live.
                var parkedBody = vehicle.GetComponent<Rigidbody>();
                if (null == parkedBody)
                    parkedBody = vehicle.AddComponent<Rigidbody>();

                parkedBody.isKinematic = true;
                parkedBody.interpolation = RigidbodyInterpolation.Interpolate;
                parkedBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                var parkedController = AddUdonBehaviour<GtaVehicleController>(vehicle);
                ApplyHandling(parkedController, handlingForParked, prefab.name);
                UdonSharpEditorUtility.CopyProxyToUdon(parkedController);

                // the handling file's mass, so a stolen car drives like itself and a bus takes more
                // punishment than a hatchback
                if (parkedController.vehicleMass > 1f)
                    parkedBody.mass = parkedController.vehicleMass;

                damagedPanels += AddVehicleDamage(vehicle, parkedBody.mass);

                var parkedSeat = AddDriverSeat(vehicle, parkedController, null);
                if (parkedSeat != null)
                {
                    parkedSeat.parkedBody = parkedBody;
                    UdonSharpEditorUtility.CopyProxyToUdon(parkedSeat);
                }

                // drivable, so it belongs on the default layer rather than with the scenery
                VRChatLayerSetup.SetLayerRecursive(vehicle, VRChatLayerSetup.LayerDefault);

                if (guaranteed)
                {
                    // the game always fills these, so they are simply left on
                    guaranteedCount++;
                }
                else
                {
                    if (!candidatesByCell.ContainsKey(parent))
                    {
                        candidatesByCell[parent] = new List<GameObject>();
                        zoneTypeByCell[parent] = zoneType;
                    }

                    candidatesByCell[parent].Add(vehicle);
                }

                placed++;
            }

            if (looseRoot != null && placed == 0)
                UnityEngine.Object.DestroyImmediate(looseRoot);

            int deciders = CreateParkingDeciders(candidatesByCell, zoneTypeByCell);

            Debug.Log($"Parked vehicles: {placed} spaces created" +
                (streamed ? " in streaming cells" : $" within {fallbackRadius}m of spawn") +
                $" ({guaranteedCount} always filled, {placed - guaranteedCount} decided at load by " +
                $"{deciders} cells, {chosenByZone} models chosen from the district's vehicle group, " +
                $"{noPrefab} missing prefab, {noCell} outside any cell, " +
                $"{damagedPanels} damage panels wired, {snappedToGround} snapped to the road surface, " +
                $"{liftedOnly} placed by wheel-height lift only)");

            return placed;
        }

        /// <summary> Distance to the nearest placed object in the covering cell, or -1 if there is none. </summary>
        private static float NearestGeometryDistance(Vector3 position)
        {
            Transform cell = FindCellFor(position);
            if (null == cell)
                return -1f;

            float nearest = -1f;

            foreach (Transform child in cell)
            {
                float distance = Vector3.Distance(child.position, position);
                if (nearest < 0f || distance < nearest)
                    nearest = distance;
            }

            return nearest;
        }

        /// <summary>
        /// True when the world has real geometry within <paramref name="radius"/> of a position.
        ///
        /// Used to check that a teleport destination leads somewhere that exists. Only the covering cell is
        /// searched, since anything further away than a cell is further away than the radius anyway.
        /// </summary>
        private static bool HasGeometryNear(Vector3 position, float radius)
        {
            Transform cell = FindCellFor(position);
            if (null == cell)
                return false;

            float sqrRadius = radius * radius;

            foreach (Transform child in cell)
            {
                if ((child.position - position).sqrMagnitude <= sqrRadius)
                    return true;
            }

            return false;
        }

        /// <summary> The streaming cell covering a position, or null if none does. </summary>
        private static Transform FindCellFor(Vector3 position)
        {
            if (s_cellRoots == null || s_cellCenters == null)
                return null;

            // cells are a regular grid, so the covering one is simply the nearest centre within half a cell
            float best = float.MaxValue;
            int bestIndex = -1;

            for (int i = 0; i < s_cellCenters.Length; i++)
            {
                Vector3 delta = s_cellCenters[i] - position;
                delta.y = 0f;

                float distance = delta.sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                return null;

            // reject anything well outside the grid rather than hanging it on a distant cell
            float reach = s_cellSize * 1.5f;
            if (best > reach * reach)
                return null;

            return s_cellRoots[bestIndex] != null ? s_cellRoots[bestIndex].transform : null;
        }

        private static GameObject FindVehiclePrefab(string modelName)
        {
            string[] guids = AssetDatabase.FindAssets(
                modelName + " t:Prefab", new[] { ExportRoot + "/Prefabs/Vehicles" });

            foreach (string guid in guids)
            {
                var candidate = AssetDatabase.LoadAssetAtPath<GameObject>(
                    AssetDatabase.GUIDToAssetPath(guid));

                if (candidate != null &&
                    string.Equals(candidate.name, modelName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return null;
        }

        private const int SpawnableVehicleCount = 30;

        /// <summary>
        /// Builds a paged list panel: background, title, a row of buttons and prev/next paging.
        ///
        /// Both the teleport board and the vehicle spawner are the same widget over different data, so the
        /// construction lives here once. Row clicks are wired to "{rowEventPrefix}{n}" on the given
        /// behaviour, which is how a Unity UI event reaches Udon.
        /// </summary>
        private static void BuildPagedPanel(
            GameObject root, VRC.Udon.UdonBehaviour backing, string title, string rowEventPrefix,
            string previousEvent, string nextEvent,
            out Text[] labels, out GameObject[] buttons, out Text pageLabel)
        {
            var canvasGo = new GameObject("Canvas");
            canvasGo.transform.SetParent(root.transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGo.AddComponent<GraphicRaycaster>();

            var canvasRect = canvas.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(700f, 900f);
            // world-space canvases are authored in pixels, so scale them down to metres
            canvasRect.localScale = Vector3.one * 0.0022f;
            canvasRect.localPosition = new Vector3(0f, 1.6f, 0f);

            // What makes VRChat point at this canvas at all.
            //
            // A world-space Canvas with a GraphicRaycaster is enough for Unity, but VRChat only aims its
            // laser at canvases carrying a VRCUiShape, and only registers a hit where there is a collider
            // to hit. Without both, the buttons are drawn but nothing can press them - which is why the
            // spawner and the teleport board could be read but not used.
            canvasGo.AddComponent<VRC.SDKBase.VRC_UiShape>();

            var uiCollider = canvasGo.AddComponent<BoxCollider>();
            uiCollider.size = new Vector3(canvasRect.sizeDelta.x, canvasRect.sizeDelta.y, 1f);
            uiCollider.isTrigger = true;

            AddPanelImage(canvasGo, new Color(0.06f, 0.07f, 0.09f, 0.92f), Vector2.zero,
                new Vector2(700f, 900f));

            var font = GetUiFont();

            AddLabel(canvasGo, "Title", title, font, 34, new Vector2(0f, 400f),
                new Vector2(660f, 60f), TextAnchor.MiddleCenter);

            labels = new Text[TeleportRows];
            buttons = new GameObject[TeleportRows];

            for (int row = 0; row < TeleportRows; row++)
            {
                float y = 320f - row * 62f;

                var buttonGo = new GameObject("Row" + row);
                buttonGo.transform.SetParent(canvasGo.transform, false);

                var image = buttonGo.AddComponent<Image>();
                image.color = new Color(0.16f, 0.18f, 0.22f, 1f);

                var rect = buttonGo.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(620f, 54f);
                rect.anchoredPosition = new Vector2(0f, y);

                var button = buttonGo.AddComponent<Button>();
                button.targetGraphic = image;

                UnityEventTools.AddStringPersistentListener(
                    button.onClick,
                    new UnityAction<string>(backing.SendCustomEvent),
                    rowEventPrefix + row);

                labels[row] = AddLabel(buttonGo, "Label", string.Empty, font, 26, Vector2.zero,
                    new Vector2(600f, 54f), TextAnchor.MiddleLeft);

                buttons[row] = buttonGo;
            }

            pageLabel = AddLabel(canvasGo, "PageLabel", "1 / 1", font, 26, new Vector2(0f, -350f),
                new Vector2(200f, 50f), TextAnchor.MiddleCenter);

            AddPagingButton(canvasGo, "Prev", "<", font, new Vector2(-220f, -350f), backing, previousEvent);
            AddPagingButton(canvasGo, "Next", ">", font, new Vector2(220f, -350f), backing, nextEvent);

            // UI must be on a layer VRChat's laser pointer can hit
            VRChatLayerSetup.SetLayerRecursive(root, VRChatLayerSetup.LayerInteractive);
        }

        private static Font GetUiFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (null == font)
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            return font;
        }

        /// <summary>
        /// Offers a selection of vehicles the player can summon.
        ///
        /// One instance of each offered model is pre-placed and parked out of the way, because Udon cannot
        /// create objects at runtime. Summoning moves that instance to the player.
        /// </summary>
        private static int CreateVehicleSpawner(Vector3 spawnPoint)
        {
            var handlingData = AssetDatabase.LoadAssetAtPath<GtaVehicleHandlingData>(VehicleHandlingPath);

            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { ExportRoot + "/Prefabs/Vehicles" });
            if (guids.Length == 0)
                return 0;

            var root = new GameObject("VehicleSpawner");
            root.transform.position = spawnPoint + new Vector3(-2.5f, 0f, 2f);
            root.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            var garage = new GameObject("SpawnableVehicles");
            // parked well below the map: they are only ever seen once summoned
            garage.transform.position = spawnPoint + Vector3.down * 200f;

            var created = new System.Collections.Generic.List<GameObject>();
            var names = new System.Collections.Generic.List<string>();

            // a spread across the roster rather than the first N alphabetically, which would be all A's
            int stride = Mathf.Max(1, guids.Length / SpawnableVehicleCount);

            for (int i = 0; i < guids.Length && created.Count < SpawnableVehicleCount; i += stride)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (null == prefab)
                    continue;

                var vehicle = (GameObject)PrefabUtility.InstantiatePrefab(prefab, garage.transform);

                SelectVehicleExtras(vehicle, prefab.name, created.Count * 7919);

                vehicle.transform.position = garage.transform.position
                    + new Vector3(created.Count * 8f, 0f, 0f);

                var body = vehicle.GetComponent<Rigidbody>();
                if (null == body)
                    body = vehicle.AddComponent<Rigidbody>();
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                AddVehicleCollider(vehicle);

                var controller = AddUdonBehaviour<GtaVehicleController>(vehicle);
                ApplyHandling(controller, handlingData, prefab.name);
                UdonSharpEditorUtility.CopyProxyToUdon(controller);

                // summoned cars are for driving, so they need the same seat the traffic cars have
                var seat = AddDriverSeat(vehicle, controller, null);
                if (seat != null)
                    UdonSharpEditorUtility.CopyProxyToUdon(seat);

                AddVehicleLights(vehicle, s_dayNight);

                VRChatLayerSetup.SetLayerRecursive(vehicle, VRChatLayerSetup.LayerDefault);

                vehicle.SetActive(false);

                created.Add(vehicle);
                names.Add(prefab.name);
            }

            if (created.Count == 0)
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(garage);
                return 0;
            }

            var spawner = AddUdonBehaviour<GtaVehicleSpawner>(root);
            var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(spawner);

            Text[] labels;
            GameObject[] buttons;
            Text pageLabel;

            BuildPagedPanel(root, backing, "VEHICLES", "SpawnRow",
                nameof(GtaVehicleSpawner.PreviousPage), nameof(GtaVehicleSpawner.NextPage),
                out labels, out buttons, out pageLabel);

            spawner.vehicles = created.ToArray();
            spawner.vehicleNames = names.ToArray();
            spawner.rowLabels = labels;
            spawner.rowButtons = buttons;
            spawner.pageLabel = pageLabel;
            UdonSharpEditorUtility.CopyProxyToUdon(spawner);

            return created.Count;
        }

        private static GtaPedGroupData s_pedGroups;
        private static string[][] s_pedModelsByCity;

        /// <summary>
        /// Street ped models, split by city.
        ///
        /// Two things are going on here. Of the 264 exported ped models most are mission and cutscene
        /// characters who never walk the streets - pedgrp.dat lists the ones the game actually uses as
        /// background population. And pedgrp repeats each group with "(SF)" and "(VEGAS)" variants, which is
        /// how San Fierro's crowd ends up looking different from Los Santos'.
        ///
        /// Index 0 is Los Santos, 1 San Fierro, 2 Las Venturas.
        /// </summary>
        private static string[][] GetPedModelsByCity()
        {
            if (s_pedModelsByCity != null)
                return s_pedModelsByCity;

            s_pedModelsByCity = new string[3][];

            if (null == s_pedGroups)
            {
                s_pedGroups = AssetDatabase.LoadAssetAtPath<GtaPedGroupData>(
                    "Assets/ExportedAssets/PedGroups.asset");
            }

            if (null == s_pedGroups || s_pedGroups.GroupCount == 0 || s_pedGroups.memberModels == null)
            {
                Debug.LogWarning("No ped group data - the crowd will be drawn from every exported ped, " +
                    "including mission-only characters");

                for (int c = 0; c < 3; c++)
                    s_pedModelsByCity[c] = new string[0];

                return s_pedModelsByCity;
            }

            for (int city = 0; city < 3; city++)
            {
                var distinct = new System.Collections.Generic.List<string>();
                var seen = new System.Collections.Generic.HashSet<string>(
                    System.StringComparer.OrdinalIgnoreCase);

                for (int g = 0; g < s_pedGroups.GroupCount; g++)
                {
                    int groupCity = s_pedGroups.groupCities != null && g < s_pedGroups.groupCities.Length
                        ? s_pedGroups.groupCities[g]
                        : 0;

                    if (groupCity != city)
                        continue;

                    int from = s_pedGroups.groupStarts[g];
                    int count = s_pedGroups.groupCounts[g];

                    for (int m = from; m < from + count && m < s_pedGroups.memberModels.Length; m++)
                    {
                        string model = s_pedGroups.memberModels[m];
                        if (!string.IsNullOrWhiteSpace(model) && seen.Add(model))
                            distinct.Add(model);
                    }
                }

                // Los Santos groups carry no suffix and are the fullest set, so a city with none of its own
                // falls back to them rather than to an empty crowd
                if (distinct.Count == 0 && city != 0 && s_pedModelsByCity[0] != null)
                    distinct.AddRange(s_pedModelsByCity[0]);

                distinct.Sort(System.StringComparer.OrdinalIgnoreCase);
                s_pedModelsByCity[city] = distinct.ToArray();
            }

            Debug.Log($"Street ped population: {s_pedModelsByCity[0].Length} Los Santos, " +
                $"{s_pedModelsByCity[1].Length} San Fierro, {s_pedModelsByCity[2].Length} Las Venturas");

            return s_pedModelsByCity;
        }

        /// <summary> Resolves a ped prefab from the given city's street population. </summary>
        private static Dictionary<string, int> s_popGroupOfModel;

        /// <summary>
        /// Maps each ped model to the popcycle column it belongs to.
        ///
        /// pedgrp.dat names its groups "POPCYCLE_GROUP_WORKERS (SF)" and so on, while popcycle.dat's columns
        /// are plain names like "WORKERS". Stripping the prefix and the city suffix lines the two up, which
        /// is what lets a pooled ped be matched against the mix a district calls for.
        /// </summary>
        private static Dictionary<string, int> s_vehicleGroupOfModel;

        /// <summary>
        /// Which popcycle group a vehicle model belongs to, or -1.
        ///
        /// cargrp.dat lists vehicles under names matching popcycle's own groups, so mapping a model back to
        /// a group is how traffic is told whether it belongs in a district. Built once and cached: it is
        /// asked for every vehicle in the pool.
        /// </summary>
        private static int VehicleGroupOf(string modelName)
        {
            if (s_vehicleGroupOfModel == null)
            {
                LoadPopulationData();
                s_vehicleGroupOfModel = new Dictionary<string, int>(
                    System.StringComparer.OrdinalIgnoreCase);

                if (s_vehicleGroups != null && s_popCycle != null)
                {
                    for (int vg = 0; vg < s_vehicleGroups.GroupCount; vg++)
                    {
                        string name = s_vehicleGroups.groupNames[vg];

                        int paren = name.IndexOf('(');
                        if (paren >= 0)
                            name = name.Substring(0, paren);

                        name = name.Trim();

                        const string prefix = "POPCYCLE_GROUP_";
                        if (name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                            name = name.Substring(prefix.Length);

                        // the popcycle group carrying the same name
                        int group = -1;
                        for (int g = 0; g < s_popCycle.groupNames.Length; g++)
                        {
                            if (string.Equals(s_popCycle.groupNames[g], name,
                                System.StringComparison.OrdinalIgnoreCase))
                            {
                                group = g;
                                break;
                            }
                        }

                        if (group < 0)
                            continue;

                        int start = s_vehicleGroups.groupStarts[vg];
                        int count = s_vehicleGroups.groupCounts[vg];

                        for (int m = 0; m < count; m++)
                        {
                            string model = s_vehicleGroups.memberModels[start + m];

                            // a model can appear in several groups; the first is good enough to place it
                            if (!s_vehicleGroupOfModel.ContainsKey(model))
                                s_vehicleGroupOfModel[model] = group;
                        }
                    }
                }
            }

            return s_vehicleGroupOfModel.TryGetValue(modelName, out int found) ? found : -1;
        }

        private static Dictionary<string, int> GetPopGroupOfModel()
        {
            if (s_popGroupOfModel != null)
                return s_popGroupOfModel;

            s_popGroupOfModel = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

            var popCycle = AssetDatabase.LoadAssetAtPath<GtaPopCycleData>(
                "Assets/ExportedAssets/PopCycle.asset");

            if (null == s_pedGroups)
            {
                s_pedGroups = AssetDatabase.LoadAssetAtPath<GtaPedGroupData>(
                    "Assets/ExportedAssets/PedGroups.asset");
            }

            if (null == popCycle || null == s_pedGroups || s_pedGroups.memberModels == null)
            {
                Debug.LogWarning("No popcycle or ped group data - districts will not shape their crowds");
                return s_popGroupOfModel;
            }

            for (int g = 0; g < s_pedGroups.GroupCount; g++)
            {
                string raw = s_pedGroups.groupNames[g];

                // "POPCYCLE_GROUP_CASUAL_POOR (SF)" -> "CASUAL_POOR"
                string name = raw;

                int paren = name.IndexOf('(');
                if (paren >= 0)
                    name = name.Substring(0, paren);

                name = name.Trim();

                const string prefix = "POPCYCLE_GROUP_";
                if (name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(prefix.Length);

                int column = System.Array.FindIndex(
                    popCycle.groupNames,
                    _ => _.Equals(name, System.StringComparison.OrdinalIgnoreCase));

                if (column < 0)
                    continue;

                int from = s_pedGroups.groupStarts[g];
                int count = s_pedGroups.groupCounts[g];

                for (int m = from; m < from + count && m < s_pedGroups.memberModels.Length; m++)
                {
                    string model = s_pedGroups.memberModels[m];

                    // a model can appear in several groups; the first is as good a claim as any
                    if (!string.IsNullOrWhiteSpace(model) && !s_popGroupOfModel.ContainsKey(model))
                        s_popGroupOfModel[model] = column;
                }
            }

            Debug.Log($"Ped models mapped to popcycle groups: {s_popGroupOfModel.Count}");

            return s_popGroupOfModel;
        }

        /// <summary> popcycle group column for a ped model, or -1. </summary>
        private static int PopGroupOf(string modelName)
        {
            var map = GetPopGroupOfModel();
            return map.TryGetValue(modelName, out int column) ? column : -1;
        }

        private static GameObject PickStreetPed(string[] allPedGuids, int seed, int city)
        {
            string[][] byCity = GetPedModelsByCity();

            string[] models = city >= 0 && city < byCity.Length ? byCity[city] : byCity[0];

            if (models != null && models.Length > 0)
            {
                // try a few: not every name in pedgrp.dat was necessarily exported
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    string model = models[Mathf.Abs(seed + attempt * 31) % models.Length];

                    string[] guids = AssetDatabase.FindAssets(
                        model + " t:Prefab", new[] { PedPrefabsFolder });

                    foreach (string guid in guids)
                    {
                        var candidate = AssetDatabase.LoadAssetAtPath<GameObject>(
                            AssetDatabase.GUIDToAssetPath(guid));

                        if (candidate != null &&
                            string.Equals(candidate.name, model, System.StringComparison.OrdinalIgnoreCase))
                        {
                            return candidate;
                        }
                    }
                }
            }

            if (allPedGuids != null && allPedGuids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(
                    allPedGuids[Mathf.Abs(seed) % allPedGuids.Length]);

                return AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }

            return null;
        }

        /// <summary>
        /// Feeds map.zon's city bounds to the NPC pool, so it only places a city's own people in it.
        ///
        /// The popcycle zone categories (BUSINESS, GANGLAND and the rest) are not present in any of the
        /// game's data files - info.zon reports every zone as type 0 - so a full population model is not
        /// reproducible. Which of the three cities a point is in IS in the data, and that is enough to keep
        /// San Fierro's crowd out of Los Santos.
        /// </summary>
        private static void ApplyCityRegions(GtaNpcPool pool)
        {
            var data = AssetDatabase.LoadAssetAtPath<GtaCityRegionData>(
                "Assets/ExportedAssets/CityRegions.asset");

            if (null == data || data.Count == 0)
            {
                Debug.LogWarning("No city region data - peds will be placed regardless of city");
                return;
            }

            var mins = new System.Collections.Generic.List<Vector3>();
            var maxs = new System.Collections.Generic.List<Vector3>();
            var indices = new System.Collections.Generic.List<int>();

            for (int i = 0; i < data.Count; i++)
            {
                mins.Add(data.mins[i]);
                maxs.Add(data.maxs[i]);

                // map.zon islands are 1=Los Santos, 2=San Fierro, 3=Las Venturas; the ped city tags are
                // zero-based to match pedgrp's ordering
                indices.Add(Mathf.Max(0, data.islands[i] - 1));
            }

            pool.cityMins = mins.ToArray();
            pool.cityMaxs = maxs.ToArray();
            pool.cityIndices = indices.ToArray();

            Debug.Log($"City-aware population: {data.Count} regions from map.zon");
        }

        /// <summary>
        /// Gives the NPC pool the district data it needs to shape a crowd.
        ///
        /// This is the last link in the chain: zone bounds say which district a point is in, the recovered
        /// categories say what kind of district that is, and popcycle says who belongs there at this hour on
        /// this kind of day. Without all four the pool falls back to placing anyone anywhere.
        /// </summary>
        private static void ApplyPopCycle(GtaNpcPool pool)
        {
            var zones = AssetDatabase.LoadAssetAtPath<GtaZoneData>("Assets/ExportedAssets/ZoneData.asset");
            var popCycle = AssetDatabase.LoadAssetAtPath<GtaPopCycleData>(
                "Assets/ExportedAssets/PopCycle.asset");

            if (null == zones || zones.Count == 0 || null == popCycle || popCycle.groupPercent == null)
            {
                Debug.LogWarning("No zone or popcycle data - the crowd will not vary by district");
                return;
            }

            pool.zoneMins = zones.mins;
            pool.zoneMaxs = zones.maxs;
            pool.zoneTypes = zones.zoneTypes;
            pool.zoneVolumes = zones.volumes;
            pool.groupPercent = popCycle.groupPercent;
            pool.dayNight = s_dayNight;

            int typedZones = 0;
            if (zones.zoneTypes != null)
            {
                foreach (int t in zones.zoneTypes)
                {
                    if (t >= 0)
                        typedZones++;
                }
            }

            Debug.Log($"District population wired: {typedZones}/{zones.Count} zones categorised, " +
                $"clock {(s_dayNight != null ? "connected" : "MISSING")}");
        }

        private static Text s_nowPlayingText;

        /// <summary>
        /// Builds the car radio.
        ///
        /// The station URLs have to be baked here: Udon cannot construct a VRCUrl at runtime, so every
        /// station a player can ever tune to must exist in the scene before the world is uploaded. They come
        /// from a plain text list so adding a station is a paste and a rebuild.
        /// </summary>
        private static int CreateRadio(Vector3 spawnPoint)
        {
            var stations = RadioStationList.Read();

            if (stations.Count == 0)
            {
                Debug.LogWarning("No radio stations configured - skipping the radio");
                return 0;
            }

            var root = new GameObject("Radio");
            root.transform.position = spawnPoint;

            // AVPro handles long streamed recordings better than Unity's player, which matters when a
            // station is a two hour video that has to be seeked into.
            var player = root.AddComponent<VRC.SDK3.Video.Components.AVPro.VRCAVProVideoPlayer>();

            var output = root.AddComponent<AudioSource>();
            output.playOnAwake = false;
            output.loop = false;
            // the player's own radio, so it is not positioned in the world
            output.spatialBlend = 0f;
            output.volume = 0.7f;

            var names = new List<string>();
            var urls = new List<VRC.SDKBase.VRCUrl>();
            var offsets = new List<float>();

            var trackStations = new List<int>();
            var trackStarts = new List<float>();
            var trackTitles = new List<string>();

            for (int i = 0; i < stations.Count; i++)
            {
                var station = stations[i];

                names.Add(station.Name);
                urls.Add(new VRC.SDKBase.VRCUrl(station.Url));

                // Stagger the playheads. Without this every station would sit at the same offset into its
                // own recording, so switching between them at the top of the hour would land on the same
                // moment each time - which is not how a set of independent stations behaves.
                offsets.Add(i * 617f);

                for (int t = 0; t < station.TrackStarts.Count; t++)
                {
                    trackStations.Add(i);
                    trackStarts.Add(station.TrackStarts[t]);
                    trackTitles.Add(station.TrackTitles[t]);
                }
            }

            var radio = AddUdonBehaviour<GtaRadio>(root);
            radio.stationNames = names.ToArray();
            radio.stationUrls = urls.ToArray();
            radio.stationOffsets = offsets.ToArray();
            radio.videoPlayer = player;
            radio.output = output;
            radio.nowPlayingText = s_nowPlayingText;

            if (trackStations.Count > 0)
            {
                radio.trackStations = trackStations.ToArray();
                radio.trackStarts = trackStarts.ToArray();
                radio.trackTitles = trackTitles.ToArray();
            }

            UdonSharpEditorUtility.CopyProxyToUdon(radio);

            // Every seat is told about the radio here, because the radio is built after the vehicles.
            // The seat reports when the player sits down, which is far cheaper than the radio scanning
            // hundreds of seats every frame to find out.
            int wired = 0;
            foreach (var seat in s_playerSeats)
            {
                if (seat == null)
                    continue;

                seat.radio = radio;
                UdonSharpEditorUtility.CopyProxyToUdon(seat);
                wired++;
            }

            Debug.Log($"Radio: {names.Count} stations, {trackStations.Count} track timecodes, " +
                $"{wired} seats wired");

            return names.Count;
        }

        /// <summary>
        /// Creates the one behaviour that receives vehicle input, and points every seat at it.
        ///
        /// Run after all the vehicles exist, since it has to reach every seat that was made.
        /// </summary>
        private static int WireVehicleInput()
        {
            var go = new GameObject("VehicleInput");

            var input = AddUdonBehaviour<GtaVehicleInput>(go);
            UdonSharpEditorUtility.CopyProxyToUdon(input);

            int wired = 0;

            foreach (var seat in s_playerSeats)
            {
                if (seat == null)
                    continue;

                seat.input = input;
                UdonSharpEditorUtility.CopyProxyToUdon(seat);
                wired++;
            }

            Debug.Log($"Damage panels skipped (damaged mesh not exported): {s_damagePanelsSkippedNoMesh}");

            Debug.Log($"Vehicle input routed through one behaviour for {wired} seats " +
                $"(was one input handler per seat, so every keypress ran {wired} Udon programs)");

            return wired;
        }

        private const string PainSoundFolder = ExportRoot + "/Audio/SFX/PAIN_A";

        // cached per sex, since every ped in the pool loads the same set
        private static AudioClip[] s_malePanicSounds;
        private static AudioClip[] s_femalePanicSounds;

        /// <summary>
        /// Loads the game's panic screams.
        ///
        /// The bank and index ranges are taken from the original's PanicState rather than chosen by ear:
        /// male peds use bank 2 indices 69-100, female peds bank 1 indices 88-130. Exported clips are named
        /// "{bank}_{index}", so the range maps directly onto asset names.
        /// </summary>
        private static AudioClip[] LoadPanicSounds(bool isMale)
        {
            if (isMale && s_malePanicSounds != null)
                return s_malePanicSounds;

            if (!isMale && s_femalePanicSounds != null)
                return s_femalePanicSounds;

            int bank = isMale ? 2 : 1;
            int first = isMale ? 69 : 88;
            int last = isMale ? 100 : 130;

            var clips = new System.Collections.Generic.List<AudioClip>();

            for (int index = first; index <= last; index++)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(
                    $"{PainSoundFolder}/{bank}_{index}.asset");

                if (clip != null)
                    clips.Add(clip);
            }

            if (clips.Count == 0)
                Debug.LogWarning($"No panic sounds found in {PainSoundFolder} for bank {bank}");

            var result = clips.ToArray();

            if (isMale)
                s_malePanicSounds = result;
            else
                s_femalePanicSounds = result;

            return result;
        }

        /// <summary>
        /// How close world geometry must be for a teleport destination to be offered.
        ///
        /// Measured: real exterior destinations are within 87m of something; interiors, which are not in
        /// the export, are 976m or more from anything. Anywhere in between separates them cleanly.
        /// </summary>
        /// <summary>
        /// Feeds the game's own authored lighting into the day/night cycle.
        ///
        /// timecyc.dat holds a set of keyframes per weather type. Only one weather is used - the world has
        /// no weather system - and the clear Los Santos entry is the iconic San Andreas look, so that is
        /// the one taken unless another is named.
        /// </summary>
        private static void ApplyTimeCycle(GtaDayNightCycle dayNight)
        {
            var data = AssetDatabase.LoadAssetAtPath<GtaTimeCycleData>(
                "Assets/ExportedAssets/TimeCycle.asset");

            if (null == data || data.WeatherCount == 0)
            {
                Debug.LogWarning("No time cycle data exported - day/night will use its built-in colours");
                return;
            }

            int weather = 0;
            for (int i = 0; i < data.weatherNames.Length; i++)
            {
                if (data.weatherNames[i].StartsWith("EXTRASUNNY_LA", System.StringComparison.OrdinalIgnoreCase))
                {
                    weather = i;
                    break;
                }
            }

            int count = GtaTimeCycleData.KeyframeCount;
            int start = weather * count;

            if (start + count > data.ambient.Length)
            {
                Debug.LogWarning("Time cycle data is shorter than expected - skipping");
                return;
            }

            var sky = new Color[count];
            var sun = new Color[count];
            var horizon = new Color[count];
            var fog = new float[count];

            for (int k = 0; k < count; k++)
            {
                sky[k] = data.skyTop[start + k];
                // SunCore, not Dir: Dir is white at every hour, while SunCore carries the orange of dawn
                // and dusk that makes the light read as a time of day
                sun[k] = data.sunCore[start + k];
                horizon[k] = data.skyBottom[start + k];
                fog[k] = data.fogStart[start + k];
            }

            dayNight.keyframeHours = data.keyframeHours;
            dayNight.skyColors = sky;
            dayNight.sunColors = sun;
            dayNight.horizonColors = horizon;
            dayNight.fogStarts = fog;

            Debug.Log($"Day/night using timecyc weather '{data.weatherNames[weather]}' ({count} keyframes)");
        }

        /// <summary>
        /// Collects each cell's street lamp glows under a single child object.
        ///
        /// The glows are added to the lamp prefabs, so a cell can hold hundreds of them scattered through
        /// its hierarchy. Gathering them under one object per cell turns "switch the street lighting on"
        /// into one SetActive per cell instead of one per lamp, which is the difference between something
        /// Udon can do at dusk and something it cannot.
        ///
        /// Reparenting keeps world position, and lamp posts do not move, so nothing is lost by moving them
        /// out of their own prefab's hierarchy.
        /// </summary>
        private static GameObject[] GroupCellLamps(System.Collections.Generic.List<GameObject> cellRoots)
        {
            var groups = new GameObject[cellRoots.Count];
            int totalLamps = 0;
            int cellsWithLamps = 0;
            int warningsDisabled = 0;

            for (int i = 0; i < cellRoots.Count; i++)
            {
                GameObject cell = cellRoots[i];
                if (null == cell)
                    continue;

                var lamps = new System.Collections.Generic.List<Transform>();

                foreach (var t in cell.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name.StartsWith("LampGlow"))
                    {
                        lamps.Add(t);
                        continue;
                    }

                    // Street lamps carry a second glow the exporter did not create: the game's own 2dfx
                    // corona, imported as a LightSource sprite. Grouping only LampGlow left those switched
                    // on permanently, which is why lamps were still lit at eleven in the morning.
                    if (!t.name.StartsWith("LightSource"))
                        continue;

                    Transform model = t.parent;
                    if (model == null)
                        continue;

                    // signals are driven by GtaTrafficSignals, not by the time of day
                    if (LooksLikeTrafficSignal(model))
                        continue;

                    // A model wearing only red glows is a warning light - a level crossing or a barrier -
                    // and those light when a train is coming, not when the sun goes down. There is no train
                    // simulation yet, so they stay dark rather than burning red all night.
                    if (IsWarningOnlyLight(model))
                    {
                        t.gameObject.SetActive(false);
                        warningsDisabled++;
                        continue;
                    }

                    lamps.Add(t);
                }

                if (lamps.Count == 0)
                    continue;

                var group = new GameObject("Lamps");
                group.transform.SetParent(cell.transform, false);

                foreach (var lamp in lamps)
                    lamp.SetParent(group.transform, true);

                // off by default; the streamer lights them after dusk
                group.SetActive(false);

                groups[i] = group;
                totalLamps += lamps.Count;
                cellsWithLamps++;
            }

            Debug.Log($"Street lighting grouped: {totalLamps} lamps across {cellsWithLamps} cells " +
                $"({warningsDisabled} crossing/warning lights switched off - no trains yet)");

            return groups;
        }

        /// <summary>
        /// Wires up the traffic signals in each streaming cell.
        ///
        /// Signals are found by shape rather than by model name: a traffic light is any object carrying
        /// glow sprites in both red and green. San Andreas has at least eight signal models across the
        /// three cities plus mission-specific variants, and a name list would quietly miss the ones it did
        /// not know about - which is how they all ended up permanently lit in the first place.
        ///
        /// Which group a signal belongs to comes from the way it faces, so the pair facing each other along
        /// a street change together while the cross street shows red.
        /// </summary>
        private static int GroupCellSignals(System.Collections.Generic.List<GameObject> cellRoots)
        {
            int totalSignals = 0;
            int totalLamps = 0;
            int controllers = 0;

            for (int i = 0; i < cellRoots.Count; i++)
            {
                GameObject cell = cellRoots[i];
                if (null == cell)
                    continue;

                var aRed = new List<GameObject>();
                var aAmber = new List<GameObject>();
                var aGreen = new List<GameObject>();
                var bRed = new List<GameObject>();
                var bAmber = new List<GameObject>();
                var bGreen = new List<GameObject>();

                foreach (var renderer in cell.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    Transform glow = renderer.transform;

                    if (!glow.name.StartsWith("LightSource"))
                        continue;

                    // the model the glow belongs to, which is what carries the facing
                    Transform signal = glow.parent;
                    if (signal == null)
                        continue;

                    int colour = ClassifySignalColour(renderer.color);
                    if (colour < 0)
                        continue;

                    // Only treat this as a signal if the same model also carries a lens of another
                    // colour. A lone red glow is a warning lamp or a radio mast, not a traffic light.
                    if (!LooksLikeTrafficSignal(signal))
                        continue;

                    // Facing, snapped to a quarter turn. Signals across a junction from one another differ
                    // by 180 degrees and share a phase; the cross street differs by 90 and takes the other.
                    float yaw = signal.eulerAngles.y;
                    int quarter = Mathf.RoundToInt(yaw / 90f) & 1;

                    if (quarter == 0)
                    {
                        if (colour == 0) aRed.Add(glow.gameObject);
                        else if (colour == 1) aAmber.Add(glow.gameObject);
                        else aGreen.Add(glow.gameObject);
                    }
                    else
                    {
                        if (colour == 0) bRed.Add(glow.gameObject);
                        else if (colour == 1) bAmber.Add(glow.gameObject);
                        else bGreen.Add(glow.gameObject);
                    }

                    totalLamps++;
                }

                if (aRed.Count + aAmber.Count + aGreen.Count + bRed.Count + bAmber.Count + bGreen.Count == 0)
                    continue;

                var go = new GameObject("TrafficSignals");
                go.transform.SetParent(cell.transform, false);

                var signals = AddUdonBehaviour<GtaTrafficSignals>(go);

                // The cycle does not exist yet - it is created with the other world systems, further down -
                // so the behaviour is kept and given its cycle there. Assigning it here would silently
                // store null and every signal would stay on whatever it loaded with. This ordering trap has
                // bitten the clock-driven systems repeatedly, so the wiring is done where the source is.
                s_signals.Add(signals);
                signals.aRed = aRed.ToArray();
                signals.aAmber = aAmber.ToArray();
                signals.aGreen = aGreen.ToArray();
                signals.bRed = bRed.ToArray();
                signals.bAmber = bAmber.ToArray();
                signals.bGreen = bGreen.ToArray();

                controllers++;
                totalSignals += (aRed.Count + bRed.Count);
            }

            Debug.Log($"Traffic signals: {totalLamps} lenses on ~{totalSignals} signals, " +
                $"cycled by {controllers} cells");

            return controllers;
        }

        /// <summary> Red, amber or green from a glow sprite's tint; -1 for anything else. </summary>
        private static int ClassifySignalColour(Color c)
        {
            // amber first: it is bright in both red and green, so a red-versus-green test would claim it
            if (c.r > 0.6f && c.g > 0.5f && c.b < 0.5f)
                return 1;

            if (c.r > 0.6f && c.g < 0.4f && c.b < 0.4f)
                return 0;

            if (c.g > 0.6f && c.r < 0.4f && c.b < 0.4f)
                return 2;

            return -1;
        }

        /// <summary>
        /// Whether a model wears red glows and nothing else - a level crossing or barrier warning light.
        /// </summary>
        private static bool IsWarningOnlyLight(Transform model)
        {
            bool red = false;

            foreach (var r in model.GetComponentsInChildren<SpriteRenderer>(true))
            {
                int c = ClassifySignalColour(r.color);

                if (c == 0)
                    red = true;
                else if (c >= 0)
                    return false;
            }

            return red;
        }

        /// <summary> Whether a model wears glows in more than one signal colour. </summary>
        private static bool LooksLikeTrafficSignal(Transform signal)
        {
            bool red = false;
            bool green = false;

            foreach (var r in signal.GetComponentsInChildren<SpriteRenderer>(true))
            {
                int c = ClassifySignalColour(r.color);

                if (c == 0) red = true;
                else if (c == 2) green = true;
            }

            return red && green;
        }

        /// <summary>
        /// Pairs up a vehicle's damage panels and attaches the behaviour that swaps them.
        ///
        /// Every model carries both versions of each panel it can lose - "bonnet_ok" beside "bonnet_dam" -
        /// with the damaged one exported switched off. Matching them by that suffix is all that is needed
        /// to find the pairs; no per-model configuration is involved.
        ///
        /// Returns the number of panels found, which is zero for models with no damage states at all.
        /// </summary>
        private static int AddVehicleDamage(GameObject vehicle, float mass)
        {
            var ok = new List<GameObject>();
            var dam = new List<GameObject>();
            var centres = new List<Vector3>();

            foreach (var t in vehicle.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.EndsWith("_ok", System.StringComparison.OrdinalIgnoreCase))
                    continue;

                string stem = t.name.Substring(0, t.name.Length - 3);

                // find the matching damaged panel among the same parent's children
                Transform pair = null;

                if (t.parent != null)
                {
                    foreach (Transform sibling in t.parent)
                    {
                        if (string.Equals(sibling.name, stem + "_dam",
                            System.StringComparison.OrdinalIgnoreCase))
                        {
                            pair = sibling;
                            break;
                        }
                    }
                }

                if (pair == null)
                    continue;

                // Only pair panels whose damaged version has geometry.
                //
                // The exporter skips objects that are switched off at import, and every *_dam panel is, so
                // most of them exist in the scene with no mesh at all. Swapping one in would make the panel
                // vanish and leave a hole in the car, which is worse than not crumpling. Such panels are
                // left out until the damaged meshes are exported.
                var damagedFilter = pair.GetComponent<MeshFilter>();
                if (damagedFilter == null || damagedFilter.sharedMesh == null)
                {
                    s_damagePanelsSkippedNoMesh++;
                    continue;
                }

                ok.Add(t.gameObject);
                dam.Add(pair.gameObject);

                // where the panel sits on the car, so an impact can be matched to the nearest one
                centres.Add(vehicle.transform.InverseTransformPoint(t.position));
            }

            if (ok.Count == 0)
                return 0;

            // Thirty-two panels is the limit of the bitmask the damage state is synced as. No GTA vehicle
            // comes close - the most is around eight - but a model that did would silently corrupt the
            // mask, so it is cut off here instead.
            if (ok.Count > 32)
            {
                ok.RemoveRange(32, ok.Count - 32);
                dam.RemoveRange(32, dam.Count - 32);
                centres.RemoveRange(32, centres.Count - 32);
            }

            var damage = AddUdonBehaviour<GtaVehicleDamage>(vehicle);
            damage.okPanels = ok.ToArray();
            damage.damagedPanels = dam.ToArray();
            damage.panelCentres = centres.ToArray();

            // A heavier vehicle takes more to wreck. The game scales durability with mass, so a bus
            // shrugs off what would total a hatchback.
            damage.maxHealth = Mathf.Clamp(1000f * (mass / 1500f), 600f, 4000f);

            UdonSharpEditorUtility.CopyProxyToUdon(damage);

            return ok.Count;
        }

        /// <summary>
        /// Where a parked car's origin goes so its wheels rest on the road.
        ///
        /// The original spawns a car at position - up * AverageWheelHeight and lets physics settle it. That
        /// settling matters: recorded car heights are only approximate, and measured against the world's
        /// colliders about half of the lifted cars were still more than 15 cm into the road. Parked cars
        /// here are kinematic, so nothing settles them, and the correction has to be made at build time.
        ///
        /// A ray is dropped onto the world from above the car and the origin is set so the lowest wheel
        /// touches the surface it hits. If nothing is found close to where the car is expected to be - a
        /// car in an interior, under an overpass, or over a gap in the collision - it falls back to the
        /// original's lift rather than snapping to a surface that may belong to a different level.
        /// </summary>
        private static Vector3 PlaceOnGround(GameObject vehicle, Vector3 position, out bool snapped)
        {
            snapped = false;

            float avgWheelY = AverageWheelHeight(vehicle);
            Vector3 lifted = position - Vector3.up * avgWheelY;

            // lowest point of any wheel, relative to the origin (vehicle is still at the origin here)
            float bottom = 0f;
            bool haveWheel = false;
            foreach (var r in vehicle.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.name != "wheel")
                    continue;

                float y = r.bounds.min.y - vehicle.transform.position.y;
                if (!haveWheel || y < bottom)
                    bottom = y;
                haveWheel = true;
            }

            if (!haveWheel)
                return lifted;

            // start just above the car's body rather than far overhead, so canopies and upper floors are
            // not mistaken for the road
            Vector3 probe = lifted + Vector3.up * 1.4f;
            int mask = LayerMask.GetMask(VRChatLayerSetup.LayerEnvironment);

            if (!Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 4f, mask, QueryTriggerInteraction.Ignore))
                return lifted;

            float expectedGround = lifted.y + bottom;

            // a surface a metre or more from where the car should be is a different level, not the road
            if (Mathf.Abs(hit.point.y - expectedGround) > 1.0f)
                return lifted;

            snapped = true;
            return new Vector3(position.x, hit.point.y - bottom + 0.02f, position.z);
        }

        /// <summary>
        /// Mean height of a vehicle's wheel dummies relative to its origin (negative: they sit below it).
        ///
        /// Measured while the vehicle is still at the origin, so a dummy's world height is its local
        /// height. Uses the same frames the original does - wheel_*_dummy - and falls back to zero for a
        /// model with none, which places it at the raw position rather than somewhere arbitrary.
        /// </summary>
        private static float AverageWheelHeight(GameObject vehicle)
        {
            float sum = 0f;
            int count = 0;

            foreach (var t in vehicle.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith("wheel_", System.StringComparison.OrdinalIgnoreCase)
                    && t.name.EndsWith("_dummy", System.StringComparison.OrdinalIgnoreCase))
                {
                    sum += vehicle.transform.InverseTransformPoint(t.position).y;
                    count++;
                }
            }

            return count > 0 ? sum / count : 0f;
        }

        private static int s_damagePanelsSkippedNoMesh;

        private static GtaZoneData s_zones;
        private static GtaPopCycleData s_popCycle;
        private static GtaVehicleGroupData s_vehicleGroups;

        /// <summary> Representative time used when baking parked cars: noon on a weekday. </summary>
        private const int ParkedSlot = 6;
        private const int ParkedDayKind = 0;

        private static void LoadPopulationData()
        {
            if (s_zones == null)
                s_zones = AssetDatabase.LoadAssetAtPath<GtaZoneData>("Assets/ExportedAssets/ZoneData.asset");

            if (s_popCycle == null)
                s_popCycle = AssetDatabase.LoadAssetAtPath<GtaPopCycleData>("Assets/ExportedAssets/PopCycle.asset");

            if (s_vehicleGroups == null)
                s_vehicleGroups = AssetDatabase.LoadAssetAtPath<GtaVehicleGroupData>(
                    "Assets/ExportedAssets/VehicleGroups.asset");
        }

        /// <summary> Population category of the smallest district containing a point, or -1. </summary>
        private static int ZoneTypeAt(Vector3 position)
        {
            if (s_zones == null || s_zones.zoneTypes == null)
                return -1;

            float smallest = 0f;
            int best = -1;

            for (int i = 0; i < s_zones.Count; i++)
            {
                Vector3 min = s_zones.mins[i];
                Vector3 max = s_zones.maxs[i];

                if (position.x < min.x || position.x > max.x) continue;
                if (position.y < min.y || position.y > max.y) continue;
                if (position.z < min.z || position.z > max.z) continue;

                if (i >= s_zones.zoneTypes.Length || s_zones.zoneTypes[i] < 0)
                    continue;

                float volume = s_zones.volumes != null && i < s_zones.volumes.Length ? s_zones.volumes[i] : 0f;

                if (best < 0 || volume < smallest)
                {
                    smallest = volume;
                    best = i;
                }
            }

            return best >= 0 ? s_zones.zoneTypes[best] : -1;
        }

        /// <summary>
        /// Picks a vehicle the way the game would for this district.
        ///
        /// popcycle says what mix of population a district has; cargrp says which vehicles go with each
        /// part of that mix. So a business district yields saloons and taxis, a farming one pickups and
        /// tractors. Choosing from a hand-filtered list of "ordinary" cars, as this used to, produced the
        /// same anonymous traffic everywhere.
        ///
        /// The choice is seeded from the placement's own position so it is stable across exports.
        /// </summary>
        private static string PickZoneVehicle(int zoneType, int seed)
        {
            if (s_popCycle == null || s_vehicleGroups == null || zoneType < 0)
                return null;

            // weighted pick over the district's population groups
            int total = 0;
            for (int g = 0; g < GtaPopCycleData.GroupCount; g++)
                total += s_popCycle.groupPercent[GtaPopCycleData.Index(zoneType, ParkedSlot, ParkedDayKind, g)];

            if (total <= 0)
                return null;

            int roll = Mathf.Abs(seed) % total;
            int chosenGroup = -1;

            for (int g = 0; g < GtaPopCycleData.GroupCount; g++)
            {
                roll -= s_popCycle.groupPercent[GtaPopCycleData.Index(zoneType, ParkedSlot, ParkedDayKind, g)];
                if (roll < 0)
                {
                    chosenGroup = g;
                    break;
                }
            }

            if (chosenGroup < 0)
                return null;

            // the vehicle group carrying the same POPCYCLE_GROUP_ name
            string wanted = s_popCycle.groupNames[chosenGroup];

            for (int vg = 0; vg < s_vehicleGroups.GroupCount; vg++)
            {
                string name = s_vehicleGroups.groupNames[vg];

                int paren = name.IndexOf('(');
                if (paren >= 0)
                    name = name.Substring(0, paren);

                name = name.Trim();

                const string prefix = "POPCYCLE_GROUP_";
                if (name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(prefix.Length);

                if (!name.Equals(wanted, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                int count = s_vehicleGroups.groupCounts[vg];
                if (count <= 0)
                    continue;

                int start = s_vehicleGroups.groupStarts[vg];
                return s_vehicleGroups.memberModels[start + (Mathf.Abs(seed / 7) % count)];
            }

            return null;
        }

        /// <summary>
        /// Gives each cell a behaviour that decides which of its parking spaces hold a car.
        ///
        /// One per cell rather than one per car: the decision is per-district and per-hour, so it belongs
        /// where the district is known, and a cell only re-decides when it loads.
        /// </summary>
        private static int CreateParkingDeciders(
            Dictionary<Transform, List<GameObject>> candidatesByCell,
            Dictionary<Transform, int> zoneTypeByCell)
        {
            var popCycle = AssetDatabase.LoadAssetAtPath<GtaPopCycleData>("Assets/ExportedAssets/PopCycle.asset");

            if (null == popCycle || popCycle.maxCars == null)
            {
                Debug.LogWarning("No popcycle data - every parking space will simply be filled");
                return 0;
            }

            // the busiest district, used to scale the rest
            int busiest = 1;
            for (int t = 0; t < GtaPopCycleData.ZoneTypeCount; t++)
            {
                for (int slot = 0; slot < GtaPopCycleData.SlotCount; slot++)
                {
                    int v = popCycle.maxCars[GtaPopCycleData.DensityIndex(t, slot, 0)];
                    if (v > busiest)
                        busiest = v;
                }
            }

            int made = 0;

            foreach (var pair in candidatesByCell)
            {
                Transform cell = pair.Key;
                List<GameObject> cars = pair.Value;

                if (cell == null || cars.Count == 0)
                    continue;

                var go = new GameObject("ParkedSpaces");
                go.transform.SetParent(cell, false);

                var decider = AddUdonBehaviour<GtaParkedCarSpaces>(go);
                decider.candidates = cars.ToArray();
                decider.zoneType = zoneTypeByCell.ContainsKey(cell) ? zoneTypeByCell[cell] : -1;
                decider.maxCars = popCycle.maxCars;
                decider.busiestBudget = busiest;
                decider.dayNight = s_dayNight;
                UdonSharpEditorUtility.CopyProxyToUdon(decider);

                made++;
            }

            return made;
        }

        private static GtaVehicleHandlingData s_handlingForExtras;

        /// <summary>
        /// Picks which optional "extra" parts a spawned vehicle wears.
        ///
        /// GTA vehicle models carry every variant of their optional parts as sibling frames named extra1,
        /// extra2 and so on - a cargo truck's advertising boards, a pickup's roll bar, a taxi's roof sign.
        /// The game activates at most two of them per vehicle when it spawns, so two trucks on the same
        /// street carry different advertising. Exporting the model leaves all of them switched on at once,
        /// which is why every poster showed on one truck.
        ///
        /// The choice is made per instance rather than per prefab, and seeded from the placement's own
        /// position, so a street gets a mix of variants that stays the same across exports.
        ///
        /// vehicles.ide packs the rule into a 32-bit word: two 16-bit halves, each holding up to three
        /// candidate part indices in its low twelve bits and a rule in its high four.
        /// </summary>
        private static void SelectVehicleExtras(GameObject vehicle, string modelName, int seed)
        {
            if (null == vehicle)
                return;

            // gather the extra frames; nothing to do for the majority of models that have none
            var extras = new List<Transform>();

            foreach (var t in vehicle.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith("extra", System.StringComparison.OrdinalIgnoreCase))
                    extras.Add(t);
            }

            if (extras.Count == 0)
                return;

            int word = CompRulesFor(modelName);

            int extraA = word & 0xFFFF;
            int extraB = (word >> 16) & 0xFFFF;

            // The original picks into the same variable for both halves, so its second pick discards its
            // first and a vehicle can never wear two extras. Kept separate here.
            int first = extraA != 0 ? ChooseExtra(extraA >> 12, extraA & 0x0FFF, extras.Count, seed) : -1;
            int second = extraB != 0 ? ChooseExtra(extraB >> 12, extraB & 0x0FFF, extras.Count, seed * 31 + 7) : -1;

            // no rule matched: wear exactly one, rather than all of them
            if (first < 0 && second < 0)
                first = Mathf.Abs(seed) % extras.Count + 1;

            foreach (var t in extras)
            {
                bool wanted = t.name.Equals("extra" + first, System.StringComparison.OrdinalIgnoreCase)
                    || t.name.Equals("extra" + second, System.StringComparison.OrdinalIgnoreCase);

                t.gameObject.SetActive(wanted);
            }
        }

        /// <summary> Applies one comp rule, returning the extra index to wear or -1 for none. </summary>
        private static int ChooseExtra(int rule, int comps, int extraCount, int seed)
        {
            const int AllowAlways = 1;
            const int OnlyWhenRaining = 2;
            const int MaybeHide = 3;
            const int FullRandom = 4;

            // there is no weather yet, so rain-only parts are never worn
            if (rule == OnlyWhenRaining)
                return -1;

            if (rule == AllowAlways || rule == MaybeHide)
            {
                // count the filled nibbles; 0xF marks an unused slot
                int count = 0;
                int scan = comps;

                while (scan != 0)
                {
                    if ((scan & 0xF) != 0xF)
                        count++;

                    scan >>= 4;
                }

                if (count <= 0)
                    return -1;

                // MAYBE_HIDE gets one extra outcome: wear nothing at all
                int span = rule == MaybeHide ? count + 1 : count;
                int pick = Mathf.Abs(seed) % span;

                if (rule == MaybeHide && pick == count)
                    return -1;

                return (comps >> (4 * pick)) & 0xF;
            }

            if (rule == FullRandom)
                return Mathf.Abs(seed) % Mathf.Max(1, extraCount) + 1;

            return -1;
        }

        /// <summary> The comp-rules word for a model, or 0 when it has none. </summary>
        private static int CompRulesFor(string modelName)
        {
            if (s_handlingForExtras == null)
                s_handlingForExtras = AssetDatabase.LoadAssetAtPath<GtaVehicleHandlingData>(VehicleHandlingPath);

            if (s_handlingForExtras == null || s_handlingForExtras.compRules == null)
                return 0;

            for (int i = 0; i < s_handlingForExtras.Count; i++)
            {
                if (i < s_handlingForExtras.compRules.Length
                    && string.Equals(s_handlingForExtras.modelNames[i], modelName,
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    return s_handlingForExtras.compRules[i];
                }
            }

            return 0;
        }

        private const string GeneratedUiFolder = "Assets/ExportedAssets/UI";

        /// <summary>
        /// Builds the small UI shapes the HUD needs, as real sprite assets.
        ///
        /// Unity ships no round mask and no arrow, and an Image with no sprite draws a filled rectangle -
        /// which is why the radar was square with a square dot in the middle of it. Both shapes are drawn
        /// into a texture here and saved, so the scene references an asset rather than something rebuilt at
        /// load time.
        /// </summary>
        private static Sprite LoadOrCreateSprite(string name, int size, bool arrow)
        {
            string path = $"{GeneratedUiFolder}/{name}.png";

            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (existing != null)
                return existing;

            if (!AssetDatabase.IsValidFolder(GeneratedUiFolder))
                System.IO.Directory.CreateDirectory(GeneratedUiFolder);

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];

            float half = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = (x + 0.5f - half) / half;
                    float py = (y + 0.5f - half) / half;

                    float alpha;

                    if (arrow)
                    {
                        // A triangle pointing up: inside when the point is above the base and within the
                        // two sloping edges, which converge at the top.
                        float t = Mathf.InverseLerp(1f, -0.7f, py);
                        float width = Mathf.Lerp(0f, 0.62f, t);

                        alpha = (py <= 1f && py >= -0.7f && Mathf.Abs(px) <= width) ? 1f : 0f;

                        // a shallow notch in the base, so it reads as an arrow rather than a cone
                        if (py < -0.35f && Mathf.Abs(px) < Mathf.Lerp(0f, 0.30f, Mathf.InverseLerp(-0.35f, -0.7f, py)))
                            alpha = 0f;
                    }
                    else
                    {
                        // a circle, with one pixel of feathering so the crop edge is not stepped
                        float d = Mathf.Sqrt(px * px + py * py);
                        alpha = Mathf.Clamp01((1f - d) * half);
                    }

                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();

            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private const float DestinationGeometryRadius = 120f;

        private const int TeleportRows = 10;

        /// <summary>
        /// Builds the fast-travel board: a world-space panel listing GTA locations the player can jump to.
        ///
        /// Udon cannot create objects at runtime, so the rows are built here and their labels rewritten as
        /// the page changes. Button clicks are wired as persistent listeners onto the backing UdonBehaviour,
        /// which is how a Unity UI event reaches Udon.
        /// </summary>
        private static int CreateTeleportMenu(Vector3 spawnPoint)
        {
            var data = AssetDatabase.LoadAssetAtPath<GtaTeleportData>(
                "Assets/ExportedAssets/TeleportDestinations.asset");

            if (null == data || data.Count == 0)
            {
                Debug.LogWarning("No teleport destinations exported - skipping the teleport menu");
                return 0;
            }

            var root = new GameObject("TeleportMenu");
            // stand it just beside the spawn, facing back toward the player
            root.transform.position = spawnPoint + new Vector3(2.5f, 0f, 2f);
            root.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            var menu = AddUdonBehaviour<GtaTeleportMenu>(root);
            var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(menu);

            Text[] labels;
            GameObject[] buttons;
            Text pageLabel;

            BuildPagedPanel(root, backing, "FAST TRAVEL", "TeleportRow",
                nameof(GtaTeleportMenu.PreviousPage), nameof(GtaTeleportMenu.NextPage),
                out labels, out buttons, out pageLabel);

            // Offer only destinations the world actually reaches.
            //
            // The enex list covers interiors as well as street corners, and whether a given interior's
            // geometry is in the export is not something to assume - an earlier version of this guessed and
            // guessed wrong in both directions.
            //
            // Finding a nearby CELL is not enough: cells are 200m and blanket the whole map, so nearly any
            // on-map coordinate has one. What matters is whether there is actual geometry where the player
            // would land, so this looks for a real object within a short distance of the destination.
            //
            // The radius was chosen from measurement, not taste. Interiors are stacked about a kilometre
            // above the streets and are not in the export at all, so their nearest object is 976-1190m away
            // (the ground below them). Genuine exterior destinations sit at most ~87m from something. A
            // threshold in that gap keeps every real destination and drops every unreachable one.
            var names = new System.Collections.Generic.List<string>();
            var positions = new System.Collections.Generic.List<Vector3>();
            var headings = new System.Collections.Generic.List<float>();

            bool canVerify = s_cellRoots != null && s_cellRoots.Length > 0;
            int rejected = 0;

            for (int i = 0; i < data.Count; i++)
            {
                if (canVerify && !HasGeometryNear(data.positions[i], DestinationGeometryRadius))
                {
                    // report how far the nearest object actually is, so a rejection can be told apart from
                    // a radius that is merely too tight
                    if (rejected < 8)
                    {
                        Debug.Log($"  dropped '{data.names[i]}' at {data.positions[i]} " +
                            $"(interior {data.interiors[i]}) - nearest object " +
                            $"{NearestGeometryDistance(data.positions[i]):F1}m away");
                    }

                    rejected++;
                    continue;
                }

                names.Add(data.names[i]);
                positions.Add(data.positions[i]);
                headings.Add(data.headings[i]);
            }

            if (rejected > 0)
                Debug.Log($"Teleport destinations: {rejected} dropped for having no world geometry nearby");

            menu.destinationNames = names.ToArray();
            menu.destinationPositions = positions.ToArray();
            menu.destinationHeadings = headings.ToArray();
            menu.rowLabels = labels;
            menu.rowButtons = buttons;
            menu.pageLabel = pageLabel;
            UdonSharpEditorUtility.CopyProxyToUdon(menu);

            return names.Count;
        }

        private static void AddPagingButton(
            GameObject parent, string name, string caption, Font font, Vector2 position,
            VRC.Udon.UdonBehaviour backing, string eventName)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);

            var image = go.AddComponent<Image>();
            image.color = new Color(0.22f, 0.26f, 0.32f, 1f);

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(90f, 50f);
            rect.anchoredPosition = position;

            var button = go.AddComponent<Button>();
            button.targetGraphic = image;

            UnityEventTools.AddStringPersistentListener(
                button.onClick, new UnityAction<string>(backing.SendCustomEvent), eventName);

            AddLabel(go, "Label", caption, font, 28, Vector2.zero, new Vector2(90f, 50f),
                TextAnchor.MiddleCenter);
        }

        private static Image AddPanelImage(GameObject parent, Color color, Vector2 position, Vector2 size)
        {
            var go = new GameObject("Panel");
            go.transform.SetParent(parent.transform, false);
            go.transform.SetAsFirstSibling();

            var image = go.AddComponent<Image>();
            image.color = color;

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            return image;
        }

        private static Text AddLabel(
            GameObject parent, string name, string content, Font font, int fontSize, Vector2 position,
            Vector2 size, TextAnchor alignment)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);

            var text = go.AddComponent<Text>();
            text.text = content;
            text.font = font;
            text.fontSize = fontSize;
            text.color = Color.white;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.anchoredPosition = position;

            return text;
        }

        /// <summary>
        /// Adds the VRCSceneDescriptor every VRChat world requires, with spawn points spread across the
        /// whole map - one per populated GTA area - and a random spawn order.
        ///
        /// VRChat re-rolls the spawn on every respawn when spawnOrder is Random, so hitting respawn drops
        /// you somewhere new. Spawns are sampled onto the nav mesh, so none of them land in geometry.
        /// </summary>
        /// <param name="constrainToCenter">
        /// When world geometry only covers part of the map, spawns must be limited to that area - spawning
        /// outside it drops the player through empty space to the respawn plane, over and over.
        /// Pass a negative radius to allow spawns anywhere.
        /// </param>
        private static System.Collections.Generic.List<Transform> AddSceneDescriptor(
            GtaPathNetwork network, Vector3 fallbackSpawn, Vector3 constrainToCenter, float constrainRadius)
        {
            var descriptorGo = new GameObject("VRCWorld");
            descriptorGo.transform.position = Vector3.zero;

            var spawns = new System.Collections.Generic.List<Transform>();
            float lowestSpawnY = float.MaxValue;

            // one spawn per area gives an even spread over San Andreas rather than clustering
            for (int areaId = 0; areaId < network.areaNodeStart.Length; areaId++)
            {
                int start = network.areaNodeStart[areaId];
                int count = network.areaNodeCount[areaId];
                int spawnsInThisArea = 0;

                for (int i = 0; i < count; i++)
                {
                    int node = start + i;

                    if (!network.nodeIsPedNode[node])
                        continue;
                    if (network.nodeIsWater[node])
                        continue;
                    if (network.nodeLinkCount[node] < 2)
                        continue;

                    // stay inside the area that actually has geometry, or the player spawns over a void
                    if (constrainRadius > 0f &&
                        (network.nodePositions[node] - constrainToCenter).sqrMagnitude >
                            constrainRadius * constrainRadius)
                        continue;

                    // only keep spawns that are actually standable
                    if (!NavMesh.SamplePosition(
                            network.nodePositions[node], out NavMeshHit hit, 5f, NavMesh.AllAreas))
                        continue;

                    var spawnGo = new GameObject($"Spawn_Area{areaId}");
                    spawnGo.transform.SetParent(descriptorGo.transform);
                    spawnGo.transform.position = hit.position + Vector3.up * 0.25f;

                    spawns.Add(spawnGo.transform);
                    lowestSpawnY = Mathf.Min(lowestSpawnY, hit.position.y);
                    spawnsInThisArea++;

                    // Unconstrained, one spawn per area spreads players over the whole map. Constrained to
                    // a geometry chunk there may only be one or two areas in range, so take several.
                    if (spawnsInThisArea >= (constrainRadius > 0f ? 8 : 1))
                        break;

                    // space them out so they aren't all clustered on one street corner
                    i += 40;
                }
            }

            if (spawns.Count == 0)
            {
                // no nav-mesh-backed node found anywhere - fall back so the world is still valid
                var fallbackGo = new GameObject("Spawn_Fallback");
                fallbackGo.transform.SetParent(descriptorGo.transform);
                fallbackGo.transform.position = fallbackSpawn + Vector3.up * 0.25f;
                spawns.Add(fallbackGo.transform);
                lowestSpawnY = fallbackSpawn.y;

                Debug.LogWarning("No nav-mesh-backed spawn points found - using a single fallback spawn");
            }

            var descriptor = descriptorGo.AddComponent<VRC.SDK3.Components.VRCSceneDescriptor>();
            descriptor.spawns = spawns.ToArray();
            descriptor.spawnOrder = VRC.SDKBase.VRC_SceneDescriptor.SpawnOrder.Random;
            descriptor.RespawnHeightY = lowestSpawnY - 200f;
            descriptor.ObjectBehaviourAtRespawnHeight =
                VRC.SDKBase.VRC_SceneDescriptor.RespawnHeightBehaviour.Respawn;

            Debug.Log($"VRCSceneDescriptor added: {spawns.Count} random spawn points, " +
                $"respawn plane at y={descriptor.RespawnHeightY:F1}");

            return spawns;
        }

        /// <summary>
        /// Builds a pool of peds that <see cref="GtaNpcPool"/> activates around whoever is playing.
        ///
        /// Every ped is created up front and parked inactive; the pool moves and enables them near players
        /// and recycles the ones left behind. Object count is therefore constant and known at build time,
        /// which is what VRChat wants - and it's how the world stays populated wherever you go rather than
        /// only at fixed points.
        /// </summary>
        /// <summary>
        /// Builds a pool of vehicles for <see cref="GtaTrafficPool"/> to place on road nodes near players.
        /// Vehicles get no NavMeshAgent - they follow the vehicle path graph, not the pedestrian nav mesh.
        /// </summary>
        private static int CreateTrafficPool(
            GtaPathNetworkData pathData, GtaTrafficLightController trafficLights, int poolSize)
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { VehiclePrefabsFolder });
            if (guids.Length == 0)
            {
                Debug.LogWarning("No vehicle prefabs found - skipping traffic pool");
                return 0;
            }

            var parent = new GameObject("TrafficPool");
            var pooled = new System.Collections.Generic.List<GameObject>();
            var pooledGroups = new System.Collections.Generic.List<int>();

            LoadPopulationData();

            var handlingData = AssetDatabase.LoadAssetAtPath<GtaVehicleHandlingData>(VehicleHandlingPath);
            if (null == handlingData)
                Debug.LogWarning($"No vehicle handling data at {VehicleHandlingPath} - vehicles will use defaults");

            for (int i = 0; i < poolSize; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i % guids.Length]);
                var vehiclePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (null == vehiclePrefab)
                    continue;

                var vehicle = (GameObject)PrefabUtility.InstantiatePrefab(vehiclePrefab, parent.transform);
                string modelName = vehiclePrefab.name;

                SelectVehicleExtras(vehicle, modelName, i * 486187739);

                // which district this model belongs in, so the pool can leave it parked otherwise
                pooledGroups.Add(VehicleGroupOf(modelName));

                // Physics body. Exported vehicles are bare geometry - no rigidbody, no collider - so both
                // have to be added here or vehicles pass through the world and each other.
                var body = vehicle.GetComponent<Rigidbody>();
                if (null == body)
                    body = vehicle.AddComponent<Rigidbody>();
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                // panels crumple, then it smokes, then it burns
                AddVehicleDamage(vehicle, body.mass);

                // A box sized to the model. Mesh colliders can't be non-convex and moving, and a convex
                // hull of a whole car is both expensive and a poor fit.
                AddVehicleCollider(vehicle);

                var controller = AddUdonBehaviour<GtaVehicleController>(vehicle);
                ApplyHandling(controller, handlingData, modelName);
                UdonSharpEditorUtility.CopyProxyToUdon(controller);

                var driver = AddUdonBehaviour<GtaTrafficVehicle>(vehicle);
                driver.pathNetwork = pathData;
                driver.controller = controller;
                driver.trafficLights = trafficLights;
                UdonSharpEditorUtility.CopyProxyToUdon(driver);

                var playerSeat = AddDriverSeat(vehicle, controller, driver);

                // A car driving itself with nobody at the wheel reads as broken. Occupants are visual
                // only - no agent, no AI - just a ped posed in the seat.
                // vehicles.ide says which in-vehicle animation set this class uses; without it every
                // occupant is posed as if sitting in a car, which is plainly wrong on a bike
                string animGroup = handlingData != null ? handlingData.AnimGroupOf(modelName) : string.Empty;

                AddVehicleLights(vehicle, s_dayNight);

                AddVehicleOccupants(vehicle, i, animGroup, out GameObject driverOccupant,
                    out GameObject passengerOccupant);

                // taking the wheel has to throw whoever was driving out of the car; wired after both exist
                if (playerSeat != null)
                {
                    playerSeat.driverOccupant = driverOccupant;
                    playerSeat.passengerOccupant = passengerOccupant;

                    if (driverOccupant != null)
                        playerSeat.driverAnimator = driverOccupant.GetComponentInChildren<Animator>();

                    if (passengerOccupant != null)
                        playerSeat.passengerAnimator = passengerOccupant.GetComponentInChildren<Animator>();

                    // Owns the driver's eject/walk-back/re-seat cycle. The traffic AI reads HasDriver from
                    // it, so a car with nobody at the wheel stops instead of steering itself down the road.
                    if (driverOccupant != null)
                    {
                        var seatMarker = new GameObject("DriverPedSeat");
                        seatMarker.transform.SetParent(vehicle.transform, false);
                        seatMarker.transform.localPosition = driverOccupant.transform.localPosition;
                        seatMarker.transform.localRotation = Quaternion.identity;

                        var vehicleDriver = AddUdonBehaviour<GtaVehicleDriver>(vehicle);
                        vehicleDriver.controller = controller;
                        vehicleDriver.driverPed = driverOccupant;
                        vehicleDriver.driverAnimator = driverOccupant.GetComponentInChildren<Animator>();
                        vehicleDriver.driverAgent = driverOccupant.GetComponent<NavMeshAgent>();
                        vehicleDriver.seatPoint = seatMarker.transform;
                        UdonSharpEditorUtility.CopyProxyToUdon(vehicleDriver);

                        playerSeat.vehicleDriver = vehicleDriver;

                        driver.vehicleDriver = vehicleDriver;
                        UdonSharpEditorUtility.CopyProxyToUdon(driver);
                    }

                    UdonSharpEditorUtility.CopyProxyToUdon(playerSeat);
                }

                // Vehicles must sit on Default: the player's interact raycast has to reach them, and they
                // need to collide with the player. Anything on Environment is world scenery, and anything
                // on VRChat's Player layers would be treated as a person.
                VRChatLayerSetup.SetLayerRecursive(vehicle, VRChatLayerSetup.LayerDefault);

                vehicle.SetActive(false);
                pooled.Add(vehicle);
            }

            var poolGo = new GameObject("TrafficPoolManager");
            var pool = AddUdonBehaviour<GtaTrafficPool>(poolGo);
            pool.pathNetwork = pathData;
            pool.pooledVehicles = pooled.ToArray();
            pool.vehicleGroups = pooledGroups.ToArray();
            pool.dayNight = s_dayNight;

            // the same district data the ped pool uses, so traffic and pedestrians agree about where
            // they are and what belongs there
            if (s_zones != null && s_popCycle != null)
            {
                pool.zoneMins = s_zones.mins;
                pool.zoneMaxs = s_zones.maxs;
                pool.zoneVolumes = s_zones.volumes;
                pool.zoneTypes = s_zones.zoneTypes;
                pool.groupPercent = s_popCycle.groupPercent;

                int known = 0;
                foreach (int g in pooledGroups)
                    if (g >= 0) known++;

                Debug.Log($"Traffic gated by district: {known} of {pooled.Count} pooled vehicles " +
                    $"mapped to a cargrp group");
            }
            else
            {
                Debug.LogWarning("No zone/popcycle data - traffic will spawn anywhere, as before");
            }

            UdonSharpEditorUtility.CopyProxyToUdon(pool);

            return pooled.Count;
        }

        /// <summary>
        /// Resolves the driver and passenger seat positions from the model's own seat frame.
        ///
        /// GTA vehicle models define only ONE "ped_frontseat" frame; the opposite side is derived by
        /// mirroring its x, and the driver sits front-left. Estimating seats from the model's bounding box
        /// instead - as this used to - lands occupants roughly in the back seat, because a car's bounds
        /// centre is nowhere near where a person actually sits.
        /// </summary>
        private static bool TryGetSeatPositions(
            GameObject vehicle, out Vector3 driverLocal, out Vector3 passengerLocal)
        {
            driverLocal = Vector3.zero;
            passengerLocal = Vector3.zero;

            Transform frontSeat = null;
            foreach (Transform t in vehicle.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "ped_frontseat")
                {
                    frontSeat = t;
                    break;
                }
            }

            if (null == frontSeat)
            {
                // no seat frame - fall back to a rough guess rather than dropping the occupant at the origin
                var renderers = vehicle.GetComponentsInChildren<MeshRenderer>();
                if (renderers.Length == 0)
                    return false;

                Bounds bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                    bounds.Encapsulate(renderers[i].bounds);

                Vector3 center = vehicle.transform.InverseTransformPoint(bounds.center);
                float halfWidth = bounds.size.x * 0.25f;

                driverLocal = new Vector3(center.x - halfWidth, center.y, center.z);
                passengerLocal = new Vector3(center.x + halfWidth, center.y, center.z);

                Debug.LogWarning($"{vehicle.name}: no ped_frontseat frame - using estimated seat positions");
                return true;
            }

            // frame positions are relative to their own parent, which is rarely the vehicle root
            Vector3 local = vehicle.transform.InverseTransformPoint(frontSeat.position);
            Vector3 mirrored = new Vector3(-local.x, local.y, local.z);

            if (local.x > 0f)
            {
                // the defined frame is the right-hand seat, so the driver is its mirror
                driverLocal = mirrored;
                passengerLocal = local;
            }
            else
            {
                driverLocal = local;
                passengerLocal = mirrored;
            }

            return true;
        }

        private const string SeatedControllersFolder = ExportRoot + "/AnimatorControllers/Seated";

        /// <summary>
        /// Puts a ped in the driver's seat, and a passenger in some vehicles.
        ///
        /// These are decoration: no NavMeshAgent, no AI, no colliders that could shove the car around.
        /// They are posed with the game's own in-car sitting animations, because a ped standing upright
        /// inside a car looks worse than an empty seat.
        /// </summary>
        private static void AddVehicleOccupants(
            GameObject vehicle, int vehicleIndex, string animGroup,
            out GameObject driverOccupant, out GameObject passengerOccupant)
        {
            driverOccupant = null;
            passengerOccupant = null;

            string[] pedGuids = AssetDatabase.FindAssets("t:Prefab", new[] { PedPrefabsFolder });
            if (pedGuids.Length == 0)
                return;

            // resolved from the model's own seat frame, the same source the player's seat uses
            if (!TryGetSeatPositions(vehicle, out Vector3 driverLocal, out Vector3 passengerLocal))
                return;

            // the pose they hold, and the clip they play when hauled out of the seat
            AnimationClip jacked = VehicleAnimationSet.ResolveJackedVictim(animGroup);

            var driverController = GetOrCreateSeatedController(
                VehicleAnimationSet.ResolveDriverPose(animGroup), jacked);

            driverOccupant = AddOccupant(
                vehicle, pedGuids, vehicleIndex, driverLocal, driverController, "Driver", keepAgent: true);

            // roughly every other car also carries a passenger, so traffic isn't uniformly single-occupant
            if (vehicleIndex % 2 == 0)
            {
                var passengerController = GetOrCreateSeatedController(
                    VehicleAnimationSet.ResolvePassengerPose(animGroup), jacked);

                passengerOccupant = AddOccupant(
                    vehicle, pedGuids, vehicleIndex + 7, passengerLocal, passengerController, "Passenger");
            }
        }

        private static GameObject AddOccupant(
            GameObject vehicle, string[] pedGuids, int seed, Vector3 localPosition,
            RuntimeAnimatorController seatedController, string label, bool keepAgent = false)
        {
            // vehicle occupants are placed where the vehicle is, so Los Santos models will do
            var pedPrefab = PickStreetPed(pedGuids, seed, 0);
            if (null == pedPrefab)
                return null;

            var ped = (GameObject)PrefabUtility.InstantiatePrefab(pedPrefab, vehicle.transform);
            ped.name = label;
            ped.transform.localPosition = localPosition;
            ped.transform.localRotation = Quaternion.identity;

            // Strip anything that would make an occupant behave like a pedestrian. The driver keeps its
            // agent - disabled - because an ejected driver has to walk back to the car; destroying it here
            // would leave nothing to drive that with.
            var agent = ped.GetComponent<NavMeshAgent>();
            if (agent != null)
            {
                if (keepAgent)
                {
                    agent.enabled = false;
                    agent.baseOffset = s_pedFootOffset;
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(agent);
                }
            }

            foreach (var collider in ped.GetComponentsInChildren<Collider>())
                UnityEngine.Object.DestroyImmediate(collider);

            var animator = ped.GetComponentInChildren<Animator>();
            if (animator != null && seatedController != null)
            {
                animator.runtimeAnimatorController = seatedController;
                animator.applyRootMotion = false;
            }

            return ped;
        }

        /// <summary> Animator trigger that throws an occupant out of their seat. </summary>
        public const string JackedTrigger = "Jacked";

        /// <summary> Animator trigger that returns an occupant to their seated pose. </summary>
        public const string ReseatTrigger = "Reseat";

        /// <summary>
        /// Builds (or reuses) the controller an occupant uses: the pose they hold, plus the clip that plays
        /// when they are dragged out of the seat.
        ///
        /// Both states live in one controller and are switched with a trigger, because Udon can call
        /// Animator.SetTrigger but cannot reliably swap a runtime controller.
        /// </summary>
        private static RuntimeAnimatorController GetOrCreateSeatedController(
            AnimationClip pose, AnimationClip jacked)
        {
            if (null == pose)
            {
                Debug.LogWarning("No seated animation resolved - occupants will use their default pose");
                return null;
            }

            if (!AssetDatabase.IsValidFolder(SeatedControllersFolder))
                System.IO.Directory.CreateDirectory(SeatedControllersFolder);

            string jackedName = jacked != null ? jacked.name : "none";
            string controllerPath = $"{SeatedControllersFolder}/{pose.name}__{jackedName}.controller";

            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (existing != null)
                return existing;

            var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            var stateMachine = controller.layers[0].stateMachine;

            var seated = stateMachine.AddState("Seated");
            seated.motion = pose;
            stateMachine.defaultState = seated;

            if (jacked != null)
            {
                controller.AddParameter(JackedTrigger, AnimatorControllerParameterType.Trigger);

                var thrownOut = stateMachine.AddState("Jacked");
                thrownOut.motion = jacked;

                var transition = seated.AddTransition(thrownOut);
                transition.AddCondition(AnimatorConditionMode.If, 0f, JackedTrigger);
                transition.hasExitTime = false;
                transition.duration = 0.1f;

                // and back again, for a driver who picks themselves up and returns to the car
                controller.AddParameter(ReseatTrigger, AnimatorControllerParameterType.Trigger);

                var back = thrownOut.AddTransition(seated);
                back.AddCondition(AnimatorConditionMode.If, 0f, ReseatTrigger);
                back.hasExitTime = false;
                back.duration = 0.1f;
            }

            return controller;
        }

        /// <summary>
        /// Adds the driver's seat: a VRCStation the player can sit in, plus the behaviour that redirects
        /// their movement input into the vehicle controller.
        ///
        /// Seat and exit points are derived from the vehicle's own bounds, so a bus and a sports car both
        /// seat the player somewhere sensible and drop them beside the door rather than inside the body.
        /// </summary>
        private static GtaPlayerVehicleSeat AddDriverSeat(
            GameObject vehicle, GtaVehicleController controller, GtaTrafficVehicle aiDriver)
        {
            if (!TryGetSeatPositions(vehicle, out Vector3 driverLocal, out Vector3 passengerLocal))
                return null;

            var seat = new GameObject("DriverSeat");
            seat.transform.SetParent(vehicle.transform, false);
            seat.transform.localPosition = driverLocal;

            // step out beside the vehicle, clear of its body
            var renderers = vehicle.GetComponentsInChildren<MeshRenderer>();
            Vector3 exitLocal = driverLocal;
            if (renderers.Length > 0)
            {
                Bounds bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                    bounds.Encapsulate(renderers[i].bounds);

                // Step out well clear of the bodywork, at ground level.
                //
                // Half the width only reaches the door skin, so the player was left standing inside the
                // car's own collider - which the physics engine resolves by shoving car and player apart.
                // Going a full width out plus a margin puts them beside the vehicle instead.
                Vector3 localSize = bounds.size;
                exitLocal = new Vector3(
                    driverLocal.x - (localSize.x * 0.5f + 1.2f),
                    bounds.min.y - vehicle.transform.position.y,
                    driverLocal.z);
            }

            var exit = new GameObject("ExitPoint");
            exit.transform.SetParent(vehicle.transform, false);
            exit.transform.localPosition = exitLocal;

            var station = vehicle.AddComponent<VRC.SDK3.Components.VRCStation>();
            station.stationEnterPlayerLocation = seat.transform;
            station.stationExitPlayerLocation = exit.transform;
            // Without this the player stays mobile while seated, so pressing a movement key walks them out
            // of the car instead of driving it. The Mobility enum is nested in the BASE VRCStation type
            // (VRC.SDKBase.VRCStation), not the SDK3 component, which is why it needs qualifying here.
            station.PlayerMobility = VRC.SDKBase.VRCStation.Mobility.ImmobilizeForVehicle;
            station.canUseStationFromStation = false;
            // Must be true, or VRChat binds movement to "Get Up" and WASD ejects the player instead of
            // reaching Udon - which is exactly why driving never worked. With the built-in exit disabled
            // the world owns the exit, so GtaPlayerVehicleSeat binds it to jump.
            station.disableStationExit = true;

            var driverSeat = AddUdonBehaviour<GtaPlayerVehicleSeat>(vehicle);
            driverSeat.controller = controller;
            driverSeat.aiDriver = aiDriver;
            driverSeat.station = station;

            // the radio needs to know every seat, so it can tell when the player is at a wheel
            s_playerSeats.Add(driverSeat);

            // proxy is copied by the caller, once the occupants it ejects have been wired in
            return driverSeat;
        }

        /// <summary>
        /// Copies this model's handling.cfg values onto its controller, so each vehicle drives like itself
        /// rather than every car sharing one generic feel. Falls back to the controller's defaults when a
        /// model has no handling entry.
        /// </summary>
        private static void ApplyHandling(
            GtaVehicleController controller, GtaVehicleHandlingData data, string modelName)
        {
            if (null == data)
                return;

            int index = data.IndexOf(modelName);
            if (index < 0)
            {
                Debug.LogWarning($"No handling entry for '{modelName}' - using default vehicle physics");
                return;
            }

            controller.vehicleMass = data.mass[index];
            // TransmissionMaxVel is stored raw and reads as roughly km/h (values run 130-200). Feeding it
            // in as m/s gave cars 470-720 km/h, which is why traffic launched off the road.
            controller.maxSpeed = data.maxSpeed[index] / 3.6f;
            controller.engineAccel = data.engineAccel[index];
            controller.brakeDecel = data.brakeDecel[index];
            controller.steeringLock = data.steeringLock[index];
            controller.tractionMult = data.traction[index];
            controller.dragCoefficient = data.drag[index];
        }

        /// <summary>
        /// Fits a box collider to the vehicle's rendered bounds. Bounds are world-space, so they are
        /// converted back into the vehicle's local space before being applied.
        /// </summary>
        private static void AddVehicleCollider(GameObject vehicle)
        {
            if (vehicle.GetComponent<Collider>() != null)
                return;

            var renderers = vehicle.GetComponentsInChildren<MeshRenderer>();
            if (renderers.Length == 0)
                return;

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            var box = vehicle.AddComponent<BoxCollider>();
            box.center = vehicle.transform.InverseTransformPoint(bounds.center);
            box.size = bounds.size;
        }

        private static int CreateNpcPool(GtaPathNetworkData pathData, int poolSize)
        {
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { PedPrefabsFolder });
            if (guids.Length == 0)
            {
                Debug.LogWarning("No ped prefabs found - skipping NPC pool");
                return 0;
            }

            var parent = new GameObject("NpcPool");
            var pooled = new System.Collections.Generic.List<GameObject>();
            var pooledCities = new System.Collections.Generic.List<int>();
            var pooledGroups = new System.Collections.Generic.List<int>();

            for (int i = 0; i < poolSize; i++)
            {
                // Drawn from pedgrp.dat rather than from every exported ped: most of the 264 models are
                // mission characters who never walk the streets, and a crowd built from all of them looks
                // like a cast list rather than a city.
                // round-robin the three cities so the pool can populate all of them
                int pedCity = i % 3;
                var pedPrefab = PickStreetPed(guids, i, pedCity);
                if (null == pedPrefab)
                    continue;

                var ped = (GameObject)PrefabUtility.InstantiatePrefab(pedPrefab, parent.transform);

                var agent = ped.GetComponent<NavMeshAgent>();
                if (null == agent)
                    agent = ped.AddComponent<NavMeshAgent>();
                agent.radius = 0.35f;
                agent.height = 1.9f;
                agent.speed = 1.6f;
                agent.angularSpeed = 300f;
                agent.acceleration = 12f;
                // GTA ped pivots sit at the pelvis, not the feet, so an agent placed straight onto the
                // nav mesh sinks to the waist. baseOffset lifts the model by however far its lowest
                // point is below the origin.
                agent.baseOffset = s_pedFootOffset;

                var ai = AddUdonBehaviour<GtaPedAI>(ped);
                ai.pathNetwork = pathData;
                ai.agent = agent;
                ai.animator = ped.GetComponentInChildren<Animator>();

                // A minority are hostile and will close on a player who comes near; the rest are civilians
                // who ignore you unless provoked. Without a few of these there is nothing to be armed
                // against, but a street full of them would be a warzone rather than a city.
                // One in twenty, not one in six. At the higher rate every street corner had someone
                // squaring up to you, which is not what walking around San Andreas is like.
                ai.isCivilian = (i % 20) != 0;

                // the game's own panic screams; male and female peds draw from different banks
                var voice = ped.AddComponent<AudioSource>();
                voice.playOnAwake = false;
                voice.spatialBlend = 1f;      // 3D, so a scream comes from the ped and not from everywhere
                voice.minDistance = 2f;
                voice.maxDistance = 35f;

                ai.voice = voice;
                ai.panicSounds = LoadPanicSounds(isMale: (i % 2) == 0);

                UdonSharpEditorUtility.CopyProxyToUdon(ai);

                // Peds go on Walkthrough so the player passes through them.
                //
                // On Default they collide with the player capsule, and a NavMeshAgent pushing into that
                // capsule wins - a group of them could walk a player off the map or under it. Walkthrough
                // still takes raycasts, so they remain shootable; they just cannot push.
                VRChatLayerSetup.SetLayerRecursive(ped, VRChatLayerSetup.LayerWalkthrough);

                // the pool enables these as players get near them
                ped.SetActive(false);
                pooled.Add(ped);
                pooledCities.Add(pedCity);
                pooledGroups.Add(PopGroupOf(pedPrefab.name));
            }

            var poolGo = new GameObject("NpcPoolManager");
            var pool = AddUdonBehaviour<GtaNpcPool>(poolGo);
            pool.pathNetwork = pathData;
            pool.pooledNpcs = pooled.ToArray();
            pool.npcCities = pooledCities.ToArray();
            pool.npcGroups = pooledGroups.ToArray();
            ApplyCityRegions(pool);
            ApplyPopCycle(pool);
            s_pooledNpcs = pool.pooledNpcs;
            // must match the agents' baseOffset, or placement buries peds that the agent then never lifts
            pool.groundOffset = s_pedFootOffset;
            UdonSharpEditorUtility.CopyProxyToUdon(pool);

            return pooled.Count;
        }

        /// <summary>
        /// How far the model's lowest point sits below its transform origin, used as NavMeshAgent
        /// baseOffset. Measured from the renderer bounds rather than assumed, since ped models differ.
        /// </summary>
        /// <summary>
        /// Vertical distance from a ped's pivot down to its feet, used as NavMeshAgent baseOffset.
        ///
        /// Measured, not guessed - see GtaPlacementDiagnostics. Every earlier attempt measured the bind
        /// pose, which is useless here: GTA skeletons are Z-up, so an unposed ped lies on its back. That
        /// reads as a 0.42m-tall figure whose toes are its highest bones, and any offset derived from it
        /// is wrong. Sampling a walk clip first stands the skeleton up, and then the pivot sits 1.01m
        /// above the feet consistently across models (standing heights 1.58-1.84m).
        ///
        /// All GTA peds share one skeleton, so a single value is sufficient. Override with
        /// -testScenePedFootOffset:&lt;value&gt;.
        /// </summary>
        private static float s_pedFootOffset = 1.01f;

        private static float ComputeFootOffsetUnused(GameObject ped)
        {
            // Use the SHARED MESH bounds. Two earlier approaches failed because both measured editor-time
            // state: SkinnedMeshRenderer.bounds reports an approximate bind-pose box, and the bone
            // transforms sit in their serialized pose rather than a standing one (the Animator only poses
            // them at runtime). Both reported ~0.2m when the mesh actually extends ~0.53m below the pivot.
            // Mesh bounds are animation-independent, so they describe the real model extent.
            float lowest = float.MaxValue;

            var skinnedRenderers = ped.GetComponentsInChildren<SkinnedMeshRenderer>();
            for (int i = 0; i < skinnedRenderers.Length; i++)
            {
                Mesh mesh = skinnedRenderers[i].sharedMesh;
                if (null == mesh)
                    continue;

                Bounds b = mesh.bounds;
                Vector3 localLowest = new Vector3(b.center.x, b.min.y, b.center.z);
                Vector3 worldLowest = skinnedRenderers[i].transform.TransformPoint(localLowest);

                lowest = Mathf.Min(lowest, worldLowest.y);
            }

            // non-skinned models (should not occur for peds, but keep it correct)
            var meshFilters = ped.GetComponentsInChildren<MeshFilter>();
            for (int i = 0; i < meshFilters.Length; i++)
            {
                Mesh mesh = meshFilters[i].sharedMesh;
                if (null == mesh)
                    continue;

                Bounds b = mesh.bounds;
                Vector3 localLowest = new Vector3(b.center.x, b.min.y, b.center.z);
                lowest = Mathf.Min(lowest, meshFilters[i].transform.TransformPoint(localLowest).y);
            }

            if (lowest == float.MaxValue)
                return 0f;

            float offset = ped.transform.position.y - lowest;

            if (offset < 0f || offset > 3f)
                return 0f;

            return offset;
        }

        /// <summary>
        /// Adds an UdonSharpBehaviour together with its backing UdonBehaviour.
        ///
        /// Must go through UdonSharpUndo: adding the proxy with a plain AddComponent and then calling
        /// CreateBehaviourForProxy fails, because serialization looks up heap data for a backing
        /// UdonBehaviour that doesn't exist yet. UdonSharpUndo creates them in the right order.
        /// </summary>
        private static T AddUdonBehaviour<T>(GameObject go) where T : UdonSharpBehaviour
        {
            return UdonSharpUndo.AddComponent<T>(go);
        }

        private static float ParseFloatArg(string name, float defaultValue)
        {
            string search = "-" + name + ":";
            string arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith(search));
            if (null == arg)
                return defaultValue;

            if (float.TryParse(arg.Substring(search.Length),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                return parsed;

            return defaultValue;
        }
    }
}
