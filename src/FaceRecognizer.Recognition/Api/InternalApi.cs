using FaceRecognizer.Contracts;
using FaceRecognizer.Recognition.Engines;

namespace FaceRecognizer.Recognition.Api;

/// <summary>
/// Internt API som Admin bruker for å få embeddings fra samme motor som gjenkjenningen. Krever token og
/// inneholder ingen administrasjon av personer eller lagring.
/// </summary>
public static class InternalApi
{
    /// <summary>Registrerer endepunktene.</summary>
    /// <param name="app">Applikasjonen.</param>
    /// <param name="token">Forventet bearer-token.</param>
    public static void MapInternalApi(this WebApplication app, string token)
    {
        var api = app.MapGroup("/internal").AddEndpointFilter(BearerTokenFilter.Create(token));

        api.MapGet("/model", (IFaceRecognitionProvider provider) => Results.Ok(new ModelInfoDto(provider.ModelId)));

        api.MapGet("/metrics", (FaceMetrics metrics) =>
            Results.Ok(metrics.Snapshot().ToDictionary(p => p.Key.ToString(), p => p.Value)));

        api.MapPost("/embed", async (HttpRequest request, IFaceRecognitionProvider provider, CancellationToken ct) =>
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length == 0)
            {
                return Results.BadRequest("Tomt bilde.");
            }

            try
            {
                var faces = await provider.EmbedAsync(buffer.ToArray(), ct);
                return Results.Ok(new EmbedResponse(provider.ModelId, faces));
            }
            catch (Exception ex) when (ex is InvalidOperationException or SixLabors.ImageSharp.UnknownImageFormatException)
            {
                return Results.BadRequest("Bildet kunne ikke leses.");
            }
        });
    }
}
