[CmdletBinding()]
param(
    [ValidateSet('Country', 'Location')][string[]] $Scope = @('Country', 'Location'),
    [ValidateSet('DEU', 'CHE')][string] $Country = 'DEU',
    [ValidateSet('Server', 'Workstation')][string] $GcMode = 'Server',
    [ValidateRange(1, 20000)][int] $PointLimit = 256,
    [ValidateRange(0, 900)][int] $IdleSeconds = 30
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$commonGitDir = (& git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate primary checkout.' }
$state = Join-Path (Split-Path -Parent $commonGitDir) '.local/geographic-workload'
if (!(Test-Path -LiteralPath (Join-Path $state 'coordinates.json'))) { throw 'Generate the workload first.' }
$output = Join-Path $state 'benchmark-build'
$harness = Join-Path $output 'bin/GeographicWorkload/release/GeographicWorkload.dll'
$runner = Join-Path $PSScriptRoot 'Run-GeographicWorkload.ps1'
$pidPath = Join-Path $state 'reversegeo.pid'
$wasRunning = (Test-Path -LiteralPath $pidPath) -and [bool](Get-Process -Id ([int][IO.File]::ReadAllText($pidPath)) -ErrorAction SilentlyContinue)
$previousGcServer = [Environment]::GetEnvironmentVariable('DOTNET_gcServer')
try {
    if ($wasRunning) {
        & $runner -Action StopApp
    }
    & dotnet build (Join-Path $PSScriptRoot 'GeographicWorkload.csproj') -c Release --artifacts-path $output --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Cannot compile benchmark harness.' }
    [Environment]::SetEnvironmentVariable('DOTNET_gcServer', $(if ($GcMode -eq 'Server') { '1' } else { '0' }))
    if ($Scope -contains 'Location') {
        # Full country classification establishes selection; this extra run has no idle delay.
        & dotnet $harness benchmark $root $state normal country 0
        if ($LASTEXITCODE -ne 0) { throw 'Country selection baseline failed.' }
        if ($Country -eq 'CHE') {
            & dotnet $harness prepare-benchmark-cache $root $state
            if ($LASTEXITCODE -ne 0) { throw 'Swiss cache preparation failed.' }
        }
    }
    foreach ($selectedScope in $Scope) {
        foreach ($mode in 'normal', 'serial', 'low') {
            if ($selectedScope -eq 'Country') {
                & dotnet $harness benchmark $root $state $mode country $IdleSeconds $PointLimit
            } else {
                & dotnet $harness benchmark $root $state $mode location $IdleSeconds $Country $PointLimit
            }
            if ($LASTEXITCODE -ne 0) { throw "Benchmark failed: $mode/$selectedScope" }
        }
    }
    & (Join-Path $PSScriptRoot 'Compare-ResolverBenchmarks.ps1') -StateDirectory $state
}
finally {
    [Environment]::SetEnvironmentVariable('DOTNET_gcServer', $previousGcServer)
    if ($wasRunning) {
        & $runner -Action StartApp
    }
}
