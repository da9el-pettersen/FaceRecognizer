using FaceRecognizer.Contracts;
using FaceRecognizer.Database;
using FaceRecognizer.Database.Api;
using FaceRecognizer.Database.Messaging;
using FaceRecognizer.Database.Storage;

var builder = WebApplication.CreateBuilder(args);
await builder.Configuration.AddHomeAssistantOptionsAsync(useMqttService: true);

var token = builder.Configuration["Database:Token"];
if (string.IsNullOrWhiteSpace(token))
{
    throw new InvalidOperationException("Database:Token må settes.");
}

var databasePath = builder.Configuration["Database:Path"] ?? "/data/faces.db";
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.AddSingleton(new EmbeddingStore(databasePath));
builder.Services.AddSingleton<IChangeNotifier, MqttChangeNotifier>();

var app = builder.Build();

var legacyFolder = builder.Configuration["Database:LegacyImportFolder"];
if (!string.IsNullOrWhiteSpace(legacyFolder) && Directory.Exists(legacyFolder))
{
    await LegacyImport.RunAsync(
        app.Services.GetRequiredService<EmbeddingStore>(), legacyFolder,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LegacyImport"), CancellationToken.None);
}

app.MapDatabaseApi(token);
app.Run();
