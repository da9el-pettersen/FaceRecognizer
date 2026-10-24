using System.Drawing;
using FaceAiSharp;
using FaceRecognizer.Recognition;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FaceRecognizer.Recognition;

/// <summary>
/// Liveness med MiniFASNetV2 og MiniFASNetV1SE (Silent-Face-Anti-Spoofing) via ONNX Runtime.
/// Gjennomsnittet av "ekte"-sannsynligheten fra begge modellene må være over terskelen. Modellene er trent på RGB, så scoren på
/// IR-bilder må kalibreres mot egne opptak.
/// </summary>
public sealed class MiniFasNetLivenessChecker : ILivenessChecker, IDisposable
{
    private const int RealClassIndex = 1;

    private readonly IFaceDetectorWithLandmarks _detector;
    private readonly LivenessOptions _options;
    private readonly List<(InferenceSession Session, float Scale, int Height, int Width)> _models = [];

    /// <summary>Laster modellene fra <see cref="LivenessOptions.ModelFolder"/>. Kaster om de mangler.</summary>
    public MiniFasNetLivenessChecker(IFaceDetectorWithLandmarks detector, IOptions<LivenessOptions> options)
    {
        _detector = detector;
        _options = options.Value;
        Load("MiniFASNetV2.onnx", 2.7f);
        Load("MiniFASNetV1SE.onnx", 4.0f);
    }

    /// <inheritdoc />
    public LivenessResult Check(byte[] image)
    {
        using var source = Image.Load<Rgb24>(image);
        var face = _detector.DetectFaces(source)
            .OrderByDescending(f => f.Box.Width * f.Box.Height)
            .Cast<FaceDetectorResult?>()
            .FirstOrDefault();
        if (face is null)
        {
            return new LivenessResult(false, 0f);
        }

        var total = 0f;
        foreach (var (session, scale, height, width) in _models)
        {
            var rect = CalculateCropRectangle(
                source.Width, source.Height, face.Value.Box.X, face.Value.Box.Y,
                face.Value.Box.Width, face.Value.Box.Height, scale);
            using var crop = source.Clone(ctx => ctx
                .Crop(new SixLabors.ImageSharp.Rectangle(rect.X, rect.Y, rect.Width, rect.Height))
                .Resize(width, height, KnownResamplers.Triangle));

            var real = Predict(session, crop, height, width);
            total += real;
        }

        var score = total / _models.Count;
        return new LivenessResult(score >= _options.MinScore, score);
    }

    /// <summary>Frigjør ONNX-sesjonene.</summary>
    public void Dispose()
    {
        foreach (var model in _models)
        {
            model.Session.Dispose();
        }
    }

    /// <summary>Beregner utsnittet rundt ansiktet, identisk med referanseimplementasjonen.</summary>
    internal static System.Drawing.Rectangle CalculateCropRectangle(
        int imageWidth, int imageHeight, float x, float y, float boxWidth, float boxHeight, float maxScale)
    {
        var scale = Math.Min(Math.Min((imageHeight - 1) / boxHeight, (imageWidth - 1) / boxWidth), maxScale);
        var newWidth = boxWidth * scale;
        var newHeight = boxHeight * scale;
        var centerX = x + boxWidth / 2;
        var centerY = y + boxHeight / 2;

        var x1 = Math.Max(0, (int)(centerX - newWidth / 2));
        var y1 = Math.Max(0, (int)(centerY - newHeight / 2));
        var x2 = Math.Min(imageWidth - 1, (int)(centerX + newWidth / 2));
        var y2 = Math.Min(imageHeight - 1, (int)(centerY + newHeight / 2));
        return new System.Drawing.Rectangle(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
    }

    /// <summary>Softmax over logits.</summary>
    internal static float[] Softmax(ReadOnlySpan<float> logits)
    {
        var max = logits.ToArray().Max();
        var exp = logits.ToArray().Select(v => MathF.Exp(v - max)).ToArray();
        var sum = exp.Sum();
        return exp.Select(v => v / sum).ToArray();
    }

    private static float Predict(InferenceSession session, Image<Rgb24> crop, int height, int width)
    {
        // Modellen forventer BGR, 0-255, NCHW uten normalisering.
        var tensor = new DenseTensor<float>([1, 3, height, width]);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = crop[x, y];
                tensor[0, 0, y, x] = p.B;
                tensor[0, 1, y, x] = p.G;
                tensor[0, 2, y, x] = p.R;
            }
        }

        var inputName = session.InputNames[0];
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        var logits = results[0].AsEnumerable<float>().ToArray();
        return Softmax(logits)[RealClassIndex];
    }

    private void Load(string fileName, float scale)
    {
        var path = Path.Combine(_options.ModelFolder, fileName);
        var session = new InferenceSession(path);
        var dims = session.InputMetadata[session.InputNames[0]].Dimensions;
        var height = dims[2] > 0 ? dims[2] : 80;
        var width = dims[3] > 0 ? dims[3] : 80;
        _models.Add((session, scale, height, width));
    }
}
