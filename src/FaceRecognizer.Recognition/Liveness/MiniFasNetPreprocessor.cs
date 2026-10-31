using System.Drawing;

namespace FaceRecognizer.Recognition.Liveness;

/// <summary>
/// Forbereder input til MiniFASNet slik referanseimplementasjonen gjør det (yakhyo/face-anti-spoofing, <c>onnx_inference.py</c>):
/// utsnitt rundt ansiktet, <c>cv2.resize</c> (bilinear uten antialiasing) til 80x80, BGR, verdier 0-255 uten normalisering, NCHW.
/// Verifisert mot nøyaktig de ONNX-filene som brukes: grafen starter direkte med <c>Conv</c>, så ingen normalisering ligger i modellen.
/// </summary>
internal static class MiniFasNetPreprocessor
{
    /// <summary>
    /// Fyller <paramref name="tensor"/> (kanal-for-kanal, BGR) fra RGB-piksler. Skriver direkte uten mellombilder.
    /// </summary>
    /// <param name="tensor">Mål med lengde 3 * <paramref name="height"/> * <paramref name="width"/>.</param>
    /// <param name="rgb">Bildet som 24-bits RGB, rad for rad.</param>
    /// <param name="imageWidth">Bildets bredde.</param>
    /// <param name="crop">Utsnittet som skal skaleres (fra <c>CalculateCropRectangle</c>).</param>
    /// <param name="width">Modellens inputbredde.</param>
    /// <param name="height">Modellens inputhøyde.</param>
    public static void Fill(
        Span<float> tensor, ReadOnlySpan<byte> rgb, int imageWidth, Rectangle crop, int width, int height)
    {
        var plane = width * height;
        Span<int> x0 = stackalloc int[width];
        Span<int> x1 = stackalloc int[width];
        Span<float> wx = stackalloc float[width];
        var scaleX = (double)crop.Width / width;
        var scaleY = (double)crop.Height / height;
        for (var x = 0; x < width; x++)
        {
            Sample(x, scaleX, crop.Width, out x0[x], out x1[x], out wx[x]);
        }

        for (var y = 0; y < height; y++)
        {
            Sample(y, scaleY, crop.Height, out var y0, out var y1, out var wy);
            var row0 = (crop.Y + y0) * imageWidth + crop.X;
            var row1 = (crop.Y + y1) * imageWidth + crop.X;
            for (var x = 0; x < width; x++)
            {
                var a = (row0 + x0[x]) * 3;
                var b = (row0 + x1[x]) * 3;
                var c = (row1 + x0[x]) * 3;
                var d = (row1 + x1[x]) * 3;
                var index = y * width + x;
                for (var channel = 0; channel < 3; channel++)
                {
                    // Utdata er BGR, kilden er RGB: kanal 0 (B) leses fra byte 2.
                    var offset = 2 - channel;
                    var top = rgb[a + offset] + (rgb[b + offset] - rgb[a + offset]) * wx[x];
                    var bottom = rgb[c + offset] + (rgb[d + offset] - rgb[c + offset]) * wx[x];
                    var value = top + (bottom - top) * wy;
                    // OpenCV avrunder til 8-bit før konvertering til float.
                    tensor[channel * plane + index] = MathF.Floor(value + 0.5f);
                }
            }
        }
    }

    // Samme kildeposisjon og kantbehandling som cv2.resize (INTER_LINEAR): halv-piksel-sentre, klemt til utsnittet.
    private static void Sample(int destination, double scale, int sourceSize, out int first, out int second, out float weight)
    {
        var position = (destination + 0.5) * scale - 0.5;
        var floor = (int)Math.Floor(position);
        var fraction = (float)(position - floor);
        if (floor < 0)
        {
            floor = 0;
            fraction = 0;
        }

        if (floor >= sourceSize - 1)
        {
            floor = sourceSize - 1;
            fraction = 0;
        }

        first = floor;
        second = Math.Min(floor + 1, sourceSize - 1);
        weight = fraction;
    }
}
