using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using FaceRecognizer.Recognition.Configuration;

namespace FaceRecognizer.Recognition.Camera;

/// <summary>Klient mot Reolink HTTP-API med sertifikat-pinning.</summary>
public sealed class ReolinkCamera
{
    private readonly HttpClient _http;
    private readonly CameraOptions _options;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires;

    /// <summary>Oppretter klienten.</summary>
    public ReolinkCamera(HttpClient http, IOptions<CameraOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    /// <summary>
    /// Lager valideringen som kun godtar kameraets kjente sertifikat.
    /// Uten konfigurert fingeravtrykk avvises alle sertifikater.
    /// </summary>
    public static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, System.Net.Security.SslPolicyErrors, bool>
        CreateCertificateValidator(string expectedSha256) =>
        (_, cert, _, _) =>
            cert is not null
            && !string.IsNullOrWhiteSpace(expectedSha256)
            && string.Equals(
                cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256),
                expectedSha256.Replace(":", "").Trim(),
                StringComparison.OrdinalIgnoreCase);

    /// <summary>Henter et stillbilde (JPEG) fra kameraet.</summary>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task<byte[]> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        var rs = Guid.NewGuid().ToString("N")[..8];
        var url = $"/cgi-bin/api.cgi?cmd=Snap&channel={_options.Channel}&rs={rs}&token={Uri.EscapeDataString(token)}";
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "image/jpeg")
        {
            _token = null;
            throw new InvalidOperationException("Kameraet returnerte ikke et JPEG-bilde.");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>Slår IR-lys og svart/hvitt-modus av eller på. Av gjenoppretter automatisk modus.</summary>
    /// <param name="enabled"><c>true</c> for IR, <c>false</c> for automatisk modus.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task SetInfraredAsync(bool enabled, CancellationToken cancellationToken)
    {
        var channel = _options.Channel;
        await SendCommandAsync("SetIrLights", new JsonObject
        {
            ["IrLights"] = new JsonObject { ["channel"] = channel, ["state"] = enabled ? "On" : "Auto" }
        }, cancellationToken);

        var isp = await SendCommandAsync("GetIsp", new JsonObject
        {
            ["channel"] = channel
        }, cancellationToken);
        var ispNode = isp?["value"]?["Isp"]?.DeepClone() as JsonObject
            ?? throw new InvalidOperationException("Kunne ikke lese ISP-innstillinger fra kameraet.");
        ispNode["dayNight"] = enabled ? "Black&White" : "Auto";
        await SendCommandAsync("SetIsp", new JsonObject { ["Isp"] = ispNode }, cancellationToken);
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpires)
        {
            return _token;
        }

        await _loginLock.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpires)
            {
                return _token;
            }

            var body = new JsonArray(new JsonObject
            {
                ["cmd"] = "Login",
                ["action"] = 0,
                ["param"] = new JsonObject
                {
                    ["User"] = new JsonObject
                    {
                        ["userName"] = _options.Username,
                        ["password"] = _options.Password
                    }
                }
            });
            using var response = await _http.PostAsJsonAsync("/cgi-bin/api.cgi?cmd=Login", body, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonArray>(cancellationToken);
            var token = json?[0]?["value"]?["Token"]?["name"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Innlogging mot kameraet feilet.");
            _token = token;
            _tokenExpires = DateTimeOffset.UtcNow.AddMinutes(30);
            return token;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private async Task<JsonNode?> SendCommandAsync(string cmd, JsonObject param, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        var body = new JsonArray(new JsonObject { ["cmd"] = cmd, ["action"] = 0, ["param"] = param });
        using var response = await _http.PostAsJsonAsync(
            $"/cgi-bin/api.cgi?cmd={cmd}&token={Uri.EscapeDataString(token)}", body, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonArray>(cancellationToken);
        var first = json?[0];
        if (first?["code"]?.GetValue<int>() != 0)
        {
            _token = null;
            throw new InvalidOperationException($"Kameraet avviste kommandoen {cmd}: {first?["error"]}");
        }

        return first;
    }
}
