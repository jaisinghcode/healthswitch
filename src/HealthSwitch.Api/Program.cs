using System.Security.Cryptography;
using System.Text;
using HealthSwitch.Auth;
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

// Each organisation signs in to its own portal. The signed token says which organisation and role;
// the endpoints only ever show or change that organisation's own data.
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path;
    if (p.StartsWithSegments("/api") && !Tokens.IsPublic(p))
    {
        var session = Tokens.Read(app.Configuration, Tokens.FromRequest(ctx.Request));
        if (session is null)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = new { code = "ERR_LOGIN_REQUIRED", message = "Please sign in." } });
            return;
        }
        ctx.Items[Tokens.ItemKey] = session;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
// One page per organisation: /palmgrove, /riverside, /northstar, /sapphire, /switch, /nhia.
// Literal routes, so they never shadow the static files (a "/{slug}" route would).
foreach (var portal in Portals.All)
    app.MapGet("/" + portal.Slug, (IWebHostEnvironment env) => Results.File(Path.Combine(env.WebRootPath, "portal.html"), "text/html; charset=utf-8"));
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
