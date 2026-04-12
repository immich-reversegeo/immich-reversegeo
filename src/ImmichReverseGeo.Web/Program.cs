using System;
using System.IO;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ── Services ────────────────────────────────────────────────────────────────

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient();

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
        configDir));
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
builder.Services.AddSingleton(sp =>
    new OvertureDivisionCacheService(
        sp.GetRequiredService<ILogger<OvertureDivisionCacheService>>(),
        sp.GetRequiredService<StorageOptions>(),
        sp.GetRequiredService<CountryCodeService>().Iso3ToAlpha2));
builder.Services.AddSingleton(sp =>
    new OverturePlacesService(
        sp.GetRequiredService<ILogger<OverturePlacesService>>(),
        sp.GetRequiredService<StorageOptions>().DataDir,
        sp.GetRequiredService<StorageOptions>().BundledDataDir));
builder.Services.AddSingleton(sp =>
    new OvertureDivisionsService(
        sp.GetRequiredService<ILogger<OvertureDivisionsService>>(),
        sp.GetRequiredService<OverturePlacesService>(),
        sp.GetRequiredService<StorageOptions>().DataDir,
        sp.GetRequiredService<StorageOptions>().BundledDataDir,
        alpha2 => sp.GetRequiredService<CountryCodeService>().Alpha2ToIso3(alpha2)));
builder.Services.AddSingleton(sp =>
    new GadmDivisionCacheService(
        sp.GetRequiredService<ILogger<GadmDivisionCacheService>>(),
        sp.GetRequiredService<StorageOptions>()));
builder.Services.AddSingleton(sp =>
    new GadmDivisionsService(
        sp.GetRequiredService<ILogger<GadmDivisionsService>>(),
        sp.GetRequiredService<StorageOptions>().DataDir));
builder.Services.AddSingleton<SkippedAssetsRepository>();
builder.Services.AddSingleton<ImmichDbRepository>();
builder.Services.AddSingleton<ProcessingState>();
builder.Services.AddSingleton<ProcessingBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessingBackgroundService>());

// ── App ─────────────────────────────────────────────────────────────────────

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<ImmichReverseGeo.Web.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
