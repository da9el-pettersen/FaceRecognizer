using System.Net.Http.Json;
using FaceRecognizer.Contracts;

namespace FaceRecognizer.Recognition.Engines;

/// <summary>Leser embeddings fra databasetjenesten over REST. Kun lesetilgang.</summary>
public sealed class DatabaseEmbeddingSource : IEmbeddingSource
{
    private readonly HttpClient _http;

    /// <summary>Oppretter kilden. <paramref name="http"/> må ha BaseAddress og eventuell Authorization satt.</summary>
    public DatabaseEmbeddingSource(HttpClient http) => _http = http;

    /// <inheritdoc />
    public async Task<EmbeddingSnapshotDto> LoadAsync(string modelId, CancellationToken cancellationToken)
    {
        var url = $"/embeddings?modelId={Uri.EscapeDataString(modelId)}";
        return await _http.GetFromJsonAsync<EmbeddingSnapshotDto>(url, RecognitionJson.Options, cancellationToken)
            ?? throw new InvalidOperationException("Tomt svar fra databasetjenesten.");
    }
}
