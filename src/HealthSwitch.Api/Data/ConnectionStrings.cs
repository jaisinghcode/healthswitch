using Npgsql;

namespace HealthSwitch.Data;

public static class ConnectionStrings
{
    /// <summary>
    /// Reads DATABASE_URL (postgres://user:pass@host/db?sslmode=require, as Neon and Render give it)
    /// or ConnectionStrings:Postgres, and falls back to a local database for development.
    /// </summary>
    public static string Postgres(IConfiguration config)
    {
        var url = config["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(url)) return FromUrl(url);
        return config.GetConnectionString("Postgres")
               ?? "Host=localhost;Port=5432;Database=healthswitch;Username=postgres;Password=postgres";
    }

    public static string FromUrl(string url)
    {
        var uri = new Uri(url);
        if (uri.Scheme is not ("postgres" or "postgresql")) return url; // already a key=value string
        var user = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(user[0]),
            Password = user.Length > 1 ? Uri.UnescapeDataString(user[1]) : null,
        };
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var ssl = query["sslmode"];
        var local = uri.Host is "localhost" or "127.0.0.1" or "postgres" or "db";
        b.SslMode = ssl?.ToLowerInvariant() switch
        {
            "disable" => SslMode.Disable,
            "verify-full" => SslMode.VerifyFull,
            "verify-ca" => SslMode.VerifyCA,
            "require" => SslMode.Require,
            _ => local ? SslMode.Prefer : SslMode.Require,
        };
        return b.ConnectionString;
    }
}
