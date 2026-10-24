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
/// <param name="PersonName">Person-ID for matchet person, ellers <c>null</c> (Unknown).</param>
/// <param name="Score">Likhetsscore (0-1) for beste match.</param>
/// <param name="Timestamp">Tidspunkt for avgjørelsen (UTC).</param>
/// <param name="LivenessScore">Laveste liveness-score (0-1) som ble målt, 0 hvis liveness ikke ble sjekket.</param>
/// <param name="CameraName">Navn på kameraet bildet kom fra.</param>
/// <param name="FaceCount">Antall ansikter funnet i bildet. Ved flere enn ett vurderes kun det største.</param>
/// <param name="Provider">Ansiktsmotoren som ble brukt.</param>
public sealed record RecognitionResult(
    RecognitionOutcome Outcome,
    string? PersonName,
    float Score,
    DateTimeOffset Timestamp,
    float LivenessScore = 0f,
    string? CameraName = null,
    int FaceCount = 0,
    string? Provider = null)
{
    /// <summary>MQTT-topic resultatet publiseres på.</summary>
    public const string Topic = "facerecognizer/result";
}
