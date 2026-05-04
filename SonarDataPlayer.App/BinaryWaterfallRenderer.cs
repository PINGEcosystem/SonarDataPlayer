using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SonarDataPlayer.Core;

namespace SonarDataPlayer.App;

public static class BinaryWaterfallRenderer
{
    public static IReadOnlyDictionary<int, BitmapSource> Render(
        SonarRecording recording,
        double? displayMaxRangeMeters = null,
        string? paletteName = null)
    {
        if (recording.SamplesPath is null || recording.Frames.Count == 0)
        {
            return new Dictionary<int, BitmapSource>();
        }

        var channelIds = recording.Frames
            .SelectMany(f => f.Channels.Select(c => c.ChannelId))
            .Distinct()
            .Order()
            .ToArray();

        var maxSamplesByChannel = channelIds.ToDictionary(
            id => id,
            id => recording.Frames
                .SelectMany(f => f.Channels.Where(c => c.ChannelId == id))
                .Select(c => c.SampleCount)
                .DefaultIfEmpty(0)
                .Max());
        var autoMaxRangeMeters = recording.Frames
            .SelectMany(f => f.Channels)
            .Select(c => c.MaximumRangeMeters ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        var renderMaxRangeMeters = displayMaxRangeMeters ?? autoMaxRangeMeters;

        var logMax = FindGlobalLogMax(recording);
        if (logMax <= 0)
        {
            logMax = 1;
        }

        var palette = SonarPaletteCatalog.Build(paletteName);
        var output = new Dictionary<int, BitmapSource>();

        foreach (var channelId in channelIds)
        {
            var width = recording.Frames.Count;
            var height = Math.Max(1, maxSamplesByChannel[channelId]);
            var pixels = new byte[width * height * 4];

            using var stream = File.OpenRead(recording.SamplesPath);
            var x = 0;
            foreach (var frame in recording.Frames)
            {
                var block = frame.Channels.FirstOrDefault(c => c.ChannelId == channelId);
                if (block is not null)
                {
                    FillColumn(
                        stream,
                        block,
                        pixels,
                        x,
                        width,
                        height,
                        renderMaxRangeMeters,
                        logMax,
                        palette);
                }

                x++;
            }

            var bitmap = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                width * 4);
            bitmap.Freeze();
            output[channelId] = bitmap;
        }

        return output;
    }

    private static double FindGlobalLogMax(SonarRecording recording)
    {
        if (recording.SamplesPath is null)
        {
            return 0;
        }

        ushort max = 0;
        var buffer = new byte[8192];
        using var stream = File.OpenRead(recording.SamplesPath);
        foreach (var block in recording.Frames.SelectMany(f => f.Channels))
        {
            stream.Seek(block.SampleOffset, SeekOrigin.Begin);
            var remaining = block.ByteLength;
            while (remaining > 0)
            {
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    break;
                }

                for (var i = 0; i + 1 < read; i += 2)
                {
                    var value = (ushort)(buffer[i] | (buffer[i + 1] << 8));
                    if (value > max)
                    {
                        max = value;
                    }
                }

                remaining -= read;
            }
        }

        return Math.Log(1 + max);
    }

    private static void FillColumn(
        FileStream stream,
        ChannelSampleBlock block,
        byte[] pixels,
        int x,
        int width,
        int height,
        double displayMaxRangeMeters,
        double logMax,
        IReadOnlyList<RgbColor> palette)
    {
        var raw = new byte[block.ByteLength];
        stream.Seek(block.SampleOffset, SeekOrigin.Begin);
        var read = stream.Read(raw, 0, raw.Length);
        if (read != raw.Length)
        {
            return;
        }

        var minRange = block.MinimumRangeMeters ?? 0;
        var maxRange = block.MaximumRangeMeters ?? displayMaxRangeMeters;
        if (displayMaxRangeMeters <= 0 || maxRange <= minRange || block.SampleCount <= 1)
        {
            return;
        }

        for (var y = 0; y < height; y++)
        {
            var depthMeters = height <= 1 ? 0 : (y / (double)(height - 1)) * displayMaxRangeMeters;
            if (depthMeters < minRange || depthMeters > maxRange)
            {
                continue;
            }

            var samplePosition = ((depthMeters - minRange) / (maxRange - minRange)) * (block.SampleCount - 1);
            var sample = Math.Clamp((int)Math.Round(samplePosition), 0, block.SampleCount - 1);
            var rawIndex = sample * 2;
            var value = (ushort)(raw[rawIndex] | (raw[rawIndex + 1] << 8));
            var paletteIndex = Math.Clamp((int)Math.Round((Math.Log(1 + value) / logMax) * 255), 0, 255);
            var color = palette[paletteIndex];

            var pixelIndex = ((y * width) + x) * 4;
            pixels[pixelIndex] = color.B;
            pixels[pixelIndex + 1] = color.G;
            pixels[pixelIndex + 2] = color.R;
            pixels[pixelIndex + 3] = 255;
        }
    }
}
