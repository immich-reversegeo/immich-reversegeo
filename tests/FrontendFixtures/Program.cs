using System.Text.Json;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;

// This harness seeds local SQLite snapshots only; it never connects to Immich.
var directory = Path.GetFullPath(args.Single());
var dataDir = Path.Combine(directory, "data");
var configDir = Path.Combine(directory, "config");
if (Directory.Exists(dataDir) || Directory.Exists(configDir))
{
    throw new InvalidOperationException("Frontend fixtures require a fresh directory; existing data will not be overwritten.");
}
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(configDir);
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
await File.WriteAllTextAsync(Path.Combine(configDir, "settings.json"), JsonSerializer.Serialize(
    new AppConfig { Schedule = new ScheduleConfig { Enabled = false } }, json));

var scans = new SavedLocationPreviewRepository(dataDir);
var changes = new LocationChangeRepository(dataDir);
var selection = LocationPreviewSelection.Create(LocationPreviewScope.All, null) with
{
    ScanMode = LocationPreviewScanMode.Continuous,
    PauseAfterDifferences = 100
};
var scanId = await scans.CreateAsync(selection, new());
var rows = Enumerable.Range(0, 100).Select(_ => Row()).ToArray();
foreach (var row in rows)
{
    await scans.AppendAsync(scanId, row, checkpoint: new LocationPreviewCheckpoint(201, TimeSpan.FromMinutes(2).Ticks, 1, 100));
}
await scans.SetStatusAsync(scanId, "Paused");
var failedScanId = await ScanAsync("Failed", "Fixture: country lookup was interrupted.");

var operationId = await changes.CreateAsync(Guid.NewGuid(), Enumerable.Range(0, 100).Select(_ => Row()).ToArray());
var items = await changes.GetItemsAsync(operationId);
for (var index = 0; index < items.Count; index++)
{
    if (index == 2)
    {
        continue;
    }
    await changes.ClaimAsync(operationId, items[index].AssetId, false);
    await changes.FinishAsync(items[index], false, index == 1 ? "NeedsReview" : "Applied", "fixture-revision",
        index == 1 ? "Fixture: interrupted write; check Immich before another change." : null);
}
var pendingUndoId = await OperationAsync("Applied");
var pendingUndoItem = (await changes.GetItemsAsync(pendingUndoId)).Single();
await changes.QueueUndoAsync(pendingUndoId, new[] { pendingUndoItem.AssetId });

// Older matching records must remain discoverable behind newer completed work.
for (var index = 0; index < 27; index++)
{
    await ScanAsync("Paused");
    await OperationAsync("Pending");
}
for (var index = 0; index < 30; index++)
{
    await ScanAsync("Complete");
    await OperationAsync("Applied");
}
await File.WriteAllTextAsync(Path.Combine(directory, "fixtures.json"), JsonSerializer.Serialize(new
{
    ScanId = scanId,
    FailedScanId = failedScanId,
    AssetIds = rows.Select(row => row.Asset.Id),
    OperationId = operationId,
    UndoAssetId = items[0].AssetId,
    PendingUndoId = pendingUndoId,
    ContinuableCount = 29,
    PendingCount = 29
}, json));
Console.WriteLine("Created isolated frontend fixtures.");

async Task<Guid> ScanAsync(string status, string? error = null)
{
    var id = await scans.CreateAsync(selection, new());
    await scans.SetStatusAsync(id, status, error);
    return id;
}

async Task<Guid> OperationAsync(string status)
{
    var id = await changes.CreateAsync(Guid.NewGuid(), new[] { Row() });
    if (status != "Pending")
    {
        var item = (await changes.GetItemsAsync(id)).Single();
        await changes.ClaimAsync(id, item.AssetId, false);
        await changes.FinishAsync(item, false, status, "fixture-revision", null);
    }
    return id;
}

static LocationPreviewRow Row() => new(new LocationPreviewAsset(Guid.NewGuid(), 0, 0,
    new GeoResult("Switzerland", "Zurich", "Zürich")), new GeoResult("Switzerland", "Zurich", "Zurich"));
