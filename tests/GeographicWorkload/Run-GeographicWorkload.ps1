[CmdletBinding()]
param(
    [ValidateSet('Import', 'Generate', 'Start', 'StartApp', 'StopApp', 'Seed', 'Report', 'Reset', 'Stop', 'Status')]
    [string] $Action = 'Status'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$gitCommand = if ($IsWindows) { 'git.exe' } else { 'git' }
$commonGitDir = (& $gitCommand -C $root rev-parse --path-format=absolute --git-common-dir).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate the primary checkout.' }
$primaryRoot = Split-Path -Parent $commonGitDir
$state = Join-Path $primaryRoot '.local/geographic-workload'
New-Item -ItemType Directory -Force -Path $state | Out-Null
[IO.File]::WriteAllText((Join-Path $state '.gitignore'), "*`n")
$composePath = Join-Path $state 'compose.yml'
$envPath = Join-Path $state 'bulk.env'
$projectName = 'immich-reversegeo-geographic-workload'
if ($Action -eq 'StopApp') {
    $pidPath = Join-Path $state 'reversegeo.pid'
    if (!(Test-Path -LiteralPath $pidPath)) { Write-Host 'No dedicated ReverseGeo process is recorded.'; return }
    $appId = [int][IO.File]::ReadAllText($pidPath)
    $appProcess = Get-Process -Id $appId -ErrorAction SilentlyContinue
    if ($appProcess) {
        $commandLine = if ($IsWindows) {
            (Get-CimInstance Win32_Process -Filter "ProcessId = $appId").CommandLine
        } else {
            (& ps -p $appId -o args=) -join ' '
        }
        if (!$commandLine -or !$commandLine.Contains('ImmichReverseGeo.Web.dll') -or !$commandLine.Contains('http://127.0.0.1:18222')) {
            throw 'The recorded PID does not belong to the dedicated test app; refusing to stop it.'
        }
        Stop-Process -Id $appId
    }
    Remove-Item -LiteralPath $pidPath
    Write-Host 'Dedicated ReverseGeo process stopped.'
    return
}
if ($Action -in 'Import', 'Generate') {
    & dotnet run --project (Join-Path $PSScriptRoot 'GeographicWorkload.csproj') --configuration Release -- $Action.ToLowerInvariant() $root $state
    if ($LASTEXITCODE -ne 0) { throw "$Action failed." }
    return
}
$endpoint = (& docker context inspect --format '{{.Endpoints.docker.Host}}').Trim()
if ($LASTEXITCODE -ne 0 -or $endpoint -notmatch '^(npipe|unix)://') { throw 'Use a local Docker context.' }
if ($Action -in 'Start', 'StartApp', 'Seed') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'compose.yml') -Destination $composePath -Force
    Copy-Item -LiteralPath (Join-Path $root 'tests/LiveImmichDb/immich.json') -Destination (Join-Path $state 'immich.json') -Force
    if (!(Test-Path -LiteralPath $envPath)) {
        $password = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
        [IO.File]::WriteAllText($envPath, "DB_PASSWORD=$password`n")
    }
    $guard = "CREATE SCHEMA immich_reversegeo_fixture;`nCREATE TABLE immich_reversegeo_fixture.guard (name text PRIMARY KEY);`nINSERT INTO immich_reversegeo_fixture.guard VALUES ('$projectName');`n"
    [IO.File]::WriteAllText((Join-Path $state 'bulk-guard.sql'), $guard)
    # Compose's host environment takes precedence over --env-file; constrain the only variable.
    $previousPassword = [Environment]::GetEnvironmentVariable('DB_PASSWORD')
    try {
        $bulkPassword = [IO.File]::ReadAllLines($envPath) | Where-Object { $_.StartsWith('DB_PASSWORD=') }
        if (@($bulkPassword).Count -ne 1) { throw 'Invalid local bulk password file.' }
        [Environment]::SetEnvironmentVariable('DB_PASSWORD', $bulkPassword.Substring('DB_PASSWORD='.Length))
        & docker compose -p $projectName --env-file $envPath -f $composePath up -d
        if ($LASTEXITCODE -ne 0) { throw 'Cannot start the isolated geographic workload stack.' }
    }
    finally {
        [Environment]::SetEnvironmentVariable('DB_PASSWORD', $previousPassword)
    }
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    do {
        $ready = $false
        try {
            $version = Invoke-RestMethod -Uri 'http://127.0.0.1:18283/api/server/version' -TimeoutSec 3
            $ready = $version.major -eq 3 -and $version.minor -eq 3 -and $version.patch -eq 1
        } catch { }
        if (!$ready) {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Timed out waiting for the pinned local server.' }
            Start-Sleep -Seconds 2
        }
    } while (!$ready)
}
if ($Action -eq 'StartApp') {
    $configDir = Join-Path $state 'reversegeo-config'
    $dataDir = Join-Path $state 'reversegeo-data'
    New-Item -ItemType Directory -Force -Path $configDir, $dataDir | Out-Null
    $settingsPath = Join-Path $configDir 'settings.json'
    if (!(Test-Path -LiteralPath $settingsPath)) {
        [IO.File]::WriteAllText($settingsPath, '{"schedule":{"enabled":false,"cron":"0 * * * *"},"processing":{"batchSize":50,"maxDegreeOfParallelism":4,"lowMemoryMode":false}}')
    }
    $pidPath = Join-Path $state 'reversegeo.pid'
    if ((Test-Path -LiteralPath $pidPath) -and (Get-Process -Id ([int][IO.File]::ReadAllText($pidPath)) -ErrorAction SilentlyContinue)) {
        Write-Host 'The dedicated ReverseGeo process is already running at http://127.0.0.1:18222.'
        return
    }
    & dotnet build (Join-Path $root 'src/ImmichReverseGeo.Web/ImmichReverseGeo.Web.csproj') --configuration Release --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Cannot build the dedicated ReverseGeo app.' }
    $appEnvironment = @{
        DB_HOST = '127.0.0.1'; DB_PORT = '18433'; DB_USERNAME = 'postgres'; DB_DATABASE_NAME = 'immich'
        DB_PASSWORD = ([IO.File]::ReadAllLines($envPath) | Where-Object { $_.StartsWith('DB_PASSWORD=') }).Substring('DB_PASSWORD='.Length)
        CONFIG_DIR = $configDir; DATA_DIR = $dataDir; ASPNETCORE_ENVIRONMENT = 'Development'
    }
    $previous = @{}
    try {
        foreach ($key in $appEnvironment.Keys) {
            $previous[$key] = [Environment]::GetEnvironmentVariable($key)
            [Environment]::SetEnvironmentVariable($key, $appEnvironment[$key])
        }
        $launch = @{
            FilePath = (Get-Command dotnet).Source
            ArgumentList = @((Join-Path $root 'src/ImmichReverseGeo.Web/bin/Release/net10.0/ImmichReverseGeo.Web.dll'), '--urls', 'http://127.0.0.1:18222')
            WorkingDirectory = (Join-Path $root 'src/ImmichReverseGeo.Web')
            RedirectStandardOutput = (Join-Path $state 'reversegeo-stdout.log')
            RedirectStandardError = (Join-Path $state 'reversegeo-stderr.log')
            PassThru = $true
        }
        if ($IsWindows) { $launch.WindowStyle = 'Hidden' }
        $appProcess = Start-Process @launch
        [IO.File]::WriteAllText($pidPath, [string]$appProcess.Id)
        Write-Host 'Dedicated ReverseGeo app: http://127.0.0.1:18222 (automatic schedule disabled on first setup).'
    }
    finally {
        foreach ($key in $previous.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previous[$key])
        }
    }
} elseif ($Action -eq 'Stop') {
    & docker compose -p $projectName --env-file $envPath -f $composePath stop
} elseif ($Action -eq 'Status') {
    if (!(Test-Path -LiteralPath $envPath)) { Write-Host 'The bulk-test stack has not been initialized.'; return }
    & docker compose -p $projectName --env-file $envPath -f $composePath ps --all
} elseif ($Action -ne 'Start') {
    & dotnet run --project (Join-Path $PSScriptRoot 'GeographicWorkload.csproj') --configuration Release -- $Action.ToLowerInvariant() $root $state
}
if ($LASTEXITCODE -ne 0) { throw "$Action failed." }
