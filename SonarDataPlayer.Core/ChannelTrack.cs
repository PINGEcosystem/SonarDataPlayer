namespace SonarDataPlayer.Core;

public sealed record ChannelTrack(
    int ChannelId,
    string WaterfallPath,
    int RowCount,
    int MaxSamples,
    double StartTimeSeconds,
    double EndTimeSeconds);
