namespace FaceRecognizer.Recognition.Pipeline;

/// <summary>
/// Slipper bare én trigger inn om gangen. Mens en behandling pågår, og i en kort pause etterpå,
/// forkastes alle nye triggere umiddelbart (ingen kø).
/// </summary>
public sealed class TriggerGate
{
    private readonly TimeProvider _time;
    private int _busy;
    private long _cooldownUntilTimestamp;

    /// <summary>Oppretter porten.</summary>
    /// <param name="time">Tidskilde (kan byttes i tester).</param>
    public TriggerGate(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Forsøker å slippe en trigger inn. Returnerer <c>false</c> hvis den skal forkastes.</summary>
    public bool TryEnter()
    {
        if (_time.GetTimestamp() < Interlocked.Read(ref _cooldownUntilTimestamp))
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
    }

    /// <summary>Frigir porten etter ferdig behandling og starter pausen.</summary>
    /// <param name="cooldown">Tid nye triggere forkastes etter at behandlingen er ferdig.</param>
    public void Exit(TimeSpan cooldown)
    {
        var until = _time.GetTimestamp() + (long)(cooldown.TotalSeconds * _time.TimestampFrequency);
        Interlocked.Exchange(ref _cooldownUntilTimestamp, until);
        Interlocked.Exchange(ref _busy, 0);
    }
}
