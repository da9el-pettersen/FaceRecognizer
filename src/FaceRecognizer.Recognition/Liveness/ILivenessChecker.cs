namespace FaceRecognizer.Recognition.Liveness;

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

    /// <summary>Minste "ekte"-score (0-1) per bilde. Startverdi, må kalibreres mot egne opptak. Ikke en sannsynlighet.</summary>
    public float MinScore { get; set; } = 0.8f;

    /// <summary>Om liveness kreves for godkjenning. Når <c>true</c> gir manglende eller mislykket liveness aldri <c>verified</c>.</summary>
    public bool Required { get; set; } = true;

    /// <summary>Andel av bildene i sekvensen som må være over <see cref="MinScore"/> (i tillegg til at medianen er det).</summary>
    public float MinLiveFraction { get; set; } = 0.6f;

    /// <summary>Gjennomsnittlig fargespredning (0-255) på eller under dette regnes som svart/hvitt-bilde.</summary>
    public double MonoMaxChroma { get; set; } = 2.0;

    /// <summary>Gjennomsnittlig fargespredning (0-255) på eller over dette regnes som fargebilde.</summary>
    public double ColorMinChroma { get; set; } = 8.0;
}
