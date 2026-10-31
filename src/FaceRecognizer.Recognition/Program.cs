using System.Threading.Channels;
using FaceAiSharp;
using FaceRecognizer.Contracts;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using FaceRecognizer.Recognition.Engines;
using FaceRecognizer.Recognition.Liveness;
using FaceRecognizer.Recognition.Camera;
using FaceRecognizer.Recognition.Messaging;
using FaceRecognizer.Recognition.Pipeline;
using FaceRecognizer.Recognition.Configuration;
using FaceRecognizer.Recognition.Health;
using FaceRecognizer.Recognition.Tools;
using FaceRecognizer.Recognition.Api;
using FaceRecognizer.Recognition.Video;

var builder = WebApplication.CreateBuilder(args);

// Kjører som HA-app: innstillinger fra /data/options.json, MQTT fra Supervisor.
await builder.Configuration.AddHomeAssistantOptionsAsync(useMqttService: true);

builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.Configure<CameraOptions>(builder.Configuration.GetSection("Camera"));
builder.Services.Configure<StreamOptions>(builder.Configuration.GetSection("Stream"));
builder.Services.Configure<PipelineOptions>(builder.Configuration.GetSection("Pipeline"));
builder.Services.Configure<FaceMatchOptions>(builder.Configuration.GetSection("Face"));
builder.Services.Configure<LivenessOptions>(builder.Configuration.GetSection("Liveness"));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
builder.Services.AddSingleton<FaceMetrics>();
builder.Services.AddSingleton<ReferenceReloadSignal>();
builder.Services.AddHostedService<ReferenceReloadWorker>();

// Embeddings hentes fra databasetjenesten (kun lesing).
builder.Services.AddHttpClient<IEmbeddingSource, DatabaseEmbeddingSource>((sp, client) =>
{
    var database = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    client.BaseAddress = new Uri(database.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
    if (!string.IsNullOrEmpty(database.Token))
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", database.Token);
    }
});

// livetest --enroll bruker en minneindeks uten database.
if (args.Contains("livetest") && args.Contains("--enroll"))
{
    builder.Services.AddSingleton<IEmbeddingSource, EmptyEmbeddingSource>();
}

// Internt API for embedding-generering: kun aktivt med token, og lytter lokalt med mindre Api:Url sier noe annet.
var apiOptions = builder.Configuration.GetSection("Api").Get<InternalApiOptions>() ?? new InternalApiOptions();
builder.WebHost.UseUrls(apiOptions.Url);

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
        sp.GetRequiredService<IOptions<FaceMatchOptions>>().Value,
        sp.GetRequiredService<IEmbeddingSource>(),
        sp.GetRequiredService<FaceMetrics>(),
        sp.GetRequiredService<ILogger<FaceAiSharpRecognitionProvider>>()));
}
builder.Services.AddSingleton(sp => new MiniFasNetLivenessChecker(
    FaceAiSharpBundleFactory.CreateFaceDetectorWithLandmarks(),
    sp.GetRequiredService<IOptions<LivenessOptions>>()));
builder.Services.AddSingleton<ILivenessChecker>(sp => sp.GetRequiredService<MiniFasNetLivenessChecker>());
builder.Services.AddSingleton<ILivenessModel>(sp => sp.GetRequiredService<MiniFasNetLivenessChecker>());

// Videokilde og analyse: én RTSP-forbindelse og én buffer deles av gjenkjenning og liveness.
builder.Services.AddSingleton(sp => new FrameBuffer(sp.GetRequiredService<IOptions<StreamOptions>>().Value.BufferFrames));
builder.Services.AddSingleton<RtspFrameSource>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RtspFrameSource>());
builder.Services.AddSingleton<CameraModeMonitor>();
builder.Services.AddSingleton<ICameraModeProvider>(sp => sp.GetRequiredService<CameraModeMonitor>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CameraModeMonitor>());
builder.Services.AddSingleton<IFrameFaceDetector>(sp => new FaceAiSharpFrameDetector(
    FaceAiSharpBundleFactory.CreateFaceDetectorWithLandmarks(),
    sp.GetRequiredService<IOptions<PipelineOptions>>().Value.DetectionWidth));
builder.Services.AddSingleton<SequenceEvaluator>();
builder.Services.AddSingleton<MqttGateway>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttGateway>());
builder.Services.AddHostedService<RecognitionWorker>();

// IHttpClientFactory logger hver forespørsel på Information; vi vil kun se advarsler.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

var app = builder.Build();
if (args.Contains("selftest"))
{
    return await SelfTest.RunAsync(app.Services, args);
}

if (args.Contains("streamtest"))
{
    return await StreamTest.RunAsync(app.Services, args);
}

if (args.Contains("livetest"))
{
    return await LiveTest.RunAsync(app.Services, args);
}

if (args.Contains("enroll"))
{
    return await Enroll.RunAsync(app.Services, args);
}

if (!string.IsNullOrEmpty(apiOptions.Token))
{
    app.MapInternalApi(apiOptions.Token);
}

app.Run();
return 0;
