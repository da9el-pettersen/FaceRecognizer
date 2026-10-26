using System.Net;
using System.Net.Http.Json;
using FaceRecognizer.Contracts;

namespace FaceRecognizer.Admin.Clients;

/// <summary>REST-klient mot databasetjenesten.</summary>
public sealed class DatabaseClient : IDatabaseClient
{
    private readonly HttpClient _http;

    /// <summary>Oppretter klienten. <paramref name="http"/> må ha BaseAddress og Authorization satt.</summary>
    public DatabaseClient(HttpClient http) => _http = http;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersonSummaryDto>> ListPersonsAsync(string modelId, CancellationToken cancellationToken) =>
        await _http.GetFromJsonAsync<List<PersonSummaryDto>>(
            $"/persons?modelId={Uri.EscapeDataString(modelId)}", RecognitionJson.Options, cancellationToken) ?? [];

    /// <inheritdoc />
    public async Task<PersonDto?> CreatePersonAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            "/persons", new CreatePersonRequest(name), RecognitionJson.Options, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PersonDto>(RecognitionJson.Options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PersonDto?> GetPersonAsync(Guid personId, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"/persons/{personId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PersonDto>(RecognitionJson.Options, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeletePersonAsync(Guid personId, CancellationToken cancellationToken) =>
        DeleteAsync($"/persons/{personId}", cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersonEmbeddingDto>> ListPersonEmbeddingsAsync(
        Guid personId, string modelId, CancellationToken cancellationToken) =>
        await _http.GetFromJsonAsync<List<PersonEmbeddingDto>>(
            $"/persons/{personId}/embeddings?modelId={Uri.EscapeDataString(modelId)}",
            RecognitionJson.Options, cancellationToken) ?? [];

    /// <inheritdoc />
    public async Task<AddReferenceImageResponse?> AddImageAsync(
        Guid personId, AddReferenceImageRequest request, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            $"/persons/{personId}/images", request, RecognitionJson.Options, cancellationToken);
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new KeyNotFoundException("Personen finnes ikke.");
            case HttpStatusCode.Conflict:
                return null;
            default:
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<AddReferenceImageResponse>(
                    RecognitionJson.Options, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReferenceImageDto>> ListImagesAsync(
        string modelId, bool missingOnly, CancellationToken cancellationToken) =>
        await _http.GetFromJsonAsync<List<ReferenceImageDto>>(
            $"/images?modelId={Uri.EscapeDataString(modelId)}&missingOnly={missingOnly.ToString().ToLowerInvariant()}",
            RecognitionJson.Options, cancellationToken) ?? [];

    /// <inheritdoc />
    public async Task<byte[]?> GetImageDataAsync(long imageId, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"/images/{imageId}/data", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> SetEmbeddingAsync(
        long imageId, string modelId, float[] embedding, CancellationToken cancellationToken)
    {
        using var response = await _http.PutAsJsonAsync(
            $"/images/{imageId}/embedding", new SetEmbeddingRequest(modelId, embedding),
            RecognitionJson.Options, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <inheritdoc />
    public Task<bool> DeleteEmbeddingAsync(long embeddingId, CancellationToken cancellationToken) =>
        DeleteAsync($"/embeddings/{embeddingId}", cancellationToken);

    private async Task<bool> DeleteAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.DeleteAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }
}
