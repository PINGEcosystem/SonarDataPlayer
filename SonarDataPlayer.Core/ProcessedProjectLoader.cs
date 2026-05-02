using System.Globalization;
using System.Text.Json;

namespace SonarDataPlayer.Core;

public static class ProcessedProjectLoader
{
    public static SonarRecording Load(string manifestPath)
    {
        var projectRoot = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
            ?? throw new InvalidOperationException("Manifest path has no parent directory.");

        using var stream = File.OpenRead(manifestPath);
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Manifest could not be read.");

        var telemetryPath = Resolve(projectRoot, manifest.Telemetry);
        var telemetry = File.Exists(telemetryPath)
            ? LoadTelemetryCsv(telemetryPath)
            : Array.Empty<PingTelemetry>();

        var channels = manifest.Channels
            .Select(c => new ChannelTrack(
                c.ChannelId,
                Resolve(projectRoot, c.Waterfall),
                c.Rows,
                c.MaxSamples,
                c.TimeStart,
                c.TimeEnd))
            .ToArray();

        return new SonarRecording(manifest.Source ?? string.Empty, channels, telemetry);
    }

    private static IReadOnlyList<PingTelemetry> LoadTelemetryCsv(string path)
    {
        var lines = File.ReadLines(path).ToArray();
        if (lines.Length < 2)
        {
            return Array.Empty<PingTelemetry>();
        }

        var headers = SplitCsvLine(lines[0]);
        var index = headers
            .Select((name, i) => (name, i))
            .ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);

        var rows = new List<PingTelemetry>(lines.Length - 1);
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = SplitCsvLine(lines[i]);
            rows.Add(new PingTelemetry(
                Long(cells, index, "record_num"),
                Int(cells, index, "channel_id"),
                Double(cells, index, "time_s"),
                Int(cells, index, "ping_cnt", "sample_cnt"),
                NullableDouble(cells, index, "min_range", "first_sample_depth"),
                NullableDouble(cells, index, "max_range", "last_sample_depth"),
                NullableDouble(cells, index, "inst_dep_m", "bottom_depth"),
                NullableDouble(cells, index, "lat"),
                NullableDouble(cells, index, "lon"),
                NullableDouble(cells, index, "speed_ms"),
                NullableDouble(cells, index, "instr_heading"),
                NullableDouble(cells, index, "tempC", "water_temp")));
        }

        return rows.OrderBy(r => r.TimeSeconds).ToArray();
    }

    private static string Resolve(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(root, path));
    }

    private static string[] SplitCsvLine(string line)
    {
        return line.Split(',');
    }

    private static int Int(string[] cells, IReadOnlyDictionary<string, int> index, params string[] names)
    {
        return (int)Long(cells, index, names);
    }

    private static long Long(string[] cells, IReadOnlyDictionary<string, int> index, params string[] names)
    {
        foreach (var name in names)
        {
            if (index.TryGetValue(name, out var i) &&
                i < cells.Length &&
                long.TryParse(cells[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return 0;
    }

    private static double Double(string[] cells, IReadOnlyDictionary<string, int> index, params string[] names)
    {
        return NullableDouble(cells, index, names) ?? 0;
    }

    private static double? NullableDouble(string[] cells, IReadOnlyDictionary<string, int> index, params string[] names)
    {
        foreach (var name in names)
        {
            if (index.TryGetValue(name, out var i) &&
                i < cells.Length &&
                double.TryParse(cells[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record ProjectManifest(
        string? Source,
        string? Telemetry,
        ChannelManifest[] Channels);

    private sealed record ChannelManifest(
        int ChannelId,
        string Waterfall,
        int Rows,
        int MaxSamples,
        double TimeStart,
        double TimeEnd);
}
