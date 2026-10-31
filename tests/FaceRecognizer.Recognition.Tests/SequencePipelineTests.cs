using System.Buffers;
using FaceRecognizer.Contracts;
using FaceRecognizer.Recognition.Camera;
using FaceRecognizer.Recognition.Configuration;
using FaceRecognizer.Recognition.Engines;
using FaceRecognizer.Recognition.Liveness;
using FaceRecognizer.Recognition.Pipeline;
using FaceRecognizer.Recognition.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FaceRecognizer.Recognition.Tests;

public class FaceTrackerTests
{
    private static FaceBox Box(float x, float y = 100, float size = 100) => new(x, y, size, size);

    private static IReadOnlyList<IReadOnlyList<FaceBox>> Frames(params FaceBox[][] frames) =>
        frames.Select(f => (IReadOnlyList<FaceBox>)f).ToList();

    [Fact]
    public void Ett_ansikt_gjennom_alle_bilder_gir_ett_spor()
    {
        var result = FaceTracker.Track(Frames([Box(100)], [Box(104)], [Box(108)], [Box(112)]));

        Assert.NotNull(result.Primary);
        Assert.Equal(4, result.Primary!.Boxes.Count);
    }

    [Fact]
    public void Ingen_ansikter_gir_no_face()
    {
        var result = FaceTracker.Track(Frames([], [], []));

        Assert.Null(result.Primary);
        Assert.Equal("no_face", result.Reason);
    }

    [Fact]
    public void Person_byttet_midt_i_sekvensen_avvises_som_ustabilt_spor()
    {
        // Første halvdel ett sted, andre halvdel et helt annet sted: to spor, ingen dekker 70 %.
        var result = FaceTracker.Track(Frames([Box(100)], [Box(100)], [Box(100)], [Box(800)], [Box(800)], [Box(800)]));

        Assert.Null(result.Primary);
        Assert.Contains(result.Reason, new[] { "track_unstable", "ambiguous_tracking" });
    }

    [Fact]
    public void To_likeverdige_ansikter_gjennom_hele_sekvensen_avvises()
    {
        var result = FaceTracker.Track(Frames([Box(100), Box(700)], [Box(102), Box(702)], [Box(104), Box(704)], [Box(106), Box(706)]));

        Assert.Null(result.Primary);
        Assert.Equal("ambiguous_tracking", result.Reason);
        Assert.Equal(2, result.FaceCount);
    }

    [Fact]
    public void Lite_bakgrunnsansikt_ignoreres_men_telles()
    {
        var small = new FaceBox(700, 100, 30, 30);
        var result = FaceTracker.Track(Frames([Box(100), small], [Box(102), small], [Box(104), small], [Box(106), small]));

        Assert.NotNull(result.Primary);
        Assert.Equal(2, result.FaceCount);
    }

    [Fact]
    public void Ansiktet_forsvinner_en_gang_er_fortsatt_samme_spor()
    {
        var result = FaceTracker.Track(Frames([Box(100)], [Box(102)], [], [Box(106)], [Box(108)]));

        Assert.NotNull(result.Primary);
        Assert.Equal(4, result.Primary!.Boxes.Count);
    }

    [Fact]
    public void Overlappende_ansikter_gir_usikker_kobling()
    {
        var result = FaceTracker.Track(Frames(
            [Box(100), Box(110)], [Box(100), Box(110)], [Box(100), Box(110)], [Box(100), Box(110)]));

        Assert.Null(result.Primary);
    }
}

public class IdentityVerifierTests
{
    private static readonly Guid Ola = Guid.NewGuid();
    private static readonly Guid Kari = Guid.NewGuid();

    private static FrameIdentity Match(Guid id, float score) => new(id, "x", null, score, true);

    private static FrameIdentity Unknown(float score = 0.2f) => new(null, null, null, score, false);

    [Fact]
    public void Samme_person_paa_alle_bilder_gjenkjennes_med_median()
    {
        var decision = IdentityVerifier.Decide([Match(Ola, 0.7f), Match(Ola, 0.9f), Match(Ola, 0.8f)], 2, 0.67f);

        Assert.Equal(RecognitionStatus.FaceRecognized, decision.Status);
        Assert.Equal(0.8f, decision.Score);
    }

