namespace SonarDataPlayer.Core;

public sealed record SonarFrame(
    int FrameIndex,
    int SequenceCount,
    double TimeSeconds,
    double? Latitude,
    double? Longitude,
    double? SpeedMetersPerSecond,
    double? TrackDistanceMeters,
    double? HeadingDegrees,
    double? TemperatureCelsius,
    IReadOnlyList<ChannelSampleBlock> Channels);
