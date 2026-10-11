using System;
using System.IO;
using System.Threading;
using DuckDB.NET.Data;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Overture.Services;

/// <summary>Owns both native resources and the shared heavy-work slot.</summary>
public sealed class DuckDbWorkScope : IDisposable
{
    private readonly MemoryResourcePolicy.WorkLease _lease;
    private readonly string? _tempDirectory;
    public DuckDBConnection Connection { get; }

    public DuckDbWorkScope(MemoryResourcePolicy policy, string dataDir, CancellationToken ct = default)
    {
        _lease = policy.Enter(ct);
        Connection = new DuckDBConnection("Data Source=:memory:");
        try
        {
            Connection.Open();
            if (_lease.IsLowMemoryMode)
            {
                _tempDirectory = Path.GetFullPath(Path.Combine(dataDir, "duckdb-tmp", Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(_tempDirectory);
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = $"SET memory_limit='256MiB'; SET threads=1; SET temp_directory='{_tempDirectory.Replace("'", "''")}';";
                cmd.ExecuteNonQuery();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            Connection.Dispose();
        }
        finally
        {
            try
            {
                // DuckDB removes its spill files on close. Delete only the empty,
                // uniquely owned directory; never recursively delete spill roots.
                if (_tempDirectory is not null && Directory.Exists(_tempDirectory))
                {
                    Directory.Delete(_tempDirectory);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                _lease.Dispose();
            }
        }
    }
}
