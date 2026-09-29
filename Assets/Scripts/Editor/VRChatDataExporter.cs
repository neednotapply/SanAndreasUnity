using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SanAndreasUnity.Editor
{
    /// <summary>
    /// Captures the game-data tables the VRChat scene needs but cannot read at runtime.
    ///
    /// Udon has no access to the game files, so anything the world needs from them - weapon statistics,
    /// teleport destinations - has to be lifted into plain assets while the data is loaded.
    /// </summary>
    public static class VRChatDataExporter
    {
        private const string WeaponsPath = "Assets/ExportedAssets/WeaponData.asset";
        private const string ZonesPath = "Assets/ExportedAssets/ZoneData.asset";
        private const string ParkedVehiclesPath = "Assets/ExportedAssets/ParkedVehicles.asset";

        public static void ExportAll()
        {
            TeleportDataExporter.Export();
            ExportWeapons();
            ExportZones();
            ExportParkedVehicles();
            WaterExporter.Export();
            TimeCycleExporter.Export();
            PedGroupExporter.Export();
            CityRegionExporter.Export();
            PopCycleExporter.Export();
            VehicleGroupExporter.Export();
            MapTextureExporter.Export();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// San Andreas' named districts.
        ///
        /// These are compiled into the importer rather than read from a data file, so they are available
        /// without the game data loaded - but they still have to be lifted into an asset, because Udon
        /// cannot reach into importer code.
        /// </summary>
        public static void ExportZones()
        {
            var zones = Importing.ZoneHelpers.zoneInfoList;

            if (zones == null || zones.Length == 0)
            {
                Debug.LogError("No zones available to export");
                return;
            }

            // The population category is not in any data file - it is assigned by the mission script at
            // runtime - so it is recovered from main.scm and joined on here. Without it popcycle.dat cannot
            // be indexed at all.
            var typesByCode = ZoneTypeExtractor.ExtractZoneTypes();
            var zoneBounds = ZoneTypeExtractor.ReadZoneBounds();

            var names = new List<string>();
            var mins = new List<Vector3>();
            var maxs = new List<Vector3>();
            var volumes = new List<float>();
            var zoneTypes = new List<int>();
            var zoneCodes = new List<string>();

            int typed = 0;

            foreach (var zone in zones)
            {
                if (zone == null || string.IsNullOrWhiteSpace(zone.name))
                    continue;

                names.Add(zone.name);
                mins.Add(zone.vmin);
                maxs.Add(zone.vmax);

                // The zone list carries display names while the category table is keyed by internal code.
                // Their bounds are the same data rounded differently, so matching on bounds links the two.
                string code = FindZoneCode(zoneBounds, zone.vmin, zone.vmax);
                zoneCodes.Add(code ?? string.Empty);

                int type = -1;
                if (code != null && typesByCode.TryGetValue(code, out int found))
                {
                    type = found;
                    typed++;
                }

                zoneTypes.Add(type);

                // precomputed so the runtime lookup does not recompute it for every zone, every check
                Vector3 size = zone.vmax - zone.vmin;
                volumes.Add(Mathf.Abs(size.x * size.y * size.z));
            }

            var asset = ScriptableObject.CreateInstance<Export.GtaZoneData>();
            asset.names = names.ToArray();
            asset.mins = mins.ToArray();
            asset.maxs = maxs.ToArray();
            asset.volumes = volumes.ToArray();
            asset.zoneTypes = zoneTypes.ToArray();
            asset.zoneCodes = zoneCodes.ToArray();

            if (AssetDatabase.LoadAssetAtPath<Export.GtaZoneData>(ZonesPath) != null)
                AssetDatabase.DeleteAsset(ZonesPath);

            AssetDatabase.CreateAsset(asset, ZonesPath);

            Debug.Log($"Zones exported: {names.Count} districts, {typed} with a population category " +
                $"-> {ZonesPath}");

            // a few worked examples, so a bad join is obvious rather than silent
            for (int i = 0; i < names.Count && i < 6; i++)
            {
                string typeName = zoneTypes[i] >= 0 && zoneTypes[i] < ZoneTypeExtractor.TypeNames.Length
                    ? ZoneTypeExtractor.TypeNames[zoneTypes[i]]
                    : "UNKNOWN";

                Debug.Log($"  {names[i]} [{zoneCodes[i]}] -> {typeName}");
            }
        }

        /// <summary>
        /// Finds the info.zon code whose bounds match a zone.
        ///
        /// The compiled zone list rounds its bounds to whole units, so this compares with a tolerance
        /// rather than exactly.
        /// </summary>
        private static string FindZoneCode(
            List<(string code, Vector3 min, Vector3 max)> zoneBounds, Vector3 min, Vector3 max)
        {
            const float tolerance = 2f;

            foreach (var candidate in zoneBounds)
            {
                if (Vector3.Distance(candidate.min, min) > tolerance)
                    continue;

                if (Vector3.Distance(candidate.max, max) > tolerance)
                    continue;

                return candidate.code;
            }

            return null;
        }

        /// <summary>
        /// Cars the game parks around the map.
        ///
        /// These are placements from the IPL files, not traffic: a specific model at a specific kerb, often
        /// with the colours the designer chose. They are a large part of why the streets look inhabited
        /// rather than swept clean, and nothing in the world currently reproduces them.
        /// </summary>
        public static void ExportParkedVehicles()
        {
            // GetPlacements returns nothing unless told which cells to look in; 0-18 is the whole map,
            // matching what Cell uses to load the world
            int[] cellIds = Enumerable.Range(0, 19).ToArray();

            var placements = Importing.Items.Item
                .GetPlacements<Importing.Items.Placements.ParkedVehicle>(cellIds)
                .ToArray();

            if (placements.Length == 0)
            {
                Debug.LogWarning("No parked vehicle placements found");
                return;
            }

            // model ids have to be resolved to names, since that is how the prefabs were exported
            var namesById = new Dictionary<int, string>();

            foreach (var def in Importing.Items.Item.GetDefinitions<Importing.Items.Definitions.VehicleDef>())
            {
                if (!string.IsNullOrWhiteSpace(def.ModelName))
                    namesById[def.Id] = def.ModelName;
            }

            var modelNames = new List<string>();
            var positions = new List<Vector3>();
            var angles = new List<float>();
            var primaryColors = new List<int>();
            var secondaryColors = new List<int>();
            var forceSpawn = new List<bool>();
            var wasRandom = new List<bool>();

            // Most placements ask for a random car rather than a specific model - id -1 is GTA's "any
            // ordinary vehicle here" marker, and it accounts for the large majority of them. Dropping those
            // would discard three quarters of the parked cars in the state and leave the streets bare.
            var randomPool = BuildRandomCarPool();

            int unresolved = 0;
            var unresolvedIds = new Dictionary<int, int>();

            foreach (var parked in placements)
            {
                if (!namesById.TryGetValue(parked.CarId, out string modelName))
                {
                    if (parked.CarId == RandomCarId && randomPool.Count > 0)
                    {
                        // Chosen from the position, not at random: the choice is baked into the asset and
                        // must be the same every export, or a rebuild silently reshuffles every car in the
                        // world. Hashing the position also keeps neighbouring spots from all matching.
                        int hash = Mathf.Abs(
                            Mathf.RoundToInt(parked.Position.x * 7.3f)
                            ^ Mathf.RoundToInt(parked.Position.z * 13.7f));

                        modelName = randomPool[hash % randomPool.Count];
                    }
                    else
                    {
                        unresolved++;
                        unresolvedIds.TryGetValue(parked.CarId, out int seen);
                        unresolvedIds[parked.CarId] = seen + 1;
                        continue;
                    }
                }

                modelNames.Add(modelName);
                positions.Add(parked.Position);
                angles.Add(parked.Angle);

                // ForceSpawn marks the placements the game always fills; the rest are candidates
                forceSpawn.Add(parked.ForceSpawn);
                wasRandom.Add(parked.CarId == RandomCarId);

                primaryColors.Add(parked.Colors != null && parked.Colors.Length > 0 ? parked.Colors[0] : -1);
                secondaryColors.Add(parked.Colors != null && parked.Colors.Length > 1 ? parked.Colors[1] : -1);
            }

            var asset = ScriptableObject.CreateInstance<Export.GtaParkedVehicleData>();
            asset.modelNames = modelNames.ToArray();
            asset.positions = positions.ToArray();
            asset.angles = angles.ToArray();
            asset.primaryColors = primaryColors.ToArray();
            asset.secondaryColors = secondaryColors.ToArray();
            asset.forceSpawn = forceSpawn.ToArray();
            asset.wasRandom = wasRandom.ToArray();

            if (AssetDatabase.LoadAssetAtPath<Export.GtaParkedVehicleData>(ParkedVehiclesPath) != null)
                AssetDatabase.DeleteAsset(ParkedVehiclesPath);

            AssetDatabase.CreateAsset(asset, ParkedVehiclesPath);

            Debug.Log($"  of these, {forceSpawn.Count(_ => _)} are guaranteed spawns and " +
                $"{wasRandom.Count(_ => _)} asked for a random vehicle");

            Debug.Log($"Parked vehicles exported: {modelNames.Count} placements " +
                $"({unresolved} unresolved model ids) -> {ParkedVehiclesPath}");

            if (unresolvedIds.Count > 0)
            {
                string summary = string.Join(", ", unresolvedIds
                    .OrderByDescending(kv => kv.Value)
                    .Take(12)
                    .Select(kv => $"id {kv.Key} x{kv.Value}"));

                Debug.Log($"Unresolved parked vehicle ids ({unresolvedIds.Count} distinct): {summary}");
            }

            Debug.Log($"Vehicle definition id range: " +
                $"{(namesById.Count > 0 ? namesById.Keys.Min() : 0)}-" +
                $"{(namesById.Count > 0 ? namesById.Keys.Max() : 0)} ({namesById.Count} definitions)");
        }

        /// <summary> GTA's marker for "put any ordinary vehicle here". </summary>
        private const int RandomCarId = -1;

        /// <summary>
        /// The set of vehicles used for random parked cars.
        ///
        /// Ordinary civilian cars only. The full roster includes tanks, aircraft, emergency vehicles and
        /// mission props, and a street lined with random Rhinos and Hydras would be a parody of the city
        /// rather than the city.
        /// </summary>
        private static List<string> BuildRandomCarPool()
        {
            // classes that would look absurd parked on a residential kerb
            string[] excluded =
            {
                "rhino", "hydra", "hunter", "seasparrow", "sparrow", "maverick", "cargobob", "leviathan",
                "androm", "at400", "beagle", "cropdust", "dodo", "nevada", "shamal", "skimmer", "stunt",
                "rustler", "raindanc", "vortex", "firetruk", "ambulan", "police", "policer", "polmav",
                "enforcer", "swatvan", "barracks", "patriot", "bus", "coach", "trash", "train", "tram",
                "freight", "streak", "artict", "petro", "packer", "dumper", "combine", "kart", "mower",
                "dozer", "forklift", "tractor", "utility", "vortex", "boat", "reefer", "tropic", "predator",
                "squalo", "speeder", "marquis", "coastg", "dinghy", "jetmax", "launch", "cabbie", "taxi",
            };

            var pool = new List<string>();

            foreach (var def in Importing.Items.Item.GetDefinitions<Importing.Items.Definitions.VehicleDef>())
            {
                if (string.IsNullOrWhiteSpace(def.ModelName))
                    continue;

                if (def.VehicleType != Importing.Items.Definitions.VehicleType.Car)
                    continue;

                string lower = def.ModelName.ToLowerInvariant();

                bool skip = false;
                foreach (string bad in excluded)
                {
                    if (lower.Contains(bad))
                    {
                        skip = true;
                        break;
                    }
                }

                if (!skip)
                    pool.Add(def.ModelName);
            }

            // stable order, so the position hash maps to the same car on every export
            pool.Sort(System.StringComparer.OrdinalIgnoreCase);

            Debug.Log($"Random parked-car pool: {pool.Count} civilian models");

            return pool;
        }

        /// <summary>
        /// Weapon statistics from weapon.dat, keyed by the model name the prefabs were exported under.
        ///
        /// weapon.dat identifies weapons by model id, while the exported prefabs are named after the model,
        /// so the two are joined through the weapon definitions.
        /// </summary>
        public static void ExportWeapons()
        {
            var defsById = new Dictionary<int, string>();

            foreach (var def in Importing.Items.Item.GetDefinitions<Importing.Items.Definitions.WeaponDef>())
            {
                if (!string.IsNullOrWhiteSpace(def.ModelName))
                    defsById[def.Id] = def.ModelName;
            }

            var names = new List<string>();
            var types = new List<string>();
            var damage = new List<int>();
            var range = new List<float>();
            var clip = new List<int>();
            var accuracy = new List<float>();
            var isGun = new List<bool>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var weapon in Importing.Weapons.WeaponData.LoadedWeaponsData)
            {
                if (weapon == null)
                    continue;

                if (!defsById.TryGetValue(weapon.modelId1, out string modelName))
                    continue;

                if (!seen.Add(modelName))
                    continue;

                var gun = weapon.gunData;

                names.Add(modelName);
                types.Add(weapon.weaponType ?? string.Empty);
                range.Add(weapon.weaponRange);
                isGun.Add(gun != null);

                damage.Add(gun != null ? gun.damage : 10);
                clip.Add(gun != null ? gun.ammoClip : 0);
                accuracy.Add(gun != null ? gun.accuracy : 1f);
            }

            var asset = ScriptableObject.CreateInstance<Export.GtaWeaponData>();
            asset.modelNames = names.ToArray();
            asset.weaponTypes = types.ToArray();
            asset.damage = damage.ToArray();
            asset.range = range.ToArray();
            asset.clipSize = clip.ToArray();
            asset.accuracy = accuracy.ToArray();
            asset.isGun = isGun.ToArray();

            if (AssetDatabase.LoadAssetAtPath<Export.GtaWeaponData>(WeaponsPath) != null)
                AssetDatabase.DeleteAsset(WeaponsPath);

            AssetDatabase.CreateAsset(asset, WeaponsPath);

            Debug.Log($"Weapon data exported: {names.Count} weapons ({isGun.Count(_ => _)} guns) -> {WeaponsPath}");
        }
    }
}
