param(
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo "src/WhalePod.DeviceCli/WhalePod.DeviceCli.csproj"
$out = Join-Path $repo "artifacts/device-cli/$Version"
$rids = @("win-x64", "linux-x64", "osx-arm64")

foreach ($rid in $rids) {
    $target = Join-Path $out $rid
    dotnet publish $project `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $target
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid"
    }
}

$lines = @()
foreach ($rid in $rids) {
    $fileName = if ($rid -eq "win-x64") { "whalepod-device.exe" } else { "whalepod-device" }
    $path = Join-Path $out "$rid/$fileName"
    if (-not (Test-Path $path)) {
        throw "published file missing: $path"
    }

    $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines += "$hash  $rid/$fileName"
}

$checksums = Join-Path $out "SHA256SUMS"
$lines | Set-Content -Path $checksums -Encoding ascii
Write-Host "Published Device CLI $Version to $out"
Write-Host "SHA-256 manifest: $checksums"
