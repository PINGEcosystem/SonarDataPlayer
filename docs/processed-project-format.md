# Processed Project Format

SonarDataPlayer initially loads a processed project folder instead of parsing Garmin RSD files directly in the UI.
This keeps the desktop application small and lets the parser evolve independently.

## Manifest

Create a `manifest.json` file beside the generated metadata CSV and waterfall PNGs:

```json
{
  "source": "Sonar000.RSD",
  "telemetry": "pings.csv",
  "channels": [
    {
      "channelId": 1785,
      "waterfall": "channels/1785/waterfall.png",
      "rows": 309,
      "maxSamples": 2048,
      "timeStart": 0.0,
      "timeEnd": 120.0
    }
  ]
}
```

Paths may be absolute or relative to the manifest directory.

## Telemetry CSV

The app reads these columns when present:

- `record_num`
- `channel_id`
- `time_s`
- `ping_cnt` or `sample_cnt`
- `min_range`
- `max_range`
- `inst_dep_m`
- `lat`
- `lon`
- `speed_ms`
- `instr_heading`
- `tempC`

Extra columns are ignored.

## Waterfall PNGs

Each channel waterfall should be one PNG where rows are pings and columns are sonar samples.
The first player version displays these PNGs directly; a later version will render raw `ushort` samples with palette, gain, and contrast controls in the application.
