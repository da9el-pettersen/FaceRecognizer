using FaceAiSharp;
using FaceAiSharp.Extensions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FaceRecognizer.Recognition.Engines;

/// <summary>Innstillinger for ansiktsmatching.</summary>
public sealed class FaceMatchOptions
{
    /// <summary>Motor som brukes: <c>FaceAiSharp</c> (standard) eller <c>DeepFace</c>.</summary>
    public string Provider { get; set; } = "FaceAiSharp";

    /// <summary>Minste likhet (0-1) for FaceAiSharp. Må kalibreres mot egne bilder.</summary>
    public float Threshold { get; set; } = 0.62f;
}

/// <summary>Ansiktsmotor basert på FaceAiSharp (lokal ONNX, ingen ekstern tjeneste).</summary>
public sealed class FaceAiSharpRecognitionProvider : IFaceRecognitionProvider
{
    // Modellversjon for FaceAiSharp.Bundle 0.6.35: SCRFD-detektor og ArcFace (w600k_r50) med landemerkejustering.
    // Endres hvis pakken bytter modell, slik at gamle embeddings ikke blandes med nye.
    internal const string DefaultModelId = "faceaisharp-bundle-0.6.35:scrfd+arcface-w600k_r50";

    private readonly IFaceDetectorWithLandmarks _detector;
    private readonly IFaceEmbeddingsGenerator _embedder;
    private readonly FaceMatchOptions _options;
    private readonly FaceMetrics _metrics;

    /// <summary>Oppretter motoren.</summary>
    public FaceAiSharpRecognitionProvider(
        IFaceDetectorWithLandmarks detector,
        IFaceEmbeddingsGenerator embedder,
        FaceMatchOptions options,
        IEmbeddingSource source,
        FaceMetrics? metrics = null,
        ILogger<FaceAiSharpRecognitionProvider>? logger = null)
    {
        _detector = detector;
        _embedder = embedder;
        _options = options;
        _metrics = metrics ?? new FaceMetrics();
        References = new FaceReferenceService(DefaultModelId, source, logger);
    }

    /// <inheritdoc />
    public string Name => "FaceAiSharp";

    /// <inheritdoc />
    public string ModelId => References.ModelId;

    /// <summary>Minneindeksen for denne motoren.</summary>
    public FaceReferenceService References { get; }

    /// <inheritdoc />
    public int ReferenceCount => References.Index.EmbeddingCount;

    /// <inheritdoc />
    public Task LoadReferencesAsync(CancellationToken cancellationToken = default) =>
        References.LoadAsync(cancellationToken);

    /// <inheritdoc />
    public Task<int> DetectFacesAsync(byte[] image, CancellationToken cancellationToken = default) =>
        Task.Run(() => EmbedAll(image).Count, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<float[]>> EmbedAsync(byte[] image, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<float[]>>(() => EmbedAll(image), cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FaceMatchResult>> RecognizeAsync(
        byte[] image, CancellationToken cancellationToken = default)
    {
        await References.EnsureLoadedAsync(cancellationToken);
        return await Task.Run<IReadOnlyList<FaceMatchResult>>(
            () => EmbedAll(image).Select(Match).ToList(), cancellationToken);
    }

    private FaceMatchResult Match(float[] embedding)
    {
        var start = FaceMetrics.Start();
        var result = References.Match(embedding, _options.Threshold);
        _metrics.Record(FaceStage.Matching, start);
        return result;
    }

    // Normaliserte embeddings for alle ansikter med landemerker, største først.
    private List<float[]> EmbedAll(byte[] imageBytes)
    {
        using var source = Image.Load<Rgb24>(imageBytes);
        var detectStart = FaceMetrics.Start();
        var faces = _detector.DetectFaces(source)
            .Where(f => f.Landmarks is not null)
            .OrderByDescending(f => f.Box.Width * f.Box.Height)
            .ToList();
        _metrics.Record(FaceStage.Detection, detectStart);

        var embeddings = new List<float[]>(faces.Count);
        foreach (var face in faces)
        {
            var embedStart = FaceMetrics.Start();
            using var aligned = source.Clone();
            _embedder.AlignFaceUsingLandmarks(aligned, face.Landmarks!);
            var embedding = _embedder.GenerateEmbedding(aligned);
            _metrics.Record(FaceStage.Embedding, embedStart);
            if (EmbeddingMath.Normalize(embedding))
            {
                embeddings.Add(embedding);
            }
        }

        return embeddings;
    }
}

