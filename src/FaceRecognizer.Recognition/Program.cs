using System.Threading.Channels;
using FaceAiSharp;
using FaceRecognizer.Recognition;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.Configure<CameraOptions>(builder.Configuration.GetSection("Camera"));
builder.Services.Configure<PipelineOptions>(builder.Configuration.GetSection("Pipeline"));
builder.Services.Configure<FaceMatchOptions>(builder.Configuration.GetSection("Face"));

// Kapasitet 1: triggere mens en behandling pågår slås sammen til én.
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

builder.Services.AddSingleton(sp => new FaceMatcher(
    FaceAiSharpBundleFactory.CreateFaceDetectorWithLandmarks(),
    FaceAiSharpBundleFactory.CreateFaceEmbeddingsGenerator(),
    sp.GetRequiredService<IOptions<FaceMatchOptions>>().Value));
builder.Services.AddSingleton<ILivenessChecker, UnconfiguredLivenessChecker>();
builder.Services.AddSingleton<MqttGateway>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttGateway>());
builder.Services.AddHostedService<RecognitionWorker>();

builder.Build().Run();
