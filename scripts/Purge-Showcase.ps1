#requires -version 7.0
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$appHost = Join-Path $repoRoot "src\Jellyfin.SemanticDiscovery.AppHost\Jellyfin.SemanticDiscovery.AppHost.csproj"
$cacheRoot = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot ".data\showcase-cache"))
$runtimeRoot = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot ".data\showcase-runtime"))

dotnet tool run aspire -- stop `
    --apphost $appHost `
    --non-interactive `
    --nologo 2>$null | Out-Null
$description = dotnet tool run aspire -- describe `
    --apphost $appHost `
    --format Json `
    --non-interactive `
    --nologo 2>&1 | Out-String
if ($description -notmatch "No AppHost is currently running") {
    throw "Refusing to purge while the showcase AppHost may still be running."
}

if (Test-Path -LiteralPath $cacheRoot) {
    if ($PSCmdlet.ShouldProcess($cacheRoot, "Purge owned showcase cache")) {
        dotnet run `
            --project (Join-Path $repoRoot "src\Jellyfin.SemanticDiscovery.Setup") `
            --no-build `
            --no-restore `
            -- showcase-purge `
            --output $cacheRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Owned showcase cache purge failed."
        }
    }
}

if (Test-Path -LiteralPath $runtimeRoot) {
    if ($PSCmdlet.ShouldProcess($runtimeRoot, "Purge showcase runtime and index")) {
        dotnet run `
            --project (Join-Path $repoRoot "src\Jellyfin.SemanticDiscovery.Setup") `
            --no-build `
            --no-restore `
            -- showcase-runtime-purge `
            --output $runtimeRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Owned showcase runtime purge failed."
        }
    }
}

if ((Test-Path -LiteralPath $cacheRoot) -or
    (Test-Path -LiteralPath $runtimeRoot)) {
    throw "Showcase purge left residual cache or runtime state."
}

Write-Host "Owned showcase cache, media, and runtime index were purged."
