using System.Net;
using System.Text;
using System.Text.Json;
using FaceRecognizer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FaceRecognizer.Recognition.Engines;
using FaceRecognizer.Recognition.Liveness;
using FaceRecognizer.Recognition.Pipeline;

namespace FaceRecognizer.Recognition.Tests;

/// <summary>Kontrakttester mot DeepFace <c>/represent</c> med en stub som erstatter selve DeepFace.</summary>
public sealed class DeepFaceRecognitionProviderTests
{
    private readonly StubDeepFace _stub = new();
    private readonly InMemoryEmbeddingSource _source = new();

    private DeepFaceRecognitionProvider CreateProvider(Action<DeepFaceOptions>? configure = null)
    {
        var options = new DeepFaceOptions { RetryDelay = TimeSpan.Zero, Threshold = 0.5f };
        configure?.Invoke(options);
        var http = new HttpClient(_stub) { BaseAddress = new Uri("http://deepface:5000") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", "hemmelig");
        return new DeepFaceRecognitionProvider(
            http,
            Options.Create(options),
            _source,
            NullLogger<DeepFaceRecognitionProvider>.Instance);
    }

    // Simulerer Admin: embedder bildet via motoren, lagrer i kilden og laster indeksen på nytt.
    private async Task RegisterAsync(DeepFaceRecognitionProvider provider, string name, byte[] image)
    {
        var embedding = Assert.Single(await provider.EmbedAsync(image));
        _source.Add(provider.ModelId, Guid.NewGuid(), name, embedding);
        await provider.LoadReferencesAsync();
    }

    private sealed class InMemoryEmbeddingSource : IEmbeddingSource
    {
        private readonly List<(string Model, EmbeddingDto Item)> _items = [];

        public void Add(string model, Guid personId, string name, float[] embedding) =>
            _items.Add((model, new EmbeddingDto(_items.Count + 1, personId, name, embedding)));

        public Task<EmbeddingSnapshotDto> LoadAsync(string modelId, CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingSnapshotDto(
                _items.Where(i => i.Model == modelId).Select(i => i.Item).ToList(),
                _items.Count(i => i.Model != modelId)));
    }

    [Fact]
    public async Task Sender_modell_detektor_og_token_til_represent()
    {
        var provider = CreateProvider();
        _stub.Faces[1] = [new(1, 0, 0, 100, 100)];

        await provider.DetectFacesAsync([1]);

        var request = Assert.Single(_stub.Requests);
        Assert.Equal("/represent", request.Path);
        Assert.Equal("Bearer hemmelig", request.Authorization);
        Assert.Equal("SFace", request.Body.GetProperty("model_name").GetString());
        Assert.Equal("yunet", request.Body.GetProperty("detector_backend").GetString());
        Assert.False(request.Body.GetProperty("enforce_detection").GetBoolean());
    }

    [Fact]
    public async Task Registrert_person_gjenkjennes_og_ukjent_gir_Unknown()
    {
        var provider = CreateProvider();
        _stub.Faces[1] = [new(1, 0, 0, 100, 100)];
        _stub.Faces[2] = [new(1, 0.1f, 0, 100, 100)];
        _stub.Faces[3] = [new(0, 0, 1, 100, 100)];
        await RegisterAsync(provider, "Ola", [1]);

        var known = Assert.Single(await provider.RecognizeAsync([2]));
        var unknown = Assert.Single(await provider.RecognizeAsync([3]));

        Assert.True(known.IsMatch);
        Assert.Equal("Ola", known.PersonName);
        Assert.False(unknown.IsMatch);
        Assert.Null(unknown.PersonName);
    }

    [Fact]
    public async Task Flere_ansikter_returneres_med_storste_forst()
    {
        var provider = CreateProvider();
        _stub.Faces[1] = [new(1, 0, 0, 100, 100)];
        await RegisterAsync(provider, "Ola", [1]);
        _stub.Faces[9] = [new(0, 1, 0, 50, 50), new(1, 0, 0, 200, 200)];

        var faces = await provider.RecognizeAsync([9]);

        Assert.Equal(2, faces.Count);
        Assert.Equal("Ola", faces[0].PersonName);
        Assert.Null(faces[1].PersonName);
    }

    [Fact]
    public async Task Ansikt_med_lav_deteksjonssikkerhet_ignoreres()
    {
        var provider = CreateProvider();
        _stub.Faces[1] = [new(1, 0, 0, 100, 100, 0.2)];

        Assert.Equal(0, await provider.DetectFacesAsync([1]));
    }

    [Fact]
    public async Task Embed_gir_normaliserte_embeddings_med_storste_ansikt_forst()
    {
        var provider = CreateProvider();
        _stub.Faces[1] = [new(3, 4, 0, 50, 50), new(0, 0, 2, 200, 200)];

        var faces = await provider.EmbedAsync([1]);

        Assert.Equal(2, faces.Count);
        Assert.Equal(1f, faces[0][2], 4);
        Assert.Equal(0.6f, faces[1][0], 4);
        Assert.All(faces, f => Assert.Equal(1f, EmbeddingMath.Dot(f, f), 4));
    }

    [Fact]
    public async Task Gjenkjenning_laster_indeksen_ved_forste_bruk_og_ignorerer_andre_modeller()
    {
        var provider = CreateProvider();
        _source.Add("annen-modell", Guid.NewGuid(), "Kari", [1, 0, 0]);
        _source.Add(provider.ModelId, Guid.NewGuid(), "Ola", [1, 0, 0]);
        _stub.Faces[1] = [new(1, 0, 0, 100, 100)];

        var face = Assert.Single(await provider.RecognizeAsync([1]));

        Assert.Equal("Ola", face.PersonName);
        Assert.Equal(1, provider.ReferenceCount);
    }

    [Fact]
    public async Task Midlertidig_feil_prøves_på_nytt()
    {
        var provider = CreateProvider();
        _stub.Faces[1] = [new(1, 0, 0, 100, 100)];
        _stub.FailNext = 2;

        Assert.Equal(1, await provider.DetectFacesAsync([1]));
        Assert.Equal(3, _stub.Requests.Count);
    }

    [Fact]
    public async Task DeepFace_nede_gir_unntak_og_gjenopprettes_ved_neste_kall()
    {
        var provider = CreateProvider(o => o.MaxRetries = 1);
        _stub.Faces[1] = [new(1, 0, 0, 100, 100)];
        _stub.FailNext = int.MaxValue;

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.RecognizeAsync([1]));
        Assert.Equal(2, _stub.Requests.Count);

        _stub.FailNext = 0;
        Assert.Single(await provider.RecognizeAsync([1]));
    }

