# Processed Project Format

SonarDataPlayer initially loads a processed project folder instead of parsing Garmin RSD files directly in the UI.
This keeps the desktop application small and lets the parser evolve independently.

## Manifest

Create a `manifest.json` file beside the generated frame metadata, raw samples, telemetry CSV, and optional preview PNGs:

```json
{
  "formatVersion": 2,
  "source": "Sonar000.RSD",
  "telemetry": "pings.csv",
  "frames": "frames.jsonl",
  "samples": {
    "path": "samples.u16le",
    "encoding": "uint16-le"
  },
  "frameCount": 309,
  "channels": [
    {
      "channelId": 1785,
      "label": "Traditional CHIRP 140-240 kHz",
      "mode": "Traditional CHIRP",
      "orientation": null,
      "beam": 1,
      "startFrequencyHz": 140000,
      "endFrequencyHz": 240000,
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

## Frame Index

`frames.jsonl` contains one JSON object per synchronized ping frame. A frame groups every channel record that shares the same Garmin `sequence_cnt`.

```json
{
  "frameIndex": 0,
  "sequenceCount": 1,
  "timeSeconds": 0.047,
  "lat": 28.498381,
  "lon": -80.342264,
  "speedMetersPerSecond": 1.42,
  "trackDistanceMeters": 0.0,
  "headingDegrees": 91.2,
  "temperatureCelsius": 23.8,
  "channels": [
    {
      "channelId": 1785,
      "sampleOffset": 0,
      "sampleCount": 2048,
      "byteLength": 4096,
      "minRangeMeters": 0.0,
      "maxRangeMeters": 26.4,
      "bottomDepthMeters": 4.1
    }
  ]
}
```

The player should use `frameIndex` for playback, `timeSeconds` for the timeline, and each channel entry to seek into `samples.u16le`.

## Raw Samples

`samples.u16le` is one contiguous binary blob of raw 16-bit little-endian unsigned samples.

To read a channel array for one frame:

1. Seek to `sampleOffset`.
2. Read `byteLength` bytes.
3. Interpret as `sampleCount` little-endian `uint16` values.

Raw sample values are intentionally not contrast-scaled. Rendering should apply palette, gain, contrast, and blending at playback time so all channels share the same display transform.

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
These PNGs are preview/fallback assets. The main player should render from `frames.jsonl` and `samples.u16le` to avoid per-channel PNG scaling artifacts.
