"""Export a Garmin RSD file into a SonarDataPlayer processed project.

This is a bridge tool while the Garmin RSD parser is still being ported from
PINGverter into SonarDataPlayer.Core.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import math
import os
import sys
import types
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Export Garmin RSD metadata and waterfall PNGs for SonarDataPlayer."
    )
    parser.add_argument("rsd", type=Path, help="Path to the Garmin .RSD file.")
    parser.add_argument(
        "output",
        type=Path,
        help="Directory where the processed SonarDataPlayer project will be written.",
    )
    parser.add_argument(
        "--pingverter",
        type=Path,
        default=None,
        help="Path to a local PINGverter repo. Defaults to ../PINGverter from this repo.",
    )
    args = parser.parse_args()

    rsd_path = args.rsd.resolve()
    output_dir = args.output.resolve()

    if not rsd_path.is_file():
        raise FileNotFoundError(f"RSD file does not exist: {rsd_path}")

    repo_root = Path(__file__).resolve().parents[1]
    pingverter_root = (
        args.pingverter.resolve()
        if args.pingverter is not None
        else repo_root.parent / "PINGverter"
    )

    gar = load_garmin_parser(pingverter_root)

    output_dir.mkdir(parents=True, exist_ok=True)
    metadata_dir = output_dir / "meta"
    channel_dir = output_dir / "channels"
    metadata_dir.mkdir(exist_ok=True)
    channel_dir.mkdir(exist_ok=True)

    sonar = gar(str(rsd_path), nchunk=500, exportUnknown=True)
    sonar.metaDir = str(metadata_dir)
    sonar._getFileLen()
    sonar._parseFileHeader()
    sonar._parsePingHeader()
    sonar._recalcRecordNum()
    recompute_track_speed(sonar.header_dat)

    pings_csv = output_dir / "pings.csv"
    sonar.header_dat.to_csv(pings_csv, index=False)

    samples_path = output_dir / "samples.u16le"
    frames_path = output_dir / "frames.jsonl"
    frame_count = write_binary_frames(sonar, samples_path, frames_path)

    waterfall_paths = sonar.write_channel_waterfall_pngs(str(channel_dir))

    channels = []
    for channel_id, png_path in sorted(waterfall_paths.items()):
        group = sonar.header_dat[sonar.header_dat["channel_id"] == channel_id]
        sample_col = "ping_cnt" if "ping_cnt" in group.columns else "sample_cnt"
        max_samples = int(group[sample_col].max()) if len(group) else 0
        time_start = float(group["time_s"].min()) if len(group) else 0.0
        time_end = float(group["time_s"].max()) if len(group) else 0.0

        channels.append(
            {
                "channelId": int(channel_id),
                "waterfall": relpath(Path(png_path), output_dir),
                "rows": int(len(group)),
                "maxSamples": max_samples,
                "timeStart": time_start,
                "timeEnd": time_end,
            }
        )

    manifest = {
        "formatVersion": 2,
        "source": str(rsd_path),
        "telemetry": relpath(pings_csv, output_dir),
        "frames": relpath(frames_path, output_dir),
        "samples": {
            "path": relpath(samples_path, output_dir),
            "encoding": "uint16-le",
        },
        "frameCount": frame_count,
        "channels": channels,
    }

    manifest_path = output_dir / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")

    print(f"Wrote {manifest_path}")
    print(f"Frames: {frame_count}")
    print(f"Channels: {', '.join(str(c['channelId']) for c in channels)}")
    return 0


def recompute_track_speed(df) -> None:
    """Recompute per-frame track distance and speed from WGS84 coordinates."""
    if not {"sequence_cnt", "time_s", "lat", "lon"}.issubset(df.columns):
        return

    frame_track = (
        df.groupby("sequence_cnt", sort=True)
        .agg(time_s=("time_s", "mean"), lat=("lat", "mean"), lon=("lon", "mean"))
        .reset_index()
    )

    distances = [0.0]
    speeds = [math.nan]
    cumulative = [0.0]

    for i in range(1, len(frame_track)):
        prev = frame_track.iloc[i - 1]
        cur = frame_track.iloc[i]
        dist = haversine_m(prev["lat"], prev["lon"], cur["lat"], cur["lon"])
        dt = float(cur["time_s"] - prev["time_s"])

        distances.append(dist)
        speeds.append(dist / dt if dt > 0 else math.nan)
        cumulative.append(cumulative[-1] + dist)

    frame_track["dist"] = distances
    frame_track["speed_ms"] = speeds
    frame_track["trk_dist"] = cumulative

    frame_track["speed_ms"] = frame_track["speed_ms"].interpolate().bfill().ffill()
    frame_track["speed_ms"] = frame_track["speed_ms"].round(2)

    replacements = frame_track.set_index("sequence_cnt")[["dist", "speed_ms", "trk_dist"]]
    for column in replacements.columns:
        df[column] = df["sequence_cnt"].map(replacements[column])


def haversine_m(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    radius_m = 6_371_000.0
    p1 = math.radians(float(lat1))
    p2 = math.radians(float(lat2))
    dp = math.radians(float(lat2) - float(lat1))
    dl = math.radians(float(lon2) - float(lon1))
    a = math.sin(dp / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(dl / 2) ** 2
    return 2 * radius_m * math.atan2(math.sqrt(a), math.sqrt(1 - a))


def write_binary_frames(sonar, samples_path: Path, frames_path: Path) -> int:
    """Write synchronized frame metadata and raw uint16 sample payloads."""
    df = sonar.header_dat.copy()
    sample_col = "ping_cnt" if "ping_cnt" in df.columns else "sample_cnt"
    frame_count = 0
    offset = 0

    with open(sonar.sonFile, "rb") as rsd, samples_path.open("wb") as samples, frames_path.open(
        "w", encoding="utf-8"
    ) as frames:
        for sequence_count, group in df.groupby("sequence_cnt", sort=True):
            channels = []

            for _, row in group.sort_values("channel_id").iterrows():
                try:
                    sample_count = int(row[sample_col])
                    byte_count = sample_count * 2
                    data_size = int(row["data_size"])
                    ping_header_len = int(row.get("ping_header_len", sonar.pingHeaderLen))
                    son_offset = int(row["son_offset"])
                    record_index = int(row["index"])
                except (TypeError, ValueError):
                    continue

                if sample_count <= 0:
                    continue
                if son_offset < ping_header_len:
                    continue
                if son_offset + byte_count > ping_header_len + data_size:
                    continue

                rsd.seek(record_index + son_offset)
                raw = rsd.read(byte_count)
                if len(raw) != byte_count:
                    continue

                samples.write(raw)
                channels.append(
                    {
                        "channelId": int(row["channel_id"]),
                        "sampleOffset": offset,
                        "sampleCount": sample_count,
                        "byteLength": byte_count,
                        "minRangeMeters": none_if_nan(row.get("min_range")),
                        "maxRangeMeters": none_if_nan(row.get("max_range")),
                        "bottomDepthMeters": none_if_nan(row.get("inst_dep_m")),
                    }
                )
                offset += byte_count

            if not channels:
                continue

            frame = {
                "frameIndex": frame_count,
                "sequenceCount": int(sequence_count),
                "timeSeconds": float(group["time_s"].mean()),
                "lat": none_if_nan(group["lat"].mean()),
                "lon": none_if_nan(group["lon"].mean()),
                "speedMetersPerSecond": none_if_nan(group["speed_ms"].mean()),
                "trackDistanceMeters": none_if_nan(group["trk_dist"].mean()),
                "headingDegrees": none_if_nan(group["instr_heading"].mean()),
                "temperatureCelsius": none_if_nan(group["tempC"].mean()),
                "channels": channels,
            }
            frames.write(json.dumps(frame, separators=(",", ":")) + "\n")
            frame_count += 1

    return frame_count


def none_if_nan(value):
    try:
        f = float(value)
    except (TypeError, ValueError):
        return None
    return None if math.isnan(f) else f


def relpath(path: Path, root: Path) -> str:
    return os.path.relpath(path.resolve(), root.resolve()).replace(os.sep, "/")


def load_garmin_parser(pingverter_root: Path):
    """Load garmin_class.py without importing pingverter.__init__."""
    package_dir = pingverter_root / "pingverter"
    garmin_class = package_dir / "garmin_class.py"
    if not garmin_class.is_file():
        raise FileNotFoundError(f"Could not find PINGverter garmin_class.py at {garmin_class}")

    package = types.ModuleType("pingverter")
    package.__path__ = [str(package_dir)]  # type: ignore[attr-defined]
    sys.modules["pingverter"] = package

    spec = importlib.util.spec_from_file_location("pingverter.garmin_class", garmin_class)
    if spec is None or spec.loader is None:
        raise ImportError(f"Could not load {garmin_class}")

    module = importlib.util.module_from_spec(spec)
    sys.modules["pingverter.garmin_class"] = module
    spec.loader.exec_module(module)
    return module.gar


if __name__ == "__main__":
    raise SystemExit(main())
