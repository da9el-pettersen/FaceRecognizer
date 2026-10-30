using FaceRecognizer.Contracts;

namespace FaceRecognizer.Admin.Clients;

/// <summary>Admin sin tilgang til databasetjenesten. Admin kjenner kun dette grensesnittet, ikke SQLite.</summary>
public interface IDatabaseClient
{
    /// <summary>Lister personer med antall embeddings for <paramref name="modelId"/>.</summary>
    Task<IReadOnlyList<PersonSummaryDto>> ListPersonsAsync(string modelId, CancellationToken cancellationToken);

    /// <summary>Oppretter en person. Returnerer <c>null</c> hvis navnet allerede er i bruk.</summary>
    Task<PersonDto?> CreatePersonAsync(string name, CancellationToken cancellationToken);

    /// <summary>Henter en person, eller <c>null</c> hvis den ikke finnes.</summary>
    Task<PersonDto?> GetPersonAsync(Guid personId, CancellationToken cancellationToken);

    /// <summary>Sletter en person med alle bilder og embeddings. Returnerer <c>false</c> hvis den ikke finnes.</summary>
    Task<bool> DeletePersonAsync(Guid personId, CancellationToken cancellationToken);

    /// <summary>Setter (eller fjerner med <c>null</c>) koblingen til en HA person-entitet. Returnerer <c>false</c> hvis personen ikke finnes.</summary>
    Task<bool> SetPersonEntityAsync(Guid personId, string? entityId, CancellationToken cancellationToken);

    /// <summary>Lister embeddings for en person og modell.</summary>
    Task<IReadOnlyList<PersonEmbeddingDto>> ListPersonEmbeddingsAsync(
        Guid personId, string modelId, CancellationToken cancellationToken);

    /// <summary>
    /// Lagrer referansebilde og embedding. Returnerer <c>null</c> hvis bildet allerede er registrert.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Personen finnes ikke.</exception>
    Task<AddReferenceImageResponse?> AddImageAsync(
        Guid personId, AddReferenceImageRequest request, CancellationToken cancellationToken);

    /// <summary>Lister referansebilder, evt. kun de som mangler embedding for <paramref name="modelId"/>.</summary>
    Task<IReadOnlyList<ReferenceImageDto>> ListImagesAsync(
        string modelId, bool missingOnly, CancellationToken cancellationToken);

    /// <summary>Henter bildedata, eller <c>null</c> hvis bildet ikke finnes.</summary>
    Task<byte[]?> GetImageDataAsync(long imageId, CancellationToken cancellationToken);

    /// <summary>Oppretter eller erstatter embedding for et bilde. Returnerer <c>false</c> hvis bildet ikke finnes.</summary>
    Task<bool> SetEmbeddingAsync(
        long imageId, string modelId, float[] embedding, CancellationToken cancellationToken);

    /// <summary>Sletter en embedding (og bildet når det ikke har flere). Returnerer <c>false</c> hvis den ikke finnes.</summary>
    Task<bool> DeleteEmbeddingAsync(long embeddingId, CancellationToken cancellationToken);
}

/// <summary>Admin sin tilgang til gjenkjenningstjenesten. Kun embedding-generering og informasjon.</summary>
public interface IRecognitionClient
{
    /// <summary>Henter modellversjonen gjenkjenningen bruker.</summary>
    Task<ModelInfoDto> GetModelAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lager embeddings for alle ansikter i bildet. Returnerer <c>null</c> hvis bildet ikke kunne leses.
    /// </summary>
    Task<EmbedResponse?> EmbedAsync(byte[] image, CancellationToken cancellationToken);

    /// <summary>Henter ytelsesmålinger som rå JSON.</summary>
    Task<System.Text.Json.JsonElement> GetMetricsAsync(CancellationToken cancellationToken);
}
