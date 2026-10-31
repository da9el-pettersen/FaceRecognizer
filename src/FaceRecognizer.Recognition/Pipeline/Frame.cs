using System.Buffers;

namespace FaceRecognizer.Recognition.Pipeline;

/// <summary>Ett ansikt funnet i et bilde, med bufret liveness-score slik at den kun beregnes én gang.</summary>
public sealed class ObservedFace
{
    private readonly object _lock = new();
    private readonly Dictionary<string, float> _livenessScores = [];

    /// <summary>Oppretter et observert ansikt.</summary>
    /// <param name="box">Ansiktsboksen i analysebildets koordinater.</param>
    public ObservedFace(FaceBox box) => Box = box;

    /// <summary>Ansiktsboksen i analysebildets koordinater.</summary>
    public FaceBox Box { get; }

    /// <summary>Henter bufret liveness-score fra gitt modell, eller beregner og lagrer den.</summary>
    /// <param name="model">Modellnavn (hver modell har egen score).</param>
    /// <param name="compute">Beregner scoren første gang.</param>
    public float GetOrComputeLivenessScore(string model, Func<float> compute)
    {
        lock (_lock)
        {
            if (!_livenessScores.TryGetValue(model, out var score))
            {
                score = compute();
                _livenessScores[model] = score;
            }

            return score;
        }
    }
}

/// <summary>
/// Ett dekodet videobilde (RGB, 8 bit per kanal). Pikselminnet leies fra en pool og leveres tilbake når siste bruker er ferdig
/// (<see cref="Retain"/> og <see cref="Dispose"/>), slik at bilder aldri brukes etter at de er frigitt.
/// </summary>
public sealed class Frame : IDisposable
{
    private readonly byte[] _buffer;
    private readonly int _length;
    private readonly object _analysisLock = new();
    private int _references = 1;
    private IReadOnlyList<ObservedFace>? _faces;
    private double _chroma = double.NaN;

    /// <summary>Oppretter bildet. Eieren overtar <paramref name="buffer"/> (leid fra <see cref="ArrayPool{T}.Shared"/>).</summary>
    /// <param name="frameId">Fortløpende ID, aldri gjenbrukt innenfor prosessen.</param>
    /// <param name="generation">Hvilken kameraforbindelse bildet kom fra (øker ved hver reconnect).</param>
    /// <param name="capturedAt">Tidspunktet bildet ble mottatt (UTC).</param>
    /// <param name="width">Bredde i piksler.</param>
    /// <param name="height">Høyde i piksler.</param>
    /// <param name="buffer">Pikseldata (RGB, rad for rad).</param>
    public Frame(long frameId, long generation, DateTimeOffset capturedAt, int width, int height, byte[] buffer)
    {
        FrameId = frameId;
        Generation = generation;
        CapturedAt = capturedAt;
        Width = width;
        Height = height;
        _buffer = buffer;
        _length = checked(width * height * 3);
        Fingerprint = ComputeFingerprint(_buffer.AsSpan(0, _length));
    }

    /// <summary>Fortløpende bilde-ID.</summary>
    public long FrameId { get; }

    /// <summary>Kameraforbindelsen bildet kom fra. Bilder fra ulike forbindelser skal aldri blandes i én sekvens.</summary>
    public long Generation { get; }

    /// <summary>Tidspunktet bildet ble mottatt (UTC).</summary>
    public DateTimeOffset CapturedAt { get; }

    /// <summary>Bredde i piksler.</summary>
    public int Width { get; }

    /// <summary>Høyde i piksler.</summary>
    public int Height { get; }

    /// <summary>Stikkprøve-fingeravtrykk av innholdet, brukes til å avvise identiske (gjentatte) bilder.</summary>
    public ulong Fingerprint { get; }

    /// <summary>Pikslene som RGB, rad for rad. Kun gyldig mens bildet er holdt (ikke frigitt).</summary>
    public ReadOnlySpan<byte> Rgb
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _references) <= 0, this);
            return _buffer.AsSpan(0, _length);
        }
    }

    /// <summary>Pikslene som minne, til bruk for bildebibliotek uten kopiering.</summary>
    public Memory<byte> RgbMemory
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _references) <= 0, this);
            return _buffer.AsMemory(0, _length);
        }
    }

    /// <summary>Gjennomsnittlig fargespredning (0-255) over et utvalg piksler. Nær 0 betyr svart/hvitt.</summary>
    public double MeanChroma
    {
        get
        {
            lock (_analysisLock)
            {
                if (double.IsNaN(_chroma))
                {
                    _chroma = Liveness.ImageAnalysis.MeanChroma(Rgb, Width, Height);
                }

                return _chroma;
            }
        }
    }

    /// <summary>Ansiktene funnet i bildet, eller <c>null</c> hvis deteksjon ikke er kjørt.</summary>
    public IReadOnlyList<ObservedFace>? Faces
    {
        get
        {
            lock (_analysisLock)
            {
                return _faces;
            }
        }
    }

    /// <summary>Gir et nytt bruksforhold til bildet. Hver <see cref="Retain"/> må følges av én <see cref="Dispose"/>.</summary>
    public Frame Retain()
    {
        while (true)
        {
            var current = Volatile.Read(ref _references);
            ObjectDisposedException.ThrowIf(current <= 0, this);
            if (Interlocked.CompareExchange(ref _references, current + 1, current) == current)
            {
                return this;
            }
        }
    }

    /// <summary>Henter bufrede ansikter, eller kjører deteksjon én gang og lagrer resultatet.</summary>
    /// <param name="detect">Finner ansikter i bildet.</param>
    public IReadOnlyList<ObservedFace> GetOrDetectFaces(Func<Frame, IReadOnlyList<FaceBox>> detect)
    {
        lock (_analysisLock)
        {
            _faces ??= detect(this).Select(box => new ObservedFace(box)).ToArray();
            return _faces;
        }
    }

    /// <summary>Slipper ett bruksforhold. Pikselminnet leveres tilbake til poolen når ingen bruker bildet.</summary>
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }

    // Identiske bilder (f.eks. gjentatt av en strøm som stopper opp) har identisk stikkprøve. Kamerastøy gjør ekte bilder ulike.
    private static ulong ComputeFingerprint(ReadOnlySpan<byte> pixels)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        const int stride = 97;
        var hash = offsetBasis;
        for (var i = 0; i < pixels.Length; i += stride)
        {
            hash = (hash ^ pixels[i]) * prime;
        }

        return hash;
    }
}