    [Fact]
    public async Task Avbrudd_stopper_uten_retry()
    {
        var provider = CreateProvider();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DetectFacesAsync([1], cts.Token));
        Assert.Empty(_stub.Requests);
    }

    [Fact]
    public async Task Avvist_token_gir_unntak_uten_retry()
    {
        var provider = CreateProvider();
        _stub.StatusOverride = HttpStatusCode.Unauthorized;

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DetectFacesAsync([1]));
        Assert.Single(_stub.Requests);
    }

    [Fact]
    public void Cosinus_likhet_er_en_for_like_og_null_for_ortogonale_vektorer()
    {
        Assert.Equal(1f, DeepFaceRecognitionProvider.CosineSimilarity([1, 2], [2, 4]), 4);
        Assert.Equal(0f, DeepFaceRecognitionProvider.CosineSimilarity([1, 0], [0, 1]), 4);
    }

    [Fact]
    public void Resultat_inneholder_kamera_motor_og_antall_ansikter_men_ingen_embedding()
    {
        var faces = new[] { new FaceMatchResult(true, "Ola", 0.9f, true), new FaceMatchResult(true, null, 0.1f, false) };

        var result = RecognitionWorker.ToResult(faces, new LivenessResult(true, 0.95f), "inngang", "DeepFace");
        var json = JsonSerializer.Serialize(result, RecognitionJson.Options);

        Assert.Equal(RecognitionOutcome.Verified, result.Outcome);
        Assert.Contains("\"cameraName\":\"inngang\"", json);
        Assert.Contains("\"faceCount\":2", json);
        Assert.Contains("\"provider\":\"DeepFace\"", json);
        Assert.DoesNotContain("embedding", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Utfall_mappes_fra_motorens_resultat()
    {
        var unknown = RecognitionWorker.ToResult([new FaceMatchResult(true, null, 0.2f, false)], null, "c", "p");
        var none = RecognitionWorker.ToResult([], null, "c", "p");
        var spoof = RecognitionWorker.ToResult(
            [new FaceMatchResult(true, "Ola", 0.9f, true)], new LivenessResult(false, 0.1f), "c", "p");

        Assert.Equal(RecognitionOutcome.Unknown, unknown.Outcome);
        Assert.Equal(RecognitionOutcome.NoFace, none.Outcome);
        Assert.Equal(RecognitionOutcome.LivenessFailed, spoof.Outcome);
    }

    /// <summary>Live-test mot en kjørende DeepFace. Kjøres kun når DEEPFACE_TEST_URL og testdata er satt.</summary>
    [Fact]
    public async Task Live_DeepFace_gjenkjenner_samme_person_og_avviser_annen()
    {
        var url = Environment.GetEnvironmentVariable("DEEPFACE_TEST_URL");
        var token = Environment.GetEnvironmentVariable("DEEPFACE_TEST_TOKEN");
        var data = Environment.GetEnvironmentVariable("DEEPFACE_TEST_IMAGES");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(data))
        {
            return;
        }

        var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var source = new InMemoryEmbeddingSource();
        var provider = new DeepFaceRecognitionProvider(
            http,
            Options.Create(new DeepFaceOptions()),
            source,
            NullLogger<DeepFaceRecognitionProvider>.Instance);

        async Task Register(string name, string file)
        {
            var embedding = Assert.Single(await provider.EmbedAsync(await File.ReadAllBytesAsync(Path.Combine(data, file))));
            source.Add(provider.ModelId, Guid.NewGuid(), name, embedding);
        }

        await Register("Person A", "img1.jpg");
        await Register("Person A", "img2.jpg");
        await Register("Person B", "img3.jpg");
        await provider.LoadReferencesAsync();

        var same = await provider.RecognizeAsync(await File.ReadAllBytesAsync(Path.Combine(data, "img4.jpg")));
        var other = await provider.RecognizeAsync(await File.ReadAllBytesAsync(Path.Combine(data, "img12.jpg")));
        var many = await provider.RecognizeAsync(
            await File.ReadAllBytesAsync(Path.Combine(data, "selfie-many-people.jpg")));

        Assert.Equal("Person A", same[0].PersonName);
        Assert.True(same[0].Score >= 0.5f, $"score {same[0].Score}");
        Assert.NotEqual("Person A", other[0].PersonName);
        Assert.True(other[0].Score < same[0].Score, $"other {other[0].Score} / same {same[0].Score}");
        Assert.True(many.Count > 1);
        Assert.True(await provider.PingAsync(default));
    }

    private sealed record StubFace(float X, float Y, float Z, int Width, int Height, double Confidence = 0.99);

    private sealed record RecordedRequest(string Path, string? Authorization, JsonElement Body);

    // Stub: bildets første byte velger hvilke ansikter DeepFace "ser".
    private sealed class StubDeepFace : HttpMessageHandler
    {
        public Dictionary<byte, StubFace[]> Faces { get; } = [];

        public List<RecordedRequest> Requests { get; } = [];

        public int FailNext { get; set; }

        public HttpStatusCode? StatusOverride { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            Requests.Add(new RecordedRequest(
                request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));

            if (StatusOverride is { } status)
            {
                return new HttpResponseMessage(status);
            }

            if (FailNext > 0)
            {
                FailNext--;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var img = body.GetProperty("img").GetString()!;
            var bytes = Convert.FromBase64String(img[(img.IndexOf(',') + 1)..]);
            var results = Faces.GetValueOrDefault(bytes[0], []).Select(f => new
            {
                embedding = new[] { f.X, f.Y, f.Z },
                facial_area = new { x = 0, y = 0, w = f.Width, h = f.Height },
                face_confidence = f.Confidence
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { results }), Encoding.UTF8, "application/json")
            };
        }
    }
}