    [Fact]
    public void Ulike_personer_i_samme_spor_gir_identitetskonflikt()
    {
        var decision = IdentityVerifier.Decide([Match(Ola, 0.8f), Match(Kari, 0.8f), Match(Ola, 0.8f)], 2, 0.67f);

        Assert.Equal(RecognitionStatus.IdentityConflict, decision.Status);
        Assert.Equal("identity_changed", decision.Reason);
        Assert.Null(decision.Identity);
    }

    [Fact]
    public void Ett_enkelt_treff_er_ikke_nok()
    {
        var decision = IdentityVerifier.Decide([Match(Ola, 0.8f), Unknown(), Unknown()], 2, 0.67f);

        Assert.Equal(RecognitionStatus.Unknown, decision.Status);
        Assert.Equal("identity_unconfirmed", decision.Reason);
    }

    [Fact]
    public void Ingen_treff_gir_ukjent()
    {
        var decision = IdentityVerifier.Decide([Unknown(), null, Unknown(0.3f)], 2, 0.67f);

        Assert.Equal(RecognitionStatus.Unknown, decision.Status);
        Assert.Equal(0.3f, decision.Score);
    }

    [Fact]
    public void To_av_tre_er_nok_naar_andelen_er_67_prosent()
    {
        var decision = IdentityVerifier.Decide([Match(Ola, 0.8f), Match(Ola, 0.7f), Unknown()], 2, 0.66f);

        Assert.Equal(RecognitionStatus.FaceRecognized, decision.Status);
    }
}

public class LivenessDecisionTests
{
    [Fact]
    public void Stabilt_hoeye_scorer_bestaar()
    {
        var result = LivenessAggregator.Aggregate([0.9f, 0.95f, 0.85f, 0.9f, 0.92f, 0.88f, 0.9f, 0.91f], 0.8f, 0.6f, 8);

        Assert.Equal(LivenessStatus.LivenessPassed, result.Status);
    }

    [Fact]
    public void Lav_median_feiler()
    {
        var result = LivenessAggregator.Aggregate([0.2f, 0.3f, 0.9f, 0.1f, 0.2f, 0.3f, 0.4f, 0.2f], 0.8f, 0.6f, 8);

        Assert.Equal(LivenessStatus.LivenessFailed, result.Status);
        Assert.Equal("liveness_score_low", result.Reason);
    }

    [Fact]
    public void Hoey_median_men_ustabil_andel_feiler()
    {
        // Median 0.8 (over terskel), men bare 50 % av bildene er over.
        var result = LivenessAggregator.Aggregate([0.9f, 0.9f, 0.9f, 0.9f, 0.1f, 0.1f, 0.1f, 0.1f], 0.8f, 0.6f, 8);

        Assert.Equal(LivenessStatus.LivenessFailed, result.Status);
    }

    [Fact]
    public void For_faa_bilder_er_utilgjengelig_ikke_bestatt()
    {
        var result = LivenessAggregator.Aggregate([0.99f, 0.99f, 0.99f], 0.8f, 0.6f, 8);

        Assert.Equal(LivenessStatus.LivenessUnavailable, result.Status);
    }

    [Theory]
    [InlineData(new[] { 0.5, 0.8, 1.0 }, ReportedDayNight.Auto, ImageMode.Infrared)]
    [InlineData(new[] { 20.0, 25.0, 30.0 }, ReportedDayNight.Auto, ImageMode.Color)]
    [InlineData(new[] { 0.5, 1.0, 0.3 }, ReportedDayNight.Auto, ImageMode.Infrared)]
    [InlineData(new[] { 0.5, 1.0, 0.3 }, ReportedDayNight.Color, ImageMode.Unknown)]
    [InlineData(new[] { 20.0, 25.0 }, ReportedDayNight.BlackAndWhite, ImageMode.Unknown)]
    [InlineData(new[] { 0.5, 1.0 }, ReportedDayNight.BlackAndWhite, ImageMode.Infrared)]
    [InlineData(new[] { 20.0, 1.0 }, ReportedDayNight.Unknown, ImageMode.Unknown)]
    [InlineData(new[] { 5.0, 5.0 }, ReportedDayNight.Auto, ImageMode.Unknown)]
    public void Bildetype_klassifiseres_forsiktig(double[] chromas, ReportedDayNight reported, ImageMode expected)
    {
        Assert.Equal(expected, ImageModeClassifier.Classify(chromas, reported, 2.0, 8.0));
    }

