namespace FaceRecognizer.Recognition.Pipeline;

/// <summary>Ett ansiktsspor gjennom bildene i en sekvens.</summary>
public sealed class FaceTrack
{
    /// <summary>Indekser (i sekvensen) for bildene sporet er sett i, stigende.</summary>
    public List<int> FrameIndexes { get; } = [];

    /// <summary>Boksene, i samme rekkefølge som <see cref="FrameIndexes"/>.</summary>
    public List<FaceBox> Boxes { get; } = [];

    /// <summary>Om en deteksjon kunne tilhøre flere spor omtrent like godt. Da er koblingen usikker.</summary>
    public bool Ambiguous { get; set; }

    /// <summary>Gjennomsnittlig bokseareal.</summary>
    public float MeanArea => Boxes.Count == 0 ? 0f : Boxes.Average(b => b.Area);

    /// <summary>Gjennomsnittlig boksebredde.</summary>
    public float MeanWidth => Boxes.Count == 0 ? 0f : Boxes.Average(b => b.Width);

    internal int LastIndex => FrameIndexes[^1];

    internal FaceBox LastBox => Boxes[^1];
}

/// <summary>Resultat av sporing.</summary>
/// <param name="Primary">Hovedsporet, eller <c>null</c> hvis sekvensen avvises.</param>
/// <param name="Reason">Årsakskode ved avvisning: <c>no_face</c>, <c>ambiguous_tracking</c> eller <c>track_unstable</c>.</param>
/// <param name="FaceCount">Største antall ansikter i ett enkelt bilde.</param>
public readonly record struct TrackingResult(FaceTrack? Primary, string? Reason, int FaceCount);

/// <summary>
/// Enkel IoU-sporing (grådig kobling) gjennom bildene. Usikker kobling, flere likeverdige ansikter eller et ustabilt spor
/// avvises i stedet for å gjettes. IoU-sporing alene sier ikke at det er samme person; det sjekkes separat av identitetskontrollen.
/// </summary>
public static class FaceTracker
{
    /// <summary>Sporer ansiktene.</summary>
    /// <param name="detections">Ansiktsbokser per bilde, eldste bilde først.</param>
    /// <param name="minIou">Minste overlapp for å koble en deteksjon til et spor.</param>
    /// <param name="maxMissedFrames">Hvor mange bilder et spor kan mangle før det er avsluttet.</param>
    /// <param name="minCoverage">Hovedsporet må være sett i minst denne andelen av bildene.</param>
    /// <param name="competitorCoverage">Et annet spor sett i minst denne andelen av bildene regnes som konkurrent hvis det er stort nok.</param>
    /// <param name="competitorAreaRatio">Konkurrentens areal i forhold til hovedsporet for at det skal regnes som likeverdig.</param>
    public static TrackingResult Track(
        IReadOnlyList<IReadOnlyList<FaceBox>> detections,
        float minIou = 0.3f,
        int maxMissedFrames = 1,
        float minCoverage = 0.7f,
        float competitorCoverage = 0.5f,
        float competitorAreaRatio = 0.7f)
    {
        var tracks = new List<FaceTrack>();
        var faceCount = 0;
        for (var frame = 0; frame < detections.Count; frame++)
        {
            var boxes = detections[frame];
            faceCount = Math.Max(faceCount, boxes.Count);
            var active = tracks.Where(t => frame - t.LastIndex <= maxMissedFrames + 1).ToList();

            var candidates = new List<(FaceTrack Track, int Detection, float Iou)>();
            foreach (var track in active)
            {
                for (var d = 0; d < boxes.Count; d++)
                {
                    var iou = track.LastBox.Iou(boxes[d]);
                    if (iou >= minIou)
                    {
                        candidates.Add((track, d, iou));
                    }
                }
            }

            var usedTracks = new HashSet<FaceTrack>();
            var usedDetections = new HashSet<int>();
            foreach (var (track, detection, iou) in candidates.OrderByDescending(c => c.Iou))
            {
                if (usedTracks.Contains(track) || usedDetections.Contains(detection))
                {
                    continue;
                }

                // En deteksjon som overlapper like godt med et annet spor er en usikker kobling.
                if (candidates.Any(c => c.Detection == detection && c.Track != track && c.Iou >= iou - 0.1f))
                {
                    track.Ambiguous = true;
                }

                track.FrameIndexes.Add(frame);
                track.Boxes.Add(boxes[detection]);
                usedTracks.Add(track);
                usedDetections.Add(detection);
            }

            for (var d = 0; d < boxes.Count; d++)
            {
                if (usedDetections.Contains(d))
                {
                    continue;
                }

                var track = new FaceTrack();
                track.FrameIndexes.Add(frame);
                track.Boxes.Add(boxes[d]);
                tracks.Add(track);
            }
        }

        if (tracks.Count == 0)
        {
            return new TrackingResult(null, "no_face", 0);
        }

        var primary = tracks.OrderByDescending(t => t.Boxes.Count).ThenByDescending(t => t.MeanArea).First();
        if (primary.Boxes.Count < minCoverage * detections.Count)
        {
            return new TrackingResult(null, "track_unstable", faceCount);
        }

        var competitor = tracks.Any(t => t != primary
            && t.Boxes.Count >= competitorCoverage * detections.Count
            && t.MeanArea >= competitorAreaRatio * primary.MeanArea);
        if (primary.Ambiguous || competitor)
        {
            return new TrackingResult(null, "ambiguous_tracking", faceCount);
        }

        return new TrackingResult(primary, null, faceCount);
    }
}
