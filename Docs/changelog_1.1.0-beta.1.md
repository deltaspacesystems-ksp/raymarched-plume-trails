# Raymarched Plume Trails 1.1.0 Beta 1

Test release for Kerbal Space Program 1.12.5 on Windows.

- Solid boosters and supported liquid engines emit separate volumetric trails from each active nozzle.
- Fresh smoke follows the nozzle as the engine turns. Released smoke keeps its path and gradually loses its initial velocity.
- Shortened trails can render on ignition near the ground.
- Solid booster trails are slightly narrower, especially on smaller motors.
- Smoke shading contrast fades gradually beyond 400 m from the camera to reduce patchiness in distant trails.
- Low and Medium use two and three sunlight samples per lighting evaluation. High and Ultra use four.
- Volume meshes are created without colliders, avoiding interference with the craft at ignition.

Extract GameData into the KSP installation. When upgrading, replace the old mod files and keep your PluginData folder to retain your settings.

Please include the craft, quality preset, and camera angle in performance reports. Multiple active nozzles and long trails viewed end-on are particularly useful test cases.
