# DeltaVMap [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

An interactive delta-v subway map for [Kitten Space Agency](https://ahwoo.com/app/100000/kitten-space-agency). Dynamically generates from the loaded solar system and shows idealized Hohmann transfer budgets between all celestial bodies.

This mod is written against the [StarMap loader](https://github.com/StarMapLoader/StarMap).

## Features

- **Subway-style map** - metro-aesthetic layout with the player's current body as root, automatically re-roots on SOI change.
- **Delta-v budgets** - closed-form Hohmann transfer calculations with Oberth-combined departure/arrival for accurate budget numbers.
- **Route planning** - click any body to see total delta-v and transfer time, with options for landing, aerobraking, plane changes, and return trips.
- **Vehicle comparison** - compares the selected route against your ship's available delta-v.
- **Dynamic generation** - works with any solar system, including modded ones.

## Installation

1. Install [StarMap](https://github.com/StarMapLoader/StarMap).
2. Download the latest release from the [Releases](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/releases) tab.
3. Extract into `Documents\My Games\Kitten Space Agency\mods\DeltaVMap\`.
4. The game auto-discovers new mods and prompts you to enable them. Alternatively, add to `Documents\My Games\Kitten Space Agency\manifest.toml`:

```toml
[[mods]]
id = "DeltaVMap"
enabled = true
```

## Dependencies

| Package | Purpose | Tested version |
| --- | --- | --- |
| [StarMap](https://github.com/StarMapLoader/StarMap) | Mod loader, required at runtime (see [Installation](#installation)) | 0.4.5 |

## Build dependencies

Required only to build the mod from source. Targets **.NET 10**.

| Package | Source | Tested Version |
| --- | --- | --- |
| [StarMap.API](https://github.com/StarMapLoader/StarMap) | NuGet | 0.3.6 |
| [Lib.Harmony](https://www.nuget.org/packages/Lib.Harmony) | NuGet | 2.4.2 |

## Mod compatibility

- Known conflicts: none

## Check out my other mods

- [AdvancedFlightComputer](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer) - set periapsis / set apoapsis / match or set inclination quick-tools in the Transfer Planner, plus hyperbolic-target support ([forum thread](https://forums.ahwoo.com/threads/advanced-flight-computer.783/))
- [AutoStage](https://github.com/Maximilian-Nesslauer/KSA-AutoStage) - automatic staging during auto-burns and manual flight, with configurable ignition delays ([forum thread](https://forums.ahwoo.com/threads/autostage.891/))
- [StageInfo](https://github.com/Maximilian-Nesslauer/KSA-StageInfo) - extra info in the stock Stage/Sequence window: per-stage delta V, TWR, burn time, fuel pool, RCS, and more ([forum thread](https://forums.ahwoo.com/threads/stageinfo.905/))
- [AutoRemoveFinishedBurns](https://github.com/Maximilian-Nesslauer/KSA-AutoRemoveFinishedBurns) - auto-removes completed auto-burns from the burn plan ([forum thread](https://forums.ahwoo.com/threads/autoremovefinishedburns.928/))
