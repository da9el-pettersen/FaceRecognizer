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

    /// <summary>Mappe med referansebilder, én undermappe per person.</summary>
    public string ReferenceFolder { get; set; } = "/data/faces";

    /// <summary>Minste likhet (0-1) for FaceAiSharp. Må kalibreres mot egne bilder.</summary>
    public float Threshold { get; set; } = 0.98f;
}

/// <summary>Ansiktsmotor basert på FaceAiSharp (lokal ONNX, ingen ekstern tjeneste).</summary>
public sealed class FaceAiSharpRecognitionProvider : IFaceRecognitionProvider
{
    private readonly IFaceDetectorWithLandmarks _detector;
    private readonly IFaceEmbeddingsGenerator _embedder;
    private readonly FaceMatchOptions _options;
    private readonly SemaphoreSlim _referencesLock = new(1, 1);
    private List<(string Name, float[] Embedding)> _references = [];

    /// <summary>Oppretter motoren.</summary>
    public FaceAiSharpRecognitionProvider(
        IFaceDetectorWithLandmarks detector,
        IFaceEmbeddingsGenerator embedder,
        FaceMatchOptions options)
    {
        _detector = detector;
        _embedder = embedder;
        _options = options;
    }

    /// <inheritdoc />
    public string Name => "FaceAiSharp";

    /// <inheritdoc />
    public int ReferenceCount => _references.Count;

    /// <inheritdoc />
    public async Task LoadReferencesAsync(CancellationToken cancellationToken = default)
    {
        var loaded = new List<(string, float[])>();
        foreach (var (personId, path) in ReferenceStore.Enumerate(_options.ReferenceFolder))
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var faces = await Task.Run(() => EmbedAll(bytes), cancellationToken);
            if (faces.Count > 0)
            {
                loaded.Add((personId, faces[0]));
            }
        }

        await _referencesLock.WaitAsync(cancellationToken);
        try
        {
            _references = loaded;
        }
        finally
        {
            _referencesLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<int> DetectFacesAsync(byte[] image, CancellationToken cancellationToken = default) =>
        Task.Run(() => EmbedAll(image).Count, cancellationToken);

    /// <inheritdoc />
    public async Task RegisterAsync(string personId, byte[] image, CancellationToken cancellationToken = default)
    {
        if (!ReferenceStore.IsValidPersonId(personId))
        {
            throw new ArgumentException("Ugyldig person-ID.", nameof(personId));
        }

        var faces = await Task.Run(() => EmbedAll(image), cancellationToken);
        if (faces.Count != 1)
        {
            throw new ArgumentException("Referansebildet må inneholde nøyaktig ett ansikt.", nameof(image));
        }

        await ReferenceStore.SaveAsync(_options.ReferenceFolder, personId, image, cancellationToken);
        await _referencesLock.WaitAsync(cancellationToken);
        try
        {
            _references = [.. _references, (personId, faces[0])];
        }
        finally
        {
            _referencesLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FaceMatchResult>> RecognizeAsync(
        byte[] image, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<FaceMatchResult>>(
            () => EmbedAll(image).Select(Match).ToList(), cancellationToken);

    private FaceMatchResult Match(float[] embedding)
    {
        string? bestName = null;
        var bestScore = float.MinValue;
        foreach (var (name, reference) in _references)
        {
            var score = embedding.Dot(reference);
            if (score > bestScore)
            {
                bestScore = score;
                bestName = name;
            }
        }

        if (bestName is null)
        {
            return new FaceMatchResult(true, null, 0f, false);
        }

        var isMatch = bestScore >= _options.Threshold;
        return new FaceMatchResult(true, isMatch ? bestName : null, bestScore, isMatch);
    }

    // Embedding for alle ansikter med landemerker, største først.
    private List<float[]> EmbedAll(byte[] imageBytes)
    {
        using var source = Image.Load<Rgb24>(imageBytes);
        var faces = _detector.DetectFaces(source)
            .Where(f => f.Landmarks is not null)
            .OrderByDescending(f => f.Box.Width * f.Box.Height)
            .ToList();

        var embeddings = new List<float[]>(faces.Count);
        foreach (var face in faces)
        {
            using var aligned = source.Clone();
            _embedder.AlignFaceUsingLandmarks(aligned, face.Landmarks!);
            embeddings.Add(_embedder.GenerateEmbedding(aligned));
        }

        return embeddings;
    }
}
