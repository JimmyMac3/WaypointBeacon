$ErrorActionPreference = "Stop"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Error "dotnet SDK is required. Install .NET SDK 10.x and ensure it is on PATH."
}

$hasNet10 = dotnet --list-sdks | Select-String '^10\.'
if (-not $hasNet10) {
    Write-Error "No .NET 10 SDK detected. Install .NET SDK 10.x (Visual Studio 2026 / SDK installer)."
}

dotnet run --project ZZCakeBuild/CakeBuild.csproj -- $args
exit $LASTEXITCODE;
