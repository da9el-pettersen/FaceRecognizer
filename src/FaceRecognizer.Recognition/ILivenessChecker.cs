namespace FaceRecognizer.Recognition;

/// <summary>Vurderer om et IR-bilde viser et levende ansikt.</summary>
public interface ILivenessChecker
{
    /// <summary>Returnerer <c>true</c> bare når liveness er positivt bekreftet.</summary>
    /// <param name="infraredImage">JPEG tatt i IR-modus.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    Task<bool> IsLiveAsync(byte[] infraredImage, CancellationToken cancellationToken);
}

/// <summary>
/// Midlertidig implementasjon uten modell. Avviser alltid, slik at døren aldri låses opp
/// før en reell liveness-modell er på plass.
/// </summary>
public sealed class UnconfiguredLivenessChecker(ILogger<UnconfiguredLivenessChecker> logger) : ILivenessChecker
{
    /// <inheritdoc />
    public Task<bool> IsLiveAsync(byte[] infraredImage, CancellationToken cancellationToken)
    {
        logger.LogWarning("Ingen liveness-modell er konfigurert. Avviser.");
        return Task.FromResult(false);
    }
}
