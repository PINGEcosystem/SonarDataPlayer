namespace SonarDataPlayer.Core;

public static class GarminPalette
{
    public static RgbColor[] Build()
    {
        var stops = new[]
        {
            (Index: 0, Color: new RgbColor(0, 0, 0)),
            (Index: 24, Color: new RgbColor(0, 20, 72)),
            (Index: 64, Color: new RgbColor(0, 92, 160)),
            (Index: 104, Color: new RgbColor(0, 178, 196)),
            (Index: 144, Color: new RgbColor(30, 185, 70)),
            (Index: 184, Color: new RgbColor(230, 205, 45)),
            (Index: 222, Color: new RgbColor(230, 78, 30)),
            (Index: 255, Color: new RgbColor(255, 245, 210))
        };

        var palette = new RgbColor[256];
        for (var i = 0; i < palette.Length; i++)
        {
            for (var stop = 0; stop < stops.Length - 1; stop++)
            {
                var a = stops[stop];
                var b = stops[stop + 1];
                if (i < a.Index || i > b.Index)
                {
                    continue;
                }

                var t = a.Index == b.Index ? 0 : (double)(i - a.Index) / (b.Index - a.Index);
                palette[i] = new RgbColor(
                    Lerp(a.Color.R, b.Color.R, t),
                    Lerp(a.Color.G, b.Color.G, t),
                    Lerp(a.Color.B, b.Color.B, t));
                break;
            }
        }

        return palette;
    }

    private static byte Lerp(byte a, byte b, double t)
    {
        return (byte)Math.Round(a + ((b - a) * t));
    }
}

public readonly record struct RgbColor(byte R, byte G, byte B);
