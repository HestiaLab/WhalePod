#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BackupFile,
    [Parameter(Mandatory = $true)][string]$Database,
    [Parameter(Mandatory = $true)][string]$ConfirmDatabase,
    [switch]$Yes,
    [switch]$AllowExisting,
    [switch]$DryRun,
    [string]$ConnectionString = $env:WHALEPOD_DB_CONNECTION,
    [string]$PgBin = $env:PG_BIN
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib/pg-connection.ps1")

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

if (-not $Yes -and -not $DryRun) {
    throw "Restore overwrites target objects; pass -Yes explicitly."
}

if ($Database -ne $ConfirmDatabase) {
    throw "-ConfirmDatabase must match -Database exactly."
}

if ($Database -notmatch "^whalepod(_[a-z0-9_]+)?$") {
    throw "Database name must be whalepod or whalepod_<lowercase suffix>."
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

$settings = Get-PgConnectionSettings $ConnectionString
$environment = Set-PgConnectionEnvironment $settings

try {
    $checksumFile = "$BackupFile.sha256"
    if (-not (Test-Path $checksumFile)) {
        throw "Checksum file is required: $checksumFile"
    }

    $expected = ((Get-Content -Path $checksumFile -Raw).Trim() -split "\s+")[0].ToLowerInvariant()
    $actual = (Get-FileHash $BackupFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($expected) -or $expected -ne $actual) {
        throw "Backup SHA-256 verification failed; restore aborted."
    }

    if ($DryRun) {
        Write-Output "dryRun=true"
        Write-Output "host=$($environment['PGHOST'])"
        Write-Output "port=$($environment['PGPORT'])"
        Write-Output "database=$Database"
        Write-Output "user=$($environment['PGUSER'])"
        Write-Output "sslmode=$($environment['PGSSLMODE'])"
        Write-Output "sslrootcert=$($environment['PGSSLROOTCERT'])"
        Write-Output "sslcert=$($environment['PGSSLCERT'])"
        Write-Output "sslkey=$($environment['PGSSLKEY'])"
        Write-Output "allowExisting=$($AllowExisting.IsPresent.ToString().ToLowerInvariant())"
        Write-Output "singleTransaction=true"
        return
    }

    $psql = Find-PgTool "psql"
    $pgRestore = Find-PgTool "pg_restore"

    $existsOutput = & $psql -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$Database'"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not check whether the target database exists."
    }

    $databaseExists = ($existsOutput | Out-String).Trim() -eq "1"
    $useClean = $false
    if ($databaseExists) {
        $tablesOutput = & $psql -d $Database -tAc "SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' LIMIT 1"
        if ($LASTEXITCODE -ne 0) {
            throw "Could not inspect the target database."
        }

        if (($tablesOutput | Out-String).Trim() -eq "1") {
            if (-not $AllowExisting) {
                throw "Target database '$Database' already contains tables. Re-run with -AllowExisting to replace it atomically."
            }

            $useClean = $true
        }
    }
    else {
        $createSql = 'CREATE DATABASE "' + $Database + '"'
        & $psql -d postgres -v ON_ERROR_STOP=1 -c $createSql
        if ($LASTEXITCODE -ne 0) {
            throw "CREATE DATABASE failed with exit code $LASTEXITCODE."
        }
    }

    $restoreArgs = @('--single-transaction', '--no-owner', '--no-privileges', '--exit-on-error')
    if ($useClean) {
        $restoreArgs += @('--clean', '--if-exists')
    }

    $restoreArgs += @('--dbname', $Database, $BackupFile)
    & $pgRestore @restoreArgs
    if ($LASTEXITCODE -ne 0) {
        throw "pg_restore failed with exit code $LASTEXITCODE."
    }

    Write-Output "restored=$Database"
    Write-Output "backup=$BackupFile"
}
finally {
    Clear-PgConnectionSecrets
}
