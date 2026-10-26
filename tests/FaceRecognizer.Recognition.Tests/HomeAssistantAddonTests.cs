using FaceRecognizer.Recognition;
using FaceRecognizer.Recognition.Configuration;

namespace FaceRecognizer.Recognition.Tests;

public class HomeAssistantAddonTests
{
    [Fact]
    public void FlattenOptions_mapper_snake_case_til_konfigurasjonsnokler()
    {
        const string json = """
            { "camera": { "base_url": "https://1.2.3.4", "channel": 0, "password": "" },
              "face": { "threshold": 0.98 },
              "mqtt": { "host": null } }
            """;

        var result = HomeAssistantAddon.FlattenOptions(json);

        Assert.Equal("https://1.2.3.4", result["Camera:BaseUrl"]);
        Assert.Equal("0", result["Camera:Channel"]);
        Assert.Equal("0.98", result["Face:Threshold"]);
        Assert.DoesNotContain("Camera:Password", result.Keys);
        Assert.DoesNotContain("Mqtt:Host", result.Keys);
    }
}
