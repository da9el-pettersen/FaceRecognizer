using System.Diagnostics;
using System.Globalization;
using FaceRecognizer.Contracts;
using FaceRecognizer.Recognition.Configuration;
using FaceRecognizer.Recognition.Engines;
using FaceRecognizer.Recognition.Pipeline;
using FaceRecognizer.Recognition.Video;
using Microsoft.Extensions.Options;

namespace FaceRecognizer.Recognition.Tools;

/// <summary>Tom embeddingkilde for <c>livetest --enroll</c>: indeksen fylles i minnet i stedet for fra databasen.</summary>
public sealed class EmptyEmbeddingSource : IEmbeddingSource
{
    /// <inheritdoc />
    public Task<EmbeddingSnapshotDto> LoadAsync(string modelId, CancellationToken cancellationToken) =>
        Task.FromResult(new EmbeddingSnapshotDto([], 0));
}

/// <summary>
/// Kjører hele pipelinen (ringbuffer → tracking → gjenkjenning → liveness) mot live video uten MQTT:
/// <c>livetest [--runs 10] [--pause 2] [--label scenario] [--csv fil] [--stream main|sub]</c>.
/// Hver kjøring skrives som én linje og kan appendes til CSV for å telle falske godkjenninger/avvisninger per scenario.
/// </summary>
public static class LiveTest
{
    /// <summary>Kjører testen. Returnerer 0 når minst én evaluering ble gjennomført.</summary>
    /// <param name="services">Tjenesteleverandør fra bygget host (hosted services startes ikke).</param>
    /// <param name="args">Kommandolinjeargumenter.</param>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var runs = int.TryParse(Value(args, "--runs"), out var r) ? r : 10;
        var pause = double.TryParse(Value(args, "--pause"), CultureInfo.InvariantCulture, out var p) ? p : 2;
        var label = Value(args, "--label") ?? "ukjent";
        var csv = Value(args, "--csv");
        var camera = services.GetRequiredService<IOptions<CameraOptions>>().Value;
        if (Value(args, "--stream") is { } stream)
        {
            camera.Stream = stream;
        }

        var source = services.GetRequiredService<RtspFrameSource>();
        var evaluator = services.GetRequiredService<SequenceEvaluator>();
        var provider = services.GetRequiredService<IFaceRecognitionProvider>();
        var self = Process.GetCurrentProcess();

        try
        {
            await provider.LoadReferencesAsync(CancellationToken.None);
            Console.WriteLine($"Motor: {provider.Name}, referanser: {provider.ReferenceCount}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Referanser kunne ikke lastes ({ex.GetType().Name}); gjenkjenning gir Unknown.");
        }

