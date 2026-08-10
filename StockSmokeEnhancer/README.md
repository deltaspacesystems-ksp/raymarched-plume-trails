# Stock Smoke Enhancer

A small Kerbal Space Program 1 mod that boosts stock engine particle effects —
smoke, exhaust, and sparks — with more particles and longer-lasting trails.
It doesn't replace any models, textures, or engine configs: it adjusts the
effects at runtime, so it works out of the box with stock engines and most
modded ones too.

## Features

- More particles per effect (emission rate)
- Longer particle lifetime, so trails linger and build up more
- Optional particle size scaling
- **Live in-game tuning** — a button on the stock AppLauncher (the toolbar on
  the right side of the screen in flight) opens a window with sliders for all
  three multipliers. Changes apply instantly, no restart required.
- Settings persist to a plain-text `config.cfg`, editable by hand or saved
  from the in-game UI.

## Installation

1. Download the latest release from the [Releases](../../releases) page (or
   build it yourself — see below).
2. Extract the `StockSmokeEnhancer` folder into `GameData/` in your KSP
   installation, so you end up with:
   ```
   GameData/StockSmokeEnhancer/Plugins/StockSmokeEnhancer.dll
   GameData/StockSmokeEnhancer/config.cfg
   ```
3. Launch the game.

No other mods required.

## Usage

In flight, click the Stock Smoke Enhancer icon in the AppLauncher (top-right
toolbar) to open the settings window:

- **Emission** — particle spawn rate multiplier (default 4x)
- **Lifetime** — how long each particle survives (default 3x)
- **Size** — particle size multiplier (default 1x, i.e. unchanged)

Drag the sliders to adjust the effect live. Click **Save** to write the
current values to `config.cfg` so they're used next time you launch the game,
or **Reset to defaults** to revert.

## Editing config.cfg directly

```
SMOKE_ENHANCER_SETTINGS
{
	emissionMultiplier = 4
	lifetimeMultiplier = 3
	sizeMultiplier = 1
}
```

Edit the file and restart the game (or use the in-game sliders instead — no
restart needed there).

## Building from source

Requires the [.NET SDK](https://dotnet.microsoft.com/download) and a KSP
installation (the build references the game's own DLLs from
`KSP_Data/Managed` to compile against its API).

1. Open `StockSmokeEnhancer.csproj` and point `KSPManagedDir` at your game's
   `KSP_x64_Data/Managed` (or `KSP_Data/Managed`) folder.
2. Run:
   ```
   dotnet build
   ```

The DLL is written to `bin/StockSmokeEnhancer.dll`. If `KSPManagedDir` points
at a real game install, it's also copied automatically to
`GameData/StockSmokeEnhancer/Plugins/`, and `config.cfg` is seeded there on
first build (later builds won't overwrite your saved settings).

## How it works

Effects are matched by object name using a keyword heuristic (`smoke`,
`exhaust`, `monoprop`, `srb`, `flame`, `plume`, `shock`, `spark`), which
covers stock engine effects. Emission is re-applied every frame (since stock
itself re-drives it every frame from a throttle curve). Lifetime and size are
cached per particle system the first time they're seen and recomputed from
that cached base each frame, which is what makes slider changes apply live to
effects that are already running.

## Known limitations

- Effects are matched by name, so mods using different naming conventions for
  their particle effects won't be picked up.
- Large multipliers on vessels with many engines can impact performance.

## License

MIT — do whatever you want with it.
