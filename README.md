Volumetric(in the future) Contrails mod for KSP!

# VolumetricContrails

A Kerbal Space Program mod adding volumetric-style engine smoke and high-altitude
contrails, built from scratch in C# and custom Unity shaders — no dependency on any
closed-source visual mod.

## Features

- **High-altitude contrail** — a thin, continuous volumetric trail that forms in the
  6-12km altitude band, raymarched in real time (custom shader, cylinder-segment
  geometry along the flight path).
- **Launch smoke** — billowing smoke puffs emitted from clustered engines at launch,
  rendered as camera-facing billboards with normal-mapped lighting that responds to
  the real sun direction, giving a pseudo-volumetric look without full raymarching.
- Engine clustering — multiple nearby engines merge into a single, shared smoke
  source instead of each spawning its own separate trail.
- Smoke behavior adapts to vessel speed and altitude: dense/billowing near the pad
  at low speed, thinning into a streak at high speed and high altitude.
- Floating-origin/Krakensbane-safe position tracking (positions are stored relative
  to the celestial body's own transform, not raw world coordinates), so effects stay
  correctly anchored during fast flight, timewarp, and map-view transitions.

## Requirements

- Kerbal Space Program (tested on the version matching Unity 2019.4.18f1)
- No other mods required. (Note: mods that already add their own particle-based
  engine smoke, e.g. SmokeScreen-based configs, may visually overlap with this
  mod's launch smoke — see Known Issues.)

## Installation

1. Download the latest release.
2. Copy the `VolumetricContrails` folder into your `GameData` folder, so you end up
   with:
   ```
   GameData/VolumetricContrails/
   ├── Plugins/
   │   └── VolumetricContrails.dll
   └── Bundles/
       └── volumetriccontrails_bundle
   ```
3. Launch KSP.

## Building from source

The plugin (`.dll`) is a standard C# project targeting KSP's Unity/Mono version.
The shaders and their AssetBundle need to be built in the **Unity Editor version
matching your KSP install's Unity version** (currently 2019.4.18f1) — mismatched
Unity versions will fail to load the bundle at runtime.

1. Open the Unity project in the matching Unity Editor version.
2. Import the textures under `Textures/` and shaders under `Shaders/`.
3. Assign `PuffDiffuse`/`PuffNormal` to the `SmokeMat` material (shader
   `VolumetricContrails/SmokeBillboard`), and confirm `ContrailMat` points to
   `ContrailVolumetric.shader`.
4. Run **Assets → Build VolumetricContrails Bundle** (custom editor script included
   under `UnityEditorScripts/`).
5. Copy the resulting bundle into `GameData/VolumetricContrails/Bundles/`.

## Status / Known issues

This mod is under active development. Current known limitations:

- Ground-color mixing (smoke picking up dust/terrain color near the ground) is not
  yet implemented in the current billboard-based launch smoke.
- No integration yet with atmospheric scattering mods or third-party volumetric
  cloud mods — launch smoke and contrails render independently of those effects'
  depth/atmosphere handling.
- If you have other mods that add their own engine smoke effects (e.g. SmokeScreen
  configs bundled with some part packs), you may see both effects overlapping.
  Disabling the other mod's smoke config avoids the overlap.

## Contributing

Forking this repo to explore the code, build it locally, or submit Pull Requests is
welcome. See [LICENSE](./LICENSE) for terms on redistributing modified builds.

## Credits

Developed independently. General volumetric-rendering techniques used in this mod
(Worley/Perlin noise combination, Henyey-Greenstein scattering, raymarching through
a signed geometric volume) are based on publicly published, general computer
graphics literature (e.g. Andrew Schneider's "Real-Time Volumetric Cloudscapes",
GDC) — no third-party mod code or assets are included.

## License

This project is licensed under **CC BY-NC-SA 4.0** (Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International).

[![License: CC BY-NC-SA 4.0](https://img.shields.io/badge/License-CC%20BY--NC--SA%204.0-lightgrey.svg)](https://creativecommons.org/licenses/by-nc-sa/4.0/)

**In short, you are free to:**
- Use this mod in your own KSP install, including modified versions, for personal use
- Fork this repository and submit Pull Requests
- Share unmodified copies, with credit

**You may not:**
- Redistribute this mod (modified or unmodified) without clear credit to the original author
- Sell this mod or bundle it as part of a paid product/service
- Publish your own modified build/release (e.g. on SpaceDock, CurseForge, or as a separate GitHub release) under a different license, or without also crediting the original and keeping it under the same CC BY-NC-SA 4.0 terms

Full license text: https://creativecommons.org/licenses/by-nc-sa/4.0/legalcode

---

**Note on forking:** forking this repo to explore the code, build it locally, or contribute back via Pull Request is always welcome. If you'd like to maintain your own public fork/continuation, please reach out first, and make sure it stays under the same license with clear attribution back to this repository.
