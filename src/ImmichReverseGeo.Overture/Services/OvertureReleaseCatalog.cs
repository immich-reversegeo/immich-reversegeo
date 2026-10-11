using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.IO;
using ImmichReverseGeo.Core.Models;
using DuckDB.NET.Data;

namespace ImmichReverseGeo.Overture.Services;

public static class OvertureReleaseCatalog
{
    public const string CatalogUrl = "https://stac.overturemaps.org/catalog.json";

    public static string GetLatestRelease(MemoryResourcePolicy? memoryPolicy = null, string? dataDir = null, CancellationToken ct = default)
    {
        try
        {
            using var work = new DuckDbWorkScope(memoryPolicy ?? new MemoryResourcePolicy(), dataDir ?? Path.GetTempPath(), ct);
            var conn = work.Connection;
            OvertureDataAccess.LoadHttpfs(conn);
            using var query = conn.CreateCommand();
            query.CommandText = $"SELECT latest FROM '{CatalogUrl}'";
            return ValidateLatestRelease(query.ExecuteScalar()?.ToString(), DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Unable to discover an available Overture release. Check access to stac.overturemaps.org and try again. Existing local caches can still be used.", ex);
        }
    }

    public static string ValidateLatestRelease(string? release, DateTime utcNow)
    {
        if (release is null
            || !Regex.IsMatch(release, @"\A\d{4}-\d{2}-\d{2}\.\d+\z", RegexOptions.CultureInvariant)
            || !DateTime.TryParseExact(release[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)
            || date > utcNow.Date
            || (utcNow.Date - date).TotalDays >= 60)
        {
            throw new InvalidOperationException("Overture's catalog did not provide a valid release within its 60-day data retention window.");
        }

        return release;
    }
}
