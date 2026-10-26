using FaceRecognizer.Contracts;
using FaceRecognizer.Admin;
using FaceRecognizer.Admin.Api;
using FaceRecognizer.Admin.Clients;

var builder = WebApplication.CreateBuilder(args);
await builder.Configuration.AddHomeAssistantOptionsAsync(useMqttService: false);

var auth = AdminAuthOptions.From(builder.Configuration, File.Exists(HomeAssistantAddon.OptionsPath));
builder.Services.AddSingleton(auth);
builder.Services.AddHttpClient<IDatabaseClient, DatabaseClient>(client => Configure(client, "Database", 30));
builder.Services.AddHttpClient<IRecognitionClient, RecognitionClient>(client => Configure(client, "Recognition", 60));
builder.Services.AddScoped<ReferenceService>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; frame-ancestors 'self'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapAdminApi(auth);
app.Run();

void Configure(HttpClient client, string section, int timeoutSeconds)
{
    var baseUrl = builder.Configuration[$"{section}:BaseUrl"]
        ?? throw new InvalidOperationException($"{section}:BaseUrl må settes.");
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    var serviceToken = builder.Configuration[$"{section}:Token"];
    if (!string.IsNullOrEmpty(serviceToken))
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", serviceToken);
    }
}
