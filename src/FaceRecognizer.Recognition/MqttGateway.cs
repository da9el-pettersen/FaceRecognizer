using System.Text.Json;
using System.Threading.Channels;
using FaceRecognizer.Contracts;
using Microsoft.Extensions.Options;
using MQTTnet;

namespace FaceRecognizer.Recognition;

/// <summary>
/// MQTT-forbindelse mot Home Assistant: lytter på trigger, publiserer resultat og MQTT Discovery.
/// Tjenesten blokkerer på meldinger og bruker ingen polling.
/// </summary>
public sealed class MqttGateway : BackgroundService
{
    private readonly MqttOptions _options;
    private readonly Channel<DateTimeOffset> _triggers;
    private readonly TriggerGate _gate;
    private readonly ILogger<MqttGateway> _logger;
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();

    /// <summary>Oppretter gatewayen.</summary>
    public MqttGateway(
        IOptions<MqttOptions> options,
        Channel<DateTimeOffset> triggers,
        TriggerGate gate,
        ILogger<MqttGateway> logger)
    {
        _options = options.Value;
        _triggers = triggers;
        _gate = gate;
        _logger = logger;
    }

    /// <summary>Publiserer et resultat (retained) slik at Home Assistant alltid ser siste utfall.</summary>
    /// <param name="result">Resultatet som skal sendes.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task PublishResultAsync(RecognitionResult result, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(result, RecognitionJson.Options);
        await PublishAsync(_options.ResultTopic, payload, retain: true, cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client.ApplicationMessageReceivedAsync += e =>
        {
            if (e.ApplicationMessage.Topic != _options.TriggerTopic)
            {
                return Task.CompletedTask;
            }

            if (!_gate.TryEnter())
            {
                _logger.LogDebug("Opptatt, trigger forkastet.");
            }
            else if (!_triggers.Writer.TryWrite(DateTimeOffset.UtcNow))
            {
                _gate.Exit(TimeSpan.Zero);
            }

            return Task.CompletedTask;
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task OnDisconnected(MqttClientDisconnectedEventArgs _)
            {
                disconnected.TrySetResult();
                return Task.CompletedTask;
            }

            _client.DisconnectedAsync += OnDisconnected;
            try
            {
                await _client.ConnectAsync(BuildClientOptions(), stoppingToken);
                await _client.SubscribeAsync(
                    new MqttClientSubscribeOptionsBuilder().WithTopicFilter(_options.TriggerTopic).Build(),
                    stoppingToken);
                await PublishDiscoveryAsync(stoppingToken);
                await PublishAsync(_options.StatusTopic, "online", retain: true, stoppingToken);
                _logger.LogInformation("Tilkoblet MQTT {Host}:{Port}, venter på trigger.", _options.Host, _options.Port);

                await disconnected.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT-feil, prøver igjen om 5 sekunder.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                _client.DisconnectedAsync -= OnDisconnected;
            }
        }

        if (_client.IsConnected)
        {
            await _client.DisconnectAsync();
        }
    }

    private MqttClientOptions BuildClientOptions()
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId("facerecognizer-recognition")
            .WithWillTopic(_options.StatusTopic)
            .WithWillPayload("offline")
            .WithWillRetain();
        if (!string.IsNullOrEmpty(_options.Username))
        {
            builder.WithCredentials(_options.Username, _options.Password);
        }

        if (_options.UseTls)
        {
            builder.WithTlsOptions(o => o.UseTls());
        }

        return builder.Build();
    }

    private async Task PublishDiscoveryAsync(CancellationToken cancellationToken)
    {
        var device = new
        {
            identifiers = new[] { "facerecognizer" },
            name = "Face Recognizer",
            manufacturer = "FaceRecognizer"
        };

        var outcome = new
        {
            name = "Utfall",
            unique_id = "facerecognizer_outcome",
            state_topic = _options.ResultTopic,
            value_template = "{{ value_json.outcome }}",
            json_attributes_topic = _options.ResultTopic,
            availability_topic = _options.StatusTopic,
            icon = "mdi:face-recognition",
            device
        };
        var score = new
        {
            name = "Score",
            unique_id = "facerecognizer_score",
            state_topic = _options.ResultTopic,
            value_template = "{{ (value_json.score * 100) | round(2) }}",
            unit_of_measurement = "%",
            availability_topic = _options.StatusTopic,
            device
        };

        await PublishAsync(
            $"{_options.DiscoveryPrefix}/sensor/facerecognizer/outcome/config",
            JsonSerializer.Serialize(outcome), retain: true, cancellationToken);
        await PublishAsync(
            $"{_options.DiscoveryPrefix}/sensor/facerecognizer/score/config",
            JsonSerializer.Serialize(score), retain: true, cancellationToken);
    }

    private Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .Build();
        return _client.PublishAsync(message, cancellationToken);
    }
}
