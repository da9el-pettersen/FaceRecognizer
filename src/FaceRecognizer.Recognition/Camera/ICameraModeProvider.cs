namespace FaceRecognizer.Recognition.Camera;

/// <summary>Dag/natt-modus slik den er konfigurert i kameraet (ikke nødvendigvis hva kameraet leverer akkurat nå).</summary>
public enum ReportedDayNight
{
    /// <summary>Ukjent: ikke lest, utdatert eller ugjenkjent verdi.</summary>
    Unknown,

    /// <summary>Automatisk. Faktisk modus kan ikke leses via HTTP-API-et og må avgjøres fra bildene.</summary>
    Auto,

    /// <summary>Tvunget fargemodus.</summary>
    Color,

    /// <summary>Tvunget svart/hvitt (IR).</summary>
    BlackAndWhite
}

/// <summary>Gir kameraets rapporterte dag/natt-modus som tillegg til bildeanalysen.</summary>
public interface ICameraModeProvider
{
    /// <summary>Sist leste modus, eller <see cref="ReportedDayNight.Unknown"/> hvis den ikke er ferskt lest.</summary>
    ReportedDayNight Current { get; }
}
