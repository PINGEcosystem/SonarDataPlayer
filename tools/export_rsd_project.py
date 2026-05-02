"""Export a Garmin RSD file into a SonarDataPlayer processed project.

This is a bridge tool while the Garmin RSD parser is still being ported from
PINGverter into SonarDataPlayer.Core.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
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

    pings_csv = output_dir / "pings.csv"
    sonar.header_dat.to_csv(pings_csv, index=False)

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
        "source": str(rsd_path),
        "telemetry": relpath(pings_csv, output_dir),
        "channels": channels,
    }

    manifest_path = output_dir / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")

    print(f"Wrote {manifest_path}")
    print(f"Channels: {', '.join(str(c['channelId']) for c in channels)}")
    return 0


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
