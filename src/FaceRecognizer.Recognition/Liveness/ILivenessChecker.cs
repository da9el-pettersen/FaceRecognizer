namespace FaceRecognizer.Recognition;

/// <summary>Resultatet av en liveness-vurdering.</summary>
/// <param name="IsLive">Om liveness er positivt bekreftet.</param>
/// <param name="Score">Gjennomsnittlig "ekte"-sannsynlighet (0-1) over modellene.</param>
public sealed record LivenessResult(bool IsLive, float Score);

/// <summary>Vurderer om et bilde viser et levende ansikt.</summary>
public interface ILivenessChecker
{
    /// <summary>Vurderer liveness. Mangler ansikt eller er scoren lav, er resultatet ikke levende.</summary>
    /// <param name="image">JPEG-bilde (farge eller IR).</param>
    LivenessResult Check(byte[] image);
}

/// <summary>Innstillinger for liveness.</summary>
public sealed class LivenessOptions
{
    /// <summary>Mappe med MiniFASNet ONNX-modellene.</summary>
    public string ModelFolder { get; set; } = "/models";

    /// <summary>Minste gjennomsnittlige "ekte"-score (0-1). Startverdi, må kalibreres mot egne opptak.</summary>
    public float MinScore { get; set; } = 0.8f;
}
