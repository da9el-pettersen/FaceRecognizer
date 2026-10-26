using System.Diagnostics;

namespace FaceRecognizer.Recognition.Engines;

/// <summary>Stegene i gjenkjenningen som måles hver for seg.</summary>
public enum FaceStage
{
    /// <summary>Ansiktsdeteksjon.</summary>
    Detection,

    /// <summary>Generering av embedding (inkluderer justering/beskjæring).</summary>
    Embedding,

    /// <summary>Deteksjon og embedding i ett kall (DeepFace <c>/represent</c>), som ikke kan skilles.</summary>
    DetectionAndEmbedding,

    /// <summary>Sammenligning mot minneindeksen.</summary>
    Matching
}

/// <summary>Målinger for ett steg.</summary>
/// <param name="Count">Antall målinger.</param>
/// <param name="AverageMs">Gjennomsnittlig tid i millisekunder.</param>
/// <param name="MaxMs">Lengste tid i millisekunder.</param>
/// <param name="LastMs">Siste tid i millisekunder.</param>
public sealed record StageStats(long Count, double AverageMs, double MaxMs, double LastMs);

/// <summary>Enkel, trådsikker ytelsesmåling per steg uten allokering ved registrering.</summary>
public sealed class FaceMetrics
{
    private static readonly int StageCount = Enum.GetValues<FaceStage>().Length;
    private readonly long[] _count = new long[StageCount];
    private readonly long[] _totalTicks = new long[StageCount];
    private readonly long[] _maxTicks = new long[StageCount];
    private readonly long[] _lastTicks = new long[StageCount];

    /// <summary>Starter en måling.</summary>
    /// <returns>Tidsstempel som sendes til <see cref="Record"/>.</returns>
    public static long Start() => Stopwatch.GetTimestamp();

    /// <summary>Registrerer tiden brukt siden <paramref name="startTimestamp"/>.</summary>
    /// <param name="stage">Steget som ble målt.</param>
    /// <param name="startTimestamp">Verdi fra <see cref="Start"/>.</param>
    public void Record(FaceStage stage, long startTimestamp)
    {
        var index = (int)stage;
        var elapsed = Stopwatch.GetTimestamp() - startTimestamp;
        Interlocked.Increment(ref _count[index]);
        Interlocked.Add(ref _totalTicks[index], elapsed);
        Interlocked.Exchange(ref _lastTicks[index], elapsed);
        long current;
        while ((current = Interlocked.Read(ref _maxTicks[index])) < elapsed
               && Interlocked.CompareExchange(ref _maxTicks[index], elapsed, current) != current)
        {
        }
    }

    /// <summary>Henter målingene for alle steg som har data.</summary>
    public IReadOnlyDictionary<FaceStage, StageStats> Snapshot()
    {
        var result = new Dictionary<FaceStage, StageStats>();
        foreach (var stage in Enum.GetValues<FaceStage>())
        {
            var index = (int)stage;
            var count = Interlocked.Read(ref _count[index]);
            if (count == 0)
            {
                continue;
            }

            result[stage] = new StageStats(
                count,
                ToMs(Interlocked.Read(ref _totalTicks[index])) / count,
                ToMs(Interlocked.Read(ref _maxTicks[index])),
                ToMs(Interlocked.Read(ref _lastTicks[index])));
        }

        return result;
    }

    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
