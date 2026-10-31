using FaceRecognizer.Contracts;

namespace FaceRecognizer.Recognition.Liveness;

/// <summary>Aggregert liveness-vurdering over en sekvens.</summary>
/// <param name="Status">Utfallet.</param>
/// <param name="Score">Median av scorene (ikke en kalibrert sannsynlighet), 0 hvis ikke vurdert.</param>
/// <param name="LiveFraction">Andel bilder over terskelen.</param>
/// <param name="Reason">Årsakskode når utfallet ikke er bestått, ellers <c>null</c>.</param>
public readonly record struct LivenessAggregate(LivenessStatus Status, float Score, float LiveFraction, string? Reason);

/// <summary>Slår sammen liveness-score fra flere bilder til én beslutning. Flere bilder gir stabilitet, ikke beskyttelse mot avspilt video.</summary>
public static class LivenessAggregator
{
    /// <summary>Vurderer sekvensen.</summary>
    /// <param name="scores">Score per bilde (0-1).</param>
    /// <param name="minScore">Terskel per bilde.</param>
    /// <param name="minLiveFraction">Andel bilder som må være over terskelen.</param>
    /// <param name="minFrames">Minste antall bilder for en gyldig vurdering.</param>
    public static LivenessAggregate Aggregate(IReadOnlyList<float> scores, float minScore, float minLiveFraction, int minFrames)
    {
        if (scores.Count < minFrames || scores.Count == 0)
        {
            return new LivenessAggregate(LivenessStatus.LivenessUnavailable, 0f, 0f, "too_few_frames");
        }

        var median = Median(scores);
        var fraction = (float)scores.Count(s => s >= minScore) / scores.Count;
        if (median < minScore)
        {
            return new LivenessAggregate(LivenessStatus.LivenessFailed, median, fraction, "liveness_score_low");
        }

        if (fraction < minLiveFraction)
        {
            return new LivenessAggregate(LivenessStatus.LivenessFailed, median, fraction, "liveness_inconsistent");
        }

        return new LivenessAggregate(LivenessStatus.LivenessPassed, median, fraction, null);
    }

    /// <summary>Median (gjennomsnittet av de to midterste ved partall).</summary>
    /// <param name="values">Verdier, minst én.</param>
    public static float Median(IReadOnlyList<float> values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2f;
    }
}
