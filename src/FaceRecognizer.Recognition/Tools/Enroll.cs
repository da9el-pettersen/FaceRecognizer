using Microsoft.Extensions.Options;
using FaceRecognizer.Recognition.Engines;
using FaceRecognizer.Recognition.Camera;
using FaceRecognizer.Recognition.Configuration;

namespace FaceRecognizer.Recognition.Tools;

/// <summary>
/// Registrerer en person fra kameraet: <c>enroll &lt;person-ID&gt; [--count N] [--ir]</c>.
/// Tar flere snapshots med nedtelling mellom hver, og lagrer kun bilder med nøyaktig ett ansikt i referanselageret.
/// </summary>
public static class Enroll
{
    private const int MaxAttempts = 6;

    private static readonly string[] Instructions =
    [
        "Se rett inn i kameraet",
        "Snu hodet litt til venstre (ca. 20 grader)",
        "Snu hodet litt til høyre (ca. 20 grader)",
        "Løft haken litt",
        "Senk haken litt",
        "Se rett frem, nøytralt uttrykk",
        "Se rett frem og smil",
        "Se rett frem fra litt lenger unna",
    ];

    /// <summary>Kjører registreringen og returnerer 0 hvis minst ett bilde ble lagret.</summary>
    /// <param name="services">Tjenesteleverandør fra bygget host (hosted services startes ikke).</param>
    /// <param name="args">Kommandolinjeargumenter.</param>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var index = Array.IndexOf(args, "enroll");
        var personId = args.Length > index + 1 ? args[index + 1] : "";
        if (!ReferenceStore.IsValidPersonId(personId))
        {
            Console.WriteLine("Bruk: enroll <person-ID> [--count N] [--ir]  (bokstaver, tall, mellomrom, _ og -)");
            return 1;
        }

        var count = 6;
        var countIndex = Array.IndexOf(args, "--count");
        if (countIndex >= 0 && (countIndex + 1 >= args.Length || !int.TryParse(args[countIndex + 1], out count)
                                || count is < 1 or > 20))
        {
            Console.WriteLine("--count må være et tall mellom 1 og 20.");
            return 1;
        }

        var withIr = args.Contains("--ir");
        var camera = services.GetRequiredService<ReolinkCamera>();
        var provider = services.GetRequiredService<IFaceRecognitionProvider>();
        var cameraOptions = services.GetRequiredService<IOptions<CameraOptions>>().Value;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = cts.Token;

        if (string.IsNullOrWhiteSpace(cameraOptions.CertificateSha256))
        {
            Console.WriteLine("FEIL: Camera:CertificateSha256 er ikke satt.");
            return 2;
        }

        Console.WriteLine($"Registrerer '{personId}' med {count} bilder{(withIr ? " + 2 IR-bilder" : "")}, motor: {provider.Name}");
        var saved = 0;
        try
        {
            for (var i = 0; i < count; i++)
            {
                saved += await CaptureAsync(camera, provider, personId, Instructions[i % Instructions.Length], i + 1, count, ct) ? 1 : 0;
            }

            if (withIr)
            {
                await camera.SetInfraredAsync(true, ct);
                try
                {
                    await Task.Delay(cameraOptions.IrSettleDelay, ct);
                    for (var i = 0; i < 2; i++)
                    {
                        saved += await CaptureAsync(camera, provider, personId, "IR-bilde: se rett inn i kameraet", i + 1, 2, ct) ? 1 : 0;
                    }
                }
                finally
                {
                    await camera.SetInfraredAsync(false, CancellationToken.None);
                }
            }
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"FEIL: kamera eller ansiktsmotor utilgjengelig: {ex.Message}");
            return 3;
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"FEIL: {ex.Message}");
            return 4;
        }

        Console.WriteLine($"Ferdig: {saved} bilde(r) lagret for '{personId}'. Anbefalt: 5-8 bilder.");
        return saved > 0 ? 0 : 1;
    }

    private static async Task<bool> CaptureAsync(
        ReolinkCamera camera, IFaceRecognitionProvider provider, string personId,
        string instruction, int number, int total, CancellationToken ct)
    {
        Console.WriteLine($"[{number}/{total}] {instruction}");
        for (var s = 4; s > 0; s--)
        {
            Console.Write($"  tar bilde om {s}... ");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        Console.WriteLine();
        byte[]? last = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            last = await camera.GetSnapshotAsync(ct);
            try
            {
                await provider.RegisterAsync(personId, last, ct);
                Console.WriteLine($"  OK: lagret (forsøk {attempt})");
                return true;
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"  forsøk {attempt}/{MaxAttempts}: ikke nøyaktig ett ansikt funnet, prøver igjen...");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }

        var rejectedPath = Path.Combine(Environment.GetEnvironmentVariable("SELFTEST_OUTPUT") ?? "/data/selftest", "enroll-avvist.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(rejectedPath)!);
        await File.WriteAllBytesAsync(rejectedPath, last!, ct);
        Console.WriteLine($"  Hoppet over. Siste avviste bilde lagret i {rejectedPath}");
        return false;
    }
}
