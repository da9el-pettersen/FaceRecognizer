namespace FaceRecognizer.Contracts;

/// <summary>Hva slags bilder kameraet leverte i analysevinduet.</summary>
public enum ImageMode
{
    /// <summary>Kunne ikke avgjøres sikkert (f.eks. motstridende signaler eller blandede bilder). Behandles som ikke-RGB.</summary>
    Unknown,

    /// <summary>Fargebilder (RGB).</summary>
    Color,

    /// <summary>Svart/hvitt-bilder fra nattmodus (IR).</summary>
    Infrared
}

/// <summary>Resultatet av selve ansiktsgjenkjenningen, uavhengig av liveness.</summary>
public enum RecognitionStatus
{
    /// <summary>Gjenkjenning ble ikke forsøkt (f.eks. IR uten validert modell).</summary>
    NotAttempted,

    /// <summary>Ingen ansikt i sekvensen.</summary>
    NoFace,

    /// <summary>Ansiktet matcher en kjent person, bekreftet på flere bilder av samme spor.</summary>
    FaceRecognized,

    /// <summary>Ansiktet matcher ikke en kjent person, eller identiteten kunne ikke bekreftes.</summary>
    Unknown,

    /// <summary>Ulike personer ble gjenkjent i samme spor. Sekvensen avvises.</summary>
    IdentityConflict
}

/// <summary>Resultatet av liveness-vurderingen, uavhengig av gjenkjenningen.</summary>
public enum LivenessStatus
{
    /// <summary>Liveness ble ikke vurdert (f.eks. fordi ingen person ble gjenkjent).</summary>
    NotEvaluated,

    /// <summary>Liveness er ikke påkrevd i konfigurasjonen.</summary>
    NotRequired,

    /// <summary>Sekvensen ble vurdert som levende.</summary>
    LivenessPassed,

    /// <summary>Sekvensen ble vurdert som ikke levende.</summary>
    LivenessFailed,

    /// <summary>Liveness kunne ikke utføres (f.eks. IR uten egnet modell, for få bilder). Gir aldri godkjenning.</summary>
    LivenessUnavailable
}