    [Theory]
    [InlineData(RecognitionStatus.FaceRecognized, LivenessStatus.LivenessPassed, RecognitionOutcome.Verified)]
    [InlineData(RecognitionStatus.FaceRecognized, LivenessStatus.NotRequired, RecognitionOutcome.Verified)]
    [InlineData(RecognitionStatus.FaceRecognized, LivenessStatus.LivenessFailed, RecognitionOutcome.LivenessFailed)]
    [InlineData(RecognitionStatus.FaceRecognized, LivenessStatus.LivenessUnavailable, RecognitionOutcome.LivenessUnavailable)]
    [InlineData(RecognitionStatus.FaceRecognized, LivenessStatus.NotEvaluated, RecognitionOutcome.LivenessUnavailable)]
    [InlineData(RecognitionStatus.NotAttempted, LivenessStatus.LivenessUnavailable, RecognitionOutcome.LivenessUnavailable)]
    [InlineData(RecognitionStatus.IdentityConflict, LivenessStatus.LivenessPassed, RecognitionOutcome.Unknown)]
    [InlineData(RecognitionStatus.Unknown, LivenessStatus.LivenessPassed, RecognitionOutcome.Unknown)]
    [InlineData(RecognitionStatus.NoFace, LivenessStatus.NotEvaluated, RecognitionOutcome.NoFace)]
    public void Verified_krever_gjenkjent_ansikt_og_bestatt_liveness(
        RecognitionStatus recognition, LivenessStatus liveness, RecognitionOutcome expected)
    {
        Assert.Equal(expected, ResultComposer.Outcome(recognition, liveness));
    }
}

public class SequenceEvaluatorTests
{
    private const int W = 320;
    private const int H = 240;
    private static readonly FaceBox TheFace = new(100, 60, 120, 120);

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedDetector(Func<FaceBox[]> faces) : IFrameFaceDetector
    {
        public int Calls;

        public IReadOnlyList<FaceBox> Detect(Frame frame)
        {
            Calls++;
            return faces();
        }
    }

    private sealed class FakeProvider(Func<FaceMatchResult[]> result) : IFaceRecognitionProvider
    {
        public string Name => "Fake";

        public string ModelId => "fake-1";

        public int ReferenceCount => 1;

