using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FaceRecognizer.Recognition.Tests;

public class ImageAnalysisTests
{
    private static byte[] Encode(Rgb24 color)
    {
        using var image = new Image<Rgb24>(64, 64, color);
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream);
        return stream.ToArray();
    }

    [Fact]
    public void Gratonebilde_regnes_som_IR()
    {
        Assert.True(ImageAnalysis.IsGrayscale(Encode(new Rgb24(120, 120, 120))));
    }

    [Fact]
    public void Fargebilde_regnes_ikke_som_IR()
    {
        Assert.False(ImageAnalysis.IsGrayscale(Encode(new Rgb24(200, 80, 60))));
    }
}
