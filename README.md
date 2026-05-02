# SonarDataPlayer

SonarDataPlayer is a lightweight Windows desktop application for playing back processed sonar recordings. The first target is Garmin RSD data exported into synchronized channel waterfall views and ping telemetry.

The app is intentionally a single-process .NET desktop program: no web server, Node.js, or browser frontend is required for playback. A Python bridge is currently used only when creating a new processed project from a Garmin `.RSD` file.

## Current Status

- WPF desktop application shell.
- Core playback and telemetry models.
- Processed project manifest loader.
- In-app Garmin RSD project creation through the bridge exporter.
- Raw sample playback using `frames.jsonl` and `samples.u16le`, with PNG preview/fallback assets.
- Play, pause, seek, speed selection, channel visibility, opacity controls, and telemetry readouts.
- Depth, speed, temperature unit controls, time/depth zoom, and stacked/overlay channel views.

The Garmin RSD parser is still bridged from the local PINGverter parser. Porting direct RSD parsing into the application remains a future cleanup step.

## Repository Layout

```text
SonarDataPlayer.App/      WPF desktop application
SonarDataPlayer.Core/     Recording, channel, telemetry, playback, and project loading code
docs/                     Project format and development notes
```

## Prerequisites

- Windows 10 or later.
- .NET 8 SDK or newer.
- Python 3 with `numpy` and `pandas` if you want to create projects from `.RSD` files inside the app or with `tools/export_rsd_project.py`.

Check your SDK:

```powershell
dotnet --info
```

## Build

From the repository root:

```powershell
dotnet restore .\SonarDataPlayer.App\SonarDataPlayer.App.csproj
dotnet build .\SonarDataPlayer.App\SonarDataPlayer.App.csproj -c Release
```

## Run

```powershell
dotnet run --project .\SonarDataPlayer.App\SonarDataPlayer.App.csproj
```

Use **Open Project** and select a processed `manifest.json`.

See [docs/processed-project-format.md](docs/processed-project-format.md) for the expected folder layout.

## Create A Project From RSD

In the app:

1. Use **Python...** to select a `python.exe` that can import `numpy` and `pandas`.
2. Use **New Project**.
3. Select a Garmin `.RSD` file.
4. Choose or create the output project folder.

On success, the app loads the generated `manifest.json` automatically.

Python selection priority is:

1. `SONAR_DATA_PLAYER_PYTHON` environment variable.
2. Saved app setting from **Python...**.
3. Local/bundled Python candidates.
4. System `python` or `py`.

Install the parser dependencies into your chosen Python environment:

```powershell
python -m pip install numpy pandas
```

## Package A Portable Windows Build

```powershell
dotnet publish .\SonarDataPlayer.App\SonarDataPlayer.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\publish\win-x64
```

The portable executable will be written to `publish\win-x64`.

## Processed Recording Workflow

You can also generate processed assets from the Garmin RSD parser with the bridge exporter:

```powershell
python .\tools\export_rsd_project.py `
  "..\SonarRecordings\Sonar000.RSD" `
  ".\ProcessedRecordings\Sonar000"
```

Then run the app and open:

```text
ProcessedRecordings\Sonar000\manifest.json
```

The exporter writes:

- `manifest.json`
- `pings.csv`
- `frames.jsonl`
- `samples.u16le`
- `channels\*_channel_*.png`

The exporter workflow is:

1. Parse the `.RSD` file.
2. Export ping metadata to CSV.
3. Extract raw 16-bit samples grouped by `channel_id`.
4. Group channel records by Garmin `sequence_cnt` into synchronized ping frames.
5. Write raw `uint16` channel arrays into `samples.u16le`.
6. Write frame metadata and sample offsets into `frames.jsonl`.
7. Render optional preview waterfall PNGs.
8. Write a `manifest.json` that points to the CSV, frame index, sample blob, and PNGs.
9. Open the manifest in SonarDataPlayer.

This workflow keeps playback deployment simple while we finish porting the parser into C#.

When `frames.jsonl` and `samples.u16le` are present, the app renders from raw samples with one shared intensity scale. PNGs are kept as preview/fallback assets.

## Planned Next Steps

- Port Garmin RSD parsing into `SonarDataPlayer.Core`.
- Add direct rendering controls for gain, contrast, palettes, and side-scan handling.
- Improve packaged Python/exporter deployment or remove the Python bridge entirely.
