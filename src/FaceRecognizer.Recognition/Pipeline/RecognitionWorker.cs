using System.Threading.Channels;
using FaceRecognizer.Contracts;
using Microsoft.Extensions.Options;
using FaceRecognizer.Recognition.Engines;
using FaceRecognizer.Recognition.Messaging;
using FaceRecognizer.Recognition.Configuration;

namespace FaceRecognizer.Recognition.Pipeline;

/// <summary>
/// Venter på triggere og analyserer de siste bildene fra den delte videokilden (<see cref="SequenceEvaluator"/>), og publiserer resultatet.
/// Feil gir alltid utfallet <see cref="RecognitionOutcome.Error"/> (døren forblir låst).
/// </summary>
public sealed class RecognitionWorker : BackgroundService
{
    private readonly Channel<DateTimeOffset> _triggers;
    private readonly TriggerGate _gate;
    private readonly SequenceEvaluator _evaluator;
    private readonly IFaceRecognitionProvider _provider;
    private readonly MqttGateway _gateway;
    private readonly PipelineOptions _options;
    private readonly CameraOptions _cameraOptions;
    private readonly ILogger<RecognitionWorker> _logger;

    /// <summary>Oppretter workeren.</summary>
    public RecognitionWorker(
        Channel<DateTimeOffset> triggers,
        TriggerGate gate,
        SequenceEvaluator evaluator,
        IFaceRecognitionProvider provider,
        MqttGateway gateway,
        IOptions<PipelineOptions> options,
        IOptions<CameraOptions> cameraOptions,
        ILogger<RecognitionWorker> logger)
    {
        _cameraOptions = cameraOptions.Value;
        _triggers = triggers;
        _gate = gate;
        _evaluator = evaluator;
        _provider = provider;
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
                result = await _evaluator.EvaluateAsync(stoppingToken);
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
                    CameraName: _cameraOptions.Name, Provider: _provider.Name, Reason: "exception");
            }

            try
            {
                await _gateway.PublishResultAsync(result, stoppingToken);
                _logger.LogInformation(
                    "Resultat: {Outcome} (gjenkjenning {Recognition}, liveness {Liveness}, match {Score:P2}, liveness-score {LivenessScore:0.000}, årsak {Reason})",
                    result.Outcome, result.RecognitionStatus, result.LivenessStatus, result.Score, result.LivenessScore, result.Reason ?? "-");
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
}
