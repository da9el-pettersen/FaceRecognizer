using FaceRecognizer.Contracts;
using FaceRecognizer.Recognition.Pipeline;

namespace FaceRecognizer.Recognition.Tests;

public sealed class LivenessRetryTests
{
    private static readonly Guid Daniel = Guid.NewGuid();

    private static RecognitionResult Result(RecognitionOutcome outcome, Guid? personId = null) =>
        new(outcome, personId is null ? null : "Daniel", 0.6f, DateTimeOffset.UtcNow, PersonId: personId);

    [Fact]
    public async Task Ikke_liveness_feil_gir_ingen_nye_forsok()
    {
        var calls = 0;
        var result = await RecognitionWorker.RetryLivenessAsync(
            Result(RecognitionOutcome.Unknown), 4, () => { calls++; return Task.FromResult(Result(RecognitionOutcome.Verified, Daniel)); });

        Assert.Equal(RecognitionOutcome.Unknown, result.Outcome);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Stopper_ved_bekreftet_samme_person()
    {
        var calls = 0;
        var result = await RecognitionWorker.RetryLivenessAsync(
            Result(RecognitionOutcome.LivenessFailed, Daniel), 4,
            () => Task.FromResult(++calls < 2
                ? Result(RecognitionOutcome.LivenessFailed, Daniel)
                : Result(RecognitionOutcome.Verified, Daniel)));

        Assert.Equal(RecognitionOutcome.Verified, result.Outcome);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Gir_opp_etter_totalt_fire_forsok()
    {
        var calls = 0;
        var result = await RecognitionWorker.RetryLivenessAsync(
            Result(RecognitionOutcome.LivenessFailed, Daniel), 4,
            () => { calls++; return Task.FromResult(Result(RecognitionOutcome.LivenessFailed, Daniel)); });

        Assert.Equal(RecognitionOutcome.LivenessFailed, result.Outcome);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Annen_person_bekreftet_godtas_ikke()
    {
        var result = await RecognitionWorker.RetryLivenessAsync(
            Result(RecognitionOutcome.LivenessFailed, Daniel), 3,
            () => Task.FromResult(Result(RecognitionOutcome.Verified, Guid.NewGuid())));

        Assert.Equal(RecognitionOutcome.LivenessFailed, result.Outcome);
        Assert.Equal(Daniel, result.PersonId);
    }
}
