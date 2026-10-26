using System.Threading.Channels;

namespace FaceRecognizer.Recognition.Messaging;

/// <summary>Signal om at embeddings er endret og indeksen bør lastes på nytt. Flere signaler slås sammen til ett.</summary>
public sealed class ReferenceReloadSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Ber om ny innlasting.</summary>
    public void Request() => _channel.Writer.TryWrite(true);

    /// <summary>Venter til neste innlasting er bedt om.</summary>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async ValueTask WaitAsync(CancellationToken cancellationToken) =>
        await _channel.Reader.ReadAsync(cancellationToken);
}

/// <summary>Laster minneindeksen på nytt når databasetjenesten melder om endringer.</summary>
public sealed class ReferenceReloadWorker : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private readonly ReferenceReloadSignal _signal;
    private readonly Engines.IFaceRecognitionProvider _provider;
    private readonly ILogger<ReferenceReloadWorker> _logger;

    /// <summary>Oppretter tjenesten.</summary>
    public ReferenceReloadWorker(
        ReferenceReloadSignal signal, Engines.IFaceRecognitionProvider provider, ILogger<ReferenceReloadWorker> logger)
    {
        _signal = signal;
        _provider = provider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(stoppingToken);
                await ReloadWithRetryAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReloadWithRetryAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await _provider.LoadReferencesAsync(cancellationToken);
                _logger.LogInformation("Indeksen lastet på nytt: {Count} embeddings.", _provider.ReferenceCount);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Kunne ikke laste embeddings, prøver igjen om {Seconds} s.", RetryDelay.TotalSeconds);
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }
}
