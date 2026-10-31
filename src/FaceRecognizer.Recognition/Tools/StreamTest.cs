using System.Diagnostics;
using FaceAiSharp;
using FaceRecognizer.Recognition.Configuration;
using FaceRecognizer.Recognition.Video;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace FaceRecognizer.Recognition.Tools;

/// <summary>
/// Måler videokilden uten MQTT/Home Assistant: <c>streamtest [--seconds 30] [--stream main|sub]</c>.
/// Rapporterer oppløsning, faktisk bildefrekvens, forkastede duplikater, CPU og minne (ffmpeg og denne prosessen)
/// og tid per ansiktsdeteksjon på full oppløsning og nedskalert. Grunnlag for valget mellom main og sub.
/// </summary>
public static class StreamTest
{
    private const int DetectionWidth = 640;

    /// <summary>Kjører målingen og returnerer 0 hvis det kom bilder.</summary>
    /// <param name="services">Tjenesteleverandør fra bygget host (hosted services startes ikke).</param>
    /// <param name="args">Kommandolinjeargumenter.</param>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var seconds = int.TryParse(Value(args, "--seconds"), out var s) ? s : 30;
        var camera = services.GetRequiredService<IOptions<CameraOptions>>().Value;
        if (Value(args, "--stream") is { } stream)
        {
            camera.Stream = stream;
        }

        var options = services.GetRequiredService<IOptions<StreamOptions>>().Value;
        var buffer = services.GetRequiredService<FrameBuffer>();
        var source = services.GetRequiredService<RtspFrameSource>();
        var self = Process.GetCurrentProcess();
        var detector = FaceAiSharpBundleFactory.CreateFaceDetectorWithLandmarks();
        Console.WriteLine($"Strøm: {camera.Stream}, mål {options.Fps} fps, maks bredde {options.MaxWidth}, kjører {seconds} s");

        await source.StartAsync(CancellationToken.None);
        var start = Stopwatch.StartNew();
        var selfCpuStart = self.TotalProcessorTime;
        var ffmpegCpuLast = TimeSpan.Zero;
        var detectionFull = new List<double>();
        var detectionSmall = new List<double>();
        var resolution = "-";
        long lastDetected = 0;
        long peakMemory = 0;

        while (start.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(1000);
            ffmpegCpuLast = source.FfmpegCpuTime ?? ffmpegCpuLast;
            self.Refresh();
            peakMemory = Math.Max(peakMemory, self.WorkingSet64);
            var result = buffer.GetWindow(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 1);
            if (result.Window is null)
            {
                continue;
            }

            using (result.Window)
            {
                var frame = result.Window.Frames[^1];
                resolution = $"{frame.Width}x{frame.Height}";
                if (frame.FrameId == lastDetected)
                {
                    continue;
                }

                lastDetected = frame.FrameId;
                using var full = Image.LoadPixelData<Rgb24>(frame.Rgb, frame.Width, frame.Height);
                var timer = Stopwatch.StartNew();
                detector.DetectFaces(full);
                detectionFull.Add(timer.Elapsed.TotalMilliseconds);

                using var small = full.Clone(c => c.Resize(DetectionWidth, 0));
                timer.Restart();
                detector.DetectFaces(small);
                detectionSmall.Add(timer.Elapsed.TotalMilliseconds);
            }
        }

        await source.StopAsync(CancellationToken.None);
        var elapsed = start.Elapsed.TotalSeconds;
        var selfCpu = (self.TotalProcessorTime - selfCpuStart).TotalSeconds;
        Console.WriteLine($"Oppløsning: {resolution}");
        Console.WriteLine($"Bilder mottatt: {source.FramesReceived} ({source.FramesReceived / elapsed:0.0} fps), forkastet som duplikat: {buffer.DuplicatesRejected}, reconnect: {source.Restarts}");
        Console.WriteLine($"CPU ffmpeg: {ffmpegCpuLast.TotalSeconds / elapsed * 100:0}% av én kjerne; denne prosessen (inkl. deteksjonstest): {selfCpu / elapsed * 100:0}%");
        Console.WriteLine($"Minne (topp arbeidssett, denne prosessen): {peakMemory / 1024 / 1024} MB");
        if (detectionFull.Count > 0)
        {
            Console.WriteLine($"Deteksjon full oppløsning: median {Median(detectionFull):0} ms; nedskalert til {DetectionWidth} px: median {Median(detectionSmall):0} ms ({detectionFull.Count} prøver)");
        }

        return source.FramesReceived > 0 ? 0 : 1;
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
