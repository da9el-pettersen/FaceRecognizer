namespace FaceRecognizer.Contracts;

/// <summary>Person i databasetjenesten.</summary>
/// <param name="PersonId">Unik ID.</param>
/// <param name="Name">Visningsnavn.</param>
public sealed record PersonDto(Guid PersonId, string Name);

/// <summary>Person med antall embeddings for en modellversjon.</summary>
/// <param name="PersonId">Unik ID.</param>
/// <param name="Name">Visningsnavn.</param>
/// <param name="EmbeddingCount">Antall embeddings for den etterspurte modellen.</param>
public sealed record PersonSummaryDto(Guid PersonId, string Name, int EmbeddingCount);

/// <summary>Forespørsel om å opprette en person.</summary>
/// <param name="Name">Navn, se <see cref="PersonNames.IsValid"/>.</param>
public sealed record CreatePersonRequest(string? Name);

/// <summary>Registrerer et referansebilde sammen med embeddingen som er laget av det.</summary>
/// <param name="Image">Bildedata (lagres, slik at embeddings kan lages på nytt for en ny modell).</param>
/// <param name="ModelId">Modellversjonen embeddingen er laget med.</param>
/// <param name="Embedding">Normalisert embedding.</param>
public sealed record AddReferenceImageRequest(byte[] Image, string ModelId, float[] Embedding);

/// <summary>Resultat av å registrere et referansebilde.</summary>
/// <param name="ImageId">ID for bildet.</param>
/// <param name="EmbeddingId">ID for embeddingen.</param>
public sealed record AddReferenceImageResponse(long ImageId, long EmbeddingId);

/// <summary>Setter (oppretter eller erstatter) embeddingen for et lagret bilde og en modell.</summary>
/// <param name="ModelId">Modellversjon.</param>
/// <param name="Embedding">Normalisert embedding.</param>
public sealed record SetEmbeddingRequest(string ModelId, float[] Embedding);

/// <summary>Et lagret referansebilde.</summary>
/// <param name="ImageId">ID for bildet.</param>
/// <param name="PersonId">Eier.</param>
/// <param name="PersonName">Eierens navn.</param>
public sealed record ReferenceImageDto(long ImageId, Guid PersonId, string PersonName);

/// <summary>En embedding som tilhører en person (uten selve vektoren).</summary>
/// <param name="EmbeddingId">ID for embeddingen.</param>
/// <param name="ImageId">Bildet embeddingen kommer fra.</param>
public sealed record PersonEmbeddingDto(long EmbeddingId, long ImageId);

/// <summary>En lagret embedding med eier.</summary>
/// <param name="EmbeddingId">ID for embeddingen.</param>
/// <param name="PersonId">Eier.</param>
/// <param name="PersonName">Eierens navn.</param>
/// <param name="Embedding">Normalisert vektor.</param>
public sealed record EmbeddingDto(long EmbeddingId, Guid PersonId, string PersonName, float[] Embedding);

/// <summary>Alle embeddings for én modellversjon, til lasting i minneindeksen.</summary>
/// <param name="Embeddings">Embeddings for den etterspurte modellen.</param>
/// <param name="OtherModelEmbeddingCount">Antall embeddings for andre modeller (ignoreres av gjenkjenning).</param>
public sealed record EmbeddingSnapshotDto(IReadOnlyList<EmbeddingDto> Embeddings, int OtherModelEmbeddingCount);

/// <summary>Modellversjonen gjenkjenningstjenesten bruker.</summary>
/// <param name="ModelId">Modellversjon.</param>
public sealed record ModelInfoDto(string ModelId);

/// <summary>Embeddings for alle ansikter i et bilde, største ansikt først.</summary>
/// <param name="ModelId">Modellversjonen embeddingene er laget med.</param>
/// <param name="Faces">Normaliserte embeddings.</param>
public sealed record EmbedResponse(string ModelId, IReadOnlyList<float[]> Faces);

/// <summary>Hendelse som databasetjenesten publiserer på MQTT når embeddings er endret.</summary>
/// <param name="At">Tidspunkt for endringen (UTC).</param>
public sealed record EmbeddingsChanged(DateTimeOffset At)
{
    /// <summary>MQTT-topic for endringshendelser.</summary>
    public const string Topic = "facerecognizer/embeddings/changed";
}

/// <summary>Person med embedding-IDer for gjeldende modell (Admin-API).</summary>
/// <param name="PersonId">Unik person-ID.</param>
/// <param name="Name">Navn.</param>
/// <param name="Embeddings">Embeddings for gjeldende modell.</param>
public sealed record PersonDetailDto(Guid PersonId, string Name, IReadOnlyList<PersonEmbeddingDto> Embeddings);

/// <summary>Utfall for én fil ved opplasting av referansebilder (Admin-API).</summary>
/// <param name="FileName">Filnavn.</param>
/// <param name="Status"><c>added</c>, <c>duplicate</c>, <c>rejected</c> (ikke nøyaktig ett ansikt) eller <c>invalid</c>.</param>
/// <param name="EmbeddingId">Embedding-ID når <paramref name="Status"/> er <c>added</c>.</param>
public sealed record ImageUploadResult(string FileName, string Status, long? EmbeddingId);

/// <summary>Resultat av reindeksering (Admin-API).</summary>
/// <param name="Embedded">Antall bilder som fikk ny embedding.</param>
/// <param name="Failed">Antall bilder som ikke kunne embeddes (ikke nøyaktig ett ansikt).</param>
public sealed record ReindexResult(int Embedded, int Failed);
