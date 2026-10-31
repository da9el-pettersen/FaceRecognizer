using System.Diagnostics;
using FaceRecognizer.Recognition.Configuration;
using FaceRecognizer.Recognition.Pipeline;
using Microsoft.Extensions.Options;

namespace FaceRecognizer.Recognition.Video;

/// <summary>
/// Holder én RTSP-forbindelse (via én ffmpeg-prosess) åpen og fyller <see cref="FrameBuffer"/> med bilder.
/// Kobler til på nytt med eksponentiell ventetid når strømmen stopper eller prosessen dør.
/// </summary>
public sealed class RtspFrameSource : BackgroundService
{
    private readonly FrameBuffer _buffer;
    private readonly CameraOptions _camera;
    private readonly StreamOptions _stream;
    private readonly ILogger<RtspFrameSource> _logger;
    private long _framesReceived;
    private long _restarts;
    private Process? _current;

    /// <summary>Prosessortid brukt av gjeldende ffmpeg-prosess, eller <c>null</c> hvis ingen kjører. Brukes til å måle belastning.</summary>
    public TimeSpan? FfmpegCpuTime
    {
        get
        {
            try
            {
                return _current?.TotalProcessorTime;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Oppretter kilden.</summary>
    public RtspFrameSource(
        FrameBuffer buffer,
        IOptions<CameraOptions> camera,
        IOptions<StreamOptions> stream,
        ILogger<RtspFrameSource> logger)
    {
        _buffer = buffer;
        _camera = camera.Value;
        _stream = stream.Value;
        _logger = logger;
    }

    /// <summary>Antall bilder mottatt fra ffmpeg siden oppstart (før duplikatfiltrering).</summary>
    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>Antall ganger forbindelsen er startet på nytt.</summary>
    public long Restarts => Interlocked.Read(ref _restarts);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string url;
        try
        {
            url = RtspStream.BuildUrl(_camera);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError("Videostrøm er ikke konfigurert: {Message}", ex.Message);
            return;
        }

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            var received = await RunOnceAsync(url, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            // Fikk vi bilder, var forbindelsen frisk: start ventetiden på nytt.
            backoff = received > 0 ? TimeSpan.FromSeconds(1) : Min(backoff * 2, _stream.ReconnectMaxBackoff);
            Interlocked.Increment(ref _restarts);
            _logger.LogWarning("Videostrømmen stoppet ({Frames} bilder). Kobler til på nytt om {Delay:F0} s.", received, backoff.TotalSeconds);
            try
            {
                await Task.Delay(backoff, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<long> RunOnceAsync(string url, CancellationToken stoppingToken)
    {
        var generation = _buffer.StartNewSession();
        var startInfo = new ProcessStartInfo(_stream.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in RtspStream.BuildArguments(url, _stream))
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffmpeg startet ikke.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogError("Kunne ikke starte ffmpeg ({Path}): {Message}", _stream.FfmpegPath, ex.Message);
            return 0;
        }

        _logger.LogInformation("Videostrøm startet (forbindelse {Generation}): {Url}", generation, RtspStream.Redact(url));
        using (process)
        {
            _current = process;
            // Strømmen regnes som død hvis ingen bilder kommer innen StallTimeout.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            using var errorPump = StartErrorPump(process, stoppingToken);
            long received = 0;
            try
            {
                stall.CancelAfter(_stream.StallTimeout * 3);
                while (true)
                {
                    var raw = await PpmFrameReader.ReadAsync(process.StandardOutput.BaseStream, stall.Token);
                    if (raw is null)
                    {
                        break;
                    }

                    stall.CancelAfter(_stream.StallTimeout);
                    received++;
                    Interlocked.Increment(ref _framesReceived);
                    var frame = new Frame(_buffer.NextFrameId(), generation, _buffer.Time.GetUtcNow(), raw.Value.Width, raw.Value.Height, raw.Value.Buffer);
                    _buffer.Add(frame);
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning("Ingen bilder fra kameraet på {Timeout:F0} s.", _stream.StallTimeout.TotalSeconds);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                _logger.LogWarning("Feil i videostrømmen: {Message}", ex.Message);
            }
            finally
            {
                _current = null;
                Kill(process);
                _buffer.StartNewSession();
            }

            return received;
        }
    }

    private IDisposable StartErrorPump(Process process, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _ = Task.Run(async () =>
        {
            try
            {
                // Maks 5 linjer per 10 s; resten telles og oppsummeres, så en feilende strøm ikke oversvømmer loggen.
                var windowStart = Environment.TickCount64;
                var logged = 0;
                var suppressed = 0;
                while (await process.StandardError.ReadLineAsync(cts.Token) is { } line)
                {
                    var now = Environment.TickCount64;
                    if (now - windowStart >= 10_000)
                    {
                        if (suppressed > 0)
                        {
                            _logger.LogWarning("ffmpeg: {Count} flere meldinger undertrykt", suppressed);
                        }

                        windowStart = now;
                        logged = 0;
                        suppressed = 0;
                    }

                    if (logged < 5)
                    {
                        logged++;
                        _logger.LogWarning("ffmpeg: {Line}", RtspStream.Redact(line));
                    }
                    else
                    {
                        suppressed++;
                    }
                }
            }
            catch (Exception)
            {
                // Prosessen er avsluttet eller vi stopper: ingenting å logge.
            }
        }, CancellationToken.None);
        return cts;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
