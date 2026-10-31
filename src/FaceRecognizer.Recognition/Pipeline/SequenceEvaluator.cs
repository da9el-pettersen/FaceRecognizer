using FaceRecognizer.Contracts;
using FaceRecognizer.Recognition.Camera;
using FaceRecognizer.Recognition.Configuration;
using FaceRecognizer.Recognition.Engines;
using FaceRecognizer.Recognition.Liveness;
using FaceRecognizer.Recognition.Video;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FaceRecognizer.Recognition.Pipeline;

/// <summary>
/// Analyserer en sekvens bilder fra den delte videokilden: ansiktsdeteksjon og sporing, identitetskontroll på flere bilder,
/// bildetype (RGB/IR) og liveness på akkurat de samme bildene og det samme sporet. Gjenkjenning og liveness deler bilde-ID,
/// tidsstempel og spor-ID. Feil og usikkerhet gir aldri <see cref="RecognitionOutcome.Verified"/>.
/// </summary>
public sealed class SequenceEvaluator
{
    private const float CropMargin = 0.5f;

    private readonly FrameBuffer _buffer;
    private readonly IFrameFaceDetector _detector;
    private readonly IFaceRecognitionProvider _provider;
    private readonly IReadOnlyList<ILivenessModel> _models;
    private readonly ICameraModeProvider _mode;
    private readonly PipelineOptions _pipeline;
    private readonly LivenessOptions _liveness;
    private readonly CameraOptions _camera;
    private readonly ILogger<SequenceEvaluator> _logger;

    /// <summary>Oppretter evaluatoren.</summary>
    public SequenceEvaluator(
        FrameBuffer buffer,
        IFrameFaceDetector detector,
        IFaceRecognitionProvider provider,
        IEnumerable<ILivenessModel> models,
        ICameraModeProvider mode,
        IOptions<PipelineOptions> pipeline,
        IOptions<LivenessOptions> liveness,
        IOptions<CameraOptions> camera,
        ILogger<SequenceEvaluator> logger)
    {
        _buffer = buffer;
        _detector = detector;
        _provider = provider;
        _models = models.ToList();
        _mode = mode;
        _pipeline = pipeline.Value;
        _liveness = liveness.Value;
        _camera = camera.Value;
        _logger = logger;
    }

    /// <summary>Analyserer de siste bildene og returnerer resultatet. Kaster ikke ved avvist sekvens; det gir et resultat med årsak.</summary>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task<RecognitionResult> EvaluateAsync(CancellationToken cancellationToken)
    {
        var now = _buffer.Time.GetUtcNow();
        var windowResult = _buffer.GetWindow(_pipeline.Window, _pipeline.MaxFrameAge, _pipeline.MaxFrameGap, _pipeline.MinFrames);
        if (windowResult.Window is null)
        {
            _logger.LogWarning("Sekvens avvist: {Reason}", windowResult.Reason);
            return Compose(RecognitionStatus.NotAttempted, LivenessStatus.NotEvaluated, null, 0f, 0f, windowResult.Reason, null, now, RecognitionOutcome.Error);
        }

        using var window = windowResult.Window;
        var frames = window.Frames;

        var perFrame = await Task.Run(
            () => frames.Select(f => f.GetOrDetectFaces(_detector.Detect)).ToList(), cancellationToken);
        var tracking = FaceTracker.Track(
            perFrame.Select(faces => (IReadOnlyList<FaceBox>)faces.Select(f => f.Box).ToArray()).ToList(),
            _pipeline.TrackMinIou);
        if (tracking.Primary is null)
        {
            _logger.LogInformation("Ingen brukbar sporing: {Reason} ({Faces} ansikt(er))", tracking.Reason, tracking.FaceCount);
            var status = tracking.Reason == "no_face" ? RecognitionStatus.NoFace : RecognitionStatus.Unknown;
            var info = new SequenceInfo(frames[0].FrameId, frames[0].FrameId, frames[^1].FrameId, window.Start, window.End, ImageMode.Unknown, tracking.FaceCount);
            return Compose(status, LivenessStatus.NotEvaluated, null, 0f, 0f, tracking.Reason, info, now);
        }

        var track = tracking.Primary;
        var observations = track.FrameIndexes.Select((index, i) => (Frame: frames[index], Face: perFrame[index].First(o => o.Box == track.Boxes[i]))).ToList();
        var mode = ImageModeClassifier.Classify(
            frames.Select(f => f.MeanChroma).ToList(), _mode.Current, _liveness.MonoMaxChroma, _liveness.ColorMinChroma);
        var sequence = new SequenceInfo(
            observations[0].Frame.FrameId, observations[0].Frame.FrameId, observations[^1].Frame.FrameId,
            observations[0].Frame.CapturedAt, observations[^1].Frame.CapturedAt, mode, tracking.FaceCount);

        if (track.MeanWidth < _pipeline.MinFaceWidth)
        {
            return Compose(RecognitionStatus.NotAttempted, LivenessStatus.NotEvaluated, null, 0f, 0f, "face_too_small", sequence, now);
        }

        var livenessWanted = _liveness.Required;
        if (mode != ImageMode.Color && !_pipeline.InfraredRecognition)
        {
            // Gjenkjenningen er ikke validert for IR, og liveness finnes ikke for IR: aldri verified.
            var live = livenessWanted ? LivenessStatus.LivenessUnavailable : LivenessStatus.NotRequired;
            var reason = mode == ImageMode.Infrared ? "infrared_not_supported" : "image_mode_unknown";
            return Compose(RecognitionStatus.NotAttempted, live, null, 0f, 0f, reason, sequence, now);
        }

        var identity = await RecognizeTrackAsync(observations, cancellationToken);
        if (identity.Status != RecognitionStatus.FaceRecognized)
        {
            return Compose(identity.Status, LivenessStatus.NotEvaluated, null, identity.Score, 0f, identity.Reason, sequence, now);
        }

        if (!livenessWanted)
        {
            return Compose(identity.Status, LivenessStatus.NotRequired, identity.Identity, identity.Score, 0f, null, sequence, now);
        }

        var model = _models.FirstOrDefault(m => m.Supports(mode));
        if (model is null)
        {
            return Compose(identity.Status, LivenessStatus.LivenessUnavailable, identity.Identity, identity.Score, 0f, "no_liveness_model", sequence, now);
        }

        var scores = await Task.Run(
            () => observations
                .Select(o => o.Face.GetOrComputeLivenessScore(model.Name, () => model.ScoreFace(o.Frame, o.Face.Box)))
                .ToList(),
            cancellationToken);
        var aggregate = LivenessAggregator.Aggregate(scores, model.MinScore, _liveness.MinLiveFraction, _pipeline.MinFrames);
        _logger.LogInformation(
            "Liveness ({Model}, {Mode}): {Status} median {Median:0.000}, andel over terskel {Fraction:P0}, {Frames} bilder, årsak {Reason}",
            model.Name, mode, aggregate.Status, aggregate.Score, aggregate.LiveFraction, scores.Count, aggregate.Reason ?? "-");
        return Compose(identity.Status, aggregate.Status, identity.Identity, identity.Score, aggregate.Score, aggregate.Reason, sequence, now);
    }

