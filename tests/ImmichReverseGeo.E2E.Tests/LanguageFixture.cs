using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DuckDB.NET.Data;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.IO;
using Npgsql;

namespace ImmichReverseGeo.E2E.Tests;

internal sealed class LanguageFixture : IAsyncDisposable
{
    public static readonly Guid ExistingAsset = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid MissingAsset = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public string Root { get; } = FindRoot();
    public string DirectoryPath { get; }
    public string DataPath => Path.Combine(DirectoryPath, "data");
    public NpgsqlDataSource Database { get; private set; } = null!;
    public LanguageFactory Factory { get; private set; } = null!;
    private readonly Dictionary<string, string?> _environment = new();
    private IContainer? _container;

    public LanguageFixture()
    {
        DirectoryPath = Path.Combine(Root, "_out", "language-e2e", Guid.NewGuid().ToString("N"));
    }

    public async Task StartAsync()
    {
        Directory.CreateDirectory(DataPath);
        var configPath = Path.Combine(DirectoryPath, "config");
        Directory.CreateDirectory(configPath);
        SetEnvironment("DATA_DIR", DataPath);
        SetEnvironment("CONFIG_DIR", configPath);
        // The production entry point reads these before service overrides are applied.
        // Never inherit credentials or connect to an existing Immich instance.
        SetEnvironment("DB_HOST", "127.0.0.1");
        SetEnvironment("DB_HOSTNAME", "127.0.0.1");
        SetEnvironment("DB_PORT", "1");
        SetEnvironment("DB_USERNAME", "language_fixture");
        SetEnvironment("DB_PASSWORD", "unused");
        SetEnvironment("DB_DATABASE_NAME", "language_fixture");
        await File.WriteAllTextAsync(Path.Combine(configPath, "settings.json"), JsonSerializer.Serialize(
            new AppConfig { Schedule = new() { Enabled = false }, Processing = new() { UseAirportInfrastructure = false } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        SeedGeodata();
        var password = Guid.NewGuid().ToString("N");
        _container = new ContainerBuilder(
                "ghcr.io/immich-app/postgres:14-vectorchord0.4.3-pgvectors0.2.0@sha256:bcf63357191b76a916ae5eb93464d65c07511da41e3bf7a8416db519b40b1c23")
            .WithEnvironment("POSTGRES_PASSWORD", password)
            .WithEnvironment("POSTGRES_DB", "language_fixture")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                "pg_isready", "-h", "127.0.0.1", "-U", "postgres", "-d", "language_fixture"))
            .Build();
        await _container.StartAsync();
        Database = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
        {
            Host = _container.Hostname, Port = _container.GetMappedPublicPort(5432),
            Username = "postgres", Password = password, Database = "language_fixture", GssEncryptionMode = GssEncryptionMode.Disable
        }.ConnectionString);
        await using var seed = Database.CreateCommand("""
            CREATE TABLE asset (id uuid PRIMARY KEY, "createdAt" timestamptz NOT NULL, "deletedAt" timestamptz);
            CREATE TABLE asset_exif ("assetId" uuid PRIMARY KEY REFERENCES asset(id), latitude double precision,
                longitude double precision, city text, state text, country text);
            INSERT INTO asset VALUES
                ('00000000-0000-0000-0000-000000000001', now(), NULL),
                ('00000000-0000-0000-0000-000000000002', now(), NULL);
            INSERT INTO asset_exif VALUES
                ('00000000-0000-0000-0000-000000000001',1,1,'Munich','Bavaria','Germany'),
                ('00000000-0000-0000-0000-000000000002',1,1,NULL,NULL,NULL);
            """);
        await seed.ExecuteNonQueryAsync();
        Factory = new LanguageFactory(this, configPath);
        Factory.UseKestrel(0);
        using var client = Factory.CreateClient();
        BaseUrl = client.BaseAddress!.ToString().TrimEnd('/');
    }

    public string BaseUrl { get; private set; } = null!;