        Console.WriteLine($"Strøm {camera.Stream}");
        await source.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(5));

        var gate = Value(args, "--gate");
        if (Value(args, "--enroll") is { } enrollName && provider is FaceAiSharpRecognitionProvider local)
        {
            if (gate is not null)
            {
                await WaitForGateAsync(gate, "registrering");
            }

            var count = int.TryParse(Value(args, "--enroll-count"), out var c) ? c : 8;
            await EnrollLiveAsync(services, local, enrollName, count);
        }

        var phases = (Value(args, "--phases") ?? $"{label}:{runs}")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => (Label: x.Split(':')[0], Runs: int.Parse(x.Split(':')[1], CultureInfo.InvariantCulture)))
            .ToArray();
        var phaseWait = double.TryParse(Value(args, "--phase-wait"), CultureInfo.InvariantCulture, out var pw) ? pw : 15;

        var start = Stopwatch.StartNew();
        var cpuStart = self.TotalProcessorTime;
        var ffmpegCpu = TimeSpan.Zero;
        long peak = 0;
        var done = 0;
        var evalMs = new List<double>();
        var latency = new List<double>();
        foreach (var phase in phases)
        {
            if (gate is not null)
            {
                await WaitForGateAsync(gate, $"scenario '{phase.Label}'");
            }
            else
            {
                Console.WriteLine($"=== Scenario '{phase.Label}' starter om {phaseWait:0} s ({phase.Runs} kjøringer) ===");
                await Task.Delay(TimeSpan.FromSeconds(phaseWait));
            }

            for (var i = 1; i <= phase.Runs; i++)
            {
                var timer = Stopwatch.StartNew();
                var result = await evaluator.EvaluateAsync(CancellationToken.None);
                var elapsed = timer.Elapsed.TotalMilliseconds;
                var sinceLastFrame = result.WindowEnd is { } end ? (DateTimeOffset.UtcNow - end).TotalMilliseconds : double.NaN;
                evalMs.Add(elapsed);
                if (!double.IsNaN(sinceLastFrame))
                {
                    latency.Add(sinceLastFrame);
                }

                ffmpegCpu = source.FfmpegCpuTime ?? ffmpegCpu;
                self.Refresh();
                peak = Math.Max(peak, self.WorkingSet64);
                done++;
                var line = string.Create(CultureInfo.InvariantCulture,
                    $"{phase.Label};{i};{result.Outcome};{result.RecognitionStatus};{result.LivenessStatus};{result.ImageMode};{result.PersonName};" +
                    $"{result.Score:0.00};{result.LivenessScore:0.00};{result.Reason};{result.FaceCount};{result.FrameFrom};{result.FrameTo};{result.TrackId};{elapsed:0};{sinceLastFrame:0}");
                Console.WriteLine(line);
                if (csv is not null)
                {
                    await File.AppendAllTextAsync(csv, line + Environment.NewLine);
                }

                await Task.Delay(TimeSpan.FromSeconds(pause));
            }
        }

        await source.StopAsync(CancellationToken.None);
        var total = start.Elapsed.TotalSeconds;
        Console.WriteLine("(label;nr;outcome;recognition;liveness;bildetype;person;score;livenessScore;årsak;ansikter;fraBilde;tilBilde;spor;evalMs;alderSisteBildeMs)");
        Console.WriteLine($"Evaluering: median {Median(evalMs):0} ms, maks {evalMs.Max():0} ms");
        if (latency.Count > 0)
        {
            Console.WriteLine($"Alder på siste bilde ved ferdig resultat: median {Median(latency):0} ms");
        }

        Console.WriteLine($"Bilder mottatt: {source.FramesReceived} ({source.FramesReceived / total:0.0} fps), duplikater: {services.GetRequiredService<FrameBuffer>().DuplicatesRejected}, reconnect: {source.Restarts}");
        Console.WriteLine($"CPU ffmpeg: {ffmpegCpu.TotalSeconds / total * 100:0}% av én kjerne; prosessen: {(self.TotalProcessorTime - cpuStart).TotalSeconds / total * 100:0}%; minne topp: {peak / 1024 / 1024} MB");
        return done > 0 ? 0 : 1;
    }

    /// <summary>
    /// Venter til filen <c>go</c> dukker opp i <paramref name="gateDir"/> (manuell bekreftelse fra testleder), og sletter den.
    /// </summary>
    private static async Task WaitForGateAsync(string gateDir, string what)
    {
        Directory.CreateDirectory(gateDir);
        var go = Path.Combine(gateDir, "go");
        File.Delete(go);
        Console.WriteLine($"VENTER: {what}");
        while (!File.Exists(go))
        {
            await Task.Delay(200);
        }

        File.Delete(go);
        Console.WriteLine($"START: {what}");
    }

    /// <summary>
    /// Registrerer en testperson kun i minnet: tar <paramref name="count"/> ansiktsutsnitt fra live-video med ca. 1 s mellom
    /// (beveg hodet litt), og legger embeddingene i motorens indeks. Ingenting lagres permanent.
    /// </summary>
    private static async Task EnrollLiveAsync(IServiceProvider services, FaceAiSharpRecognitionProvider provider, string name, int count)
    {
        var buffer = services.GetRequiredService<FrameBuffer>();
        var detector = services.GetRequiredService<IFrameFaceDetector>();
        var personId = Guid.NewGuid();
        await provider.References.LoadAsync(CancellationToken.None);
        var added = 0;
        var lastFrame = 0L;
        Console.WriteLine($"Registrerer '{name}': {count} bilder. Se mot kameraet og vri hodet litt mellom hvert.");
        for (var attempt = 0; attempt < count * 6 && added < count; attempt++)
        {
            await Task.Delay(1000);
            var result = buffer.GetWindow(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), 1);
            if (result.Window is null)
            {
                continue;
            }

            using (result.Window)
            {
                var frame = result.Window.Frames[^1];
                if (frame.FrameId == lastFrame)
                {
                    continue;
                }

                lastFrame = frame.FrameId;
                var faces = detector.Detect(frame);
                if (faces.Count != 1 || faces[0].Width < 80)
                {
                    Console.WriteLine($"  hopper over ({faces.Count} ansikt, bredde {(faces.Count > 0 ? faces[0].Width : 0):0})");
                    continue;
                }

                var embeddings = await provider.EmbedAsync(SequenceEvaluator.CropToJpeg(frame, faces[0]));
                if (embeddings.Count == 1)
                {
                    provider.References.Index.Add(personId, name, ++added, embeddings[0]);
                    Console.WriteLine($"  referanse {added}/{count}");
                }
            }
        }

        Console.WriteLine($"Registrert {added} referanser for '{name}'.");
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
