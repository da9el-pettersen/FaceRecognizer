namespace FaceRecognizer.Contracts;

/// <summary>Utfallet av en ansiktsgjenkjenning, inkludert liveness-sjekk.</summary>
public enum RecognitionOutcome
{
    /// <summary>Ansiktet matchet en kjent person og liveness er bekreftet.</summary>
    Verified,

    /// <summary>Ansiktet matchet ikke noen kjent person, eller scoren var for lav.</summary>
    Unknown,

    /// <summary>Ansiktet matchet, men liveness kunne ikke bekreftes (mulig forfalskning).</summary>
    LivenessFailed,

    /// <summary>Ingen ansikt ble funnet i bildet.</summary>
    NoFace,

    /// <summary>Teknisk feil under behandling. Døren skal forbli låst.</summary>
    Error
}

/// <summary>
/// Kontrakten som sendes fra gjenkjenningsmodulen til handlingsmodulen.
/// Handlingsmodulen skal kun reagere på <see cref="RecognitionOutcome.Verified"/> for opplåsing.
/// </summary>
/// <param name="Outcome">Utfallet av gjenkjenningen.</param>
/// <param name="PersonName">Navn på matchet person, ellers <c>null</c>.</param>
/// <param name="Score">Likhetsscore (0-1) for beste match.</param>
/// <param name="Timestamp">Tidspunkt for avgjørelsen (UTC).</param>
public sealed record RecognitionResult(
    RecognitionOutcome Outcome,
    string? PersonName,
    float Score,
    DateTimeOffset Timestamp)
{
    /// <summary>MQTT-topic resultatet publiseres på.</summary>
    public const string Topic = "facerecognizer/result";
}
