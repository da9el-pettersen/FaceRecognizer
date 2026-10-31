using System.Drawing;
using FaceAiSharp;
using FaceRecognizer.Recognition;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using FaceRecognizer.Contracts;
using FaceRecognizer.Recognition.Pipeline;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FaceRecognizer.Recognition.Liveness;

/// <summary>
/// Liveness med MiniFASNetV2 og MiniFASNetV1SE (Silent-Face-Anti-Spoofing) via ONNX Runtime.
/// Gjennomsnittet av "ekte"-sannsynligheten fra begge modellene brukes som score per bilde. Modellene er trent på RGB og
/// støtter derfor kun fargebilder (<see cref="Supports"/>). Scoren er ikke en kalibrert sannsynlighet.
/// </summary>
public sealed class MiniFasNetLivenessChecker : ILivenessChecker, ILivenessModel, IDisposable
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
    public string Name => "MiniFASNet";

    /// <inheritdoc />
    public float MinScore => _options.MinScore;

    /// <inheritdoc />
    /// <remarks>Modellene er trent på RGB-bilder. IR/svart-hvitt støttes ikke, og gir aldri en liveness-vurdering.</remarks>
    public bool Supports(ImageMode mode) => mode == ImageMode.Color;

    /// <inheritdoc />
    public float ScoreFace(Frame frame, FaceBox box) => ScoreFace(frame.Rgb, frame.Width, frame.Height, box);

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

        var pixels = new byte[source.Width * source.Height * 3];
        source.CopyPixelDataTo(pixels);
        var box = new FaceBox(face.Value.Box.X, face.Value.Box.Y, face.Value.Box.Width, face.Value.Box.Height);
        var score = ScoreFace(pixels, source.Width, source.Height, box);
        return new LivenessResult(score >= _options.MinScore, score);
    }

    /// <summary>Gjennomsnittlig "ekte"-sannsynlighet (0-1) for ett ansikt, over begge modellene.</summary>
    /// <param name="rgb">Hele bildet som 24-bits RGB.</param>
    /// <param name="width">Bildets bredde.</param>
    /// <param name="height">Bildets høyde.</param>
    /// <param name="box">Ansiktsboksen i bildet.</param>
    public float ScoreFace(ReadOnlySpan<byte> rgb, int width, int height, FaceBox box)
    {
        var scores = ScorePerModel(rgb, width, height, box);
        return scores.Average();
    }

    /// <summary>"Ekte"-sannsynlighet fra hver enkelt modell, i samme rekkefølge som de er lastet (V2, V1SE).</summary>
    /// <param name="rgb">Hele bildet som 24-bits RGB.</param>
    /// <param name="width">Bildets bredde.</param>
    /// <param name="height">Bildets høyde.</param>
    /// <param name="box">Ansiktsboksen i bildet.</param>
    internal float[] ScorePerModel(ReadOnlySpan<byte> rgb, int width, int height, FaceBox box)
    {
        var scores = new float[_models.Count];
        for (var i = 0; i < _models.Count; i++)
        {
            var (session, scale, modelHeight, modelWidth) = _models[i];
            var rect = CalculateCropRectangle(width, height, box.X, box.Y, box.Width, box.Height, scale);
            scores[i] = Softmax(Predict(session, rgb, width, rect, modelHeight, modelWidth))[RealClassIndex];
        }

        return scores;
    }

    /// <summary>Rå logits (uten softmax) per modell. Brukes til å verifisere preprocessing mot referanseimplementasjonen.</summary>
    internal float[][] LogitsPerModel(ReadOnlySpan<byte> rgb, int width, int height, FaceBox box)
    {
        var logits = new float[_models.Count][];
        for (var i = 0; i < _models.Count; i++)
        {
            var (session, scale, modelHeight, modelWidth) = _models[i];
            var rect = CalculateCropRectangle(width, height, box.X, box.Y, box.Width, box.Height, scale);
            logits[i] = Predict(session, rgb, width, rect, modelHeight, modelWidth);
        }

        return logits;
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

    private static float[] Predict(
        InferenceSession session, ReadOnlySpan<byte> rgb, int imageWidth, System.Drawing.Rectangle crop, int height, int width)
    {
        // Modellen forventer BGR, 0-255, NCHW uten normalisering (se MiniFasNetPreprocessor).
        var data = new float[3 * height * width];
        MiniFasNetPreprocessor.Fill(data, rgb, imageWidth, crop, width, height);
        var tensor = new DenseTensor<float>(data, [1, 3, height, width]);
        var inputName = session.InputNames[0];
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        return results[0].AsEnumerable<float>().ToArray();
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
