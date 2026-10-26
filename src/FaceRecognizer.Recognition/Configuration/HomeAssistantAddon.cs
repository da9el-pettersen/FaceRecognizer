using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FaceRecognizer.Recognition;

/// <summary>Støtte for å kjøre som Home Assistant-app: innstillinger og MQTT-tilkobling fra Supervisor.</summary>
public static class HomeAssistantAddon
{
    /// <summary>Plassering av innstillingene brukeren har satt i HA.</summary>
    public const string OptionsPath = "/data/options.json";

    /// <summary>Mappe i app-ens vedvarende konfigurasjon (addon_config) for referansebilder.</summary>
    public const string FacesFolder = "/config/faces";

    /// <summary>
    /// Gjør om options.json (snake_case) til konfigurasjonsnøkler (<c>Camera:BaseUrl</c>).
    /// Tomme og null-verdier hoppes over slik at standardverdiene gjelder.
    /// </summary>
    /// <param name="json">Innholdet i options.json.</param>
    public static Dictionary<string, string?> FlattenOptions(string json)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        Flatten(document.RootElement, "", result);
        return result;
    }

    /// <summary>Henter MQTT-tilkoblingen fra Supervisor, uten at bruker trenger å oppgi passord.</summary>
    /// <param name="http">HTTP-klient.</param>
    /// <param name="supervisorToken">Verdien av SUPERVISOR_TOKEN.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public static async Task<Dictionary<string, string?>> GetMqttServiceAsync(
        HttpClient http, string supervisorToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://supervisor/services/mqtt");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", supervisorToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var data = json.GetProperty("data");
        return new Dictionary<string, string?>
        {
            ["Mqtt:Host"] = data.GetProperty("host").GetString(),
            ["Mqtt:Port"] = data.GetProperty("port").GetInt32().ToString(),
            ["Mqtt:Username"] = data.GetProperty("username").GetString(),
            ["Mqtt:Password"] = data.GetProperty("password").GetString(),
            ["Mqtt:UseTls"] = data.GetProperty("ssl").GetBoolean().ToString()
        };
    }

    /// <summary>Gjør om <c>base_url</c> til <c>BaseUrl</c>.</summary>
    public static string ToPascalCase(string name)
    {
        var builder = new StringBuilder();
        foreach (var part in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
        }

        return builder.ToString();
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string?> result)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = prefix.Length == 0 ? ToPascalCase(property.Name) : $"{prefix}:{ToPascalCase(property.Name)}";
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Flatten(property.Value, key, result);
                    break;
                case JsonValueKind.Null:
                    break;
                case JsonValueKind.String when string.IsNullOrWhiteSpace(property.Value.GetString()):
                    break;
                case JsonValueKind.String:
                    result[key] = property.Value.GetString();
                    break;
                default:
                    result[key] = property.Value.GetRawText();
                    break;
            }
        }
    }
}
