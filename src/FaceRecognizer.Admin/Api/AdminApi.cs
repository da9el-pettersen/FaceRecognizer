using FaceRecognizer.Admin.Clients;
using FaceRecognizer.Contracts;

namespace FaceRecognizer.Admin.Api;

/// <summary>Administrasjons-API for personer og referansebilder. Alle kall krever token.</summary>
public static class AdminApi
{
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private const int MaxFiles = 20;

    /// <summary>Registrerer endepunktene under <c>/api</c>.</summary>
    /// <param name="app">Applikasjonen.</param>
    /// <param name="auth">Autentiseringsinnstillinger.</param>
    public static void MapAdminApi(this WebApplication app, AdminAuthOptions auth)
    {
        var api = app.MapGroup("/api")
            .AddEndpointFilter(AdminAuth.CreateFilter(auth))
            .AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                if (!HttpMethods.IsGet(http.Request.Method))
                {
                    http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AdminApi")
                        .LogInformation("{User}: {Method} {Path}", http.Items["user"], http.Request.Method, http.Request.Path);
                }

                return await next(context);
            })
            .AddEndpointFilter(async (context, next) =>
            {
                try
                {
                    return await next(context);
                }
                catch (HttpRequestException)
                {
                    return Results.Problem("Database- eller gjenkjenningstjenesten er utilgjengelig.", statusCode: 502);
                }
            });

        api.MapGet("/me", (HttpContext http) => Results.Ok(new { user = http.Items["user"] }));

        api.MapGet("/persons", async (ReferenceService service, CancellationToken ct) =>
            Results.Ok(await service.ListPersonsAsync(ct)));

        api.MapPost("/persons", async (CreatePersonRequest request, IDatabaseClient database, CancellationToken ct) =>
        {
            if (!PersonNames.IsValid(request.Name))
            {
                return Results.BadRequest("Ugyldig navn.");
            }

            var person = await database.CreatePersonAsync(request.Name!, ct);
            return person is null
                ? Results.Conflict("Navnet er allerede i bruk.")
                : Results.Created($"/api/persons/{person.PersonId}", person);
        });

        api.MapGet("/persons/{personId:guid}", async (Guid personId, ReferenceService service, CancellationToken ct) =>
            await service.GetPersonAsync(personId, ct) is { } person ? Results.Ok(person) : Results.NotFound());

        api.MapPost("/persons/{personId:guid}/images", async (
            Guid personId, HttpRequest request, ReferenceService service, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest("Forventer multipart/form-data med feltet 'files'.");
            }

            var form = await request.ReadFormAsync(ct);
            var files = form.Files.GetFiles("files");
            if (files.Count is 0 or > MaxFiles)
            {
                return Results.BadRequest($"Send mellom 1 og {MaxFiles} filer i feltet 'files'.");
            }

            var oversized = new List<ImageUploadResult>();
            var accepted = new List<(string, byte[])>();
            foreach (var file in files)
            {
                if (file.Length is 0 or > MaxFileBytes)
                {
                    oversized.Add(new ImageUploadResult(file.FileName, "invalid", null));
                    continue;
                }

                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                accepted.Add((file.FileName, buffer.ToArray()));
            }

            try
            {
                var results = await service.AddImagesAsync(personId, accepted, ct);
                return Results.Ok(oversized.Concat(results).ToList());
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).DisableAntiforgery();

        api.MapDelete("/persons/{personId:guid}/embeddings/{embeddingId:long}", async (
            Guid personId, long embeddingId, ReferenceService service, CancellationToken ct) =>
            await service.DeleteEmbeddingAsync(personId, embeddingId, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPut("/persons/{personId:guid}/entity", async (
            Guid personId, SetPersonEntityRequest request, IDatabaseClient database, CancellationToken ct) =>
        {
            var entity = string.IsNullOrWhiteSpace(request.EntityId) ? null : request.EntityId.Trim();
            if (entity is not null && !HaPersonEntities.IsValid(entity))
            {
                return Results.BadRequest("Ugyldig entitet. Bruk formatet person.navn.");
            }

            return await database.SetPersonEntityAsync(personId, entity, ct) ? Results.NoContent() : Results.NotFound();
        });

        api.MapDelete("/persons/{personId:guid}", async (Guid personId, IDatabaseClient database, CancellationToken ct) =>
            await database.DeletePersonAsync(personId, ct) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/reindex", async (bool? rebuild, ReferenceService service, CancellationToken ct) =>
            Results.Ok(await service.ReindexAsync(rebuild ?? false, ct)));

        api.MapGet("/metrics", async (IRecognitionClient recognition, CancellationToken ct) =>
            Results.Ok(await recognition.GetMetricsAsync(ct)));
    }
}
