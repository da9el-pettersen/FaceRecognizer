using System.Threading.Channels;
using FaceRecognizer.Contracts;
using Microsoft.Extensions.Options;
using FaceRecognizer.Recognition.Engines;
using FaceRecognizer.Recognition.Liveness;
using FaceRecognizer.Recognition.Camera;
using FaceRecognizer.Recognition.Messaging;
using FaceRecognizer.Recognition.Configuration;

namespace FaceRecognizer.Recognition.Pipeline;

/// <summary>
/// Venter på triggere og kjører flyten: snapshot, match, IR, liveness, publisering.
/// Feil gir alltid utfallet <see cref="RecognitionOutcome.Error"/> (døren forblir låst).
/// </summary>
public sealed class RecognitionWorker : BackgroundService
{
    private readonly Channel<DateTimeOffset> _triggers;
    private readonly TriggerGate _gate;
    private readonly ReolinkCamera _camera;
    private readonly IFaceRecognitionProvider _provider;
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
        IFaceRecognitionProvider provider,
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
        _provider = provider;
        _liveness = liveness;
        _gateway = gateway;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _provider.LoadReferencesAsync(stoppingToken);
            _logger.LogInformation(
                "Motor {Provider}: lastet {Count} referanseansikter.", _provider.Name, _provider.ReferenceCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Motoren kan være midlertidig nede (f.eks. DeepFace). Referansene lastes på nytt ved første gjenkjenning.
            _logger.LogWarning(ex, "Kunne ikke laste referanser ved oppstart, prøver igjen ved første bruk.");
        }

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
                result = new RecognitionResult(
                    RecognitionOutcome.Error, null, 0f, DateTimeOffset.UtcNow,
                    CameraName: _cameraOptions.Name, Provider: _provider.Name);
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
        var first = await EvaluateOnceAsync(cancellationToken);
        return await RetryLivenessAsync(
            first, _options.LivenessAttempts, () => EvaluateOnceAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Når en person er gjenkjent, men liveness feilet, prøves nye bilder til samme person er bekreftet
    /// eller <paramref name="attempts"/> forsøk er brukt. Andre utfall i forsøkene teller som mislykket forsøk.
    /// </summary>
    /// <param name="first">Resultatet av første forsøk.</param>
    /// <param name="attempts">Totalt antall forsøk, inkludert det første.</param>
    /// <param name="evaluate">Tar nytt bilde og vurderer det.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    internal static async Task<RecognitionResult> RetryLivenessAsync(
        RecognitionResult first, int attempts, Func<Task<RecognitionResult>> evaluate,
        CancellationToken cancellationToken = default)
    {
        if (first.Outcome != RecognitionOutcome.LivenessFailed)
        {
            return first;
        }

        for (var attempt = 1; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = await evaluate();
            if (next.Outcome == RecognitionOutcome.Verified && next.PersonId == first.PersonId)
            {
                return next;
            }
        }

        return first;
    }

    private async Task<RecognitionResult> EvaluateOnceAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _camera.GetSnapshotAsync(cancellationToken);

        if (ImageAnalysis.IsGrayscale(snapshot))
        {
            // Kameraet er allerede i IR: bildet duger til både match og liveness, så de kjører samtidig.
            var matchTask = _provider.RecognizeAsync(snapshot, cancellationToken);
            var livenessTask = Task.Run(() => _liveness.Check(snapshot), cancellationToken);
            await Task.WhenAll(matchTask, livenessTask);
            return ToResult(matchTask.Result, livenessTask.Result, _cameraOptions.Name, _provider.Name);
        }

        // Fargebilde: først match. Bare ved treff sjekkes liveness, med bytte til IR.
        var match = await _provider.RecognizeAsync(snapshot, cancellationToken);
        if (match.Count == 0 || !match[0].IsMatch)
        {
            return ToResult(match, null, _cameraOptions.Name, _provider.Name);
        }

        // Liveness på fargebildet kjører mens kameraet bytter til IR og tar nytt bilde.
        var colorLivenessTask = Task.Run(() => _liveness.Check(snapshot), cancellationToken);
        var infraredLivenessTask = CheckInfraredLivenessAsync(cancellationToken);
        await Task.WhenAll(colorLivenessTask, infraredLivenessTask);
        var color = colorLivenessTask.Result;
        var infrared = infraredLivenessTask.Result;
        var combined = new LivenessResult(color.IsLive && infrared.IsLive, Math.Min(color.Score, infrared.Score));
        return ToResult(match, combined, _cameraOptions.Name, _provider.Name);
    }

    // Ved flere ansikter vurderes kun det største (første). Antallet publiseres slik at HA kan stille ekstra krav.
    internal static RecognitionResult ToResult(
        IReadOnlyList<FaceMatchResult> faces, LivenessResult? liveness, string camera, string provider)
    {
        var now = DateTimeOffset.UtcNow;
        if (faces.Count == 0)
        {
            return new RecognitionResult(RecognitionOutcome.NoFace, null, 0f, now, 0f, camera, 0, provider);
        }

        var match = faces[0];
        if (!match.IsMatch)
        {
            return new RecognitionResult(
                RecognitionOutcome.Unknown, null, match.Score, now, 0f, camera, faces.Count, provider);
        }

        var outcome = liveness is { IsLive: true } ? RecognitionOutcome.Verified : RecognitionOutcome.LivenessFailed;
        return new RecognitionResult(
            outcome, match.PersonName, match.Score, now, liveness?.Score ?? 0f, camera, faces.Count, provider,
            match.PersonId);
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