    public async Task<GeoResult> ReadLocationAsync(Guid id)
    {
        await using var command = Database.CreateCommand("SELECT country,state,city FROM asset_exif WHERE \"assetId\"=$1");
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        return new(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Factory is not null)
            {
                await Factory.DisposeAsync();
            }
            if (Database is not null)
            {
                await Database.DisposeAsync();
            }
            if (_container is not null)
            {
                await _container.DisposeAsync();
            }
        }
        finally
        {
            foreach (var (key, value) in _environment)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private void SetEnvironment(string key, string value)
    {
        _environment[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    private void SeedGeodata()
    {
        var bundled = Path.Combine(DirectoryPath, "bundled", "defaults");
        Directory.CreateDirectory(bundled);
        File.Copy(Path.Combine(Root, "src", "ImmichReverseGeo.Web", "bundled-data", "iso3166.json"),
            Path.Combine(DirectoryPath, "bundled", "iso3166.json"));
        var geometry = new WKBWriter().Write(new WKTReader().Read("POLYGON ((0 0,2 0,2 2,0 2,0 0))"));
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(bundled, "overture-country-divisions.db")};Pooling=false"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE division_area (id TEXT, name TEXT, subtype TEXT, class_name TEXT, admin_level INTEGER,
                    country TEXT, alpha3 TEXT, is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                    bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
                INSERT INTO division_area VALUES ('country-area','Germany','country','land',0,'DE','DEU',1,1,$geometry,0,0,2,2);
                """;
            command.Parameters.AddWithValue("$geometry", geometry);
            command.ExecuteNonQuery();
        }
        var areaDir = Path.Combine(DirectoryPath, "source", "type=division_area");
        var divisionDir = Path.Combine(DirectoryPath, "source", "type=division");
        Directory.CreateDirectory(areaDir);
        Directory.CreateDirectory(divisionDir);
        using var duck = new DuckDBConnection("Data Source=:memory:");
        duck.Open();
        using var query = duck.CreateCommand();
        query.CommandText = """
            CREATE TABLE divisions AS SELECT 'country' AS id, 'country' AS subtype, 0 AS admin_level, 'DE' AS country,
                {'primary':'Deutschland','common':MAP {'en':'Germany','fr':'Allemagne'}} AS names,
                [[{'division_id':'country','subtype':'country','name':'Deutschland'}]] AS hierarchies;
            INSERT INTO divisions VALUES ('state','region',1,'DE',
                {'primary':'Bayern','common':MAP {'en':'Bavaria','fr':'Bavière'}},
                [[{'division_id':'country','subtype':'country','name':'Deutschland'},
                  {'division_id':'state','subtype':'region','name':'Bayern'}]]);
            INSERT INTO divisions VALUES ('city','locality',2,'DE',
                {'primary':'München','common':MAP {'en':'Munich','fr':'Munich français'}},
                [[{'division_id':'country','subtype':'country','name':'Deutschland'},
                  {'division_id':'state','subtype':'region','name':'Bayern'},
                  {'division_id':'city','subtype':'locality','name':'München'}]]);
            CREATE TABLE areas AS SELECT id || '-area' AS id, id AS division_id, subtype, admin_level, country, names,
                'land' AS class, true AS is_land, true AS is_territorial,
                $geometry AS geometry, {'xmin':0.0::DOUBLE,'ymin':0.0::DOUBLE,'xmax':2.0::DOUBLE,'ymax':2.0::DOUBLE} AS bbox FROM divisions;
            """;
        query.Parameters.Add(new DuckDBParameter("geometry", geometry));
        query.ExecuteNonQuery();
        query.Parameters.Clear();
        query.CommandText = $"COPY divisions TO '{divisionDir.Replace('\\', '/')}/rows.parquet' (FORMAT PARQUET); COPY areas TO '{areaDir.Replace('\\', '/')}/rows.parquet' (FORMAT PARQUET)";
        query.ExecuteNonQuery();
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "immich-reversegeo.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Cannot find repository root.");
    }
}

internal sealed class LanguageFactory(LanguageFixture fixture, string configPath) : WebApplicationFactory<ConfigService>
{
    public FixtureCache Cache { get; } = new(fixture.DirectoryPath, fixture.DataPath);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseContentRoot(Path.Combine(fixture.Root, "src", "ImmichReverseGeo.Web"));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<StorageOptions>();
            services.AddSingleton(new StorageOptions(fixture.DataPath, Path.Combine(fixture.DirectoryPath, "bundled")));
            services.RemoveAll<ConfigService>();
            services.AddSingleton(new ConfigService(NullLogger<ConfigService>.Instance, configPath));
            services.RemoveAll<NpgsqlDataSource>();
            services.AddSingleton(fixture.Database);
            services.RemoveAll<OvertureDivisionCacheService>();
            services.AddSingleton<OvertureDivisionCacheService>(Cache);
        });
    }
}

internal sealed class FixtureCache(string root, string data) : OvertureDivisionCacheService(
    NullLogger<OvertureDivisionCacheService>.Instance, data, _ => "DE")
{
    public int Exports { get; private set; }
    public bool FailExport { get; set; }
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    protected override DateTimeOffset UtcNow => Now;
    protected override string GetLatestOvertureReleaseForCache(CancellationToken ct) => "2026-10-15.0";
    protected override string GetDivisionAreaReleaseUrl(string release) =>
        Path.Combine(root, "source", "type=division_area", "*.parquet").Replace('\\', '/');
    protected override void PrepareExportConnection(DuckDBConnection connection)
    {
        // Fixtures already contain valid WKB; retain the production export query/SQLite writer.
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE MACRO ST_AsWKB(geometry) AS geometry";
        command.ExecuteNonQuery();
    }
    protected override long ExportOvertureDivisions(string path, string alpha2, string iso3, string release, CancellationToken ct)
    {
        Exports++;
        if (FailExport)
        {
            throw new IOException("Simulated geodata download failure");
        }
        return base.ExportOvertureDivisions(path, alpha2, iso3, release, ct);
    }
}
