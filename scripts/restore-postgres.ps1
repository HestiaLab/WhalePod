#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BackupFile,
    [Parameter(Mandatory = $true)][string]$Database,
    [Parameter(Mandatory = $true)][string]$ConfirmDatabase,
    [switch]$Yes,
    [string]$ConnectionString = $env:WHALEPOD_DB_CONNECTION,
    [string]$PgBin = $env:PG_BIN
)

$ErrorActionPreference = "Stop"

function Get-ConnectionSettings {
    param([string]$Value)
    $settings = @{}
    foreach ($part in $Value.Split(";")) {
        if ([string]::IsNullOrWhiteSpace($part)) { continue }
        $separator = $part.IndexOf("=")
        if ($separator -lt 1) { continue }
        $key = $part.Substring(0, $separator).Trim().ToLowerInvariant()
        $settings[$key] = $part.Substring($separator + 1).Trim()
    }
    return $settings
}

function Find-PgTool {
    param([string]$Name)
    if ($PgBin) {
        $candidate = Join-Path $PgBin "$Name.exe"
        if (Test-Path $candidate) { return $candidate }
    }

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $roots = @(
        "C:\Program Files\PostgreSQL\18\bin",
        "C:\Program Files\PostgreSQL\17\bin",
        "/usr/bin",
        "/usr/local/bin"
    )
    foreach ($root in $roots) {
        $candidate = Join-Path $root $(if ($IsWindows -or $env:OS -eq "Windows_NT") { "$Name.exe" } else { $Name })
        if (Test-Path $candidate) { return $candidate }
    }

    throw "Cannot find $Name. Set PG_BIN to the PostgreSQL bin directory."
}

if (-not $Yes) {
    throw "Restore overwrites target objects; pass -Yes explicitly."
}

if ($Database -ne $ConfirmDatabase) {
    throw "-ConfirmDatabase must match -Database exactly."
}

if ($Database -notmatch "^[a-z0-9_]+$") {
    throw "Database name may only contain lowercase letters, digits and underscores."
}

if ($Database -in @("postgres", "template0", "template1")) {
    throw "Refusing to restore into a system database."
}

if (-not (Test-Path $BackupFile)) {
    throw "Backup file not found: $BackupFile"
}

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw "Set WHALEPOD_DB_CONNECTION or pass -ConnectionString."
}

$settings = Get-ConnectionSettings $ConnectionString
$env:PGHOST = if ($settings.ContainsKey("host")) { $settings["host"] } else { "127.0.0.1" }
$env:PGPORT = if ($settings.ContainsKey("port")) { $settings["port"] } else { "5432" }
if ($settings.ContainsKey("username")) { $env:PGUSER = $settings["username"] }
if ($settings.ContainsKey("password")) { $env:PGPASSWORD = $settings["password"] }

$checksumFile = "$BackupFile.sha256"
if (Test-Path $checksumFile) {
    $expected = ((Get-Content -Path $checksumFile -Raw).Trim() -split "\s+")[0].ToLowerInvariant()
    $actual = (Get-FileHash $BackupFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) {
        throw "Backup SHA-256 verification failed; restore aborted."
    }
}

$psql = Find-PgTool "psql"
$pgRestore = Find-PgTool "pg_restore"

try {
    $existsOutput = & $psql -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$Database'"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not check whether the target database exists."
    }

    $databaseExists = ($existsOutput | Out-String).Trim() -eq "1"
    if (-not $databaseExists) {
        $createSql = 'CREATE DATABASE "' + $Database + '"'
        & $psql -d postgres -v ON_ERROR_STOP=1 -c $createSql
        if ($LASTEXITCODE -ne 0) {
            throw "CREATE DATABASE failed with exit code $LASTEXITCODE."
        }
    }

    & $pgRestore --clean --if-exists --no-owner --no-privileges --exit-on-error --dbname $Database $BackupFile
    if ($LASTEXITCODE -ne 0) {
        throw "pg_restore failed with exit code $LASTEXITCODE."
    }

    Write-Output "restored=$Database"
    Write-Output "backup=$BackupFile"
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
