using System.Threading.Channels;
using FaceAiSharp;
using FaceRecognizer.Recognition;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

if (File.Exists(HomeAssistantAddon.OptionsPath))
{
    // Kjører som HA-app: innstillinger fra /data/options.json, MQTT fra Supervisor.
    var settings = HomeAssistantAddon.FlattenOptions(File.ReadAllText(HomeAssistantAddon.OptionsPath));
    settings.TryAdd("Face:ReferenceFolder", HomeAssistantAddon.FacesFolder);
    var supervisorToken = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN");
    if (!settings.ContainsKey("Mqtt:Host") && !string.IsNullOrEmpty(supervisorToken))
    {
        using var http = new HttpClient();
        foreach (var (key, value) in await HomeAssistantAddon.GetMqttServiceAsync(http, supervisorToken, default))
        {
            settings[key] = value;
        }
    }

    builder.Configuration.AddInMemoryCollection(settings);
}

builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.Configure<CameraOptions>(builder.Configuration.GetSection("Camera"));
builder.Services.Configure<PipelineOptions>(builder.Configuration.GetSection("Pipeline"));
builder.Services.Configure<FaceMatchOptions>(builder.Configuration.GetSection("Face"));
builder.Services.Configure<LivenessOptions>(builder.Configuration.GetSection("Liveness"));

// Porten forkaster triggere mens vi er opptatt; kanalen bærer kun den ene aksepterte triggeren.
builder.Services.AddSingleton<TriggerGate>();
builder.Services.AddSingleton(Channel.CreateBounded<DateTimeOffset>(
    new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite }));

builder.Services.AddHttpClient<ReolinkCamera>((sp, client) =>
    {
        var camera = sp.GetRequiredService<IOptions<CameraOptions>>().Value;
        client.BaseAddress = new Uri(camera.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(15);
    })
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var camera = sp.GetRequiredService<IOptions<CameraOptions>>().Value;
        var validate = ReolinkCamera.CreateCertificateValidator(camera.CertificateSha256);
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
                validate(request, cert, chain, errors)
        };
    });

// Motor velges via FACE_RECOGNITION_PROVIDER (eller Face:Provider). FaceAiSharp er standard.
var providerName = Environment.GetEnvironmentVariable("FACE_RECOGNITION_PROVIDER")
    ?? builder.Configuration["Face:Provider"]
    ?? "FaceAiSharp";
builder.Services.Configure<DeepFaceOptions>(builder.Configuration.GetSection("DeepFace"));
builder.Services.AddHealthChecks().AddCheck<MqttHealthCheck>("mqtt");
builder.Services.AddSingleton<IHealthCheckPublisher, LogHealthCheckPublisher>();
builder.Services.Configure<HealthCheckPublisherOptions>(o =>
{
    o.Delay = TimeSpan.FromSeconds(10);
    o.Period = TimeSpan.FromSeconds(60);
});

if (providerName.Equals("DeepFace", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<DeepFaceRecognitionProvider>((sp, client) =>
    {
        var deepFace = sp.GetRequiredService<IOptions<DeepFaceOptions>>().Value;
        client.BaseAddress = new Uri(deepFace.BaseUrl);
        client.Timeout = deepFace.Timeout;
        if (!string.IsNullOrEmpty(deepFace.AuthToken))
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", deepFace.AuthToken);
        }
    });
    builder.Services.AddSingleton<IFaceRecognitionProvider>(
        sp => sp.GetRequiredService<DeepFaceRecognitionProvider>());
    builder.Services.AddHealthChecks().AddCheck<DeepFaceHealthCheck>("deepface");
}
else
{
    // Egne detektor-instanser: matcher og liveness kjører samtidig og deler ikke ONNX-tilstand.
    builder.Services.AddSingleton<IFaceRecognitionProvider>(sp => new FaceAiSharpRecognitionProvider(
        FaceAiSharpBundleFactory.CreateFaceDetectorWithLandmarks(),
        FaceAiSharpBundleFactory.CreateFaceEmbeddingsGenerator(),
        sp.GetRequiredService<IOptions<FaceMatchOptions>>().Value));
}
builder.Services.AddSingleton<ILivenessChecker>(sp => new MiniFasNetLivenessChecker(
    FaceAiSharpBundleFactory.CreateFaceDetectorWithLandmarks(),
    sp.GetRequiredService<IOptions<LivenessOptions>>()));
builder.Services.AddSingleton<MqttGateway>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttGateway>());
builder.Services.AddHostedService<RecognitionWorker>();

// IHttpClientFactory logger hver forespørsel på Information; vi vil kun se advarsler.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

if (args.Contains("selftest"))
{
    return await SelfTest.RunAsync(builder.Build().Services, args);
}

builder.Build().Run();
return 0;
