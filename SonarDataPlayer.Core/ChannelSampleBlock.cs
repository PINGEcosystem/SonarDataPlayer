namespace SonarDataPlayer.Core;

public sealed record ChannelSampleBlock(
    int ChannelId,
    long SampleOffset,
    int SampleCount,
    int ByteLength,
    double? MinimumRangeMeters,
    double? MaximumRangeMeters,
    double? BottomDepthMeters);