        public Task LoadReferencesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> DetectFacesAsync(byte[] image, CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<IReadOnlyList<float[]>> EmbedAsync(byte[] image, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<float[]>>([]);

        public Task<IReadOnlyList<FaceMatchResult>> RecognizeAsync(byte[] image, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FaceMatchResult>>(result());
    }

    private sealed class FakeModel(float score, bool supportsColor = true, bool supportsInfrared = false) : ILivenessModel
    {
        public int Calls;

        public string Name => "Fake";

        public float MinScore => 0.8f;

        public bool Supports(ImageMode mode) => mode == ImageMode.Color ? supportsColor : mode == ImageMode.Infrared && supportsInfrared;

        public float ScoreFace(Frame frame, FaceBox box)
        {
            Interlocked.Increment(ref Calls);
            return score;
        }
    }

    private sealed class FixedMode(ReportedDayNight mode) : ICameraModeProvider
    {
        public ReportedDayNight Current => mode;
    }

    private static readonly Guid OlaId = Guid.NewGuid();

    private static FaceMatchResult OlaMatch() => new(true, "Ola", 0.7f, true, OlaId, "person.ola");

    private static FrameBuffer Buffer(FakeTime time, bool color, int count = 12)
    {
        var buffer = new FrameBuffer(30, time);
        buffer.StartNewSession();
        var start = time.Now;
        for (var i = 0; i < count; i++)
        {
            var pixels = ArrayPool<byte>.Shared.Rent(W * H * 3);
            for (var p = 0; p < W * H; p++)
            {
                var gray = (byte)(100 + i);
                pixels[p * 3] = color ? (byte)(gray + 60) : gray;
                pixels[p * 3 + 1] = gray;
                pixels[p * 3 + 2] = color ? (byte)(gray - 40) : gray;
            }

            buffer.Add(new Frame(buffer.NextFrameId(), buffer.Generation, start + TimeSpan.FromSeconds(i * 0.2), W, H, pixels));
        }

        time.Now = start + TimeSpan.FromSeconds((count - 1) * 0.2);
        return buffer;
    }

    private static SequenceEvaluator Create(
        FrameBuffer buffer, IFrameFaceDetector detector, IFaceRecognitionProvider provider, ILivenessModel[] models,
        ReportedDayNight reported = ReportedDayNight.Auto, bool required = true, bool irRecognition = false) =>
        new(buffer, detector, provider, models, new FixedMode(reported),
            Options.Create(new PipelineOptions { InfraredRecognition = irRecognition }),
            Options.Create(new LivenessOptions { Required = required }),
            Options.Create(new CameraOptions { Name = "cam" }),
            NullLogger<SequenceEvaluator>.Instance);

    [Fact]
    public async Task RGB_med_gjenkjent_person_og_bestatt_liveness_gir_verified_med_felles_ider()
    {
        var time = new FakeTime();
        var buffer = Buffer(time, color: true);
        var evaluator = Create(buffer, new FixedDetector(() => [TheFace]), new FakeProvider(() => [OlaMatch()]), [new FakeModel(0.95f)]);

        var result = await evaluator.EvaluateAsync(CancellationToken.None);

        Assert.Equal(RecognitionOutcome.Verified, result.Outcome);
        Assert.Equal(RecognitionStatus.FaceRecognized, result.RecognitionStatus);
        Assert.Equal(LivenessStatus.LivenessPassed, result.LivenessStatus);
        Assert.Equal(ImageMode.Color, result.ImageMode);
        Assert.Equal("Ola", result.PersonName);
        Assert.Equal(OlaId, result.PersonId);
        Assert.Equal(result.FrameFrom, result.TrackId);
        Assert.True(result.FrameTo > result.FrameFrom);
        Assert.True(result.WindowEnd > result.WindowStart);
        Assert.Equal(0.95f, result.LivenessScore);
    }

    [Fact]
    public async Task Liveness_feiler_gir_livenessFailed_men_beholder_identiteten()
    {
        var time = new FakeTime();
        var evaluator = Create(Buffer(time, true), new FixedDetector(() => [TheFace]), new FakeProvider(() => [OlaMatch()]), [new FakeModel(0.2f)]);

        var result = await evaluator.EvaluateAsync(CancellationToken.None);

        Assert.Equal(RecognitionOutcome.LivenessFailed, result.Outcome);
        Assert.Equal("Ola", result.PersonName);
    }

    [Fact]
    public async Task IR_gir_livenessUnavailable_aldri_verified_og_kjoerer_ikke_modellen()
    {
        var time = new FakeTime();
        var model = new FakeModel(0.99f);
        var provider = new FakeProvider(() => [OlaMatch()]);
        var evaluator = Create(Buffer(time, color: false), new FixedDetector(() => [TheFace]), provider, [model]);

        var result = await evaluator.EvaluateAsync(CancellationToken.None);

        Assert.Equal(RecognitionOutcome.LivenessUnavailable, result.Outcome);
        Assert.Equal(ImageMode.Infrared, result.ImageMode);
        Assert.Equal(LivenessStatus.LivenessUnavailable, result.LivenessStatus);
        Assert.Equal(RecognitionStatus.NotAttempted, result.RecognitionStatus);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task IR_med_gjenkjenning_paa_men_uten_ir_modell_gir_fortsatt_aldri_verified()
    {
        var time = new FakeTime();
        var evaluator = Create(Buffer(time, false), new FixedDetector(() => [TheFace]), new FakeProvider(() => [OlaMatch()]), [new FakeModel(0.99f)], irRecognition: true);

        var result = await evaluator.EvaluateAsync(CancellationToken.None);

        Assert.Equal(RecognitionOutcome.LivenessUnavailable, result.Outcome);
        Assert.Equal(RecognitionStatus.FaceRecognized, result.RecognitionStatus);
        Assert.Equal("no_liveness_model", result.Reason);
    }

    [Fact]
    public async Task Fremtidig_IR_modell_brukes_uten_endring_i_pipelinen()
    {
        var time = new FakeTime();
        var infraredModel = new FakeModel(0.9f, supportsColor: false, supportsInfrared: true);
        var evaluator = Create(Buffer(time, false), new FixedDetector(() => [TheFace]), new FakeProvider(() => [OlaMatch()]), [new FakeModel(0.1f), infraredModel], irRecognition: true);

        var result = await evaluator.EvaluateAsync(CancellationToken.None);

        Assert.Equal(RecognitionOutcome.Verified, result.Outcome);
        Assert.True(infraredModel.Calls > 0);
    }

    [Fact]
    public async Task Ukjent_bildetype_naar_kameraet_sier_farge_men_bildene_er_graa_gir_ikke_verified()
    {
        var time = new FakeTime();
        var evaluator = Create(Buffer(time, color: false), new FixedDetector(() => [TheFace]), new FakeProvider(() => [OlaMatch()]), [new FakeModel(0.99f)], ReportedDayNight.Color);

        var result = await evaluator.EvaluateAsync(CancellationToken.None);

        Assert.NotEqual(RecognitionOutcome.Verified, result.Outcome);
        Assert.Equal(ImageMode.Unknown, result.ImageMode);
    }

    [Fact]
    public async Task Liveness_ikke_paakrevd_hopper_over_modellen()
    {
        var time = new FakeTime();
        var model = new FakeModel(0.0f);
        var evaluator = Create(Buffer(time, true), new FixedDetector(() => [TheFace]), new FakeProvider(() => [OlaMatch()]), [model], required: false);

        var result = await evaluator.EvaluateAsync(CancellationToken.None);

        Assert.Equal(RecognitionOutcome.Verified, result.Outcome);
        Assert.Equal(LivenessStatus.NotRequired, result.LivenessStatus);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Identitetsbytte_i_sporet_avvises()
    {
        var time = new FakeTime();
        var calls = 0;
        var provider = new FakeProvider(() => [++calls % 2 == 0 ? new FaceMatchResult(true, "Kari", 0.8f, true, Guid.NewGuid()) : OlaMatch()]);
        var evaluator = Create(Buffer(time, true), new FixedDetector(() => [TheFace]), provider, [new FakeModel(0.99f)]);

        var result = await evaluator.EvaluateAsync(CancellationToken.None);

        Assert.Equal(RecognitionOutcome.Unknown, result.Outcome);
        Assert.Equal(RecognitionStatus.IdentityConflict, result.RecognitionStatus);
        Assert.Equal("identity_changed", result.Reason);
        Assert.Null(result.PersonName);
    }

    [Fact]
    public async Task Gamle_bilder_gir_error_og_aldri_verified()
    {
        var time = new FakeTime();
        var buffer = Buffer(time, true);
        time.Now += TimeSpan.FromSeconds(10);
        var evaluator = Create(buffer, new FixedDetector(() => [TheFace]), new FakeProvider(() => [OlaMatch()]), [new FakeModel(0.99f)]);

        var result = await evaluator.EvaluateAsync(CancellationToken.None);

        Assert.Equal(RecognitionOutcome.Error, result.Outcome);
        Assert.Equal("stale_frames", result.Reason);
    }

    [Fact]
    public async Task Ingen_ansikter_gir_noFace()
    {
        var time = new FakeTime();
        var evaluator = Create(Buffer(time, true), new FixedDetector(() => []), new FakeProvider(() => []), [new FakeModel(0.99f)]);

        var result = await evaluator.EvaluateAsync(CancellationToken.None);

        Assert.Equal(RecognitionOutcome.NoFace, result.Outcome);
    }

    [Fact]
    public async Task For_lite_ansikt_gjenkjennes_ikke()
    {
        var time = new FakeTime();
        var evaluator = Create(Buffer(time, true), new FixedDetector(() => [new FaceBox(100, 60, 20, 20)]), new FakeProvider(() => [OlaMatch()]), [new FakeModel(0.99f)]);

        var result = await evaluator.EvaluateAsync(CancellationToken.None);

        Assert.Equal(RecognitionOutcome.Unknown, result.Outcome);
        Assert.Equal("face_too_small", result.Reason);
    }

    [Fact]
    public async Task Deteksjon_kjoeres_kun_en_gang_per_bilde_ved_overlappende_triggere()
    {
        var time = new FakeTime();
        var detector = new FixedDetector(() => [TheFace]);
        var model = new FakeModel(0.95f);
        var evaluator = Create(Buffer(time, true), detector, new FakeProvider(() => [OlaMatch()]), [model]);

        await evaluator.EvaluateAsync(CancellationToken.None);
        var detectionsAfterFirst = detector.Calls;
        var modelCallsAfterFirst = model.Calls;
        await evaluator.EvaluateAsync(CancellationToken.None);

        Assert.Equal(detectionsAfterFirst, detector.Calls);
        Assert.Equal(modelCallsAfterFirst, model.Calls);
    }
}
