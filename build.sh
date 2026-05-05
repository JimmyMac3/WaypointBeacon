#!/usr/bin/env bash
set -euo pipefail

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet SDK is required. Install .NET SDK 10.x and ensure it is on PATH." >&2
  exit 127
fi

if ! dotnet --list-sdks | grep -q '^10\.'; then
  echo "No .NET 10 SDK detected. Install .NET SDK 10.x (Visual Studio 2026 / SDK installer)." >&2
  exit 1
fi

dotnet run --project ./ZZCakeBuild/CakeBuild.csproj -- "$@"
