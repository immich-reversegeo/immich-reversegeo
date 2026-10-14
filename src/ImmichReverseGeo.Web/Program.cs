using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ── Services ────────────────────────────────────────────────────────────────

builder.Services.AddScoped<PreviewCircuitLifetime>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler>(
    sp => sp.GetRequiredService<PreviewCircuitLifetime>());
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient();
builder.Services.AddSingleton(ImmichPhotoOptions.FromEnvironment());
builder.Services.AddHttpClient<ImmichPhotoService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        MaxConnectionsPerServer = 6
    });
builder.Services.AddSingleton<MemoryResourcePolicy>();

var bundledDataDir = builder.Environment.IsDevelopment()
    ? Path.Combine(Directory.GetCurrentDirectory(), "bundled-data")
    : "/app/bundled-data";

var dataDir = Environment.GetEnvironmentVariable("DATA_DIR")
    ?? (builder.Environment.IsDevelopment()
        ? Path.Combine(Directory.GetCurrentDirectory(), "localdata")
        : "/data");

var configDir = Environment.GetEnvironmentVariable("CONFIG_DIR")
    ?? (builder.Environment.IsDevelopment()
        ? Path.Combine(Directory.GetCurrentDirectory(), "localdata")
        : "/config");

var dataProtectionDir = Path.Combine(configDir, "dataprotection-keys");
Directory.CreateDirectory(dataProtectionDir);

// StorageOptions is the single source of truth for the two data roots.
// Every service that needs file paths injects this instead of raw strings.
builder.Services.AddSingleton(new StorageOptions(dataDir, bundledDataDir));

builder.Services.AddDataProtection()
    .SetApplicationName("ImmichReverseGeo")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDir));

builder.Services.AddSingleton(sp =>
    new ConfigService(
        sp.GetRequiredService<ILogger<ConfigService>>(),
        configDir,
        sp.GetRequiredService<MemoryResourcePolicy>()));
builder.Services.AddSingleton<CountryCodeService>();
builder.Services.AddSingleton(sp =>
{
    var db = sp.GetRequiredService<ConfigService>().GetDbSettings();
    var connectionString = $"Host={db.Host};Port={db.Port};Username={db.Username};Password={db.Password};Database={db.Database};GSS Encryption Mode=Disable";
    var builder = new NpgsqlDataSourceBuilder(connectionString);
    return builder.Build();
});
builder.Services.AddSingleton<CityResolverProfileCatalogService>();
builder.Services.AddSingleton<AdministrativeAreaResolverService>();
builder.Services.AddSingleton<AssetLocationResolverService>();
builder.Services.AddSingleton(sp =>
    new OvertureDivisionCacheService(
        sp.GetRequiredService<ILogger<OvertureDivisionCacheService>>(),
        sp.GetRequiredService<StorageOptions>(),
        sp.GetRequiredService<CountryCodeService>().Iso3ToAlpha2,
        sp.GetRequiredService<MemoryResourcePolicy>()));
builder.Services.AddSingleton(sp =>
    new OverturePlacesService(
        sp.GetRequiredService<ILogger<OverturePlacesService>>(),
        sp.GetRequiredService<StorageOptions>().DataDir,
        sp.GetRequiredService<StorageOptions>().BundledDataDir,
        sp.GetRequiredService<MemoryResourcePolicy>()));
builder.Services.AddSingleton(sp =>
    new OvertureDivisionsService(
        sp.GetRequiredService<ILogger<OvertureDivisionsService>>(),
        sp.GetRequiredService<OverturePlacesService>(),
        sp.GetRequiredService<StorageOptions>().DataDir,
        sp.GetRequiredService<StorageOptions>().BundledDataDir,
        alpha2 => sp.GetRequiredService<CountryCodeService>().Alpha2ToIso3(alpha2),
        sp.GetRequiredService<MemoryResourcePolicy>()));
builder.Services.AddSingleton(sp =>
    new GadmDivisionCacheService(
        sp.GetRequiredService<ILogger<GadmDivisionCacheService>>(),
        sp.GetRequiredService<StorageOptions>(),
        sp.GetRequiredService<MemoryResourcePolicy>()));
builder.Services.AddSingleton(sp =>
    new GadmDivisionsService(
        sp.GetRequiredService<ILogger<GadmDivisionsService>>(),
        sp.GetRequiredService<StorageOptions>().DataDir));
builder.Services.AddSingleton<SkippedAssetsRepository>();
builder.Services.AddSingleton<SavedLocationPreviewRepository>();
builder.Services.AddSingleton<ImmichDbRepository>();
builder.Services.AddSingleton<ILocationChangeWriter>(sp => sp.GetRequiredService<ImmichDbRepository>());
builder.Services.AddSingleton<LocationChangeRepository>();
builder.Services.AddSingleton<LocationManagementService>();
builder.Services.AddSingleton<LocationReviewResults>();
builder.Services.AddSingleton<ProcessingState>();
builder.Services.AddScoped<LocationPreviewSession>();
builder.Services.AddSingleton<ProcessingBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessingBackgroundService>());

// ── App ─────────────────────────────────────────────────────────────────────

var app = builder.Build();
try
{
    var startupConfig = await app.Services.GetRequiredService<ConfigService>().GetConfigAsync();
    app.Services.GetRequiredService<MemoryResourcePolicy>().LowMemoryMode = startupConfig.Processing.LowMemoryMode;
    app.Services.GetRequiredService<MemoryResourcePolicy>().DuckDbThreads = startupConfig.Processing.DuckDbThreads;
    app.Services.GetRequiredService<MemoryResourcePolicy>().CountryIndexIdleMinutes = startupConfig.Processing.CountryIndexIdleMinutes;
    app.Services.GetRequiredService<MemoryResourcePolicy>().UseBoundedCountryCache = startupConfig.Processing.UseBoundedCountryCache;
    app.Services.GetRequiredService<MemoryResourcePolicy>().CountryCacheMiB = startupConfig.Processing.CountryCacheMiB;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
{
    app.Logger.LogError(ex, "Could not load memory settings at startup. Settings remain available for recovery.");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapProcessingEndpoints(
    () => app.Services.GetRequiredService<ProcessingBackgroundService>().TryTriggerRun());
app.MapGet("/api/location-previews/{previewId:guid}/assets/{assetId:guid}/thumbnail",
    async Task<IResult> (Guid previewId, Guid assetId, ImmichPhotoService photos, HttpContext context) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var thumbnail = await photos.GetThumbnailAsync(previewId, assetId, context.RequestAborted);
        return thumbnail is null ? Results.NotFound() : Results.File(thumbnail.Bytes, thumbnail.ContentType);
    });
app.MapRazorComponents<ImmichReverseGeo.Web.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
