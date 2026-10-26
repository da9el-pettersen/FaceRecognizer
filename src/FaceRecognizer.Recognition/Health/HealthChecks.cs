using Microsoft.Extensions.Diagnostics.HealthChecks;
using FaceRecognizer.Recognition.Engines;
using FaceRecognizer.Recognition.Messaging;

namespace FaceRecognizer.Recognition.Health;

/// <summary>Helsesjekk for MQTT-forbindelsen.</summary>
public sealed class MqttHealthCheck(MqttGateway gateway) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(gateway.IsConnected
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("MQTT er ikke tilkoblet."));
}

/// <summary>Helsesjekk for DeepFace. Feil her gir <c>Degraded</c>: resten av tjenesten fortsetter å kjøre.</summary>
public sealed class DeepFaceHealthCheck(DeepFaceRecognitionProvider provider) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await provider.PingAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded("DeepFace svarte med feilkode.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Degraded("DeepFace er ikke tilgjengelig.");
        }
    }
}

/// <summary>Logger helsestatus periodisk, uten å åpne noen porter.</summary>
public sealed class LogHealthCheckPublisher(ILogger<LogHealthCheckPublisher> logger) : IHealthCheckPublisher
{
    private HealthStatus? _last;

    /// <inheritdoc />
    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        if (_last != report.Status)
        {
            var problems = report.Entries.Where(e => e.Value.Status != HealthStatus.Healthy).Select(e => e.Key);
            logger.LogInformation(
                "Helsestatus: {Status} {Problems}", report.Status, string.Join(",", problems));
            _last = report.Status;
        }

        return Task.CompletedTask;
    }
}
