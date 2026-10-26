using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FaceRecognizer.Contracts;

namespace FaceRecognizer.Admin.Clients;

/// <summary>REST-klient mot gjenkjenningstjenestens interne API.</summary>
public sealed class RecognitionClient : IRecognitionClient
{
    private readonly HttpClient _http;

    /// <summary>Oppretter klienten. <paramref name="http"/> må ha BaseAddress og Authorization satt.</summary>
    public RecognitionClient(HttpClient http) => _http = http;

    /// <inheritdoc />
    public async Task<ModelInfoDto> GetModelAsync(CancellationToken cancellationToken) =>
        await _http.GetFromJsonAsync<ModelInfoDto>("/internal/model", RecognitionJson.Options, cancellationToken)
            ?? throw new InvalidOperationException("Tomt svar fra gjenkjenningstjenesten.");

    /// <inheritdoc />
    public async Task<EmbedResponse?> EmbedAsync(byte[] image, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(image);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.PostAsync("/internal/embed", content, cancellationToken);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EmbedResponse>(RecognitionJson.Options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<JsonElement> GetMetricsAsync(CancellationToken cancellationToken) =>
        await _http.GetFromJsonAsync<JsonElement>("/internal/metrics", cancellationToken);
}
