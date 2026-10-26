namespace FaceRecognizer.Recognition;

/// <summary>Resultatet av gjenkjenning for ett ansikt i et bilde.</summary>
/// <param name="FaceFound">Om et ansikt ble funnet.</param>
/// <param name="PersonName">Beste match (person-ID), <c>null</c> hvis ukjent.</param>
/// <param name="Score">Beste likhet (0-1) mot registrerte personer.</param>
/// <param name="IsMatch">Om scoren oppfyller terskelen.</param>
public sealed record FaceMatchResult(bool FaceFound, string? PersonName, float Score, bool IsMatch);

/// <summary>Utskiftbar ansiktsmotor for deteksjon, registrering og gjenkjenning.</summary>
public interface IFaceRecognitionProvider
{
    /// <summary>Navn på motoren, f.eks. <c>FaceAiSharp</c> eller <c>DeepFace</c>.</summary>
    string Name { get; }

    /// <summary>Antall registrerte referanseansikter som er lastet.</summary>
    int ReferenceCount { get; }

    /// <summary>Laster registrerte personer fra det lokale referanselageret.</summary>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    Task LoadReferencesAsync(CancellationToken cancellationToken = default);

    /// <summary>Teller ansikter i et bilde.</summary>
    /// <param name="image">Bilde (JPEG/PNG).</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    Task<int> DetectFacesAsync(byte[] image, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registrerer en kjent person med ett bilde og lagrer det varig. Kall flere ganger for flere bilder.
    /// </summary>
    /// <param name="personId">Person-ID (bokstaver, tall, mellomrom, understrek og bindestrek).</param>
    /// <param name="image">Bilde med nøyaktig ett ansikt.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    /// <exception cref="ArgumentException">Ugyldig person-ID, eller bildet har ikke nøyaktig ett ansikt.</exception>
    Task RegisterAsync(string personId, byte[] image, CancellationToken cancellationToken = default);

    /// <summary>Gjenkjenner alle ansikter i bildet, største ansikt først. Tom liste betyr ingen ansikter.</summary>
    /// <param name="image">Bilde (JPEG/PNG).</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    Task<IReadOnlyList<FaceMatchResult>> RecognizeAsync(byte[] image, CancellationToken cancellationToken = default);
}
