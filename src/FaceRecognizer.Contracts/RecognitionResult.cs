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
    Error,

    /// <summary>Ansiktet matchet, men liveness kunne ikke utføres (f.eks. nattmodus/IR). Skal aldri gi opplåsing.</summary>
    LivenessUnavailable
}

/// <summary>
/// Kontrakten som sendes fra gjenkjenningsmodulen til handlingsmodulen.
/// Handlingsmodulen skal kun reagere på <see cref="RecognitionOutcome.Verified"/> for opplåsing.
/// </summary>
/// <param name="Outcome">Utfallet av gjenkjenningen.</param>
/// <param name="PersonName">Person-ID for matchet person, ellers <c>null</c> (Unknown).</param>
/// <param name="Score">Likhetsscore (0-1) for beste match.</param>
/// <param name="Timestamp">Tidspunkt for avgjørelsen (UTC).</param>
/// <param name="LivenessScore">Aggregert liveness-score (median over sekvensen, 0-1), 0 hvis liveness ikke ble vurdert. Ikke en kalibrert sannsynlighet.</param>
/// <param name="CameraName">Navn på kameraet bildet kom fra.</param>
/// <param name="FaceCount">Antall ansikter funnet i bildet. Ved flere enn ett vurderes kun det største.</param>
/// <param name="Provider">Ansiktsmotoren som ble brukt.</param>
/// <param name="PersonId">Unik ID for matchet person, ellers <c>null</c>.</param>
/// <param name="PersonEntityId">Home Assistant person-entitet koblet til matchet person (f.eks. <c>person.daniel</c>), ellers <c>null</c>.</param>
/// <param name="RecognitionStatus">Utfallet av selve gjenkjenningen (uavhengig av liveness).</param>
/// <param name="LivenessStatus">Utfallet av liveness (uavhengig av gjenkjenningen).</param>
/// <param name="ImageMode">Om analysevinduet besto av fargebilder eller IR.</param>
/// <param name="Reason">Kort årsakskode når noe ble avvist eller ikke kunne utføres, f.eks. <c>identity_changed</c>.</param>
/// <param name="TrackId">ID for ansiktssporet som både gjenkjenning og liveness gjelder.</param>
/// <param name="FrameFrom">Første bilde-ID i sekvensen som ble analysert.</param>
/// <param name="FrameTo">Siste bilde-ID i sekvensen som ble analysert.</param>
/// <param name="WindowStart">Tidspunkt (UTC) for første bilde i sekvensen.</param>
/// <param name="WindowEnd">Tidspunkt (UTC) for siste bilde i sekvensen.</param>
public sealed record RecognitionResult(
    RecognitionOutcome Outcome,
    string? PersonName,
    float Score,
    DateTimeOffset Timestamp,
    float LivenessScore = 0f,
    string? CameraName = null,
    int FaceCount = 0,
    string? Provider = null,
    Guid? PersonId = null,
    string? PersonEntityId = null,
    RecognitionStatus? RecognitionStatus = null,
    LivenessStatus? LivenessStatus = null,
    ImageMode? ImageMode = null,
    string? Reason = null,
    long? TrackId = null,
    long? FrameFrom = null,
    long? FrameTo = null,
    DateTimeOffset? WindowStart = null,
    DateTimeOffset? WindowEnd = null)
{
    /// <summary>MQTT-topic resultatet publiseres på.</summary>
    public const string Topic = "facerecognizer/result";
}
