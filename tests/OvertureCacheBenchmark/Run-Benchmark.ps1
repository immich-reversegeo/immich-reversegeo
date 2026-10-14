[CmdletBinding()]
param(
    [ValidatePattern('^\d{4}-\d{2}-\d{2}\.\d+$')][string] $Release = '2026-09-23.1',
    [ValidatePattern('^[A-Z]{2}$')][string] $Country = 'ES',
    [ValidateSet('original-default', 'original-threads8', 'phased-default', 'phased-buffer32', 'phased-threads8', 'phased-buffer32-threads8')]
    [string[]] $Variants = @('original-default', 'phased-default', 'phased-buffer32', 'phased-threads8'),
    [ValidateRange(1, 10)][int] $Repetitions = 1,
    [ValidateRange(1, 32)][int] $CpuCount = 2,
    [ValidateRange(512, 32768)][int] $MemoryMiB = 2048,
    [ValidateRange(1, 3600)][int] $TimeoutSeconds = 1200,
    [switch] $Fixture,
    [switch] $LowMemory,
    [switch] $SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runName = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$output = Join-Path $root "_out/duckdb-cache-benchmark/$runName"
$image = 'immich-reversegeo:duckdb-cache-benchmark'
New-Item -ItemType Directory -Path $output -Force | Out-Null
if ($LowMemory -and ($Variants | Where-Object { $_ -notin @('original-default', 'phased-default') }))
{
    throw 'Low-memory comparisons must keep their single-thread/default-buffer settings.'
}
if (!$SkipBuild)
{
    & docker build -f (Join-Path $PSScriptRoot 'Dockerfile') -t $image $root
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Benchmark image build failed.'
    }
}
$imageId = (& docker image inspect $image --format '{{.Id}}').Trim()
if ($LASTEXITCODE -ne 0)
{
    throw 'Cannot resolve benchmark image.'
}
@{
    image = $image; imageId = $imageId; release = $Release; country = $Country
    cpuCount = $CpuCount; memoryMiB = $MemoryMiB; repetitions = $Repetitions
    fixture = $Fixture.IsPresent; lowMemory = $LowMemory.IsPresent
    variants = $Variants; timeoutSeconds = $TimeoutSeconds
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'environment.json') -Encoding utf8
$results = @()
$failed = $false
for ($repetition = 1; $repetition -le $Repetitions; $repetition++)
{
    # Reverse ordering on alternating repetitions to expose order/cache bias.
    $ordered = @($Variants)
    if ($repetition % 2 -eq 0)
    {
        [Array]::Reverse($ordered)
    }
    foreach ($variant in $ordered)
    {
        $label = "$variant-$repetition"
        $name = "duckdb-cache-$runName-$label"
        $arguments = @('run', '--rm', '--name', $name, '--cpus', "$CpuCount", '--memory', "$($MemoryMiB)m",
            '--mount', "type=bind,source=$output,target=/results", $imageId,
            '--release', $Release, '--country', $Country,
            '--variant', $(if ($variant.StartsWith('original')) { 'original' } else { 'phased' }),
            '--output', "/results/$label", '--fixture', "$($Fixture.IsPresent)".ToLowerInvariant(),
            '--low-memory', "$($LowMemory.IsPresent)".ToLowerInvariant(), '--timeout-seconds', "$TimeoutSeconds")
        if ($variant.Contains('buffer32'))
        {
            $arguments += @('--buffer-mib', '32')
        }
        if ($variant.Contains('threads8'))
        {
            $arguments += @('--threads', '8')
        }
        Write-Host "Running $label; output: $output"
        & docker @arguments 2>&1 | Tee-Object -FilePath (Join-Path $output "$label.log")
        if ($LASTEXITCODE -ne 0)
        {
            $failed = $true
        }
        $summary = Join-Path $output "$label/summary.json"
        if (Test-Path -LiteralPath $summary)
        {
            $entry = Get-Content -LiteralPath $summary -Raw | ConvertFrom-Json
            $entry | Add-Member -NotePropertyName label -NotePropertyValue $label
            $results += $entry
        }
        else
        {
            $failed = $true
        }
    }
}
$results | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $output 'comparison.json') -Encoding utf8
$results | Select-Object label,success,querySeconds,resultRows,peakWorkingSetBytes,logicalSha256 | Format-Table -AutoSize
$successful = @($results | Where-Object success)
if (@($successful.logicalSha256 | Select-Object -Unique).Count -gt 1 -or @($successful.resultRows | Select-Object -Unique).Count -gt 1)
{
    throw "Output mismatch between benchmark variants. Inspect $output"
}
Write-Host "Results: $output"
if ($failed)
{
    throw 'One or more benchmark variants failed; successful timings are not a complete comparison.'
}
