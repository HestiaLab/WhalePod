$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "../lib/pg-connection.ps1")

function Fail([string]$Message) {
    throw "FAIL: $Message"
}

$settings = Get-PgConnectionSettings 'Host=db.internal;Port=5432;Database=whalepod;Username=app;Password="p;a ss""word";SSL Mode=VerifyFull;Root Certificate="C:\ca cert.pem";Client Certificate=/tmp/client.pem;Client Key=/tmp/client.key;Ssl Password="k;ey"'
if ($settings["host"] -ne "db.internal") { Fail "host" }
if ($settings["password"] -ne 'p;a ss"word') { Fail "quoted password with semicolon/quote/space" }
if ($settings["sslmode"] -ne "VerifyFull") { Fail "sslmode parsing" }

$environment = Get-PgConnectionEnvironment $settings
if ($environment["PGSSLMODE"] -ne "verify-full") { Fail "sslmode normalization" }
if ($environment["PGSSLROOTCERT"] -ne 'C:\ca cert.pem') { Fail "root certificate" }
if ($environment["PGSSLCERT"] -ne "/tmp/client.pem") { Fail "client certificate" }
if ($environment["PGSSLKEY"] -ne "/tmp/client.key") { Fail "client key" }
if ($environment["PGSSLPASSWORD"] -ne "k;ey") { Fail "ssl password" }

$tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("whalepod-pg-test-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempDirectory -Force | Out-Null
try {
    $dump = Join-Path $tempDirectory "backup.dump"
    "not-a-real-dump" | Set-Content -Path $dump -NoNewline
    $hash = (Get-FileHash $dump -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  backup.dump" | Set-Content -Path "$dump.sha256" -Encoding ascii

    $connection = 'Host=127.0.0.1;Database=whalepod;Username=app;Password="secret;value";SSL Mode=VerifyFull;Root Certificate="C:\ca cert.pem"'
    $output = & (Join-Path $PSScriptRoot "../restore-postgres.ps1") `
        -BackupFile $dump `
        -Database whalepod_test_restore `
        -ConfirmDatabase whalepod_test_restore `
        -DryRun `
        -ConnectionString $connection
    $joined = $output -join "`n"
    if ($joined -notmatch "sslmode=verify-full") { Fail "dry-run must show sslmode" }
    if ($joined -notmatch "singleTransaction=true") { Fail "dry-run must show transaction mode" }
    if ($joined -match "secret;value") { Fail "dry-run must not print the password" }

    $missingChecksum = Join-Path $tempDirectory "missing.dump"
    "dump" | Set-Content -Path $missingChecksum -NoNewline
    try {
        & (Join-Path $PSScriptRoot "../restore-postgres.ps1") `
            -BackupFile $missingChecksum `
            -Database whalepod_test_restore `
            -ConfirmDatabase whalepod_test_restore `
            -DryRun `
            -ConnectionString $connection | Out-Null
        Fail "missing checksum must abort"
    }
    catch {
        if ($_.Exception.Message -notmatch "Checksum file is required") { throw }
    }
}
finally {
    Remove-Item -LiteralPath $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "pg-connection PowerShell tests passed"
