param(
    [Parameter(Mandatory)][string]$Inputs,
    [Parameter(Mandatory)][string]$Baseline,
    [Parameter(Mandatory)][string]$Candidate,
    [Parameter(Mandatory)][string]$Results,
    [ValidateRange(3, 20)][int]$Rounds = 3
)
$ErrorActionPreference = 'Stop'
$resultsPath = [IO.Path]::GetFullPath($Results)
if (Test-Path -LiteralPath $resultsPath) {
    throw "Use a new result directory: $resultsPath"
}
New-Item -ItemType Directory -Path $resultsPath | Out-Null
$versions = @{ baseline = $Baseline; candidate = $Candidate }
foreach ($round in 0..$Rounds) {
    foreach ($country in @('FRA', 'USA')) {
        $order = if ($round % 2 -eq 0) { @('baseline', 'candidate') } else { @('candidate', 'baseline') }
        foreach ($version in $order) {
            $output = Join-Path $resultsPath "$country-$round-$version"
            & dotnet $versions[$version] build $Inputs $country $output
            if ($LASTEXITCODE -ne 0) {
                throw "Builder failed: $country round $round $version"
            }
        }
        & dotnet $Candidate verify (Join-Path $resultsPath "$country-$round-baseline/overture-divisions/$country.db") (Join-Path $resultsPath "$country-$round-candidate/overture-divisions/$country.db")
        if ($LASTEXITCODE -ne 0) {
            throw "Cache contents differ: $country round $round"
        }
    }
}
$summary = foreach ($country in @('FRA', 'USA')) {
    foreach ($version in @('baseline', 'candidate')) {
        $rows = @(1..$Rounds | ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $resultsPath "$country-$_-$version/result.json") | ConvertFrom-Json })
        $record = [ordered]@{ country = $country; version = $version; rounds = $Rounds }
        foreach ($metric in @('elapsedMs', 'importMs', 'cpuMs', 'allocatedBytes', 'peakWorkingSetBytes', 'cacheBytes')) {
            $values = @($rows | ForEach-Object { $_.$metric } | Sort-Object)
            $middle = [int][Math]::Floor($values.Count / 2)
            $record[$metric] = if ($values.Count % 2 -eq 0) { ($values[$middle - 1] + $values[$middle]) / 2 } else { $values[$middle] }
        }
        [pscustomobject]$record
    }
}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resultsPath 'summary.json')
$summary | Format-Table -AutoSize
