[CmdletBinding()]
param(
    [string] $Configuration = "Debug",
    [string] $ModelDirectory = "",
    [int] $Port = 18096
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$dataRoot = Join-Path $repoRoot ".data"
$modelDirectory = if ($ModelDirectory) {
    [System.IO.Path]::GetFullPath($ModelDirectory)
} else {
    Join-Path $dataRoot "models\all-MiniLM-L6-v2"
}
$pluginProject = Join-Path $repoRoot "src\Jellyfin.SemanticDiscovery.Plugin\Jellyfin.SemanticDiscovery.Plugin.csproj"
$setupProject = Join-Path $repoRoot "src\Jellyfin.SemanticDiscovery.Setup\Jellyfin.SemanticDiscovery.Setup.csproj"
$pluginOutput = Join-Path $repoRoot "src\Jellyfin.SemanticDiscovery.Plugin\bin\$Configuration\net10.0"
$positiveRoot = Join-Path $dataRoot "plugin-smoke"
$negativeRoot = Join-Path $dataRoot "plugin-smoke-missing-native"
$image = "jellyfin/jellyfin@sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5"

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

function Reset-SmokeRoot {
    param([string] $Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $safePrefix = [System.IO.Path]::GetFullPath($dataRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($safePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset path outside the repository data directory: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }

    New-Item -ItemType Directory -Force $fullPath | Out-Null
}

function Install-Plugin {
    param([string] $Root)

    $pluginDirectory = Join-Path $Root "config\plugins\Semantic Discovery_0.1.0.0"
    New-Item -ItemType Directory -Force $pluginDirectory | Out-Null
    Get-ChildItem -LiteralPath $pluginOutput | Copy-Item `
        -Destination $pluginDirectory `
        -Recurse `
        -Force
    return $pluginDirectory
}

function Start-Jellyfin {
    param(
        [string] $Root,
        [int] $HostPort
    )

    $configDirectory = Join-Path $Root "config"
    $cacheDirectory = Join-Path $Root "cache"
    New-Item -ItemType Directory -Force $configDirectory, $cacheDirectory | Out-Null

    $containerId = docker run `
        --detach `
        --publish "127.0.0.1:${HostPort}:8096" `
        --env "SEMANTIC_DISCOVERY_STARTUP_SMOKE=true" `
        --env "SEMANTIC_DISCOVERY_MODEL_DIRECTORY=/models/all-MiniLM-L6-v2" `
        --mount "type=bind,source=$configDirectory,target=/config" `
        --mount "type=bind,source=$cacheDirectory,target=/cache" `
        --mount "type=bind,source=$(Split-Path -Parent $modelDirectory),target=/models,readonly" `
        $image
    if ($LASTEXITCODE -ne 0 -or -not $containerId) {
        throw "Unable to start the pinned Jellyfin container."
    }

    return $containerId.Trim()
}

function Remove-Container {
    param([string] $ContainerId)

    if (-not $ContainerId) {
        return
    }

    docker rm --force $ContainerId | Out-Null
}

function Wait-Jellyfin {
    param(
        [string] $ContainerId,
        [int] $HostPort
    )

    $lastError = "no response"
    for ($attempt = 0; $attempt -lt 90; $attempt++) {
        if ((docker inspect --format "{{.State.Running}}" $ContainerId) -ne "true") {
            throw "Jellyfin exited before becoming ready.`n$(docker logs $ContainerId 2>&1)"
        }

        try {
            $response = Invoke-WebRequest `
                -UseBasicParsing `
                -Uri "http://127.0.0.1:$HostPort/System/Ping" `
                -TimeoutSec 2
            if ($response.StatusCode -eq 200) {
                return
            }
        } catch {
            $lastError = $_.Exception.Message
        }

        Start-Sleep -Seconds 1
    }

    throw "Jellyfin did not become ready: $lastError`n$(docker logs $ContainerId 2>&1)"
}

function Assert-PluginLoaded {
    param([string] $ContainerId)

    $logs = docker logs $ContainerId 2>&1 | Out-String
    if ($logs -notmatch "Loaded plugin: Semantic Discovery 0\.1\.0\.0") {
        throw "Semantic Discovery did not load.`n$logs"
    }

    if ($logs -notmatch "SEMANTIC_DISCOVERY_ONNX_SMOKE_OK") {
        throw "Linux ONNX inference did not run inside Jellyfin.`n$logs"
    }

    if ($logs -match "Error registering plugin services from\s+Jellyfin\.SemanticDiscovery" -or
        $logs -match "Error creating\s+Jellyfin\.SemanticDiscovery") {
        throw "Semantic Discovery reported a dependency or activation error.`n$logs"
    }
}

function Assert-MissingNativeFails {
    param([string] $ContainerId)

    $logs = ""
    for ($attempt = 0; $attempt -lt 45; $attempt++) {
        $logs = docker logs $ContainerId 2>&1 | Out-String
        if ($logs -match "SEMANTIC_DISCOVERY_ONNX_SMOKE_FAILED") {
            break
        }

        Start-Sleep -Seconds 1
    }

    if ($logs -notmatch "SEMANTIC_DISCOVERY_ONNX_SMOKE_FAILED") {
        throw "Missing native library did not fail the in-container ONNX smoke.`n$logs"
    }

    if ($logs -match "SEMANTIC_DISCOVERY_ONNX_SMOKE_OK") {
        throw "Missing-native negative control unexpectedly reported inference success.`n$logs"
    }
}

Push-Location $repoRoot
try {
    $lockStatusBefore = @(
        git status --porcelain=v1 --untracked-files=all -- `
            ":(glob)**/packages.lock.json")
    Invoke-Checked dotnet restore $pluginProject --configfile NuGet.Config --locked-mode
    Invoke-Checked dotnet restore $setupProject --configfile NuGet.Config --locked-mode
    Invoke-Checked dotnet build $pluginProject --configuration $Configuration --no-restore
    Invoke-Checked dotnet run --project $setupProject --configuration $Configuration --no-restore -- smoke --model-dir $modelDirectory
    Invoke-Checked docker pull $image

    Reset-SmokeRoot $positiveRoot
    Install-Plugin $positiveRoot | Out-Null
    for ($boot = 1; $boot -le 2; $boot++) {
        $containerId = $null
        try {
            $containerId = Start-Jellyfin $positiveRoot $Port
            Wait-Jellyfin $containerId $Port
            Assert-PluginLoaded $containerId
            Write-Host "Jellyfin plugin boot $boot passed ($containerId)."
        } finally {
            Remove-Container $containerId
        }
    }

    Reset-SmokeRoot $negativeRoot
    $negativePluginDirectory = Install-Plugin $negativeRoot
    Remove-Item -LiteralPath (Join-Path $negativePluginDirectory "libonnxruntime.so") -Force
    $negativeContainerId = $null
    try {
        $negativeContainerId = Start-Jellyfin $negativeRoot $Port
        Assert-MissingNativeFails $negativeContainerId
        Write-Host "Missing-native negative control passed ($negativeContainerId)."
    } finally {
        Remove-Container $negativeContainerId
    }

    $lockStatusAfter = @(
        git status --porcelain=v1 --untracked-files=all -- `
            ":(glob)**/packages.lock.json")
    if (($lockStatusBefore -join "`n") -ne ($lockStatusAfter -join "`n")) {
        throw "Plugin smoke changed the locked NuGet dependency graph."
    }

    Write-Host "Plugin graph, Linux pinned-model parity, and restart smoke passed."
} finally {
    Pop-Location
}
