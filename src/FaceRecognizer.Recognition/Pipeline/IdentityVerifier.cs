namespace FaceRecognizer.Recognition.Pipeline;

/// <summary>Gjenkjenningsresultat for ett utvalgt bilde i sporet. <c>null</c> betyr at ingen ansikt ble funnet i beskjæringen.</summary>
/// <param name="PersonId">Matchet person, <c>null</c> hvis ukjent.</param>
/// <param name="PersonName">Navn på matchet person.</param>
/// <param name="PersonEntityId">Koblet HA person-entitet.</param>
/// <param name="Score">Likhet (0-1).</param>
/// <param name="IsMatch">Om scoren oppfyller terskelen.</param>
public sealed record FrameIdentity(Guid? PersonId, string? PersonName, string? PersonEntityId, float Score, bool IsMatch);

/// <summary>Samlet identitetsvurdering for sporet.</summary>
/// <param name="Status">Gjenkjent, ukjent eller identitetskonflikt.</param>
/// <param name="Identity">Valgt identitet (kun ved gjenkjent), ellers <c>null</c>.</param>
/// <param name="Score">Median av scorene til de enige bildene (gjenkjent), ellers høyeste score.</param>
/// <param name="Reason">Årsakskode ved ukjent/konflikt: <c>identity_changed</c>, <c>identity_unconfirmed</c> eller <c>no_match</c>.</param>
public sealed record IdentityDecision(
    Contracts.RecognitionStatus Status, FrameIdentity? Identity, float Score, string? Reason);

/// <summary>Krever at samme person gjenkjennes på flere bilder i sporet. Ulike personer i samme spor avviser sekvensen.</summary>
public static class IdentityVerifier
{
    /// <summary>Vurderer gjenkjenningene.</summary>
    /// <param name="samples">Ett resultat per utvalgt bilde (<c>null</c> hvis ingen ansikt ble funnet).</param>
    /// <param name="minAgreement">Minste antall bilder som må peke på samme person.</param>
    /// <param name="minFraction">Minste andel av utvalget som må peke på samme person.</param>
    public static IdentityDecision Decide(IReadOnlyList<FrameIdentity?> samples, int minAgreement, float minFraction)
    {
        var matches = samples.Where(s => s is { IsMatch: true }).Select(s => s!).ToList();
        var bestScore = samples.Where(s => s is not null).Select(s => s!.Score).DefaultIfEmpty(0f).Max();
        var groups = matches.GroupBy(Key).ToList();
        if (groups.Count > 1)
        {
            return new IdentityDecision(Contracts.RecognitionStatus.IdentityConflict, null, bestScore, "identity_changed");
        }

        if (groups.Count == 0)
        {
            return new IdentityDecision(Contracts.RecognitionStatus.Unknown, null, bestScore, "no_match");
        }

        var agreeing = groups[0].ToList();
        if (agreeing.Count < minAgreement || (float)agreeing.Count / samples.Count < minFraction)
        {
            return new IdentityDecision(Contracts.RecognitionStatus.Unknown, null, bestScore, "identity_unconfirmed");
        }

        var median = Liveness.LivenessAggregator.Median(agreeing.Select(a => a.Score).ToList());
        return new IdentityDecision(Contracts.RecognitionStatus.FaceRecognized, agreeing.OrderByDescending(a => a.Score).First(), median, null);
    }

    private static string Key(FrameIdentity identity) =>
        identity.PersonId?.ToString() ?? identity.PersonName ?? "";
}
