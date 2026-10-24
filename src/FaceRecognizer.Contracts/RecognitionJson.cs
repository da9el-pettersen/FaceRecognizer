using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaceRecognizer.Contracts;

/// <summary>Felles JSON-innstillinger for meldinger mellom modulene og Home Assistant.</summary>
public static class RecognitionJson
{
    /// <summary>camelCase og enum som tekst, slik at Home Assistant kan bruke <c>value_json.outcome</c>.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
