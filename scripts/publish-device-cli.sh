#!/usr/bin/env bash
set -euo pipefail

version="${1:-0.1.0}"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo/src/WhalePod.DeviceCli/WhalePod.DeviceCli.csproj"
out="$repo/artifacts/device-cli/$version"
rids=(win-x64 linux-x64 osx-arm64)

for rid in "${rids[@]}"; do
    dotnet publish "$project" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:DebugType=None \
        -p:DebugSymbols=false \
        -o "$out/$rid"
done

: > "$out/SHA256SUMS"
for rid in "${rids[@]}"; do
    file_name="whalepod-device"
    if [[ "$rid" == "win-x64" ]]; then
        file_name="whalepod-device.exe"
    fi

    file="$out/$rid/$file_name"
    if [[ ! -f "$file" ]]; then
        echo "published file missing: $file" >&2
        exit 1
    fi

    if command -v sha256sum >/dev/null 2>&1; then
        (cd "$out" && sha256sum "$rid/$file_name" >> SHA256SUMS)
    else
        (cd "$out" && shasum -a 256 "$rid/$file_name" >> SHA256SUMS)
    fi
done

echo "Published Device CLI $version to $out"
echo "SHA-256 manifest: $out/SHA256SUMS"
