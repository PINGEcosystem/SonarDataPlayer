# SonarDataPlayer

SonarDataPlayer is a lightweight Windows desktop application for playing back processed sonar recordings. The first target is Garmin RSD data exported into synchronized channel waterfall PNGs and ping telemetry CSV files.

The app is intentionally a single-process .NET desktop program: no Python runtime, web server, Node.js, or browser frontend is required for playback.

## Current Status

- WPF desktop application shell.
- Core playback and telemetry models.
- Processed project manifest loader.
- Stacked channel waterfall display using generated PNGs.
- Play, pause, seek, speed selection, channel visibility, opacity controls, and telemetry readouts.

Direct Garmin RSD parsing will be ported into the application after the playback UI is validated against processed exports.

## Repository Layout

```text
SonarDataPlayer.App/      WPF desktop application
SonarDataPlayer.Core/     Recording, channel, telemetry, playback, and project loading code
docs/                     Project format and development notes
```

## Prerequisites

- Windows 10 or later.
- .NET 8 SDK or newer.

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

For now, generate processed assets from the Garmin RSD parser:

1. Parse the `.RSD` file.
2. Export ping metadata to CSV.
3. Extract raw 16-bit samples grouped by `channel_id`.
4. Render one waterfall PNG per channel.
5. Write a `manifest.json` that points to the CSV and PNGs.
6. Open the manifest in SonarDataPlayer.

This workflow keeps playback deployment simple while we finish porting the parser into C#.

## Planned Next Steps

- Add a first-class processed-project exporter.
- Render waterfall viewports instead of full-image scaling.
- Add overlay mode with per-channel opacity and palette controls.
- Port Garmin RSD parsing into `SonarDataPlayer.Core`.
- Add direct raw sample rendering with gain and contrast controls.
