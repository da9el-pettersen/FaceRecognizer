using FaceRecognizer.Admin.Clients;
using FaceRecognizer.Contracts;

namespace FaceRecognizer.Admin;

/// <summary>
/// Forretningslogikk for administrasjon: får embeddings fra gjenkjenningstjenesten og lagrer dem i databasetjenesten.
/// Kjenner ingen AI-motor og ingen databaseteknologi.
/// </summary>
public sealed class ReferenceService
{
    private readonly IDatabaseClient _database;
    private readonly IRecognitionClient _recognition;

    /// <summary>Oppretter tjenesten.</summary>
    public ReferenceService(IDatabaseClient database, IRecognitionClient recognition)
    {
        _database = database;
        _recognition = recognition;
    }

    /// <summary>Lister personer med antall embeddings for gjeldende modell.</summary>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task<IReadOnlyList<PersonSummaryDto>> ListPersonsAsync(CancellationToken cancellationToken) =>
        await _database.ListPersonsAsync((await _recognition.GetModelAsync(cancellationToken)).ModelId, cancellationToken);

    /// <summary>Henter en person med embedding-IDer for gjeldende modell.</summary>
    /// <param name="personId">Person-ID.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    /// <returns>Personen, eller <c>null</c> hvis den ikke finnes.</returns>
    public async Task<PersonDetailDto?> GetPersonAsync(Guid personId, CancellationToken cancellationToken)
    {
        var person = await _database.GetPersonAsync(personId, cancellationToken);
        if (person is null)
        {
            return null;
        }

        var model = await _recognition.GetModelAsync(cancellationToken);
        var embeddings = await _database.ListPersonEmbeddingsAsync(personId, model.ModelId, cancellationToken);
        return new PersonDetailDto(person.PersonId, person.Name, embeddings, person.HaPersonEntity);
    }

    /// <summary>
    /// Registrerer referansebilder. Hvert bilde må ha nøyaktig ett ansikt; resultatet rapporteres per fil.
    /// </summary>
    /// <param name="personId">Person-ID.</param>
    /// <param name="files">Filnavn og bildedata.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    /// <exception cref="KeyNotFoundException">Personen finnes ikke.</exception>
    public async Task<IReadOnlyList<ImageUploadResult>> AddImagesAsync(
        Guid personId, IEnumerable<(string FileName, byte[] Data)> files, CancellationToken cancellationToken)
    {
        if (await _database.GetPersonAsync(personId, cancellationToken) is null)
        {
            throw new KeyNotFoundException("Personen finnes ikke.");
        }

        var results = new List<ImageUploadResult>();
        foreach (var (fileName, data) in files)
        {
            var embedded = await _recognition.EmbedAsync(data, cancellationToken);
            if (embedded is null)
            {
                results.Add(new ImageUploadResult(fileName, "invalid", null));
                continue;
            }

            if (embedded.Faces.Count != 1)
            {
                results.Add(new ImageUploadResult(fileName, "rejected", null));
                continue;
            }

            var added = await _database.AddImageAsync(
                personId, new AddReferenceImageRequest(data, embedded.ModelId, embedded.Faces[0]), cancellationToken);
            results.Add(added is null
                ? new ImageUploadResult(fileName, "duplicate", null)
                : new ImageUploadResult(fileName, "added", added.EmbeddingId));
        }

        return results;
    }

    /// <summary>Sletter en embedding og tilhørende referansebilde.</summary>
    /// <param name="personId">Eier, som må stemme med embeddingen.</param>
    /// <param name="embeddingId">Embedding-ID.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    /// <returns><c>false</c> hvis embeddingen ikke finnes for denne personen.</returns>
    public async Task<bool> DeleteEmbeddingAsync(Guid personId, long embeddingId, CancellationToken cancellationToken)
    {
        var model = await _recognition.GetModelAsync(cancellationToken);
        var owned = await _database.ListPersonEmbeddingsAsync(personId, model.ModelId, cancellationToken);
        return owned.Any(e => e.EmbeddingId == embeddingId)
            && await _database.DeleteEmbeddingAsync(embeddingId, cancellationToken);
    }

    /// <summary>
    /// Lager embeddings på nytt fra lagrede referansebilder. Uten <paramref name="rebuild"/> embeddes kun bilder som
    /// mangler embedding for gjeldende modell (f.eks. etter modellbytte eller import).
    /// </summary>
    /// <param name="rebuild">Om alle bilder skal embeddes på nytt og erstatte eksisterende embeddings.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task<ReindexResult> ReindexAsync(bool rebuild, CancellationToken cancellationToken)
    {
        var model = await _recognition.GetModelAsync(cancellationToken);
        var images = await _database.ListImagesAsync(model.ModelId, missingOnly: !rebuild, cancellationToken);
        int embeddedCount = 0, failed = 0;
        foreach (var image in images)
        {
            var data = await _database.GetImageDataAsync(image.ImageId, cancellationToken);
            var embedded = data is null ? null : await _recognition.EmbedAsync(data, cancellationToken);
            if (embedded is { Faces.Count: 1 }
                && await _database.SetEmbeddingAsync(image.ImageId, embedded.ModelId, embedded.Faces[0], cancellationToken))
            {
                embeddedCount++;
            }
            else
            {
                failed++;
            }
        }

        return new ReindexResult(embeddedCount, failed);
    }
}
