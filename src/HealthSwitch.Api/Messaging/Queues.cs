using System.Security.Authentication;
using MassTransit;

namespace HealthSwitch.Messaging;

public record RouteMessage(Guid EnvelopeId);
public record DeliverMessage(Guid EnvelopeId);

/// <summary>Queue names and their retry policies (scope document, section 10.2).</summary>
public static class Queues
{
    public const string Route = "q.route";
    public const string DeliverApi = "q.deliver.api";
    public const string DeliverPortal = "q.deliver.portal";
    public const string Dead = "q.dead";

    /// <summary>Retry intervals for API deliveries. Short for the demo; production would extend to hours.</summary>
    public static readonly TimeSpan[] ApiRetries = { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) };

    public static void Configure<T>(IBusRegistrationContext ctx, IBusFactoryConfigurator<T> cfg) where T : IReceiveEndpointConfigurator
    {
        cfg.ReceiveEndpoint(Route, e =>
        {
            e.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(1)));
            e.ConfigureConsumer<RouterConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(DeliverApi, e =>
        {
            e.UseMessageRetry(r => r.Intervals(ApiRetries));
            e.ConfigureConsumer<ApiAdapterConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(DeliverPortal, e =>
        {
            e.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(2)));
            e.ConfigureConsumer<PortalAdapterConsumer>(ctx);
        });
        cfg.ReceiveEndpoint(Dead, e => e.ConfigureConsumer<DeadLetterConsumer>(ctx));
    }

    /// <summary>Connects to RabbitMQ from an amqp:// or amqps:// URL (as CloudAMQP provides it).</summary>
    public static void RabbitHost(IRabbitMqBusFactoryConfigurator cfg, string url)
    {
        var uri = new Uri(url);
        var ssl = uri.Scheme.Equals("amqps", StringComparison.OrdinalIgnoreCase);
        var vhost = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (string.IsNullOrEmpty(vhost)) vhost = "/";
        var port = (ushort)(uri.Port > 0 ? uri.Port : ssl ? 5671 : 5672);
        var user = uri.UserInfo.Split(':', 2);
        cfg.Host(uri.Host, port, vhost, h =>
        {
            if (user.Length == 2)
            {
                h.Username(Uri.UnescapeDataString(user[0]));
                h.Password(Uri.UnescapeDataString(user[1]));
            }
            if (ssl) h.UseSsl(s => { s.Protocol = SslProtocols.Tls12; s.ServerName = uri.Host; });
        });
    }
}
