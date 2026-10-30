#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ConnectionString = $env:WHALEPOD_DB_CONNECTION,
    [string]$OutputDirectory,
    [int]$RetentionDays = 30,
    [string]$StatusFile = $env:WHALEPOD_BACKUP_STATUS_FILE,
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

if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    throw "Set WHALEPOD_DB_CONNECTION or pass -ConnectionString."
}

$settings = Get-PgConnectionSettings $ConnectionString
$database = $settings["database"]
if ([string]::IsNullOrWhiteSpace($database)) {
    throw "The connection string must include Database."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\backups"
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$environment = Set-PgConnectionEnvironment $settings
$env:PGDATABASE = $database

$timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
$fileName = "whalepod-$database-$timestamp.dump"
$backupPath = Join-Path $OutputDirectory $fileName
$pgDump = Find-PgTool "pg_dump"

try {
    & $pgDump --format=custom --compress=9 --no-owner --no-privileges --file $backupPath
    if ($LASTEXITCODE -ne 0) {
        throw "pg_dump failed with exit code $LASTEXITCODE."
    }

    $file = Get-Item $backupPath
    $hash = (Get-FileHash $backupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $fileName" | Set-Content -Path "$backupPath.sha256" -Encoding ascii

    if (-not [string]::IsNullOrWhiteSpace($StatusFile)) {
        $statusDirectory = Split-Path $StatusFile -Parent
        if (-not [string]::IsNullOrWhiteSpace($statusDirectory)) {
            New-Item -ItemType Directory -Path $statusDirectory -Force | Out-Null
        }

        [ordered]@{
            formatVersion = 1
            lastSuccessUtc = (Get-Date).ToUniversalTime().ToString("o")
            database = $database
            fileName = $fileName
            bytes = $file.Length
            sha256 = $hash
        } | ConvertTo-Json -Compress | Set-Content -Path $StatusFile -Encoding utf8
    }

    if ($RetentionDays -gt 0) {
        $cutoff = (Get-Date).AddDays(-$RetentionDays)
        Get-ChildItem -Path $OutputDirectory -Filter "whalepod-*.dump" -File |
            Where-Object { $_.LastWriteTime -lt $cutoff } |
            ForEach-Object {
                Remove-Item -LiteralPath $_.FullName -Force
                $checksum = "$($_.FullName).sha256"
                if (Test-Path $checksum) { Remove-Item -LiteralPath $checksum -Force }
            }
    }

    Write-Output "backup=$backupPath"
    Write-Output "bytes=$($file.Length)"
    Write-Output "sha256=$hash"
}
finally {
    Clear-PgConnectionSecrets
}
