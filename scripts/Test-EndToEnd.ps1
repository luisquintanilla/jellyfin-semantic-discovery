#requires -version 7.0
[CmdletBinding()]
param(
    [int] $TimeoutSeconds = 420,
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$dataRoot = Join-Path $repoRoot ".data\e2e"
$credentialsPath = Join-Path $dataRoot "jellyfin-login.json"
$artifactsRoot = Join-Path $repoRoot "artifacts\e2e"
$appHost = Join-Path `
    $repoRoot `
    "src\Jellyfin.SemanticDiscovery.AppHost\Jellyfin.SemanticDiscovery.AppHost.csproj"
$testProject = Join-Path `
    $repoRoot `
    "tests\Jellyfin.SemanticDiscovery.EndToEndTests\Jellyfin.SemanticDiscovery.EndToEndTests.csproj"

function Set-ContainerUserForCurrentHost {
    if ($IsWindows) {
        return
    }

    $uid = (& id -u).Trim()
    if ($LASTEXITCODE -ne 0 -or $uid -notmatch '^\d+$') {
        throw "Could not determine the host user ID for E2E containers."
    }
    $gid = (& id -g).Trim()
    if ($LASTEXITCODE -ne 0 -or $gid -notmatch '^\d+$') {
        throw "Could not determine the host group ID for E2E containers."
    }

    $env:SEMANTIC_DISCOVERY_CONTAINER_USER = "${uid}:${gid}"
}

function Invoke-Aspire {
    param([Parameter(ValueFromRemainingArguments)] [string[]] $Arguments)

    $output = & dotnet tool run aspire -- @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "Aspire command failed: $($Arguments -join ' ')"
    }
}

function Stop-Demo {
    $scopedContainers = @(Get-ScopedContainerIds)
    & dotnet tool run aspire -- stop `
        --apphost $appHost `
        --force `
        --non-interactive `
        --nologo 2>$null | Out-Null
    $stopExitCode = $LASTEXITCODE

    foreach ($containerId in $scopedContainers) {
        docker container inspect $containerId 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            docker container rm --force $containerId 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Could not remove scoped Aspire container '$containerId'."
            }
        }
    }
    if (@(Get-ScopedContainerIds).Count -ne 0) {
        throw "Scoped Aspire containers remain after stop."
    }

    $description = & dotnet tool run aspire -- describe `
        --apphost $appHost `
        --format Json `
        --non-interactive `
        --nologo 2>&1 | Out-String
    if ($description -match "No AppHost is currently running") {
        return
    }
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($description)) {
        throw "The scoped Aspire AppHost still appears discoverable after stop."
    }
    if ($stopExitCode -ne 0) {
        throw "Aspire stop failed and the scoped AppHost state could not be confirmed."
    }
}

