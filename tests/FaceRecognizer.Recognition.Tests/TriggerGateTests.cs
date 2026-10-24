namespace FaceRecognizer.Recognition.Tests;

public class TriggerGateTests
{
    [Fact]
    public void Forkaster_triggere_mens_opptatt_og_i_pausen_etterpa()
    {
        var time = new FakeTimeProvider();
        var gate = new TriggerGate(time);

        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());

        gate.Exit(TimeSpan.FromSeconds(10));
        Assert.False(gate.TryEnter());

        time.Advance(TimeSpan.FromSeconds(11));
        Assert.True(gate.TryEnter());
    }

    [Fact]
    public async Task Kun_en_av_mange_samtidige_triggere_slipper_inn()
    {
        var gate = new TriggerGate();

        var accepted = await Task.WhenAll(Enumerable.Range(0, 200)
            .Select(_ => Task.Run(gate.TryEnter)));

        Assert.Equal(1, accepted.Count(a => a));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += (long)(by.TotalSeconds * 1000);
    }
}
