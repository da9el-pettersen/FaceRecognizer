namespace FaceRecognizer.Recognition.Engines;

/// <summary>Resultatet av gjenkjenning for ett ansikt i et bilde.</summary>
/// <param name="FaceFound">Om et ansikt ble funnet.</param>
/// <param name="PersonName">Beste match (person-ID), <c>null</c> hvis ukjent.</param>
/// <param name="Score">Beste likhet (0-1) mot registrerte personer.</param>
/// <param name="IsMatch">Om scoren oppfyller terskelen.</param>
/// <param name="PersonId">Unik ID for matchet person, <c>null</c> hvis ukjent.</param>
/// <param name="PersonEntityId">Koblet Home Assistant person-entitet, <c>null</c> hvis ukjent eller ikke koblet.</param>
public sealed record FaceMatchResult(
    bool FaceFound, string? PersonName, float Score, bool IsMatch, Guid? PersonId = null, string? PersonEntityId = null);

/// <summary>Utskiftbar ansiktsmotor for deteksjon, embedding og gjenkjenning. Registrering av personer hører ikke hjemme her.</summary>
public interface IFaceRecognitionProvider
{
    /// <summary>Navn på motoren, f.eks. <c>FaceAiSharp</c> eller <c>DeepFace</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Modellversjon (motor, modell og detektor). Lagrede embeddings brukes kun når de er laget med samme versjon.
    /// </summary>
    string ModelId { get; }

    /// <summary>Antall registrerte referanseansikter som er lastet i minneindeksen.</summary>
    int ReferenceCount { get; }

    /// <summary>Laster embeddings fra embeddingkilden til minneindeksen (erstatter innholdet).</summary>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    Task LoadReferencesAsync(CancellationToken cancellationToken = default);

    /// <summary>Teller ansikter i et bilde.</summary>
    /// <param name="image">Bilde (JPEG/PNG).</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    Task<int> DetectFacesAsync(byte[] image, CancellationToken cancellationToken = default);

    /// <summary>Lager normaliserte embeddings for alle ansikter i bildet, største ansikt først.</summary>
    /// <param name="image">Bilde (JPEG/PNG).</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    Task<IReadOnlyList<float[]>> EmbedAsync(byte[] image, CancellationToken cancellationToken = default);

    /// <summary>Gjenkjenner alle ansikter i bildet, største ansikt først. Tom liste betyr ingen ansikter.</summary>
    /// <param name="image">Bilde (JPEG/PNG).</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    Task<IReadOnlyList<FaceMatchResult>> RecognizeAsync(byte[] image, CancellationToken cancellationToken = default);
}