    private async Task<IdentityDecision> RecognizeTrackAsync(
        List<(Frame Frame, ObservedFace Face)> observations, CancellationToken cancellationToken)
    {
        var count = Math.Min(_pipeline.RecognitionFrames, observations.Count);
        var samples = new List<FrameIdentity?>(count);
        for (var i = 0; i < count; i++)
        {
            var (frame, face) = observations[(int)((i + 0.5) * observations.Count / count)];
            var jpeg = await Task.Run(() => CropToJpeg(frame, face.Box), cancellationToken);
            var faces = await _provider.RecognizeAsync(jpeg, cancellationToken);
            samples.Add(faces.Count == 0
                ? null
                : new FrameIdentity(faces[0].PersonId, faces[0].PersonName, faces[0].PersonEntityId, faces[0].Score, faces[0].IsMatch));
        }

        var decision = IdentityVerifier.Decide(
            samples, Math.Min(_pipeline.IdentityMinAgreement, count), _pipeline.IdentityMinFraction);
        _logger.LogInformation(
            "Identitet: {Status} ({Matches}/{Count} bilder), score {Score:0.000}, årsak {Reason}",
            decision.Status, samples.Count(s => s is { IsMatch: true }), count, decision.Score, decision.Reason ?? "-");
        return decision;
    }

    // Beskjæringen har margin rundt ansiktet slik at motorens "største ansikt" er sporets ansikt.
    internal static byte[] CropToJpeg(Frame frame, FaceBox box)
    {
        var marginX = box.Width * CropMargin;
        var marginY = box.Height * CropMargin;
        var x = Math.Max(0, (int)(box.X - marginX));
        var y = Math.Max(0, (int)(box.Y - marginY));
        var right = Math.Min(frame.Width, (int)(box.Right + marginX));
        var bottom = Math.Min(frame.Height, (int)(box.Bottom + marginY));
        using var image = Image.LoadPixelData<Rgb24>(frame.Rgb, frame.Width, frame.Height);
        image.Mutate(c => c.Crop(new Rectangle(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y))));
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 92 });
        return stream.ToArray();
    }

    private RecognitionResult Compose(
        RecognitionStatus recognition, LivenessStatus liveness, FrameIdentity? identity, float matchScore,
        float livenessScore, string? reason, SequenceInfo? sequence, DateTimeOffset now, RecognitionOutcome? forced = null)
    {
        var result = ResultComposer.Compose(
            recognition, liveness, identity, matchScore, livenessScore, reason, sequence, _camera.Name, _provider.Name, now);
        return forced is null ? result : result with { Outcome = forced.Value };
    }
}
