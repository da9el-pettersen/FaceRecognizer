using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FaceRecognizer.Contracts;
using Microsoft.Extensions.Options;
using FaceRecognizer.Recognition.Camera;
using FaceRecognizer.Recognition.Configuration;

namespace FaceRecognizer.Recognition.Tools;

/// <summary>
/// Registrerer en person fra kameraet: <c>enroll &lt;person-ID&gt; [--count N] [--ir]</c>.
/// Tar flere snapshots med nedtelling mellom hver og sender dem til Admin-API-et (<c>Admin:BaseUrl</c> og
/// <c>Admin:Token</c>). Admin avviser bilder uten nøyaktig ett ansikt.
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
        if (!PersonNames.IsValid(personId))
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
        var configuration = services.GetRequiredService<IConfiguration>();
        var cameraOptions = services.GetRequiredService<IOptions<CameraOptions>>().Value;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = cts.Token;

        if (string.IsNullOrWhiteSpace(cameraOptions.CertificateSha256))
        {
            Console.WriteLine("FEIL: Camera:CertificateSha256 er ikke satt.");
            return 2;
        }

        var adminUrl = configuration["Admin:BaseUrl"];
        if (string.IsNullOrWhiteSpace(adminUrl) || string.IsNullOrWhiteSpace(configuration["Admin:Token"]))
        {
            Console.WriteLine("FEIL: Admin:BaseUrl og Admin:Token må være satt.");
            return 2;
        }

        using var admin = new HttpClient { BaseAddress = new Uri(adminUrl), Timeout = TimeSpan.FromSeconds(60) };
        admin.DefaultRequestHeaders.Authorization = new("Bearer", configuration["Admin:Token"]);

        Console.WriteLine($"Registrerer '{personId}' med {count} bilder{(withIr ? " + 2 IR-bilder" : "")}");
        var saved = 0;
        try
        {
            var person = await GetOrCreatePersonAsync(admin, personId, ct);
            for (var i = 0; i < count; i++)
            {
                saved += await CaptureAsync(camera, admin, person, Instructions[i % Instructions.Length], i + 1, count, ct) ? 1 : 0;
            }

            if (withIr)
            {
                await camera.SetInfraredAsync(true, ct);
                try
                {
                    await Task.Delay(cameraOptions.IrSettleDelay, ct);
                    for (var i = 0; i < 2; i++)
                    {
                        saved += await CaptureAsync(camera, admin, person, "IR-bilde: se rett inn i kameraet", i + 1, 2, ct) ? 1 : 0;
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
            Console.WriteLine($"FEIL: kamera eller Admin utilgjengelig: {ex.Message}");
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

    private static async Task<PersonDto> GetOrCreatePersonAsync(HttpClient admin, string name, CancellationToken ct)
    {
        using var created = await admin.PostAsJsonAsync("/api/persons", new CreatePersonRequest(name), ct);
        if (created.StatusCode == HttpStatusCode.Created)
        {
            return (await created.Content.ReadFromJsonAsync<PersonDto>(ct))!;
        }

        if (created.StatusCode != HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException($"Admin svarte {(int)created.StatusCode} ved opprettelse av person.");
        }

        var persons = await admin.GetFromJsonAsync<List<PersonSummaryDto>>("/api/persons", ct) ?? [];
        var existing = persons.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Personen finnes, men ble ikke funnet.");
        return new PersonDto(existing.PersonId, existing.Name);
    }

    private static async Task<bool> CaptureAsync(
        ReolinkCamera camera, HttpClient admin, PersonDto person,
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
            var status = await UploadAsync(admin, person.PersonId, last, ct);
            if (status == "added")
            {
                Console.WriteLine($"  OK: lagret (forsøk {attempt})");
                return true;
            }

            if (status == "duplicate")
            {
                Console.WriteLine("  Identisk bilde er allerede registrert, prøver igjen...");
            }
            else
            {
                Console.WriteLine($"  forsøk {attempt}/{MaxAttempts}: ikke nøyaktig ett ansikt funnet, prøver igjen...");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        var rejectedPath = Path.Combine(Environment.GetEnvironmentVariable("SELFTEST_OUTPUT") ?? "/data/selftest", "enroll-avvist.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(rejectedPath)!);
        await File.WriteAllBytesAsync(rejectedPath, last!, ct);
        Console.WriteLine($"  Hoppet over. Siste avviste bilde lagret i {rejectedPath}");
        return false;
    }

    private static async Task<string> UploadAsync(HttpClient admin, Guid personId, byte[] image, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "files", "snapshot.jpg");
        using var response = await admin.PostAsync($"/api/persons/{personId}/images", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Admin svarte {(int)response.StatusCode} ved opplasting.");
        }

        var results = await response.Content.ReadFromJsonAsync<List<ImageUploadResult>>(ct) ?? [];
        return results.FirstOrDefault()?.Status ?? "invalid";
    }
}
