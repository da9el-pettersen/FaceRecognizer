using Microsoft.Extensions.Options;
using FaceRecognizer.Recognition.Engines;
using FaceRecognizer.Recognition.Liveness;
using FaceRecognizer.Recognition.Camera;
using FaceRecognizer.Recognition.Configuration;

namespace FaceRecognizer.Recognition.Tools;

/// <summary>
/// Lokal selvtest uten MQTT/Home Assistant: <c>selftest [--ir]</c>. Tester kamerakontakt (sertifikat-pinning og innlogging),
/// snapshot, ansiktsmotor og liveness, og lagrer bildene lokalt slik at de kan kontrolleres visuelt.
/// </summary>
public static class SelfTest
{
    /// <summary>Kjører selvtesten og returnerer 0 ved suksess.</summary>
    /// <param name="services">Tjenesteleverandør fra bygget host (hosted services startes ikke).</param>
    /// <param name="args">Kommandolinjeargumenter.</param>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var testIr = args.Contains("--ir");
        var outputFolder = Environment.GetEnvironmentVariable("SELFTEST_OUTPUT") ?? "/data/selftest";
        Directory.CreateDirectory(outputFolder);
        var camera = services.GetRequiredService<ReolinkCamera>();
        var provider = services.GetRequiredService<IFaceRecognitionProvider>();
        var liveness = services.GetRequiredService<ILivenessChecker>();
        var cameraOptions = services.GetRequiredService<IOptions<CameraOptions>>().Value;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        Console.WriteLine($"Kamera: {cameraOptions.BaseUrl} (kanal {cameraOptions.Channel}), motor: {provider.Name}");
        if (string.IsNullOrWhiteSpace(cameraOptions.CertificateSha256))
        {
            Console.WriteLine("FEIL: Camera:CertificateSha256 er ikke satt. Alle kamerakall avvises (sikker standard).");
            return 2;
        }

        byte[] color;
        try
        {
            color = await camera.GetSnapshotAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"FEIL: kunne ikke nå kameraet (nettverk eller sertifikat stemmer ikke med pinnet SHA-256): {ex.Message}");
            return 3;
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"FEIL: {ex.Message} Sjekk brukernavn/passord.");
            return 4;
        }

        var colorPath = Path.Combine(outputFolder, "snapshot.jpg");
        await File.WriteAllBytesAsync(colorPath, color, ct);
        Console.WriteLine($"OK: snapshot {color.Length} byte, gråtone={ImageAnalysis.IsGrayscale(color)}, lagret {colorPath}");

        try
        {
            await provider.LoadReferencesAsync(ct);
            var faces = await provider.RecognizeAsync(color, ct);
            Console.WriteLine($"OK: {provider.ReferenceCount} referanser, {faces.Count} ansikt(er) funnet");
            foreach (var face in faces)
            {
                Console.WriteLine($"    person={face.PersonName ?? "Unknown"} score={face.Score:0.000} match={face.IsMatch}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            Console.WriteLine($"FEIL: ansiktsmotoren {provider.Name} svarte ikke: {ex.Message}");
            return 5;
        }

        var colorLiveness = liveness.Check(color);
        Console.WriteLine($"OK: liveness (farge) live={colorLiveness.IsLive} score={colorLiveness.Score:0.000}");

        if (testIr)
        {
            await camera.SetInfraredAsync(true, ct);
            try
            {
                await Task.Delay(cameraOptions.IrSettleDelay, ct);
                var infrared = await camera.GetSnapshotAsync(ct);
                var irPath = Path.Combine(outputFolder, "snapshot-ir.jpg");
                await File.WriteAllBytesAsync(irPath, infrared, ct);
                var irLiveness = liveness.Check(infrared);
                Console.WriteLine(
                    $"OK: IR snapshot gråtone={ImageAnalysis.IsGrayscale(infrared)} (bør være True), lagret {irPath}");
                Console.WriteLine($"OK: liveness (IR) live={irLiveness.IsLive} score={irLiveness.Score:0.000}");
            }
            finally
            {
                await camera.SetInfraredAsync(false, CancellationToken.None);
                Console.WriteLine("OK: kameraet er satt tilbake til automatisk dag/natt-modus");
            }
        }

        return 0;
    }
}
