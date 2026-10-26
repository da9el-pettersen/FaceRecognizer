using FaceRecognizer.Contracts;
using FaceRecognizer.Database.Messaging;
using FaceRecognizer.Database.Storage;

namespace FaceRecognizer.Database.Api;

/// <summary>REST-API mot databasetjenesten. Alle kall krever token. Endringer publiserer en MQTT-hendelse.</summary>
public static class DatabaseApi
{
    /// <summary>Registrerer endepunktene.</summary>
    /// <param name="app">Applikasjonen.</param>
    /// <param name="token">Forventet bearer-token.</param>
    public static void MapDatabaseApi(this WebApplication app, string token)
    {
        var api = app.MapGroup("").AddEndpointFilter(BearerTokenFilter.Create(token));

        api.MapGet("/persons", async (string modelId, EmbeddingStore store, CancellationToken ct) =>
            Results.Ok(await store.ListPersonsAsync(modelId, ct)));

        api.MapPost("/persons", async (
            CreatePersonRequest request, EmbeddingStore store, IChangeNotifier notifier, CancellationToken ct) =>
        {
            if (!PersonNames.IsValid(request.Name))
            {
                return Results.BadRequest("Ugyldig navn.");
            }

            var person = await store.TryCreatePersonAsync(request.Name!, ct);
            return person is null ? Results.Conflict("Navnet er allerede i bruk.") : Results.Created($"/persons/{person.PersonId}", person);
        });

        api.MapGet("/persons/{personId:guid}", async (Guid personId, EmbeddingStore store, CancellationToken ct) =>
            await store.GetPersonAsync(personId, ct) is { } person ? Results.Ok(person) : Results.NotFound());

        api.MapDelete("/persons/{personId:guid}", async (
            Guid personId, EmbeddingStore store, IChangeNotifier notifier, CancellationToken ct) =>
        {
            if (!await store.DeletePersonAsync(personId, ct))
            {
                return Results.NotFound();
            }

            await notifier.NotifyAsync(ct);
            return Results.NoContent();
        });

        api.MapGet("/persons/{personId:guid}/embeddings", async (
            Guid personId, string modelId, EmbeddingStore store, CancellationToken ct) =>
            Results.Ok(await store.ListPersonEmbeddingsAsync(personId, modelId, ct)));

        api.MapPost("/persons/{personId:guid}/images", async (
            Guid personId, AddReferenceImageRequest request, EmbeddingStore store, IChangeNotifier notifier,
            CancellationToken ct) =>
        {
            if (request.Image is not { Length: > 0 } || string.IsNullOrWhiteSpace(request.ModelId)
                || request.Embedding is not { Length: > 0 })
            {
                return Results.BadRequest("Bilde, modell og embedding må oppgis.");
            }

            var (status, result) = await store.AddImageAsync(personId, request.Image, request.ModelId, request.Embedding, ct);
            switch (status)
            {
                case AddImageStatus.PersonNotFound:
                    return Results.NotFound();
                case AddImageStatus.Duplicate:
                    return Results.Conflict("Bildet er allerede registrert.");
                default:
                    await notifier.NotifyAsync(ct);
                    return Results.Ok(result);
            }
        });

        api.MapGet("/images", async (string modelId, bool? missingOnly, EmbeddingStore store, CancellationToken ct) =>
            Results.Ok(await store.ListImagesAsync(modelId, missingOnly ?? false, ct)));

        api.MapGet("/images/{imageId:long}/data", async (long imageId, EmbeddingStore store, CancellationToken ct) =>
            await store.GetImageDataAsync(imageId, ct) is { } data
                ? Results.File(data, "application/octet-stream")
                : Results.NotFound());

        api.MapPut("/images/{imageId:long}/embedding", async (
            long imageId, SetEmbeddingRequest request, EmbeddingStore store, IChangeNotifier notifier,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ModelId) || request.Embedding is not { Length: > 0 })
            {
                return Results.BadRequest("Modell og embedding må oppgis.");
            }

            if (!await store.SetEmbeddingAsync(imageId, request.ModelId, request.Embedding, ct))
            {
                return Results.NotFound();
            }

            await notifier.NotifyAsync(ct);
            return Results.NoContent();
        });

        api.MapDelete("/embeddings/{embeddingId:long}", async (
            long embeddingId, EmbeddingStore store, IChangeNotifier notifier, CancellationToken ct) =>
        {
            if (!await store.DeleteEmbeddingAsync(embeddingId, ct))
            {
                return Results.NotFound();
            }

            await notifier.NotifyAsync(ct);
            return Results.NoContent();
        });

        api.MapGet("/embeddings", async (string modelId, EmbeddingStore store, CancellationToken ct) =>
            Results.Ok(await store.LoadSnapshotAsync(modelId, ct)));
    }
}
