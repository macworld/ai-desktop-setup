#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
export PYTHONDONTWRITEBYTECODE=1
dotnet_bin="${AI_SETUP_DOTNET:-$(command -v dotnet || true)}"
if [[ ! -x "$dotnet_bin" ]]; then
  dotnet_bin="$(command -v dotnet || true)"
fi
if [[ -z "$dotnet_bin" || ! -x "$dotnet_bin" ]]; then
  echo 'Install the .NET 10 SDK or set AI_SETUP_DOTNET to its dotnet executable.' >&2
  exit 1
fi
if [[ "$("$dotnet_bin" --version)" != 10.0.401 ]]; then
  echo 'This build requires .NET SDK 10.0.401.' >&2
  exit 1
fi
makensis_bin="${AI_SETUP_MAKENSIS:-$(command -v makensis || true)}"
if [[ -z "$makensis_bin" || ! -x "$makensis_bin" ]]; then
  echo 'Install NSIS 3.12 (macOS: brew install makensis), or set AI_SETUP_MAKENSIS.' >&2
  exit 1
fi
export PATH="$(dirname "$dotnet_bin"):$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
build_id="$(date -u +%Y%m%dT%H%M%SZ)"
output_dir="$repo_root/artifacts/$build_id"
mkdir -p "$output_dir"
"$dotnet_bin" test "$repo_root/Tests/AI.Desktop.Setup.Tests.csproj" -c Release -f net10.0 --nologo
"$dotnet_bin" build "$repo_root/Tests/AI.Desktop.Setup.Tests.csproj" -c Release -f net48 --nologo
for architecture in x64 arm64; do
  platform="$architecture"
  if [[ "$architecture" == arm64 ]]; then platform=ARM64; fi
  "$dotnet_bin" publish "$repo_root/App/AI.Desktop.Setup.csproj" \
    -c Release -p:Platform="$platform" \
    -o "$output_dir/publish-$architecture" --nologo
done
python3 "$repo_root/scripts/package-windows.py" "$output_dir" --makensis "$makensis_bin"
