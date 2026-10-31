using FaceRecognizer.Contracts;

namespace FaceRecognizer.Recognition.Pipeline;

/// <summary>Opplysninger om sekvensen som ble analysert, felles for gjenkjenning og liveness.</summary>
/// <param name="TrackId">ID for ansiktssporet (bilde-ID til første bilde i sporet).</param>
/// <param name="FrameFrom">Første bilde-ID i sporet.</param>
/// <param name="FrameTo">Siste bilde-ID i sporet.</param>
/// <param name="WindowStart">Tidspunkt for første bilde.</param>
/// <param name="WindowEnd">Tidspunkt for siste bilde.</param>
/// <param name="ImageMode">Bildetype i sekvensen.</param>
/// <param name="FaceCount">Største antall ansikter i ett bilde.</param>
public sealed record SequenceInfo(
    long TrackId, long FrameFrom, long FrameTo, DateTimeOffset WindowStart, DateTimeOffset WindowEnd,
    ImageMode ImageMode, int FaceCount);

/// <summary>Setter sammen gjenkjenning og liveness til ett utfall. Eneste sted som avgjør om resultatet blir <c>verified</c>.</summary>
public static class ResultComposer
{
    /// <summary>
    /// Utfallet. <see cref="RecognitionOutcome.Verified"/> krever gjenkjent ansikt og bestått liveness (eller at liveness ikke er påkrevd).
    /// </summary>
    /// <param name="recognition">Gjenkjenningsstatus.</param>
    /// <param name="liveness">Liveness-status.</param>
    public static RecognitionOutcome Outcome(RecognitionStatus recognition, LivenessStatus liveness)
    {
        if (recognition == RecognitionStatus.FaceRecognized)
        {
            return liveness switch
            {
                LivenessStatus.LivenessPassed or LivenessStatus.NotRequired => RecognitionOutcome.Verified,
                LivenessStatus.LivenessFailed => RecognitionOutcome.LivenessFailed,
                _ => RecognitionOutcome.LivenessUnavailable
            };
        }

        return recognition switch
        {
            RecognitionStatus.NoFace => RecognitionOutcome.NoFace,
            RecognitionStatus.NotAttempted when liveness == LivenessStatus.LivenessUnavailable => RecognitionOutcome.LivenessUnavailable,
            _ => RecognitionOutcome.Unknown
        };
    }

    /// <summary>Bygger resultatet som publiseres.</summary>
    public static RecognitionResult Compose(
        RecognitionStatus recognition,
        LivenessStatus liveness,
        FrameIdentity? identity,
        float matchScore,
        float livenessScore,
        string? reason,
        SequenceInfo? sequence,
        string camera,
        string provider,
        DateTimeOffset now)
    {
        var recognized = recognition == RecognitionStatus.FaceRecognized && identity is not null;
        return new RecognitionResult(
            Outcome(recognition, liveness),
            recognized ? identity!.PersonName : null,
            matchScore,
            now,
            livenessScore,
            camera,
            sequence?.FaceCount ?? 0,
            provider,
            recognized ? identity!.PersonId : null,
            recognized ? identity!.PersonEntityId : null,
            recognition,
            liveness,
            sequence?.ImageMode,
            reason,
            sequence?.TrackId,
            sequence?.FrameFrom,
            sequence?.FrameTo,
            sequence?.WindowStart,
            sequence?.WindowEnd);
    }
}
