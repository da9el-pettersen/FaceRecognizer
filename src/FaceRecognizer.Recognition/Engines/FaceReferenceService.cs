using FaceRecognizer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace FaceRecognizer.Recognition.Engines;

/// <summary>Kilde for ferdigberegnede embeddings. Gjenkjenning leser kun, og eier verken bilder eller database.</summary>
public interface IEmbeddingSource
{
    /// <summary>Henter alle embeddings som er laget med <paramref name="modelId"/>.</summary>
    /// <param name="modelId">Modellversjon.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    Task<EmbeddingSnapshotDto> LoadAsync(string modelId, CancellationToken cancellationToken);
}

/// <summary>
/// Holder minneindeksen for én modellversjon og fyller den fra en <see cref="IEmbeddingSource"/>. Delt av alle
/// ansiktsmotorer. Matching leser kun indeksen; innlasting bytter hele indeksen atomisk.
/// </summary>
public sealed class FaceReferenceService
{
    private readonly IEmbeddingSource _source;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile bool _loaded;

    /// <summary>Oppretter tjenesten.</summary>
    /// <param name="modelId">Modellversjon (motor, modell, detektor). Embeddings fra andre modeller brukes aldri.</param>
    /// <param name="source">Kilde for embeddings.</param>
    /// <param name="logger">Logger, valgfri.</param>
    public FaceReferenceService(string modelId, IEmbeddingSource source, ILogger? logger = null)
    {
        _source = source;
        _logger = logger ?? NullLogger.Instance;
        Index = new EmbeddingIndex(modelId);
    }

    /// <summary>Modellversjonen embeddingene er laget med.</summary>
    public string ModelId => Index.ModelId;

    /// <summary>Minneindeksen som brukes ved matching.</summary>
    public EmbeddingIndex Index { get; }

    /// <summary>Om indeksen er lastet minst én gang.</summary>
    public bool IsLoaded => _loaded;

    /// <summary>Laster embeddings for gjeldende modell fra kilden og erstatter indeksen.</summary>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await _source.LoadAsync(ModelId, cancellationToken);
            Index.ReplaceAll(snapshot.Embeddings.Select(e => (e.PersonId, e.PersonName, e.EmbeddingId, e.Embedding)));
            _loaded = true;
            if (snapshot.OtherModelEmbeddingCount > 0)
            {
                _logger.LogWarning(
                    "{Count} embeddings fra andre modellversjoner ignoreres (gjeldende: {Model}). Kjør reindeksering i Admin.",
                    snapshot.OtherModelEmbeddingCount, ModelId);
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Laster indeksen hvis den ikke er lastet ennå.</summary>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public Task EnsureLoadedAsync(CancellationToken cancellationToken) =>
        _loaded ? Task.CompletedTask : LoadAsync(cancellationToken);

    /// <summary>Sammenligner et normalisert ansikt mot indeksen uten databaseoppslag.</summary>
    /// <param name="normalizedEmbedding">Normalisert embedding.</param>
    /// <param name="threshold">Minste likhet for at treffet godtas.</param>
    /// <returns>Treff, eller resultat uten navn (<c>Unknown</c>) hvis beste likhet er under terskelen.</returns>
    public FaceMatchResult Match(ReadOnlySpan<float> normalizedEmbedding, float threshold)
    {
        if (!Index.TryFindBest(normalizedEmbedding, out var best))
        {
            return new FaceMatchResult(true, null, 0f, false);
        }

        var isMatch = best.Score >= threshold;
        return new FaceMatchResult(true, isMatch ? best.Name : null, best.Score, isMatch, isMatch ? best.PersonId : null);
    }
}
