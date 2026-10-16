using System;
using System.Collections.Generic;
using System.Text.Json;
using ImmichReverseGeo.Overture.Models;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Overture.Services;

/// <summary>Reads only the indexed division names referenced by this point's candidates.</summary>
internal static class OvertureCachedNames
{
    public static Dictionary<string, (string? NamesJson, string? Country)> ReadReferenced(
        SqliteConnection connection, IEnumerable<OvertureDivisionCandidateDiagnostic> candidates)
    {
        var result = new Dictionary<string, (string? NamesJson, string? Country)>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='division_name'";
        if (Convert.ToInt64(command.ExecuteScalar()) == 0)
        {
            return result; // Older/custom caches retain their embedded-name compatibility path.
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.HierarchyJson))
            {
                continue;
            }
            try
            {
                using var hierarchy = JsonDocument.Parse(candidate.HierarchyJson);
                if (hierarchy.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var entry in hierarchy.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("division_id", out var id)
                        && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } value)
                    {
                        ids.Add(value);
                    }
                }
            }
            catch (JsonException) { }
        }
        command.CommandText = "SELECT names_json, country FROM division_name WHERE id=$id";
        var parameter = command.Parameters.Add("$id", SqliteType.Text);
        command.Prepare();
        foreach (var id in ids)
        {
            parameter.Value = id;
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                result[id] = (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
            }
        }
        return result;
    }
}
