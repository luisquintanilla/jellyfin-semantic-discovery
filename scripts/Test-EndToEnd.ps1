#requires -version 7.0
<#
.SYNOPSIS
Starts the AppHost, waits for setup to finish, runs the end-to-end tests against Jellyfin, and stops everything.

.EXAMPLE
./scripts/Test-EndToEnd.ps1                        # ci: embeddings only, text-layer PDFs
./scripts/Test-EndToEnd.ps1 -Profile demo -OllamaGpu nvidia
#>
[CmdletBinding()]
param(
    [ValidateSet("ci", "demo")]
    [string] $Profile = "ci",
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug",
    [string] $NuGetConfig = "",
    [ValidateSet("none", "nvidia", "amd")]
    [string] $OllamaGpu = "none",
    [int] $TimeoutSeconds = 900
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$appHost = Join-Path $repoRoot "src/Jellyfin.SemanticDiscovery.AppHost/Jellyfin.SemanticDiscovery.AppHost.csproj"
$testProject = Join-Path $repoRoot "tests/Jellyfin.SemanticDiscovery.EndToEndTests/Jellyfin.SemanticDiscovery.EndToEndTests.csproj"
$dataRoot = Join-Path $repoRoot ".data/e2e-$Profile"
$credentialsPath = Join-Path $dataRoot "jellyfin-login.json"
$configFile = if ($NuGetConfig) { @("--configfile", [IO.Path]::GetFullPath($NuGetConfig)) } else { @() }

function Invoke-Checked([string] $What, [scriptblock] $Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

function Invoke-Aspire {
    param([Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)
    Invoke-Checked "aspire $($Arguments[0])" {
        dotnet tool run aspire -- @Arguments --apphost $appHost --non-interactive --nologo
    }
}

function Stop-AppHost {
    dotnet tool run aspire -- stop --apphost $appHost --non-interactive --nologo 2>&1 | Out-Null
}

Push-Location $repoRoot
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
    Invoke-Checked "dotnet tool restore" { dotnet tool restore @configFile }
    Invoke-Checked "dotnet restore" { dotnet restore Jellyfin.SemanticDiscovery.slnx --locked-mode --nologo @configFile }
    Invoke-Checked "dotnet build" { dotnet build Jellyfin.SemanticDiscovery.slnx -c $Configuration --no-restore --nologo }

    Stop-AppHost
    if (Test-Path $dataRoot) { Remove-Item $dataRoot -Recurse -Force }

    $env:SEMANTIC_DISCOVERY_PROFILE = $Profile
    $env:SEMANTIC_DISCOVERY_DATA = $dataRoot
    $env:SEMANTIC_DISCOVERY_OLLAMA_GPU = $OllamaGpu

    Write-Host "Starting the AppHost ($Profile, $Configuration)..."
    Invoke-Aspire start --no-build
    foreach ($resource in "semantic-service", "jellyfin") {
        Invoke-Aspire wait $resource --status healthy --timeout $TimeoutSeconds
    }
    # jellyfin-setup creates the libraries, scans them, and runs the first sync, then exits.
    Invoke-Aspire wait jellyfin-setup --status down --timeout $TimeoutSeconds
    if (-not (Test-Path $credentialsPath)) {
        foreach ($resource in "jellyfin-setup", "semantic-service") {
            dotnet tool run aspire -- logs $resource --apphost $appHost --non-interactive --nologo 2>&1 | Select-Object -Last 60
        }
        throw "jellyfin-setup did not finish; its logs are above."
    }
    Write-Host ("Setup finished after {0:n0} s." -f $watch.Elapsed.TotalSeconds)
    # How long each book took, and which ones the vision model had to read.
    dotnet tool run aspire -- logs semantic-service --apphost $appHost --non-interactive --nologo 2>&1 |
        Select-String "Ingested book|Reading .* with the vision model|Vision model read" | ForEach-Object { $_.Line }

    $env:JELLYFIN_E2E_CREDENTIALS_PATH = $credentialsPath
    $env:JELLYFIN_E2E_PROFILE = $Profile
    Invoke-Checked "End-to-end tests" {
        dotnet test $testProject -c $Configuration --no-build --nologo --logger "console;verbosity=normal"
    }
    Write-Host ("Jellyfin '$Profile' end-to-end tests passed in {0:n0} s." -f $watch.Elapsed.TotalSeconds)
} finally {
    foreach ($name in "SEMANTIC_DISCOVERY_PROFILE", "SEMANTIC_DISCOVERY_DATA", "SEMANTIC_DISCOVERY_OLLAMA_GPU",
        "JELLYFIN_E2E_CREDENTIALS_PATH", "JELLYFIN_E2E_PROFILE") {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }
    Stop-AppHost
    # On Linux the Jellyfin container writes as root, so the runner may not be able to delete everything.
    Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
    Pop-Location
}
