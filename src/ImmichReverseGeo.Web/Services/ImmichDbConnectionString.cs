using ImmichReverseGeo.Core.Models;
using Npgsql;

namespace ImmichReverseGeo.Web.Services;

public static class ImmichDbConnectionString
{
    public static string Build(DbSettings settings) => new NpgsqlConnectionStringBuilder
    {
        Host = settings.Host,
        Port = settings.Port,
        Username = settings.Username,
        Password = settings.Password,
        Database = settings.Database,
        GssEncryptionMode = GssEncryptionMode.Disable
    }.ConnectionString;
}
