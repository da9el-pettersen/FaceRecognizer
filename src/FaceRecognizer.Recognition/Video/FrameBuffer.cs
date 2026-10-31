using FaceRecognizer.Recognition.Pipeline;

namespace FaceRecognizer.Recognition.Video;

/// <summary>Resultat av å legge et bilde i bufferen.</summary>
public enum AddResult
{
    /// <summary>Bildet ble lagt inn.</summary>
    Accepted,

    /// <summary>Bildet er identisk med forrige (gjentatt bilde) og ble forkastet.</summary>
    Duplicate,

    /// <summary>Bildet er eldre enn eller like gammelt som forrige, eller fra en gammel forbindelse, og ble forkastet.</summary>
    OutOfOrder
}

/// <summary>Et sammenhengende, ferskt utvalg bilder fra én kameraforbindelse. Må frigis etter bruk.</summary>
public sealed class FrameWindow : IDisposable
{
    /// <summary>Oppretter vinduet. Bildene må allerede være holdt (<see cref="Frame.Retain"/>).</summary>
    public FrameWindow(IReadOnlyList<Frame> frames) => Frames = frames;

    /// <summary>Bildene, eldst først.</summary>
    public IReadOnlyList<Frame> Frames { get; }

    /// <summary>Tidspunkt for første bilde.</summary>
    public DateTimeOffset Start => Frames[0].CapturedAt;

    /// <summary>Tidspunkt for siste bilde.</summary>
    public DateTimeOffset End => Frames[^1].CapturedAt;

    /// <summary>Frigir bildene.</summary>
    public void Dispose()
    {
        foreach (var frame in Frames)
        {
            frame.Dispose();
        }
    }
}

/// <summary>Resultat av å be om et bildevindu: enten et vindu eller en årsakskode.</summary>
/// <param name="Window">Vinduet, eller <c>null</c> ved avvisning. Mottakeren må frigi det.</param>
/// <param name="Reason">Årsakskode ved avvisning: <c>no_frames</c>, <c>stale_frames</c> eller <c>too_few_frames</c>.</param>
public readonly record struct WindowResult(FrameWindow? Window, string? Reason);

/// <summary>
/// Ringbuffer med de siste bildene fra den delte videokilden. Håndterer tidsstempler, ferskhet og reconnect:
/// et vindu inneholder aldri bilder fra ulike forbindelser, aldri hull større enn tillatt og aldri gamle bilder.
/// </summary>
public sealed class FrameBuffer
{
    private readonly object _lock = new();
    private readonly LinkedList<Frame> _frames = new();
    private readonly int _capacity;
    private readonly TimeProvider _time;
    private ulong? _lastFingerprint;
    private long _generation;
    private long _nextFrameId = 1;
    private long _duplicates;

    /// <summary>Oppretter bufferen.</summary>
    /// <param name="capacity">Maks antall bilder.</param>
    /// <param name="time">Tidskilde (til test).</param>
    public FrameBuffer(int capacity, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Nåværende forbindelsesnummer. Økes av <see cref="StartNewSession"/>.</summary>
    public long Generation
    {
        get
        {
            lock (_lock)
            {
                return _generation;
            }
        }
    }

    /// <summary>Antall bilder forkastet som gjentatte siden oppstart.</summary>
    public long DuplicatesRejected => Interlocked.Read(ref _duplicates);

    /// <summary>Antall bilder i bufferen nå.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>Tidspunkt for siste aksepterte bilde, eller <c>null</c>.</summary>
    public DateTimeOffset? LastFrameAt
    {
        get
        {
            lock (_lock)
            {
                return _frames.Last?.Value.CapturedAt;
            }
        }
    }

    /// <summary>Tidskilden bufferen bruker, slik at andre kan måle ferskhet likt.</summary>
    public TimeProvider Time => _time;

    /// <summary>Reserverer neste bilde-ID. IDene er stigende på tvers av reconnect.</summary>
    public long NextFrameId() => Interlocked.Increment(ref _nextFrameId) - 1;

    /// <summary>
    /// Starter en ny forbindelse: tømmer bufferen og øker <see cref="Generation"/>, slik at bilder fra før en reconnect
    /// aldri kan inngå i samme sekvens som bilder etter.
    /// </summary>
    /// <returns>Det nye forbindelsesnummeret.</returns>
    public long StartNewSession()
    {
        lock (_lock)
        {
            foreach (var frame in _frames)
            {
                frame.Dispose();
            }

            _frames.Clear();
            _lastFingerprint = null;
            return ++_generation;
        }
    }

    /// <summary>Legger inn et bilde. Bufferen overtar eierskapet, også ved avvisning (bildet frigis da).</summary>
    /// <param name="frame">Bildet.</param>
    public AddResult Add(Frame frame)
    {
        lock (_lock)
        {
            if (frame.Generation != _generation)
            {
                frame.Dispose();
                return AddResult.OutOfOrder;
            }

            var last = _frames.Last?.Value;
            if (last is not null && (frame.CapturedAt <= last.CapturedAt || frame.FrameId <= last.FrameId))
            {
                frame.Dispose();
                return AddResult.OutOfOrder;
            }

            if (_lastFingerprint == frame.Fingerprint)
            {
                Interlocked.Increment(ref _duplicates);
                frame.Dispose();
                return AddResult.Duplicate;
            }

            _lastFingerprint = frame.Fingerprint;
            _frames.AddLast(frame);
            while (_frames.Count > _capacity)
            {
                _frames.First!.Value.Dispose();
                _frames.RemoveFirst();
            }

            return AddResult.Accepted;
        }
    }

    /// <summary>
    /// Henter det nyeste sammenhengende vinduet. Siste bilde må være ferskt, og ingen hull mellom bilder kan overstige <paramref name="maxGap"/>.
    /// </summary>
    /// <param name="window">Ønsket lengde på vinduet.</param>
    /// <param name="maxFrameAge">Eldste tillatte alder på nyeste bilde.</param>
    /// <param name="maxGap">Største tillatte tid mellom to nabobilder.</param>
    /// <param name="minFrames">Minste antall bilder.</param>
    public WindowResult GetWindow(TimeSpan window, TimeSpan maxFrameAge, TimeSpan maxGap, int minFrames)
    {
        lock (_lock)
        {
            var newest = _frames.Last;
            if (newest is null)
            {
                return new WindowResult(null, "no_frames");
            }

            if (_time.GetUtcNow() - newest.Value.CapturedAt > maxFrameAge)
            {
                return new WindowResult(null, "stale_frames");
            }

            var selected = new List<Frame> { newest.Value };
            var oldestAllowed = newest.Value.CapturedAt - window;
            for (var node = newest.Previous; node is not null; node = node.Previous)
            {
                var candidate = node.Value;
                if (candidate.CapturedAt < oldestAllowed || selected[^1].CapturedAt - candidate.CapturedAt > maxGap)
                {
                    break;
                }

                selected.Add(candidate);
            }

            if (selected.Count < minFrames)
            {
                return new WindowResult(null, "too_few_frames");
            }

            selected.Reverse();
            foreach (var frame in selected)
            {
                frame.Retain();
            }

            return new WindowResult(new FrameWindow(selected), null);
        }
    }
}
