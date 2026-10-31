using FaceRecognizer.Recognition.Configuration;
using Microsoft.Extensions.Options;

namespace FaceRecognizer.Recognition.Camera;

/// <summary>
/// Leser kameraets konfigurerte dag/natt-modus jevnlig (kun lesing, endrer aldri kameraet). Verdien er utdatert etter
/// <see cref="MaxAge"/> og regnes da som ukjent. I automatisk modus finnes den faktiske tilstanden bare i kameraets
/// proprietære protokoll (Baichuan), som ikke brukes her; bildeanalysen avgjør da.
/// </summary>
public sealed class CameraModeMonitor : BackgroundService, ICameraModeProvider
{
    /// <summary>Hvor ofte modusen leses.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Eldste lesing som fortsatt regnes som gyldig.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(120);

    private readonly ReolinkCamera _camera;
    private readonly CameraOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CameraModeMonitor> _logger;
    private readonly object _lock = new();
    private ReportedDayNight _value;
    private DateTimeOffset _readAt;

    /// <summary>Oppretter monitoren.</summary>
    public CameraModeMonitor(
        ReolinkCamera camera, IOptions<CameraOptions> options, ILogger<CameraModeMonitor> logger, TimeProvider? time = null)
    {
        _camera = camera;
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ReportedDayNight Current
    {
        get
        {
            lock (_lock)
            {
                return _time.GetUtcNow() - _readAt <= MaxAge ? _value : ReportedDayNight.Unknown;
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Uten kamera-API (kun RTSP-adresse) finnes ingen modus å lese.
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(_options.Username))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var mode = await _camera.GetReportedDayNightAsync(stoppingToken);
                lock (_lock)
                {
                    _value = mode;
                    _readAt = _time.GetUtcNow();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning("Kunne ikke lese dag/natt-modus fra kameraet: {Message}", ex.Message);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
