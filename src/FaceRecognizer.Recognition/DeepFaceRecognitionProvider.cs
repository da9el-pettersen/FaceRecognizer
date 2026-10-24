using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace FaceRecognizer.Recognition;

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
    public float MinFaceConfidence { get; set; } = 0.9f;

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
/// bilder og embeddings logges eller publiseres aldri. Referansebildene lagres i <see cref="ReferenceStore"/>.
/// </summary>
public sealed class DeepFaceRecognitionProvider : IFaceRecognitionProvider
{
    private readonly HttpClient _http;
    private readonly DeepFaceOptions _options;
    private readonly FaceMatchOptions _faceOptions;
    private readonly ILogger<DeepFaceRecognitionProvider> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile bool _loaded;
    private List<(string PersonId, float[] Embedding)> _references = [];

    /// <summary>Oppretter motoren.</summary>
    public DeepFaceRecognitionProvider(
        HttpClient http,
        IOptions<DeepFaceOptions> options,
        IOptions<FaceMatchOptions> faceOptions,
        ILogger<DeepFaceRecognitionProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _faceOptions = faceOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "DeepFace";

    /// <inheritdoc />
    public int ReferenceCount => _references.Count;

    /// <summary>Sjekker at DeepFace svarer.</summary>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("/", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    /// <inheritdoc />
    public async Task LoadReferencesAsync(CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            var loaded = new List<(string, float[])>();
            foreach (var (personId, path) in ReferenceStore.Enumerate(_faceOptions.ReferenceFolder))
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                try
                {
                    var faces = await RepresentAsync(bytes, cancellationToken);
                    if (faces.Count == 1)
                    {
                        loaded.Add((personId, faces[0].Embedding));
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Hopper over referansebilde for {PersonId}: {Faces} ansikter (forventet 1).",
                            personId, faces.Count);
                    }
                }
                catch (InvalidOperationException)
                {
                    _logger.LogWarning("Hopper over referansebilde for {PersonId}: kunne ikke leses.", personId);
                }
            }

            _references = loaded;
            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> DetectFacesAsync(byte[] image, CancellationToken cancellationToken = default) =>
        (await RepresentAsync(image, cancellationToken)).Count;

    /// <inheritdoc />
    public async Task RegisterAsync(string personId, byte[] image, CancellationToken cancellationToken = default)
    {
        if (!ReferenceStore.IsValidPersonId(personId))
        {
            throw new ArgumentException("Ugyldig person-ID.", nameof(personId));
        }

        var faces = await RepresentAsync(image, cancellationToken);
        if (faces.Count != 1)
        {
            throw new ArgumentException("Referansebildet må inneholde nøyaktig ett ansikt.", nameof(image));
        }

        await ReferenceStore.SaveAsync(_faceOptions.ReferenceFolder, personId, image, cancellationToken);
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            _references = [.. _references, (personId, faces[0].Embedding)];
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FaceMatchResult>> RecognizeAsync(
        byte[] image, CancellationToken cancellationToken = default)
    {
        // Lastes først ved behov, så en DeepFace som er nede ved oppstart ikke stopper tjenesten.
        if (!_loaded)
        {
            await LoadReferencesAsync(cancellationToken);
        }

        var faces = await RepresentAsync(image, cancellationToken);
        return faces.Select(face => Match(face.Embedding)).ToList();
    }

    private FaceMatchResult Match(float[] embedding)
    {
        string? bestId = null;
        var bestScore = float.MinValue;
        foreach (var (personId, reference) in _references)
        {
            var score = CosineSimilarity(embedding, reference);
            if (score > bestScore)
            {
                bestScore = score;
                bestId = personId;
            }
        }

        if (bestId is null)
        {
            return new FaceMatchResult(true, null, 0f, false);
        }

        var isMatch = bestScore >= _options.Threshold;
        return new FaceMatchResult(true, isMatch ? bestId : null, bestScore, isMatch);
    }

    internal static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0f;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0f : (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }

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
                return (body.Results ?? [])
                    .Where(r => r.FaceConfidence >= _options.MinFaceConfidence && r.Embedding.Length > 0)
                    .OrderByDescending(r => r.FacialArea.Width * r.FacialArea.Height)
                    .Select(r => new RepresentedFace(r.Embedding))
                    .ToList();
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
