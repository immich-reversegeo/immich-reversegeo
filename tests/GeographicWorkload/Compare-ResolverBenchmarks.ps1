[CmdletBinding()]
param([string] $StateDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$StateDirectory) {
    $commonGitDir = (& git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot locate primary checkout.' }
    $StateDirectory = Join-Path (Split-Path -Parent $commonGitDir) '.local/geographic-workload'
}
$folder = Join-Path $StateDirectory 'benchmarks'
$reports = @{}
foreach ($file in (Get-ChildItem -LiteralPath $folder -Filter '*-summary.json' | Sort-Object LastWriteTime)) {
    $report = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    if ($report.passes.Count -ne 2 -or @($report.samples | Where-Object { $_.phase -eq 'forced-gc-diagnostic' }).Count -eq 0) { continue }
    $profile = if ($report.profile) { $report.profile } else { $report.scope }
    $gcMode = if ($report.gcMode) { $report.gcMode } else { 'workstation' }
    $reports["$($report.mode)-$profile-$gcMode"] = $report
}
$rows = foreach ($key in ($reports.Keys | Sort-Object)) {
    $report = $reports[$key]
    foreach ($pass in $report.passes) {
        $idleSamples = @($report.samples | Where-Object { $_.phase -eq "idle-after-$($pass.pass)" })
        $idle = if ($idleSamples.Count -gt 0) { $idleSamples[-1] } else { $null }
        $gc = @($report.samples | Where-Object { $_.phase -eq 'forced-gc-diagnostic' })[-1]
        $switched = @($report.samples | Where-Object { $_.phase -eq 'switch-to-low-forced-gc' })[-1]
        [PSCustomObject]@{
            Profile=$key; Pass=$pass.pass; Points=$pass.points; Errors=$pass.errors
            Seconds=[Math]::Round($pass.seconds,4)
            PeakRSSMiB=[Math]::Round($pass.peakRssBytes/1MB,1)
            PeakPrivateMiB=[Math]::Round($pass.peakPrivateBytes/1MB,1)
            AllocatedMiB=[Math]::Round($pass.allocatedBytes/1MB,1)
            IdleRSSMiB=if ($idle) { [Math]::Round($idle.rssBytes/1MB,1) } else { $null }
            IdleManagedMiB=if ($idle) { [Math]::Round($idle.managedBytes/1MB,1) } else { $null }
            ForcedGCRSSMiB=[Math]::Round($gc.rssBytes/1MB,1)
            ForcedGCManagedMiB=[Math]::Round($gc.managedBytes/1MB,1)
            SwitchedManagedMiB=if ($switched) { [Math]::Round($switched.managedBytes/1MB,1) } else { $null }
            Hash=$pass.outputSha256
        }
    }
}
$rows | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $folder 'comparison.json')
$rows | Format-Table Profile,Pass,Points,Seconds,PeakRSSMiB,IdleRSSMiB,ForcedGCManagedMiB,SwitchedManagedMiB,Errors -AutoSize
$comparisons = foreach ($key in ($reports.Keys | Where-Object { $_.StartsWith('normal-') } | Sort-Object)) {
    $baseline = $reports[$key]
    $profile = $key.Substring('normal-'.Length)
    foreach ($mode in 'serial', 'low') {
        $other = $reports["$mode-$profile"]
        if (!$other) { continue }
        if ($baseline.datasetSha256 -ne $other.datasetSha256 -or $baseline.countrySha256 -ne $other.countrySha256 -or $baseline.cacheSha256 -ne $other.cacheSha256 -or $baseline.revision -ne $other.revision -or $baseline.runtime -ne $other.runtime) {
            throw 'Input hashes differ; cannot compare modes.'
        }
        [PSCustomObject]@{
            Profile=$profile; Mode=$mode
            SameOutputs=($baseline.passes[0].outputSha256 -ceq $other.passes[0].outputSha256)
            BaselineRepeatStable=($baseline.passes[0].outputSha256 -ceq $baseline.passes[1].outputSha256)
            ModeRepeatStable=($other.passes[0].outputSha256 -ceq $other.passes[1].outputSha256)
        }
    }
}
$comparisons | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $folder 'output-comparisons.json')
$comparisons | Format-Table
$manifest = Get-Content -LiteralPath (Join-Path $StateDirectory 'coordinates.json') -Raw | ConvertFrom-Json
$fullCountryPath = Join-Path $folder 'normal-country-results.json'
if (Test-Path -LiteralPath $fullCountryPath) {
    $fullCountries = Get-Content -LiteralPath $fullCountryPath -Raw | ConvertFrom-Json
    if ($fullCountries.Count -ne $manifest.Count) { throw 'Full country manifest length mismatch.' }
    $catalog = Get-Content -LiteralPath (Join-Path $root 'src/ImmichReverseGeo.Web/bundled-data/iso3166.json') -Raw | ConvertFrom-Json
    $controls = 0
    $failed = 0
    for ($index=0; $index -lt $manifest.Count; $index++) {
        if ($manifest[$index].expectation -ne 'required-country') { continue }
        $controls++
        $iso3 = ($fullCountries[$index] -split '\|')[0]
        $alpha2 = $catalog.iso3ToAlpha2.PSObject.Properties[$iso3].Value
        if ($alpha2 -cne $manifest[$index].referenceCountry) { $failed++ }
    }
    Write-Output "Maintained country controls: $($controls-$failed)/$controls passed in the full normal-mode country run."
    if ($failed -gt 0) { throw 'Required country controls failed; inspect private result files.' }
    $lowControlsPath = Join-Path $folder 'low-country-controls-results.json'
    if (Test-Path -LiteralPath $lowControlsPath) {
        $lowControls = Get-Content -LiteralPath $lowControlsPath -Raw | ConvertFrom-Json
        if ($lowControls.Count -ne $manifest.Count) { throw 'Low-mode controls manifest length mismatch.' }
        $failed = 0
        for ($index=0; $index -lt $manifest.Count; $index++) {
            if ($manifest[$index].expectation -ne 'required-country') { continue }
            $iso3 = ($lowControls[$index] -split '\|')[0]
            $alpha2 = $catalog.iso3ToAlpha2.PSObject.Properties[$iso3].Value
            if ($alpha2 -cne $manifest[$index].referenceCountry) { $failed++ }
        }
        Write-Output "Maintained low-memory country controls: $($controls-$failed)/$controls passed."
        if ($failed -gt 0) { throw 'Low-memory country controls failed.' }
    }
}
if (@($rows | Where-Object { $_.Errors -ne 0 }).Count -gt 0 -or @($comparisons | Where-Object { !$_.SameOutputs -or !$_.BaselineRepeatStable -or !$_.ModeRepeatStable }).Count -gt 0) {
    throw 'Benchmark errors or output differences require review.'
}
