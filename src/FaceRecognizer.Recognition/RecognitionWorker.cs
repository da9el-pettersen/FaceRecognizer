using System.Threading.Channels;
using FaceRecognizer.Contracts;
using Microsoft.Extensions.Options;

namespace FaceRecognizer.Recognition;

/// <summary>
/// Venter på triggere og kjører flyten: snapshot, match, IR, liveness, publisering.
/// Feil gir alltid utfallet <see cref="RecognitionOutcome.Error"/> (døren forblir låst).
/// </summary>
public sealed class RecognitionWorker : BackgroundService
{
    private readonly Channel<DateTimeOffset> _triggers;
    private readonly TriggerGate _gate;
    private readonly ReolinkCamera _camera;
    private readonly FaceMatcher _matcher;
    private readonly ILivenessChecker _liveness;
    private readonly MqttGateway _gateway;
    private readonly PipelineOptions _options;
    private readonly CameraOptions _cameraOptions;
    private readonly ILogger<RecognitionWorker> _logger;

    /// <summary>Oppretter workeren.</summary>
    public RecognitionWorker(
        Channel<DateTimeOffset> triggers,
        TriggerGate gate,
        ReolinkCamera camera,
        FaceMatcher matcher,
        ILivenessChecker liveness,
        MqttGateway gateway,
        IOptions<PipelineOptions> options,
        IOptions<CameraOptions> cameraOptions,
        ILogger<RecognitionWorker> logger)
    {
        _cameraOptions = cameraOptions.Value;
        _triggers = triggers;
        _gate = gate;
        _camera = camera;
        _matcher = matcher;
        _liveness = liveness;
        _gateway = gateway;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _matcher.LoadReferencesAsync(stoppingToken);
        _logger.LogInformation("Lastet {Count} referanseansikter.", _matcher.ReferenceCount);

        await foreach (var trigger in _triggers.Reader.ReadAllAsync(stoppingToken))
        {
            RecognitionResult result;
            try
            {
                result = await EvaluateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Feil under gjenkjenning.");
                result = new RecognitionResult(RecognitionOutcome.Error, null, 0f, DateTimeOffset.UtcNow);
            }

            try
            {
                await _gateway.PublishResultAsync(result, stoppingToken);
                _logger.LogInformation("Resultat: {Outcome} ({Score:P2})", result.Outcome, result.Score);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Kunne ikke publisere resultat.");
            }
            finally
            {
                _gate.Exit(_options.Cooldown);
            }
        }
    }

    private async Task<RecognitionResult> EvaluateAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _camera.GetSnapshotAsync(cancellationToken);

        if (ImageAnalysis.IsGrayscale(snapshot))
        {
            // Kameraet er allerede i IR: bildet duger til både match og liveness, så de kjører samtidig.
            var matchTask = Task.Run(() => _matcher.Match(snapshot), cancellationToken);
            var livenessTask = Task.Run(() => _liveness.Check(snapshot), cancellationToken);
            await Task.WhenAll(matchTask, livenessTask);
            return ToResult(matchTask.Result, livenessTask.Result);
        }

        // Fargebilde: først match. Bare ved treff sjekkes liveness, med bytte til IR.
        var match = await Task.Run(() => _matcher.Match(snapshot), cancellationToken);
        if (!match.FaceFound || !match.IsMatch)
        {
            return ToResult(match, null);
        }

        // Liveness på fargebildet kjører mens kameraet bytter til IR og tar nytt bilde.
        var colorLivenessTask = Task.Run(() => _liveness.Check(snapshot), cancellationToken);
        var infraredLivenessTask = CheckInfraredLivenessAsync(cancellationToken);
        await Task.WhenAll(colorLivenessTask, infraredLivenessTask);
        var color = colorLivenessTask.Result;
        var infrared = infraredLivenessTask.Result;
        var combined = new LivenessResult(color.IsLive && infrared.IsLive, Math.Min(color.Score, infrared.Score));
        return ToResult(match, combined);
    }

    private static RecognitionResult ToResult(FaceMatchResult match, LivenessResult? liveness)
    {
        var now = DateTimeOffset.UtcNow;
        if (!match.FaceFound)
        {
            return new RecognitionResult(RecognitionOutcome.NoFace, null, 0f, now);
        }

        if (!match.IsMatch)
        {
            return new RecognitionResult(RecognitionOutcome.Unknown, null, match.Score, now);
        }

        var outcome = liveness is { IsLive: true } ? RecognitionOutcome.Verified : RecognitionOutcome.LivenessFailed;
        return new RecognitionResult(outcome, match.PersonName, match.Score, now, liveness?.Score ?? 0f);
    }

    private async Task<LivenessResult> CheckInfraredLivenessAsync(CancellationToken cancellationToken)
    {
        await _camera.SetInfraredAsync(true, cancellationToken);
        try
        {
            await Task.Delay(_cameraOptions.IrSettleDelay, cancellationToken);
            var infrared = await _camera.GetSnapshotAsync(cancellationToken);
            return _liveness.Check(infrared);
        }
        finally
        {
            await _camera.SetInfraredAsync(false, CancellationToken.None);
        }
    }
}
