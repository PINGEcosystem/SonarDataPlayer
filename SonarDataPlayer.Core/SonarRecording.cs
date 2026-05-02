namespace SonarDataPlayer.Core;

public sealed record SonarRecording(
    string SourcePath,
    IReadOnlyList<ChannelTrack> Channels,
    IReadOnlyList<PingTelemetry> Telemetry)
{
    public double DurationSeconds =>
        Telemetry.Count == 0 ? 0 : Telemetry[^1].TimeSeconds - Telemetry[0].TimeSeconds;

    public PingTelemetry? FindNearestTelemetry(double timeSeconds)
    {
        if (Telemetry.Count == 0)
        {
            return null;
        }

        var lo = 0;
        var hi = Telemetry.Count - 1;

        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (Telemetry[mid].TimeSeconds < timeSeconds)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        if (lo == 0)
        {
            return Telemetry[0];
        }

        var before = Telemetry[lo - 1];
        var after = Telemetry[lo];
        return Math.Abs(before.TimeSeconds - timeSeconds) <= Math.Abs(after.TimeSeconds - timeSeconds)
            ? before
            : after;
    }
}