function Get-ScopedContainerIds {
    $allIds = @(docker container ls --all --quiet)
    if ($allIds.Count -eq 0) {
        return @()
    }

    $scopedIds = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    $networks = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    $root = [System.IO.Path]::GetFullPath($dataRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $inspected = @{}
    foreach ($containerId in $allIds) {
        $container = docker container inspect $containerId |
            ConvertFrom-Json |
            Select-Object -First 1
        $inspected[$containerId] = $container
        $usesScopedData = @($container.Mounts | Where-Object {
            $_.Type -eq "bind" -and
            [System.IO.Path]::GetFullPath([string]$_.Source).StartsWith(
                $root,
                [System.StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
        if ($usesScopedData) {
            [void]$scopedIds.Add($containerId)
            foreach ($network in $container.NetworkSettings.Networks.PSObject.Properties.Name) {
                [void]$networks.Add($network)
            }
        }
    }

    if ($networks.Count -gt 0) {
        foreach ($entry in $inspected.GetEnumerator()) {
            $containerNetworks =
                @($entry.Value.NetworkSettings.Networks.PSObject.Properties.Name)
            if (@($containerNetworks | Where-Object {
                    $networks.Contains($_)
                }).Count -gt 0) {
                [void]$scopedIds.Add([string]$entry.Key)
            }
        }
    }

    return @($scopedIds)
}

function Reset-E2EData {
    $resolved = [System.IO.Path]::GetFullPath($dataRoot)
    $expected = [System.IO.Path]::GetFullPath(
        (Join-Path $repoRoot ".data\e2e"))
    if (-not $resolved.Equals(
            $expected,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset an unexpected E2E data directory."
    }

    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

function Reset-E2EArtifacts {
    $resolved = [System.IO.Path]::GetFullPath($artifactsRoot)
    $expected = [System.IO.Path]::GetFullPath(
        (Join-Path $repoRoot "artifacts\e2e"))
    if (-not $resolved.Equals(
            $expected,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset an unexpected E2E artifact directory."
    }

    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Force $resolved | Out-Null
}

function Get-ResourceDescription {
    $json = & dotnet tool run aspire -- describe `
        --apphost $appHost `
        --format Json `
        --include-hidden `
        --non-interactive `
        --nologo | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "Could not describe Aspire resources."
    }

    return $json | ConvertFrom-Json
}

function Get-Resources {
    param($Description)

    if ($Description.resources) {
        return @($Description.resources)
    }
    if ($Description.Resources) {
        return @($Description.Resources)
    }
    return @($Description)
}

function ConvertTo-SanitizedResourceEvidence {
    param($Description)

    $controlledNames = @(
            "model-setup",
            "fixture-setup",
            "plugin-setup",
            "jellyfin-bootstrap",
            "jellyfin",
            "markitdown")
    $resources = Get-Resources $Description
    return [ordered]@{
            resources = @($resources |
                Where-Object {
                    $controlledNames -contains [string](
                        $_.displayName ?? $_.DisplayName)
                } |
                ForEach-Object {
                    [ordered]@{
                        displayName = [string](
                            $_.displayName ?? $_.DisplayName)
                        resourceType = [string](
                            $_.resourceType ?? $_.ResourceType)
                        state = [string]($_.state ?? $_.State)
                        healthStatus = [string](
                            $_.healthStatus ?? $_.HealthStatus)
                        exitCode = $_.exitCode ?? $_.ExitCode
                        urls = @(@($_.urls ?? $_.Urls) | ForEach-Object {
                            [ordered]@{
                                name = [string]($_.name ?? $_.Name)
                                url = [string]($_.url ?? $_.Url)
                            }
                        })
                        volumes = @(@($_.volumes ?? $_.Volumes) | ForEach-Object {
                            [ordered]@{
                                target = [string]($_.target ?? $_.Target)
                                mountType = [string](
                                    $_.mountType ?? $_.MountType)
                                isReadOnly = $_.isReadOnly ?? $_.IsReadOnly
                            }
                        })
                    }
                })
    }
}

function Assert-ResourceStates {
    param(
        [string] $Phase,
        $Description,
        $Login
    )

    $resources = Get-Resources $Description
    foreach ($name in @(
            "model-setup",
            "fixture-setup",
            "plugin-setup",
            "jellyfin-bootstrap")) {
        $resource = @($resources | Where-Object {
            ($_.displayName ?? $_.DisplayName) -eq $name
        })
        if ($resource.Count -ne 1) {
            throw "Resource '$name' was missing from Aspire describe."
        }
        $state = [string]($resource[0].state ?? $resource[0].State)
        $exitCode = $resource[0].exitCode ?? $resource[0].ExitCode
        if ($state -notin @("Finished", "Exited") -or
            $null -eq $exitCode -or
            [int]$exitCode -ne 0) {
            throw "Resource '$name' did not complete successfully (state=$state, exitCode=$exitCode)."
        }
    }

    foreach ($name in @("jellyfin", "markitdown")) {
        $resource = @($resources | Where-Object {
            ($_.displayName ?? $_.DisplayName) -eq $name
        })
        if ($resource.Count -ne 1) {
            throw "Resource '$name' was missing from Aspire describe."
        }
        $state = [string]($resource[0].state ?? $resource[0].State)
        $health = [string](
            $resource[0].healthStatus ??
            $resource[0].HealthStatus ??
            $resource[0].health ??
            $resource[0].Health)
        if ($state -in @("Failed", "Exited", "Finished", "Stopped") -or
            $health -ne "Healthy") {
            throw "Resource '$name' is not running and healthy (state=$state, health=$health)."
        }
    }

    $descriptionJson =
        ConvertTo-SanitizedResourceEvidence $Description |
        ConvertTo-Json -Depth 10
    $descriptionJson |
        Set-Content `
            -LiteralPath (Join-Path $artifactsRoot "resources-$Phase.json") `
            -Encoding utf8NoBOM
    if ($null -eq $Login) {
        return
    }

    $rawDescriptionJson = $Description | ConvertTo-Json -Depth 100
    if ($rawDescriptionJson.Contains(
            [string]$Login.Password,
            [System.StringComparison]::Ordinal)) {
        throw "Raw Aspire describe exposed the generated administrator secret."
    }

    $jellyfin = @($resources | Where-Object {
        ($_.displayName ?? $_.DisplayName) -eq "jellyfin"
    })[0]
    $endpoints = @(
        $jellyfin.urls ??
        $jellyfin.Urls ??
        $jellyfin.endpoints ??
        $jellyfin.Endpoints)
    $httpEndpoint = @($endpoints | Where-Object {
        ($_.name ?? $_.Name) -eq "http"
    })
    if ($httpEndpoint.Count -ne 1) {
        throw "Jellyfin's named HTTP endpoint was missing from Aspire describe."
    }
    $currentUrl = [string](
        $httpEndpoint[0].url ??
        $httpEndpoint[0].Url ??
        $httpEndpoint[0].value ??
        $httpEndpoint[0].Value)
    if ([string]::IsNullOrWhiteSpace($currentUrl) -or
        ([Uri]$currentUrl).GetLeftPart([UriPartial]::Authority) -ne
        ([Uri][string]$Login.Url).GetLeftPart([UriPartial]::Authority)) {
        throw "The recreated login URL does not match Jellyfin's current named Aspire endpoint."
    }

}

function Start-And-Wait {
    param([string] $Phase)

    if (Test-Path -LiteralPath $credentialsPath) {
        Remove-Item -LiteralPath $credentialsPath -Force
    }
    Invoke-Aspire start `
        --apphost $appHost `
        --format Json `
        --non-interactive `
        --nologo
    foreach ($name in @("jellyfin", "markitdown")) {
        Invoke-Aspire wait $name `
            --status healthy `
            --timeout $TimeoutSeconds `
            --apphost $appHost `
            --non-interactive `
            --nologo
    }
    foreach ($name in @(
            "model-setup",
            "fixture-setup",
            "plugin-setup",
            "jellyfin-bootstrap")) {
        Invoke-Aspire wait $name `
            --status down `
            --timeout $TimeoutSeconds `
            --apphost $appHost `
            --non-interactive `
            --nologo
    }

    $description = Get-ResourceDescription
    Assert-ResourceStates $Phase $description $null
    if (-not (Test-Path -LiteralPath $credentialsPath)) {
        throw "Successful bootstrap did not write the scoped login file."
    }
    $login = Get-Content -Raw -LiteralPath $credentialsPath |
        ConvertFrom-Json
    Assert-ResourceStates $Phase $description $login

    return $login
}

function Invoke-E2ETests {
    param(
        [string] $Phase,
        [string] $EvidencePath
    )

    $env:JELLYFIN_E2E_CREDENTIALS_PATH = $credentialsPath
    $env:JELLYFIN_E2E_EVIDENCE_PATH = $EvidencePath
    dotnet test $testProject `
        --no-build `
        --no-restore `
        --configuration $Configuration `
        --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Jellyfin end-to-end tests failed in '$Phase' phase."
    }
    if (-not (Test-Path -LiteralPath $EvidencePath)) {
        throw "The '$Phase' E2E run did not write fresh runtime evidence."
    }
}

Push-Location $repoRoot
$primaryFailure = $null
try {
    Set-ContainerUserForCurrentHost
    dotnet tool restore --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) {
        throw "Pinned Aspire CLI restore failed."
    }
    Stop-Demo
    Reset-E2EData
    Reset-E2EArtifacts
    $initialEvidencePath = Join-Path $artifactsRoot "runtime-initial.json"
    $restartEvidencePath = Join-Path $artifactsRoot "runtime-restart.json"
    foreach ($path in @($initialEvidencePath, $restartEvidencePath)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
    $env:SEMANTIC_DISCOVERY_DATA = $dataRoot
    $env:SEMANTIC_DISCOVERY_CONFIGURATION = $Configuration

    dotnet restore Jellyfin.SemanticDiscovery.slnx `
        --configfile NuGet.Config `
        --locked-mode `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Locked solution restore failed; run from a checkout with committed package locks."
    }
    dotnet build Jellyfin.SemanticDiscovery.slnx `
        --configuration $Configuration `
        --no-restore `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "The explicit E2E solution build failed."
    }

    $initialLogin = Start-And-Wait "initial"
    Invoke-E2ETests "initial" $initialEvidencePath
    Stop-Demo

    $restartLogin = Start-And-Wait "restart"
    if ($initialLogin.Password -ne $restartLogin.Password) {
        throw "The persisted generated Jellyfin administrator secret changed on restart."
    }
    Invoke-E2ETests "restart" $restartEvidencePath

    $initialEvidence = Get-Content -Raw -LiteralPath $initialEvidencePath |
        ConvertFrom-Json
    $restartEvidence = Get-Content -Raw -LiteralPath $restartEvidencePath |
        ConvertFrom-Json
    if ([long]$restartEvidence.indexGeneration -le
            [long]$initialEvidence.indexGeneration -or
        [long]$restartEvidence.metadataRecordCount -ne
            [long]$initialEvidence.metadataRecordCount -or
        [long]$restartEvidence.contentChunkCount -ne
            [long]$initialEvidence.contentChunkCount -or
        [long]$restartEvidence.lastEmbeddedMetadataCount -ne 0 -or
        [long]$restartEvidence.lastEmbeddedContentChunkCount -ne 0 -or
        [string]$restartEvidence.indexIdentity -ne
            [string]$initialEvidence.indexIdentity -or
        [long]$restartEvidence.recommendationQueryCount -lt
            [long]$initialEvidence.recommendationQueryCount -or
        [long]$restartEvidence.recommendationResultCount -lt
            [long]$initialEvidence.recommendationResultCount -or
        [long]$restartEvidence.ingestEmbeddingsTotal -ne
            [long]$initialEvidence.ingestEmbeddingsTotal -or
        [long]$restartEvidence.queryEmbeddingsTotal -lt
            [long]$initialEvidence.queryEmbeddingsTotal) {
        throw "Persistent restart evidence did not preserve identity/counts and advance generation monotonically."
    }

    $restartLogs = & dotnet tool run aspire -- logs jellyfin `
        --apphost $appHost `
        --search "Semantic index ready" `
        --format Json `
        --non-interactive `
        --nologo | Out-String
    if ($LASTEXITCODE -ne 0 -or
        $restartLogs -notmatch "embedded 0 items and 0 chunks") {
        throw "Restart did not prove checksum-based vector reuse."
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $artifactsRoot "jellyfin-restart-index-log.json"),
        $restartLogs,
        [System.Text.UTF8Encoding]::new($false))

    Write-Host "Clean and persistent-restart Jellyfin E2E gates passed."
} catch {
    $primaryFailure = $_
    try {
        $diagnosticDescription =
            ConvertTo-SanitizedResourceEvidence (Get-ResourceDescription) |
            ConvertTo-Json -Depth 10
        if ($initialLogin -and
            $diagnosticDescription.Contains(
                [string]$initialLogin.Password,
                [System.StringComparison]::Ordinal)) {
            $diagnosticDescription = $diagnosticDescription.Replace(
                [string]$initialLogin.Password,
                "***",
                [System.StringComparison]::Ordinal)
        }
        [System.IO.File]::WriteAllText(
            (Join-Path $artifactsRoot "failure-resources.json"),
            $diagnosticDescription,
            [System.Text.UTF8Encoding]::new($false))
    } catch {
        Write-Warning "Could not capture sanitized E2E failure diagnostics."
    }
    throw
} finally {
    Remove-Item Env:JELLYFIN_E2E_CREDENTIALS_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:JELLYFIN_E2E_EVIDENCE_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:SEMANTIC_DISCOVERY_DATA -ErrorAction SilentlyContinue
    Remove-Item Env:SEMANTIC_DISCOVERY_CONFIGURATION -ErrorAction SilentlyContinue
    Remove-Item Env:SEMANTIC_DISCOVERY_CONTAINER_USER -ErrorAction SilentlyContinue
    $stopConfirmed = $false
    try {
        Stop-Demo
        $stopConfirmed = $true
    } catch {
        if ($null -eq $primaryFailure) {
            throw
        }
        Write-Warning "Scoped Aspire cleanup failed; retaining bind data."
    }
    if ($stopConfirmed) {
        try {
            Reset-E2EData
        } catch {
            if ($null -eq $primaryFailure) {
                throw
            }
            Write-Warning "Could not remove scoped E2E data."
        }
    }
    Pop-Location
}
