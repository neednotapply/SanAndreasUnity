# San Andreas for VRChat

An experiment in turning **GTA: San Andreas** into a **VRChat world**: the whole map, streamed in around
you, with traffic, pedestrians, weapons, a radio, a day/night cycle and district-aware population,
running as Udon (UdonSharp) behaviours.

> **Work in progress.** Parts of this work, parts are unverified, and some are known to be broken. The
> [status section](#status) says which is which.

This project is a fork of [San Andreas Unity](https://github.com/in0finite/SanAndreasUnity), and it does
**not** share that project's goals:

| | San Andreas Unity (original) | This project |
|---|---|---|
| Goal | A standalone, moddable reimplementation of the GTA:SA engine, with multiplayer | A VRChat world built from the game's data |
| Runs on | Unity player (Windows, Linux, Mac, Android) | VRChat, through Udon |
| Game logic | C# reimplementing the engine at runtime | Udon behaviours written for VRChat's limits, plus geometry exported ahead of time |
| Game data | Read from your GTA install at startup | Exported once into Unity assets, then baked into a scene |

The original's importer is what reads GTA's file formats, and this project builds on it. The original's
standalone game code is still in the tree, but it isn't maintained or verified here.

## How it works

Udon can't run the original engine: no generics, no LINQ, no `Dictionary`, no creating objects at runtime,
and `VRCUrl`s must exist before the world is built. So the work is split in two:

1. **Export (editor, once).** Editor tools in `Assets/Scripts/Editor` read your copy of GTA and write
   meshes, materials, collision, prefabs and data tables to `Assets/ExportedAssets/`. That folder is
   **gitignored** and is generated on your machine.
2. **Build (editor, batch).** `VRChatTestSceneBuilder` assembles the scene: about 45,000 map objects
   grouped into 200 m streaming cells, plus pooled vehicles, pedestrians and weapons, and the Udon
   behaviours that drive them. The scene is also gitignored: it is around 100 MB, and it can only be
   rebuilt from your exported assets.
3. **Run (VRChat / ClientSim).** The behaviours in `Assets/Scripts/VRChat` run the world.

Design choices worth knowing about:

- **Time is derived from server time**, not synced. The day/night cycle, the day of the week and the radio
  playhead are all computed from `Networking.GetServerTimeInSeconds()`, so there is no sync traffic, no
  drift, and late joiners are correct. That value can be negative before the network is up, so it is
  sanitised before any arithmetic.
- **Objects are pre-placed and pooled.** Udon can't spawn anything, so parked cars, pedestrians, traffic
  and weapon racks exist in the scene and are switched on and off as cells stream in.
- **The game's own data drives behaviour**: districts come from `info.zon`, `map.zon` and the zone opcodes
  in `main.scm`; population from `popcycle.dat`, `pedgrp.dat` and `cargrp.dat`; lighting from
  `timecyc.dat`; vehicle and weapon stats from `handling.cfg` and `weapon.dat`.

## Status

### Confirmed in testing

- Day/night cycle, district names, and plausible population density per district
- Water renders
- Driving works
- Traffic signals cycle correctly, on the same phase the traffic AI obeys

### Implemented, not yet confirmed in a test

- Radio: 11 stations in dial order, with playback position derived from server time and static covering
  load time
- Parked cars: which spaces are filled is decided as each cell loads, from the district's hourly vehicle
  budget, and cars are placed on the road surface
- Pedestrians and traffic after the clock fix, including zone-appropriate vehicles
- Weapon pickups at every spawn, held at the correct angle
- Teleport board and vehicle spawner using VRChat's laser pointer
- Rotating circular minimap
- Street lamps lighting at dusk only, with a soft glow instead of a solid tile
- Entering and leaving vehicles without clipping into them

### Known broken or incomplete

- **Missing ground.** Some areas, near cell `-12,3` and in Las Venturas, have no walkable surface and the
  player falls through. Streaming range and missing geometry have both been ruled out; the cause is
  unknown.
- **Vehicle textures are scrambled** on many models. Cause: exported materials are named by renderer
  index and reused by path, and adding wheel cloning shifted every index. Fix: the vehicle materials and
  textures need regenerating (see [Rebuilding the vehicles](#rebuilding-the-vehicles)).
- **Vehicle damage does nothing visible.** The damaged panel meshes were never exported, and the smoke,
  fire and explosion effects were never built.
- Traffic is only district-gated for 28 of 40 pooled vehicles.
- Trains and level crossings are not implemented; crossing lights are forced off.
- No water collision or swimming, and weapon fire sounds are not mapped.
- One map object fails to export.

## Requirements

- **Unity 2022.3.22f1**
- The **VRChat Worlds SDK**, **UdonSharp** and **AudioLink** (resolved through `Packages/vpm-manifest.json`
  with the VRChat Creator Companion)
- A **legal copy of GTA: San Andreas (PC)**. Point the project at it in `config.user.json` (gitignored):
  ```json
  { "game_dir": "D:/SteamLibrary/steamapps/common/Grand Theft Auto San Andreas" }
  ```
- Git submodules: `git clone --recurse-submodules`
- Radio station URLs, which you supply locally in `Assets/ExportedAssets/RadioStations.txt`

## Building it

Exports run as Unity batch jobs. **Only one Unity process can open a project at a time, so close the
Editor first.** Don't pass `-quit` to the export entry points; they exit themselves.

```
Unity.exe -batchmode -projectPath <project> -executeMethod SanAndreasUnity.Editor.AssetExportCommandLine.<Method>
```

| Method | Exports | Rough time |
|---|---|---|
| `Run` | Everything: animations, peds, vehicles, weapons, audio, world | hours |
| `RunWorldOnly` | The static world (do **not** use `-nographics`: it exports collision with no visible geometry) | ~1h 45m |
| `RunVehiclesOnly` | All vehicle models, materials and textures | ~15 min |
| `RunPedsAndWeaponsOnly` | Ped and weapon meshes | ~5 min |
| `RunVehicleDataOnly` | Handling, animation groups, comp rules | seconds |
| `RunVRChatData` | Zones, popcycle, pedgroups, paths, timecycle | minutes |
| `RunStreetLights` | Lamp glows on the exported lamp prefabs (re-run after `RunWorldOnly`) | ~15 min |

Then build the scene, either from the **San Andreas Unity → Build VRChat test scene** menu or with:

```
-executeMethod SanAndreasUnity.Editor.VRChatTestSceneBuilder.BuildFromCommandLine
    -testSceneStreamWorld:1 -testSceneCellSize:200 -testScenePedCount:120 -testSceneVehicleCount:40
```

A full scene build takes about 6 minutes. Adding a **new** `UdonSharpBehaviour` needs two compile passes:
the first creates its program asset, the second lets the build serialize it.

### Rebuilding the vehicles

Exported files are reused by path when they exist. If vehicle materials or textures go stale, delete only
the vehicle-named files (`<model>-<n>-<n>.mat` in `Materials/`, `<model>-<n>-<n>.asset` in `Textures/`) and
run `RunVehiclesOnly`. `Materials/`, `Textures/`, `Models/` and `CollisionModels/` are **shared by every
exported category**: never bulk-delete from them.

### Diagnostics

Editor tools for checking results rather than trusting log lines:

- `GtaSceneMeshCheck`: opens the built scene and counts meshes that failed to resolve
- `GtaVehicleRenderCheck`: renders vehicles to PNG
- `GtaVehiclePartDump`: prints each part's position and materials
- `GtaParkedCarGroundCheck`: measures how far parked cars sit from the road
- `GtaVehicleAssetDependencyCheck`: lists which assets are vehicle-only before you delete anything

## Legal

This repository contains **no Rockstar assets** and must not. Everything under `Assets/ExportedAssets/`
is extracted from your own copy of the game and is gitignored. Uploading a world built from those assets
to VRChat would redistribute copyrighted content; that is your responsibility, and it is why no built
scene is included here.

This project is not affiliated with or endorsed by Rockstar Games, Take-Two Interactive or VRChat Inc.
Grand Theft Auto and San Andreas are trademarks of their owners.

## Credits and licence

Licensed under the **MIT License** (see [LICENSE](LICENSE)), Copyright (c) 2015 James King.

The GTA file importers, renderer and the engine reimplementation this builds on are the work of
[San Andreas Unity](https://github.com/in0finite/SanAndreasUnity) by **in0finite** and its contributors.
Its documentation is on the [project wiki](https://github.com/GTA-ASM/SanAndreasUnity/wiki), and it is
where to look if you want the standalone game. Build dependencies such as `UGameCoreUtilities`,
`MirrorLite` and `NavMeshes` come from the same ecosystem as git submodules.
