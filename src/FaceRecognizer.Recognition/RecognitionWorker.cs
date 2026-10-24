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

            await Task.Delay(_options.Cooldown, stoppingToken);
            while (_triggers.Reader.TryRead(out _))
            {
            }
        }
    }

    private async Task<RecognitionResult> EvaluateAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _camera.GetSnapshotAsync(cancellationToken);
        var match = _matcher.Match(snapshot);

        if (!match.FaceFound)
        {
            return new RecognitionResult(RecognitionOutcome.NoFace, null, 0f, DateTimeOffset.UtcNow);
        }

        if (!match.IsMatch)
        {
            return new RecognitionResult(RecognitionOutcome.Unknown, null, match.Score, DateTimeOffset.UtcNow);
        }

        var live = await CheckLivenessAsync(cancellationToken);
        var outcome = live ? RecognitionOutcome.Verified : RecognitionOutcome.LivenessFailed;
        return new RecognitionResult(outcome, match.PersonName, match.Score, DateTimeOffset.UtcNow);
    }

    private async Task<bool> CheckLivenessAsync(CancellationToken cancellationToken)
    {
        await _camera.SetInfraredAsync(true, cancellationToken);
        try
        {
            await Task.Delay(_cameraOptions.IrSettleDelay, cancellationToken);
            var infrared = await _camera.GetSnapshotAsync(cancellationToken);
            return await _liveness.IsLiveAsync(infrared, cancellationToken);
        }
        finally
        {
            await _camera.SetInfraredAsync(false, CancellationToken.None);
        }
    }
}
