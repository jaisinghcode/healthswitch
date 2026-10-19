using System.Security.Cryptography;
using System.Text;
using HealthSwitch.Data;
using HealthSwitch.Endpoints;
using HealthSwitch.Gateway;
using HealthSwitch.Messaging;
using HealthSwitch.Sim;
using MassTransit;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Render and most hosts pass the port in PORT.
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Configuration["SelfBaseUrl"] = $"http://127.0.0.1:{port}";

builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(ConnectionStrings.Postgres(builder.Configuration)));
builder.Services.AddScoped<GatewayService>();
builder.Services.AddScoped<DeliveryEffects>();
builder.Services.AddSingleton<PayerSimulator>();
builder.Services.AddHttpClient("payer", c => c.Timeout = TimeSpan.FromSeconds(10));

var rabbitUrl = builder.Configuration["RABBITMQ_URL"];
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<RouterConsumer>();
    x.AddConsumer<ApiAdapterConsumer>();
    x.AddConsumer<PortalAdapterConsumer>();
    x.AddConsumer<DeadLetterConsumer>();
    if (!string.IsNullOrWhiteSpace(rabbitUrl))
        x.UsingRabbitMq((ctx, cfg) => { Queues.RabbitHost(cfg, rabbitUrl); Queues.Configure(ctx, cfg); });
    else
        x.UsingInMemory((ctx, cfg) => Queues.Configure(ctx, cfg)); // no broker configured: same queues, in process
});

var app = builder.Build();

await Seeder.InitAsync(app.Services, app.Logger);
app.Logger.LogInformation("Message transport: {Transport}", string.IsNullOrWhiteSpace(rabbitUrl) ? "in-memory" : "RabbitMQ");

app.UseDefaultFiles();
app.UseStaticFiles();

// Optional shared demo key, so a public free URL is not open to everyone.
app.Use(async (ctx, next) =>
{
    var key = app.Configuration["DEMO_ACCESS_KEY"];
    var p = ctx.Request.Path;
    var guarded = (p.StartsWithSegments("/api") || p.StartsWithSegments("/v1") || p.StartsWithSegments("/sim"))
                  && !p.StartsWithSegments("/api/meta") && !p.StartsWithSegments("/v1/webhooks");
    if (!string.IsNullOrEmpty(key) && guarded)
    {
        var given = ctx.Request.Headers["X-Demo-Key"].ToString();
        if (string.IsNullOrEmpty(given)) given = ctx.Request.Query["key"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key)))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = new { code = "ERR_ACCESS_DENIED", message = "Enter the demo access key." } });
            return;
        }
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/meta", (IConfiguration config) => Results.Ok(new
{
    keyRequired = !string.IsNullOrEmpty(config["DEMO_ACCESS_KEY"]),
    transport = string.IsNullOrWhiteSpace(config["RABBITMQ_URL"]) ? "in-memory" : "RabbitMQ",
    version = typeof(Program).Assembly.GetName().Version?.ToString(),
}));
ProtocolApi.Map(app);
PortalApi.Map(app);
SimEndpoints.Map(app);

app.Run();

public partial class Program { }
