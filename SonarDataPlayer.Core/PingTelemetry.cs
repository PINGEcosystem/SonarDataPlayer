namespace SonarDataPlayer.Core;

public sealed record PingTelemetry(
    long RecordNumber,
    int ChannelId,
    double TimeSeconds,
    int SampleCount,
    double? MinimumRangeMeters,
    double? MaximumRangeMeters,
    double? DepthMeters,
    double? Latitude,
    double? Longitude,
    double? SpeedMetersPerSecond,
    double? HeadingDegrees,
    double? TemperatureCelsius);
