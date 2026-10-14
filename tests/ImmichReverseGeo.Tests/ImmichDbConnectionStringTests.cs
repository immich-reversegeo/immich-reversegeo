using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using Npgsql;

namespace ImmichReverseGeo.Tests;

[TestClass]
public sealed class ImmichDbConnectionStringTests
{
    [TestMethod]
    [DataRow("a;b")]
    [DataRow("a\"b'c")]
    [DataRow("  spaced password  ")]
    [DataRow("value;Host=other;Database=other")]
    public void ConnectionString_PreservesCredentialsWithoutChangingConnectionOptions(string password)
    {
        var settings = new DbSettings("database", 5433, "user;name", password, "db;name");
        var parsed = new NpgsqlConnectionStringBuilder(ImmichDbConnectionString.Build(settings));
        Assert.AreEqual(settings.Host, parsed.Host);
        Assert.AreEqual(settings.Port, parsed.Port);
        Assert.AreEqual(settings.Username, parsed.Username);
        Assert.AreEqual(settings.Password, parsed.Password);
        Assert.AreEqual(settings.Database, parsed.Database);
        Assert.AreEqual(GssEncryptionMode.Disable, parsed.GssEncryptionMode);
    }
}
