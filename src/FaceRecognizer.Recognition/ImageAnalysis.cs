using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FaceRecognizer.Recognition;

/// <summary>Enkel bildeanalyse for å avgjøre om kameraet leverer IR (svart/hvitt).</summary>
public static class ImageAnalysis
{
    private const int Step = 8;
    private const float MaxMeanChroma = 6f;

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
