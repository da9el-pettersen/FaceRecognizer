using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace FaceRecognizer.Recognition.Engines;

/// <summary>Innstillinger for DeepFace REST API.</summary>
public sealed class DeepFaceOptions
{
    /// <summary>Intern adresse til DeepFace-containeren (aldri eksponert mot internett).</summary>
    public string BaseUrl { get; set; } = "http://deepface:5000";

    /// <summary>Hemmelig token (<c>DEEPFACE_AUTH_TOKEN</c> i DeepFace). Skal aldri ligge i Git eller i imaget.</summary>
    public string AuthToken { get; set; } = "";

    /// <summary>Gjenkjenningsmodell. SFace (Apache-2.0) er standard, vektene er bakt inn i DeepFace-imaget.</summary>
    public string ModelName { get; set; } = "SFace";

    /// <summary>Ansiktsdetektor. YuNet (MIT) er standard.</summary>
    public string DetectorBackend { get; set; } = "yunet";

    /// <summary>Minste cosinus-likhet (1 - cosinusdistanse) for match. SFace sin anbefalte grense er 0.407.</summary>
    public float Threshold { get; set; } = 0.5f;

    /// <summary>Minste deteksjonssikkerhet (0-1) for at et ansikt telles.</summary>
    public float MinFaceConfidence { get; set; } = 0.7f;

    /// <summary>Maks antall ansikter per bilde.</summary>
    public int MaxFaces { get; set; } = 5;

    /// <summary>Tidsavbrudd per forespørsel.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Antall nye forsøk ved forbigående feil.</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Ventetid mellom forsøk (dobles for hvert forsøk).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Ansiktsmotor som bruker DeepFace REST API (<c>/represent</c>). Embeddings holdes kun i minnet og sammenlignes lokalt;
/// bilder og embeddings logges eller publiseres aldri.
/// </summary>
public sealed class DeepFaceRecognitionProvider : IFaceRecognitionProvider
{
    private readonly HttpClient _http;
    private readonly DeepFaceOptions _options;
    private readonly ILogger<DeepFaceRecognitionProvider> _logger;
    private readonly FaceMetrics _metrics;

    /// <summary>Oppretter motoren.</summary>
    public DeepFaceRecognitionProvider(
        HttpClient http,
        IOptions<DeepFaceOptions> options,
        IEmbeddingSource source,
        ILogger<DeepFaceRecognitionProvider> logger,
        FaceMetrics? metrics = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _metrics = metrics ?? new FaceMetrics();
        // Detektor og justering påvirker ansiktsutsnittet, så de er en del av modellversjonen.
        References = new FaceReferenceService(
            $"deepface:{_options.ModelName}:{_options.DetectorBackend}:align", source, logger);
    }

    /// <inheritdoc />
    public string Name => "DeepFace";

    /// <inheritdoc />
    public string ModelId => References.ModelId;

    /// <summary>Minneindeksen for denne motoren.</summary>
    public FaceReferenceService References { get; }

    /// <inheritdoc />
    public int ReferenceCount => References.Index.EmbeddingCount;

    /// <summary>Sjekker at DeepFace svarer.</summary>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("/", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    /// <inheritdoc />
    public Task LoadReferencesAsync(CancellationToken cancellationToken = default) =>
        References.LoadAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<int> DetectFacesAsync(byte[] image, CancellationToken cancellationToken = default) =>
        (await RepresentAsync(image, cancellationToken)).Count;

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> EmbedAsync(byte[] image, CancellationToken cancellationToken = default) =>
        (await RepresentAsync(image, cancellationToken)).Select(f => f.Embedding).ToList();

    /// <inheritdoc />
    public async Task<IReadOnlyList<FaceMatchResult>> RecognizeAsync(
        byte[] image, CancellationToken cancellationToken = default)
    {
        // Lastes først ved behov, så en tjeneste som er nede ved oppstart ikke stopper gjenkjenning.
        await References.EnsureLoadedAsync(cancellationToken);

        var faces = await RepresentAsync(image, cancellationToken);
        return faces.Select(face => Match(face.Embedding)).ToList();
    }

    private FaceMatchResult Match(float[] embedding)
    {
        var start = FaceMetrics.Start();
        var result = References.Match(embedding, _options.Threshold);
        _metrics.Record(FaceStage.Matching, start);
        return result;
    }

    internal static float CosineSimilarity(float[] a, float[] b) => EmbeddingMath.CosineSimilarity(a, b);

    // Returnerer ansikter over sikkerhetsgrensen, største først.
    private async Task<List<RepresentedFace>> RepresentAsync(byte[] image, CancellationToken cancellationToken)
    {
        var request = new RepresentRequest(
            "data:image/jpeg;base64," + Convert.ToBase64String(image),
            _options.ModelName,
            _options.DetectorBackend,
            false,
            true,
            _options.MaxFaces);

        for (var attempt = 0; ; attempt++)
        {
            var start = FaceMetrics.Start();
            try
            {
                using var response = await _http.PostAsJsonAsync("/represent", request, cancellationToken);
                if ((int)response.StatusCode >= 500)
                {
                    throw new HttpRequestException($"DeepFace svarte {(int)response.StatusCode}.");
                }

                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    throw new InvalidOperationException("DeepFace avviste bildet (400).");
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException("DeepFace avviste tokenet. Sjekk DeepFace:AuthToken.");
                }

                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<RepresentResponse>(cancellationToken)
                    ?? throw new InvalidOperationException("Tomt svar fra DeepFace.");
                var faces = (body.Results ?? [])
                    .Where(r => r.FaceConfidence >= _options.MinFaceConfidence && r.Embedding.Length > 0)
                    .OrderByDescending(r => r.FacialArea.Width * r.FacialArea.Height)
                    .Where(r => EmbeddingMath.Normalize(r.Embedding))
                    .Select(r => new RepresentedFace(r.Embedding))
                    .ToList();
                _metrics.Record(FaceStage.DetectionAndEmbedding, start);
                return faces;
            }
            catch (Exception ex) when (
                attempt < _options.MaxRetries
                && !cancellationToken.IsCancellationRequested
                && ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(
                    "DeepFace utilgjengelig (forsøk {Attempt} av {Max}): {Type}.",
                    attempt + 1, _options.MaxRetries + 1, ex.GetType().Name);
                await Task.Delay(_options.RetryDelay * Math.Pow(2, attempt), cancellationToken);
            }
        }
    }

    private sealed record RepresentedFace(float[] Embedding);

    private sealed record RepresentRequest(
        [property: JsonPropertyName("img")] string Img,
        [property: JsonPropertyName("model_name")] string ModelName,
        [property: JsonPropertyName("detector_backend")] string DetectorBackend,
        [property: JsonPropertyName("enforce_detection")] bool EnforceDetection,
        [property: JsonPropertyName("align")] bool Align,
        [property: JsonPropertyName("max_faces")] int MaxFaces);

    private sealed record RepresentResponse(
        [property: JsonPropertyName("results")] List<RepresentResult>? Results);

    private sealed record RepresentResult(
        [property: JsonPropertyName("embedding")] float[] Embedding,
        [property: JsonPropertyName("facial_area")] FacialArea FacialArea,
        [property: JsonPropertyName("face_confidence")] double FaceConfidence);

    private sealed record FacialArea(
        [property: JsonPropertyName("w")] int Width,
        [property: JsonPropertyName("h")] int Height);
}
