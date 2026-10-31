using FaceAiSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FaceRecognizer.Recognition.Pipeline;

/// <summary>Finner ansikter i ett videobilde. Boksene er i bildets egne koordinater.</summary>
public interface IFrameFaceDetector
{
    /// <summary>Finner ansikter. Kalles én gang per bilde (resultatet bufres på bildet av kalleren).</summary>
    /// <param name="frame">Bildet.</param>
    IReadOnlyList<FaceBox> Detect(Frame frame);
}

/// <summary>
/// Ansiktsdeteksjon med FaceAiSharp på et nedskalert bilde (for CPU), med boksene skalert tilbake til analyseoppløsningen
/// slik at gjenkjenning og liveness får full oppløsning. Egen detektor-instans, beskyttet av lås.
/// </summary>
public sealed class FaceAiSharpFrameDetector : IFrameFaceDetector
{
    private readonly IFaceDetectorWithLandmarks _detector;
    private readonly int _detectionWidth;
    private readonly float _minConfidence;
    private readonly object _lock = new();

    /// <summary>Oppretter detektoren.</summary>
    /// <param name="detector">Egen detektor-instans.</param>
    /// <param name="detectionWidth">Bredden bildet skaleres ned til før deteksjon (0 = ingen nedskalering).</param>
    /// <param name="minConfidence">Laveste konfidens for et ansikt.</param>
    public FaceAiSharpFrameDetector(IFaceDetectorWithLandmarks detector, int detectionWidth, float minConfidence = 0.5f)
    {
        _detector = detector;
        _detectionWidth = detectionWidth;
        _minConfidence = minConfidence;
    }

    /// <inheritdoc />
    public IReadOnlyList<FaceBox> Detect(Frame frame)
    {
        using var full = Image.LoadPixelData<Rgb24>(frame.Rgb, frame.Width, frame.Height);
        var factor = 1f;
        var image = full;
        Image<Rgb24>? small = null;
        if (_detectionWidth > 0 && frame.Width > _detectionWidth)
        {
            small = full.Clone(c => c.Resize(_detectionWidth, 0));
            factor = (float)frame.Width / small.Width;
            image = small;
        }

        try
        {
            IEnumerable<FaceDetectorResult> faces;
            lock (_lock)
            {
                faces = _detector.DetectFaces(image).ToList();
            }

            return faces
                .Where(f => f.Confidence is null || f.Confidence >= _minConfidence)
                .Select(f => new FaceBox(f.Box.X, f.Box.Y, f.Box.Width, f.Box.Height).Scale(factor))
                .ToArray();
        }
        finally
        {
            small?.Dispose();
        }
    }
}
