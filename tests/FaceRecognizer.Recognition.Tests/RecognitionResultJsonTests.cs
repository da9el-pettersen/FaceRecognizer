using System.Text.Json;
using FaceRecognizer.Contracts;

namespace FaceRecognizer.Recognition.Tests;

public class RecognitionResultJsonTests
{
    [Fact]
    public void Serialiseres_med_camelCase_og_enum_som_tekst()
    {
        var result = new RecognitionResult(RecognitionOutcome.LivenessFailed, "Ola", 0.99f, DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(result, RecognitionJson.Options);

        Assert.Contains("\"outcome\":\"livenessFailed\"", json);
        Assert.Contains("\"personName\":\"Ola\"", json);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("AA", false)]
    public void Sertifikatvalidering_avviser_uten_gyldig_fingeravtrykk(string pinned, bool expected)
    {
        var validate = ReolinkCamera.CreateCertificateValidator(pinned);

        Assert.Equal(expected, validate(new HttpRequestMessage(), null, null, default));
    }
}
