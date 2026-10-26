using System.Text.Json;
using FaceRecognizer.Contracts;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace FaceRecognizer.Database.Messaging;

/// <summary>MQTT-tilkobling for endringshendelser. Tom <see cref="Host"/> slår publisering av.</summary>
public sealed class MqttOptions
{
    /// <summary>Vertsnavn på brokeren. Tomt = ingen publisering.</summary>
    public string Host { get; set; } = "";

    /// <summary>Port på brokeren.</summary>
    public int Port { get; set; } = 1883;

    /// <summary>Brukernavn.</summary>
    public string? Username { get; set; }

    /// <summary>Passord.</summary>
    public string? Password { get; set; }

    /// <summary>Om TLS skal brukes.</summary>
    public bool UseTls { get; set; }
}

/// <summary>Varsler andre tjenester om at embeddings er endret.</summary>
public interface IChangeNotifier
{
    /// <summary>Publiserer en endringshendelse. Feil logges og kaster ikke, siden endringen allerede er lagret.</summary>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    Task NotifyAsync(CancellationToken cancellationToken);
}

/// <summary>Publiserer <see cref="EmbeddingsChanged"/> på MQTT (QoS 1, ikke retained).</summary>
public sealed class MqttChangeNotifier : IChangeNotifier, IAsyncDisposable
{
    private readonly MqttOptions _options;
    private readonly ILogger<MqttChangeNotifier> _logger;
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    /// <summary>Oppretter notifikatoren.</summary>
    public MqttChangeNotifier(IOptions<MqttOptions> options, ILogger<MqttChangeNotifier> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task NotifyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.Host))
        {
            return;
        }

        try
        {
            await EnsureConnectedAsync(cancellationToken);
            var payload = JsonSerializer.Serialize(new EmbeddingsChanged(DateTimeOffset.UtcNow), RecognitionJson.Options);
            await _client.PublishAsync(
                new MqttApplicationMessageBuilder().WithTopic(EmbeddingsChanged.Topic).WithPayload(payload).Build(),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Kunne ikke publisere endringshendelse. Gjenkjenning henter nye data ved neste omstart.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            await _client.DisconnectAsync();
        }

        _client.Dispose();
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected)
        {
            return;
        }

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_client.IsConnected)
            {
                return;
            }

            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(_options.Host, _options.Port)
                .WithClientId("facerecognizer-database");
            if (!string.IsNullOrEmpty(_options.Username))
            {
                builder.WithCredentials(_options.Username, _options.Password);
            }

            if (_options.UseTls)
            {
                builder.WithTlsOptions(o => o.UseTls());
            }

            await _client.ConnectAsync(builder.Build(), cancellationToken);
        }
        finally
        {
            _connectLock.Release();
        }
    }
}
