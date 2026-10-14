using System;
using System.IO;

namespace ImmichReverseGeo.Core.Models;

/// <summary>Cheap file metadata used to invalidate remembered country-cache readiness.</summary>
public readonly record struct CacheFileStamp(long Length, long CreatedTicks, long ModifiedTicks)
{
    public static bool TryRead(string path, out CacheFileStamp stamp)
    {
        stamp = default;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return false;
            }
            stamp = new CacheFileStamp(file.Length, file.CreationTimeUtc.Ticks, file.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
