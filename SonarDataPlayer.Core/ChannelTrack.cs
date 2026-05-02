namespace SonarDataPlayer.Core;

public sealed record ChannelTrack(
    int ChannelId,
    string Label,
    string Mode,
    string? Orientation,
    int? Beam,
    int? StartFrequencyHz,
    int? EndFrequencyHz,
    string WaterfallPath,
    int RowCount,
    int MaxSamples,
    double StartTimeSeconds,
    double EndTimeSeconds);
