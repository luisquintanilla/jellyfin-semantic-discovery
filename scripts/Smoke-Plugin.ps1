#requires -version 7.0
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug",
    [int] $Port = 18096
)

$ErrorActionPreference = "Stop"
$env:ImportDirectoryBuildTargets = "false"
$repoRoot = [System.IO.Path]::GetFullPath(
    (Split-Path -Parent $PSScriptRoot))
$dataRoot = Join-Path $repoRoot ".data\plugin-smoke"
$pluginProject = Join-Path `
    $repoRoot `
    "src\Jellyfin.SemanticDiscovery.Plugin\Jellyfin.SemanticDiscovery.Plugin.csproj"
$setupProject = Join-Path `
    $repoRoot `
    "src\Jellyfin.SemanticDiscovery.Setup\Jellyfin.SemanticDiscovery.Setup.csproj"
$pluginOutput = Join-Path `
    $repoRoot `
    "src\Jellyfin.SemanticDiscovery.Plugin\bin\$Configuration\net10.0"
$pluginDirectory = Join-Path `
    $dataRoot `
    "config\plugins\Semantic Discovery_0.1.0.0"
$image = "jellyfin/jellyfin@sha256:97150310f89f908abbf6fa9640ccc36f9bfa9e5e987775db310b3b70c2d11602"

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,
        [Parameter(ValueFromRemainingArguments)]
        [string[]] $Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath $($Arguments -join ' ')' failed with exit code $LASTEXITCODE."
    }
}

function Remove-SmokeContainer {
    param([string] $ContainerId)

    if ($ContainerId) {
        docker rm --force $ContainerId | Out-Null
    }
}

Push-Location $repoRoot
try {
    if (Test-Path -LiteralPath $dataRoot) {
        Remove-Item -LiteralPath $dataRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force `
        (Join-Path $dataRoot "config"), `
        (Join-Path $dataRoot "cache") | Out-Null

    Invoke-Checked dotnet build `
        $pluginProject `
        --configuration $Configuration `
        --no-restore
    Invoke-Checked dotnet run `
        --project $setupProject `
        --configuration $Configuration `
        --no-restore `
        -- `
        plugin `
        --source $pluginOutput `
        --destination $pluginDirectory
    Invoke-Checked docker pull $image

    for ($boot = 1; $boot -le 2; $boot++) {
        $containerId = $null
        try {
            $containerId = docker run `
                --detach `
                --publish "127.0.0.1:${Port}:8096" `
                --mount "type=bind,source=$(Join-Path $dataRoot 'config'),target=/config" `
                --mount "type=bind,source=$(Join-Path $dataRoot 'cache'),target=/cache" `
                $image
            if ($LASTEXITCODE -ne 0 -or -not $containerId) {
                throw "Unable to start the pinned Jellyfin container."
            }

            $containerId = $containerId.Trim()
            for ($attempt = 0; $attempt -lt 90; $attempt++) {
                try {
                    $response = Invoke-WebRequest `
                        -UseBasicParsing `
                        -Uri "http://127.0.0.1:$Port/System/Ping" `
                        -TimeoutSec 2
                    if ($response.StatusCode -eq 200) {
                        break
                    }
                } catch {
                    if ($attempt -eq 89) {
                        throw
                    }
                }
                Start-Sleep -Seconds 1
            }

            $logs = docker logs $containerId 2>&1 | Out-String
            if ($logs -notmatch "Loaded plugin: Semantic Discovery 0\.1\.0\.0") {
                throw "Semantic Discovery did not load.`n$logs"
            }
            if ($logs -match "Error registering plugin services from\s+Jellyfin\.SemanticDiscovery" -or
                $logs -match "Error creating\s+Jellyfin\.SemanticDiscovery") {
                throw "Semantic Discovery reported an activation error.`n$logs"
            }
        } finally {
            Remove-SmokeContainer $containerId
        }
    }

    Write-Host "Jellyfin 12 plugin load and restart smoke passed."
} finally {
    Pop-Location
}
