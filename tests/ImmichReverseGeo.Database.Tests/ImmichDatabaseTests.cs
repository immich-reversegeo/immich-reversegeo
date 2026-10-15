using System.Text;
using DotNet.Testcontainers.Builders;
using FixtureChecks = ImmichReverseGeo.LiveImmichDb.Program;

[assembly: DoNotParallelize]

namespace ImmichReverseGeo.Database.Tests;

[TestClass]
[TestCategory("Integration")]
public sealed class ImmichDatabaseTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("v31", "3.1.0", "b434cb9287eea1471c9974845914d4dd328c9c2d652e446ed4930f99944f0ceb")]
    [DataRow("v32", "3.2.0", "ae13784ffcfcce8f4178113eb6661602a1fd1912f3d539880b8ac0dd95fc8ac2")]
    [DataRow("v331", "3.3.1", "db996e352359771c6a3db121a0ed8761516b22f727fc092a0f46dcfef82c1bc1")]
    public async Task RepositoryContractsAgainstRealImmich(string name, string version, string digest)
    {
        var guard = $"reversegeo-test-{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(TestContext.TestResultsDirectory!, guard);
        Directory.CreateDirectory(directory);

        await using var network = new NetworkBuilder().Build();
        await using var databaseVolume = new VolumeBuilder().Build();
        await using var libraryVolume = new VolumeBuilder().Build();
        await using var database = new ContainerBuilder(
                "ghcr.io/immich-app/postgres:14-vectorchord0.4.3-pgvectors0.2.0@sha256:bcf63357191b76a916ae5eb93464d65c07511da41e3bf7a8416db519b40b1c23")
            .WithNetwork(network).WithNetworkAliases("database")
            .WithEnvironment("POSTGRES_PASSWORD", password)
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("POSTGRES_DB", "immich")
            .WithEnvironment("POSTGRES_INITDB_ARGS", "--data-checksums")
            .WithVolumeMount(databaseVolume, "/var/lib/postgresql/data")
            .WithCreateParameterModifier(parameters => parameters.HostConfig!.ShmSize = 128 * 1024 * 1024)
            .WithResourceMapping(Encoding.UTF8.GetBytes($"""
                CREATE SCHEMA immich_reversegeo_fixture;
                CREATE TABLE immich_reversegeo_fixture.guard (name text PRIMARY KEY);
                INSERT INTO immich_reversegeo_fixture.guard VALUES ('{guard}');
                """), "/docker-entrypoint-initdb.d/99-reversegeo-fixture.sql")
            .WithPortBinding(5432, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilExternalTcpPortIsAvailable(5432, options => options.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();
        await using var redis = new ContainerBuilder(
                "docker.io/valkey/valkey:9@sha256:70739f85ad2ee01a726a965584a0f94895f01b0c60b3cc8b0aeef11eaa6888cf")
            .WithNetwork(network).WithNetworkAliases("redis")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilInternalTcpPortIsAvailable(6379, options => options.WithTimeout(TimeSpan.FromMinutes(2))))
            .Build();
        await using var server = new ContainerBuilder($"ghcr.io/immich-app/immich-server:v{version}@sha256:{digest}")
            .WithNetwork(network)
            .WithEnvironment("DB_HOSTNAME", "database")
            .WithEnvironment("DB_USERNAME", "postgres")
            .WithEnvironment("DB_PASSWORD", password)
            .WithEnvironment("DB_DATABASE_NAME", "immich")
            .WithEnvironment("REDIS_HOSTNAME", "redis")
            .WithEnvironment("IMMICH_CONFIG_FILE", "/config/immich.json")
            .WithResourceMapping(Encoding.UTF8.GetBytes(
                "{\"machineLearning\":{\"enabled\":false},\"reverseGeocoding\":{\"enabled\":false}}"), "/config/immich.json")
            .WithVolumeMount(libraryVolume, "/data")
            .WithPortBinding(2283, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPort(2283).ForPath("/api/server/version"), options => options.WithTimeout(TimeSpan.FromMinutes(3))))
            .Build();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            await network.CreateAsync(timeout.Token);
            await databaseVolume.CreateAsync(timeout.Token);
            await libraryVolume.CreateAsync(timeout.Token);
            await Task.WhenAll(database.StartAsync(timeout.Token), redis.StartAsync(timeout.Token));
            await server.StartAsync(timeout.Token);
            var target = new FixtureChecks.Target(name, version, server.GetMappedPublicPort(2283),
                database.GetMappedPublicPort(5432), server.Hostname, guard);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                FixtureChecks.RunAsync(target with { Version = "0.0.0" }, directory, password));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                FixtureChecks.RunAsync(target with { GuardName = "wrong-marker" }, directory, password));
            Assert.IsFalse(File.Exists(Path.Combine(directory, $"{name}-account.json")),
                "Rejected targets must not create a fixture account.");
            await FixtureChecks.RunAsync(target, directory, password);
            TestContext.AddResultFile(Path.Combine(directory, $"{name}-results.json"));
        }
        catch
        {
            // Attach service logs on failure; never attach account files or database credentials.
            foreach (var (service, container) in new[] { ("database", database), ("redis", redis), ("server", server) })
            {
                try
                {
                    var (stdout, stderr) = await container.GetLogsAsync();
                    var log = Path.Combine(directory, $"{name}-{service}.log");
                    await File.WriteAllTextAsync(log, (stdout + stderr).Replace(password, "[REDACTED]"));
                    TestContext.AddResultFile(log);
                }
                catch (Exception error)
                {
                    TestContext.WriteLine($"Could not collect {service} logs: {error.GetType().Name}");
                }
            }
            throw;
        }
        finally
        {
            var account = Path.Combine(directory, $"{name}-account.json");
            if (File.Exists(account))
            {
                File.Delete(account);
            }
        }
    }
}
