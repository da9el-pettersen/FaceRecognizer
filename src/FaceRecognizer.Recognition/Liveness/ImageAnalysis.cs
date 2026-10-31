using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FaceRecognizer.Recognition.Liveness;

/// <summary>Enkel bildeanalyse for å avgjøre om kameraet leverer IR (svart/hvitt).</summary>
public static class ImageAnalysis
{
    private const int Step = 8;
    private const int RawStep = 16;
    private const float MaxMeanChroma = 6f;

    /// <summary>
    /// Gjennomsnittlig forskjell mellom største og minste fargekanal (0-255) på et utvalg piksler i et rått RGB-bilde.
    /// Svart/hvitt-bilder ligger nær 0, men også mørke eller avfargede fargebilder kan ligge lavt.
    /// </summary>
    /// <param name="rgb">Piksler som 24-bits RGB.</param>
    /// <param name="width">Bredde.</param>
    /// <param name="height">Høyde.</param>
    public static double MeanChroma(ReadOnlySpan<byte> rgb, int width, int height)
    {
        double total = 0;
        var count = 0;
        for (var y = 0; y < height; y += RawStep)
        {
            for (var x = 0; x < width; x += RawStep)
            {
                var i = (y * width + x) * 3;
                var r = rgb[i];
                var g = rgb[i + 1];
                var b = rgb[i + 2];
                total += Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                count++;
            }
        }

        return count > 0 ? total / count : 0;
    }

    /// <summary>
    /// Returnerer <c>true</c> når bildet er (nesten) gråtone, slik IR/nattmodus gir.
    /// Måler gjennomsnittlig forskjell mellom fargekanalene på et utvalg piksler.
    /// </summary>
    /// <param name="imageBytes">JPEG- eller PNG-bilde.</param>
    public static bool IsGrayscale(byte[] imageBytes)
    {
        using var image = Image.Load<Rgb24>(imageBytes);
        double total = 0;
        var count = 0;
        for (var y = 0; y < image.Height; y += Step)
        {
            for (var x = 0; x < image.Width; x += Step)
            {
                var p = image[x, y];
                total += Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B));
                count++;
            }
        }

        return count > 0 && total / count < MaxMeanChroma;
    }
}
