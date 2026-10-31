using FaceRecognizer.Recognition.Liveness;
using FaceRecognizer.Recognition.Pipeline;
using Microsoft.Extensions.Options;
using Xunit;

namespace FaceRecognizer.Recognition.Tests;

/// <summary>
/// Verifiserer MiniFASNet-preprocessing mot gullverdier generert med referanseimplementasjonen (Python, cv2 + onnxruntime)
/// for nøyaktig de ONNX-filene som brukes. Pikseltestene kjører alltid; modelltestene krever miljøvariabelen
/// <c>MINIFASNET_MODEL_DIR</c> (mappe med MiniFASNetV2.onnx og MiniFASNetV1SE.onnx).
/// </summary>
public class MiniFasNetPreprocessingTests
{
    private const int ImageWidth = 640;
    private const int ImageHeight = 480;
    private static readonly FaceBox Box = new(280, 170, 80, 90);

    private static byte[] SyntheticImage()
    {
        var rgb = new byte[ImageWidth * ImageHeight * 3];
        for (var y = 0; y < ImageHeight; y++)
        {
            for (var x = 0; x < ImageWidth; x++)
            {
                var i = (y * ImageWidth + x) * 3;
                rgb[i] = (byte)((x * 7 + y * 13) & 255);
                rgb[i + 1] = (byte)(((x ^ y) * 5 + ((x * y) >> 4)) & 255);
                rgb[i + 2] = (byte)((x * 3 + y * 11 + ((x * x + y * y) >> 6)) & 255);
            }
        }

        return rgb;
    }

    [Theory]
    [InlineData(2.7f, 212, 93, 217, 244)]
    [InlineData(4.0f, 160, 35, 321, 361)]
    public void Beskjaering_er_lik_referansen(float scale, int x, int y, int w, int h)
    {
        var rect = MiniFasNetLivenessChecker.CalculateCropRectangle(ImageWidth, ImageHeight, Box.X, Box.Y, Box.Width, Box.Height, scale);

        Assert.Equal(new System.Drawing.Rectangle(x, y, w, h), rect);
    }

    public static TheoryData<float, int[][]> PixelSamples => new()
    {
        { 2.7f, [[0, 0, 214, 153, 152], [10, 70, 234, 230, 86], [40, 40, 51, 239, 194], [79, 5, 12, 226, 51], [79, 79, 98, 98, 177], [25, 33, 53, 39, 234]] },
        { 4.0f, [[0, 0, 37, 14, 72], [10, 70, 33, 99, 65], [40, 40, 81, 23, 214], [79, 5, 121, 162, 239], [79, 79, 204, 158, 13], [25, 33, 130, 50, 162]] }
    };

    [Theory]
    [MemberData(nameof(PixelSamples))]
    public void Tensor_er_BGR_0_til_255_og_cv2_kompatibel(float scale, int[][] samples)
    {
        var rgb = SyntheticImage();
        var rect = MiniFasNetLivenessChecker.CalculateCropRectangle(ImageWidth, ImageHeight, Box.X, Box.Y, Box.Width, Box.Height, scale);
        var tensor = new float[3 * 80 * 80];

        MiniFasNetPreprocessor.Fill(tensor, rgb, ImageWidth, rect, 80, 80);

        foreach (var s in samples)
        {
            var row = s[0];
            var col = s[1];
            for (var channel = 0; channel < 3; channel++)
            {
                var actual = tensor[channel * 80 * 80 + row * 80 + col];
                Assert.InRange(actual, s[2 + channel] - 2, s[2 + channel] + 2);
            }
        }
    }

    [Fact]
    public void Logits_og_sannsynlighet_er_lik_referansen_for_begge_modellene()
    {
        var folder = Environment.GetEnvironmentVariable("MINIFASNET_MODEL_DIR");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        using var checker = new MiniFasNetLivenessChecker(
            FaceAiSharp.FaceAiSharpBundleFactory.CreateFaceDetectorWithLandmarks(),
            Options.Create(new LivenessOptions { ModelFolder = folder }));
        var rgb = SyntheticImage();

        var logits = checker.LogitsPerModel(rgb, ImageWidth, ImageHeight, Box);
        var scores = checker.ScorePerModel(rgb, ImageWidth, ImageHeight, Box);

        float[][] expected = [[0.2101f, -1.0195f, 0.8094f], [-1.2632f, 2.1157f, -0.8534f]];
        float[] expectedReal = [0.0939f, 0.9213f];
        for (var m = 0; m < 2; m++)
        {
            for (var c = 0; c < 3; c++)
            {
                Assert.InRange(logits[m][c], expected[m][c] - 0.05f, expected[m][c] + 0.05f);
            }

            Assert.InRange(scores[m], expectedReal[m] - 0.05f, expectedReal[m] + 0.05f);
        }
    }
}
